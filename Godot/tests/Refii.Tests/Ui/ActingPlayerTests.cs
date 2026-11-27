namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;
using Refii.Ui;

/// <summary>
/// The acting player every handler lever carries (the guest's r5, its GUI input object's vf+16):
/// Fable 2's rule from Godot's device map, and the `lever:&lt;player&gt;:rest` form the pump parses.
/// </summary>
[TestSuite]
public class ActingPlayerTests
{
	[TestCase]
	public void SoloAndAHostAreAlwaysPlayerOne()
	{
		AssertThat(Fable2ActingPlayer.Of(client: false, localCoop: false, menuOwner: 0)).IsEqual(1);
		AssertThat(Fable2ActingPlayer.Of(client: false, localCoop: false, menuOwner: 2)).IsEqual(1);
	}

	[TestCase]
	public void AnOnlineClientsPadIsPlayerTwos()
	{
		AssertThat(Fable2ActingPlayer.Of(client: true, localCoop: false, menuOwner: 0)).IsEqual(2);
	}

	[TestCase]
	public void LocalCoopFollowsTheStatedMenuOwner()
	{
		AssertThat(Fable2ActingPlayer.Of(client: false, localCoop: true, menuOwner: 0)).IsEqual(1);
		AssertThat(Fable2ActingPlayer.Of(client: false, localCoop: true, menuOwner: 1)).IsEqual(1);
		AssertThat(Fable2ActingPlayer.Of(client: false, localCoop: true, menuOwner: 2)).IsEqual(2);
	}

	[TestCase]
	public void ABoxOverTheLocalCoopScreenIsPlayerTwos()
	{
		AssertThat(Fable2ActingPlayer.BoxOwner(localCoop: true, screenId: 24u, modalUp: true)).IsEqual(2);
		AssertThat(Fable2ActingPlayer.BoxOwner(localCoop: true, screenId: 24u, modalUp: false)).IsEqual(0);
		AssertThat(Fable2ActingPlayer.BoxOwner(localCoop: true, screenId: 0u, modalUp: true)).IsEqual(0);
		AssertThat(Fable2ActingPlayer.BoxOwner(localCoop: false, screenId: 24u, modalUp: true)).IsEqual(0);
	}

	[TestCase]
	public void PlayerTwoAnswersEveryBoxWhileTheyOwnTheGui()
	{
		AssertThat(Fable2ActingPlayer.Answerer(statedOwner: 2, modalMode: 1u)).IsEqual(2);
		AssertThat(Fable2ActingPlayer.Answerer(statedOwner: 2, modalMode: 3u)).IsEqual(2);
		AssertThat(Fable2ActingPlayer.Answerer(statedOwner: 1, modalMode: 1u)).IsEqual(1);
		AssertThat(Fable2ActingPlayer.Answerer(statedOwner: 1, modalMode: 3u)).IsEqual(0);
	}

	/// <summary>
	/// Player two's arrest (int7, 13:26): the crime screen was up a frame before the owner the arrest
	/// stated reached the host (`menu owner tag: player 1 over screen 21`, then `the open menu is player
	/// 2's`), and the tag latched that first frame. It follows the stated owner instead.
	/// </summary>
	[TestCase]
	public void TheTagFollowsTheOwnerStatedAfterTheScreenOpened()
	{
		int shown = Fable2ActingPlayer.TagOwner(0, menuUp: true, inputOwner: 1, statedOwner: 1);
		AssertThat(shown).IsEqual(1);
		shown = Fable2ActingPlayer.TagOwner(shown, menuUp: true, inputOwner: 2, statedOwner: 2);
		AssertThat(shown).IsEqual(2);
		AssertThat(Fable2ActingPlayer.TagOwner(shown, menuUp: false, inputOwner: 2, statedOwner: 2)).IsEqual(0);
	}

	/// <summary>A box over player one's menu states no input owner (Answerer 0): the tag keeps naming
	/// player one; with nothing named yet it names the GUI's stated owner.</summary>
	[TestCase]
	public void TheTagKeepsItsPlayerThroughABoxThatStatesNone()
	{
		AssertThat(Fable2ActingPlayer.TagOwner(1, menuUp: true, inputOwner: 0, statedOwner: 2)).IsEqual(1);
		AssertThat(Fable2ActingPlayer.TagOwner(2, menuUp: true, inputOwner: 0, statedOwner: 1)).IsEqual(2);
		AssertThat(Fable2ActingPlayer.TagOwner(0, menuUp: true, inputOwner: 0, statedOwner: 2)).IsEqual(2);
	}

	[TestCase]
	public void TheCoopScreensBoxesAreAnsweredByGodot()
	{
		static Engine_UiModalTemplate Row(uint id, bool dismissal)
		{
			foreach (Engine_UiModalTemplate row in Engine_UiModals.Shipped())
			{
				if (row.ModalId == id && row.Dismissal == dismissal)
				{
					return row;
				}
			}

			throw new System.InvalidOperationException($"no row for box {id}");
		}

		AssertThat(Row(39u, false).Confirm).IsEqual(36u);
		AssertThat(Row(39u, false).Cancel).IsEqual(37u);
		AssertThat(Row(41u, false).Confirm).IsEqual(36u);
		AssertThat(Row(40u, true).Confirm).IsEqual(36u);
		AssertThat(Row(43u, true).Confirm).IsEqual(36u);
	}

	[TestCase]
	public void TheLeverLeadsWithThePlayer()
	{
		AssertThat(Engine_UiSource.PlayerLever("QueryAnswer", 2, "46@3")).IsEqual("QueryAnswer:2:46@3");
		AssertThat(Engine_UiSource.PlayerLever("BoxAction", 1, "36@4C001000")).IsEqual("BoxAction:1:36@4C001000");
	}

	[TestCase]
	public void TheEditorsSourcePostsPlayerOne()
	{
		var source = new Engine_UiAuthoredSource();
		source.ScreenAction(37u);
		source.PostPlayerLever(Engine_UiReceiveItemsSurface.Lever, Engine_UiReceiveItemsSurface.Dismiss);
		AssertThat(source.Posted).ContainsExactly("ScreenAction:1:37", "Card:1:dismiss");
	}
}
