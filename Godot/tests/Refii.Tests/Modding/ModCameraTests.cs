namespace Refii.Tests.Modding;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Entities;
using Refii.Mods;
using Refii.Ui;

/// <summary>
/// The per-player camera (C.11) and hidden parts: camera_mode / camera_changed stated from the game
/// VM, the camera_override hold through the game's own CameraManager.SetCameraOverride kept out of
/// saves, fov, and Godot's hide_part — the pure rules here, and every surface named. No Godot runtime.
/// </summary>
[TestSuite]
public class ModCameraTests
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

    private static string Read(string root, params string[] path) => File.ReadAllText(Path.Combine(new[] { root }.Concat(path).ToArray()));

    [TestCase]
    public void TheCameraEventIsInTheCatalog()
    {
        ModApiEvent changed = ModApi.Find("camera_changed")!;
        AssertThat(changed.Kind).IsEqual(112);
        AssertThat(changed.Name).IsEqual("CameraChanged");
        AssertThat(string.Join(",", changed.Fields.Select(f => f.Name))).IsEqual("player,mode,previous,hero");
        AssertThat(changed.Cancellable).IsFalse();
        AssertThat(changed.Fields.Any(f => f.Writable)).IsFalse();
        AssertThat(RefiiHost.SignalNames.Contains("camera_changed")).IsTrue();

        // The game's ECameraMode (miscellaneous/cameramodeenum.lua).
        AssertThat(ModApi.Constants.Single(c => c.Name == "CAMERA_GAME").Value).IsEqual(2);
        AssertThat(ModApi.Constants.Single(c => c.Name == "CAMERA_ICS").Value).IsEqual(7);
        AssertThat(ModApi.Constants.Single(c => c.Name == "CAMERA_FIRST_PERSON").Value).IsEqual(8);
        AssertThat(ModApi.Constants.Single(c => c.Name == "CAMERA_CANNED_ACTION").Value).IsEqual(10);
        AssertThat(ModApi.Constants.Single(c => c.Name == "CAMERA_FREEZE").Value).IsEqual(14);

        foreach (string signature in new[] { "refii_mod_camera_mode(", "refii_mod_camera_override(", "refii_mod_camera_fov(", "refii_mod_hide_part(", "refii_mod_hide_part_for(" })
        {
            AssertThat(ModApi.Helpers.Any(h => h.Signature.Contains(signature, StringComparison.Ordinal))).OverrideFailureMessage(signature).IsTrue();
        }

        foreach (string signature in new[] { "Refii.camera_mode(", "Refii.camera_override(", "Refii.fov(", "Refii.hide_part(e, part, on [, p])" })
        {
            AssertThat(ModApi.LuaHelpers.Any(h => h.Signature.Contains(signature, StringComparison.Ordinal))).OverrideFailureMessage(signature).IsTrue();
        }

        foreach (string signature in new[] { "camera_mode(player)", "camera_override(player", "fov(player)", "hide_part(handle, part, on, player = 0)" })
        {
            AssertThat(ModApi.GodotHelpers.Any(h => h.Signature.Contains(signature, StringComparison.Ordinal))).OverrideFailureMessage(signature).IsTrue();
        }
    }

    [TestCase]
    public void TheCameraIsOnEverySurface()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string events = Read(root, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h");
        AssertThat(events.Contains("REFII_EVENT_CAMERA_CHANGED = 112,", StringComparison.Ordinal)).IsTrue();
        AssertThat(events.Contains("struct CameraChangedEvent : Event {", StringComparison.Ordinal)).IsTrue();
        string bus = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp");
        AssertThat(bus.Contains("\"player=%d,mode=%d,previous=%d,hero=%u\"", StringComparison.Ordinal)).IsTrue();

        string header = (Read(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h") + Read(root, "src", "runtime", "include", "core", "refii_mod_requests.h"));
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_camera_mode(int32_t player);", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("#define REFII_CAMERA_FIRST_PERSON 8", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("#define REFII_CAMERA_HOLD_AIM 1", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("extern \"C\" float refii_mod_camera_fov(uint8_t* base, int32_t player);", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_hide_part(uint32_t entity, const char* part, int32_t on);", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_hide_part_for(uint32_t entity, const char* part, int32_t on, int32_t player);", StringComparison.Ordinal)).IsTrue();

        string natives = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp");
        foreach (string entry in new[]
                 {
                     "{\"camera_mode\", &NativeCameraMode, 0}", "{\"_camera_override\", &NativeCameraOverride, 0}",
                     "{\"fov\", &NativeCameraFov, 0}", "{\"_camera_stated\", &NativeCameraStated, 0}",
                     "{\"_camera_hold\", &NativeCameraHold, 0}", "{\"_camera_held\", &NativeCameraHeld, 0}",
                     "{\"hide_part\", &NativeHidePart, 0}",
                 })
        {
            AssertThat(natives.Contains(entry, StringComparison.Ordinal)).OverrideFailureMessage(entry).IsTrue();
        }

        string commands = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp");
        AssertThat(commands.Contains("op == \"camera_override\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("what == \"camera_mode\"", StringComparison.Ordinal)).IsTrue();
        // The live block's FOV: `lfs f0,0x30(r3); stfs f0,0xC0(r31)`, manager+0x90 +0x30.
        AssertThat(commands.Contains("manager + 0xC0u", StringComparison.Ordinal)).IsTrue();

        // A held LookAt half is a mod's camera, not a scene's.
        string api = (Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp") + Read(root, "src", "payload", "src", "core", "mod_requests.cpp"));
        AssertThat(api.Contains("(refii::vfs::ModCameraHeldBits(player) & 2) == 0", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("op='hide_part',entity=", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("\",player=\" + std::to_string(player)", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("return refii_mod_hide_part_for(entity, part, on, 0);", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("refii_mod_hide_part_for(EntityArgument(lua, 1), part, lua.Boolean(3) ? 1 : 0, lua.Int(4))", StringComparison.Ordinal)).IsTrue();

        // Holds go with the VM: cleared before a session's chunks run.
        string hooks = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_hooks.cpp");
        int clear = hooks.IndexOf("refii::vfs::ModCameraClearHolds();", StringComparison.Ordinal);
        int init = hooks.IndexOf("RunComposedChunk(work, base, state, kInitKey);", StringComparison.Ordinal);
        AssertThat(clear >= 0 && init > clear).IsTrue();

        string preamble = Read(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs");
        AssertThat(preamble.Contains("function Refii.camera_override(p, mode, o)", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModLua.InfrastructureRuntime().Contains("Refii.CAMERA_FIRST_PERSON = 8\n", StringComparison.Ordinal)).IsTrue();

        string host = Read(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs");
        foreach (string member in new[]
                 {
                     "public int camera_mode(long player)", "public void camera_override(int player, int mode, Godot.Collections.Dictionary options)",
                     "public float fov(int player)", "public bool hide_part(long handle, string part, bool on)",
                     "public bool hide_part(long handle, string part, bool on, int player)",
                     "Refii.Entities.Engine_HiddenParts.Clear();", "query(\"camera_mode\"",
                 })
        {
            AssertThat(host.Contains(member, StringComparison.Ordinal)).OverrideFailureMessage(member).IsTrue();
        }

        string reference = Read(root, "Godot", "docs", "modding", "reference-events.md");
        AssertThat(reference.Contains("| 112 | CameraChanged / camera_changed |", StringComparison.Ordinal)).IsTrue();
        string chapter = Read(root, "Godot", "docs", "modding", "cutscenes.md");
        AssertThat(chapter.Contains("## The camera", StringComparison.Ordinal)).IsTrue();
        AssertThat(chapter.Contains("## Hidden parts", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheCoreCameraChunkStatesTheModeAndHoldsThroughTheGamesOverride()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string core = Read(root, "Godot", "src", "Titles", "Fable2", "host", "CoopHeroLua.cs");
        // Around every CameraManager:FrameUpdate(hero): the hold before, the statement after, on all three paths
        // (unswapped, local co-op player one on the shipped fields, the swapped per-hero state).
        AssertThat(core.Contains("pcall(C.CameraHold, hero)", StringComparison.Ordinal)).IsTrue();
        AssertThat(core.Split("pcall(C.CameraStated, hero, self.CurrentCamera)").Length - 1).IsEqual(3);
        // The mode is the override set's key for the active camera.
        AssertThat(core.Contains("set.OverridesCameraValidity and type(set.CameraModes) == \"table\"", StringComparison.Ordinal)).IsTrue();
        // The game's own per-player hold, with the data its cameras read.
        AssertThat(core.Contains("CameraManager.SetCameraOverride(hero, hold.Mode, C.CameraScope(claim), hold.Data)", StringComparison.Ordinal)).IsTrue();
        AssertThat(core.Contains("local data = { SupercedesCombat = true }", StringComparison.Ordinal)).IsTrue();
        AssertThat(core.Contains("data.BoundAngleVert = 0", StringComparison.Ordinal)).IsTrue();
        AssertThat(core.Contains("CannedActionCamera.Active", StringComparison.Ordinal)).IsTrue();
        AssertThat(core.Contains("RangedCombatCamera.IsValid(hero)", StringComparison.Ordinal)).IsTrue();
        // Never saved: the core save wrapper rewrites the written copy of CameraSave.Overrides.
        AssertThat(core.Contains("pcall(C.CameraSaveTable, t)", StringComparison.Ordinal)).IsTrue();
        AssertThat(core.Contains("save.Overrides = copy", StringComparison.Ordinal)).IsTrue();
        int strip = core.IndexOf("pcall(C.CameraSaveTable, t)", StringComparison.Ordinal);
        int wrapper = core.IndexOf("GetSaveTable = function(...)", StringComparison.Ordinal);
        AssertThat(wrapper >= 0 && strip > wrapper).IsTrue();
    }

    [TestCase]
    public void HiddenPartsAreKeptPerHandle()
    {
        Engine_HiddenParts.Clear();
        int before = Engine_HiddenParts.Revision;
        AssertThat(Engine_HiddenParts.Set(0, "Shadow_Neck", true)).IsFalse();
        AssertThat(Engine_HiddenParts.Set(7, "  ", true)).IsFalse();
        AssertThat(Engine_HiddenParts.Count).IsEqual(0);

        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", true)).IsTrue();
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", true)).IsTrue();
        AssertThat(Engine_HiddenParts.Set(9, "Shadow_Head", true)).IsTrue();
        AssertThat(Engine_HiddenParts.Count).IsEqual(2);
        AssertThat(Engine_HiddenParts.Of(7)).ContainsExactly("Shadow_Neck");
        AssertThat(Engine_HiddenParts.Handles().OrderBy(h => h).ToArray()).ContainsExactly(7u, 9u);
        AssertThat(Engine_HiddenParts.Revision > before).IsTrue();

        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", false)).IsTrue();
        AssertThat(Engine_HiddenParts.Of(7).Length).IsEqual(0);
        AssertThat(Engine_HiddenParts.Count).IsEqual(1);

        Engine_HiddenParts.Clear();
        AssertThat(Engine_HiddenParts.Count).IsEqual(0);
        AssertThat(Engine_HiddenParts.Of(9).Length).IsEqual(0);
    }

    [TestCase]
    public void HiddenPartsAreKeptPerPlayerView()
    {
        Engine_HiddenParts.Clear();
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", true, 3)).IsFalse();
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", true, -1)).IsFalse();
        AssertThat(Engine_HiddenParts.Count).IsEqual(0);

        // Player one's view alone: not an every-view part.
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Head", true, 1)).IsTrue();
        AssertThat(Engine_HiddenParts.Count).IsEqual(1);
        AssertThat(Engine_HiddenParts.Of(7).Length).IsEqual(0);
        AssertThat(Engine_HiddenParts.Of(7, 1)).ContainsExactly("Shadow_Head");
        AssertThat(Engine_HiddenParts.Of(7, 2).Length).IsEqual(0);

        // Each part and player is its own statement: the every-view hide outlives player one's show.
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", true)).IsTrue();
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", true, 2)).IsTrue();
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", false, 1)).IsTrue();
        AssertThat(Engine_HiddenParts.Of(7)).ContainsExactly("Shadow_Neck");
        AssertThat(Engine_HiddenParts.Of(7, 0)).ContainsExactly("Shadow_Neck");
        AssertThat(Engine_HiddenParts.Of(7, 2)).ContainsExactly("Shadow_Neck");
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", false)).IsTrue();
        AssertThat(Engine_HiddenParts.Of(7).Length).IsEqual(0);
        AssertThat(Engine_HiddenParts.Of(7, 2)).ContainsExactly("Shadow_Neck");

        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", false, 2)).IsTrue();
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Head", false, 1)).IsTrue();
        AssertThat(Engine_HiddenParts.Count).IsEqual(0);
        AssertThat(Engine_HiddenParts.Handles().Length).IsEqual(0);
        Engine_HiddenParts.Clear();
    }

    [TestCase]
    public void AHidePartRequestCrossesTheUiKind()
    {
        Engine_UiModRequest.Request? request = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hide_part',entity=4097,part='Shadow_Neck',on=1"));
        AssertThat(request.HasValue).IsTrue();
        AssertThat(request!.Value.Op).IsEqual(Engine_UiModRequest.Op.HidePart);
        AssertThat(request.Value.Entity).IsEqual(4097u);
        AssertThat(request.Value.Name).IsEqual("Shadow_Neck");
        AssertThat(request.Value.On).IsTrue();
        AssertThat(request.Value.Player).IsEqual(0);

        // The player whose view loses it; 0 is every view.
        Engine_UiModRequest.Request? mine = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hide_part',entity=4097,part='Shadow_Neck',on=1,player=2"));
        AssertThat(mine!.Value.Player).IsEqual(2);
        AssertThat(Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hide_part',entity=4097,part='Shadow_Neck',on=1,player=0"))!.Value.Player).IsEqual(0);

        Engine_UiModRequest.Request? off = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hide_part',entity=4097,part='Shadow_Neck',on=0"));
        AssertThat(off!.Value.On).IsFalse();
        AssertThat(Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hide_part',entity=0,part='Shadow_Neck',on=1")).HasValue).IsFalse();
        AssertThat(Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hide_part',entity=5,part='',on=1")).HasValue).IsFalse();
    }

    private static readonly string[] ShapeCalls =
    {
        "camera_tune", "camera_frame", "camera_focus", "camera_combat_blend", "camera_interest", "camera_pitch_hold",
        "camera_lock_frame", "camera_mode_allow", "camera_offset", "camera_shake",
    };

    /// <summary>
    /// The camera's shape calls (C2b): one list on the three surfaces, stated into the game VM's store
    /// by the shared preamble and applied by each title's core camera chunk.
    /// </summary>
    [TestCase]
    public void TheShapeCallsAreOnEverySurface()
    {
        AssertThat(ModLuaCamera.Calls.ToList()).IsEqual(ShapeCalls.ToList());
        string runtime = ModLuaCamera.Runtime;
        AssertThat(runtime.Contains("Refii._camera_calls = { " + string.Join(", ", ShapeCalls.Select(c => "'" + c + "'")) + " }", StringComparison.Ordinal)).IsTrue();
        foreach (string call in ShapeCalls)
        {
            AssertThat(runtime.Contains("function Refii._camera_do." + call + "(owner, p", StringComparison.Ordinal)).OverrideFailureMessage(call).IsTrue();
            AssertThat(ModApi.LuaHelpers.Any(h => h.Signature.StartsWith("Refii." + call + "(p", StringComparison.Ordinal))).OverrideFailureMessage("Lua " + call).IsTrue();
            AssertThat(ModApi.GodotHelpers.Any(h => h.Signature.StartsWith(call + "(player", StringComparison.Ordinal))).OverrideFailureMessage("Godot " + call).IsTrue();
            AssertThat(typeof(RefiiHost).GetMethods().Any(m => m.Name == call)).OverrideFailureMessage("RefiiHost." + call).IsTrue();
        }

        AssertThat(ModApi.Helpers.Any(h => h.Signature.Contains("refii_mod_camera_state(", StringComparison.Ordinal))).IsTrue();
        AssertThat(ModLua.RuntimePreamble(Array.Empty<(string, float)>()).Contains(runtime, StringComparison.Ordinal)).IsTrue();
        // A mod's block states as that mod: its view carries the calls with the mod as owner.
        AssertThat(ModLua.RequireRuntime.Contains("if Refii._camera_view ~= nil then Refii._camera_view(view, mod) end", StringComparison.Ordinal)).IsTrue();
        AssertThat(runtime.Contains("rawset(view, name, function(...) return Refii._camera_call(mod, name, ...) end)", StringComparison.Ordinal)).IsTrue();

        // Fable 3's VM walks pairs only with two loop variables (Fable3CoreLua's header): the preamble
        // both titles run never loops with one.
        AssertThat(Regex.IsMatch(runtime, @"for\s+\w+\s+in\s+pairs\(")).IsFalse();
        // The store holds plain values under Refii, a permanent written by name: no entity, no function.
        AssertThat(runtime.Contains("Refii._cam = { { layers = {} }, { layers = {} }", StringComparison.Ordinal)).IsTrue();

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string header = Read(root, "src", "runtime", "include", "core", "refii_mod_requests.h");
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_camera_state(PPCContext* ctx, uint8_t* base, int32_t player, const char* call,", StringComparison.Ordinal)).IsTrue();
        string requests = Read(root, "src", "payload", "src", "core", "mod_requests.cpp");
        AssertThat(requests.Contains("Refii._camera_call('native','", StringComparison.Ordinal)).IsTrue();

        string host = Read(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs");
        AssertThat(host.Contains("if Refii and Refii._camera_call then Refii._camera_call(", StringComparison.Ordinal)).IsTrue();
        AssertThat(host.Contains("ModLuaLane.Literal(\"godot:\" + (owner ?? string.Empty))", StringComparison.Ordinal)).IsTrue();

        // Each title's core camera chunk applies what is stated, and is the shipped function with nothing stated.
        string f2 = Read(root, "Godot", "src", "Titles", "Fable2", "host", "CoopHeroLua.cs");
        foreach (string applied in new[]
                 {
                     "SACCamera.CalculateActiveZoomLevel = C.CamCalculateActiveZoomLevel", "SACCamera.GetDesiredCombatCamWeight = C.CamGetDesiredCombatCamWeight",
                     "SACCamera.CalculateCentreAndCage = C.CamCalculateCentreAndCage", "SACCamera.GetHeight = C.CamGetHeight",
                     "SACCamera.CalculateDesiredDirection = C.CamCalculateDesiredDirection", "RangedCombatCamera.GetTightness = C.CamGetTightness",
                     "RangedCombatCamera.GetOffsetDegrees = C.CamGetOffsetDegrees", "Refii._camera_changed = C.CamChanged",
                     "return not CameraFunctions.IsMultiplayer(hero)", "pcall(C.CamTuneFor, hero)",
                 })
        {
            AssertThat(f2.Contains(applied, StringComparison.Ordinal)).OverrideFailureMessage("Fable 2 lacks " + applied).IsTrue();
        }

        string f3 = Read(root, "Godot", "src", "Titles", "Fable3", "Fable3CoreLua.cs");
        foreach (string applied in new[]
                 {
                     "SACCamera.CalculateActiveZoomLevel = function(self, ...)", "SACCamera.GetDesiredCombatCamWeight = function(self, ...)",
                     "SACCamera.CalculateCentreAndCage = function(self, ...)", "SACCamera.GetHeight = function(self, current_pos, centre, cage_radius, ...)",
                     "SACCamera.CalculateDesiredDirection = function(self, ...)", "function Refii._camera_changed(p)",
                     "return not CameraFunctions.IsMultiplayerSharedScreen(hero)", "pcall(Refii._camapi.TuneFor, player)",
                     "\"SlotDepthNear\", \"SlotDepthFar\"", "if Refii._wrap_camera_api ~= nil then Refii._wrap_camera_api() end",
                 })
        {
            AssertThat(f3.Contains(applied, StringComparison.Ordinal)).OverrideFailureMessage("Fable 3 lacks " + applied).IsTrue();
        }

        int from = f3.IndexOf("local A = Refii._camapi or {}", StringComparison.Ordinal);
        int to = f3.IndexOf("Refii._wrap_camera_api = A.Wrap", StringComparison.Ordinal);
        AssertThat(from > 0 && to > from).IsTrue();
        AssertThat(Regex.IsMatch(f3[from..to], @"for\s+\w+\s+in\s+pairs\(")).IsFalse();

        string reference = Read(root, "Godot", "docs", "modding", "reference-calls.md");
        string chapter = Read(root, "Godot", "docs", "modding", "cutscenes.md");
        AssertThat(chapter.Contains("## Shaping the camera", StringComparison.Ordinal)).IsTrue();
        foreach (string call in ShapeCalls)
        {
            AssertThat(reference.Contains("`Refii." + call + "(p", StringComparison.Ordinal)).OverrideFailureMessage("reference-calls.md lacks Refii." + call).IsTrue();
            AssertThat(chapter.Contains("`Refii." + call + "(p", StringComparison.Ordinal)).OverrideFailureMessage("cutscenes.md lacks Refii." + call).IsTrue();
        }
    }

    /// <summary>
    /// camera_shake (cam3 left it out): the game's own screen shake, which the game gives to player one's
    /// camera only (Fable 2 sub_823EB468 asks the getter sub_821F87C8, `bl` 0x823EB4A4, for the manager whose
    /// +0x48 list takes it; Fable 3 sub_8243B330 the selector sub_82208EA0, manager +0x4C), so player two's
    /// call runs inside player two's camera bracket on both titles.
    /// </summary>
    [TestCase]
    public void AShakeForPlayerTwoRunsInsidePlayerTwosBracket()
    {
        string runtime = ModLuaCamera.Runtime;
        int at = runtime.IndexOf("function Refii._camera_do.camera_shake(owner, p, t)", StringComparison.Ordinal);
        AssertThat(at > 0).IsTrue();
        string body = runtime.Substring(at, runtime.IndexOf("\nend\n", at, StringComparison.Ordinal) - at);
        string[] steps =
        {
            "if Refii.camera_pose == nil or Refii.camera_pose(p) == nil then return false end",
            "Refii.entity(Refii.player_hero(1))",
            "call, spec = Player.FadeScreenShake, { ID = kind, DecayTime = t.fade }",
            "call, spec = Player.AddScreenShake, { ID = kind, MaxLevel = n(t.level, 1)",
            "local partner = p == 2 and Refii._partner_screen ~= nil",
            "if partner then Refii._partner_screen(true) end",
            "local ok = pcall(call, hero, spec)",
            "if partner then Refii._partner_screen(false) end",
        };
        int last = -1;
        foreach (string step in steps)
        {
            int found = body.IndexOf(step, last + 1, StringComparison.Ordinal);
            AssertThat(found > last).OverrideFailureMessage(step).IsTrue();
            last = found;
        }

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string f2 = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp");
        AssertThat(f2.Contains("{\"_partner_screen\", &NativePartnerScreen, 0}", StringComparison.Ordinal)).IsTrue();
        string f2Getter = Read(root, "Fable2", "src", "hooks", "src", "coop_camera_readers.cpp");
        AssertThat(f2Getter.Contains("if (RefiiCoopPartnerScreenDepth() > 0 && RefiiCoopSplitScreen() != 0) {", StringComparison.Ordinal)).IsTrue();
        string f3 = Read(root, "Fable3", "src", "hooks", "src", "mod_lua_hooks.cpp");
        AssertThat(f3.Contains("{\"_partner_screen\", &NativePartnerScreen, 0u}", StringComparison.Ordinal)).IsTrue();
        string f3Hooks = Read(root, "Fable3", "src", "hooks", "src", "coop_camera_hooks.cpp");
        int screen = f3Hooks.IndexOf("extern \"C\" void RefiiF3PartnerScreen(int32_t on) {", StringComparison.Ordinal);
        AssertThat(screen > 0).IsTrue();
        string f3Body = f3Hooks.Substring(screen, f3Hooks.IndexOf("\n}\n", screen, StringComparison.Ordinal) - screen);
        AssertThat(f3Body.Contains("const bool enter = refii::fable3::coopcam::SplitInEffect(GuestBase());", StringComparison.Ordinal)).IsTrue();
        AssertThat(f3Body.Contains("if (enter) {\n        ++t_partner_depth;", StringComparison.Ordinal)).IsTrue();
        string host = Read(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs");
        AssertThat(host.Contains("public bool camera_shake(int player, Variant values) => CameraCall(\"camera_shake\", player, null, Plain(values));", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// Fable 3 answers what Fable 2 answers: camera_focus (the centre slid, as C.CamSlid), the ranged_* keys
    /// (its RangedCombatCamera: AngleLimit, GetTightness, GetOffsetDegrees) and the held first-person view's
    /// fp_* keys over its own FirstPersonCamera (only GAME_ACTION_CAMERA_ROTATION turns it).
    /// </summary>
    [TestCase]
    public void Fable3AnswersFocusTheGunAimAndTheHeldFirstPersonView()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string f3 = Read(root, "Godot", "src", "Titles", "Fable3", "Fable3CoreLua.cs");
        foreach (string line in new[]
                 {
                     "local focus = state ~= nil and state.focus or nil",
                     "return slid(self, centre, slide), cage",
                     "RangedCombatCamera.GetTightness = function(self, ...)",
                     "self.AngleLimit = limit * math.pi / 180",
                     "RangedCombatCamera.GetOffsetDegrees = function(self, ...)",
                     "FirstPersonCamera.Activate = function(self, hero, camera, previous_camera, ...)",
                     "if action == EGameAction.GAME_ACTION_CAMERA_ROTATION then",
                     "FirstPersonCamera.VerticalAngularBound = tune.fp_pitch_limit or 75",
                     "if tune ~= nil and tune.fp_scenes ~= nil and tune.fp_scenes ~= 0 then",
                 })
        {
            AssertThat(f3.Contains(line, StringComparison.Ordinal)).OverrideFailureMessage("Fable 3 lacks " + line).IsTrue();
        }

        AssertThat(ModApi.CameraShapeMeaning.Contains("does not answer", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// A camera_override hold never leaves its mode in the pair the hero's Player component saves
    /// (OverrideCameraModeNormal / OverrideCameraModeLookAt): each of the hold's own SetCameraOverride
    /// calls is bracketed by a read of the pair and a write of it back, on both titles.
    /// </summary>
    [TestCase]
    public void TheHoldNeverLeavesItsModeInTheSavedPair()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string f2 = Read(root, "Godot", "src", "Titles", "Fable2", "host", "CoopHeroLua.cs");
        AssertThat(Regex.Matches(f2, @"local normal, look_at = C\.CameraNative\(hero\)\s*\n\s*CameraManager\.SetCameraOverride\(hero, [^\n]*\)\s*\n\s*C\.CameraNativeRestore\(hero, normal, look_at\)").Count).IsEqual(2);
        AssertThat(f2.Contains("Player.SetOverrideCameraMode(hero, normal, scope.CUTSCENE_RULE_SCOPE_NORMAL)", StringComparison.Ordinal)).IsTrue();
        AssertThat(f2.Contains("Player.SetOverrideCameraMode(hero, look_at, scope.CUTSCENE_RULE_SCOPE_LOOK_AT)", StringComparison.Ordinal)).IsTrue();
        AssertThat(f2.Contains("Player.GetOverrideCameraMode = C.GetOverrideCameraMode", StringComparison.Ordinal)).IsTrue();

        string f3 = Read(root, "Godot", "src", "Titles", "Fable3", "Fable3CoreLua.cs");
        AssertThat(Regex.Matches(f3, @"local kept, normal, look_at = pcall\(CameraNative, hero\)\s*\n\s*CameraManager\.SetCameraOverride\(hero, [^\n]*\)\s*\n\s*if kept and Refii\._camera_native_restore ~= nil then Refii\._camera_native_restore\(hero, normal, look_at\) end").Count).IsEqual(2);

        // Fable 3's setter also sends the override to the peer, so the pair is stored back natively: both
        // words of the Player component, the offsets of both builds.
        string natives = Read(root, "Fable3", "src", "hooks", "src", "mod_lua_hooks.cpp");
        AssertThat(natives.Contains("{\"_camera_native_restore\", &NativeCameraNativeRestore, 0u}", StringComparison.Ordinal)).IsTrue();
        string combat = Read(root, "Fable3", "src", "hooks", "src", "mod_combat_hooks.cpp");
        AssertThat(combat.Contains("constexpr uint32_t kPlayerNormalCamera = 0xC0u;", StringComparison.Ordinal)).IsTrue();
        AssertThat(combat.Contains("StoreU32(base, component + kPlayerNormalCamera, normal);", StringComparison.Ordinal)).IsTrue();
        AssertThat(combat.Contains("StoreU32(base, component + kPlayerLookAtCamera, look_at);", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// THE READ CALLS AND THE CAMERA EVENTS on every surface: Lua (the preamble over each title's
    /// Refii._camera_read), Godot (RefiiHost), native (refii_mod_requests.h), the catalog, the docs; the
    /// events in both titles' buses; the views and statements in the shared runtime; ABI 272 both ways.
    /// </summary>
    [TestCase]
    public void TheReadCallsAndEventsAreOnEverySurface()
    {
        string runtime = ModLuaCamera.Runtime;
        foreach (string read in ModLuaCamera.Reads)
        {
            AssertThat(runtime.Contains("function Refii." + read + "(", StringComparison.Ordinal)).OverrideFailureMessage("preamble " + read).IsTrue();
            AssertThat(ModApi.LuaHelpers.Any(h => h.Signature.Contains("Refii." + read + "(", StringComparison.Ordinal))).OverrideFailureMessage("Lua " + read).IsTrue();
            AssertThat(ModApi.GodotHelpers.Any(h => h.Signature.Contains(read + "(", StringComparison.Ordinal))).OverrideFailureMessage("Godot " + read).IsTrue();
            AssertThat(typeof(RefiiHost).GetMethods().Any(m => m.Name == read)).OverrideFailureMessage("RefiiHost." + read).IsTrue();
        }

        AssertThat(Regex.IsMatch(runtime, @"for\s+\w+\s+in\s+pairs\(")).IsFalse();
        // The offset is a statement of the store and goes to the shell through the title's native.
        AssertThat(runtime.Contains("s.offset = top.offset ~= nil and top.offset.value or nil", StringComparison.Ordinal)).IsTrue();
        AssertThat(runtime.Contains("Refii._camera_offset_stated(p, true, o.right, o.up, o.back, o.yaw, o.pitch, o.roll, o.fov)", StringComparison.Ordinal)).IsTrue();

        string[] kinds = { "camera_cut", "camera_blend", "camera_screen_changed", "camera_zoom_changed", "camera_tick" };
        for (int i = 0; i < kinds.Length; i++)
        {
            ModApiEvent e = ModApi.Find(kinds[i])!;
            AssertThat(e.Kind).IsEqual(164 + i);
            AssertThat(e.Cancellable).IsFalse();
            AssertThat(RefiiHost.SignalNames.Contains(kinds[i])).IsTrue();
            AssertThat(ModApi.Fable3Raised.ContainsKey(e.Kind)).IsTrue();
        }

        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string requests = Read(root, "src", "runtime", "include", "core", "refii_mod_requests.h");
        foreach (string native in new[]
                 {
                     "extern \"C\" int32_t refii_mod_camera_pose(int32_t player, float* out12);",
                     "extern \"C\" int32_t refii_mod_camera_stated(int32_t player, float* out11);",
                     "extern \"C\" int32_t refii_mod_camera_view(int32_t player, float* out4);",
                     "extern \"C\" int32_t refii_mod_camera_screen_mode(int32_t* split);",
                     "extern \"C\" int32_t refii_mod_camera_ray(int32_t player, float x, float y, float* out6);",
                 })
        {
            AssertThat(requests.Contains(native, StringComparison.Ordinal)).OverrideFailureMessage(native).IsTrue();
        }

        foreach (string title in new[] { Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"), Read(root, "Fable3", "src", "hooks", "src", "mod_lua_hooks.cpp") })
        {
            foreach (string native in new[] { "\"_camera_read\", &NativeCameraRead", "\"_camera_zoom_stated\", &NativeCameraZoomStated", "\"_camera_offset_stated\", &NativeCameraOffsetStated" })
            {
                AssertThat(title.Contains(native, StringComparison.Ordinal)).OverrideFailureMessage(native).IsTrue();
            }
        }

        string f2bus = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp");
        int dispatch = f2bus.IndexOf("  DispatchCameraEvents(ctx, base);", StringComparison.Ordinal);
        int tick = f2bus.IndexOf("  TickEvent tick{};", StringComparison.Ordinal);
        AssertThat(dispatch > 0 && tick > dispatch).OverrideFailureMessage("the camera events are raised before the Tick event").IsTrue();
        string f3 = Read(root, "Fable3", "src", "hooks", "src", "mod_lua_hooks.cpp");
        int raise = f3.IndexOf("  RaiseCameraEvents(lua);", StringComparison.Ordinal);
        AssertThat(raise > 0 && f3.IndexOf("Raise(lua, kEventTick, fields);", raise, StringComparison.Ordinal) > raise).IsTrue();

        // The shared runtime keys a block to its manager's push and reads the events off the records.
        string view = Read(root, "src", "runtime", "src", "core", "stated_view.cpp");
        AssertThat(view.Contains("TakeStatement(slot);", StringComparison.Ordinal)).IsTrue();
        string layout = Read(root, "src", "runtime", "include", "core", "camera_views.h");
        AssertThat(layout.Contains("constexpr uint32_t kViewWords = " + Engine_CameraViewsWords.View + "u;", StringComparison.Ordinal)).IsTrue();
        AssertThat(layout.Contains("constexpr uint32_t kViewsWords = 2u + 2u * kViewWords;", StringComparison.Ordinal)).IsTrue();

        string exports = Read(root, "src", "hostcore", "src", "host_exports.cpp");
        AssertThat(exports.Contains("REFII_HOST_ABI_EXPORT void RefiiHostStateCameraViews(const float* words, uint32_t count)", StringComparison.Ordinal)).IsTrue();
        AssertThat(exports.Contains("REFII_HOST_ABI_EXPORT int32_t RefiiHostGetCameraOffset(int32_t player, float* out7)", StringComparison.Ordinal)).IsTrue();
        AssertThat(Refii.Host.HostBridge.ExpectedAbiVersion).IsGreaterEqual(273);

        string chapter = Read(root, "Godot", "docs", "modding", "cutscenes.md");
        AssertThat(chapter.Contains("## Reading the camera", StringComparison.Ordinal)).IsTrue();
        string reference = Read(root, "Godot", "docs", "modding", "reference-calls.md");
        foreach (string read in ModLuaCamera.Reads)
        {
            AssertThat(reference.Contains("`Refii." + read + "(", StringComparison.Ordinal)).OverrideFailureMessage("reference-calls.md lacks Refii." + read).IsTrue();
        }

    }

    private static class Engine_CameraViewsWords
    {
        public const int View = Refii.Camera.Engine_CameraViews.ViewWords;
    }
}
