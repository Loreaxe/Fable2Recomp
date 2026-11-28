namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Entities;
using Refii.Mods;

/// <summary>
/// First Person's switch is a Godot script on the camera API: each player's view is the game's own
/// first-person camera held through <c>camera_override</c> (field of view, pitch limit and the aim rule
/// as the hold's options), toggled by whoever pressed the action, stated again on
/// <c>setting_changed</c>, <c>cutscene</c>, <c>hero_ready</c>, <c>hero_gone</c> and <c>lua_ready</c>, never
/// on a shared screen. How the held view behaves is stated beside the hold through <c>camera_tune</c>
/// (the <c>fp_*</c> keys), for each player this machine plays; the mod ships no Lua, and the engine's
/// core camera chunk applies the values inside the game's first-person camera. The head is the core's
/// (<see cref="Engine_FirstPersonView"/>). Every RefiiHost member the script uses exists with that
/// argument count, and the events and data files are stated.
/// </summary>
[TestSuite]
public class FirstPersonGodotTests
{
    private const string Mod = "first_person";
    private const string File = "first_person.gd";


    private static string Read(string root, params string[] parts) =>
        System.IO.File.ReadAllText(Path.Combine(new[] { GodotModScripts.ModFolder(root, Mod) }.Concat(parts).ToArray()));

    /// <summary>R3 toggles on a controller and is taken from the game, except while L3 is held for Photo mode.</summary>
    [TestCase]
    public void TheControllerTogglesOnR3AndLeavesPhotoMode()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string script = Read(root, "godot", File);
        AssertThat(Read(root, "input.json")).Contains("\"buttons\": [\"R3\"]");
        AssertThat(script).Contains("host.claim_input(1, TOGGLE, not l3_held)");
        AssertThat(script).Contains("host.claim_input(2, TOGGLE, not l3_held)");
        AssertThat(script).Contains("JOY_BUTTON_LEFT_STICK");
        AssertThat(script).Contains("and not l3_held and");
    }

    [TestCase]
    public void ShipsTheGodotSwitchAndNoLua()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        GodotModScripts.AssertShips(GodotModScripts.ModFolder(root, Mod), Array.Empty<string>(), new[] { "godot/" + File });
    }

    [TestCase]
    public void EveryHostMemberExistsAndTheHoldIsPerPlayer()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string text = GodotModScripts.Script(root, Mod, File);
        string code = GodotModScripts.Code(text);
        var called = GodotModScripts.AssertHostContract(root, File, text);
        foreach (string name in new[] { "camera_override", "camera_tune", "in_cutscene", "coop_mode", "split_screen", "player_here", "setting", "constants" })
        {
            AssertThat(called.Contains(name)).OverrideFailureMessage(File + " never calls host." + name).IsTrue();
        }

        AssertThat(GodotModScripts.Constants(code).ToList()).ContainsExactlyInAnyOrder("CAMERA_FIRST_PERSON", "CUTSCENE_INTERACTIVE");

        // The hold is per player: stated for the pressing player and let go with 0; none on a shared screen.
        AssertThat(code.Contains("host.camera_override(p, first_person, { \"fov\": fov, \"pitch_limit\": pitch, \"aim\": aim })", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("host.camera_override(p, 0)", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("on[p] = not on[p]", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("return host.coop_mode() != 1 or host.split_screen()", StringComparison.Ordinal)).IsTrue();

        // The view is stated before the hold and let go after it, only where the player is played.
        int tune = code.IndexOf("host.camera_tune(p, view if here else VIEW_OFF, MOD)", StringComparison.Ordinal);
        int hold = code.IndexOf("host.camera_override(p, first_person,", StringComparison.Ordinal);
        AssertThat(tune >= 0 && hold > tune).IsTrue();
        int release = code.IndexOf("host.camera_override(p, 0)", StringComparison.Ordinal);
        AssertThat(code.IndexOf("host.camera_tune(p, VIEW_OFF, MOD)", StringComparison.Ordinal) > release).IsTrue();
        AssertThat(code.Contains("var here: bool = host.player_here(p)", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("player_set", StringComparison.Ordinal)).IsFalse();

        // The 0.1 workarounds stay gone, and no Lua is run from the script.
        foreach (string gone in new[] { "hide_part", "head_radius", "local_hero", "run_lua", "call_lua", "signal_lua", "node_for" })
        {
            AssertThat(code.Contains(gone, StringComparison.Ordinal)).OverrideFailureMessage(File + " still uses " + gone).IsFalse();
        }
    }

    [TestCase]
    public void TheViewIsStatedAsTuneKeysTheCoreApplies()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string code = GodotModScripts.Code(GodotModScripts.Script(root, Mod, File));
        string[] keys = { "fp_view", "fp_fov", "fp_pitch_limit", "fp_eye_forward", "fp_eye_height", "fp_face", "fp_scenes" };
        foreach (string key in keys)
        {
            // Stated with the view and let go with it.
            AssertThat(Regex.Matches(code, "\"" + key + "\":").Count).OverrideFailureMessage(key).IsEqual(2);
            AssertThat(code.Contains("\"" + key + "\": false", StringComparison.Ordinal)).OverrideFailureMessage(key + " is never let go").IsTrue();
            AssertThat(ModApi.CameraTuneMeaning.Contains(key, StringComparison.Ordinal)).OverrideFailureMessage(key + " is not in the catalog").IsTrue();
        }

        // The core camera chunk reads each key where the game's first-person camera reads its own.
        string core = (string)typeof(Refii.Titles.Fable2.CoopHeroLua)
            .GetField("CameraSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
        foreach (string applied in new[]
                 {
                     "tune.fp_view", "tune.fp_fov", "tune.fp_pitch_limit", "tune.fp_eye_forward", "tune.fp_eye_height", "tune.fp_face", "tune.fp_scenes",
                     "FirstPersonCamera.Activate = C.CamFirstPersonActivate", "FirstPersonCamera.ProcessGameAction = C.CamFirstPersonProcessGameAction",
                     "FirstPersonCamera.FrameUpdate = C.CamFirstPersonFrameUpdate", "set.BestCameraFunction = wrapped",
                     "FirstPersonCamera.GetDesiredPosFromPlayer(self.Player)", "C.CamOverrideFocus(data)", "Refii.in_cutscene(p, Refii.CUTSCENE_FULL)",
                 })
        {
            AssertThat(core.Contains(applied, StringComparison.Ordinal)).OverrideFailureMessage("the core camera chunk lacks " + applied).IsTrue();
        }

        // The first-person functions never call the game's fade themselves; the one mention is player two's fade book.
        AssertThat(core.Contains(":FadeEntityBasedOnPositionIfNeeded(", StringComparison.Ordinal)).IsFalse();
        AssertThat(core.Contains(".FadeEntityBasedOnPositionIfNeeded(", StringComparison.Ordinal)).IsFalse();
    }

    [TestCase]
    public void EveryEventHeardHasTheFieldsRead()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string code = GodotModScripts.Code(GodotModScripts.Script(root, Mod, File));
        GodotModScripts.AssertEvents(File, code,
            ("input_action", new[] { "name", "player", "pressed" }),
            ("cutscene", new[] { "player", "kind" }),
            ("hero_ready", Array.Empty<string>()),
            ("hero_gone", Array.Empty<string>()),
            ("lua_ready", Array.Empty<string>()));
        AssertThat(GodotModScripts.Signals(code).Contains("setting_changed")).IsTrue();
    }

    [TestCase]
    public void TheDataFilesValidateAndEverySettingIsRead()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        (int checkedFiles, var warnings) = GodotModScripts.AssertDataValidates(GodotModScripts.ModFolder(root, Mod));
        AssertThat(checkedFiles).IsEqual(4);   // input.json, settings.json, settings.ui.json, mod.toml
        AssertThat(warnings.Count).IsEqual(0);

        string code = GodotModScripts.Code(GodotModScripts.Script(root, Mod, File));
        string input = Read(root, "input.json");
        AssertThat(Regex.Matches(input, @"^\s*""([a-z0-9_]+)""\s*:", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList())
            .ContainsExactly("toggle");
        AssertThat(code.Contains("\"first_person.toggle\"", StringComparison.Ordinal)).IsTrue();

        string settings = Read(root, "settings.json");
        string page = Read(root, "settings.ui.json");
        var keys = Regex.Matches(settings, @"""([a-z_]+)"":").Select(m => m.Groups[1].Value).Where(k => k != Mod).ToList();
        AssertThat(keys).ContainsExactlyInAnyOrder("start_on", "fov", "eye_height", "eye_forward", "pitch_limit", "face_camera", "third_person_aim", "scenes");
        foreach (string key in keys)
        {
            AssertThat(code.Contains("_setting(\"" + key + "\"", StringComparison.Ordinal))
                .OverrideFailureMessage("never read: " + key).IsTrue();
            AssertThat(page.Contains("\"" + key + "\"", StringComparison.Ordinal)).OverrideFailureMessage("settings.ui.json lacks " + key).IsTrue();
        }

        // The hold's options are stated again when their setting changes.
        foreach (string key in new[] { "fov", "pitch_limit", "third_person_aim", "scenes", "eye_forward", "eye_height", "face_camera" })
        {
            AssertThat(code.Contains("\"first_person." + key + "\"", StringComparison.Ordinal)).OverrideFailureMessage("not restated on " + key).IsTrue();
        }

        AssertThat(Read(root, "README.md").Contains("one Godot script", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheCoreHidesAHeroHeadExactlyInItsPlayersStatedFirstPerson()
    {
        AssertThat(Engine_FirstPersonView.FirstPersonMode).IsEqual(ModApi.Constants.Single(c => c.Name == "CAMERA_FIRST_PERSON").Value);

        // A hero's player is its player-map key; anything else is no player.
        AssertThat(Engine_FirstPersonView.PlayerOf(0x40001000u, 0x40001000u, 0x40002000u)).IsEqual(1);
        AssertThat(Engine_FirstPersonView.PlayerOf(0x40002000u, 0x40001000u, 0x40002000u)).IsEqual(2);
        AssertThat(Engine_FirstPersonView.PlayerOf(0x40003000u, 0x40001000u, 0x40002000u)).IsEqual(0);
        AssertThat(Engine_FirstPersonView.PlayerOf(0u, 0u, 0u)).IsEqual(0);
        AssertThat(Engine_FirstPersonView.PlayerOf(0x40001000u, 0x40001000u, 0u)).IsEqual(1);

        // Hidden only under CAMERA_FIRST_PERSON stated for that hero's own player.
        AssertThat(Engine_FirstPersonView.HeadHidden(1, 8)).IsTrue();
        AssertThat(Engine_FirstPersonView.HeadHidden(2, 8)).IsTrue();
        foreach (int mode in new[] { -1, 0, 1, 2, 3, 4, 7, 9, 10, 11 })
        {
            AssertThat(Engine_FirstPersonView.HeadHidden(1, mode)).OverrideFailureMessage("hidden under mode " + mode).IsFalse();
        }

        AssertThat(Engine_FirstPersonView.HeadHidden(0, 8)).IsFalse();
        AssertThat(Engine_FirstPersonView.HeadHidden(3, 8)).IsFalse();
    }

    [TestCase]
    public void TheHeadHiderFollowsTheStatedModeNotADistance()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string hider = System.IO.File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "entities", "Engine_FirstPersonHead.cs"));
        AssertThat(hider.Contains("Engine_FirstPersonView.HeadHidden(_player, mode)", StringComparison.Ordinal)).IsTrue();
        AssertThat(hider.Contains("camera_mode(_player)", StringComparison.Ordinal)).IsTrue();
        AssertThat(hider.Contains("Radius", StringComparison.Ordinal)).IsFalse();
        AssertThat(hider.Contains("DistanceTo", StringComparison.Ordinal)).IsFalse();

        // The host hears camera_changed with or without a mod script loaded.
        string host = System.IO.File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs"));
        AssertThat(host.Contains("own = own.With(CameraChangedKind);", StringComparison.Ordinal)).IsTrue();

        string entities = System.IO.File.ReadAllText(Path.Combine(root, "Godot", "src", "game", "GameEntities.cs"));
        AssertThat(entities.Contains("Engine_FirstPersonView.PlayerOf(entity.Address, _principals.One, _principals.Two)", StringComparison.Ordinal)).IsTrue();
    }
}
