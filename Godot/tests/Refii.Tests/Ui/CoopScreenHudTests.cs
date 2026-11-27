namespace Refii.Tests.Ui;

using GdUnit4;
using Refii.Ui;
using Refii.Titles.Fable2;
using static GdUnit4.Assertions;

/// <summary>
/// The local co-op screen's HUD: which pieces the whole window and each half draw (the guest's one
/// HUD carries both players: TU1 sub_821A5C68 / disc sub_821A5AC8 states hero one's PlayerHealth and
/// SpellCharging and hero two's CoopHealth and SpellCharging2 on the shared screen), and the runtime
/// meters following a live switch between split and shared. Pure: no Godot runtime object.
/// </summary>
[TestSuite]
public class CoopScreenHudTests
{
	[TestCase]
	public void TheSharedScreenDrawsBothPlayersColumnsAndOneOfEverythingElse()
	{
		Fable2HudLayout rules = Fable2HudLayout.Instance;
		AssertThat(rules.OwnerOf(Piece("group.CoopHealth.CoopHealth"))).IsEqual(Engine_UiSeats.Seat.PlayerTwo);
		AssertThat(rules.OwnerOf(Piece("group.SpellCharging2.SpellCharging2"))).IsEqual(Engine_UiSeats.Seat.PlayerTwo);
		AssertThat(rules.OwnerOf(Piece("group.PlayerHealth.PlayerHealth"))).IsEqual(Engine_UiSeats.Seat.PlayerOne);
		AssertThat(rules.OwnerOf(Piece("group.SpellCharging.SpellCharging"))).IsEqual(Engine_UiSeats.Seat.PlayerOne);

		// The quest box and the d-pad are the screen's: one each on the shared screen (the d-pad is
		// the GUI's one player's, sub_822D1B48 -> sub_821C7CE0), one per half on split.
		AssertThat(rules.OwnerOf(Piece("group.QuestBox.TopBox"))).IsEqual(Engine_UiSeats.Seat.Whole);
		AssertThat(rules.OwnerOf(Piece("slidetoggle.DPadUp.up"))).IsEqual(Engine_UiSeats.Seat.Whole);
		AssertThat(rules.SharedOnSplit(Piece("group.QuestBox.TopBox"))).IsTrue();
		AssertThat(rules.EachPlayerOnSplit(Piece("slidetoggle.DPadUp.up"))).IsTrue();
	}

	[TestCase]
	public void OnlyTheWholeWindowOfALocalCoopGameIsASharedScreenForTheMeters()
	{
		AssertThat(Engine_HudModMeters.SharedScreenView(wholeWindow: true, localPlayerTwo: false, localCoop: true)).IsTrue();
		// Player one's half, the joiner's own window, single player.
		AssertThat(Engine_HudModMeters.SharedScreenView(wholeWindow: false, localPlayerTwo: false, localCoop: true)).IsFalse();
		AssertThat(Engine_HudModMeters.SharedScreenView(wholeWindow: true, localPlayerTwo: true, localCoop: false)).IsFalse();
		AssertThat(Engine_HudModMeters.SharedScreenView(wholeWindow: true, localPlayerTwo: false, localCoop: false)).IsFalse();
	}

	[TestCase]
	public void TheMetersRebuildWhenTheScreenTurnsSharedOrSplitWithNoNewMeter()
	{
		// The same meter set, built for player one's half: a live switch to shared rebuilds it with
		// player two's meters, a switch back rebuilds it without.
		AssertThat(Engine_HudModMeters.Rebuilds(generation: 4, builtGeneration: 4, shared: true, builtShared: false)).IsTrue();
		AssertThat(Engine_HudModMeters.Rebuilds(generation: 4, builtGeneration: 4, shared: false, builtShared: true)).IsTrue();
		AssertThat(Engine_HudModMeters.Rebuilds(generation: 5, builtGeneration: 4, shared: false, builtShared: false)).IsTrue();
		// Nothing changed: nothing is rebuilt (single player every frame).
		AssertThat(Engine_HudModMeters.Rebuilds(generation: 4, builtGeneration: 4, shared: false, builtShared: false)).IsFalse();
	}

	private static Engine_HudLayout.Piece Piece(string id) =>
		new(id, "art", Engine_HudLayout.Anchor.TopLeft, Godot.Vector2.Zero, id, Engine_HudLayout.Drive.Bar);
}
