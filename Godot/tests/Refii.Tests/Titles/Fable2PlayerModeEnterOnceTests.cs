namespace Refii.Tests.Titles;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// Fable 2: the play-game mode's resume enters a player's top mode only when it is not entered already
/// (player_mode_hooks.cpp). Player two's default mode is entered by the drop-in's henchman setup while the
/// game is covered; the resume entering it again filed its action handler twice and doubled every one of
/// player two's uses until the next pause. Read from the source and both configs; the run is one A on a sale
/// sign right after a drop-in (one use line, one OnEnter).
/// </summary>
[TestSuite]
public class Fable2PlayerModeEnterOnceTests
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
	public void TheResumeStepsOverTheEnterOfAnEnteredMode()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string hook = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "player_mode_hooks.cpp"));
		AssertThat(hook.Contains("bool RefiiPlayerModeEnteredOnce(PPCRegister& r30) {", StringComparison.Ordinal)).IsTrue();
		AssertThat(hook.Contains("constexpr uint32_t kEnteredByte = 0x10u;", StringComparison.Ordinal)).IsTrue();
		AssertThat(hook.Contains("if (refii::vfs::LoadGuestU8(base, mode + kEnteredByte) == 0u) {\n    return false;\n  }", StringComparison.Ordinal)).IsTrue();

		string cmake = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "CMakeLists.txt"));
		AssertThat(cmake.Contains("${CMAKE_CURRENT_SOURCE_DIR}/src/player_mode_hooks.cpp", StringComparison.Ordinal)).IsTrue();

		// Before `lwz r11,0(r30)` of sub_823D93E8 / sub_824609C8; past its `bctrl` to `addi r31,r31,4`.
		foreach ((string build, string at, string to) in new[] { ("Fable2TU1", "0x823D9410", "0x823D9424"), ("Fable2", "0x824609F0", "0x82460A04") })
		{
			string config = File.ReadAllText(Path.Combine(root, "Fable2", "versions", build, "refii_config.toml"));
			AssertThat(config.Contains("name = \"RefiiPlayerModeEnteredOnce\"\naddress = " + at + "\nregisters = [\"r30\"]\njump_address_on_true = " + to + "\n", StringComparison.Ordinal))
				.OverrideFailureMessage(build).IsTrue();
		}
	}
}
