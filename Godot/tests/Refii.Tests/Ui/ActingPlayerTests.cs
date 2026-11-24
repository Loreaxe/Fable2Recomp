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
		source.PostPlayerLever("ReceiveItemsAction", "36");
		AssertThat(source.Posted).ContainsExactly("ScreenAction:1:37", "ReceiveItemsAction:1:36");
	}
}
