using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Gdb;
using Refii.Host;

namespace Refii.Entities;

/// <summary>
/// Cross-title classification, retention lifetime, and memory-cap eviction policy for fallen
/// creature and NPC corpses across Fable 2 and Fable 3.
/// </summary>
/// <remarks>
/// Pure logic with zero scene-tree dependencies so every filter, CVar tier, and eviction rule is
/// verified directly in headless Tier-1 unit tests.
/// </remarks>
public static partial class Engine_CorpsePolicy
{
    /// <summary>Hot-reload CVar controlling how many seconds physical corpses persist before fade-out.</summary>
    public const string LifetimeKey = "corpse_lifetime_seconds";

    /// <summary>Hot-reload CVar controlling max active retained corpses (0 = Auto, &lt;0 = Unlimited).</summary>
    public const string MaxCountKey = "corpse_max_count";

    /// <summary>Default desktop retention duration in seconds (Balanced preset).</summary>
    public const float DefaultLifetimeSeconds = 45f;

    /// <summary>Duration of the smooth fade-out once a corpse expires or is evicted.</summary>
    public const float FadeDurationSeconds = 2.0f;

    /// <summary>Opacity threshold below which the retained corpse drops its cast shadow proxy.</summary>
    public const float ShadowDropAlpha = 0.35f;

    /// <summary>Horizontal distance in metres beyond which distant corpses are evicted first.</summary>
    public const float DistantEvictionRadiusMeters = 25.0f;

    /// <summary>Authoritative corpse persistence mode resolved from GDB metadata and runtime gates.</summary>
    public enum CorpseDeathMode : byte
    {
        Excluded = 0,
        PhysicalRagdoll = 1,
        CrumbleSplode = 2,
    }

    /// <summary>Minimum rest height in metres for a bone to act as a reliable collapse probe.</summary>
    public const float MinCollapseProbeRestHeight = 0.35f;

    /// <summary>Maximum posed-to-rest height ratio treated as collapsed on the ground.</summary>
    public const float CollapseHeightRatio = 0.62f;

    // Cross-title Lionhead GDB schema hashes (verified identical across Fable 2 and Fable 3).
    internal const uint CreatureComponentHash = 0xC3B90D4Fu;
    internal const uint DeathTypeHash = 0xCD9174D9u;
    internal const uint DeathMeshHash = 0x0D364222u;
    internal const uint CreatureSpecialisationHash = 0xD3755112u;
    internal const uint CreatureBaseTypeHash = 0x72DB57C9u;
    internal const uint PhysicsNavigatorComponentHash = 0xC5A11B7Au;
    internal const uint PhysicsControlledComponentHash = 0xD51F7655u;
    internal const uint PhysicsStaticComponentHash = 0xF73572C4u;
    internal const uint HasRagdollHash = 0xD09A6BC0u;
    internal const uint VillagerComponentHash = 0x5BA014D4u;
    internal const uint JobTagHash = 0x7FA702D2u;
    internal const uint GhostOccupationTagHash = 0xFC18ABA6u; // TEXT_CHARACTER_OCCUPATION_GHOST

    private const uint ShadowSpecialisationValue = 4u; // CREATURE_SPECIALISE_SHADOW
    private const uint DogBaseTypeValue = 8u;          // CREATURE_DOG (do NOT exclude DogComponent: F3 wolves inherit it)

    private const uint DeathTypeDoNothing = 0u;
    private const uint DeathTypeDestroy = 1u;
    private const uint DeathTypeDropDead = 2u;
    private const uint DeathTypePlayAction = 3u;
    private const uint DeathTypePlayActionAndDestroy = 4u;
    private const uint DeathTypeHollowManSplode = 5u;
    private const uint DeathTypeFadeOut = 6u;
    private const uint DeathTypeShadowCreature = 7u;
    private const uint DeathTypeShard = 8u;

    // Component slots that mark non-corpse supernatural/statue archetypes even when DeathType == 2.
    private static readonly uint[] ExcludedComponentFieldHashes =
    {
        0x2B1E4BC7u, // ShadowCreatureComponent (Shadows, Banshee Children, Cornelius Grim Shadow, F3 Chessboard)
        0xFB6177C1u, // GhostVillagerComponent (F3 ghost villagers incl. Max & Sam)
        0xEA238706u, // HollowmanWispComponent (F3 wisps)
        0xB77F7EB2u, // HeroStatueComponent (F2 & F3 statues)
    };

    private static readonly string[] ExcludedTokens =
    {
        "beetle",
        "banshee",
        "ghost",
        "shadow",
        "wisp",
        "spirit",
        "spectre",
        "phantom",
        "summon",
        "shard",
        "crawler",
        "darkness",
        "poltergeist",
        "slime",
    };

    private static readonly object CacheGate = new();
    private static int _cachedGeneration = -1;
    private static float _cachedLifetime = DefaultLifetimeSeconds;
    private static int _cachedConfiguredMax;

    private static GdbView? _activeLevelGdb;
    private static GdbMulti? _cachedBaseGdb;
    private static GdbView? _cachedLevelGdb;
    private static GdbMulti? _cachedEffectiveGdb;
    private static readonly Dictionary<uint, (CorpseDeathMode Mode, bool FromGdb)> RecordModeCache = new();

    /// <summary>Inputs evaluated to decide whether a retired guest entity should be kept as a corpse.</summary>
    /// <param name="GuestLeftCorpse">
    /// The guest leaves this creature lying: see <see cref="LeavesCorpse"/>.
    /// </param>
    public readonly record struct CorpseCandidate(
        bool IsDeadCreature,
        bool GuestLeftCorpse,
        bool HasSkeleton,
        bool SawVisibleWhileDead,
        bool SawEtherealWhileAlive,
        bool IsPrincipal,
        bool IsCompanion,
        uint RecordHash,
        string? ModelPath,
        CorpseDeathMode DeathMode,
        bool ClassifiedFromGdb,
        bool SawPosedWhileDead,
        bool SawRagdollWhileDead,
        bool SawDisarticulatedWhileDead,
        bool SawCollapsedPoseWhileDead);

    /// <summary>Lightweight per-corpse summary passed to <see cref="SelectEvictionIndex"/>.</summary>
    public readonly record struct CorpseSlotSummary(Vector3 Position, float Age, bool FadingOut);

    /// <summary>Configured corpse retention duration in seconds (0 disables retention; no upper cap).</summary>
    public static float LifetimeSeconds
    {
        get
        {
            RefreshFromConfig();
            return _cachedLifetime;
        }
    }

    /// <summary>Configured raw max corpse cap (0 = Auto, &lt;0 = Unlimited, &gt;0 = Explicit count).</summary>
    public static int ConfiguredMaxCount
    {
        get
        {
            RefreshFromConfig();
            return _cachedConfiguredMax;
        }
    }

    /// <summary>True when host corpse persistence is currently active.</summary>
    public static bool Enabled => LifetimeSeconds > 0f;

    static partial void ResolveCompactCap(ref bool compact);

    private static bool UseCompactCap()
    {
        bool compact = false;
        ResolveCompactCap(ref compact);
        return compact;
    }

    /// <summary>Resolved effective region corpse limit for the active config and platform tier.</summary>
    public static int EffectiveMaxCount =>
        ResolveMaxCorpses(LifetimeSeconds, ConfiguredMaxCount, UseCompactCap());

    private static void RefreshFromConfig()
    {
        int generation = Engine_Switches.Generation;
        lock (CacheGate)
        {
            if (_cachedGeneration == generation)
            {
                return;
            }
        }

        IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);
        float lifetime = values.TryGetValue(LifetimeKey, out string? rawLife)
            ? ParseLifetimeSeconds(rawLife)
            : DefaultLifetimeSeconds;
        int maxCount = values.TryGetValue(MaxCountKey, out string? rawMax)
            ? ParseConfiguredMaxCount(rawMax)
            : 0;

        lock (CacheGate)
        {
            _cachedGeneration = generation;
            _cachedLifetime = lifetime;
            _cachedConfiguredMax = maxCount;
        }
    }

    /// <summary>
    /// Parses a corpse lifetime CVar string with no upper cap so arbitrary user durations (including
    /// multi-minute or multi-hour settings) are honored verbatim.
    /// </summary>
    public static float ParseLifetimeSeconds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultLifetimeSeconds;
        }

        string trimmed = raw.Trim().Trim('"');
        if (float.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value) &&
            float.IsFinite(value))
        {
            return value <= 0f ? 0f : value;
        }

        return DefaultLifetimeSeconds;
    }

    /// <summary>Parses a <c>corpse_max_count</c> value (0 = Auto, negative = Unlimited).</summary>
    public static int ParseConfiguredMaxCount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        string trimmed = raw.Trim().Trim('"');
        return int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;
    }

    /// <summary>Computes the region corpse cap for the configured lifetime and memory tier.</summary>
    public static int ResolveMaxCorpses(float lifetimeSeconds, int configuredMax, bool compactTier)
    {
        if (configuredMax < 0)
        {
            return int.MaxValue;
        }

        if (configuredMax > 0)
        {
            return configuredMax;
        }

        if (lifetimeSeconds <= 0f)
        {
            return 0;
        }

        if (compactTier)
        {
            return lifetimeSeconds <= 15f ? 8 : (lifetimeSeconds <= 30f ? 12 : 20);
        }

        return lifetimeSeconds <= 15f
            ? 16
            : (lifetimeSeconds <= 60f ? 32 : (lifetimeSeconds <= 180f ? 64 : 96));
    }

    /// <summary>
    /// Returns true when a living creature exhibits translucent or overbright supernatural rendering
    /// (ghosts, spirits, wisps, Banshees, shadow creatures), marking it ineligible for a solid corpse.
    /// </summary>
    public static bool IsEtherealAppearance(in HostBridge.EntityAppearance look)
    {
        float maxTint = MathF.Max(look.TintLanes.X, MathF.Max(look.TintLanes.Y, look.TintLanes.Z));
        return look.Alpha <= 0.92f || maxTint > 1.5f;
    }

    /// <summary>
    /// Returns true when a living creature exhibits a normal opaque appearance, clearing any transient
    /// spawn fade-in flag so solid enemies are not permanently disqualified.
    /// </summary>
    public static bool IsSolidLivingAppearance(in HostBridge.EntityAppearance look)
    {
        float maxTint = MathF.Max(look.TintLanes.X, MathF.Max(look.TintLanes.Y, look.TintLanes.Z));
        return look.Drawable && look.Alpha >= 0.98f && maxTint <= 1.2f;
    }

    /// <summary>Returns true if a record name or model asset path matches an exploding bug or ethereal entity.</summary>
    public static bool HasExcludedName(string? nameOrPath)
    {
        if (string.IsNullOrEmpty(nameOrPath))
        {
            return false;
        }

        foreach (string token in ExcludedTokens)
        {
            if (nameOrPath.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Sets or clears the active region's level GDB so level-local creature records resolve cleanly.</summary>
    public static void SetActiveLevelGdb(GdbView? levelView)
    {
        lock (CacheGate)
        {
            if (ReferenceEquals(_activeLevelGdb, levelView))
            {
                return;
            }

            _activeLevelGdb = levelView;
            _cachedLevelGdb = null;
            _cachedEffectiveGdb = null;
            RecordModeCache.Clear();
        }
    }

    private static GdbMulti? BuildEffectiveGdb(GdbMulti? baseMulti, GdbView? levelView)
    {
        if (levelView is null)
        {
            return baseMulti;
        }

        if (baseMulti is null)
        {
            return GdbMulti.Build(new[] { levelView });
        }

        var views = new List<GdbView>(baseMulti.Views.Count + 1);
        views.AddRange(baseMulti.Views);
        views.Add(levelView);
        return GdbMulti.Build(views);
    }

    private static GdbMulti? ResolveEffectiveGdbAndTryCache(
        uint recordHash,
        out (CorpseDeathMode Mode, bool FromGdb) cached,
        out bool hit,
        out GdbMulti? baseSnap,
        out GdbView? levelSnap)
    {
        GdbMulti? baseMulti = Engine_GameGdb.Database;
        GdbView? targetLevel;
        lock (CacheGate)
        {
            targetLevel = _activeLevelGdb;
            if (ReferenceEquals(_cachedBaseGdb, baseMulti) && ReferenceEquals(_cachedLevelGdb, targetLevel))
            {
                baseSnap = _cachedBaseGdb;
                levelSnap = _cachedLevelGdb;
                if (RecordModeCache.TryGetValue(recordHash, out cached))
                {
                    hit = true;
                    return _cachedEffectiveGdb;
                }

                hit = false;
                return _cachedEffectiveGdb;
            }
        }

        GdbMulti? built = BuildEffectiveGdb(baseMulti, targetLevel);
        lock (CacheGate)
        {
            if (!ReferenceEquals(_cachedBaseGdb, baseMulti) || !ReferenceEquals(_cachedLevelGdb, targetLevel))
            {
                _cachedBaseGdb = baseMulti;
                _cachedLevelGdb = targetLevel;
                _cachedEffectiveGdb = built;
                RecordModeCache.Clear();
            }

            baseSnap = _cachedBaseGdb;
            levelSnap = _cachedLevelGdb;
            if (RecordModeCache.TryGetValue(recordHash, out cached))
            {
                hit = true;
                return _cachedEffectiveGdb;
            }

            hit = false;
            return _cachedEffectiveGdb;
        }
    }

    private static void StoreCachedRecordMode(
        GdbMulti? baseSnap,
        GdbView? levelSnap,
        uint recordHash,
        (CorpseDeathMode Mode, bool FromGdb) value)
    {
        lock (CacheGate)
        {
            if (ReferenceEquals(_cachedBaseGdb, baseSnap) && ReferenceEquals(_cachedLevelGdb, levelSnap))
            {
                if (RecordModeCache.Count > 4096)
                {
                    RecordModeCache.Clear();
                }

                RecordModeCache[recordHash] = value;
            }
        }
    }

    /// <summary>
    /// Classifies how a creature record behaves on death using layered global + active-level GDB
    /// metadata (<c>EDeathType</c>, <c>DeathMesh</c>, <c>HasRagdoll</c>, and component slots),
    /// falling back to model path tokens only when GDB lacks an authoritative creature classification.
    /// </summary>
    public static CorpseDeathMode ClassifyRecord(uint recordHash, string? modelPath) =>
        ClassifyRecord(recordHash, modelPath, out _);

    /// <summary>
    /// Classifies how a creature record behaves on death and reports whether the classification came
    /// authoritatively from GDB schema data (<paramref name="classifiedFromGdb"/>).
    /// </summary>
    public static CorpseDeathMode ClassifyRecord(uint recordHash, string? modelPath, out bool classifiedFromGdb)
    {
        if (recordHash == 0u || recordHash == GdbHash.Null)
        {
            classifiedFromGdb = false;
            return HasExcludedName(modelPath) ? CorpseDeathMode.Excluded : CorpseDeathMode.PhysicalRagdoll;
        }

        GdbMulti? effective = ResolveEffectiveGdbAndTryCache(
            recordHash,
            out (CorpseDeathMode Mode, bool FromGdb) cached,
            out bool hit,
            out GdbMulti? baseSnap,
            out GdbView? levelSnap);
        if (!hit)
        {
            CorpseDeathMode evaluated = EvaluateRecordMode(effective, recordHash, out bool fromGdb);
            cached = (evaluated, fromGdb);
            StoreCachedRecordMode(baseSnap, levelSnap, recordHash, cached);
        }

        classifiedFromGdb = cached.FromGdb;
        if (!classifiedFromGdb && HasExcludedName(modelPath))
        {
            return CorpseDeathMode.Excluded;
        }

        return cached.Mode;
    }

    /// <summary>
    /// Checks whether a GDB record hash (or fallback model path) resolves to <see cref="CorpseDeathMode.Excluded"/>.
    /// </summary>
    public static bool IsExcludedRecord(uint recordHash, string? modelPath) =>
        ClassifyRecord(recordHash, modelPath) == CorpseDeathMode.Excluded;

    /// <summary>Returns true if a GDB component field hash denotes an excluded non-corpse archetype.</summary>
    public static bool IsExcludedComponentFieldHash(uint fieldHash)
    {
        foreach (uint excluded in ExcludedComponentFieldHashes)
        {
            if (excluded == fieldHash)
            {
                return true;
            }
        }

        return false;
    }

    internal static CorpseDeathMode EvaluateRecordMode(GdbMulti? multi, uint recordHash, out bool classifiedFromGdb)
    {
        if (multi is null ||
            recordHash == 0u ||
            recordHash == GdbHash.Null ||
            !multi.TryLookup(recordHash, out GdbCursor start) ||
            !start.Valid)
        {
            classifiedFromGdb = false;
            return CorpseDeathMode.PhysicalRagdoll;
        }

        foreach (uint fieldHash in ExcludedComponentFieldHashes)
        {
            if (multi.TryFindInherited(start, fieldHash, (int)GdbFieldType.Reference, out _, out _, out _))
            {
                classifiedFromGdb = true;
                return CorpseDeathMode.Excluded;
            }
        }

        if (multi.TryFindInherited(start, VillagerComponentHash, (int)GdbFieldType.Reference, out _, out uint villagerCompHash, out _) &&
            multi.TryLookup(villagerCompHash, out GdbCursor villagerCursor) &&
            multi.TryFindInherited(villagerCursor, JobTagHash, -1, out _, out uint jobTagHash, out _) &&
            jobTagHash == GhostOccupationTagHash)
        {
            classifiedFromGdb = true;
            return CorpseDeathMode.Excluded;
        }

        bool hasCreature = false;
        if (multi.TryFindInherited(start, CreatureComponentHash, (int)GdbFieldType.Reference, out _, out uint creatureCompHash, out _) &&
            multi.TryLookup(creatureCompHash, out GdbCursor creatureCursor))
        {
            hasCreature = true;

            if (multi.TryFindInherited(creatureCursor, CreatureSpecialisationHash, -1, out _, out uint spec, out _) &&
                spec == ShadowSpecialisationValue)
            {
                classifiedFromGdb = true;
                return CorpseDeathMode.Excluded;
            }

            if (multi.TryFindInherited(creatureCursor, CreatureBaseTypeHash, -1, out _, out uint baseType, out _) &&
                baseType == DogBaseTypeValue)
            {
                classifiedFromGdb = true;
                return CorpseDeathMode.Excluded;
            }

            if (multi.TryFindInherited(creatureCursor, DeathTypeHash, -1, out _, out uint deathType, out _))
            {
                switch (deathType)
                {
                    case DeathTypeDropDead:
                    case DeathTypePlayAction:
                        break;

                    case DeathTypeHollowManSplode:
                        if (multi.TryFindInherited(creatureCursor, DeathMeshHash, -1, out _, out uint deathMeshRaw, out _) &&
                            deathMeshRaw != 0u &&
                            deathMeshRaw != GdbHash.Null)
                        {
                            classifiedFromGdb = true;
                            return CorpseDeathMode.CrumbleSplode;
                        }

                        classifiedFromGdb = true;
                        return CorpseDeathMode.Excluded;

                    case DeathTypeDoNothing:
                    case DeathTypeDestroy:
                    case DeathTypePlayActionAndDestroy:
                    case DeathTypeFadeOut:
                    case DeathTypeShadowCreature:
                    case DeathTypeShard:
                    default:
                        classifiedFromGdb = true;
                        return CorpseDeathMode.Excluded;
                }
            }
        }

        if (multi.TryFindInherited(start, PhysicsNavigatorComponentHash, (int)GdbFieldType.Reference, out _, out uint navPhysHash, out _) &&
            multi.TryLookup(navPhysHash, out GdbCursor physCursor))
        {
            if (multi.TryFindInherited(physCursor, HasRagdollHash, -1, out _, out uint hasRagdoll, out _) &&
                hasRagdoll == 0u)
            {
                classifiedFromGdb = true;
                return CorpseDeathMode.Excluded;
            }

            classifiedFromGdb = hasCreature;
            return CorpseDeathMode.PhysicalRagdoll;
        }

        if (multi.TryFindInherited(start, PhysicsControlledComponentHash, (int)GdbFieldType.Reference, out _, out _, out _) ||
            multi.TryFindInherited(start, PhysicsStaticComponentHash, (int)GdbFieldType.Reference, out _, out _, out _))
        {
            classifiedFromGdb = true;
            return CorpseDeathMode.Excluded;
        }

        if (!hasCreature && HasExcludedName(multi.NameOf(recordHash)))
        {
            classifiedFromGdb = false;
            return CorpseDeathMode.Excluded;
        }

        classifiedFromGdb = hasCreature;
        return CorpseDeathMode.PhysicalRagdoll;
    }

    /// <summary>
    /// Returns true when a bone's posed Y height has dropped far enough below its standing rest Y
    /// height to confirm the body collapsed rather than freezing upright.
    /// </summary>
    public static bool IsPoseCollapsed(float restHeightY, float posedHeightY) =>
        float.IsFinite(restHeightY) &&
        float.IsFinite(posedHeightY) &&
        restHeightY >= MinCollapseProbeRestHeight &&
        posedHeightY <= restHeightY * CollapseHeightRatio;

    /// <summary>
    /// Verifies that the guest drove a genuine dead pose (ragdoll, disarticulation, or collapsed
    /// death clip) rather than leaving an offscreen or uncrumbled entity frozen upright.
    /// </summary>
    public static bool HasValidDeathPose(
        CorpseDeathMode mode,
        bool sawPosedWhileDead,
        bool sawRagdollWhileDead,
        bool sawDisarticulatedWhileDead,
        bool sawCollapsedPoseWhileDead) => mode switch
    {
        CorpseDeathMode.Excluded => false,
        CorpseDeathMode.CrumbleSplode =>
            sawDisarticulatedWhileDead || (sawPosedWhileDead && sawRagdollWhileDead && sawCollapsedPoseWhileDead),
        CorpseDeathMode.PhysicalRagdoll =>
            sawPosedWhileDead && (sawRagdollWhileDead || sawCollapsedPoseWhileDead),
        _ => false,
    };

    /// <summary>
    /// Whether the guest leaves a dead creature lying, the one thing a host corpse may extend.
    /// </summary>
    /// <remarks>
    /// Fable 2 STATES it: <c>CECCreature_FinishDeath</c> (TU1 0x823A4290, disc 0x82432848) ran on
    /// the creature since its constructor, which the bridge reports as
    /// <see cref="HostBridge.EntityAppearance.GuestCorpseFlag"/>. The death switch reaches it for
    /// DeathType 2 directly (TU1 0x823a6cb8, disc 0x82435220) and for 3 at the end of the Die
    /// action (`CAction_Die_vf18` TU1 0x8276fb84, disc 0x82869d24, its +0x78 destroy flag clear);
    /// 1, 4 and 6 end in <c>lhEntity_Destroy</c>; 5 (hollow-man crumble), 7 (shadow) and 8
    /// (<c>Lua_Shard_Kill</c>) never reach FinishDeath. A title whose bridge does
    /// not state it (<paramref name="titleStates"/> false: Fable 3, whose FinishDeath is not yet
    /// read) keeps the record classification (<see cref="ClassifyRecord(uint, string?)"/>) and the
    /// name exclusions.
    /// </remarks>
    public static bool LeavesCorpse(bool titleStates, bool statedCorpse, bool unstatedExcluded) =>
        titleStates ? statedCorpse : !unstatedExcluded;

    /// <summary>
    /// <see cref="LeavesCorpse(bool, bool, bool)"/> with the record's death mode: a
    /// <see cref="CorpseDeathMode.CrumbleSplode"/> record (DeathType 5) never reaches FinishDeath —
    /// its pieces are what lies there — so its corpse stands on the classification alone.
    /// </summary>
    public static bool LeavesCorpse(bool titleStates, bool statedCorpse, CorpseDeathMode mode, bool unstatedExcluded) =>
        mode == CorpseDeathMode.CrumbleSplode || LeavesCorpse(titleStates, statedCorpse, unstatedExcluded);

    /// <summary>Evaluates all eligibility gates for turning a retired guest entity into a persistent corpse.</summary>
    public static bool IsEligibleCorpse(in CorpseCandidate candidate) =>
        candidate.IsDeadCreature &&
        candidate.GuestLeftCorpse &&
        candidate.HasSkeleton &&
        candidate.SawVisibleWhileDead &&
        !candidate.SawEtherealWhileAlive &&
        !candidate.IsPrincipal &&
        !candidate.IsCompanion &&
        candidate.DeathMode != CorpseDeathMode.Excluded &&
        (candidate.ClassifiedFromGdb || !HasExcludedName(candidate.ModelPath)) &&
        HasValidDeathPose(
            candidate.DeathMode,
            candidate.SawPosedWhileDead,
            candidate.SawRagdollWhileDead,
            candidate.SawDisarticulatedWhileDead,
            candidate.SawCollapsedPoseWhileDead);

    /// <summary>
    /// Selects which active non-fading corpse to evict first when exceeding the cap, strongly
    /// preferring distant corpses (&gt;25 m from the camera) before touching bodies near the player.
    /// </summary>
    public static int SelectEvictionIndex(ReadOnlySpan<CorpseSlotSummary> slots, Vector3 cameraPos)
    {
        float distantRadiusSq = DistantEvictionRadiusMeters * DistantEvictionRadiusMeters;
        int bestDistantIdx = -1;
        float bestDistantAge = float.MinValue;
        int bestAnyIdx = -1;
        float bestAnyAge = float.MinValue;

        for (int i = 0; i < slots.Length; ++i)
        {
            if (slots[i].FadingOut)
            {
                continue;
            }

            float age = slots[i].Age;
            if (age > bestAnyAge)
            {
                bestAnyAge = age;
                bestAnyIdx = i;
            }

            if (slots[i].Position.DistanceSquaredTo(cameraPos) > distantRadiusSq && age > bestDistantAge)
            {
                bestDistantAge = age;
                bestDistantIdx = i;
            }
        }

        return bestDistantIdx >= 0 ? bestDistantIdx : bestAnyIdx;
    }
}
