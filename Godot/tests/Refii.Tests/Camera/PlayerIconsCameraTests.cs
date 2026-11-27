namespace Refii.Tests.Camera;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// The offscreen co-op markers, both titles (Fable2 / Fable3 src/hooks/src/coop_player_icons.cpp): the hero
/// marker's record update is asked of player two's view node's camera while split, takes player two's switch,
/// and a camera's own switch is never sent to an online partner. Native code: the rows and the steps are read
/// from the source, every build.
/// </summary>
[TestSuite]
public class PlayerIconsCameraTests
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

	private static readonly (string Name, uint Named, uint Disc)[] Fable2Rows =
	{
		("rex_lhSceneView_SetCamera", 0x82293820u, 0x822933D8u),
		("rex_lhPlayerIcons_UpdateRecord", 0x822B2F38u, 0x822B2D70u),
		("rex_lhSceneView_Construct", 0x823893C0u, 0x8238DE58u),
	};

	private static readonly (string Name, uint Named, uint Disc)[] Fable3Rows =
	{
		("rex_View_SetRenderCamera", 0x822607C8u, 0x82261CB8u),
		("rex_Hud_PlayerIconRecord", 0x82196548u, 0x82196410u),
		("rex_SceneView_Construct", 0x824F19F8u, 0x824D1BB0u),
		("rex_Camera_ScreenBlock", 0x821CB2E0u, 0x821CB288u),
	};

	private static void InOrder(string body, string[] steps)
	{
		int at = 0;
		foreach (string step in steps)
		{
			int next = body.IndexOf(step, at, StringComparison.Ordinal);
			AssertThat(next >= at).OverrideFailureMessage(step).IsTrue();
			at = next + step.Length;
		}
	}

	private static void Rows(string named, string disc, (string Name, uint Named, uint Disc)[] rows)
	{
		foreach ((string name, uint a, uint b) in rows)
		{
			AssertThat(named.Contains($"0x{a:X8} = {{ name = \"{name}\" }}\n", StringComparison.Ordinal)).OverrideFailureMessage("named " + name).IsTrue();
			AssertThat(disc.Contains($"0x{b:X8} = {{ name = \"{name}\" }}\n", StringComparison.Ordinal)).OverrideFailureMessage("disc " + name).IsTrue();
			AssertThat(named.Split("\"" + name + "\"").Length).IsEqual(2);
			AssertThat(disc.Split("\"" + name + "\"").Length).IsEqual(2);
		}
	}

	private static void Hook(string config, string name, string address, string tail)
	{
		AssertThat(config.Contains($"[[midasm_hook]]\nname = \"{name}\"\naddress = {address}\n{tail}\n", StringComparison.Ordinal))
			.OverrideFailureMessage(name + " " + address).IsTrue();
	}

	[TestCase]
	public void TheSeamsAreNamedOnEveryBuild()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string tu1 = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2TU1", "refii_config.toml"));
		string disc2 = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "refii_config.toml"));
		Rows(tu1, disc2, Fable2Rows);
		Hook(tu1, "RefiiCoopPlayerIconCamera", "0x822B32E4", "registers = [\"r24\", \"r30\"]");
		Hook(disc2, "RefiiCoopPlayerIconCamera", "0x822B311C", "registers = [\"r24\", \"r30\"]");
		Hook(tu1, "RefiiPlayerIconsStayHere", "0x823062C0", "jump_address_on_true = 0x823062E0");
		Hook(disc2, "RefiiPlayerIconsStayHere", "0x82907EE8", "jump_address_on_true = 0x82907F08");

		string tu3 = File.ReadAllText(Path.Combine(root, "Fable3", "versions", "Fable3TU3", "refiii_config.toml"));
		string disc3 = File.ReadAllText(Path.Combine(root, "Fable3", "versions", "Fable3", "refiii_config.toml"));
		Rows(tu3, disc3, Fable3Rows);
		Hook(tu3, "RefiiF3PlayerIconCamera", "0x82196A3C", "registers = [\"r26\", \"r31\"]");
		Hook(disc3, "RefiiF3PlayerIconCamera", "0x82196844", "registers = [\"r25\", \"r29\"]");
		Hook(tu3, "RefiiF3PlayerIconsStayHere", "0x8254BE10", "jump_address_on_true = 0x8254BE34");
		Hook(disc3, "RefiiF3PlayerIconsStayHere", "0x8251C18C", "jump_address_on_true = 0x8251C1B0");

		string cmake2 = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "CMakeLists.txt"));
		AssertThat(cmake2.Contains("src/coop_player_icons.cpp", StringComparison.Ordinal)).IsTrue();
		string cmake3 = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "CMakeLists.txt"));
		AssertThat(cmake3.Contains("src/coop_player_icons.cpp", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void PlayerTwosNodeIsTheGuestsOwnOnPlayerTwosBlock()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string f2 = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "coop_player_icons.cpp"));
		int set2 = f2.IndexOf("REX_EXTERN(rex_lhSceneView_SetCamera) {", StringComparison.Ordinal);
		int record2 = f2.IndexOf("REX_EXTERN(rex_lhPlayerIcons_UpdateRecord) {", StringComparison.Ordinal);
		AssertThat(set2 > 0 && record2 > set2).IsTrue();
		InOrder(f2.Substring(set2, record2 - set2), new[]
		{
			"__imp__rex_lhSceneView_SetCamera(ctx, base);",
			"entry.r3.u32 != WorldNode(base)",
			"RefiiCoopSplitPartnerCameraHolder(base)",
			"__imp__rex_lhCamera_ScreenBlock(block, base);",
			"__imp__rex_lhSceneView_SetCamera(set, base);",
		});
		InOrder(f2, new[]
		{
			"rex_lhSceneView_Construct(construct, base);",
			"refii::vfs::StoreGuestU32(base, node + kNodeRenderCamera, kNoRenderCamera);",
		});
		AssertThat(f2.Contains("constexpr uint32_t kNoRenderCamera = 0xFFFFFFFEu;", StringComparison.Ordinal)).IsTrue();
		AssertThat(f2.Contains("void RefiiCoopPlayerIconCamera(PPCRegister& r24, PPCRegister& r30) {", StringComparison.Ordinal)).IsTrue();
		AssertThat(f2.Contains("refii::vfs::LoadGuestU32(base, r24.u32 + 8u) != kHeroMarker", StringComparison.Ordinal)).IsTrue();

		string f3 = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "coop_player_icons.cpp"));
		int set3 = f3.IndexOf("REX_EXTERN(rex_View_SetRenderCamera) {", StringComparison.Ordinal);
		int record3 = f3.IndexOf("REX_EXTERN(rex_Hud_PlayerIconRecord) {", StringComparison.Ordinal);
		AssertThat(set3 > 0 && record3 > set3).IsTrue();
		InOrder(f3.Substring(set3, record3 - set3), new[]
		{
			"__imp__rex_View_SetRenderCamera(ctx, base);",
			"entry.r3.u32 != WorldNode(base)",
			"refii::fable3::coopcam::SplitInEffect(base) ? refii::fable3::coopcam::PartnerHolder(base) : 0u",
			"rex_Camera_ScreenBlock(block, base);",
			"__imp__rex_View_SetRenderCamera(set, base);",
		});
		AssertThat(f3.Contains("void RefiiF3PlayerIconCamera(PPCRegister& record, PPCRegister& camera) {", StringComparison.Ordinal)).IsTrue();
		AssertThat(f3.Contains("LoadGuestU32(base, record.u32 + 4u) != kHeroMarker", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void TheHeroMarkerTakesPlayerTwosSwitchAndACameraStaysHome()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		foreach (string title in new[] { "Fable2", "Fable3" })
		{
			string hooks = File.ReadAllText(Path.Combine(root, title, "src", "hooks", "src", "coop_player_icons.cpp"));
			AssertThat(hooks.Contains("ctx.r4.u64 = refii_bridge_stated_value_now(kStatedPartnerIcons) != 1u ? 1u : 0u;", StringComparison.Ordinal))
				.OverrideFailureMessage(title).IsTrue();
			AssertThat(hooks.Contains("constexpr uint32_t kStatedPartnerIcons = 25u;", StringComparison.Ordinal)).OverrideFailureMessage(title).IsTrue();
			AssertThat(hooks.Contains("if (g_from_camera.load(std::memory_order_relaxed) <= 0) {\n    return false;", StringComparison.Ordinal))
				.OverrideFailureMessage(title).IsTrue();
		}

		string natives2 = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
		AssertThat(natives2.Contains("{\"_camera_player_icons\", &NativeCameraPlayerIcons, 0},", StringComparison.Ordinal)).IsTrue();
		AssertThat(natives2.Contains("RefiiPlayerIconsFromCamera(lua.Boolean(1) ? 1 : 0);", StringComparison.Ordinal)).IsTrue();
		string natives3 = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "mod_lua_hooks.cpp"));
		AssertThat(natives3.Contains("{\"_camera_player_icons\", &NativeCameraPlayerIcons, 0u},", StringComparison.Ordinal)).IsTrue();
		AssertThat(natives3.Contains("RefiiF3PlayerIconsFromCamera(lua.Boolean(1) ? 1 : 0);", StringComparison.Ordinal)).IsTrue();

		// A camera's statement is marked around SACCamera:Activate / :Deactivate; anything else reaches player two's half too.
		string lua2 = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "host", "CoopHeroLua.cs"));
		InOrder(lua2, new[]
		{
			"for _, name in ipairs({ \"Activate\", \"Deactivate\" }) do",
			"C.PlayerIconsFromCamera = (C.PlayerIconsFromCamera or 0) + 1",
			"C.SetShouldDrawPlayerIcons = C.Permanent(function(on, ...)",
			"local camera = (C.PlayerIconsFromCamera or 0) > 0",
			"if not camera and C.LocalCoopCameras() and Refii ~= nil and Refii._partner_player_icons ~= nil then",
			"Refii._camera_player_icons(true)",
			"Refii._camera_player_icons(false)",
		});
		string lua3 = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable3", "Fable3CoreLua.cs"));
		InOrder(lua3, new[]
		{
			"for _, name in ipairs({ \"Activate\", \"Deactivate\" }) do",
			"CC.IconsFromCamera = (CC.IconsFromCamera or 0) + 1",
			"Refii._coop_player_icons = function(on, ...)",
			"local camera = (CC.IconsFromCamera or 0) > 0",
			"if not camera and CC.Couch() and Refii._partner_player_icons ~= nil then",
			"Refii._camera_player_icons(true)",
			"Refii._camera_player_icons(false)",
		});
	}
}
