namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Achievements;
using Refii.Text;

/// <summary>
/// Fable 3's achievements page, as its rows read: a tally row (the hero's count, its gamerscore, the
/// profile's total — <see cref="Refii.Ui.Engine_UiAchievementPage.TallyLines"/>, the one wording both
/// titles' pages use), then one row per achievement in the title's order, its gamerscore on the line,
/// ghosted while the hero shown has not earned it, its description the text the title gives and whether
/// and when it was unlocked. Pure, so the tests run it.
/// </summary>
public static class Fable3AchievementsModel
{
	/// <summary>The tally row's name.</summary>
	public const string SummaryRow = "Summary";

	/// <summary>One row: its name, its line, ghosted, the description shown under the list while it is the cursor row.</summary>
	public readonly record struct Entry(string Name, string Line, bool Ghosted, string Description);

	/// <summary>
	/// The rows: the tally (<paramref name="tally"/>: the hero's line, then the lines under it), then each of
	/// <paramref name="definitions"/>; <paramref name="shows"/> whether the hero shown has an id,
	/// <paramref name="when"/> when it unlocked it (null for another player's), <paramref name="say"/> our words.
	/// </summary>
	public static List<Entry> Rows(IReadOnlyList<AchievementDefinition> definitions, System.Func<uint, bool> shows,
		System.Func<uint, System.DateTime?> when, IReadOnlyList<string> tally, System.Func<string, object[], string> say)
	{
		var rows = new List<Entry>(definitions.Count + 1);
		var under = new List<string>();
		for (int i = 1; i < tally.Count; ++i)
		{
			under.Add(tally[i]);
		}

		rows.Add(new Entry(SummaryRow, tally.Count > 0 ? tally[0] : string.Empty, false, string.Join("\n", under)));
		foreach (AchievementDefinition definition in definitions)
		{
			bool unlocked = shows(definition.Id);
			string name = definition.Name.Length == 0 ? "#" + definition.Id : definition.Name;
			string text = unlocked || definition.LockedDescription.Length == 0 ? definition.UnlockedDescription : definition.LockedDescription;
			string status = !unlocked ? say("REFII_ACH_LOCKED", System.Array.Empty<object>()) :
				when(definition.Id) is { } at
					? say("REFII_ACH_UNLOCKED_ON", new object[] { at.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) })
					: say("REFII_ACH_UNLOCKED", System.Array.Empty<object>());
			rows.Add(new Entry("Achievement:" + definition.Id, name + "    " + definition.Gamerscore + " G", !unlocked,
				text.Length == 0 ? status : text + "\n" + status));
		}

		return rows;
	}

	/// <summary>The tab strip: the opener's hero and the partner's, when a partner's view exists; none otherwise.</summary>
	public static IReadOnlyList<string>? Tabs(bool partner, System.Func<string, string> say) =>
		partner ? new[] { say("REFII_ACH_SHOW_OWN"), say("REFII_ACH_SHOW_PARTNER") } : null;
}

/// <summary>
/// THE ACHIEVEMENTS PAGE IN FABLE 3, in its PC layout (the one menu template, the Game Settings tab strip's
/// look). Neither Fable 3 build has an achievements screen or asks for the guide's: no
/// `XamShowAchievementsUI` among either executable's imports (TU3 xam.xex imports XamShowMessageBoxUI(Ex),
/// SigninUI, KeyboardUI, GamerCardUIForXUID, MarketplaceUI, DeviceSelectorUI, GameInviteUI,
/// DirtyDiscErrorUI; the disc the same and MarketplaceDownloadItemsUI), and the guest's own per-hero list
/// (`Stats.GetAllAchievementDetailsForGUI`, TU3 0x823EAF98) is called by no shipped screen — only
/// `Debug.PrintStats` in the game scripts; the PC build has no page of its own either. So the page is the
/// host's, reached as Fable 2's is: a host row on the Options list (pages 1 and 5,
/// <see cref="Fable3OptionsModel.HostRows"/>), opened for the player whose Options it is.
/// </summary>
/// <remarks>
/// It shows the opener's hero by default; when that player has a partner — local co-op, or online with the
/// partner's machine stating theirs over RexNet presence (<see cref="Engine_Achievements.PartnerView"/>) —
/// the tab strip switches to the partner's hero and the partner's profile total, read-only. Another
/// player's card (<see cref="Fable3GamerCard"/>) opens it on that player's view. B leaves; every open starts
/// on the opener's own hero.
/// </remarks>
public partial class Fable3AchievementsPage : Fable3HostSurface
{
	private enum Shown
	{
		Own,
		Partner,
		Peer,
	}

	private static bool _open;
	private static int _opener = Fable3ActingPlayer.One;
	private static Shown _shown;
	private static Engine_AchievementWire.View? _peer;
	private static string _peerName = string.Empty;
	private static bool _reset;

	private int[] _pads = { 0 };

	protected override string LogTag => "fable3-achievements";

	protected override IReadOnlyList<int> Pads => _pads;

	/// <summary>Opens the page on player <paramref name="player"/>'s own hero.</summary>
	public static void Open(int player)
	{
		_opener = player == Fable3ActingPlayer.Two ? Fable3ActingPlayer.Two : Fable3ActingPlayer.One;
		_shown = Shown.Own;
		_peer = null;
		_open = true;
		_reset = true;
		GD.Print($"[fable3-achievements] opened for player {_opener}");
	}

	/// <summary>Opens the page on another player's hero and profile, as their machine stated them (read-only).</summary>
	public static void OpenPeer(Engine_AchievementWire.View view, string name)
	{
		_opener = Fable3ActingPlayer.One;
		_shown = Shown.Peer;
		_peer = view;
		_peerName = name;
		_open = true;
		_reset = true;
		GD.Print($"[fable3-achievements] opened on '{name}'s achievements");
	}

	protected override Fable3ScreenPage? Page()
	{
		if (!_open)
		{
			return null;
		}

		if (_reset)
		{
			_reset = false;
			Cursor = 0;
		}

		_pads = _shown == Shown.Peer ? new[] { 0, Fable3ActingPlayer.PadOfTwo } : new[] { Fable3ActingPlayer.PadOf(_opener) };
		Engine_AchievementWire.View? partner = _shown == Shown.Peer ? null : Engine_Achievements.PartnerView(_opener);
		if (_shown == Shown.Partner && partner is null)
		{
			_shown = Shown.Own;
		}

		var prompts = new[] { new Fable3ScreenPage.Prompt("B", Engine_OwnWords.Say("REFII_BACK")) };
		string title = Engine_OwnWords.Say("REFII_ACHIEVEMENTS");
		if (!Engine_Achievements.DefinitionsLoaded)
		{
			var waiting = new[] { new Fable3ScreenPage.Row(Fable3AchievementsModel.SummaryRow, Engine_OwnWords.Say("REFII_ACH_NOT_READY"), true) };
			return new Fable3ScreenPage(title, waiting, 0, string.Empty, prompts, true);
		}

		Engine_AchievementWire.View? other = _shown switch { Shown.Partner => partner, Shown.Peer => _peer, _ => null };
		System.Func<uint, bool> shows = other is { } view ? view.Hero.Contains : id => Engine_Achievements.IsUnlocked(_opener, id);
		System.Func<uint, System.DateTime?> when = other is null ? id => Engine_Achievements.UnlockedAt(_opener, id) : _ => null;
		string[] tally = Refii.Ui.Engine_UiAchievementPage.TallyLines(_opener, other,
			_shown == Shown.Partner && Engine_Achievements.PartnerIsLocal(_opener), _shown != Shown.Peer);
		List<Fable3AchievementsModel.Entry> entries = Fable3AchievementsModel.Rows(Engine_Achievements.Definitions, shows, when,
			tally, Engine_OwnWords.Say);

		var rows = new List<Fable3ScreenPage.Row>(entries.Count);
		foreach (Fable3AchievementsModel.Entry entry in entries)
		{
			rows.Add(new Fable3ScreenPage.Row(entry.Name, entry.Line, entry.Ghosted, entry.Description));
		}

		IReadOnlyList<string>? tabs = _shown == Shown.Peer ? null : Fable3AchievementsModel.Tabs(partner is not null, Engine_OwnWords.Say);
		if (_shown == Shown.Peer)
		{
			title += " - " + _peerName;
		}

		return new Fable3ScreenPage(title, rows, Cursor, string.Empty, prompts, true, null, tabs,
			tabs is null ? -1 : _shown == Shown.Partner ? 1 : 0);
	}

	/// <summary>The rows are read-only.</summary>
	protected override void Accept(int row)
	{
	}

	protected override void Back()
	{
		_open = false;
		_peer = null;
		GD.Print("[fable3-achievements] closed");
	}

	protected override void StepTab(int step) => PickTab(_shown == Shown.Partner ? 0 : 1);

	protected override void PickTab(int tab)
	{
		if (_shown == Shown.Peer || Engine_Achievements.PartnerView(_opener) is null)
		{
			return;
		}

		Shown next = tab == 1 ? Shown.Partner : Shown.Own;
		if (next != _shown)
		{
			_shown = next;
			Cursor = 0;
			GD.Print($"[fable3-achievements] showing {(next == Shown.Partner ? "the partner's" : "the opener's own")} hero");
		}
	}
}
