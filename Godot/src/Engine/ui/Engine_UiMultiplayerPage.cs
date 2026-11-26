using System.Collections.Generic;
using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// The multiplayer book: friends, requests, invitations and joinable games as one list, with
/// the highlighted row's detail on the page beside it.
/// </summary>
/// <remarks>
/// A HOST-ONLY SCREEN, like <see cref="Engine_UiAchievementPage"/> and for the same reason —
/// the guest has no screen for any of this, so its id is above
/// <see cref="Engine_UiBookSet.HostOnlyScreenId"/> and its manager never reports it.
///
/// ONE LIST WITH GROUP HEADERS rather than four sections. `Engine_UiMenuSources.Row.Group`
/// already builds a header row per distinct group, folding and ghosting exactly like an authored
/// category, so the four subjects cost one source and one fold mechanism instead of four screens.
/// </remarks>
public static class Engine_UiMultiplayerPage
{
	/// <summary>Above <see cref="Engine_UiBookSet.HostOnlyScreenId"/>, clear of achievements.</summary>
	public const uint ScreenId = 1014u;

	public const string MenuName = "multiplayer-root";

	/// <summary>
	/// The Active Mods book: the same page over a list of the enabled mods, each row's page its
	/// description. Opened from a mod row under the title's Downloadable Content.
	/// </summary>
	public const uint ModsScreenId = 1015u;

	public const string ModsMenuName = "mods-root";

	/// <summary>The list, as <see cref="Engine_UiMenuSources"/> registers it.</summary>
	public const string SourceName = "multiplayer";

	/// <summary>The borrowed page — the same one the achievements book writes into.</summary>
	private const string PageRoot = "Foreground.Book.Pages.ExpressionsPage";

	private const string Title = "ExpressionsPage.Title";
	private const string Description = "ExpressionsPage.Contents.Description";
	private const string Effects = "ExpressionsPage.Contents.Effects";
	private const string Icon = "ExpressionsPage.Contents.Icon";

	private static readonly string[] OtherPages =
	{
		"Foreground.Book.Pages.MoralityPage", "Foreground.Book.Pages.CompletedQuestsPage",
		"Foreground.Book.Pages.RenownPage", "Foreground.Book.Pages.AttractivenessPage",
		"Foreground.Book.Pages.StatsPage", "Foreground.Book.Pages.DogPage",
		"Foreground.Book.Pages.HelpPage",
	};

	private static readonly Dictionary<string, string> NoValues = new();

	private static readonly Dictionary<string, uint> NoTextures = new();

	private static readonly Dictionary<string, bool> NoActive = new();

	/// <summary>Is this row one of ours? Headers included — a header's page is its summary.</summary>
	public static bool Owns(Engine_UiExpandableMenu.Item? row) =>
		row is not null &&
		(row.Name.StartsWith("friend:", System.StringComparison.Ordinal) ||
		 row.Name.StartsWith("request:", System.StringComparison.Ordinal) ||
		 row.Name.StartsWith("invite:", System.StringComparison.Ordinal) ||
		 row.Name.StartsWith("game:", System.StringComparison.Ordinal) ||
		 row.Name.StartsWith("none:", System.StringComparison.Ordinal) ||
		 row.Name.StartsWith("self:", System.StringComparison.Ordinal) ||
		 row.Name.StartsWith(Engine_UiNetworkSource.ModRowPrefix, System.StringComparison.Ordinal) ||
		 Engine_UiNetworkSource.IsGroupName(row.Name));

	public static IReadOnlyDictionary<string, string> Values(Engine_UiExpandableMenu.Item? row)
	{
		if (row is null)
		{
			return NoValues;
		}

		Engine_UiNetworkSource.CurrentRow = row.Name;
		return new Dictionary<string, string>
		{
			// THE ROW'S LABEL, not its name. `Name` is the identity the commands are keyed on
			// ("MyCode"); `TextTag` is what the row draws — the game's key or one of our own,
			// said as the running book, then our own words, have it.
			[Title] = row.StatedLabel is { Length: > 0 } stated
				? stated
				: row.TextTag is { Length: > 0 } tag && tag != "_no tag_"
					? Engine_UiGuiText.Resolve(tag, Refii.Core.Engine_TitleGame.Active.Strings)
					: row.Name,
			// AN OUTCOME FIRST. A press answers on this same page, never in a box (a box draws
			// under the book and blocks it); the plain page returns once it fades.
			[Description] = Engine_UiNetworkSource.Said(row.Name) is { Length: > 0 } said
				? said
				: Owns(row)
					? Engine_UiNetworkSource.Detail(row.Name)
					: Engine_UiNetworkSource.ActionDetail(row.Name),
		};
	}

	public static IReadOnlyDictionary<string, uint> Textures(Engine_UiExpandableMenu.Item? row) =>
		NoTextures;

	/// <summary>
	/// Our page on, every other subject page off.
	/// </summary>
	/// <remarks>
	/// The book draws whatever is active, and `logbookscreen` authors eight subject pages over
	/// each other. Naming only ours would leave the other seven wherever the master baseline put
	/// them, which is on.
	/// </remarks>
	public static IReadOnlyDictionary<string, bool> Active(Engine_UiExpandableMenu.Item? row)
	{
		// UNCONDITIONAL. `logbookscreen` authors eight subject pages over each other, and this
		// map is the only thing switching the other seven off — returning nothing for a row we
		// do not own let all eight draw at once, which is the overlapping text on the left
		// list's action rows.
		var active = new Dictionary<string, bool>
		{
			[PageRoot] = true,
			[Effects] = false,
			[Icon] = false,
		};

		foreach (string page in OtherPages)
		{
			active[page] = false;
		}

		return active;
	}
}
