namespace Refii.Tests.Ui;

using System;
using System.IO;
using GdUnit4;
using Refii.Core;
using static GdUnit4.Assertions;

/// <summary>
/// Each local player's button glyphs follow their own controller: the shared set's player (the open
/// menu's owner on a split screen, else player one; the last pad used on one screen), player two's
/// own set for the surfaces that are theirs alone, and the quad shader's switch per set. Pure, plus
/// the shader and project text.
/// </summary>
[TestSuite]
public class PlayerGlyphsTests
{
	[TestCase]
	public void OnASplitScreenTheSharedSetFollowsTheOpenMenusOwnerElsePlayerOne()
	{
		AssertThat(Engine_ControllerFamily.PromptPlayer(split: true, menuOwner: 2, lastUsedPlayer: 1)).IsEqual(2);
		AssertThat(Engine_ControllerFamily.PromptPlayer(split: true, menuOwner: 1, lastUsedPlayer: 2)).IsEqual(1);
		// Nothing open: player one's half is player one's, whoever pressed last (player two's half
		// draws player two's own set).
		AssertThat(Engine_ControllerFamily.PromptPlayer(split: true, menuOwner: 0, lastUsedPlayer: 2)).IsEqual(1);
	}

	[TestCase]
	public void OnOneScreenTheSharedSetFollowsTheLastPadUsed()
	{
		AssertThat(Engine_ControllerFamily.PromptPlayer(split: false, menuOwner: 0, lastUsedPlayer: 2)).IsEqual(2);
		AssertThat(Engine_ControllerFamily.PromptPlayer(split: false, menuOwner: 2, lastUsedPlayer: 1)).IsEqual(1);
		// Single player, before any pad was touched.
		AssertThat(Engine_ControllerFamily.PromptPlayer(split: false, menuOwner: 0, lastUsedPlayer: 0)).IsEqual(1);
	}

	[TestCase]
	public void OnlyPlayerTwoTakesTheSecondSet()
	{
		AssertThat(Engine_ControllerFamily.SurfaceSet(2)).IsEqual(2);
		AssertThat(Engine_ControllerFamily.SurfaceSet(1)).IsEqual(1);
		AssertThat(Engine_ControllerFamily.SurfaceSet(0)).IsEqual(1);
	}

	[TestCase]
	public void APromptStatedForPlayerTwosHeroTakesPlayerTwosSet()
	{
		// Fable 3's emotion choices state their owner as the hero's uid (rec+8); 0 is any player's.
		const ulong one = 0x000001D800000000ul, two = 0x00003BA700000000ul;
		AssertThat(Engine_ControllerFamily.OwnerSet(two, two)).IsEqual(2);
		AssertThat(Engine_ControllerFamily.OwnerSet(one, two)).IsEqual(1);
		AssertThat(Engine_ControllerFamily.OwnerSet(0ul, two)).IsEqual(1);
		// No local player two (solo, an online machine): every prompt takes the shared set.
		AssertThat(Engine_ControllerFamily.OwnerSet(two, 0ul)).IsEqual(1);
		AssertThat(Engine_ControllerFamily.OwnerSet(0ul, 0ul)).IsEqual(1);
	}

	[TestCase]
	public void EachPlayersPadDrawsItsOwnVocabulary()
	{
		// The game lane's case: player one on a Switch Pro pad, player two on an Xbox pad. Player two's
		// set reads B for the south button and A for the east one no longer.
		GlyphStyle one = Engine_ControllerFamily.Resolve("auto", ControllerFamily.Nintendo);
		GlyphStyle two = Engine_ControllerFamily.Resolve("auto", ControllerFamily.Xbox);
		AssertThat(Engine_ControllerFamily.Label(one, PadInput.A)).IsEqual("B");
		AssertThat(Engine_ControllerFamily.Label(two, PadInput.A)).IsEqual("A");
		AssertThat(Engine_ControllerFamily.Label(two, PadInput.B)).IsEqual("B");
	}

	[TestCase]
	public void TheQuadShaderSwitchesEachSetOnItsOwnGlobal()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string shader = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "ui", "ui_quad.gdshader"));
		AssertThat(shader.Contains("uniform bool glyph_player_two = false;", StringComparison.Ordinal)).IsTrue();
		AssertThat(shader.Contains("global uniform bool ui_keyboard_mode_two;", StringComparison.Ordinal)).IsTrue();
		AssertThat(shader.Contains("(glyph_player_two ? ui_keyboard_mode_two : ui_keyboard_mode)", StringComparison.Ordinal)).IsTrue();

		// A global the shader names must be declared, or the shader fails to compile.
		string project = File.ReadAllText(Path.Combine(root, "Godot", "project.godot"));
		AssertThat(project.Contains("ui_keyboard_mode_two={", StringComparison.Ordinal)).IsTrue();

		// The player-two surfaces open the scope: player two's HUD half and their world icons.
		string view = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "ui", "Engine_HudView.cs"));
		AssertThat(view.Contains("private int GlyphSurface => Seat == Engine_UiSeats.Seat.PlayerTwo ? 2 : 1;", StringComparison.Ordinal)).IsTrue();
		string ui = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "game", "GameUi.cs"));
		AssertThat(ui.Contains("GlyphPlayer = 2,", StringComparison.Ordinal)).IsTrue();
	}

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
}
