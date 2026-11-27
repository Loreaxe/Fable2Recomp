using System;
using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// A HOST OPTIONS PAGE: value rows the host owns, standing on the options screen's own page
/// furniture. One kind for every such page — the dynamic sky's switches, a mod's settings —
/// so the window, the cursor, the left/right stepping, the captions, the pointer and the A/B
/// prompts are written once and every page gets them.
/// </summary>
/// <remarks>
/// <para>The furniture is the Game page's: five pickbox rows (<see cref="Rows"/>), each with
/// a label and a <c>.grp.text</c> caption, under the <see cref="TitleField"/> heading. A page
/// longer than five rows scrolls a window over them (<see cref="WindowStart"/>) and the
/// heading says where the window is. The page opens through <see cref="Engine_UiOptionsPending"/>
/// like a guest category page, so the same pending index per row, the same 28..31 codes and
/// the same commit/cancel on 36/37 drive it; only the guest is not told, because it has no page
/// behind this one.</para>
/// <para>A row is a PICKBOX: an index over <see cref="Choices"/> captions, stepped by left and
/// right without wrapping — the guest's `sub_828820B0` arithmetic. A numeric range is a pickbox
/// too: index <c>i</c> stands for <c>min + i·step</c>, which is what the shipped breadcrumb
/// size row is.</para>
/// <para>A page is reached by a menu row's <see cref="Engine_UiMenuEntry.Command"/>; the
/// registry maps the command's prefix to the page it opens (<see cref="Register"/>), and the
/// same lookup gives the list level its preview of a highlighted row's page.</para>
/// </remarks>
public abstract class Engine_UiHostOptionsPage
{
	/// <summary>The page's A and B prompts group.</summary>
	public const string Buttons = "Page1.Contents.Buttons.Buttons";

	/// <summary>The heading over the rows.</summary>
	public const string TitleField = "Page1.Contents.OptionName";

	/// <summary>The five pickbox rows, top to bottom, as the Game page authors them.</summary>
	public static readonly string[] Rows =
	{
		"Options.Subtitles_pickbox", "Options.BreadcrumbSize_pickbox",
		"Options.TutorialBoxes_pickbox", "Options.MultiplayerOrbs_pickbox",
		"Options.Auto_joinable_pickbox",
	};

	/// <summary>The identity <see cref="Engine_UiOptionsPending.Page"/> carries while this page is open.</summary>
	public abstract string Name { get; }

	/// <summary>The heading, before the window position is appended.</summary>
	public abstract string Title { get; }

	/// <summary>How many rows the page has.</summary>
	public abstract int Count { get; }

	/// <summary>A row's label.</summary>
	public abstract string RowTitle(int row);

	/// <summary>How many positions a row's index can take.</summary>
	public abstract int Choices(int row);

	/// <summary>The caption a row shows at an index.</summary>
	public abstract string Caption(int row, int index);

	/// <summary>
	/// The caption where the page's commit is the button named <paramref name="commitButton"/>: a
	/// caption that names the commit button (a reset row's) says that one.
	/// </summary>
	public virtual string Caption(int row, int index, string commitButton) => Caption(row, index);

	/// <summary>Every row's index as the committed values put it — the page's seed on entry and its preview at the list level.</summary>
	public abstract int[] Read();

	/// <summary>A row's index moved by left or right. A page that applies as it goes does it here.</summary>
	public virtual void Changed(int row, int index)
	{
	}

	/// <summary>A on the page; false keeps the page open. <paramref name="index"/> is each row's pending index.</summary>
	public virtual bool Commit(Func<int, int> index) => true;

	/// <summary>
	/// Whether A (Enter, a click) acts on the cursor ROW (<see cref="Accepted"/>) — a rebind row
	/// arms its capture, an opener opens its page — so the page's commit is another button of the
	/// driver's. False: A is the commit. A capability of the page; a driver opts in by reading it.
	/// </summary>
	public virtual bool RowsTakeAccept => false;

	/// <summary>Whether the page has values to commit; false for a page of openers.</summary>
	public virtual bool Commits => true;

	/// <summary>
	/// A on <paramref name="row"/> standing at <paramref name="index"/>: the index the row goes
	/// to. The driver stores it and calls <see cref="Changed"/>, which is where a capture arms. A
	/// row that opens another page does it here and answers <paramref name="index"/>.
	/// </summary>
	public virtual int Accepted(int row, int index) => index;

	/// <summary>The page open now, or null while the list level or a guest page is up.</summary>
	public static Engine_UiHostOptionsPage? Current { get; internal set; }

	public static bool Open => Current is not null;

	/// <summary>Where the five-row window starts for the open page's cursor.</summary>
	public static int VisibleStart =>
		Current is { } page ? page.WindowStart(Refii.Core.Engine_TitleGame.Active.OptionsCursorRow) : 0;

	/// <summary>The window start that keeps <paramref name="selected"/> on its last row once the page scrolls.</summary>
	public int WindowStart(int selected) =>
		Math.Clamp(selected - Rows.Length + 1, 0, Math.Max(0, Count - Rows.Length));

	/// <summary>The labels, captions and heading for the window starting at <paramref name="first"/>.</summary>
	public Dictionary<string, string> Text(Func<int, int> index, int first = 0)
	{
		var values = new Dictionary<string, string>();
		int start = Math.Clamp(first, 0, Math.Max(0, Count - Rows.Length));
		for (int row = 0; row < Rows.Length; row++)
		{
			int setting = row + start;
			bool shown = setting < Count;
			values[Rows[row] + ".grp.text"] = shown ? Caption(setting, index(setting)) : string.Empty;
			values[Rows[row][..^"_pickbox".Length]] = shown ? RowTitle(setting) : string.Empty;
		}

		values[TitleField] = Count > Rows.Length
			? $"{Title} ({start + 1}-{Math.Min(start + Rows.Length, Count)}/{Count})"
			: Title;
		return values;
	}

	/// <summary>
	/// Paints the page onto the options book: the open page at its pending indices, or the
	/// highlighted row's page at its committed ones as the list level's preview. Nothing when
	/// neither applies.
	/// </summary>
	public static void Apply(Engine_UiOwnedBook book, Engine_UiExpandableMenu.Item? row)
	{
		bool open = Open;
		Engine_UiHostOptionsPage? page = Current ?? For(row?.Command);
		if (page is null)
		{
			return;
		}

		int[] committed = open ? Array.Empty<int>() : page.Read();
		int Index(int at) => open
			? Refii.Core.Engine_TitleGame.Active.OptionsCursorIndex(at)
			: at < committed.Length ? committed[at] : 0;
		Paint(book, page.Paint(Index, open, open ? Refii.Core.Engine_TitleGame.Active.OptionsCursorRow : 0));
	}

	/// <summary>Writes one <see cref="Painted"/> onto the options book over what it already holds.</summary>
	public static void Paint(Engine_UiOwnedBook book, Painted painted)
	{
		book.HostValues = painted.Values;

		var active = new Dictionary<string, bool>(book.HostActive ?? new Dictionary<string, bool>());
		foreach ((string key, bool on) in painted.Active)
		{
			active[key] = on;
		}

		book.HostActive = active;

		var opacity = new Dictionary<string, float>(book.HostOpacity ?? new Dictionary<string, float>());
		foreach ((string key, float alpha) in painted.Opacity)
		{
			opacity[key] = alpha;
		}

		book.HostOpacity = opacity;
	}

	/// <summary>
	/// What the page writes onto the options book's channels: the labels, captions and heading;
	/// the page, its rows and the A/B prompts switched on or off; and each row's opacity.
	/// </summary>
	public readonly record struct Painted(
		Dictionary<string, string> Values,
		Dictionary<string, bool> Active,
		Dictionary<string, float> Opacity);

	/// <summary>
	/// The page as the options book shows it: <paramref name="index"/> each row's index,
	/// <paramref name="open"/> whether the page has the input (its prompts up, the rows but the
	/// cursor's dimmed) or is the list level's preview, <paramref name="cursor"/> the row the
	/// cursor is on. The game's options screen and the Mod Studio's preview both paint this.
	/// </summary>
	public Painted Paint(Func<int, int> index, bool open, int cursor)
	{
		int start = open ? WindowStart(cursor) : 0;
		var active = new Dictionary<string, bool>
		{
			["Foreground.Book.Pages.Page1"] = true,
			[Buttons] = open,
			[Buttons + ".Learn.Edit"] = false,
			[Buttons + ".Learn.Done"] = open,
		};
		var opacity = new Dictionary<string, float> { [Buttons] = 1f };
		for (int i = 0; i < Rows.Length; i++)
		{
			active[Rows[i]] = start + i < Count;
			opacity[Rows[i]] = !open || cursor == i + start ? 1f : 0.6f;
		}

		return new Painted(Text(index, start), active, opacity);
	}

	// ---------------------------------------------------------------------------------------
	// THE REGISTRY: a row's command names the page it opens.
	// ---------------------------------------------------------------------------------------

	private static readonly Dictionary<string, Func<string, Engine_UiHostOptionsPage?>> Openers =
		new(StringComparer.Ordinal);

	/// <summary>
	/// Adds a page family: a command starting with <paramref name="commandPrefix"/> resolves
	/// through <paramref name="resolve"/>, handed the rest of the command.
	/// </summary>
	public static void Register(string commandPrefix, Func<string, Engine_UiHostOptionsPage?> resolve) =>
		Openers[commandPrefix] = resolve;

	/// <summary>The page a command opens, or null when no family claims it.</summary>
	public static Engine_UiHostOptionsPage? For(string? command)
	{
		if (string.IsNullOrEmpty(command))
		{
			return null;
		}

		foreach ((string prefix, Func<string, Engine_UiHostOptionsPage?> resolve) in Openers)
		{
			if (command.StartsWith(prefix, StringComparison.Ordinal))
			{
				return resolve(command[prefix.Length..]);
			}
		}

		return null;
	}

	/// <summary>Opens the page a command names. False when the command is not a page's.</summary>
	public static bool Run(string action)
	{
		if (For(action) is not { } page)
		{
			return false;
		}

		Refii.Core.Engine_TitleGame.Active.EnterHostOptions(page);
		return true;
	}

	static Engine_UiHostOptionsPage()
	{
		Register(Engine_UiWeatherOptions.OpenCommand, _ => Engine_UiWeatherOptions.Page);
		Register(Refii.Mods.ModSettingsUi.OpenCommandPrefix, Refii.Mods.ModSettingsUi.PageFor);
		Register(Engine_UiControlsPage.OpenCommand, _ => Engine_UiControlsPage.Page);
		Register(Engine_UiDisplayOptions.OpenCommand, _ => Engine_UiDisplayOptions.Page);
	}
}
