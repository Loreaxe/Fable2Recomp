namespace Refii.Tests.Titles;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Game;
using Refii.Titles.Fable3;

/// <summary>
/// The PC build's extra keys on Fable 3: the actions they post, and their places in the key table.
/// </summary>
[TestSuite]
public class Fable3PcExtrasTests
{
	/// <summary>Each extra names its player; anything but player two is player one.</summary>
	[TestCase]
	public void AnExtraCarriesItsPlayer()
	{
		AssertThat(Fable3PcExtras.QuickSave(Fable3ActingPlayer.One)).IsEqual("pc-extra:quicksave:1");
		AssertThat(Fable3PcExtras.QuickSave(Fable3ActingPlayer.Two)).IsEqual("pc-extra:quicksave:2");
		AssertThat(Fable3PcExtras.Safety(Fable3ActingPlayer.One)).IsEqual("pc-extra:safety:1");
		AssertThat(Fable3PcExtras.Safety(Fable3ActingPlayer.Two)).IsEqual("pc-extra:safety:2");
	}

	/// <summary>
	/// The keys post once per press in play only, stand clear of the host's own F5 and F6, and
	/// leave the Back button on Backspace.
	/// </summary>
	[TestCase]
	public void TheKeysArePlayOnlyAndLeaveTheHostsKeysAlone()
	{
		GameInput.KeyAction? save = null, safety = null, back = null;
		foreach (GameInput.KeyAction action in new Fable3Input().KeyActions)
		{
			switch (action.Name)
			{
				case "quick_save": save = action; break;
				case "safety_mode": safety = action; break;
				case "nickname": back = action; break;
			}

			foreach (string key in action.Defaults.Split(','))
			{
				AssertThat(key is "F5" or "F6").IsFalse();
			}
		}

		AssertThat(save!.Value.Defaults).IsEqual("F9");
		AssertThat(save.Value.Press).IsEqual(GameInput.KeyPress.HostAction);
		AssertThat(save.Value.When).IsEqual(GameInput.KeyContext.Play);
		AssertThat(save.Value.HostAction).IsEqual("pc-extra:quicksave:1");
		AssertThat(safety!.Value.Defaults).IsEqual("G");
		AssertThat(safety.Value.HostAction).IsEqual("pc-extra:safety:1");
		AssertThat(safety.Value.When).IsEqual(GameInput.KeyContext.Play);
		AssertThat(back!.Value.Defaults).IsEqual("Backspace");
	}

	/// <summary>
	/// The three extras, and only they, take a pad chord: unbound by default (no token in the
	/// defaults), and a chord's press acts for the player whose pad made it.
	/// </summary>
	[TestCase]
	public void TheExtrasTakeAPadChordForThePlayerWhoPressedIt()
	{
		var bindable = new System.Collections.Generic.List<string>();
		foreach (GameInput.KeyAction action in new Fable3Input().KeyActions)
		{
			AssertThat(Refii.Core.Engine_PadActionBinding.HasPadToken(action.Defaults)).IsFalse();
			if (!action.PadBindable)
			{
				continue;
			}

			bindable.Add(action.Name);
			if (action.Press == GameInput.KeyPress.HostAction)
			{
				AssertThat(action.HostActionFor!(Fable3ActingPlayer.One)).IsEqual(action.HostAction);
				AssertThat(action.HostActionFor!(Fable3ActingPlayer.Two)).IsEqual(action.HostAction[..^1] + "2");
			}
		}

		AssertThat(bindable.ToArray()).IsEqual(new[] { "quick_save", "safety_mode", Fable3LitePause.Action });
	}
}
