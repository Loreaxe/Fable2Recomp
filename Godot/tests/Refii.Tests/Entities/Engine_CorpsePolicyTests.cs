using System;
using GdUnit4;
using Godot;
using Refii.Core;
using Refii.Entities;
using Refii.Gdb;
using Refii.Host;
using static GdUnit4.Assertions;

namespace Refii.Tests;

[TestSuite]
public sealed class Engine_CorpsePolicyTests
{
    private const uint CreatureCompSlot = 0xC3B90D4Fu;
    private const uint DeathTypeField = 0xCD9174D9u;
    private const uint DeathMeshField = 0x0D364222u;
    private const uint SpecialisationField = 0xD3755112u;
    private const uint BaseTypeField = 0x72DB57C9u;
    private const uint PhysNavSlot = 0xC5A11B7Au;
    private const uint PhysCtrlSlot = 0xD51F7655u;
    private const uint PhysStaticSlot = 0xF73572C4u;
    private const uint HasRagdollField = 0xD09A6BC0u;
    private const uint ShadowCompSlot = 0x2B1E4BC7u;
    private const uint GhostVillagerCompSlot = 0xFB6177C1u;
    private const uint HollowmanWispCompSlot = 0xEA238706u;
    private const uint HeroStatueCompSlot = 0xB77F7EB2u;
    private const uint VillagerCompSlot = 0x5BA014D4u;
    private const uint JobTagField = 0x7FA702D2u;
    private const uint GhostJobTag = 0xFC18ABA6u;
    private const uint DogCompSlot = 0x65A5F006u;
    private const uint SentinelCompSlot = 0xA8B3788Au;
    private const uint MinionCompSlot = 0xFC3DF81Cu;

    private static uint AddCreatureRecord(
        GdbPatchDatabase db,
        uint recordHash,
        uint deathType,
        uint? deathMesh = null,
        bool? hasRagdoll = true,
        uint extraCompSlot = 0u,
        uint extraCompHash = 0u,
        uint? specialisation = null,
        uint? baseType = null,
        uint parentHash = 0u,
        uint? jobTag = null,
        bool useStaticPhysics = false,
        bool useControlledPhysics = false,
        string? name = null)
    {
        var entry = new GdbPatchEntry(recordHash, 0x01);
        if (parentHash != 0u)
        {
            entry.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, parentHash));
        }

        uint ccHash = recordHash + 1u;
        var cc = new GdbPatchEntry(ccHash, 0x01);
        cc.Properties.Add(new GdbPatchProperty(DeathTypeField, GdbFieldType.Enum, deathType));
        if (deathMesh.HasValue)
        {
            cc.Properties.Add(new GdbPatchProperty(DeathMeshField, GdbFieldType.String, deathMesh.Value));
        }
        if (specialisation.HasValue)
        {
            cc.Properties.Add(new GdbPatchProperty(SpecialisationField, GdbFieldType.Enum, specialisation.Value));
        }
        if (baseType.HasValue)
        {
            cc.Properties.Add(new GdbPatchProperty(BaseTypeField, GdbFieldType.Enum, baseType.Value));
        }
        db.Entries.Add(cc);
        entry.Properties.Add(new GdbPatchProperty(CreatureCompSlot, GdbFieldType.Reference, ccHash));

        if (useStaticPhysics)
        {
            uint stHash = recordHash + 2u;
            db.Entries.Add(new GdbPatchEntry(stHash, 0x01));
            entry.Properties.Add(new GdbPatchProperty(PhysStaticSlot, GdbFieldType.Reference, stHash));
        }
        else if (useControlledPhysics)
        {
            uint ctHash = recordHash + 2u;
            db.Entries.Add(new GdbPatchEntry(ctHash, 0x01));
            entry.Properties.Add(new GdbPatchProperty(PhysCtrlSlot, GdbFieldType.Reference, ctHash));
        }
        else if (hasRagdoll.HasValue)
        {
            uint phHash = recordHash + 2u;
            var ph = new GdbPatchEntry(phHash, 0x01);
            ph.Properties.Add(new GdbPatchProperty(HasRagdollField, GdbFieldType.Bool, hasRagdoll.Value ? 1u : 0u));
            db.Entries.Add(ph);
            entry.Properties.Add(new GdbPatchProperty(PhysNavSlot, GdbFieldType.Reference, phHash));
        }

        if (extraCompSlot != 0u)
        {
            uint ecHash = extraCompHash != 0u ? extraCompHash : recordHash + 3u;
            db.Entries.Add(new GdbPatchEntry(ecHash, 0x01));
            entry.Properties.Add(new GdbPatchProperty(extraCompSlot, GdbFieldType.Reference, ecHash));
        }

        if (jobTag.HasValue)
        {
            uint vcHash = recordHash + 4u;
            var vc = new GdbPatchEntry(vcHash, 0x01);
            vc.Properties.Add(new GdbPatchProperty(JobTagField, GdbFieldType.String, jobTag.Value));
            db.Entries.Add(vc);
            entry.Properties.Add(new GdbPatchProperty(VillagerCompSlot, GdbFieldType.Reference, vcHash));
        }

        if (!string.IsNullOrEmpty(name))
        {
            db.Names[recordHash] = name;
        }

        db.Entries.Add(entry);
        return recordHash;
    }

    [TestCase]
    public void ParsesLifetimeWithoutUpperCap()
    {
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds(null)).IsEqual(Engine_CorpsePolicy.DefaultLifetimeSeconds);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("")).IsEqual(Engine_CorpsePolicy.DefaultLifetimeSeconds);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("invalid")).IsEqual(Engine_CorpsePolicy.DefaultLifetimeSeconds);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("0")).IsEqual(0f);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("-12")).IsEqual(0f);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("\"45\"")).IsEqual(45f);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("300")).IsEqual(300f);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("7200")).IsEqual(7200f);
        AssertThat(Engine_CorpsePolicy.ParseLifetimeSeconds("86400")).IsEqual(86400f);
    }

    [TestCase]
    public void ParsesAndResolvesMaxCorpseCaps()
    {
        AssertThat(Engine_CorpsePolicy.ParseConfiguredMaxCount(null)).IsEqual(0);
        AssertThat(Engine_CorpsePolicy.ParseConfiguredMaxCount("-1")).IsEqual(-1);
        AssertThat(Engine_CorpsePolicy.ParseConfiguredMaxCount("128")).IsEqual(128);

        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(0f, 0, compactTier: false)).IsEqual(0);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(10f, 0, compactTier: false)).IsEqual(16);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(45f, 0, compactTier: false)).IsEqual(32);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(120f, 0, compactTier: false)).IsEqual(64);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(300f, 0, compactTier: false)).IsEqual(96);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(3600f, 0, compactTier: false)).IsEqual(96);

        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(10f, 0, compactTier: true)).IsEqual(8);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(20f, 0, compactTier: true)).IsEqual(12);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(45f, 0, compactTier: true)).IsEqual(20);

        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(45f, 64, compactTier: true)).IsEqual(64);
        AssertThat(Engine_CorpsePolicy.ResolveMaxCorpses(45f, -1, compactTier: false)).IsEqual(int.MaxValue);
    }

    [TestCase]
    public void DetectsEtherealAppearancesAndExcludedTypes()
    {
        var solid = new HostBridge.EntityAppearance(1f, 1f, 1f, Vector4.One, HostBridge.EntityAppearance.DrawableFlag);
        var shadowF2 = solid with { Alpha = 0.90f };
        var fadingOrGhost = solid with { Alpha = 0.75f };
        var glowingSpirit = solid with { TintLanes = new Vector4(0.8f, 2.1f, 3.0f, 1f) };
        var hiddenSolid = solid with { Flags = 0u };

        AssertThat(Engine_CorpsePolicy.IsEtherealAppearance(solid)).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEtherealAppearance(shadowF2)).IsTrue();
        AssertThat(Engine_CorpsePolicy.IsEtherealAppearance(fadingOrGhost)).IsTrue();
        AssertThat(Engine_CorpsePolicy.IsEtherealAppearance(glowingSpirit)).IsTrue();

        AssertThat(Engine_CorpsePolicy.IsSolidLivingAppearance(solid)).IsTrue();
        AssertThat(Engine_CorpsePolicy.IsSolidLivingAppearance(shadowF2)).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsSolidLivingAppearance(glowingSpirit)).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsSolidLivingAppearance(hiddenSolid)).IsFalse();

        // Fallback token list keeps non-physical/exploding names, but allows "sentinel".
        AssertThat(Engine_CorpsePolicy.HasExcludedName("creatures/beetle_bomb.mdl")).IsTrue();
        AssertThat(Engine_CorpsePolicy.HasExcludedName("creatures/Banshee_Elder.mdl")).IsTrue();
        AssertThat(Engine_CorpsePolicy.HasExcludedName("creatures/shadow_wisp.mdl")).IsTrue();
        AssertThat(Engine_CorpsePolicy.HasExcludedName("creatures/sentinel_base.mdl")).IsFalse();

        foreach (uint fieldHash in new[] { ShadowCompSlot, GhostVillagerCompSlot, HollowmanWispCompSlot, HeroStatueCompSlot })
        {
            AssertThat(Engine_CorpsePolicy.IsExcludedComponentFieldHash(fieldHash)).IsTrue();
        }
        AssertThat(Engine_CorpsePolicy.IsExcludedComponentFieldHash(CreatureCompSlot)).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsExcludedComponentFieldHash(DogCompSlot)).IsFalse(); // Shared by F3 Wolf
        AssertThat(Engine_CorpsePolicy.IsExcludedComponentFieldHash(SentinelCompSlot)).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsExcludedComponentFieldHash(MinionCompSlot)).IsFalse();

        // Unknown record hashes rely on model path fallback only when unmapped in GDB.
        AssertThat(Engine_CorpsePolicy.IsExcludedRecord(0x12345678u, "creatures/hollow_man_armoured.mdl")).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsExcludedRecord(0x12345678u, "characters/bandit_thug.mdl")).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsExcludedRecord(0x12345678u, "creatures/balverine_white.mdl")).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsExcludedRecord(0x12345678u, "creatures/banshee_elder.mdl")).IsTrue();
    }

    [TestCase]
    public void ClassifiesAllDeathTypesAndPhysicsFromGdb()
    {
        var db = new GdbPatchDatabase();
        const uint hollowMesh = 0x95247F72u;
        const uint sentinelMesh = 0x6092F8CFu;
        const uint minionMesh = 0x22B39D8Du;

        uint dt0Chicken = AddCreatureRecord(db, 0x10000100u, deathType: 0u, hasRagdoll: false);
        uint dt1Beetle = AddCreatureRecord(db, 0x10000200u, deathType: 1u, hasRagdoll: false);
        uint dt2Bandit = AddCreatureRecord(db, 0x10000300u, deathType: 2u, hasRagdoll: true);
        uint dt2NoRagdoll = AddCreatureRecord(db, 0x10000400u, deathType: 2u, hasRagdoll: false);
        uint dt2Static = AddCreatureRecord(db, 0x10000500u, deathType: 2u, useStaticPhysics: true);
        uint dt2Controlled = AddCreatureRecord(db, 0x10000600u, deathType: 2u, useControlledPhysics: true);
        uint dt3RenegadeLeader = AddCreatureRecord(db, 0x10000700u, deathType: 3u, hasRagdoll: true);
        uint dt4NightCrawler = AddCreatureRecord(db, 0x10000800u, deathType: 4u, hasRagdoll: false);
        uint dt5HollowMan = AddCreatureRecord(db, 0x10000900u, deathType: 5u, deathMesh: hollowMesh);
        uint dt5Sentinel = AddCreatureRecord(db, 0x10000A00u, deathType: 5u, deathMesh: sentinelMesh, extraCompSlot: SentinelCompSlot);
        uint dt5Minion = AddCreatureRecord(db, 0x10000B00u, deathType: 5u, deathMesh: minionMesh, extraCompSlot: MinionCompSlot);
        uint dt5NoMeshHollowWolf = AddCreatureRecord(db, 0x10000C00u, deathType: 5u, deathMesh: null);
        uint dt5ShadowChess = AddCreatureRecord(db, 0x10000D00u, deathType: 5u, deathMesh: hollowMesh, extraCompSlot: ShadowCompSlot);
        uint dt6Necromancer = AddCreatureRecord(db, 0x10000E00u, deathType: 6u, hasRagdoll: true);
        uint dt7Shadow = AddCreatureRecord(db, 0x10000F00u, deathType: 7u, hasRagdoll: false);
        uint dt8Shard = AddCreatureRecord(db, 0x10001000u, deathType: 8u, hasRagdoll: false);

        // DT=2 exclusions & Dog vs F3 Wolf edge case:
        uint ghostVillagerF3 = AddCreatureRecord(db, 0x10001100u, deathType: 2u, extraCompSlot: GhostVillagerCompSlot);
        uint wisp = AddCreatureRecord(db, 0x10001200u, deathType: 2u, extraCompSlot: HollowmanWispCompSlot);
        uint statue = AddCreatureRecord(db, 0x10001300u, deathType: 2u, extraCompSlot: HeroStatueCompSlot);
        uint ghostVillagerF2 = AddCreatureRecord(db, 0x10001400u, deathType: 2u, jobTag: GhostJobTag);
        uint shadowSpec = AddCreatureRecord(db, 0x10001500u, deathType: 2u, specialisation: 4u);
        uint dogRecord = AddCreatureRecord(db, 0x10001600u, deathType: 2u, extraCompSlot: DogCompSlot, baseType: 8u);
        uint wolfF3Record = AddCreatureRecord(db, 0x10001700u, deathType: 2u, extraCompSlot: DogCompSlot, baseType: 24u);

        GdbMulti multi = GdbMulti.Build(new[] { GdbWriter.Serialize(db) });

        static void AssertMode(GdbMulti m, uint id, Engine_CorpsePolicy.CorpseDeathMode expected, bool expectFromGdb = true)
        {
            Engine_CorpsePolicy.CorpseDeathMode mode = Engine_CorpsePolicy.EvaluateRecordMode(m, id, out bool fromGdb);
            AssertThat(mode).IsEqual(expected);
            AssertThat(fromGdb).IsEqual(expectFromGdb);
        }

        AssertMode(multi, dt0Chicken, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, dt1Beetle, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, dt2Bandit, Engine_CorpsePolicy.CorpseDeathMode.PhysicalRagdoll);
        AssertMode(multi, dt2NoRagdoll, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, dt2Static, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, dt2Controlled, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, dt3RenegadeLeader, Engine_CorpsePolicy.CorpseDeathMode.PhysicalRagdoll);
        AssertMode(multi, dt4NightCrawler, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, dt5HollowMan, Engine_CorpsePolicy.CorpseDeathMode.CrumbleSplode);
        AssertMode(multi, dt5Sentinel, Engine_CorpsePolicy.CorpseDeathMode.CrumbleSplode);
        AssertMode(multi, dt5Minion, Engine_CorpsePolicy.CorpseDeathMode.CrumbleSplode);
        AssertMode(multi, dt5NoMeshHollowWolf, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, dt5ShadowChess, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, dt6Necromancer, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, dt7Shadow, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, dt8Shard, Engine_CorpsePolicy.CorpseDeathMode.Excluded);

        AssertMode(multi, ghostVillagerF3, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, wisp, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, statue, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, ghostVillagerF2, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, shadowSpec, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, dogRecord, Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertMode(multi, wolfF3Record, Engine_CorpsePolicy.CorpseDeathMode.PhysicalRagdoll);

        // Missing record falls back cleanly without claiming GDB authority.
        AssertMode(multi, 0xDEADBEEFu, Engine_CorpsePolicy.CorpseDeathMode.PhysicalRagdoll, expectFromGdb: false);
    }

    [TestCase]
    public void ResolvesActiveLevelGdbInheritanceAndOverrides()
    {
        const uint grimParentHash = 0xE65A1981u;
        const uint grimLevelHash = 0x6D0E19E4u;
        const uint globalBirdHash = 0xB72F28D2u;
        const uint globalBirdCreatureComp = globalBirdHash + 1u;

        var baseDb = new GdbPatchDatabase();
        AddCreatureRecord(baseDb, grimParentHash, deathType: 7u, hasRagdoll: false, extraCompSlot: ShadowCompSlot);
        AddCreatureRecord(baseDb, globalBirdHash, deathType: 2u, hasRagdoll: true);

        var levelDb = new GdbPatchDatabase();
        var grimLevelEntry = new GdbPatchEntry(grimLevelHash, 0x01);
        grimLevelEntry.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, grimParentHash));
        levelDb.Entries.Add(grimLevelEntry);

        // Level-local override changing a CreatureComponent's DeathType from 2 -> 1 (Bird / Tabletop pattern).
        var overrideBirdCc = new GdbPatchEntry(globalBirdCreatureComp, 0x01);
        overrideBirdCc.Properties.Add(new GdbPatchProperty(DeathTypeField, GdbFieldType.Enum, 1u));
        levelDb.Entries.Add(overrideBirdCc);

        AssertThat(GdbView.TryParse(GdbWriter.Serialize(baseDb), out GdbView baseView, out _)).IsTrue();
        AssertThat(GdbView.TryParse(GdbWriter.Serialize(levelDb), out GdbView levelView, out _)).IsTrue();

        GdbMulti baseOnly = GdbMulti.Build(new[] { baseView });
        GdbMulti layered = GdbMulti.Build(new[] { baseView, levelView });

        // Base GDB alone does not contain the level-placed Cornelius Grim shadow record.
        AssertThat(Engine_CorpsePolicy.EvaluateRecordMode(baseOnly, grimLevelHash, out bool baseFound)).IsEqual(Engine_CorpsePolicy.CorpseDeathMode.PhysicalRagdoll);
        AssertThat(baseFound).IsFalse();

        // Layered global + active level GDB resolves inheritance (0x6D0E19E4 -> 0xE65A1981) & overrides.
        AssertThat(Engine_CorpsePolicy.EvaluateRecordMode(layered, grimLevelHash, out bool layeredFound)).IsEqual(Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertThat(layeredFound).IsTrue();
        AssertThat(Engine_CorpsePolicy.EvaluateRecordMode(layered, globalBirdHash, out bool birdOverridden)).IsEqual(Engine_CorpsePolicy.CorpseDeathMode.Excluded);
        AssertThat(birdOverridden).IsTrue();

        // Active level GDB setter integrates with ClassifyRecord cache and cleans up properly.
        const uint levelOnlyShadow = 0x22223333u;
        var localLevelDb = new GdbPatchDatabase();
        AddCreatureRecord(localLevelDb, levelOnlyShadow, deathType: 7u, extraCompSlot: ShadowCompSlot);
        AssertThat(GdbView.TryParse(GdbWriter.Serialize(localLevelDb), out GdbView localLevelView, out _)).IsTrue();

        try
        {
            Engine_CorpsePolicy.SetActiveLevelGdb(null);
            AssertThat(Engine_CorpsePolicy.ClassifyRecord(levelOnlyShadow, null, out bool beforeSet)).IsEqual(Engine_CorpsePolicy.CorpseDeathMode.PhysicalRagdoll);
            AssertThat(beforeSet).IsFalse();

            Engine_CorpsePolicy.SetActiveLevelGdb(localLevelView);
            AssertThat(Engine_CorpsePolicy.ClassifyRecord(levelOnlyShadow, null, out bool afterSet)).IsEqual(Engine_CorpsePolicy.CorpseDeathMode.Excluded);
            AssertThat(afterSet).IsTrue();

            Engine_CorpsePolicy.SetActiveLevelGdb(null);
            AssertThat(Engine_CorpsePolicy.ClassifyRecord(levelOnlyShadow, null, out bool afterClear)).IsEqual(Engine_CorpsePolicy.CorpseDeathMode.PhysicalRagdoll);
            AssertThat(afterClear).IsFalse();
        }
        finally
        {
            Engine_CorpsePolicy.SetActiveLevelGdb(null);
        }
    }

    [TestCase]
    public void ValidatesPoseCollapseAndDeathPoseGates()
    {
        AssertThat(Engine_CorpsePolicy.IsPoseCollapsed(1.65f, 1.60f)).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsPoseCollapsed(1.65f, 0.35f)).IsTrue();
        AssertThat(Engine_CorpsePolicy.IsPoseCollapsed(0.20f, 0.05f)).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsPoseCollapsed(float.NaN, 0.1f)).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsPoseCollapsed(1.5f, float.PositiveInfinity)).IsFalse();

        var ragdoll = new Engine_CorpsePolicy.CorpseCandidate(
            IsDeadCreature: true,
            GuestLeftCorpse: true,
            HasSkeleton: true,
            SawVisibleWhileDead: true,
            SawEtherealWhileAlive: false,
            IsPrincipal: false,
            IsCompanion: false,
            RecordHash: 0x12345678u,
            ModelPath: "characters/bandit_thug.mdl",
            DeathMode: Engine_CorpsePolicy.CorpseDeathMode.PhysicalRagdoll,
            ClassifiedFromGdb: true,
            SawPosedWhileDead: true,
            SawRagdollWhileDead: true,
            SawDisarticulatedWhileDead: false,
            SawCollapsedPoseWhileDead: false);

        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(ragdoll)).IsTrue();
        // Offscreen death (never posed while dead) is rejected so frozen standing NPCs cannot persist.
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(ragdoll with { SawPosedWhileDead = false })).IsFalse();
        // Posed upright without ragdoll or collapsed death clip is rejected.
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(ragdoll with { SawRagdollWhileDead = false, SawCollapsedPoseWhileDead = false })).IsFalse();
        // Clip death (e.g. Renegade Leader / finisher) passes when posed pose actually collapsed.
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(ragdoll with { SawRagdollWhileDead = false, SawCollapsedPoseWhileDead = true })).IsTrue();

        // Standard eligibility disqualifiers remain strict.
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(ragdoll with { IsDeadCreature = false })).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(ragdoll with { HasSkeleton = false })).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(ragdoll with { SawVisibleWhileDead = false })).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(ragdoll with { SawEtherealWhileAlive = true })).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(ragdoll with { IsPrincipal = true })).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(ragdoll with { IsCompanion = true })).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(ragdoll with { DeathMode = Engine_CorpsePolicy.CorpseDeathMode.Excluded })).IsFalse();

        // CrumbleSplode (Hollow Men, Sentinels, Minions):
        var crumble = ragdoll with
        {
            DeathMode = Engine_CorpsePolicy.CorpseDeathMode.CrumbleSplode,
            ModelPath = "creatures/hollow_man.mdl",
            SawRagdollWhileDead = false,
            SawDisarticulatedWhileDead = true,
        };
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(crumble)).IsTrue();
        // Uncrumbled standing hollow man is rejected cleanly.
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(crumble with { SawDisarticulatedWhileDead = false, SawCollapsedPoseWhileDead = false })).IsFalse();
        // Ragdoll without collapsing cannot stand-in for CrumbleSplode, while ragdoll + collapsed can.
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(crumble with { SawDisarticulatedWhileDead = false, SawRagdollWhileDead = true, SawCollapsedPoseWhileDead = false })).IsFalse();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(crumble with { SawDisarticulatedWhileDead = false, SawRagdollWhileDead = true, SawCollapsedPoseWhileDead = true })).IsTrue();
        // GDB-authoritative DT=5 (Sentinel/Minion) is kept even if model path contains "darkness".
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(crumble with { ModelPath = "creatures/darkness_minion.mdl", ClassifiedFromGdb = true })).IsTrue();
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(crumble with { ModelPath = "creatures/darkness_minion.mdl", ClassifiedFromGdb = false })).IsFalse();
        // The guest did not leave it lying (no FinishDeath stated): no corpse, whatever the record says.
        AssertThat(Engine_CorpsePolicy.IsEligibleCorpse(ragdoll with { GuestLeftCorpse = false })).IsFalse();
    }

    /// <summary>
    /// Each DeathType of the death switch (TU1 `sub_823A6C58`, disc `sub_824351C0`, jump table
    /// resolved: 1 destroy, 2 FinishDeath, 3/4 the Die action with its destroy flag = type==4,
    /// 5 crumble, 6 fade, 7 shadow, 8 shard) as the bridge states it: the corpse bit is set only
    /// where FinishDeath runs, 2 and 3.
    /// </summary>
    [TestCase]
    public void KeepsOnlyTheDeathTypesTheGuestLeavesLying()
    {
        for (int deathType = 1; deathType <= 8; ++deathType)
        {
            bool finishDeathRuns = deathType is 2 or 3;
            bool kept = Engine_CorpsePolicy.LeavesCorpse(titleStates: true, statedCorpse: finishDeathRuns,
                unstatedExcluded: false);
            AssertThat(kept).IsEqual(finishDeathRuns);
        }

        // A stated title never consults the exclusions, either way.
        AssertThat(Engine_CorpsePolicy.LeavesCorpse(true, statedCorpse: true, unstatedExcluded: true)).IsTrue();
        AssertThat(Engine_CorpsePolicy.LeavesCorpse(true, statedCorpse: false, unstatedExcluded: false)).IsFalse();

        // A crumble (DeathType 5) never reaches FinishDeath: its classification alone decides.
        AssertThat(Engine_CorpsePolicy.LeavesCorpse(true, statedCorpse: false,
            Engine_CorpsePolicy.CorpseDeathMode.CrumbleSplode, unstatedExcluded: false)).IsTrue();
        AssertThat(Engine_CorpsePolicy.LeavesCorpse(true, statedCorpse: false,
            Engine_CorpsePolicy.CorpseDeathMode.PhysicalRagdoll, unstatedExcluded: false)).IsFalse();
        AssertThat(Engine_CorpsePolicy.LeavesCorpse(true, statedCorpse: true,
            Engine_CorpsePolicy.CorpseDeathMode.PhysicalRagdoll, unstatedExcluded: false)).IsTrue();

        // An unstated title (Fable 3) keeps its exclusions.
        AssertThat(Engine_CorpsePolicy.LeavesCorpse(false, statedCorpse: false, unstatedExcluded: false)).IsTrue();
        AssertThat(Engine_CorpsePolicy.LeavesCorpse(false, statedCorpse: false, unstatedExcluded: true)).IsFalse();
    }

    [TestCase]
    public void CorpseBitIsLivenessNotLook()
    {
        var corpse = new HostBridge.EntityAppearance(1f, 1f, 1f, Vector4.One,
            HostBridge.EntityAppearance.DrawableFlag | HostBridge.EntityAppearance.DeadCreatureFlag |
            HostBridge.EntityAppearance.GuestCorpseFlag);

        AssertThat(corpse.IsGuestCorpse).IsTrue();
        AssertThat(corpse.IsConstructed).IsTrue();
        AssertThat(corpse.VisualOnly.Flags).IsEqual(HostBridge.EntityAppearance.DrawableFlag);
        AssertThat(corpse.VisualOnly.IsGuestCorpse).IsFalse();
    }

    [TestCase]
    public void EvictsOldestDistantCorpseBeforeTouchingNearbyOnes()
    {
        Vector3 cameraPos = Vector3.Zero;
        ReadOnlySpan<Engine_CorpsePolicy.CorpseSlotSummary> slots = new Engine_CorpsePolicy.CorpseSlotSummary[]
        {
            new(new Vector3(4f, 0f, 0f), Age: 40f, FadingOut: false),   // 0: nearby oldest
            new(new Vector3(32f, 0f, 0f), Age: 15f, FadingOut: false),  // 1: distant younger
            new(new Vector3(40f, 0f, 0f), Age: 25f, FadingOut: false),  // 2: distant oldest
            new(new Vector3(50f, 0f, 0f), Age: 99f, FadingOut: true),   // 3: already fading out
        };

        AssertThat(Engine_CorpsePolicy.SelectEvictionIndex(slots, cameraPos)).IsEqual(2);

        ReadOnlySpan<Engine_CorpsePolicy.CorpseSlotSummary> allNearby = new Engine_CorpsePolicy.CorpseSlotSummary[]
        {
            new(new Vector3(3f, 0f, 0f), Age: 12f, FadingOut: false),
            new(new Vector3(8f, 0f, 0f), Age: 28f, FadingOut: false),
        };

        AssertThat(Engine_CorpsePolicy.SelectEvictionIndex(allNearby, cameraPos)).IsEqual(1);
    }
}
