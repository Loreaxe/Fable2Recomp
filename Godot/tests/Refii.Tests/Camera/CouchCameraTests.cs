namespace Refii.Tests.Camera;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// Camera stage C2f, Fable 3: player two's camera manager in a couch game. The two gates and the camera-
/// script skip on both builds, the second update with its latch bytes, the selector's player-two answer and
/// its brackets, the push and the mouse kept to player one's camera, the split stated from the shell, and the
/// core chunk's guards (each hero's current camera, player one's CameraPosition, no leash or canopy from player
/// two's camera, player two's default record unsaved, the leash per screen). Read from the source; the run is
/// the game lane's (NOTES.md).
/// </summary>
[TestSuite]
public class CouchCameraTests
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
	public void BothBuildsBuildPlayerTwosCameraAndTickIt()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		foreach ((string build, string holder, string selector, string move, string gate, string build_at, string setup, string spawn, string reload, string after) in new[]
		{
			("Fable3TU3", "0x82268F38", "0x82208EA0", "0x829330D0", "0x8245AD9C", "0x8245ADA0", "0x8245B384", "0x8245B388", "0x8256ABB8", "0x8256ABBC"),
			("Fable3", "0x8226A8C0", "0x82209680", "0x828DA310", "0x8243B4E4", "0x8243B4E8", "0x8243BAF8", "0x8243BAFC", "0x8253AC00", "0x8253AC04"),
		})
		{
			string config = File.ReadAllText(Path.Combine(root, "Fable3", "versions", build, "refiii_config.toml"));
			AssertThat(config.Contains(holder + " = { name = \"rex_CameraHolder_Update\" }", StringComparison.Ordinal)).OverrideFailureMessage(build + " holder").IsTrue();
			AssertThat(config.Contains(selector + " = { name = \"rex_PlayerManager_CameraHolder\" }", StringComparison.Ordinal)).OverrideFailureMessage(build + " selector").IsTrue();
			AssertThat(config.Contains(move + " = { name = \"rex_CInputProcessCharacterMovement_ProcessInput\" }", StringComparison.Ordinal)).OverrideFailureMessage(build + " movement").IsTrue();
			AssertThat(config.Contains("name = \"RefiiF3PartnerCamera\"\naddress = " + gate + "\nregisters = [\"r31\"]\njump_address_on_true = " + build_at + "\n", StringComparison.Ordinal)).OverrideFailureMessage(build + " ctor gate").IsTrue();
			AssertThat(config.Contains("name = \"RefiiF3PartnerCameraEntities\"\naddress = " + setup + "\nregisters = [\"r28\"]\njump_address_on_true = " + spawn + "\n", StringComparison.Ordinal)).OverrideFailureMessage(build + " setup gate").IsTrue();
			AssertThat(config.Contains("name = \"RefiiF3PartnerCameraScripts\"\naddress = " + reload + "\nregisters = [\"r31\"]\njump_address_on_true = " + after + "\n", StringComparison.Ordinal)).OverrideFailureMessage(build + " scripts").IsTrue();
		}

		string rows = File.ReadAllText(Path.Combine(root, "Fable3", "versions", "Fable3", "guest_addresses.inc"));
		foreach (string row in new[] { "{0x835970C2u, 0x835565BFu}", "{0x835970C3u, 0x835565E0u}", "{0x835970D8u, 0x835565E1u}", "{0x83597034u, 0x83556540u}", "{0x835979F8u, 0x83556EF8u}" })
		{
			AssertThat(rows.Contains(row, StringComparison.Ordinal)).OverrideFailureMessage(row).IsTrue();
		}

		string hooks = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "coop_camera_hooks.cpp"));
		AssertThat(hooks.Contains("session + refii::guest::Pick(0x80u, 0x7Cu)) == 1u", StringComparison.Ordinal)).IsTrue();
		AssertThat(hooks.Contains("StoreU8(base, kLatches[i], g_partner_latches[i]);", StringComparison.Ordinal)).IsTrue();
		AssertThat(hooks.Contains("StoreU8(base, kLatches[i], first[i]);", StringComparison.Ordinal)).IsTrue();
		AssertThat(hooks.Contains("if (t_partner_depth > 0u) {", StringComparison.Ordinal)).IsTrue();
		string cmake = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "CMakeLists.txt"));
		AssertThat(cmake.Contains("${CMAKE_CURRENT_SOURCE_DIR}/src/coop_camera_hooks.cpp", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void PlayerTwosCameraSendsNothingAndTakesNoMouse()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string push = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "camera_statements.cpp"));
		AssertThat(push.Contains("if (!partner) {\n    __imp__rex_CameraManager_Push(ctx, base);\n  }", StringComparison.Ordinal)).IsTrue();
		AssertThat(push.Contains("Player(presented ? 1u : partner ? 2u : 0u, presented ? cam::kFlagDrawn : 0u)", StringComparison.Ordinal)).IsTrue();
		string mouse = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "mouse_look.cpp"));
		AssertThat(mouse.Contains("if (manager != 0u && manager == refii::fable3::coopcam::PartnerManagerTicking()) {", StringComparison.Ordinal)).IsTrue();
		string pump = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "game_tick_pump.cpp"));
		AssertThat(pump.Contains("refii::fable3::coopcam::AnswerCoopSplit(name);", StringComparison.Ordinal)).IsTrue();
		string natives = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "mod_lua_hooks.cpp"));
		AssertThat(natives.Contains("{\"_coop_split\", &NativeCoopSplit, 0u}", StringComparison.Ordinal)).IsTrue();
		string title = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable3", "Fable3Title.cs"));
		AssertThat(title.Contains("Refii.Host.HostBridge.PostFrontEndAction(\"CoopSplitScreen:\" + want);", StringComparison.Ordinal)).IsTrue();
		AssertThat(title.Contains("HostBridge.StatedValue.CoopLocalPartner) == 1u", StringComparison.Ordinal)).IsTrue();
		AssertThat((uint)Refii.Host.HostBridge.StatedValue.CoopLocalPartner).IsEqual(24u);
	}

	[TestCase]
	public void TheCoreChunkKeepsTheSaveAndTheSharedScreenPlayerOnes()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string lua = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable3", "Fable3CoreLua.cs"));
		foreach (string line in new[]
		{
			"function Refii.split_screen() return Refii._coop_split ~= nil and Refii._coop_split() == true end",
			"r1, r2, r3 = Refii._coop_frame(self, player, inner, a, b, c)",
			"ok, r1, r2, r3 = pcall(Refii.run_as_player, 2, inner, self, player, a, b, c)",
			"CameraManager.CameraPosition, CameraManager.PrimedFromLoad = position, primed",
			"if overrides ~= nil and not had and overrides[key] ~= nil then CC.Made[key] = overrides[key] end",
			"if PartnerDefault(id, record) then",
			"if CC.PartnerUpdating() then return end",
			"Debug.SetMultiPlayerHeroFree(free or reuniting)",
			"local free = CC.Split() or CC.Hybrid()",
			"Refii._coop_tether_stated(CC.SharedLeashMetres or CC.LeashMetres, CC.Separation)",
			"elseif not CC.Split() then\n\t\t\t\t\tCC.SharedLeashMetres = distance",
			"if type(set) == \"table\" and set.IsValid == shipped then set.IsValid = wrap end",
			"if Refii._wrap_coop_camera ~= nil then Refii._wrap_coop_camera() end",
		})
		{
			AssertThat(lua.Contains(line, StringComparison.Ordinal)).OverrideFailureMessage(line).IsTrue();
		}

		// The guards hold for player two's whole update (the native's span), not around the selection alone.
		AssertThat(lua.Contains("CC.PartnerUpdate =", StringComparison.Ordinal)).IsFalse();
		AssertThat(lua.Contains("if CC.PartnerUpdate then", StringComparison.Ordinal)).IsFalse();

		// Single player goes straight to the shipped FrameUpdate: the couch path asks for a couch game first.
		AssertThat(lua.Contains("if player ~= nil and Refii._coop_frame ~= nil and Refii._couch ~= nil and Refii._couch.Couch() then", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("if not CC.Couch() then return shipped(self, ...) end", StringComparison.Ordinal)).IsTrue();
	}

	/// <summary>
	/// C2F_CS: one manager update is the selection (TU3 0x82255518 / disc 0x822562E8) and then the mode's own
	/// FrameUpdate (0x82255540 / 0x82256310), which writes CameraPosition; the couch guards span the native's
	/// whole update of player two's manager.
	/// </summary>
	[TestCase]
	public void PlayerTwosWholeUpdateKeepsTheSavePlayerOnes()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string hooks = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "coop_camera_hooks.cpp"));
		AssertThat(hooks.Contains("return refii::fable3::coopcam::PartnerManagerTicking() != 0u ? 1 : 0;", StringComparison.Ordinal)).IsTrue();
		// The span is the second holder update: the flag is set before the guest's update of player two's holder and cleared after it.
		int set = hooks.IndexOf("g_partner_ticking.store(manager, std::memory_order_relaxed);", StringComparison.Ordinal);
		int call = hooks.IndexOf("__imp__rex_CameraHolder_Update(work, base);", StringComparison.Ordinal);
		int clear = hooks.IndexOf("g_partner_ticking.store(0u, std::memory_order_relaxed);", StringComparison.Ordinal);
		AssertThat(set > 0 && set < call && call < clear).IsTrue();
		string natives = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "mod_lua_hooks.cpp"));
		AssertThat(natives.Contains("{\"_partner_camera_update\", &NativePartnerCameraUpdate, 0u}", StringComparison.Ordinal)).IsTrue();

		string lua = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable3", "Fable3CoreLua.cs"));
		int wrap = lua.IndexOf("local wrap = function(self, ...)", StringComparison.Ordinal);
		AssertThat(wrap > 0).IsTrue();
		string body = lua.Substring(wrap, lua.IndexOf("CameraBase[name] = wrap", wrap, StringComparison.Ordinal) - wrap);
		string[] steps =
		{
			"if not CC.PartnerUpdating() then return shipped(self, ...) end",
			"local position, primed = CameraManager.CameraPosition, CameraManager.PrimedFromLoad",
			"local ok, a, b, c, d = pcall(shipped, self, ...)",
			"CameraManager.CameraPosition, CameraManager.PrimedFromLoad = position, primed",
			"if not ok then error(a, 0) end",
		};
		int at = 0;
		foreach (string step in steps)
		{
			int next = body.IndexOf(step, at, StringComparison.Ordinal);
			AssertThat(next >= at).OverrideFailureMessage(step).IsTrue();
			at = next + step.Length;
		}

		AssertThat(lua.Contains("for _, name in ipairs({ \"FrameUpdate\", \"PureFrameUpdate\" }) do", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("function CC.PartnerUpdating() return Refii._partner_camera_update ~= nil and Refii._partner_camera_update() == true end", StringComparison.Ordinal)).IsTrue();
		// Player two's mode update runs as player two too: the hero answers ask the native's span.
		AssertThat(lua.Contains("if p == nil and Refii._partner_camera_update ~= nil and Refii._partner_camera_update() == true then return 2 end", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("GetLocalHero = function() if Acting() == 2 then return rem() end return loc() end", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void ADestroyedManagerLeavesEveryTableAndTheShell()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		// The manager destructor's only live caller is the holder's slot 0 (TU3 sub_8256CBD8 0x8256CC04, disc
		// sub_8253CBD0 0x8253CBFC): hooked on both builds, it frees the manager's last block and presented
		// mark, ends player two's carried bytes for it and states the manager's end, which the rig drops.
		foreach ((string build, string at) in new[] { ("Fable3TU3", "0x8256EBB0"), ("Fable3", "0x8253E9B8") })
		{
			string config = File.ReadAllText(Path.Combine(root, "Fable3", "versions", build, "refiii_config.toml"));
			AssertThat(config.Contains(at + " = { name = \"rex_CameraManager_Destroy\" }\n", StringComparison.Ordinal))
				.OverrideFailureMessage(build).IsTrue();
		}

		string statements = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "camera_statements.cpp"));
		int hook = statements.IndexOf("REX_EXTERN(rex_CameraManager_Destroy) {", StringComparison.Ordinal);
		AssertThat(hook > 0).IsTrue();
		string body = statements.Substring(hook, statements.IndexOf("\n}\n", hook, StringComparison.Ordinal) - hook);
		string[] order =
		{
			"refii::fable3::coopcam::ManagerDestroyed(base, manager);",
			"last = LastBlock{};",
			"g_presented_manager.compare_exchange_strong(presented, 0u, std::memory_order_relaxed);",
			"cam::Statement statement(cam::kDestroy, 0u);",
			"__imp__rex_CameraManager_Destroy(ctx, base);",
		};
		int last = -1;
		foreach (string step in order)
		{
			int found = body.IndexOf(step, StringComparison.Ordinal);
			AssertThat(found > last).OverrideFailureMessage(step).IsTrue();
			last = found;
		}

		string hooks = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "coop_camera_hooks.cpp"));
		AssertThat(hooks.Contains("if (g_partner_latches_for == manager) {\n    g_partner_latches_for = 0u;", StringComparison.Ordinal)).IsTrue();
		string rig = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "camera", "Engine_CameraRig.cs"));
		AssertThat(rig.Contains("case Call.Destroy:\n\t\t\t\tDrop(statement.Manager);", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void PlayerTwosCurrentCameraBelongsToOneManager()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		// A re-join's new manager starts with no current camera, so its first selection activates the best one
		// (the update calls the active mode with no test: TU3 0x8225552C..0x82255540, disc 0x822562FC..0x82256310).
		string hooks = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "coop_camera_hooks.cpp"));
		AssertThat(hooks.Contains("    g_partner_latches_for = manager;\n    g_partner_generation.fetch_add(1u, std::memory_order_relaxed);", StringComparison.Ordinal)).IsTrue();
		string natives = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "mod_lua_hooks.cpp"));
		AssertThat(natives.Contains("{\"_partner_camera_manager\", &NativePartnerCameraManager, 0u}", StringComparison.Ordinal)).IsTrue();

		string lua = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable3", "Fable3CoreLua.cs"));
		// The HUD's player icons (SACCamera:Activate / :Deactivate) are player one's camera's, as the leash and the canopy.
		AssertThat(lua.Contains("Refii._coop_player_icons = function(on, ...)\n\t\t\tif CC.PartnerUpdating() then\n\t\t\t\tif Refii._partner_player_icons ~= nil then Refii._partner_player_icons(on == true) end\n\t\t\t\treturn\n\t\t\tend\n\t\t\tlocal camera = (CC.IconsFromCamera or 0) > 0", StringComparison.Ordinal)).IsTrue();
		int frame = lua.IndexOf("function Refii._coop_frame(self, player, inner, a, b, c)", StringComparison.Ordinal);
		AssertThat(frame > 0).IsTrue();
		string body = lua.Substring(frame, lua.IndexOf("\nend\n", frame, StringComparison.Ordinal) - frame);
		string[] steps =
		{
			"pcall(CC.TetherTick, player)\n\t\treturn inner(self, player, a, b, c)",
			"local manager = CC.PartnerManager()",
			"if state == nil or state.Manager ~= manager then\n\t\tstate = { Manager = manager }",
			"self.CurrentSet, self.CurrentCamera, self.CurrentSetPriority = state.Set, state.Camera, state.Priority",
			"pcall(Refii.run_as_player, 2, inner, self, player, a, b, c)",
			"self.CurrentSet, self.CurrentCamera, self.CurrentSetPriority = set, camera, priority",
		};
		int at = 0;
		foreach (string step in steps)
		{
			int next = body.IndexOf(step, at, StringComparison.Ordinal);
			AssertThat(next >= at).OverrideFailureMessage(step).IsTrue();
			at = next + step.Length;
		}
	}

	[TestCase]
	public void TheParticlesLevelOfDetailAnswersEitherCameraWhileSplit()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		// The particle frame (TU3 sub_831BD0F8 / disc sub_8318B238) and one system's level of detail (sub_831BFAA8 /
		// sub_8318DBE8, its only caller sub_831BFCC0), on both builds; the view lookup and the guest's own camera
		// setter for player two's view by address row.
		foreach ((string build, string frame, string detail) in new[] { ("Fable3TU3", "0x831BD0F8", "0x831BFAA8"), ("Fable3", "0x8318B238", "0x8318DBE8") })
		{
			string config = File.ReadAllText(Path.Combine(root, "Fable3", "versions", build, "refiii_config.toml"));
			AssertThat(config.Contains(frame + " = { name = \"rex_ParticleThread_Frame\" }\n", StringComparison.Ordinal)).OverrideFailureMessage(build).IsTrue();
			AssertThat(config.Contains(detail + " = { name = \"rex_ParticleSystem_LevelOfDetail\" }\n", StringComparison.Ordinal)).OverrideFailureMessage(build).IsTrue();
		}

		string rows = File.ReadAllText(Path.Combine(root, "Fable3", "versions", "Fable3", "guest_addresses.inc"));
		AssertThat(rows.Contains("{0x831BE848u, 0x8318C988u}", StringComparison.Ordinal)).IsTrue();
		AssertThat(rows.Contains("{0x831BF8C0u, 0x8318DA00u}", StringComparison.Ordinal)).IsTrue();
		string cmake = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "CMakeLists.txt"));
		AssertThat(cmake.Contains("src/coop_particle_view.cpp", StringComparison.Ordinal)).IsTrue();

		string hooks = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "coop_particle_view.cpp"));
		int frameAt = hooks.IndexOf("REX_EXTERN(rex_ParticleThread_Frame) {", StringComparison.Ordinal);
		int detailAt = hooks.IndexOf("REX_EXTERN(rex_ParticleSystem_LevelOfDetail) {", StringComparison.Ordinal);
		AssertThat(frameAt > 0 && detailAt > frameAt).IsTrue();
		string frameBody = hooks.Substring(frameAt, detailAt - frameAt);
		string detailBody = hooks.Substring(detailAt);
		string[] frameSteps =
		{
			"if (!refii::fable3::coopcam::SplitInEffect(base) || refii_bridge_camera_stated_block(2, block) == 0) {\n    __imp__rex_ParticleThread_Frame(ctx, base);",
			"lookup.r3.u32 = ctx.r5.u32;",
			"std::memcpy(base + view, base + first, kViewBytes);",
			"CallAt(set, base, kViewSetCamera)",
			"t_second_view = view;\n  __imp__rex_ParticleThread_Frame(work, base);\n  t_second_view = 0u;",
		};
		string[] detailSteps =
		{
			"if (second == 0u || ctx.r4.u32 == second || !Plausible(holder)) {\n    __imp__rex_ParticleSystem_LevelOfDetail(ctx, base);",
			"if (state == 0u) {\n    return;",
			"StoreU32(base, holder, system);\n  StoreU32(base, holder + 4u, count);",
			"AddReference(base, count);",
			"again.r4.u32 = second;",
			"if (LoadU32(base, system + 8u) > state) {",
		};
		foreach ((string body, string[] steps) in new[] { (frameBody, frameSteps), (detailBody, detailSteps) })
		{
			int at = 0;
			foreach (string step in steps)
			{
				int next = body.IndexOf(step, at, StringComparison.Ordinal);
				AssertThat(next >= at).OverrideFailureMessage(step).IsTrue();
				at = next + step.Length;
			}
		}
	}

	[TestCase]
	public void VisibleOnAnyScreenAsksPlayerTwosScreenWhileSplit()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string lua = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable3", "Fable3CoreLua.cs"));
		int at = lua.IndexOf("Refii._coop_visible_any = function(entity, ...)", StringComparison.Ordinal);
		AssertThat(at > 0).IsTrue();
		string body = lua.Substring(at, lua.IndexOf("CameraManager.IsEntityVisibleOnAnyScreen = Refii._coop_visible_any", at, StringComparison.Ordinal) - at);
		string[] steps =
		{
			"if not CC.Split() or type(IsEntityVisibleOnScreen) ~= \"function\" or Refii._partner_screen == nil then\n\t\t\t\treturn shipped(entity, ...)",
			"if IsEntityVisibleOnScreen(entity) == true then return true end",
			"Refii._partner_screen(true)",
			"local ok, seen = pcall(IsEntityVisibleOnScreen, entity)",
			"Refii._partner_screen(false)",
		};
		int from = 0;
		foreach (string step in steps)
		{
			int next = body.IndexOf(step, from, StringComparison.Ordinal);
			AssertThat(next >= from).OverrideFailureMessage(step).IsTrue();
			from = next + step.Length;
		}
	}
}
