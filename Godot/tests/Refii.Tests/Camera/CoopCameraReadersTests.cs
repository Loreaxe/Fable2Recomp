namespace Refii.Tests.Camera;

using System;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// Camera stage C2c, Fable 2: the sim's per-player camera readers answer player two's camera for player
/// two (Fable2/src/hooks/src/coop_camera_readers.cpp). Native code: the rows and the rules are read from
/// the source, both builds.
/// </summary>
[TestSuite]
public class CoopCameraReadersTests
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

	private static readonly (string Name, uint Tu1, uint Disc)[] Wrapped =
	{
		("rex_lhPlayers_Camera", 0x821F87C8u, 0x821F8760u),
		("rex_CInputProcessCharacterMovement_ProcessInput", 0x826E70C8u, 0x82743E48u),
		("rex_CInputProcessStickJabDetection_ProcessInput", 0x826E95A8u, 0x82746328u),
		("rex_Targeting_SearchContext", 0x821A9478u, 0x821A95E0u),
		("rex_CECPlayer_LookAtUpdate", 0x823EE918u, 0x823CD340u),
		("rex_CECProbeSet_Probe", 0x8227D588u, 0x8227D400u),
		("rex_Lua_Player_GetPositionOfCameraAssociatedWithPlayer", 0x823E4F28u, 0x823C38F0u),
		("rex_Lua_Player_GetForwardOfCameraAssociatedWithPlayer", 0x823E4F80u, 0x823C3948u),
		("rex_Kaim_CEntityInfoFilterF2Visibility_Filter", 0x82626848u, 0x8263B110u),
		("rex_Kaim_CEntityInfoFilterF2Sight_Filter", 0x82626AF8u, 0x8263B360u),
	};

	[TestCase]
	public void EveryReaderIsNamedOnBothBuildsAndWrapped()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string tu1 = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2TU1", "refii_config.toml"));
		string disc = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "refii_config.toml"));
		string readers = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "coop_camera_readers.cpp"));
		foreach ((string name, uint a, uint b) in Wrapped)
		{
			AssertThat(tu1.Contains($"0x{a:X8} = {{ name = \"{name}\" }}", StringComparison.Ordinal)).OverrideFailureMessage("TU1 " + name).IsTrue();
			AssertThat(disc.Contains($"0x{b:X8} = {{ name = \"{name}\" }}", StringComparison.Ordinal)).OverrideFailureMessage("disc " + name).IsTrue();
			AssertThat(readers.Contains("REX_EXTERN(" + name + ")", StringComparison.Ordinal)).OverrideFailureMessage("hook " + name).IsTrue();
			AssertThat(readers.Contains("__imp__" + name + "(", StringComparison.Ordinal)).OverrideFailureMessage("shipped call " + name).IsTrue();
		}

		// Each name once per build.
		foreach ((string name, _, _) in Wrapped)
		{
			AssertThat(tu1.Split("\"" + name + "\"").Length).IsEqual(2);
			AssertThat(disc.Split("\"" + name + "\"").Length).IsEqual(2);
		}

		string cmake = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "CMakeLists.txt"));
		AssertThat(cmake.Contains("src/coop_camera_readers.cpp", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void SinglePlayerTakesTheShippedPath()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string readers = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "coop_camera_readers.cpp"));
		// A bracket only for player two's hero with player two's view here; the getter answers key 2 only
		// inside one and only with player two's own manager.
		AssertThat(readers.Contains("entity == refii_mod_player_hero(base, 2) && RefiiCoopPartnerViewHere() != 0", StringComparison.Ordinal)).IsTrue();
		AssertThat(readers.Contains("if (RefiiCoopPartnerScreenDepth() > 0 && RefiiCoopSplitScreen() != 0) {", StringComparison.Ordinal)).IsTrue();
		AssertThat(readers.Contains("call.r4.u64 = 2u;", StringComparison.Ordinal)).IsTrue();
		// The AI filter asks again only when the shipped answer filtered.
		AssertThat(readers.Contains("if (ctx.r3.u32 == 0u || RefiiCoopPartnerScreenDepth() > 0 || RefiiCoopPartnerViewHere() == 0) {", StringComparison.Ordinal)).IsTrue();

		string hero = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "coop_hero_hooks.cpp"));
		AssertThat(hero.Contains("if ((t_peer_view || (t_partner_screen > 0 && PeerCone(ctx, base) != 0u)) && g_peer_guest != 0u) {", StringComparison.Ordinal)).IsTrue();
		AssertThat(hero.Contains("void RefiiCoopLodCamera(PPCVRegister& v0) {", StringComparison.Ordinal)).IsTrue();
		foreach (string build in new[] { "Fable2TU1", "Fable2" })
		{
			string config = File.ReadAllText(Path.Combine(root, "Fable2", "versions", build, "refii_config.toml"));
			int at = config.IndexOf("name = \"RefiiCoopLodCamera\"", StringComparison.Ordinal);
			AssertThat(at > 0).IsTrue();
			string row = config.Substring(at, 80);
			AssertThat(row.Contains("registers = [\"v0\"]", StringComparison.Ordinal)).OverrideFailureMessage(build).IsTrue();
		}

		string bus = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
		int partner = bus.IndexOf("const bool partner_camera = RefiiCoopPartnerCameraFor(base, receiver_hero) != 0;", StringComparison.Ordinal);
		AssertThat(partner > 0 && bus.IndexOf("__imp__rex_CPlayerActionReceiver_ProcessAction(ctx, base);", partner, StringComparison.Ordinal) > partner).IsTrue();
	}
}
