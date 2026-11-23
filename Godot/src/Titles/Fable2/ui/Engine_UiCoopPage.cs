using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE CO-OP SCREEN'S GUEST-DECIDED STATE (screen 24), composed from the screen's own data
/// (<see cref="Fable2Bridge.GetCoopState"/>) by its writers' rules, so the book reads
/// nothing back from the guest's units. What <c>CoopProvider</c> already owns (names, statuses,
/// the hero/henchman choice, the hidden sliders and save picker) stays there.
///
/// <list type="bullet">
/// <item>PICKBOXES (`sub_82881F58`): the caption is the option SetIndex (`sub_828821D0`) handed
/// the box's text element, as stated (<see cref="Engine_UiPickBoxes"/>); the frame on the
/// widget's own slide (+28: 1 normal, 2 focused, 3 disabled).</item>
/// <item>BUTTONS (`sub_82439988`, verified): with L = the right player signed in,
/// r = 0 unless (`!f297 || f298 && S==2`) and a right save user, then 2 if L, else 1 in local
/// co-op; q = `S==2 && !L && (!f297 || f298)`. `RightPage.buttons` slide = L ? (r==2 &amp;&amp; !s &amp;&amp;
/// !accR ? 2 : 3) : 1; the load caption CHANGE_/SELECT_SAVE by s; `RightPage.ProfileSelector`
/// slide when !L = (s || !q || accR) ? 5 : r==1 ? 3 : 4.</item>
/// <item>PROFILES (`sub_8281F010`): a published widget (+56 == 4) puts its selector on slide 2.</item>
/// <item>ACCEPT (`sub_8243AC48`, `sub_824398B8`): each side's `AcceptGroup` at 50 accepted, 100
/// not; with flag 298 each `CancelGroup` at 100 accepted, 50 not. Accepted is stated by
/// SetAccepted itself (<see cref="Engine_UiCoopStated"/>).</item>
/// <item>CONNECTING (`sub_82437810`): on the online host path while the peer is not ready, the
/// right page's selector, pickboxes and buttons hide and `Connecting.Text` is on slide 2 with
/// `GUI_SCREEN_COOP_CONNECTING_TO_X` (the peer's name) or `GUI_SCREEN_COOP_CONNECTING`.</item>
/// </list>
/// </summary>
public static class Engine_UiCoopPage
{
	private const string Right = "Foreground.RightPage.";

	private const string Left = "Foreground.LeftPage.";

	/// <summary>One frame's statements, or empty when the screen state is unknown.</summary>
	public sealed class Statements
	{
		public readonly Dictionary<string, string> Values = new();
		public readonly Dictionary<string, int> Slides = new();
		public readonly Dictionary<string, float> Opacities = new();
		public readonly Dictionary<string, bool> Active = new();
	}

	private static ulong _frame = ulong.MaxValue;

	private static Statements _said = new();

	public static Statements State(Refii.Text.Engine_BabelDatabase? strings, string peerName, bool remotePlayerTwo)
	{
		ulong frame = Godot.Engine.GetProcessFrames();
		if (frame == _frame)
		{
			return _said;
		}

		_frame = frame;
		_said = Compute(Fable2Bridge.GetCoopState(), strings, peerName, remotePlayerTwo);
		return _said;
	}

	private static string Text(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is not null && strings.TryGetString(tag, out string text) ? text : tag;

	private static Statements Compute(
		Fable2Bridge.CoopPageState coop, Refii.Text.Engine_BabelDatabase? strings,
		string peerName, bool remotePlayerTwo)
	{
		var said = new Statements();
		if (!coop.Known)
		{
			return said;
		}

		// The pickboxes: each caption as SetIndex stated it, under the text element Init named.
		for (int k = 0; k < 3; ++k)
		{
			string path = $"{Right}Pickbox.pickbox{k + 1}";
			if (Engine_UiPickBoxes.Caption(path + ".grp.text") is { } caption)
			{
				said.Values[path + ".grp.text"] = caption;
			}

			if (coop.PickState[k] is >= 1 and <= 3)
			{
				said.Slides[path] = coop.PickState[k];
			}
		}

		// Each page's accepted, as its SetAccepted stated it (Engine_UiCoopStated).
		bool acceptedLeft = Engine_UiCoopStated.Current?.LeftAccepted ?? false;
		bool acceptedRight = Engine_UiCoopStated.Current?.RightAccepted ?? false;

		// The buttons.
		bool local = coop.Session == 2;
		bool signedIn = coop.RightSignedIn;
		bool ok = !coop.Flag297 || (coop.Flag298 && local);
		int r = !ok || coop.UserRightSave == -1 ? 0 : signedIn ? 2 : local ? 1 : 0;
		bool q = local && !signedIn && (!coop.Flag297 || coop.Flag298);
		bool s = coop.SaveChosen;
		said.Slides[Right + "buttons"] = signedIn ? (r == 2 && !s && !acceptedRight ? 2 : 3) : 1;
		if (!remotePlayerTwo)
		{
			said.Values[Right + "buttons.LoadGroup.LoadText"] =
				Text(strings, s ? "GUI_BUTTONS_CHANGE_SAVE" : "GUI_BUTTONS_SELECT_SAVE");
		}

		// The profile selectors.
		if (coop.ProfileLeft == 4)
		{
			said.Slides[Left + "ProfileSelector"] = 2;
		}

		if (!signedIn)
		{
			said.Slides[Right + "ProfileSelector"] = s || !q || acceptedRight ? 5 : r == 1 ? 3 : 4;
		}
		else if (coop.ProfileRight == 4)
		{
			said.Slides[Right + "ProfileSelector"] = 2;
		}

		// Accept and cancel.
		said.Opacities[Left + "buttons.AcceptGroup"] = acceptedLeft ? 0.5f : 1f;
		said.Opacities[Right + "buttons.AcceptGroup"] = acceptedRight ? 0.5f : 1f;
		if (coop.Flag298)
		{
			said.Opacities[Left + "buttons.CancelGroup"] = acceptedLeft ? 1f : 0.5f;
			said.Opacities[Right + "buttons.CancelGroup"] = acceptedRight ? 1f : 0.5f;
		}

		said.Values[Left + "buttons.CancelGroup.CancelText"] = Text(strings, "GUI_SCREEN_COOP_REJECT");

		// Connecting: the online host waiting for its peer.
		bool connecting = !local && !coop.OnlineClient && !coop.PeerReady;
		said.Slides["Foreground.Connecting.Text"] = connecting ? 2 : 1;
		if (connecting)
		{
			said.Values["Foreground.Connecting.Text.Connecting"] = peerName.Length != 0
				? Text(strings, "GUI_SCREEN_COOP_CONNECTING_TO_X").Replace("%1", peerName, System.StringComparison.Ordinal)
				: Text(strings, "GUI_SCREEN_COOP_CONNECTING");
			said.Active[Right + "ProfileSelector"] = false;
			said.Active[Right + "Pickbox"] = false;
			said.Active[Right + "buttons"] = false;
		}

		return said;
	}
}
