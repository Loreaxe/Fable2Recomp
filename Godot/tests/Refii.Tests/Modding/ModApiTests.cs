namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;
using Refii.Titles.Fable2;

/// <summary>
/// The catalog (Engine_ModApi) is held to the header (core/refii_mod_events.h) and to the two
/// build configs: every event's kind and name agree with the enum, every seam function is named
/// in both builds, and the Lua names are distinct identifiers.
/// </summary>
[TestSuite]
public class ModApiTests
{
    private static string? RepoRoot()
    {
        string? at = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (File.Exists(Path.Combine(at, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h")))
            {
                return at;
            }

            at = Path.GetDirectoryName(at);
        }

        return null;
    }

    /// <summary>The modders' guide in the repo: <c>Godot/docs/modding</c> (the SDK's <c>docs/modding</c>).</summary>
    private static string GuideFolder(string root) => Path.Combine(root, "Godot", ModGuide.ProjectFolder);

    /// <summary>Every guide file's text, in reading order.</summary>
    private static string GuideText(string root) =>
        string.Join("\n", ModGuide.Files(GuideFolder(root)).Select(f => File.ReadAllText(Path.Combine(GuideFolder(root), f))));

    [TestCase]
    public void KindsAreDenseAndNamesUnique()
    {
        for (int i = 0; i < ModApi.Events.Count; i++)
        {
            AssertThat(ModApi.Events[i].Kind).IsEqual(i);
        }

        AssertThat(ModApi.Events.Select(e => e.Name).Distinct().Count()).IsEqual(ModApi.Events.Count);
        AssertThat(ModApi.Events.Select(e => e.LuaName).Distinct().Count()).IsEqual(ModApi.Events.Count);
        foreach (ModApiEvent e in ModApi.Events)
        {
            AssertThat(Regex.IsMatch(e.LuaName, "^[a-z][a-z0-9_]*$")).IsTrue();
            AssertThat(e.Fields.Count > 0).IsTrue();
        }
    }

    [TestCase]
    public void HeaderAgreesWithCatalog()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;  // a packaged test run without the source tree
        }

        string header = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h"));
        Match version = Regex.Match(header, @"#define REFII_MOD_API_VERSION (\d+)");
        AssertThat(version.Success).IsTrue();
        AssertThat(int.Parse(version.Groups[1].Value)).IsEqual(ModApi.Version);

        var kinds = new Dictionary<string, int>();
        foreach (Match m in Regex.Matches(header, @"REFII_EVENT_([A-Z_]+) = (\d+)"))
        {
            kinds[m.Groups[1].Value] = int.Parse(m.Groups[2].Value);
        }

        AssertThat(kinds["COUNT"]).IsEqual(ModApi.Events.Count);
        foreach (ModApiEvent e in ModApi.Events)
        {
            string enumName = e.LuaName.ToUpperInvariant();
            AssertThat(kinds.ContainsKey(enumName)).OverrideFailureMessage($"{e.Name}: no REFII_EVENT_{enumName} in the header").IsTrue();
            AssertThat(kinds[enumName]).OverrideFailureMessage($"{e.Name}: kind differs from the header").IsEqual(e.Kind);

            // The struct, with the catalog's kind and every field it lists (ctx/base/cancel are the base's).
            Match structMatch = Regex.Match(header, @"struct " + e.Name + @"Event : (\w+) \{(.*?)\};", RegexOptions.Singleline);
            AssertThat(structMatch.Success).OverrideFailureMessage($"{e.Name}: no struct {e.Name}Event in the header").IsTrue();
            AssertThat(structMatch.Groups[2].Value.Contains($"kKind = REFII_EVENT_{enumName};")).IsTrue();
            string body = structMatch.Groups[2].Value;
            string parent = structMatch.Groups[1].Value;
            string parentBody = parent == "Event"
                ? string.Empty
                : Regex.Match(header, @"struct " + parent + @" : \w+ \{(.*?)\};", RegexOptions.Singleline).Groups[1].Value;
            foreach (ModApiField f in e.Fields)
            {
                bool declared = Regex.IsMatch(body + parentBody, @"\b" + f.Name + @";");
                AssertThat(declared).OverrideFailureMessage($"{e.Name}.{f.Name}: not a field of the struct").IsTrue();
            }
        }
    }

    [TestCase]
    public void SeamsAreNamedInBothBuilds()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        foreach (string build in new[] { "Fable2", "Fable2TU1" })
        {
            string config = File.ReadAllText(Path.Combine(root, "Fable2", "versions", build, "refii_config.toml"));
            foreach (string function in ModApi.Events.SelectMany(e => e.SeamFunctions).Distinct())
            {
                AssertThat(config.Contains("\"" + function + "\""))
                    .OverrideFailureMessage($"{function} is not named in versions/{build}/refii_config.toml")
                    .IsTrue();
            }
        }
    }

    [TestCase]
    public void LuaAndGodotReceiveEveryCatalogField()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        // The table text the bus builds per kind (mod_events.cpp LuaFields) is what both Lua and
        // Godot scripts receive: every field the catalog offers them must be in it.
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        foreach (ModApiEvent e in ModApi.Events)
        {
            string label = "case REFII_EVENT_" + e.LuaName.ToUpperInvariant() + ":";
            int at = bus.IndexOf(label, StringComparison.Ordinal);
            AssertThat(at >= 0).OverrideFailureMessage($"{e.Name}: no LuaFields case").IsTrue();
            int end = bus.IndexOf("return;", at, StringComparison.Ordinal);
            string body = bus[at..end];
            foreach (ModApiField f in e.Fields.Where(f => !f.NativeOnly && !f.Type.Contains('*') || f.Type.Contains("string")))
            {
                AssertThat(body.Contains(f.Name + "=", StringComparison.Ordinal))
                    .OverrideFailureMessage($"{e.Name}.{f.Name}: the catalog offers it to Lua and Godot, the bus never sends it")
                    .IsTrue();
            }
        }
    }

    [TestCase]
    public void CoopEventsStateTheirPlayerLast()
    {
        // Added after the API shipped: a trailing field, so a mod built against the old struct
        // keeps every offset it reads.
        foreach (string name in new[]
                 {
                     "dog_trick", "opinion_modified", "sleep_menu", "travel", "fast_travel", "menu_opened",
                     "menu_closed", "menu_screen", "weather_region", "sleep_chosen",
                 })
        {
            ModApiEvent e = ModApi.Find(name)!;
            AssertThat(e.Fields[^1].Name).OverrideFailureMessage($"{e.Name}: player is not the last field").IsEqual("player");
            AssertThat(e.Fields[^1].Type).IsEqual("int32_t");
        }

        ModApiEvent weather = ModApi.Find("dynamic_weather")!;
        AssertThat(weather.Kind).IsEqual(86);
        AssertThat(string.Join(",", weather.Fields.Select(f => f.Name))).IsEqual("level,weather,previous,seconds,from_host");
    }

    /// <summary>
    /// THE MASKS HOLD EVERY KIND, on every surface a subscription crosses as bits: Lua's groups
    /// (float32-exact: never more than 20 significant bits in one number), the host's words
    /// (Godot's event and verdict masks). The catalog's constants are the payload's, and the kind
    /// count stays under each capacity — the next kind past a mask fails here, not silently.
    /// </summary>
    [TestCase]
    public void TheMasksHoldEveryKindOnEverySurface()
    {
        AssertThat(ModApi.LuaMaskBits <= 20).OverrideFailureMessage("a game Lua number is a float32: at most 20 significant bits per group").IsTrue();
        AssertThat(ModApi.LuaMaskCapacity).IsEqual(ModApi.LuaMaskBits * ModApi.LuaMaskGroups);
        AssertThat(ModApi.HostMaskCapacity).IsEqual(64 * ModApi.HostMaskWords);
        AssertThat(ModApi.LuaMaskCapacity >= 200).IsTrue();
        AssertThat(ModApi.HostMaskCapacity >= 200).IsTrue();
        AssertThat(ModApi.Events.Count <= ModApi.LuaMaskCapacity)
            .OverrideFailureMessage($"{ModApi.Events.Count} kinds exceed the Lua masks' {ModApi.LuaMaskCapacity}: raise kLuaMaskGroups (mod_events.cpp) and ModApi.LuaMaskGroups")
            .IsTrue();
        AssertThat(ModApi.Events.Count <= ModApi.HostMaskCapacity)
            .OverrideFailureMessage($"{ModApi.Events.Count} kinds exceed the host masks' {ModApi.HostMaskCapacity}: raise REFII_MOD_HOST_MASK_WORDS (payload_bridge.h), ModApi.HostMaskWords and ModEventMask's words")
            .IsTrue();

        // The host's mask type holds its whole capacity, word by word.
        AssertThat(ModEventMask.Capacity).IsEqual(ModApi.HostMaskCapacity);
        AssertThat(ModEventMask.Words).IsEqual(ModApi.HostMaskWords);
        var empty = new ModEventMask();
        AssertThat(empty.IsZero).IsTrue();
        for (int word = 0; word < ModEventMask.Words; word++)
        {
            ModEventMask low = empty.With(64 * word);
            ModEventMask high = empty.With(64 * word + 63);
            AssertThat(low.Word(word)).OverrideFailureMessage($"word {word} bit 0").IsEqual(1ul);
            AssertThat(high.Word(word)).OverrideFailureMessage($"word {word} bit 63").IsEqual(1ul << 63);
            AssertThat(high.Has(64 * word + 63)).IsTrue();
            AssertThat(high.Has(64 * word + 62)).IsFalse();
            AssertThat(low.IsZero).IsFalse();
            AssertThat(low == high).IsFalse();
        }

        AssertThat(empty.With(ModEventMask.Capacity).IsZero).OverrideFailureMessage("a kind past the capacity is never in the set").IsTrue();
        AssertThat(empty.With(-1).IsZero).IsTrue();
        AssertThat(empty.With(ModEventMask.Capacity - 1).Has(ModEventMask.Capacity - 1)).IsTrue();
        AssertThat(empty.With(ModApi.Events.Count - 1).Has(ModApi.Events.Count - 1)).IsTrue();

        // A full group is exact as the game Lua's float32 number (the reader clamps to it).
        int fullGroup = (1 << ModApi.LuaMaskBits) - 1;
        AssertThat((int)(float)fullGroup).IsEqual(fullGroup);
        AssertThat((int)(float)(fullGroup + 1)).IsEqual(fullGroup + 1);

        if (RepoRoot() is not { } root)
        {
            return;
        }

        // The preamble's Refii.on groups by the catalog's bit count (the composed preamble is not
        // built here: it folds in ModSettings, which is Godot-backed, and the test host has no Godot).
        string preamble = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs"));
        AssertThat(preamble.Contains("\"\\tlocal group = math.floor(kind / \" + ModApi.LuaMaskBits + \") + 1\\n\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(preamble.Contains("\"\\tlocal bit = 2 ^ (kind % \" + ModApi.LuaMaskBits + \")\\n\"", StringComparison.Ordinal)).IsTrue();

        // The payload's constants are the catalog's: the Lua reader's groups, the host's words, and
        // the header's count under both (the static_asserts beside them say the same at compile time).
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        Match bits = Regex.Match(bus, @"constexpr int32_t kLuaMaskBits = (\d+);");
        Match groups = Regex.Match(bus, @"constexpr int32_t kLuaMaskGroups = (\d+);");
        AssertThat(bits.Success && groups.Success).IsTrue();
        AssertThat(int.Parse(bits.Groups[1].Value)).OverrideFailureMessage("kLuaMaskBits != ModApi.LuaMaskBits").IsEqual(ModApi.LuaMaskBits);
        AssertThat(int.Parse(groups.Groups[1].Value)).OverrideFailureMessage("kLuaMaskGroups != ModApi.LuaMaskGroups").IsEqual(ModApi.LuaMaskGroups);
        AssertThat(bus.Contains("static_assert(REFII_EVENT_COUNT <= kLuaMaskBits * kLuaMaskGroups", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("refii_mod_game_lua_int(&ctx, base, expr, 0, (1 << kLuaMaskBits) - 1, &mask)", StringComparison.Ordinal))
            .OverrideFailureMessage("the Lua reader clamps each group to its bit count").IsTrue();

        string bridge = File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "payload_bridge.h"));
        Match words = Regex.Match(bridge, @"#define REFII_MOD_HOST_MASK_WORDS (\d+)");
        AssertThat(words.Success).IsTrue();
        AssertThat(int.Parse(words.Groups[1].Value)).OverrideFailureMessage("REFII_MOD_HOST_MASK_WORDS != ModApi.HostMaskWords").IsEqual(ModApi.HostMaskWords);
        string values = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        AssertThat(values.Contains("static_assert(REFII_EVENT_COUNT <= refii::mods::kHostMaskCapacity", StringComparison.Ordinal)).IsTrue();

        string header = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h"));
        Match count = Regex.Match(header, @"REFII_EVENT_COUNT = (\d+)");
        AssertThat(count.Success).IsTrue();
        int kinds = int.Parse(count.Groups[1].Value);
        AssertThat(kinds <= ModApi.LuaMaskCapacity).OverrideFailureMessage($"REFII_EVENT_COUNT {kinds} exceeds the Lua masks' {ModApi.LuaMaskCapacity}").IsTrue();
        AssertThat(kinds <= ModApi.HostMaskCapacity).OverrideFailureMessage($"REFII_EVENT_COUNT {kinds} exceeds the host masks' {ModApi.HostMaskCapacity}").IsTrue();

        // Every word of the host's masks has a way across: 0 and 1 by the exports, the rest by the lane.
        string hostBridge = File.ReadAllText(Path.Combine(root, "Godot", "src", "host", "HostBridge.cs"));
        AssertThat(hostBridge.Contains("mask_word event {word} {mask.Word(word):x}", StringComparison.Ordinal)).IsTrue();
        AssertThat(hostBridge.Contains("mask_word verdict {word} {mask.Word(word):x}", StringComparison.Ordinal)).IsTrue();
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("op == \"mask_word\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("refii_bridge_mod_set_verdict_mask_word(word, bits)", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("refii_bridge_mod_set_host_event_mask_word(word, bits)", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void GodotHelpersNameEveryCommandAndQuery()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        // Every verb the guest tick answers (mod_commands.cpp) is in the catalog's Godot section,
        // which the Guide and RefiiModApi.ListGodotHelpers show.
        string grammar = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        List<string> verbs = Regex.Matches(grammar, "(?:what|op) == \"([a-z_]+)\"")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        AssertThat(verbs.Count > 30).IsTrue();
        // menu_row and box_answer are the host's own: Godot's menu lists and host boxes post them
        // when a mod's row is chosen or its box answered (Engine_UiModMenus, Engine_UiModBoxes), so
        // no script calls them; notice_done and menu_box_answer likewise for a notice gone and a menu
        // box answered. fx_play and fx_stop likewise: Engine_FxPlayer posts them for a
        // play_fx under the classic look; a script calls play_fx / stop_fx.
        // fx_reload is the remount's (Engine_ModRemount -> Engine_FxModEffects.ReloadGuestBank), and
        // so are gdb_reload and gdb_revert (Engine_ModRemount -> GdbHotReload.Apply).
        // mask_word is HostBridge.SetModEventMask / SetModVerdictMask's: the words of the host's
        // masks past the two the exports carry. appearance_state is GameCharacterMorphs': a settings
        // slot's look stated for its player where that player is played; a script calls set_appearance.
        foreach (string verb in verbs.Where(v => v is not ("menu_row" or "box_answer" or "notice_done" or "menu_box_answer" or "fx_play" or "fx_stop" or "fx_reload" or "gdb_reload" or "gdb_revert" or "mask_word" or "appearance_state")))
        {
            AssertThat(Regex.IsMatch(godot, @"\b" + verb + @"\b"))
                .OverrideFailureMessage($"'{verb}' is answered by mod_commands.cpp but missing from ModApi.GodotHelpers")
                .IsTrue();
        }
    }

    [TestCase]
    public void CatalogListsTheSharedPlayerPrimitives()
    {
        // The co-op primitives the core layer and the mods share (Forge/Fabric style): each is
        // documented on every surface that has it.
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        foreach (string call in new[] { "Refii.player_for(", "Refii.acting_player(", "Refii.run_as_player(", "Refii.combat_state(", "Refii.world_here(", "Refii.player_of(", "Refii.event_replica(", "Refii.show_to_player(", "Refii.forgive_crimes(", "Refii.discovered_crime(", "Refii.perceived_renown(" })
        {
            AssertThat(lua.Contains(call, StringComparison.Ordinal)).OverrideFailureMessage($"{call} is not in ModApi.LuaHelpers").IsTrue();
        }

        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        foreach (string call in new[] { "refii_mod_player_for(", "refii_mod_world_here(", "refii_mod_player_of(", "refii_mod_event_replica(", "refii_mod_forgive_crimes(", "refii_mod_discovered_crime(", "refii_mod_perceived_renown(" })
        {
            AssertThat(native.Contains(call, StringComparison.Ordinal)).OverrideFailureMessage($"{call} is not in ModApi.Helpers").IsTrue();
        }

        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        foreach (string verb in new[] { "player_for", "combat_state", "world_here", "replica", "forgive_crimes", "discovered_crime", "perceived_renown" })
        {
            AssertThat(Regex.IsMatch(godot, @"\b" + verb + @"\b")).OverrideFailureMessage($"'{verb}' is not in ModApi.GodotHelpers").IsTrue();
        }

        AssertThat(ModApi.PlayerAgnostic.Contains("NEVER ASSUME PLAYER ONE", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        foreach (string call in new[] { "refii_mod_player_for(PPCContext* ctx, uint8_t* base, uint32_t entity)", "refii_mod_world_here(uint8_t* base)",
                     "refii_mod_forgive_crimes(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero)",
                     "refii_mod_discovered_crime(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero)" })
        {
            AssertThat(header.Contains("extern \"C\" int32_t " + call, StringComparison.Ordinal))
                .OverrideFailureMessage($"{call} is not declared in core/refii_mod.h").IsTrue();
        }
    }

    [TestCase]
    public void TheRouteIsTheCoreLuasAndSaveSafe()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        // The route (player two's boxes, questions, cameras and Player calls on player two's screen)
        // is defined once, in the core co-op Lua, and exposed as Refii.show_to_player; a question
        // waits through the frozen trampolines only (a saved coroutine resumes into them by name).
        string lua = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "host", "CoopHeroLua.cs"));
        AssertThat(lua.Contains("\"Api.show_to_player\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("C.Call1 = C.Permanent(function(after, state, fn, ...) return after(state, fn(...)) end, \"Call1\")", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("C.Wait1 = C.Permanent(function(poll, state, yield) while true do local done, value = poll(state) if done then return value end yield() end end, \"Wait1\")", StringComparison.Ordinal)).IsTrue();
        AssertThat(Regex.Matches(lua, @"return C\.Call1\(C\.RouteUnpack, nil, C\.Wait1, C\.RouteAnswerPoll, out, coroutine\.yield\)").Count).IsEqual(2);

        string wire = File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "event_relay.h"));
        AssertThat(wire.Contains("ps.r=", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void RelayedEventsAgreeWithTheBus()
    {
        // The catalog marks exactly the kinds mod_events.cpp's relay table carries.
        foreach (string name in new[] { "MeleeSwing", "ItemUsed", "RollStarted", "HitLanded" })
        {
            AssertThat(ModApi.Find(name)!.IsRelayed).OverrideFailureMessage($"{name} is not marked relayed").IsTrue();
        }

        foreach (ModApiEvent e in ModApi.Events.Where(e => e.IsRelayed))
        {
            AssertThat(e.Relayed.StartsWith("RELAYED:", StringComparison.Ordinal)).IsTrue();
        }

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        string[] rows = Regex.Matches(bus, @"REFII_RELAY\((\w+), k(?:Player|World)\)")
            .Select(m => m.Groups[1].Value).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        AssertThat(rows).IsEqual(ModApi.Events.Where(e => e.IsRelayed).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [TestCase]
    public void TheWheelSwitchedLockIsOnTheTickSurfaces()
    {
        // One primitive on the surfaces that run on the guest tick: a lock stated each tick to
        // switch by the mouse wheel. Souls Combat states it for its toggled lock.
        ModApiHelper native = ModApi.Helpers.Single(h => h.Signature.Contains("refii_mod_wheel_switches_target(base, hero)", StringComparison.Ordinal));
        ModApiHelper lua = ModApi.LuaHelpers.Single(h => h.Signature.Contains("Refii.wheel_switches_target(e)", StringComparison.Ordinal));
        AssertThat(native.Meaning).IsEqual(lua.Meaning);
        AssertThat(native.Meaning.Contains("CheckForBetterTargetInDirection", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Meaning.Contains("down the next target right, up the next left", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        AssertThat(header.Contains("extern \"C\" void refii_mod_wheel_switches_target(uint8_t* base, uint32_t hero);", StringComparison.Ordinal)).IsTrue();
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        AssertThat(natives.Contains("{\"wheel_switches_target\", &NativeWheelSwitchesTarget, 0}", StringComparison.Ordinal)).IsTrue();
        // Souls Combat's toggled lock, now Lua (mod/lockon.lua), states it sticky with the lock and
        // again when the lock's target changes.
        string souls = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "Fable2", "mods-src", "souls_combat", "mod"), "*.lua").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText));
        AssertThat(souls.Contains("Refii.wheel_switches_target(hero, on)", StringComparison.Ordinal)).IsTrue();
        AssertThat(souls.Contains("Refii.wheel_switches_target(e.hero, true)", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheEntityTintIsOnEverySurface()
    {
        // One primitive, three surfaces: native, Lua and Godot all reach refii_mod_entity_tint, the
        // game's own vector tint setter (sub_82573770), and its untint.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        AssertThat(native.Contains("refii_mod_entity_tint(ctx, base, entity, r, g, b, a)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_entity_untint(ctx, base, entity)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.entity_tint(e, r, g, b [, a])", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.entity_untint(e)", StringComparison.Ordinal)).IsTrue();
        ModApiHelper tint = ModApi.GodotHelpers.Single(h => h.Signature.StartsWith("entity_tint(", StringComparison.Ordinal));
        AssertThat(ModApi.GodotMethodNames(tint.Signature).ToArray()).IsEqual(new[] { "entity_tint", "entity_untint" });
        AssertThat(tint.Meaning.Contains("opacity is not the tint's", StringComparison.Ordinal)).IsTrue();
        AssertThat(Regex.IsMatch(godot, @"\bentity_untint\b")).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_entity_tint(PPCContext* ctx, uint8_t* base, uint32_t entity, float r, float g, float b,", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_entity_untint(PPCContext* ctx, uint8_t* base, uint32_t entity);", StringComparison.Ordinal)).IsTrue();

        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        AssertThat(natives.Contains("{\"entity_tint\", &NativeEntityTint, 0}", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("{\"entity_untint\", &NativeEntityUntint, 0}", StringComparison.Ordinal)).IsTrue();

        // The setter is called by its TU1 address, so the disc build needs its row (same 48
        // instructions at 0x82548418).
        string rows = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc"));
        AssertThat(rows.Contains("{0x82573770u, 0x82548418u},", StringComparison.Ordinal)).IsTrue();
        string api = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp")) + File.ReadAllText(Path.Combine(root, "src", "payload", "src", "core", "mod_requests.cpp")));
        AssertThat(api.Contains("kSetTintVector = 0x82573770u", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheGuestSideAdditionsAreOnEverySurface()
    {
        // Part H's guest-side items: the appearance setters beside the tint (H.1), the target lock
        // (H.3), statuses (H.7) and knockback/stagger (H.8) — native, Lua and Godot each name every
        // call, and every guest function called by its TU1 address has its disc row.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        string[] calls =
        {
            "entity_alpha", "entity_scale", "entity_saturation_mask", "entity_drawable",
            "lock_target", "release_lock", "switch_target",
            "apply_status", "clear_status",
            "knockback", "stagger",
        };
        foreach (string call in calls)
        {
            AssertThat(native.Contains("refii_mod_" + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("native " + call).IsTrue();
            AssertThat(lua.Contains("Refii." + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("lua " + call).IsTrue();
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(call))).OverrideFailureMessage("godot " + call).IsTrue();
        }
        // The reads are Lua natives and Godot queries.
        AssertThat(lua.Contains("Refii.locked_target(p)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.has_status(e, kind)", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("query locked_target P / has_status E KIND", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_locked_target(ctx, base, player)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_has_status(base, entity, kind)", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        foreach (string call in calls)
        {
            AssertThat(header.Contains("refii_mod_" + call + "(PPCContext* ctx, uint8_t* base, ", StringComparison.Ordinal)).OverrideFailureMessage("header " + call).IsTrue();
            AssertThat(Regex.IsMatch(natives, "\\{\"" + call + "\", &Native\\w+, 0\\}")).OverrideFailureMessage("natives " + call).IsTrue();
            AssertThat(commands.Contains("op == \"" + call + "\"", StringComparison.Ordinal)).OverrideFailureMessage("command " + call).IsTrue();
        }
        AssertThat(commands.Contains("what == \"locked_target\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("what == \"has_status\"", StringComparison.Ordinal)).IsTrue();

        // Every guest function the helpers call by TU1 address, with the disc address found by its
        // instruction pattern (same size on both builds).
        string rows = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc"));
        string[] expected =
        {
            "{0x82573220u, 0x82547EC8u},",  // SetAlpha
            "{0x82573078u, 0x82547D20u},",  // SetScale
            "{0x822B8188u, 0x822B7EA8u},",  // SaturationMask
            "{0x825725A8u, 0x82547250u},",  // SetAsDrawable
            "{0x8290F078u, 0x82455F10u},",  // SetForceTargetLock (the contract; the byte is stored directly)
            "{0x821E8B90u, 0x821E8870u},",  // the player handle's HasMode
            "{0x825C4958u, 0x824831E8u},",  // the hit record's constructor
            "{0x82275438u, 0x82275368u},",  // the hit's response-name setter
            "{0x823919C0u, 0x8237D608u},",  // the hit record's destructor
            "{0x827427B8u, 0x827178B8u},",  // remove CECElectrified
            "{0x82749F38u, 0x8271F038u},",  // remove CECInChaos
            "{0x827469A8u, 0x8271BAA8u},",  // remove CECOnFire
        };
        foreach (string row in expected)
        {
            AssertThat(rows.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage(row).IsTrue();
        }

        // Souls Combat's lock is the API's now, in Lua (mod/lockon.lua): no raw force-byte store of its own.
        string souls = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "Fable2", "mods-src", "souls_combat", "mod"), "*.lua").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText));
        AssertThat(souls.Contains("Refii.lock_target(", StringComparison.Ordinal)).IsTrue();
        AssertThat(souls.Contains("Refii.release_lock(", StringComparison.Ordinal)).IsTrue();
        AssertThat(souls.Contains("Refii.locked_target(", StringComparison.Ordinal)).IsTrue();
        AssertThat(souls.Contains("kForceLock", StringComparison.Ordinal)).IsFalse();
    }

    [TestCase]
    public void TheSoundAnimationAndOpinionSettersAreOnEverySurface()
    {
        // Part H's H.6 (sound through the game's own cue player and music box), H.9 (a named
        // animation as the shipped AI's scripted action) and H.10 (opinion and deeds through the
        // game's own setters) — native, Lua, Godot and the command lines each name every call, and
        // every guest function called by its TU1 address has its disc row.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string[] calls =
        {
            "play_sound", "play_sound_at", "stop_sound", "play_music", "stop_music",
            "play_animation", "stop_animation",
            "modify_opinion", "add_deed",
        };
        foreach (string call in calls)
        {
            string nativeName = call == "play_sound" ? "play_sound_on" : call;
            AssertThat(native.Contains("refii_mod_" + nativeName + "(", StringComparison.Ordinal)).OverrideFailureMessage("native " + call).IsTrue();
            AssertThat(lua.Contains("Refii." + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("lua " + call).IsTrue();
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(call))).OverrideFailureMessage("godot " + call).IsTrue();
        }
        // Both players are principals: the opinion setters take the hero, never assume player one.
        AssertThat(native.Contains("refii_mod_modify_opinion(ctx, base, villager, hero, axis, delta)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_add_deed(ctx, base, villager, hero, deed)", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        foreach (string call in calls)
        {
            string nativeName = call == "play_sound" ? "play_sound_on" : call;
            AssertThat(header.Contains("refii_mod_" + nativeName + "(PPCContext* ctx, uint8_t* base, ", StringComparison.Ordinal)).OverrideFailureMessage("header " + call).IsTrue();
            AssertThat(Regex.IsMatch(natives, "\\{\"" + call + "\", &Native\\w+, 0\\}")).OverrideFailureMessage("natives " + call).IsTrue();
            AssertThat(commands.Contains("op == \"" + call + "\"", StringComparison.Ordinal)).OverrideFailureMessage("command " + call).IsTrue();
        }

        // The opinion setter and the deed poster are the hooked names (the events fire); the sound
        // bodies are called by TU1 address, each with its disc row (same instruction sequence,
        // only the relocated globals differ).
        string api = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp")) + File.ReadAllText(Path.Combine(root, "src", "payload", "src", "core", "mod_requests.cpp")));
        AssertThat(api.Contains("rex_CECOpinionReaction_ModifyAxisValue(work, base);", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("refii::guest::Address(0x82279B80u)", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("Action.SetCurrentAction(e, a)", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("Action.FinishScriptedActions(e)", StringComparison.Ordinal)).IsTrue();
        string rows = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc"));
        string[] expected =
        {
            "{0x821D9608u, 0x821D9258u},",  // the cue player on a sound component (Sound.PlayEvent's body)
            "{0x821E0F48u, 0x821E0B08u},",  // the cue player on an emitter
            "{0x822671A8u, 0x82267168u},",  // the cue record's constructor
            "{0x82287DE0u, 0x822878C8u},",  // is the cue hash in a loaded bank
            "{0x82289A48u, 0x82289530u},",  // the cue record's destructor
            "{0x8255BB28u, 0x825204B0u},",  // SoundTools.PlayMusic
            "{0x8255BC20u, 0x825205A8u},",  // SoundTools.StopMusic
            "{0x829FDEF0u, 0x829F9128u},",  // stop an emitter's voices by tag
            "{0x829FD018u, 0x829F8250u},",  // the sound manager's emitter factory
            "{0x829FD168u, 0x829F83A0u},",  // the sound manager's emitter release
            "{0x82C05EF8u, 0x822ADE08u},",  // the voice handle's release
        };
        foreach (string row in expected)
        {
            AssertThat(rows.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage(row).IsTrue();
        }
    }

    [TestCase]
    public void GodotDeclaresASignalPerEvent()
    {
        AssertThat(string.Join(",", RefiiHost.SignalNames)).IsEqual(string.Join(",", ModApi.Events.Select(e => e.LuaName)));
    }

    [TestCase]
    public void TemplatesNameTheEvent()
    {
        ModApiEvent hit = ModApi.Find("hit")!;
        string native = ModApi.NativeTemplate(hit, "my_mod");
        AssertThat(native.Contains("REFII_MOD_ON(Hit, OnHit);")).IsTrue();
        AssertThat(native.Contains("refii::mods::HitEvent& e")).IsTrue();
        AssertThat(native.Contains("e.damage")).IsTrue();

        string lua = ModApi.LuaTemplate(hit);
        AssertThat(lua.StartsWith("Refii.on(\"hit\", function(e)")).IsTrue();
        AssertThat(lua.Contains("return true")).IsTrue();
        AssertThat(ModApi.LuaTemplate(ModApi.Find("Tick")!).Contains("return true")).IsFalse();
    }

    [TestCase]
    public void TheLightAndPartnerCallsAreOnEverySurface()
    {
        // Part H.11, H.12: a mod-placed light and the co-op message channel — one meaning each,
        // stated on native, Lua and Godot; the partner message is an event of the dense table.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature + " " + h.Meaning));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature + " " + h.Meaning));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature + " " + h.Meaning));
        foreach (string surface in new[] { native, lua, godot })
        {
            AssertThat(surface.Contains(ModApi.LightMeaning, StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.PartnerMeaning, StringComparison.Ordinal)).IsTrue();
        }

        AssertThat(native.Contains("refii_mod_light(entity, xyz, rgb, energy, range, options)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_send_to_partner(mod, name, payload)", StringComparison.Ordinal)).IsTrue();
        // H.16: the spot form is one meaning with the omni's, stated on every surface's options.
        AssertThat(ModApi.LightMeaning.Contains("A SPOT when a cone is stated", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("direction, cone, attenuation for a spot", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("direction = {x, y, z}, cone = degrees, attenuation = n", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("options { offset, at, flicker, direction, cone, attenuation }", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.light(target, color, energy, range [, o])", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.send_to_partner(mod, name, payload)", StringComparison.Ordinal)).IsTrue();
        foreach (string name in new[] { "light", "light_remove", "lights", "send_to_partner" })
        {
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(name)))
                .OverrideFailureMessage($"'{name}' is not a Godot helper of the catalog").IsTrue();
        }

        ModApiEvent partner = ModApi.Find("partner_message")!;
        AssertThat(partner.Kind).IsEqual(93);
        AssertThat(string.Join(",", partner.Fields.Select(f => f.Name))).IsEqual("mod,name,payload,from");
        AssertThat(partner.Cancellable).IsFalse();
        AssertThat(partner.IsRelayed).IsFalse();  // the route carries it; it is the receiver's own event, not a replica

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        foreach (string declared in new[]
                 {
                     "extern \"C\" uint32_t refii_mod_light(uint32_t entity, const float* xyz, const float* rgb, float energy, float range,",
                     "extern \"C\" void refii_mod_light_remove(uint32_t id);",
                     // The spot fields trail the options (a native mod is rebuilt from source, so a trailing field is safe).
                     "  float direction[3];\n  float cone;\n",
                     "  float attenuation;\n} refii_mod_light_options;",
                     "extern \"C\" int32_t refii_mod_send_to_partner(const char* mod, const char* name, const char* payload);",
                     "#define REFII_MOD_PARTNER_PAYLOAD_MAX 256",
                 })
        {
            AssertThat(header.Contains(declared, StringComparison.Ordinal)).OverrideFailureMessage($"{declared} is not in core/refii_mod.h").IsTrue();
        }

        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        foreach (string row in new[] { "{\"_light\", &NativeLight, 0}", "{\"light_remove\", &NativeLightRemove, 0}", "{\"send_to_partner\", &NativeSendToPartner, 0}" })
        {
            AssertThat(natives.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage($"{row} is not registered").IsTrue();
        }

        string preamble = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs"));
        // The Lua form carries the spot fields to the native on both branches (handle and point):
        // nineteen arguments, direction then cone then attenuation after flicker.
        AssertThat(preamble.Contains("\"\\tlocal d = o.direction or {}\\n\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(Regex.Matches(preamble, @"o\.flicker or 0,\\n"" \+\s+""(\\t)+d\[1\] or d\.x or 0, d\[2\] or d\.y or 0, d\[3\] or d\.z or 0, o\.cone or 0, o\.attenuation or 0\)").Count).IsEqual(2);
        AssertThat(natives.Contains("options.direction[0] = static_cast<float>(lua.Number(15));", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("options.cone = static_cast<float>(lua.Number(18));", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("options.attenuation = static_cast<float>(lua.Number(19));", StringComparison.Ordinal)).IsTrue();
        AssertThat(preamble.Contains("function Refii.light(target, color, energy, range, o)", StringComparison.Ordinal)).IsTrue();
        string api = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp")) + File.ReadAllText(Path.Combine(root, "src", "payload", "src", "core", "mod_requests.cpp")));
        foreach (string op in new[] { "op='light'", "op='light_off'", "dx=%.9g,dy=%.9g,dz=%.9g,cone=%.9g,atten=%.9g", "\"pm:\" + std::to_string(owned)" })
        {
            AssertThat(api.Contains(op, StringComparison.Ordinal)).OverrideFailureMessage($"{op} is never posted").IsTrue();
        }

        // The route's mod lane: the receiver keeps pm: lines apart and the bus drains them on the tick.
        string relay = File.ReadAllText(Path.Combine(root, "src", "runtime", "src", "core", "event_relay.cpp"));
        AssertThat(relay.Contains("constexpr std::string_view kModLane = \"pm:\";", StringComparison.Ordinal)).IsTrue();
        AssertThat(relay.Contains("extern \"C\" int32_t refii_bridge_route_take_mod(char* out, uint32_t capacity)", StringComparison.Ordinal)).IsTrue();
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        AssertThat(bus.Contains("DrainPartnerMessages(ctx, base);", StringComparison.Ordinal)).IsTrue();
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("op == \"partner_send\"", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheQuickActionsAreOnEverySurface()
    {
        // The quick menu's actions (use_item, equip_weapon, perform_expression, assign_spell), the reads
        // they choose from (expressions, spells_known, selected_expression) and record_string: native,
        // Lua and Godot each name every call, by PLAYER — never a player-one default.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        string[] actions = { "use_item", "equip_weapon", "perform_expression", "assign_spell" };
        foreach (string call in actions)
        {
            AssertThat(native.Contains("refii_mod_" + call + "(ctx, base, player, ", StringComparison.Ordinal)).OverrideFailureMessage("native " + call).IsTrue();
            AssertThat(lua.Contains("Refii." + call + "(p, ", StringComparison.Ordinal)).OverrideFailureMessage("lua " + call).IsTrue();
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(call))).OverrideFailureMessage("godot " + call).IsTrue();
        }
        string[] reads = { "expressions", "spells_known", "selected_expression" };
        foreach (string read in reads)
        {
            AssertThat(lua.Contains("Refii." + read + "(p)", StringComparison.Ordinal)).OverrideFailureMessage("lua " + read).IsTrue();
            AssertThat(Regex.IsMatch(godot, @"query [^\n]*\b" + read + @" P\b")).OverrideFailureMessage("godot " + read).IsTrue();
        }
        AssertThat(native.Contains("refii_mod_expression(base, player, index, uint32_t* record, int32_t* uses)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_spells_known(base, player, uint32_t* mask)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_selected_expression(base, player)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_record_string(ctx, base, definition, field, out, size)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.record_string(def, field)", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("record_string DEF FIELD", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_record_int(ctx, base, definition, field, int32_t* out)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.record_int(def, field)", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("record_int DEF FIELD", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        string host = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs"));
        foreach (string call in actions)
        {
            AssertThat(header.Contains("refii_mod_" + call + "(PPCContext* ctx, uint8_t* base, int32_t player, ", StringComparison.Ordinal)).OverrideFailureMessage("header " + call).IsTrue();
            AssertThat(Regex.IsMatch(natives, "\\{\"" + call + "\", &Native\\w+, 0\\}")).OverrideFailureMessage("natives " + call).IsTrue();
            AssertThat(commands.Contains("op == \"" + call + "\"", StringComparison.Ordinal)).OverrideFailureMessage("command " + call).IsTrue();
            AssertThat(Regex.IsMatch(host, @"public void " + call + @"\(int player, ")).OverrideFailureMessage("host " + call).IsTrue();
        }
        foreach (string read in reads.Append("record_string").Append("record_int"))
        {
            AssertThat(Regex.IsMatch(natives, "\\{\"" + read + "\", &Native\\w+, 0\\}")).OverrideFailureMessage("natives " + read).IsTrue();
            AssertThat(commands.Contains("what == \"" + read + "\"", StringComparison.Ordinal)).OverrideFailureMessage("query " + read).IsTrue();
        }
        AssertThat(header.Contains("refii_mod_expression(uint8_t* base, int32_t player, int32_t index, uint32_t* out_record,", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("refii_mod_spells_known(uint8_t* base, int32_t player, uint32_t* out_mask)", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("refii_mod_selected_expression(uint8_t* base, int32_t player)", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("refii_mod_record_string(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,", StringComparison.Ordinal)).IsTrue();

        // One body: the pump's PerformQuickslot is the quick slot's and the API's alike, and the
        // joiner's weapon pick crosses to the co-op kit under its own host kind.
        string pump = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "guest_tick_pump.cpp"));
        string api = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp")) + File.ReadAllText(Path.Combine(root, "src", "payload", "src", "core", "mod_requests.cpp")));
        string events = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h"));
        string kit = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "host", "CoopHeroKit.cs"));
        AssertThat(pump.Contains("bool PerformQuickslot(PPCContext& ctx, uint8_t* base, uint32_t player, uint32_t kind, uint32_t record, uint32_t flags)", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("refii::vfs::PerformQuickslot(work, base,", StringComparison.Ordinal)).IsTrue();
        AssertThat(events.Contains("#define REFII_MOD_HOST_HERO_KIND 1005", StringComparison.Ordinal)).IsTrue();
        AssertThat(pump.Contains("refii::mods::PostEvent(REFII_MOD_HOST_HERO_KIND, fields)", StringComparison.Ordinal)).IsTrue();
        AssertThat(kit.Contains("public const int PickKind = 1005;", StringComparison.Ordinal)).IsTrue();
        // The shell hands the title its own bus lines; Fable 2 routes the pick into its co-op kit.
        string title = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "Fable2Title.cs"));
        AssertThat(host.Contains("Engine_TitleGame.Active.HandleBusLine(kind, fields)", StringComparison.Ordinal)).IsTrue();
        AssertThat(title.Contains("CoopHeroKit.HandlePick(fields)", StringComparison.Ordinal)).IsTrue();

        // Every guest function called by TU1 address has its disc row.
        string rows = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc"));
        foreach (string row in new[] { "{0x824F1C90u, 0x8261B978u},", "{0x823E0FF8u, 0x823BF9C0u},", "{0x82764DC0u, 0x82741BD8u},", "{0x82323540u, 0x82345978u},", "{0x82A006C8u, 0x829FB978u},", "{0x83496B08u, 0x834968A4u},", "{0x83496DA0u, 0x83496BD0u}," })
        {
            AssertThat(rows.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage(row).IsTrue();
        }
    }

    [TestCase]
    public void TheWheelSwitchedLockIsOnEverySurface()
    {
        // H.16: Refii.wheel_switches_target restates itself each Lua tick; a Godot script runs off
        // the tick, so RefiiHost.wheel_switches_target(handle, on) is a STICKY command line the tick
        // restates (mod_commands.cpp RestateWheelSwitches) until off or the lock is released.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        AssertThat(native.Contains("refii_mod_wheel_switches_target(base, hero)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.wheel_switches_target(e)", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains("wheel_switches_target"))).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("op == \"wheel_switches_target\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("RestateWheelSwitches(work, base);", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("refii_mod_locked_target(&ctx, base, player) == 0u", StringComparison.Ordinal)).IsTrue();
        string host = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs"));
        AssertThat(host.Contains("Post($\"wheel_switches_target {handle} {(on ? 1 : 0)}\")", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheExamplesGoNativeFree()
    {
        // Part H.15: what the two example mods still did by raw memory or by a hook of their own
        // is a primitive or an event on every surface — the speed override, the action
        // commitment, the look-at button, the slot table, a live cast's parts and its lifecycle.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        foreach (string call in new[] { "set_speed_multiplier", "clear_speed_multiplier", "allow_action_interrupt" })
        {
            AssertThat(native.Contains("refii_mod_" + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("native " + call).IsTrue();
            AssertThat(lua.Contains("Refii." + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("lua " + call).IsTrue();
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(call))).OverrideFailureMessage("godot " + call).IsTrue();
        }
        foreach (string read in new[]
                 {
                     "refii_mod_speed_multiplier(base, player, float* out)", "refii_mod_current_action(base, entity)",
                     "refii_mod_action_elapsed(ctx, base, action, float* out)", "refii_mod_action_interruptible(base, action)",
                     "refii_mod_entity_living(base, entity)", "refii_mod_spell_in_slot(base, player, slot)",
                     "refii_mod_spell_parts(ctx, base, serial, refii_mod_cast_parts* out)",
                 })
        {
            AssertThat(native.Contains(read, StringComparison.Ordinal)).OverrideFailureMessage("native " + read).IsTrue();
        }
        foreach (string read in new[]
                 {
                     "Refii.speed_multiplier(p) -> overridden, factor", "Refii.current_action(e) -> action", "Refii.action_elapsed(a) -> seconds",
                     "Refii.action_interruptible(a)", "Refii.living(e)", "Refii.spell_in_slot(p, slot) -> type", "Refii.spell_parts(serial) -> table",
                 })
        {
            AssertThat(lua.Contains(read, StringComparison.Ordinal)).OverrideFailureMessage("lua " + read).IsTrue();
        }
        foreach (string query in new[] { "query speed_multiplier P / current_action E", "query living E / spell_in_slot P SLOT", "query spell_parts SERIAL" })
        {
            AssertThat(godot.Contains(query, StringComparison.Ordinal)).OverrideFailureMessage("godot " + query).IsTrue();
        }

        ModApiEvent lookAt = ModApi.Find("look_at_button")!;
        AssertThat(lookAt.Kind).IsEqual(94);
        AssertThat(string.Join(",", lookAt.Fields.Select(f => f.Name))).IsEqual("hero,player,down,component");
        AssertThat(lookAt.Fields.Single(f => f.Name == "down").Writable).IsTrue();
        AssertThat(lookAt.Cancellable).IsTrue();
        ModApiEvent started = ModApi.Find("vortex_started")!;
        AssertThat(started.Kind).IsEqual(95);
        AssertThat(string.Join(",", started.Fields.Select(f => f.Name))).IsEqual("vortex,entity,caster,caster_player,target,serial,level,x,y,z,tag,damage,lifts");
        AssertThat(started.Cancellable).IsFalse();
        ModApiEvent ended = ModApi.Find("vortex_ended")!;
        AssertThat(ended.Kind).IsEqual(96);
        AssertThat(string.Join(",", ended.Fields.Select(f => f.Name))).IsEqual("vortex,entity,caster,caster_player,serial,x,y,z");
        ModApiEvent impact = ModApi.Find("fireball_impact")!;
        AssertThat(impact.Kind).IsEqual(97);
        AssertThat(string.Join(",", impact.Fields.Select(f => f.Name))).IsEqual("spell,serial,level,caster,caster_player,struck,struck_player,x,y,z,nx,ny,nz,detonated,hits,tag");
        AssertThat(impact.Cancellable).IsFalse();
        AssertThat(ModApi.Events.Count).IsEqual(164);  // 98 paused, 99 start_game (H.16), 100 cutscene, 101..104 Part I wave B, 105 attack_mode_changed, 106..111 Part I wave C, 112 camera_changed, 113 vortex_caught, 114 appearance_changed, 115 morph_changed, 116..118 Fable 3 (guild_seals, treasury, kingdom_decision), 119..125 shared (chest, shop, dig, demon door, drink), 126..163 wave 3 (Sanctuary, weapons, social, dog, jobs, guild, titles, scars, age, clothing, pub games, wealth, gargoyles)

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        foreach (string declared in new[]
                 {
                     "extern \"C\" int32_t refii_mod_set_speed_multiplier(uint8_t* base, int32_t player, float factor);",
                     "extern \"C\" int32_t refii_mod_clear_speed_multiplier(uint8_t* base, int32_t player);",
                     "extern \"C\" int32_t refii_mod_speed_multiplier(uint8_t* base, int32_t player, float* out);",
                     "extern \"C\" uint32_t refii_mod_current_action(uint8_t* base, uint32_t entity);",
                     "extern \"C\" int32_t refii_mod_action_elapsed(PPCContext* ctx, uint8_t* base, uint32_t action, float* out);",
                     "extern \"C\" int32_t refii_mod_action_interruptible(uint8_t* base, uint32_t action);",
                     "extern \"C\" int32_t refii_mod_allow_action_interrupt(uint8_t* base, uint32_t action);",
                     "extern \"C\" int32_t refii_mod_entity_living(uint8_t* base, uint32_t entity);",
                     "extern \"C\" int32_t refii_mod_spell_in_slot(uint8_t* base, int32_t player, int32_t slot);",
                     "struct refii_mod_cast_parts {",
                     "extern \"C\" int32_t refii_mod_spell_parts(PPCContext* ctx, uint8_t* base, uint32_t serial, refii_mod_cast_parts* out);",
                 })
        {
            AssertThat(header.Contains(declared, StringComparison.Ordinal)).OverrideFailureMessage($"{declared} is not in core/refii_mod.h").IsTrue();
        }

        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        foreach (string name in new[]
                 {
                     "set_speed_multiplier", "clear_speed_multiplier", "speed_multiplier", "current_action", "action_elapsed",
                     "action_interruptible", "allow_action_interrupt", "living", "spell_in_slot", "spell_parts",
                 })
        {
            AssertThat(Regex.IsMatch(natives, "\\{\"" + name + "\", &Native\\w+, 0\\}")).OverrideFailureMessage("natives " + name).IsTrue();
        }
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        foreach (string op in new[] { "set_speed_multiplier", "clear_speed_multiplier", "allow_action_interrupt" })
        {
            AssertThat(commands.Contains("op == \"" + op + "\"", StringComparison.Ordinal)).OverrideFailureMessage("command " + op).IsTrue();
        }
        foreach (string what in new[] { "speed_multiplier", "current_action", "living", "spell_in_slot", "spell_parts" })
        {
            AssertThat(commands.Contains("what == \"" + what + "\"", StringComparison.Ordinal)).OverrideFailureMessage("query " + what).IsTrue();
        }

        // The four seams are refii's now: hooked once in the bus, the mods subscribe.
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        foreach (string seam in new[] { "rex_CPlayer_SetLookAtButtonDown", "rex_VortexComponent_Init", "rex_VortexComponent_Shutdown", "rex_TargetedFireball_Impact" })
        {
            AssertThat(bus.Contains("REX_EXTERN(" + seam + ")", StringComparison.Ordinal)).OverrideFailureMessage("bus " + seam).IsTrue();
        }
        AssertThat(bus.Contains("extern \"C\" int32_t refii_mod_spell_parts(", StringComparison.Ordinal)).IsTrue();
        // Souls Combat is pure Lua now (SoulsCombatLuaTests): the look-at seam reaches it as an event.
        AssertThat(Directory.Exists(Path.Combine(root, "Fable2", "mods-src", "souls_combat", "native"))).IsFalse();
        // Souls Combat's Lua is one file per rule (mod/*.lua, composed through require): read as one.
        string souls = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "Fable2", "mods-src", "souls_combat", "mod"), "*.lua").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText));
        AssertThat(souls.Contains("Refii.on(\"look_at_button\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(souls.Contains("REX_EXTERN(", StringComparison.Ordinal)).IsFalse();
        // Spell Weaving is pure Lua now (SpellWeavingLuaTests): the three seams reach it as events.
        AssertThat(Directory.Exists(Path.Combine(root, "Fable2", "mods-src", "spell_weaving", "native"))).IsFalse();
        string weaving = File.ReadAllText(Path.Combine(root, "Fable2", "mods-src", "spell_weaving", "mod", "init.lua"));
        foreach (string on in new[] { "Refii.on(\"vortex_started\"", "Refii.on(\"vortex_ended\"", "Refii.on(\"fireball_impact\"", "Refii.spell_parts(" })
        {
            AssertThat(weaving.Contains(on, StringComparison.Ordinal)).OverrideFailureMessage(on).IsTrue();
        }

        // Every guest function the helpers cite as the contract, with the disc address found by
        // its instruction pattern (same size on both builds).
        string rows = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc"));
        foreach (string row in new[]
                 {
                     "{0x823DFF78u, 0x823BE7D0u},",  // Player.SetPlayerMovementSpeedOverridden (stb r4,0x1E(r3))
                     "{0x8283ECC8u, 0x827EDB38u},",  // Player.SetOverriddenMovementSpeedMultiplier (stfs f1,0xCC(r3))
                     "{0x823E3258u, 0x823C1C20u},",  // Player.GetOverriddenMovementSpeedMultiplier
                     "{0x822B1F78u, 0x822B1DB0u},",  // the locomotion update
                     "{0x82709938u, 0x826DF198u},",  // the action's animation-event handler
                     "{0x822CB150u, 0x822CB140u},",  // the interrupt rule
                     "{0x822A4888u, 0x822A4648u},",  // the manager's start (current action -> +0x10)
                     "{0x82709FD8u, 0x826DF838u},",  // the action's Stop
                     "{0x8226D418u, 0x8226D3D8u},",  // the action's clock, slot 7
                     "{0x823A3148u, 0x82431700u},",  // CECCreature constructor
                     "{0x82844950u, 0x8277B2B0u},",  // the target selector
                     "{0x82740568u, 0x8294A2F8u},",  // the fireball's shot getter
                     "{0x825A2188u, 0x82552F70u},",  // Blades targeted Start
                     "{0x825A2720u, 0x82553508u},",  // Blades area Start
                     "{0x825A37E0u, 0x825545C8u},",  // Blades record add
                     "{0x82759928u, 0x82731308u},",  // the vortex update (twister point)
                     "{0x828E5020u, 0x828BE468u},",  // the spell base constructor
                 })
        {
            AssertThat(rows.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage(row).IsTrue();
        }
    }

    [TestCase]
    public void TheHudCallsAreOnEverySurface()
    {
        // Part H.2, H.4, H.5: a play at a model helper, world markers, the per-player fade and the
        // HUD meters — one meaning each, stated on native, Lua and Godot.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature + " " + h.Meaning));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature + " " + h.Meaning));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature + " " + h.Meaning));
        foreach (string surface in new[] { native, lua, godot })
        {
            AssertThat(surface.Contains("at (a model helper", StringComparison.Ordinal) || surface.Contains("at = \"Weapon.Weapon.Top\"", StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.MarkMeaning, StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.FadeMeaning, StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.MeterMeaning, StringComparison.Ordinal)).IsTrue();
        }

        AssertThat(native.Contains("refii_mod_mark(player, entity, xyz, icon, text)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_fade(player, to_black, seconds)", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_meter(player, id, label, value, max, style)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.mark(target, icon, text [, p])", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.fade(p, dir [, seconds])", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.meter(id, label, value, max [, p [, style]])", StringComparison.Ordinal)).IsTrue();
        foreach (string name in new[] { "mark", "unmark", "markers", "fade", "fade_state", "meter", "meter_remove", "meters" })
        {
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(name)))
                .OverrideFailureMessage($"'{name}' is not a Godot helper of the catalog").IsTrue();
        }

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        foreach (string declared in new[]
                 {
                     "const char* at;",
                     "extern \"C\" uint32_t refii_mod_play_fx_at(const char* name, uint32_t entity, const char* helper,",
                     "extern \"C\" uint32_t refii_mod_mark(int32_t player, uint32_t entity, const float* xyz, const char* icon,",
                     "extern \"C\" void refii_mod_unmark(uint32_t id);",
                     "extern \"C\" int32_t refii_mod_fade(int32_t player, int32_t to_black, float seconds);",
                     "extern \"C\" int32_t refii_mod_meter(int32_t player, const char* id, const char* label, float value, float max,",
                     "extern \"C\" int32_t refii_mod_meter_remove(const char* id);",
                 })
        {
            AssertThat(header.Contains(declared, StringComparison.Ordinal)).OverrideFailureMessage($"{declared} is not in core/refii_mod.h").IsTrue();
        }

        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        foreach (string row in new[] { "{\"_mark\", &NativeMark, 0}", "{\"unmark\", &NativeUnmark, 0}", "{\"fade\", &NativeFade, 0}", "{\"meter\", &NativeMeter, 0}", "{\"meter_remove\", &NativeMeterRemove, 0}" })
        {
            AssertThat(natives.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage($"{row} is not registered").IsTrue();
        }

        // The Lua preamble unpacks `at` as the sixteenth _play_fx argument and builds mark from a handle or a point.
        string preamble = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs"));
        AssertThat(preamble.Contains("f[3] or f.z or 0, o.at or ''", StringComparison.Ordinal)).IsTrue();
        AssertThat(preamble.Contains("function Refii.mark(target, icon, text, p, o)", StringComparison.Ordinal)).IsTrue();
        string api = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp")) + File.ReadAllText(Path.Combine(root, "src", "payload", "src", "core", "mod_requests.cpp")));
        AssertThat(api.Contains(",at='%s'\"", StringComparison.Ordinal)).IsTrue();
        foreach (string op in new[] { "op='mark'", "op='unmark'", "op='fade'", "op='meter'", "op='meter_remove'" })
        {
            AssertThat(api.Contains(op, StringComparison.Ordinal)).OverrideFailureMessage($"{op} is never posted").IsTrue();
        }
    }

    [TestCase]
    public void TheNpcTimeAndWeatherCallsAreOnEverySurface()
    {
        // Part H's H.13 (NPC orders through the quests' own entries in the game VM) and H.14 (the
        // game clock's own setter, the quests' environment-theme blend) — native, Lua, Godot and
        // the command lines each name every call, one meaning per item on every surface, the read
        // backs as a Lua native and a Godot query, and the one guest function called by its TU1
        // address has its disc row.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature + " " + h.Meaning));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature + " " + h.Meaning));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature + " " + h.Meaning));
        string[] calls =
        {
            "npc_follow", "npc_go_to", "npc_flee", "npc_attack", "npc_set_hostile", "npc_stop",
            "set_time_of_day", "set_weather",
        };
        foreach (string call in calls)
        {
            AssertThat(native.Contains("refii_mod_" + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("native " + call).IsTrue();
            AssertThat(lua.Contains("Refii." + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("lua " + call).IsTrue();
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(call))).OverrideFailureMessage("godot " + call).IsTrue();
        }
        foreach (string surface in new[] { native, lua, godot })
        {
            AssertThat(surface.Contains(ModApi.NpcMeaning, StringComparison.Ordinal)).IsTrue();
            AssertThat(surface.Contains(ModApi.TimeWeatherMeaning, StringComparison.Ordinal)).IsTrue();
        }
        AssertThat(lua.Contains("Refii.time_of_day() -> hours", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("query time_of_day", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_time_of_day(base, hours)", StringComparison.Ordinal)).IsTrue();
        // Both players are principals: hostility is against an entity (a hero is its own), never a player number.
        AssertThat(native.Contains("refii_mod_npc_set_hostile(ctx, base, entity, other, on)", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        foreach (string call in calls)
        {
            AssertThat(header.Contains("refii_mod_" + call + "(PPCContext* ctx, uint8_t* base, ", StringComparison.Ordinal)).OverrideFailureMessage("header " + call).IsTrue();
            AssertThat(Regex.IsMatch(natives, "\\{\"" + call + "\", &Native\\w+, 0\\}")).OverrideFailureMessage("natives " + call).IsTrue();
            AssertThat(commands.Contains("op == \"" + call + "\"", StringComparison.Ordinal)).OverrideFailureMessage("command " + call).IsTrue();
        }
        AssertThat(header.Contains("refii_mod_time_of_day(uint8_t* base, float* hours)", StringComparison.Ordinal)).IsTrue();
        AssertThat(Regex.IsMatch(natives, "\\{\"time_of_day\", &NativeTimeOfDay, 0\\}")).IsTrue();
        AssertThat(commands.Contains("what == \"time_of_day\"", StringComparison.Ordinal)).IsTrue();

        // The orders are the shipped scripts' own calls, run in the game VM; the clock's setter is
        // called by TU1 address with its disc row.
        string api = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp")) + File.ReadAllText(Path.Combine(root, "src", "payload", "src", "core", "mod_requests.cpp")));
        foreach (string entry in new[]
                 {
                     "Follow.FollowEntity(e, t, %.3f)",
                     "ScriptFunction.StartScriptControlledMovement(e, CVector3(%.3f, %.3f, %.3f), false)",
                     "Navigation.FleeFromEntity(e, t, ENavigationSpeed.NAV_SPEED_SPRINT, true)",
                     "CombatRegister:SetScriptTarget(e, t, true, false)",
                     "Faction.AddTemporaryEntityRelationship(e, t, EFactionStatus.FACTION_STATUS_ENEMY)",
                     "Faction.RemoveTemporaryEntityRelationship(e, t)",
                     "Follow.StopFollowing(e)",
                     "ScriptFunction.StopScriptControlledMovement(e) CombatRegister:ClearScriptTarget(e)",
                     "EnvironmentTheme.BlendToEnvironmentTheme(\\\"%s\\\", %.4f, %.4f)",
                     "refii::guest::Address(kSetTimeOfDayAbsolute)",
                 })
        {
            AssertThat(api.Contains(entry, StringComparison.Ordinal)).OverrideFailureMessage(entry).IsTrue();
        }
        string rows = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc"));
        AssertThat(rows.Contains("{0x82515AA0u, 0x8249A790u},", StringComparison.Ordinal)).IsTrue();  // Timing.SetTimeOfDayAbsolute's body
    }

    [TestCase]
    public void TheLuaSurfaceMatchesNativeForTheExamples()
    {
        // The seven places the two pure-Lua ports stopped at (hard_mods_plan H.15): each is closed in
        // core, on every surface, and both mods' Lua stands on it.
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature + " " + h.Meaning));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        // 1 + 2: a position cast from Lua and Godot, and the cast's serial.
        AssertThat(lua.Contains("Refii.cast_spell(caster, type, targeted, target, level [, pos [, dir [, options]]]) -> spell, serial", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.spell_serial(spell) -> serial", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains("cast_spell_at"))).IsTrue();
        AssertThat(godot.Contains("cast_spell_at(caster, type, targeted, target, level, pos, dir)", StringComparison.Ordinal)).IsTrue();
        // 2: events raised inside a running chunk are deferred, not dropped.
        AssertThat(ModApi.LuaHelpers.Single(h => h.Signature.StartsWith("Refii.on(", StringComparison.Ordinal)).Meaning.Contains("e.deferred = true", StringComparison.Ordinal)).IsTrue();
        // 5: the action handle on both events, no longer native-only.
        foreach (string name in new[] { "melee_swing", "anim_event" })
        {
            ModApiField action = ModApi.Find(name)!.Fields.Single(f => f.Name == "action");
            AssertThat(action.Meaning.Contains("(native", StringComparison.Ordinal)).OverrideFailureMessage(name + " action is still native-only").IsFalse();
            AssertThat(action.Meaning.Contains("handle", StringComparison.Ordinal)).IsTrue();
        }
        // 3 + 7 + 4: the charge level every frame, the tick every guest tick.
        AssertThat(ModApi.Find("spell_charge_level")!.Summary.Contains("every surface alike", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModApi.Find("spell_charge_level")!.Summary.Contains("verdict holds between", StringComparison.Ordinal)).IsFalse();
        AssertThat(ModApi.Find("tick")!.Summary.Contains("every guest tick", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModApi.Find("tick")!.Summary.Contains("ten times", StringComparison.Ordinal)).IsFalse();
        // 6: the wheel statement cannot lapse under that cadence.
        AssertThat(ModApi.LuaHelpers.Single(h => h.Signature.Contains("Refii.wheel_switches_target(e)", StringComparison.Ordinal)).Meaning.Contains("never lapses", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        // The preamble's wrapper (read from the source: the composed preamble reads mod settings
        // through Godot's FileAccess, a native crash in the test host).
        string preamble = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs"));
        AssertThat(preamble.Contains("function Refii.cast_spell(caster, spell_type, targeted, target, level, pos, dir, options)", StringComparison.Ordinal)).IsTrue();
        AssertThat(preamble.Contains("return Refii._cast_spell(caster, spell_type, t, target or 0, level or 0, pos ~= nil and 1 or 0,", StringComparison.Ordinal)).IsTrue();
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        AssertThat(natives.Contains("{\"_cast_spell\", &NativeCastSpell, 0}", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("{\"spell_serial\", &NativeSpellSerial, 0}", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("{\"cast_spell\", &NativeCastSpell, 0}", StringComparison.Ordinal)).IsFalse();
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("w.size() >= 9 ? pos : nullptr", StringComparison.Ordinal)).IsTrue();
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        AssertThat(bus.Contains("kLuaTickPeriod", StringComparison.Ordinal)).OverrideFailureMessage("the Lua tick is throttled again").IsFalse();
        AssertThat(bus.Contains("last_verdict", StringComparison.Ordinal)).OverrideFailureMessage("the charge-level memo is back").IsFalse();
        AssertThat(bus.Contains("struct LuaDeferred", StringComparison.Ordinal)).IsTrue();
        // 2, the live half: a writable event raised inside a chunk nests once, live; the cancel is
        // per depth; the drain waits for the outermost chunk; the gate lists exactly the kinds a
        // handler can write back to.
        AssertThat(bus.Contains("constexpr int kLuaMaxDepth = 2;", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("if (g_lua_depth >= kLuaMaxDepth) {", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("const bool nested_live = g_lua_depth > 0 && HasWritableFields(kind) && g_lua_depth < kLuaMaxDepth;", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("const bool outer_cancelled = g_lua_cancelled;", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("g_lua_cancelled = outer_cancelled;", StringComparison.Ordinal)).IsTrue();
        AssertThat(Regex.IsMatch(bus, @"if \(g_lua_depth == 0\) \{\s*DrainLuaDeferred\(ctx, base\);")).IsTrue();
        AssertThat(bus.Contains("g_lua_running", StringComparison.Ordinal)).IsFalse();
        string gate = Regex.Match(bus, @"bool HasWritableFields\(int32_t kind\) \{.*?\n\}", RegexOptions.Singleline).Value;
        string writer = Regex.Match(bus, @"bool WriteEventField\(int32_t kind, void\* event, const char\* field, double value\) \{.*?\n\}", RegexOptions.Singleline).Value;
        AssertThat(gate.Length > 0 && writer.Length > 0).IsTrue();
        string[] gated = Regex.Matches(gate, @"case (REFII_EVENT_\w+):").Select(m => m.Groups[1].Value).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        string[] written = Regex.Matches(writer, @"case (REFII_EVENT_\w+):").Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
        AssertThat(gated).IsEqual(written);
        // ... and those are the catalog's writable kinds (by the header's numbering).
        string header = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h"));
        var kinds = Regex.Matches(header, @"(REFII_EVENT_\w+) = (\d+),").ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value));
        int[] gatedKinds = gated.Select(n => kinds[n]).OrderBy(k => k).ToArray();
        int[] catalogKinds = ModApi.WritableFields.Select(w => w.Kind).Distinct().OrderBy(k => k).ToArray();
        AssertThat(gatedKinds).IsEqual(catalogKinds);
        AssertThat(bus.Contains("\"entity=%u,player=%d,action=%u,action_type=%d,action_name='%s',strike=%d,flourish=%s\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("name='%s',action=%u,action_type=%d,action_name='%s'\"", StringComparison.Ordinal)).IsTrue();
        string mouse = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mouse_look.cpp"));
        AssertThat(mouse.Contains("constexpr uint32_t kWheelStatementTicks = 2u;", StringComparison.Ordinal)).IsTrue();
        // Both mods stand on the closed items.
        string weaving = File.ReadAllText(Path.Combine(root, "Fable2", "mods-src", "spell_weaving", "mod", "init.lua"));
        AssertThat(weaving.Contains("local spell, serial = Refii.cast_spell(hero, cast.type, true, 0, cast.level, cast.origin, cast.facing,", StringComparison.Ordinal)).IsTrue();
        AssertThat(weaving.Contains("spell_parts(serial).spell", StringComparison.Ordinal)).IsFalse();
        AssertThat(weaving.Contains("partner_casts", StringComparison.Ordinal)).IsFalse();
        // Part I: the charge's end is spell_charge_ended, not a staleness timer.
        AssertThat(weaving.Contains("kChargeHoldSeconds", StringComparison.Ordinal)).IsFalse();
        AssertThat(weaving.Contains("Refii.on(\"spell_charge_ended\"", StringComparison.Ordinal)).IsTrue();
        // Part I: the partner's hits, the bolt inside its own factory call included, are scaled by the
        // cast's damage_scale where the hit is decided; no in-flight cast record matched by type and caster.
        AssertThat(weaving.Contains("{ at = at, tag = cast.tag, damage_scale = Knob(\"weave_damage\", 0.7) }", StringComparison.Ordinal)).IsTrue();
        AssertThat(weaving.Contains("casting = {", StringComparison.Ordinal)).IsFalse();
        string souls = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "Fable2", "mods-src", "souls_combat", "mod"), "*.lua").OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText));
        AssertThat(souls.Contains("S.Commit[p] = { action = e.action }", StringComparison.Ordinal)).IsTrue();
        AssertThat(souls.Contains("if commit == nil or e.action ~= commit.action then", StringComparison.Ordinal)).IsTrue();
        AssertThat(souls.Contains("Refii.allow_action_interrupt_at(commit.action, commit.at)", StringComparison.Ordinal)).IsTrue();
        AssertThat(souls.Contains("ten times a second", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// cast_spell's `at` (Part I): the core places a cast at an entity or a point by the spell
    /// class's own Start, on every surface, and a type with no rule for the place is refused with
    /// a mod error rather than guessed. The rules are the binary's: Lightning on its bolt's end
    /// (sub_825FEFC8 with the power's CreateImpactFX, read by the class's own data read) or one 15.0
    /// reach above a point (outside the 1.35 re-acquire height), Fireball within its 1.0
    /// immediate impact, Vortex set back its 1.25 spawn, Chaos on its stream's end, Force Push from
    /// the origin.
    /// </summary>
    [TestCase]
    public void CastSpellAtPlacesByTheClassesOwnRule()
    {
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature + " " + h.Meaning));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature + " " + h.Meaning));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature + " " + h.Meaning));
        AssertThat(native.Contains("{ tag, damage_scale, at_kind, at_entity, at_point }", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("at = e or {x, y, z}", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("[at E | at X Y Z]", StringComparison.Ordinal)).IsTrue();
        foreach (string surface in new[] { native, lua, godot })
        {
            AssertThat(surface.Contains("Fireball and Chaos at a point, and Slow Time and Raise Dead, are refused with a mod error", StringComparison.Ordinal)).IsTrue();
        }

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        AssertThat(header.Contains("#define REFII_MOD_CAST_AT_ENTITY 1", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("#define REFII_MOD_CAST_AT_POINT 2", StringComparison.Ordinal)).IsTrue();
        AssertThat(Regex.IsMatch(header, @"float damage_scale;\s*(///[^\n]*\n\s*)*int32_t at_kind;\s*(///[^\n]*\n\s*)*uint32_t at_entity;\s*(///[^\n]*\n\s*)*float at_point\[3\];")).IsTrue();

        string api = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp")) + File.ReadAllText(Path.Combine(root, "src", "payload", "src", "core", "mod_requests.cpp")));
        foreach (string rule in new[]
                 {
                     "constexpr uint32_t kAimPoint = 0x825FEFC8u;",
                     "constexpr uint32_t kSpellDefinition = 0x8260D580u;",
                     "constexpr uint32_t kLightningReadData = 0x827405C0u;",
                     "constexpr uint32_t kLightningCreateImpactFx = 0x1Cu;",
                     "constexpr float kLightningLift = 0.25f;",
                     "constexpr float kLightningReach = 15.0f;",
                     "constexpr float kLightningReacquireHeight = 1.35f;",
                     "static_assert(kLightningReach > kLightningReacquireHeight,",
                     "constexpr float kFireballImpactReach = 1.0f;",
                     "constexpr float kVortexSpawnAhead = 1.25f;",
                     "v1[2] = place[2] + kLightningReach;",
                     "v1[i] = place[i] - 0.5f * kFireballImpactReach * u[i];",
                     "v1[i] = place[i] - kVortexSpawnAhead * u[i];",
                     "AimPoint(ctx, base, options.at_entity, LightningCreatesImpactFx(ctx, base, definitions, power),",
                     "refii_bridge_mod_error_post(error, std::strlen(error));",
                 })
        {
            AssertThat(api.Contains(rule, StringComparison.Ordinal)).OverrideFailureMessage(rule).IsTrue();
        }
        // Each refusal is a case of its own: no point for Fireball or Chaos, nothing for the rest.
        string place = Regex.Match(api, @"bool PlaceCastAt\(.*?\n\}", RegexOptions.Singleline).Value;
        AssertThat(place.Length > 0).IsTrue();
        AssertThat(Regex.Matches(place, @"CastAtError\(""cast_spell %s: no rule for a point").Count).IsEqual(2);
        AssertThat(place.Contains("default:\n      CastAtError(\"cast_spell %s: no rule for casting at a place\", name);", StringComparison.Ordinal)).IsTrue();
        foreach (string type in new[] { "kSpellLightning", "kSpellFireball", "kSpellBlades", "kSpellVortex", "kSpellChaos", "kSpellForcePush" })
        {
            AssertThat(place.Contains("case " + type + ":", StringComparison.Ordinal)).OverrideFailureMessage(type).IsTrue();
        }

        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        AssertThat(natives.Contains("options.at_kind = lua.Int(16);", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("options.at_entity = lua.Entity(17);", StringComparison.Ordinal)).IsTrue();
        string preamble = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs"));
        AssertThat(preamble.Contains("if type(o.at) == 'table' then ak, a = 2, o.at elseif o.at ~= nil then ak, ae = 1, o.at end", StringComparison.Ordinal)).IsTrue();
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("options.at_kind = REFII_MOD_CAST_AT_ENTITY;", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("options.at_kind = REFII_MOD_CAST_AT_POINT;", StringComparison.Ordinal)).IsTrue();
        string host = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs"));
        AssertThat(host.Contains("line += $\" at {Num(point.X)} {Num(point.Y)} {Num(point.Z)}\";", StringComparison.Ordinal)).IsTrue();

        // The three functions the rules call, each with its disc twin (same size, unique masked body).
        string rows = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc"));
        foreach (string row in new[] { "{0x825FEFC8u, 0x824BF198u},", "{0x8260D580u, 0x82622940u},", "{0x827405C0u, 0x827156C8u}," })
        {
            AssertThat(rows.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage(row).IsTrue();
        }
    }

    [TestCase]
    public void TheActionKindThePauseAndTheFrontEndAreOnEverySurface()
    {
        // Part H.16: an action's kind (its definition is a script table; its Type is
        // EScriptableAction), the game's pause state as an event, and the front end's own surface.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        foreach (string entry in new[]
                 {
                     "refii_mod_action_type(base, action)", "refii_mod_action_name(base, action, char* out, size)",
                     "refii_mod_scriptable_action_name(type)", "refii_mod_signed_in_user(base)", "refii_mod_save_count(ctx, base)",
                     "refii_mod_frontend_screen(ctx, base, char* out, size)",
                 })
        {
            AssertThat(native.Contains(entry, StringComparison.Ordinal)).OverrideFailureMessage(entry).IsTrue();
        }
        foreach (string entry in new[] { "Refii.action_type(a)", "Refii.action_name(a)", "Refii.signed_in()", "Refii.save_count()", "Refii.screen()" })
        {
            AssertThat(lua.Contains(entry, StringComparison.Ordinal)).OverrideFailureMessage(entry).IsTrue();
        }
        AssertThat(godot.Contains("query action A / frontend", StringComparison.Ordinal)).IsTrue();

        // The two events, their kinds (98 and 99: the last two of the hundred; the masks' capacity
        // is TheMasksHoldEveryKindOnEverySurface's), and the action kind on the two action events.
        ModApiEvent paused = ModApi.Events.Single(e => e.Name == "Paused");
        AssertThat(paused.Kind).IsEqual(98);
        AssertThat(paused.LuaName).IsEqual("paused");
        AssertThat(paused.Fields.Select(f => f.Name).ToArray()).IsEqual(new[] { "paused", "mode", "player", "screen" });
        AssertThat(paused.SeamFunctions.Contains("rex_lhGameFlowGuiMode_Apply")).IsTrue();
        AssertThat(paused.SeamFunctions.Contains("rex_lhGameFlowGuiMode_Remove")).IsTrue();
        ModApiEvent start = ModApi.Events.Single(e => e.Name == "StartGame");
        AssertThat(start.Kind).IsEqual(99);
        AssertThat(start.Fields.Select(f => f.Name).ToArray()).IsEqual(new[] { "save", "level", "new_game", "player", "commit" });
        AssertThat(start.SeamFunctions.Contains("rex_lhFrontEnd_SetStartGameTarget")).IsTrue();
        AssertThat(ModApi.Events.Count).IsEqual(164);
        foreach (string name in new[] { "MeleeSwing", "AnimEvent" })
        {
            ModApiEvent e = ModApi.Events.Single(x => x.Name == name);
            AssertThat(e.Fields.Any(f => f.Name == "action_type")).OverrideFailureMessage(name + ".action_type").IsTrue();
            AssertThat(e.Fields.Any(f => f.Name == "action_name")).OverrideFailureMessage(name + ".action_name").IsTrue();
        }

        // The front-end surface: every native the payload registers in the Gameface state is under
        // Refii in the preamble, and the doc's front-end table names each call and each event.
        foreach (string entry in new[] { "Refii.hash = __refii_hash", "Refii.signed_in = __refii_signed_in", "Refii.save_count = __refii_save_count", "Refii.screen = __refii_screen" })
        {
            AssertThat(ModLua.FrontendNatives.Contains(entry, StringComparison.Ordinal)).OverrideFailureMessage(entry).IsTrue();
        }
        if (RepoRoot() is not { } root)
        {
            return;
        }

        // The guide's UI chapter carries the table: its section runs to the next heading of its level or above.
        string doc = File.ReadAllText(Path.Combine(GuideFolder(root), "ui-and-hud.md"));
        int table = doc.IndexOf("### The front-end surface", StringComparison.Ordinal);
        AssertThat(table >= 0).IsTrue();
        int next = doc.IndexOf("\n## ", table, StringComparison.Ordinal);
        string section = next < 0 ? doc[table..] : doc[table..next];
        foreach (string call in new[]
                 {
                     "`Refii.hash(name)`", "`Refii.setting(key, default)`", "`Refii.log(text)`", "`Refii.publish(key, n)`", "`Refii.value(key)`",
                     "`Refii.notice(", "`Refii.message_box(", "`Refii.menu_box(", "`Refii.open_menu(", "`Refii.menu_player()`",
                     "`Refii.signed_in()`", "`Refii.save_count()`", "`Refii.screen()`", "`start_game`", "`mod_menu_row`", "`mod_message_box`",
                     "`mod_notice`", "`mod_menu_box`",
                 })
        {
            AssertThat(section.Contains(call, StringComparison.Ordinal)).OverrideFailureMessage("front-end table: " + call).IsTrue();
        }
        AssertThat(GuideText(root).Contains("A pause/unpause event: no single function was found", StringComparison.Ordinal)).IsFalse();

        // The seams: the three new hooks named in both builds, the type reader's row.
        foreach (string build in new[] { "Fable2", "Fable2TU1" })
        {
            string config = File.ReadAllText(Path.Combine(root, "Fable2", "versions", build, "refii_config.toml"));
            foreach (string function in new[] { "rex_CActionFactory_CreateFromDefinition", "rex_lhGameFlowGuiMode_Apply", "rex_lhGameFlowGuiMode_Remove", "rex_lhFrontEnd_SetStartGameTarget" })
            {
                AssertThat(config.Contains("\"" + function + "\"", StringComparison.Ordinal)).OverrideFailureMessage(build + " " + function).IsTrue();
            }
        }
        string rows = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc"));
        AssertThat(rows.Contains("{0x8229DDF0u, 0x8229DB28u},", StringComparison.Ordinal)).IsTrue();  // the definition's field reader
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        AssertThat(bus.Contains("static_assert(kScriptableActionCount == 89", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("REX_EXTERN(rex_lhFrontEnd_SetStartGameTarget)", StringComparison.Ordinal)).IsTrue();
        string frontend = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_frontend_lua.cpp"));
        foreach (string registered in new[] { "__refii_hash", "__refii_signed_in", "__refii_save_count", "__refii_screen" })
        {
            AssertThat(frontend.Contains("{\"" + registered + "\", &Native", StringComparison.Ordinal)).OverrideFailureMessage(registered).IsTrue();
        }
    }
    [TestCase]
    public void TheCutsceneIsOnEverySurface()
    {
        // The game's own three statements of a cutscene (full, interactive, look-at) as one event
        // raised per kind on each edge and one query, on every surface, both players principals.
        ModApiEvent cutscene = ModApi.Events.Single(e => e.Name == "Cutscene");
        AssertThat(cutscene.Kind).IsEqual(100);
        AssertThat(cutscene.LuaName).IsEqual("cutscene");
        AssertThat(string.Join(",", cutscene.Fields.Select(f => f.Name))).IsEqual("player,active,kind,kinds,hero");
        AssertThat(cutscene.Cancellable).IsFalse();
        AssertThat(cutscene.SeamFunctions.Any()).OverrideFailureMessage("polled, no hook: no codegen name").IsFalse();
        AssertThat(ModApi.CutsceneFull).IsEqual(1);
        AssertThat(ModApi.CutsceneInteractive).IsEqual(2);
        AssertThat(ModApi.CutsceneLookAt).IsEqual(4);
        AssertThat(ModApi.CutsceneAll).IsEqual(7);

        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        AssertThat(native.Contains("int32_t refii_mod_in_cutscene(ctx, base, player)", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.in_cutscene([p [, kinds]]) -> inside, kinds", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("in_cutscene(player = 0, kinds = 7) -> bool / cutscene_kinds(player = 0)", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("/ in_cutscene P", StringComparison.Ordinal)).IsTrue();
        // Every short form is an overload of its own (the script bridge passes no C# default).
        AssertThat(typeof(RefiiHost).GetMethods().Where(m => m.Name == "in_cutscene").Select(m => m.GetParameters().Length).OrderBy(n => n).ToArray())
            .IsEqual(new[] { 0, 1, 2 });
        AssertThat(typeof(RefiiHost).GetMethods().Where(m => m.Name == "cutscene_kinds").Select(m => m.GetParameters().Length).OrderBy(n => n).ToArray())
            .IsEqual(new[] { 0, 1 });

        if (RepoRoot() is not { } root)
        {
            return;
        }

        string header = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h"));
        AssertThat(header.Contains("#define REFII_CUTSCENE_FULL 1", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("#define REFII_CUTSCENE_INTERACTIVE 2", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("#define REFII_CUTSCENE_LOOK_AT 4", StringComparison.Ordinal)).IsTrue();
        string modHeader = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        AssertThat(modHeader.Contains("extern \"C\" int32_t refii_mod_in_cutscene(PPCContext* ctx, uint8_t* base, int32_t player);", StringComparison.Ordinal)).IsTrue();

        // The three reads: the game's own full-cutscene query (row for the disc build), the two
        // fields the Player setters write (the same offsets on both builds).
        string api = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp")) + File.ReadAllText(Path.Combine(root, "src", "payload", "src", "core", "mod_requests.cpp")));
        AssertThat(api.Contains("constexpr uint32_t kForegroundCutscene = 0x3B;", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("constexpr uint32_t kLookAtCameraMode = 0xC8;", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("constexpr uint32_t kIsFullCutscenePlaying = 0x823060E0u;", StringComparison.Ordinal)).IsTrue();
        string rows = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc"));
        AssertThat(rows.Contains("{0x823060E0u, 0x82907D08u},", StringComparison.Ordinal)).IsTrue();
        foreach ((string build, string address) in new[] { ("Fable2TU1", "0x823060E0"), ("Fable2", "0x82907D08") })
        {
            string config = File.ReadAllText(Path.Combine(root, "Fable2", "versions", build, "refii_config.toml"));
            AssertThat(config.Contains(address + " = { size = 0x6C }", StringComparison.Ordinal))
                .OverrideFailureMessage($"{build}: GUI.IsFullCutscenePlaying is not a function the recompiler emits").IsTrue();
        }

        // The edges: raised from the tick before the Tick event, per kind, read only while someone listens.
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        int edges = bus.IndexOf("DispatchCutsceneEdges(ctx, base);", StringComparison.Ordinal);
        int tick = bus.IndexOf("TickEvent tick{};", StringComparison.Ordinal);
        AssertThat(edges >= 0 && tick > edges).OverrideFailureMessage("the edges are raised before the Tick event").IsTrue();
        AssertThat(bus.Contains("if (!AnySubscriber(REFII_EVENT_CUTSCENE)) {", StringComparison.Ordinal)).IsTrue();
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        AssertThat(natives.Contains("{\"in_cutscene\", &NativeInCutscene, 0},", StringComparison.Ordinal)).IsTrue();
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("what == \"in_cutscene\"", StringComparison.Ordinal)).IsTrue();
        string preamble = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs"));
        AssertThat(preamble.Contains("Refii.CUTSCENE_FULL = ", StringComparison.Ordinal)).IsTrue();
        string events = File.ReadAllText(Path.Combine(GuideFolder(root), "reference-events.md"));
        AssertThat(events.Contains("| 100 | Cutscene / cutscene |", StringComparison.Ordinal)).IsTrue();
        string cutscenes = File.ReadAllText(Path.Combine(GuideFolder(root), "cutscenes.md"));
        AssertThat(cutscenes.Contains("Refii.in_cutscene(", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheCutsceneStateFollowsEachStatement()
    {
        var state = new ModCutscene();
        AssertThat(state.Kinds(1)).IsEqual(0);
        AssertThat(state.In(0)).IsFalse();

        // Player one's conversation camera: look-at for player one only.
        state.Stated(1, ModApi.CutsceneLookAt);
        AssertThat(state.Kinds(1)).IsEqual(ModApi.CutsceneLookAt);
        AssertThat(state.Kinds(2)).IsEqual(0);
        AssertThat(state.In(1)).IsTrue();
        AssertThat(state.In(2)).IsFalse();
        AssertThat(state.In(0)).IsTrue();
        AssertThat(state.In(1, ModApi.CutsceneInteractive)).IsFalse();

        // A full cutscene is every player's: player 0's edge carries only its own bit.
        state.Stated(0, ModApi.CutsceneFull);
        AssertThat(state.Kinds(2)).IsEqual(ModApi.CutsceneFull);
        AssertThat(state.Kinds(1)).IsEqual(ModApi.CutsceneFull | ModApi.CutsceneLookAt);

        // Player two's interactive scene starts while the full one plays: kinds carries both.
        state.Stated(2, ModApi.CutsceneFull | ModApi.CutsceneInteractive);
        AssertThat(state.Kinds(2)).IsEqual(ModApi.CutsceneFull | ModApi.CutsceneInteractive);
        AssertThat(state.Kinds(0)).IsEqual(ModApi.CutsceneAll);

        // The full cutscene ends; each player keeps their own.
        state.Stated(0, 0);
        AssertThat(state.Kinds(1)).IsEqual(ModApi.CutsceneLookAt);
        AssertThat(state.Kinds(2)).IsEqual(ModApi.CutsceneInteractive);

        // A player's edge restates the full bit too (a seed answered while a full cutscene plays).
        state.Stated(1, ModApi.CutsceneFull);
        AssertThat(state.Kinds(1)).IsEqual(ModApi.CutsceneFull);
        AssertThat(state.Kinds(2)).IsEqual(ModApi.CutsceneFull | ModApi.CutsceneInteractive);

        // Everything ends; an out-of-range player is ignored.
        state.Stated(1, 0);
        state.Stated(2, 0);
        state.Stated(3, ModApi.CutsceneAll);
        state.Stated(-1, ModApi.CutsceneAll);
        AssertThat(state.Kinds(0)).IsEqual(0);
        AssertThat(state.In(1)).IsFalse();
        AssertThat(state.In(2)).IsFalse();
    }

    [TestCase]
    public void OneCastHasOneSerialOnEverySurface()
    {
        // hard_mods_plan Part I wave C, the replica -> local serial mapping: spell_created states the
        // host's serial last (a trailing field), and host_serial / local_serial translate on native,
        // Lua and Godot.
        ModApiEvent created = ModApi.Find("spell_created")!;
        AssertThat(created.Fields[^1].Name).IsEqual("host_serial");
        AssertThat(created.Fields[^1].Type).IsEqual("uint32_t");
        AssertThat(created.Fields[^1].Writable).IsFalse();
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        foreach (string call in new[] { "host_serial", "local_serial" })
        {
            AssertThat(native.Contains("refii_mod_" + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("native " + call).IsTrue();
            AssertThat(lua.Contains("Refii." + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("lua " + call).IsTrue();
            AssertThat(Regex.IsMatch(godot, @"\b" + call + @" SERIAL\b")).OverrideFailureMessage("query " + call).IsTrue();
        }

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        foreach (string call in new[] { "host_serial", "local_serial" })
        {
            AssertThat(header.Contains("extern \"C\" uint32_t refii_mod_" + call + "(uint8_t* base, ", StringComparison.Ordinal)).OverrideFailureMessage("header " + call).IsTrue();
            AssertThat(Regex.IsMatch(natives, "\\{\"" + call + "\", &Native\\w+, 0\\}")).OverrideFailureMessage("natives " + call).IsTrue();
            AssertThat(commands.Contains("what == \"" + call + "\"", StringComparison.Ordinal)).OverrideFailureMessage("query " + call).IsTrue();
        }

        // The three cast-action Starts are named in both builds and hooked; the route carries the pc: lane.
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        foreach (string start in new[] { "rex_CActionSpellReleaseArea_Start", "rex_CActionSpellReleaseTargeted_Start", "rex_CActionSpellCast_Start" })
        {
            AssertThat(bus.Contains("REX_EXTERN(" + start + ")", StringComparison.Ordinal)).OverrideFailureMessage("hook " + start).IsTrue();
            foreach (string build in new[] { "Fable2", "Fable2TU1" })
            {
                string config = File.ReadAllText(Path.Combine(root, "Fable2", "versions", build, "refii_config.toml"));
                AssertThat(config.Contains("{ name = \"" + start + "\" }", StringComparison.Ordinal)).OverrideFailureMessage(build + " " + start).IsTrue();
            }
        }
        AssertThat(bus.Contains("DrainCastPairs(base);", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("MapRelayedHitSerial(e);", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("e.host_serial = refii_mod_host_serial(base, e.serial);", StringComparison.Ordinal)).IsTrue();
        string relay = File.ReadAllText(Path.Combine(root, "src", "runtime", "src", "core", "event_relay.cpp"));
        AssertThat(relay.Contains("constexpr std::string_view kCastLane = \"pc:\";", StringComparison.Ordinal)).IsTrue();
        AssertThat(relay.Contains("extern \"C\" int32_t refii_bridge_route_take_cast(char* out, uint32_t capacity)", StringComparison.Ordinal)).IsTrue();
        AssertThat(File.Exists(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "cast_pairing.h"))).IsTrue();
        AssertThat(File.Exists(Path.Combine(root, "tools", "tests", "cast_pairing_test.cpp"))).IsTrue();
    }

    [TestCase]
    public void ThePartIWaveBGuestSeamsAreOnEverySurface()
    {
        // hard_mods_plan Part I wave B: the stat setters (I.10), the save (I.14), facing and velocity
        // (I.15) and the cast options (I.13) on native, Lua and Godot; the four new events (I.11,
        // I.12, I.14, I.16) and the cast tag on the events of a cast.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        string[] calls = { "modify_renown", "modify_morality", "modify_purity", "save_game", "set_facing" };
        foreach (string call in calls)
        {
            AssertThat(native.Contains("refii_mod_" + call, StringComparison.Ordinal)).OverrideFailureMessage("native " + call).IsTrue();
            AssertThat(lua.Contains("Refii." + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("lua " + call).IsTrue();
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(call))).OverrideFailureMessage("godot " + call).IsTrue();
        }
        foreach (string read in new[] { "can_save", "facing", "velocity" })
        {
            AssertThat(native.Contains("refii_mod_" + read + "(", StringComparison.Ordinal)).OverrideFailureMessage("native " + read).IsTrue();
            AssertThat(lua.Contains("Refii." + read + "(", StringComparison.Ordinal)).OverrideFailureMessage("lua " + read).IsTrue();
            AssertThat(godot.Contains("query " + read, StringComparison.Ordinal)).OverrideFailureMessage("query " + read).IsTrue();
        }
        AssertThat(native.Contains("refii_mod_cast_spell_with(", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("[, options]", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains("cast_spell_with"))).IsTrue();

        // The events: kinds after the cutscene, the verdict's writable seconds, the read-only rest.
        AssertThat(ModApi.Find("health_changed")!.Kind).IsEqual(101);
        AssertThat(ModApi.Find("status_applying")!.Kind).IsEqual(102);
        AssertThat(ModApi.Find("save_written")!.Kind).IsEqual(103);
        AssertThat(ModApi.Find("lock_lost")!.Kind).IsEqual(104);
        AssertThat(string.Join(",", ModApi.Find("health_changed")!.Fields.Select(f => f.Name))).IsEqual("entity,player,delta,health,max,source");
        ModApiEvent applying = ModApi.Find("status_applying")!;
        AssertThat(applying.Cancellable).IsTrue();
        AssertThat(applying.Fields.Single(f => f.Name == "seconds").Writable).IsTrue();
        AssertThat(applying.Fields.Count(f => f.Writable)).IsEqual(1);
        foreach (string name in new[] { "health_changed", "save_written", "lock_lost" })
        {
            AssertThat(ModApi.Find(name)!.Cancellable).OverrideFailureMessage(name + " is after the fact").IsFalse();
            AssertThat(ModApi.Find(name)!.Fields.Any(f => f.Writable)).OverrideFailureMessage(name + " is read-only").IsFalse();
            AssertThat(RefiiHost.SignalNames.Contains(name)).IsTrue();
        }
        AssertThat(string.Join(",", ModApi.Find("save_written")!.Fields.Select(f => f.Name))).IsEqual("player,save,ok");
        AssertThat(ModApi.Find("status_effect")!.Fields.Select(f => f.Name).TakeLast(2).ToArray()).IsEqual(new[] { "serial", "source" });

        // The cast's tag: writable on the launch, carried by the cast's later events; the slot on spell_created.
        ModApiField launchTag = ModApi.Find("spell_launch")!.Fields.Single(f => f.Name == "tag");
        AssertThat(launchTag.Writable).IsTrue();
        foreach (string name in new[] { "spell_created", "vortex_started", "hit", "hit_landed", "fireball_impact", "status_applying" })
        {
            ModApiField tag = ModApi.Find(name)!.Fields.SingleOrDefault(f => f.Name == "tag");
            AssertThat(tag.Name).OverrideFailureMessage(name + " carries no tag").IsEqual("tag");
            AssertThat(tag.Writable).OverrideFailureMessage(name + "'s tag is the launch's").IsFalse();
        }
        AssertThat(ModApi.Find("spell_created")!.Fields.Any(f => f.Name == "slot")).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        foreach (string call in new[] { "modify_renown", "modify_morality", "modify_purity", "can_save", "save_game", "facing", "velocity", "set_facing" })
        {
            AssertThat(header.Contains("refii_mod_" + call + "(PPCContext* ctx, uint8_t* base, ", StringComparison.Ordinal)).OverrideFailureMessage("header " + call).IsTrue();
            AssertThat(Regex.IsMatch(natives, "\\{\"" + call + "\", &Native\\w+, 0\\}")).OverrideFailureMessage("natives " + call).IsTrue();
        }
        foreach (string op in new[] { "modify_renown", "modify_morality", "modify_purity", "save_game", "set_facing", "cast_spell_with" })
        {
            AssertThat(commands.Contains("op == \"" + op + "\"", StringComparison.Ordinal)).OverrideFailureMessage("command " + op).IsTrue();
        }
        foreach (string what in new[] { "can_save", "facing", "velocity" })
        {
            AssertThat(commands.Contains("what == \"" + what + "\"", StringComparison.Ordinal)).OverrideFailureMessage("query " + what).IsTrue();
        }

        // The seams: the installers and health's Modify hooked by their config names, the lock polled.
        foreach (string seam in new[] { "rex_CECHealth_Modify", "rex_SpellLightning_Electrify", "rex_SpellChaos_ApplyToVictim", "rex_SpellFireball_Ignite" })
        {
            AssertThat(bus.Contains("REX_EXTERN(" + seam + ")", StringComparison.Ordinal)).OverrideFailureMessage("hook " + seam).IsTrue();
        }
        // The lock's end is the mode drop's hook now (Part I wave C), not a poll.
        AssertThat(bus.Contains("REX_EXTERN(rex_CPlayer_ClearPlayerMode)", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("DispatchLockEdges", StringComparison.Ordinal)).IsFalse();
        // Chaos fires where it is decided: the creature branch, never the network receive's copy.
        AssertThat(bus.Contains("if (installs && (ctx.r8.u32 & 0xFFu) == 0) {", StringComparison.Ordinal)).IsTrue();
        // The cast's serial is the registry's counter, read before the factory runs.
        AssertThat(bus.Contains("constexpr uint32_t kRegistrySerialCounter = 0xC;", StringComparison.Ordinal)).IsTrue();
        string saves = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "player_state_hooks.cpp"));
        AssertThat(saves.Contains("RaiseSaveWritten(ctx, base, name, player, ok);", StringComparison.Ordinal)).IsTrue();
        // Godot's save_written is the event's: the host's stamp kind no longer emits it.
        string host = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs"));
        AssertThat(host.Contains("EmitSignal(SignalName.save_written", StringComparison.Ordinal)).IsFalse();

        // The guest functions the helpers call by TU1 address, each with its disc row (masked body
        // match, unique, same size on both builds).
        string rows = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc"));
        foreach (string row in new[]
                 {
                     "{0x823F3660u, 0x823D2070u},",  // Player.IsAbleToSaveGame
                     "{0x82445730u, 0x824E8D28u},",  // AutoSaveGame
                     "{0x821F5BC0u, 0x821F5A90u},",  // Physics.GetFacingVector
                     "{0x82266518u, 0x822664D8u},",  // Physics.SetFacingVector
                     "{0x823F3420u, 0x823D1E30u},",  // Player.SetFacingVector
                 })
        {
            AssertThat(rows.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage(row).IsTrue();
        }
    }

    [TestCase]
    public void ThePartIWaveCMovementAndQuickSeamsAreOnEverySurface()
    {
        // hard_mods_plan Part I wave C (movement, inventory, quick actions): the sprint (C.15), the
        // held expression and its release (C.17), the ungated quick actions (C.16), per-hero
        // locomotion and parry (C.12) on native, Lua and Godot; item_removed (C.13) and
        // expression_wheel (C.14).
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        foreach (string call in new[] { "release_expression", "end_sprint", "set_locomotion", "set_parry" })
        {
            AssertThat(native.Contains("refii_mod_" + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("native " + call).IsTrue();
            AssertThat(lua.Contains("Refii." + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("lua " + call).IsTrue();
            AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains(call))).OverrideFailureMessage("godot " + call).IsTrue();
        }
        foreach (string read in new[] { "sprinting", "locomotion", "parry" })
        {
            AssertThat(native.Contains("refii_mod_" + read + "(", StringComparison.Ordinal)).OverrideFailureMessage("native " + read).IsTrue();
            AssertThat(lua.Contains("Refii." + read + "(", StringComparison.Ordinal)).OverrideFailureMessage("lua " + read).IsTrue();
            AssertThat(godot.Contains("query " + read, StringComparison.Ordinal)).OverrideFailureMessage("query " + read).IsTrue();
        }
        AssertThat(native.Contains("refii_mod_quick_action(", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.perform_expression(p, expression [, held [, opts]])", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("perform_expression(player, expression, held", StringComparison.Ordinal)).IsTrue();

        // The events: appended in one change, read-only, after the fact, a signal each.
        ModApiEvent removed = ModApi.Find("item_removed")!;
        ModApiEvent wheel = ModApi.Find("expression_wheel")!;
        AssertThat(wheel.Kind).IsEqual(removed.Kind + 1);
        AssertThat(string.Join(",", removed.Fields.Select(f => f.Name))).IsEqual("entity,player,inventory,definition,count,remaining");
        AssertThat(string.Join(",", wheel.Fields.Select(f => f.Name))).IsEqual("player,open,highlighted");
        foreach (ModApiEvent e in new[] { removed, wheel })
        {
            AssertThat(e.Cancellable).OverrideFailureMessage(e.LuaName + " is after the fact").IsFalse();
            AssertThat(e.Fields.Any(f => f.Writable)).OverrideFailureMessage(e.LuaName + " is read-only").IsFalse();
            AssertThat(RefiiHost.SignalNames.Contains(e.LuaName)).IsTrue();
        }

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        string preamble = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs"));
        foreach (string call in new[] { "quick_action", "release_expression", "end_sprint", "set_locomotion", "locomotion", "set_parry", "parry" })
        {
            AssertThat(header.Contains("refii_mod_" + call + "(PPCContext* ctx, uint8_t* base, ", StringComparison.Ordinal)).OverrideFailureMessage("header " + call).IsTrue();
        }
        AssertThat(header.Contains("refii_mod_sprinting(uint8_t* base, int32_t player)", StringComparison.Ordinal)).IsTrue();
        foreach (string native_name in new[] { "release_expression", "sprinting", "end_sprint", "_set_locomotion", "locomotion", "set_parry", "parry" })
        {
            AssertThat(Regex.IsMatch(natives, "\\{\"" + native_name + "\", &Native\\w+, 0\\}")).OverrideFailureMessage("natives " + native_name).IsTrue();
        }
        AssertThat(preamble.Contains("function Refii.set_locomotion(p, values, value)", StringComparison.Ordinal)).IsTrue();
        foreach (string op in new[] { "release_expression", "end_sprint", "set_locomotion", "set_parry" })
        {
            AssertThat(commands.Contains("op == \"" + op + "\"", StringComparison.Ordinal)).OverrideFailureMessage("command " + op).IsTrue();
        }
        foreach (string what in new[] { "sprinting", "locomotion", "parry" })
        {
            AssertThat(commands.Contains("what == \"" + what + "\"", StringComparison.Ordinal)).OverrideFailureMessage("query " + what).IsTrue();
        }

        // The seams: the removal tail, the wheel's open and close and the locomotion load hooked by
        // their config names (both builds), the highlight polled, the parry restated on a new hero.
        foreach (string seam in new[] { "rex_CECInventory_RefreshRecordRefs", "rex_lhGuiManager_OpenExpressionWheel", "rex_lhGuiManager_CloseExpressionWheel", "rex_CECLocomotion_LoadData" })
        {
            AssertThat(bus.Contains("REX_EXTERN(" + seam + ")", StringComparison.Ordinal)).OverrideFailureMessage("hook " + seam).IsTrue();
            foreach (string build in new[] { "Fable2", "Fable2TU1" })
            {
                string config = File.ReadAllText(Path.Combine(root, "Fable2", "versions", build, "refii_config.toml"));
                AssertThat(config.Contains("\"" + seam + "\"", StringComparison.Ordinal)).OverrideFailureMessage(build + " " + seam).IsTrue();
            }
        }
        AssertThat(bus.Contains("DispatchWheelHighlight(ctx, base);", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("refii::vfs::ReapplyHeldParry(ctx, base, p);", StringComparison.Ordinal)).IsTrue();

        // The guest functions the helpers call by TU1 address, each with its disc row.
        string rows = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc"));
        foreach (string row in new[]
                 {
                     "{0x82414A50u, 0x823E6E88u},",  // the performer's end of a held expression
                     "{0x8284A8C0u, 0x827DA750u},",  // the sprint setter
                     "{0x823C5E00u, 0x824191A0u},",  // the locomotion map's operator[]
                     "{0x8257EC08u, 0x82C343A0u},",  // the combat parameter table's find
                     "{0x827649C8u, 0x827417E0u},",  // the item's UsableFromInventory test
                 })
        {
            AssertThat(rows.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage(row).IsTrue();
        }
    }

    /// <summary>
    /// PART I WAVE C (spells and combat): each end a mod guessed with a timer is an event at the
    /// guest's own seam, the move-cancel is a verdict, the lock's end is the mode drop's hook, the
    /// summon carries its cast, and record_ref / creature_tier / charge_spell are on every surface.
    /// </summary>
    [TestCase]
    public void PartIWaveCSpellsAndCombat()
    {
        // The events: appended after the expression wheel, in kind order, with their fields.
        AssertThat(ModApi.Find("spell_ended")!.Kind).IsEqual(108);
        AssertThat(ModApi.Find("spell_charge_ended")!.Kind).IsEqual(109);
        AssertThat(ModApi.Find("melee_swing_ended")!.Kind).IsEqual(110);
        AssertThat(ModApi.Find("action_move_interrupt")!.Kind).IsEqual(111);
        AssertThat(string.Join(",", ModApi.Find("spell_ended")!.Fields.Select(f => f.Name))).IsEqual("serial,type,caster,caster_player,tag,reason,level");
        AssertThat(string.Join(",", ModApi.Find("spell_charge_ended")!.Fields.Select(f => f.Name))).IsEqual("player,caster,released");
        AssertThat(string.Join(",", ModApi.Find("melee_swing_ended")!.Fields.Select(f => f.Name))).IsEqual("entity,player,action,action_type,hits,strike,flourish");
        AssertThat(string.Join(",", ModApi.Find("action_move_interrupt")!.Fields.Select(f => f.Name))).IsEqual("entity,player,action,action_type");
        foreach (string name in new[] { "spell_ended", "spell_charge_ended", "melee_swing_ended" })
        {
            AssertThat(ModApi.Find(name)!.Cancellable).OverrideFailureMessage(name + " is after the fact").IsFalse();
            AssertThat(ModApi.Find(name)!.Fields.Any(f => f.Writable)).OverrideFailureMessage(name + " is read-only").IsFalse();
        }
        AssertThat(ModApi.Find("action_move_interrupt")!.Cancellable).IsTrue();
        AssertThat(ModApi.Find("melee_swing_ended")!.IsRelayed).IsTrue();
        AssertThat(ModApi.Find("spell_ended")!.IsRelayed).IsFalse();
        foreach (string name in new[] { "spell_ended", "spell_charge_ended", "melee_swing_ended", "action_move_interrupt" })
        {
            AssertThat(RefiiHost.SignalNames.Contains(name)).OverrideFailureMessage(name + " signal").IsTrue();
        }

        // summoned carries its cast: serial and tag, trailing (a mod built against the old struct keeps its offsets).
        AssertThat(ModApi.Find("summoned")!.Fields.Select(f => f.Name).TakeLast(2).ToArray()).IsEqual(new[] { "serial", "tag" });
        AssertThat(ModApi.Find("spell_ended")!.Fields.Single(f => f.Name == "tag").Writable).IsFalse();

        // The calls on every surface.
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        foreach (string call in new[] { "record_ref", "creature_tier", "charge_spell" })
        {
            AssertThat(native.Contains("refii_mod_" + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("native " + call).IsTrue();
            AssertThat(lua.Contains("Refii." + call + "(", StringComparison.Ordinal)).OverrideFailureMessage("lua " + call).IsTrue();
        }
        AssertThat(godot.Contains("query record_ref DEF FIELD", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("query creature_tier E", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModApi.GodotHelpers.Any(h => ModApi.GodotMethodNames(h.Signature).Contains("charge_spell"))).IsTrue();
        AssertThat(ModApi.Constants.Single(c => c.Name == "CREATURE_ELITE").Value).IsEqual(1);
        AssertThat(ModApi.Constants.Single(c => c.Name == "CREATURE_LIEUTENANT").Value).IsEqual(2);

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        string events = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h"));
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        string api = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp")) + File.ReadAllText(Path.Combine(root, "src", "payload", "src", "core", "mod_requests.cpp")));
        string abilities = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_abilities.cpp"));
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_record_ref(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_creature_tier(uint8_t* base, uint32_t entity);", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_charge_spell(PPCContext* ctx, uint8_t* base, int32_t player, int32_t type, int32_t held);", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("#define REFII_CREATURE_ELITE 1", StringComparison.Ordinal) && header.Contains("#define REFII_CREATURE_LIEUTENANT 2", StringComparison.Ordinal)).IsTrue();
        AssertThat(events.Contains("#define REFII_SPELL_END_FINISHED 0", StringComparison.Ordinal) && events.Contains("#define REFII_SPELL_END_STOPPED 1", StringComparison.Ordinal)).IsTrue();
        foreach (string call in new[] { "record_ref", "creature_tier", "charge_spell" })
        {
            AssertThat(Regex.IsMatch(natives, "\\{\"" + call + "\", &Native\\w+, 0\\}")).OverrideFailureMessage("natives " + call).IsTrue();
        }
        AssertThat(commands.Contains("what == \"record_ref\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("what == \"creature_tier\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("op == \"charge_spell\"", StringComparison.Ordinal)).IsTrue();
        // The type-6 value word is the named record's hash; the rank bytes the creature init writes.
        AssertThat(api.Contains("constexpr uint32_t kGdbTypeRef = 6;", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("constexpr uint32_t kCreatureIsLieutenant = 0x1C;", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("constexpr uint32_t kCreatureIsElite = 0x1D;", StringComparison.Ordinal)).IsTrue();
        // The lend writes with NOTIFY clear (no spell_pick); a real pick meanwhile is what comes back.
        AssertThat(api.Contains("work.r7.u32 = 0u;", StringComparison.Ordinal)).IsTrue();
        AssertThat(abilities.Contains("refii::vfs::ModChargeLendPicked(player, slot,", StringComparison.Ordinal)).IsTrue();

        // The seams, hooked here and named in both builds.
        var seams = new Dictionary<string, (string Tu1, string Disc)>
        {
            ["rex_CSpellRegistry_Update"] = ("0x822A7F50", "0x822A7D10"),
            ["rex_CAction_End"] = ("0x822861A8", "0x82285D10"),
            ["rex_CAbilityActionMagicCast_Start"] = ("0x8298A870", "0x829B9E30"),
            ["rex_CAction_MovementInterruptible"] = ("0x822BDEA0", "0x822BDBC0"),
            ["rex_CPlayer_ClearPlayerMode"] = ("0x82389CD0", "0x8238F4A0"),
            ["rex_CECSpellManager_SetState"] = ("0x8253E840", "0x82429280"),
        };
        string tu1 = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2TU1", "refii_config.toml"));
        string disc = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "refii_config.toml"));
        foreach ((string name, (string t, string d)) in seams)
        {
            AssertThat(bus.Contains("REX_EXTERN(" + name + ")", StringComparison.Ordinal)).OverrideFailureMessage("hook " + name).IsTrue();
            AssertThat(bus.Contains("PPC_EXTERN_FUNC(__imp__" + name + ");", StringComparison.Ordinal)).OverrideFailureMessage("extern " + name).IsTrue();
            AssertThat(tu1.Contains($"{t} = {{ name = \"{name}\" }}")).OverrideFailureMessage(name + " TU1").IsTrue();
            AssertThat(disc.Contains($"{d} = {{ name = \"{name}\" }}")).OverrideFailureMessage(name + " disc").IsTrue();
        }
        // The registry's two midasm reads, r31 the spell, at each build's own addresses.
        foreach ((string config, string updating, string ending) in new[] { (tu1, "0x822A7FD8", "0x822A8008"), (disc, "0x822A7D98", "0x822A7DC8") })
        {
            AssertThat(config.Contains("name = \"RefiiSpellRegistryUpdating\"\naddress = " + updating + "\nregisters = [\"r31\"]", StringComparison.Ordinal)).IsTrue();
            AssertThat(config.Contains("name = \"RefiiSpellRegistryEnding\"\naddress = " + ending + "\nregisters = [\"r31\"]", StringComparison.Ordinal)).IsTrue();
        }
        AssertThat(bus.Contains("void RefiiSpellRegistryUpdating(PPCRegister& r31)", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("void RefiiSpellRegistryEnding(PPCRegister& r31)", StringComparison.Ordinal)).IsTrue();
        // The strike's end is relayed like its start; the decider's hits count on this machine.
        AssertThat(bus.Contains("REFII_RELAY(MeleeSwingEnded, kPlayer),", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("NoteSwingHit(base, e.attacker);", StringComparison.Ordinal)).IsTrue();
        // The summon's serial as each seam states it.
        AssertThat(bus.Contains("const uint32_t serial = ctx.r6.u32;", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("constexpr uint32_t kSwordSetSerial = 0x10;", StringComparison.Ordinal)).IsTrue();

        // The addresses the hooks name, each with its disc row.
        string rows = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc"));
        foreach (string row in new[]
                 {
                     "{0x820D2FB0u, 0x820CF820u},",  // the quick charge action's vtable
                     "{0x820D30A0u, 0x820CF910u},",  // the full charge action's vtable
                     "{0x82298738u, 0x822982F8u},",  // the action handle's resolver
                 })
        {
            AssertThat(rows.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage(row).IsTrue();
        }
    }

    /// <summary>
    /// A spell's own numbers are a stated read on every surface: one power as its Targeted class's own
    /// data read loads it for the caster's definitions (Lightning's ElectrifyDuration, Fireball's
    /// CreatureBurnTime, Chaos's Duration with their Starts' spreads; Vortex's Duration; Blades'
    /// NumSwords), each read a function row on both builds.
    /// </summary>
    [TestCase]
    public void ASpellStatesItsOwnBalance()
    {
        AssertThat(ModApi.LuaHelpers.Any(h => h.Signature.StartsWith("Refii.spell_balance(caster, type, level)", StringComparison.Ordinal))).IsTrue();
        AssertThat(ModApi.Helpers.Any(h => h.Signature.Contains("refii_mod_spell_balance(", StringComparison.Ordinal))).IsTrue();
        AssertThat(ModApi.SpellBalanceMeaning.Contains("ElectrifyDuration", StringComparison.Ordinal) &&
                   ModApi.SpellBalanceMeaning.Contains("CreatureBurnTime", StringComparison.Ordinal) &&
                   ModApi.SpellBalanceMeaning.Contains("NumSwords", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModApi.VerdictReads.Any(r => r.Verb == "spell_balance" && r.Lua == "spell_balance")).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }
        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_spell_balance(PPCContext* ctx, uint8_t* base, uint32_t caster, int32_t type, int32_t level,", StringComparison.Ordinal)).IsTrue();
        string api = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp")) + File.ReadAllText(Path.Combine(root, "src", "payload", "src", "core", "mod_requests.cpp")));
        foreach (string read in new[]
                 {
                     "{kSpellLightning, kLightningReadData, kLightningRecords, kLightningRecordBytes, 4, 5.0f, -1, -1},",
                     "{kSpellFireball, 0x82743968u, 0xB4u, 36u, 0, 0.0f, -1, -1},",
                     "{kSpellBlades, 0x825A1D68u, 0x80u, 20u, -1, 0.0f, -1, 0},",
                     "{kSpellVortex, 0x828B1EA8u, 0x80u, 92u, -1, 0.0f, 0, -1},",
                     "{kSpellChaos, 0x82748B10u, 0x80u, 4u, 0, 2.0f, -1, -1},",
                 })
        {
            AssertThat(api.Contains(read, StringComparison.Ordinal)).OverrideFailureMessage(read).IsTrue();
        }
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        AssertThat(natives.Contains("{\"spell_balance\", &NativeSpellBalance, 0},", StringComparison.Ordinal)).IsTrue();
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("what == \"spell_balance\"", StringComparison.Ordinal)).IsTrue();
        // The data reads, matched to the unpatched build by masked body (struct offsets kept).
        string rows = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc"));
        foreach (string row in new[]
                 {
                     "{0x825A1D68u, 0x82552B50u},", "{0x827405C0u, 0x827156C8u},", "{0x82743968u, 0x82718A68u},",
                     "{0x82748B10u, 0x8271DC10u},", "{0x828B1EA8u, 0x8288CB80u},", "{0x8260D580u, 0x82622940u},",
                 })
        {
            AssertThat(rows.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage(row).IsTrue();
        }
        string calls = File.ReadAllText(Path.Combine(GuideFolder(root), "reference-calls.md"));
        AssertThat(calls.Contains("Refii.spell_balance(caster, type, level)", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void AVortexStatesWhoItCatchesAndLetsGo()
    {
        ModApiEvent caught = ModApi.Find("vortex_caught")!;
        AssertThat(caught.Kind).IsEqual(113);
        AssertThat(caught.Name).IsEqual("VortexCaught");
        AssertThat(string.Join(",", caught.Fields.Select(f => f.Name))).IsEqual("serial,tag,vortex,entity,victim,player,caster,caster_player,caught");
        AssertThat(caught.Cancellable).IsFalse();
        AssertThat(caught.Fields.Any(f => f.Writable)).IsFalse();
        AssertThat(RefiiHost.SignalNames.Contains("vortex_caught")).IsTrue();
        AssertThat(ModApi.Find("status_effect")!.Fields.Single(f => f.Name == "serial").Meaning.Contains("vortex", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }
        string events = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h"));
        AssertThat(events.Contains("REFII_EVENT_VORTEX_CAUGHT = 113,", StringComparison.Ordinal)).IsTrue();
        AssertThat(events.Contains("struct VortexCaughtEvent : Event {", StringComparison.Ordinal)).IsTrue();
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        // Capture states the status's serial and source; both seams raise the event after the call.
        AssertThat(bus.Contains("Installing(REFII_STATUS_VORTEX, serial, caster, [&] { __imp__rex_VortexComponent_Capture(ctx, base); });", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("RaiseVortexCaught(ctx, base, component, victim, true);", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("RaiseVortexCaught(ctx, base, component, victim, false);", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("case REFII_EVENT_VORTEX_CAUGHT:", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("extern \"C\" int32_t refii_mod_vortex_held(", StringComparison.Ordinal)).IsTrue();
        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_vortex_held(uint8_t* base, uint32_t serial, uint32_t* out, int32_t max);", StringComparison.Ordinal)).IsTrue();
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        AssertThat(natives.Contains("{\"vortex_held\", &NativeVortexHeld, 0},", StringComparison.Ordinal)).IsTrue();
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("what == \"vortex_held\"", StringComparison.Ordinal)).IsTrue();

        // Capture was named already; Release is new. Same bodies on both builds (masked, unique).
        var seams = new Dictionary<string, (string Tu1, string Disc)>
        {
            ["rex_VortexComponent_Capture"] = ("0x8275C578", "0x82733F58"),
            ["rex_VortexComponent_Release"] = ("0x8275CB28", "0x82734508"),
        };
        string tu1 = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2TU1", "refii_config.toml"));
        string disc = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "refii_config.toml"));
        foreach ((string name, (string t, string d)) in seams)
        {
            AssertThat(bus.Contains("REX_EXTERN(" + name + ")", StringComparison.Ordinal)).OverrideFailureMessage("hook " + name).IsTrue();
            AssertThat(bus.Contains("PPC_EXTERN_FUNC(__imp__" + name + ");", StringComparison.Ordinal)).OverrideFailureMessage("extern " + name).IsTrue();
            AssertThat(tu1.Contains($"{t} = {{ name = \"{name}\" }}")).OverrideFailureMessage(name + " TU1").IsTrue();
            AssertThat(disc.Contains($"{d} = {{ name = \"{name}\" }}")).OverrideFailureMessage(name + " disc").IsTrue();
        }
        string reference = File.ReadAllText(Path.Combine(GuideFolder(root), "reference-events.md"));
        AssertThat(reference.Contains("| 113 | VortexCaught / vortex_caught |", StringComparison.Ordinal)).IsTrue();
        string calls = File.ReadAllText(Path.Combine(GuideFolder(root), "reference-calls.md"));
        AssertThat(calls.Contains("Refii.vortex_held(serial)", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// Murgo's Mirror is a mod surface: appearance(p) / set_appearance / set_appearance_automatic on
    /// every surface over the one player-store look, and appearance_changed raised where the morph
    /// hook takes a new look.
    /// </summary>
    [TestCase]
    public void TheMirrorsLookIsOneApiOnEverySurface()
    {
        ModApiEvent changed = ModApi.Find("appearance_changed")!;
        AssertThat(changed.Kind).IsEqual(114);
        AssertThat(changed.Name).IsEqual("AppearanceChanged");
        AssertThat(string.Join(",", changed.Fields.Select(f => f.Name))).IsEqual("player,automatic,hero");
        AssertThat(changed.Cancellable).IsFalse();
        AssertThat(changed.Fields.Any(f => f.Writable)).IsFalse();
        AssertThat(RefiiHost.SignalNames.Contains("appearance_changed")).IsTrue();
        AssertThat(ModApi.VerdictReads.Any(r => r.Verb == "appearance" && r.Lua == "appearance")).IsTrue();
        AssertThat(ModLua.RuntimePreamble(Array.Empty<(string, float)>()).Contains("function Refii.set_appearance(p, t)", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }
        string events = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h"));
        AssertThat(events.Contains("REFII_EVENT_APPEARANCE_CHANGED = 114,", StringComparison.Ordinal)).IsTrue();
        AssertThat(events.Contains("REFII_EVENT_COUNT = 164,", StringComparison.Ordinal)).IsTrue();
        AssertThat(events.Contains("struct AppearanceChangedEvent : Event {", StringComparison.Ordinal)).IsTrue();
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        AssertThat(bus.Contains("case REFII_EVENT_APPEARANCE_CHANGED:", StringComparison.Ordinal)).IsTrue();
        string hook = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "character_morph_hooks.cpp"));
        AssertThat(hook.Contains("refii::mods::AppearanceChangedEvent e{};", StringComparison.Ordinal)).IsTrue();
        string header = (File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h")) + File.ReadAllText(Path.Combine(root, "src", "runtime", "include", "core", "refii_mod_requests.h")));
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_appearance(PPCContext* ctx, uint8_t* base, int32_t player, refii_mod_appearance_t* out);", StringComparison.Ordinal)).IsTrue();
        string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
        AssertThat(natives.Contains("{\"appearance\", &NativeAppearance, 0},", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("{\"_set_appearance\", &NativeSetAppearance, 0},", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("{\"set_appearance_automatic\", &NativeSetAppearanceAutomatic, 0},", StringComparison.Ordinal)).IsTrue();
        string commands = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp"));
        AssertThat(commands.Contains("what == \"appearance\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("op == \"appearance_set\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("op == \"appearance_automatic\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("op == \"appearance_state\"", StringComparison.Ordinal)).IsTrue();
        string reference = File.ReadAllText(Path.Combine(GuideFolder(root), "reference-events.md"));
        AssertThat(reference.Contains("| 114 | AppearanceChanged / appearance_changed |", StringComparison.Ordinal)).IsTrue();
        string calls = File.ReadAllText(Path.Combine(GuideFolder(root), "reference-calls.md"));
        AssertThat(calls.Contains("Refii.appearance(p)", StringComparison.Ordinal)).IsTrue();
        string gameplay = File.ReadAllText(Path.Combine(GuideFolder(root), "gameplay.md"));
        AssertThat(gameplay.Contains("## Appearance (Murgo's Mirror)", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// A vortex can lift nobody: vortex_started's writable lifts is answered where the update asks
    /// how it takes each creature (rex_VortexComponent_Classify, inside that vortex's update), as
    /// the store the classification makes for a creature it cannot lift — so the creature takes the
    /// cast's shudder hit instead of the capture. Trailing fields, so a native mod built against the
    /// old struct keeps its offsets.
    /// </summary>
    [TestCase]
    public void AVortexCanLiftNobody()
    {
        ModApiEvent started = ModApi.Find("vortex_started")!;
        AssertThat(started.Fields.Select(f => f.Name).TakeLast(2).ToArray()).IsEqual(new[] { "damage", "lifts" });
        AssertThat(started.Fields.Single(f => f.Name == "lifts").Writable).IsTrue();
        AssertThat(started.Fields.Count(f => f.Writable)).IsEqual(1);
        AssertThat(started.Cancellable).IsFalse();
        AssertThat(ModApi.WritableFields.Contains((95, "lifts"))).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }
        string events = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h"));
        AssertThat(events.Contains("  float damage;\n  /// Whether the vortex lifts creatures", StringComparison.Ordinal)).IsTrue();
        AssertThat(events.Contains("  bool lifts;\n};", StringComparison.Ordinal)).IsTrue();
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        AssertThat(bus.Contains("REX_EXTERN(rex_VortexComponent_Classify)", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("PPC_EXTERN_FUNC(__imp__rex_VortexComponent_Classify);", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("live->lifts = e.lifts;", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("refii::mods::StoreU32(base, kind, 2u);", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("static_cast<VortexStartedEvent*>(event)->lifts = value != 0.0;", StringComparison.Ordinal)).IsTrue();
        string owner = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "spell_fx_owner.cpp"));
        AssertThat(owner.Contains("const uint32_t outer = refii::vfs::VortexUpdating(", StringComparison.Ordinal)).IsTrue();
        AssertThat(owner.Contains("refii::vfs::VortexUpdating(outer);", StringComparison.Ordinal)).IsTrue();
        // The classification is named on both builds (masked bodies identical, 152 instructions).
        string tu1 = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2TU1", "refii_config.toml"));
        string disc = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "refii_config.toml"));
        AssertThat(tu1.Contains("0x8275EB10 = { name = \"rex_VortexComponent_Classify\" }", StringComparison.Ordinal)).IsTrue();
        AssertThat(disc.Contains("0x827364F0 = { name = \"rex_VortexComponent_Classify\" }", StringComparison.Ordinal)).IsTrue();
        string reference = File.ReadAllText(Path.Combine(GuideFolder(root), "reference-events.md"));
        AssertThat(reference.Contains("`lifts` writable", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// Online a vortex's lifts holds on both machines: the deciding machine's answer rides the cast
    /// pairing's spell line as the spell state, the copy adopts it with the tag, takes no creature
    /// it could lift until then (bounded by the unpaired life), and a copy that caught creatures
    /// before a late pair lets them go through the game's own let-go (sub_8275CA30) before its next
    /// update. tools/tests/cast_pairing_test.cpp holds the table and the line encoding.
    /// </summary>
    [TestCase]
    public void AVortexLiftsAlikeOnBothMachines()
    {
        AssertThat(ModApi.Find("vortex_started")!.Fields.Single(f => f.Name == "lifts").Meaning.Contains("both machines", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }
        string pairing = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "include", "core", "cast_pairing.h"));
        AssertThat(pairing.Contains("constexpr uint32_t kSpellStateNoLift = 1u;", StringComparison.Ordinal)).IsTrue();
        AssertThat(pairing.Contains("\"pc:s,%u,%d,%u,%d,%u\"", StringComparison.Ordinal)).IsTrue();
        string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
        AssertThat(bus.Contains("AdoptSpellState(base, pair.local_serial, pair.state);", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("const uint32_t state = SpellStateOf(base, serial);", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("constexpr uint32_t kVortexLetGo = 0x8275CA30u;", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("refii::cast_pairing::ParseLine(text, &cast, &spell)", StringComparison.Ordinal)).IsTrue();
        AssertThat(bus.Contains("const bool awaiting = SpellAwaitsPair(live->serial);", StringComparison.Ordinal)).IsTrue();
        AssertThat(pairing.Contains("bool AwaitsPeer(uint32_t local_serial, double now) const", StringComparison.Ordinal)).IsTrue();
        string owner = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "spell_fx_owner.cpp"));
        AssertThat(owner.Contains("refii::vfs::ReleaseUnliftedCreatures(ctx, base, component);", StringComparison.Ordinal)).IsTrue();
        string addresses = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc"));
        AssertThat(addresses.Contains("{0x8275CA30u, 0x82734410u},", StringComparison.Ordinal)).IsTrue();
        string guide = File.ReadAllText(Path.Combine(GuideFolder(root), "gameplay.md"));
        AssertThat(guide.Contains("takes the decider's spell state too", StringComparison.Ordinal)).IsTrue();
    }
}
