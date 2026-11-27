namespace Refii.Tests.Camera;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// Camera stage C2d, Fable 2: player two's camera manager in every local co-op game, and the guards that
/// keep the save the one a single-player game writes at that spot: no camera script at its construction,
/// player one's CameraPosition kept across player two's update, no leash or canopy statement from player
/// two's camera, player two's own default override record left out of the written save. "Player two's
/// update" is the whole native update, whose second Lua call (the active mode's FrameUpdate) is the one
/// that writes CameraPosition and states the leash and canopy. Read from the source; the run is the co-op
/// camera save gate (NOTES.md).
/// </summary>
[TestSuite]
public class PartnerCameraManagerTests
{
	private static string? RepoRoot()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null; i++)
		{
			if (File.Exists(Path.Combine(at, "Fable2", "versions", "Fable2TU1", "refii_config.toml")))
			{
				return at;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	[TestCase]
	public void TheManagerIsBuiltInLocalCoopAndRunsNoScript()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string hooks = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "coop_hero_hooks.cpp"));
		AssertThat(hooks.Contains("const bool build = base != nullptr && LocalCoopGame(base);", StringComparison.Ordinal)).IsTrue();
		AssertThat(hooks.Contains("if (g_ticking_manager != 0u || RefiiCoopPartnerCameraManager() == 0) {", StringComparison.Ordinal)).IsTrue();
		AssertThat(hooks.Contains("refii::vfs::LoadGuestU32(base, player + 0x24u) != 2u || ManagerForKey(base, 1u) == 0u", StringComparison.Ordinal)).IsTrue();
		foreach ((string build, string at, string to) in new[] { ("Fable2TU1", "0x82481568", "0x8248156C"), ("Fable2", "0x82422D48", "0x82422D4C") })
		{
			string config = File.ReadAllText(Path.Combine(root, "Fable2", "versions", build, "refii_config.toml"));
			AssertThat(config.Contains("name = \"RefiiCoopPartnerCameraScripts\"\naddress = " + at + "\nregisters = [\"r31\"]\njump_address_on_true = " + to + "\n", StringComparison.Ordinal))
				.OverrideFailureMessage(build).IsTrue();
		}
	}

	[TestCase]
	public void PlayerTwosCameraLeavesTheSaveAndTheSharedScreenAlone()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string lua = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "host", "CoopHeroLua.cs"));
		foreach (string rule in new[]
				 {
					 "local position, primed = CameraManager.CameraPosition, CameraManager.PrimedFromLoad",
					 "CameraManager.CameraPosition, CameraManager.PrimedFromLoad = position, primed",
					 "local partner = local_two and not C.IsHeroOne(hero)",
					 "if key == nil or not (local_two or C.OwnCameras()) then",
					 "if local_two and not partner then\n\t\t\t\t\tlocal result = shipped(self, hero, ...)",
					 "local manager = partner and C.PartnerCameraManager() or 0",
					 "if state == nil or state.Manager ~= manager then",
					 "state = partner and { Manager = manager } or { Manager = manager, Set = self.CurrentSet, Camera = self.CurrentCamera, Priority = self.CurrentSetPriority }",
					 "Camera.EnableTreeCanopyFading = C.EnableTreeCanopyFading",
					 "function C.PartnerDefaultRecord(overrides)",
					 "if id == partner then",
				 })
		{
			AssertThat(lua.Contains(rule, StringComparison.Ordinal)).OverrideFailureMessage(rule).IsTrue();
		}

		// The leash and the canopy are stated by player one's camera alone, asked of the native for the whole
		// update (they are stated from the mode's FrameUpdate, after the selection call returns).
		foreach (string wrapper in new[] { "C.SetMaxCoopDistance = C.Permanent(function(self, ...)", "C.EnableTreeCanopyFading = C.Permanent(function(...)", "C.SetShouldDrawPlayerIcons = C.Permanent(function(on, ...)" })
		{
			int at = lua.IndexOf(wrapper, StringComparison.Ordinal);
			AssertThat(at > 0 && lua.IndexOf("if C.PartnerCameraUpdating() then", at, StringComparison.Ordinal) - at < 200)
				.OverrideFailureMessage(wrapper).IsTrue();
		}

		AssertThat(lua.Contains("function C.PartnerCameraUpdating()\n\t\t\treturn Refii ~= nil and Refii._partner_camera_update ~= nil and Refii._partner_camera_update() == true", StringComparison.Ordinal)).IsTrue();
		// No mark of our own around the selection call: it ended before the mode's update wrote.
		AssertThat(lua.Contains("C.PartnerCameraUpdate =", StringComparison.Ordinal)).IsFalse();
		AssertThat(lua.Contains("C.PartnerCameraUpdate then", StringComparison.Ordinal)).IsFalse();
	}

	[TestCase]
	public void PlayerOnesCameraPositionIsPutBackAtTheWriteItself()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string lua = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "host", "CoopHeroLua.cs"));
		// Both writers of CameraManager.CameraPosition (camerabase.lua 234 and 287), wrapped by name.
		AssertThat(lua.Contains("for _, name in ipairs({ \"FrameUpdate\", \"PureFrameUpdate\" }) do", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("CameraBase[name] = C[\"CameraBase\" .. name]", StringComparison.Ordinal)).IsTrue();
		int wrap = lua.IndexOf("C[\"CameraBase\" .. name] = C.Permanent(function(self, ...)", StringComparison.Ordinal);
		AssertThat(wrap > 0).IsTrue();
		string body = lua.Substring(wrap, lua.IndexOf("end, \"CameraBase\" .. name)", wrap, StringComparison.Ordinal) - wrap);
		string[] order =
		{
			"if not C.PartnerCameraUpdating() then",
			"return shipped(self, ...)",
			"local position, primed = CameraManager.CameraPosition, CameraManager.PrimedFromLoad",
			"local results = { pcall(shipped, self, ...) }",
			"CameraManager.CameraPosition, CameraManager.PrimedFromLoad = position, primed",
			"error(results[2], 0)",
		};
		int last = -1;
		foreach (string step in order)
		{
			int at = body.IndexOf(step, StringComparison.Ordinal);
			AssertThat(at > last).OverrideFailureMessage(step).IsTrue();
			last = at;
		}

		// The native's answer spans the second manager's whole update, the selection and the mode update.
		string hooks = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "coop_hero_hooks.cpp"));
		AssertThat(hooks.Contains("extern \"C\" int RefiiCoopPartnerCameraUpdating(void) { return g_ticking_manager != 0u ? 1 : 0; }", StringComparison.Ordinal)).IsTrue();
		AssertThat(hooks.Contains("g_ticking_manager = second;\n  PPCContext work = ctx;\n  work.r3.u32 = second;\n  __imp__rex_lhCameraManager_Update(work, base);\n  g_ticking_manager = 0u;", StringComparison.Ordinal)).IsTrue();
		string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
		AssertThat(natives.Contains("{\"_partner_camera_update\", &NativePartnerCameraUpdate, 0},", StringComparison.Ordinal)).IsTrue();
		AssertThat(natives.Contains("lua.PushBoolean(RefiiCoopPartnerCameraUpdating() != 0);", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void ARejoinedPlayerTwoStartsTheirNewManagerWithNoCamera()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		// Every second player's Player built with a manager moves the number on; the chunk keys player two's
		// current camera to it, so the new manager's first update activates a camera and builds its wrapper.
		string hooks = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "coop_hero_hooks.cpp"));
		int build = hooks.IndexOf("bool RefiiCoopSplitPlayerCamera(PPCRegister& r31) {", StringComparison.Ordinal);
		AssertThat(build > 0).IsTrue();
		AssertThat(hooks.IndexOf("g_partner_manager_generation.fetch_add(1u, std::memory_order_acq_rel);", build, StringComparison.Ordinal) - build < 900)
			.IsTrue();
		AssertThat(hooks.Contains("extern \"C\" int RefiiCoopPartnerCameraGeneration(void) {", StringComparison.Ordinal)).IsTrue();
		string natives = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
		AssertThat(natives.Contains("{\"_partner_camera_manager\", &NativePartnerCameraManager, 0},", StringComparison.Ordinal)).IsTrue();
		AssertThat(natives.Contains("lua.PushNumber(static_cast<double>(RefiiCoopPartnerCameraGeneration()));", StringComparison.Ordinal)).IsTrue();
		string lua = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "host", "CoopHeroLua.cs"));
		AssertThat(lua.Contains("Refii._partner_camera_manager ~= nil and Refii._partner_camera_manager() or 0", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void ADestroyedManagerLeavesTheTableAndTheShell()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		// The manager destructor's only caller is the Player's holder (TU1 sub_82483440 0x82483474, disc
		// sub_82424C80 0x82424CB4): hooked on both builds, it takes the manager's Player out of the table and
		// states the manager's end to the shell, which drops it.
		foreach ((string build, string at) in new[] { ("Fable2TU1", "0x824834B0"), ("Fable2", "0x82424CF0") })
		{
			string config = File.ReadAllText(Path.Combine(root, "Fable2", "versions", build, "refii_config.toml"));
			AssertThat(config.Contains(at + " = { name = \"rex_lhCameraManager_Destroy\" }\n", StringComparison.Ordinal))
				.OverrideFailureMessage(build).IsTrue();
		}

		string hooks = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "coop_hero_hooks.cpp"));
		int hook = hooks.IndexOf("REX_EXTERN(rex_lhCameraManager_Destroy) {", StringComparison.Ordinal);
		AssertThat(hook > 0).IsTrue();
		string body = hooks.Substring(hook, hooks.IndexOf("\n}\n", hook, StringComparison.Ordinal) - hook);
		string[] order =
		{
			"const uint32_t player = refii::vfs::LoadGuestU32(base, manager);",
			"ForgetSplitPlayer(player);",
			"RefiiCameraManagerDestroyed(manager);",
			"__imp__rex_lhCameraManager_Destroy(ctx, base);",
		};
		int last = -1;
		foreach (string step in order)
		{
			int at = body.IndexOf(step, StringComparison.Ordinal);
			AssertThat(at > last).OverrideFailureMessage(step).IsTrue();
			last = at;
		}

		string statements = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "camera_statements.cpp"));
		AssertThat(statements.Contains("cam::Statement statement(cam::kDestroy, 0u);", StringComparison.Ordinal)).IsTrue();
		string rig = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "camera", "Engine_CameraRig.cs"));
		AssertThat(rig.Contains("case Call.Destroy:\n\t\t\t\tDrop(statement.Manager);", StringComparison.Ordinal)).IsTrue();
	}
}
