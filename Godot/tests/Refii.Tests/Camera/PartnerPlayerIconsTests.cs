namespace Refii.Tests.Camera;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// Each half its own player's GUI.SetShouldDrawPlayerIcons (SACCamera:Activate / :Deactivate): player one's camera
/// states the game's own switch, player two's camera states player two's half's (stated value 25), both titles;
/// on split each half shows the other player's hero's marker.
/// </summary>
[TestSuite]
public class PartnerPlayerIconsTests
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
	public void EachHalfShowsTheOtherPlayersHero()
	{
		AssertThat((uint)Refii.Host.HostBridge.StatedValue.PartnerPlayerIcons).IsEqual(25u);
		AssertThat(Refii.Ui.Engine_HudSpriteView.PartnerMarkerHalf(52u)).IsEqual(1);   // hero one's marker: player two's half
		AssertThat(Refii.Ui.Engine_HudSpriteView.PartnerMarkerHalf(53u)).IsEqual(0);   // hero two's: player one's
	}

	[TestCase]
	public void BothTitlesStateAndReadIt()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string f2 = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
		AssertThat(f2.Contains("refii_bridge_stated_value(25u, lua.Boolean(1) ? 2u : 1u);", StringComparison.Ordinal)).IsTrue();
		AssertThat(f2.Contains("{\"_partner_player_icons\", &NativePartnerPlayerIcons, 0},", StringComparison.Ordinal)).IsTrue();
		string f3 = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "mod_lua_hooks.cpp"));
		AssertThat(f3.Contains("refii_bridge_stated_value(25u, lua.Boolean(1) ? 2u : 1u);", StringComparison.Ordinal)).IsTrue();
		AssertThat(f3.Contains("{\"_partner_player_icons\", &NativePartnerPlayerIcons, 0u},", StringComparison.Ordinal)).IsTrue();

		string lua2 = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "host", "CoopHeroLua.cs"));
		AssertThat(lua2.Contains("if C.PartnerCameraUpdating() then\n\t\t\t\t\tif Refii ~= nil and Refii._partner_player_icons ~= nil then\n\t\t\t\t\t\tRefii._partner_player_icons(on == true)", StringComparison.Ordinal)).IsTrue();
		// The guest applies player two's switch to the hero marker itself (coop_player_icons.cpp): Godot draws what
		// is stated, each marker in the half the guest tested it for.
		string ui2 = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "game", "GameUi.cs"));
		AssertThat(ui2.Contains("split && sprite.Type is (52u or 53u) && Engine_HudSpriteView.PartnerMarkerHalf(sprite.Type) == 1", StringComparison.Ordinal)).IsTrue();
		AssertThat(ui2.Contains("PlayerIconsIn", StringComparison.Ordinal)).IsFalse();
		string ui3 = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable3", "Fable3WorldIcons.cs"));
		AssertThat(ui3.Contains("(split && Engine_HudSpriteView.PartnerMarkerHalf(sprite.Type) != index)", StringComparison.Ordinal)).IsTrue();
		AssertThat(ui3.Contains("PlayerIconsIn", StringComparison.Ordinal)).IsFalse();
	}
}
