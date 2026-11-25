using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The open options page's row cursor and row values — Godot's record of the page level.
///
/// The guest's options handler is ported (gui_screen_stub_options.cpp, screen 18): its OnEnter keeps every
/// page's reader (TU1 `sub_82990610` → page vtable +4), which STATES the committed values — each
/// pickbox's index (`@PickBox.Set`), the Audio page's slider values (`@Slider.Set`) — and the profile
/// gamma and the audio levels come from their own getters and setters (<see cref="Refii.Host.HostBridge.GetOptionSliders"/>).
/// Its input slot does nothing, so the page level is Godot's: <see cref="Enter"/> seeds the page from
/// those statements, <see cref="Track"/> steps it with the guest widgets' own arithmetic, and every
/// consequence is an `Options:&lt;p&gt;:&lt;setting&gt;:&lt;value&gt;` lever that runs the guest's
/// setter for it:
/// <list type="bullet">
/// <item>Pickbox rows step by one: toggles, orbs and speaker wrap (the list widget's ctor
/// `sub_82881F58` sets +60 = 1), the breadcrumb row does not (its populate `sub_829BE4C8` clears +60,
/// 0x829BE5C8). Sliders step 1/20 and clamp to 0..1 (`sub_82881D60` → `sub_82881E38`).</item>
/// <item>Left/right on the Audio page applies live (`sub_829C1488(page, 0)` → the four leaves); on
/// the Video page it previews brightness (`gammalive`, the page's per-frame +12 `sub_829C0AB0`).</item>
/// <item>A commits the page (Game `sub_829BF698`, Controls `sub_829C00E0`, Video `sub_829C07D0`,
/// Audio `sub_829C1488(page, 1)`) and closes it; B closes it — Video puts the preview back to the
/// value the page opened with (`sub_829C09F0`), Audio leaves the live levels where its last
/// left/right put them (its B re-applies them, 0x829C16FC).</item>
/// </list>
///
/// A HOST page (<see cref="Engine_UiHostOptionsPage"/>) keeps the same record; its consequences are
/// the page's own (<see cref="Engine_UiHostOptionsPage.Changed"/> / <see cref="Engine_UiHostOptionsPage.Commit"/>).
/// </summary>
public static class Engine_UiOptionsPending
{
	/// <summary>One row of a guest page: its widget's element path, its choice count (0 for a slider),
	/// whether stepping past an end wraps, and the `Options:` setting it commits.</summary>
	private readonly record struct GuestRow(string Path, int Choices, bool Wraps, string Setting);

	private const string Pages = "Foreground.Book.Pages.";

	/// <summary>The suffix a pickbox's text element carries (`lhGuiPickBox_Init`).</summary>
	private const string PickBoxText = ".grp.text";

	/// <summary>The slider widget's step, `1 / +44` with +44 = 20 (`sub_82881D60`).</summary>
	public const float SliderStep = 1f / 20f;

	/// <summary>
	/// The guest's pages, by the name the list row carries: per row in the guest's key order, the path
	/// its widget is initialised on (the page populates TU1 `sub_829BE130`, `sub_829BFCF0`,
	/// `sub_829C08B8`, `sub_829C0F40`).
	/// </summary>
	private static readonly Dictionary<string, GuestRow[]> GuestPages = new(StringComparer.Ordinal)
	{
		["Game"] = new[]
		{
			new GuestRow(Pages + "Page1.Contents.Options.Subtitles_pickbox", 2, true, "subtitles"),
			new GuestRow(Pages + "Page1.Contents.Options.BreadcrumbSize_pickbox", 5, false, "breadcrumbs"),
			new GuestRow(Pages + "Page1.Contents.Options.TutorialBoxes_pickbox", 2, true, "tutorials"),
			new GuestRow(Pages + "Page1.Contents.Options.MultiplayerOrbs_pickbox", 3, true, "orbs"),
			new GuestRow(Pages + "Page1.Contents.Options.Auto_joinable_pickbox", 2, true, "autojoin"),
		},
		["Controls"] = new[] { new GuestRow(Pages + "Page2.Contents.pickbox1", 2, true, "invert") },
		["Video"] = new[] { new GuestRow(Pages + "Page3.Contents.slider1", 0, false, "gamma") },
		["Audio"] = new[]
		{
			new GuestRow(Pages + "Page4.Contents.slider1", 0, false, "sfx"),
			new GuestRow(Pages + "Page4.Contents.slider2", 0, false, "music"),
			new GuestRow(Pages + "Page4.Contents.slider3", 0, false, "speech"),
			new GuestRow(Pages + "Page4.Contents.pickbox1", 3, true, "speaker"),
		},
	};

	/// <summary>Which category page is open, or empty when the list level is up.</summary>
	public static string Page { get; private set; } = string.Empty;

	public static bool Open => Page.Length != 0;

	/// <summary>The page's row cursor.</summary>
	public static int Row => _row;

	private static int _row;

	/// <summary>How many rows the cursor walks: player two's Game page has its subtitle row alone.</summary>
	private static int _rows;

	/// <summary>The pending index per row (pickbox rows; -1 on a slider row or one never stated).</summary>
	private static int[] _index = Array.Empty<int>();

	/// <summary>The pending level per row (slider rows).</summary>
	private static float[] _level = Array.Empty<float>();

	/// <summary>The levels the guest page opened with, which B puts back on the Video page.</summary>
	private static float[] _opened = Array.Empty<float>();

	/// <summary>Each row's choice count — a HOST page's.</summary>
	private static int[] _count = Array.Empty<int>();

	/// <summary>The open guest page's rows, or null for a host page or none.</summary>
	private static GuestRow[]? GuestRows =>
		Engine_UiHostOptionsPage.Current is null && GuestPages.TryGetValue(Page, out var rows) ? rows : null;

	/// <summary>Whether <paramref name="page"/> names one of the guest's four pages.</summary>
	public static bool IsGuestPage(string page) => GuestPages.ContainsKey(page);

	/// <summary>A guest page's slider row's pending value, or null for a pickbox row or no page.</summary>
	public static float? SliderValue(int row) =>
		GuestRows is { } rows && row >= 0 && row < rows.Length && rows[row].Choices == 0 ? _level[row] : null;

	/// <summary>
	/// The levels the options screen SHOWS. While a guest page is open, its pending sliders and speaker;
	/// otherwise each widget's stated value over the level the game runs with — the Audio page's reader
	/// (`sub_829C0F40`) states the profile's levels into its widgets on the screen's enter and after a
	/// commit, which is what the page shows even when a cancelled edit left the live levels elsewhere.
	/// </summary>
	public static Refii.Host.HostBridge.OptionSliders Pending(Refii.Host.HostBridge.OptionSliders committed)
	{
		if (!committed.Known)
		{
			return committed;
		}

		if (GuestRows is not null && Page == "Video")
		{
			return committed with { Gamma = _level[0] };
		}

		if (GuestRows is not null && Page == "Audio")
		{
			return committed with { Sfx = _level[0], Music = _level[1], Speech = _level[2], Speaker = _index[3] };
		}

		int speaker = Engine_UiPickBoxes.Index(Pages + "Page4.Contents.pickbox1" + PickBoxText);
		return committed with
		{
			Sfx = Engine_UiRowWidgets.Value(Pages + "Page4.Contents.slider1") ?? committed.Sfx,
			Music = Engine_UiRowWidgets.Value(Pages + "Page4.Contents.slider2") ?? committed.Music,
			Speech = Engine_UiRowWidgets.Value(Pages + "Page4.Contents.slider3") ?? committed.Speech,
			Speaker = speaker >= 0 ? speaker : committed.Speaker,
		};
	}

	/// <summary>
	/// A guest page's pickbox row's index as the guest last stated it; -1 for a slider row, one not yet
	/// stated or no such page.
	/// </summary>
	public static int StatedIndex(string page, int row) =>
		GuestPages.TryGetValue(page, out var rows) && row >= 0 && row < rows.Length && rows[row].Choices > 0
			? Engine_UiPickBoxes.Index(rows[row].Path + PickBoxText)
			: -1;

	/// <summary>A row's pending pickbox index, or -1 for a slider row or one not yet stated.</summary>
	public static int IndexOf(int row) => row >= 0 && row < _index.Length ? _index[row] : -1;

	/// <summary>
	/// Opens the guest page named by the list row, seeded from what the guest states: each pickbox's
	/// stated index, each slider's stated widget value over <paramref name="committed"/> (gamma has no
	/// widget until a page focus the port never runs, so it is the profile's). An unknown or empty name
	/// is refused. <paramref name="playerTwo"/>: the Game page's rows 1..4 exist only for player one's
	/// book (`sub_829BE130` 0x829BE1AC..0x829BE1B8).
	/// </summary>
	public static bool Enter(string page, Refii.Host.HostBridge.OptionSliders committed, bool playerTwo = false)
	{
		if (!GuestPages.TryGetValue(page, out GuestRow[]? rows))
		{
			return false;
		}

		Leave();
		Page = page;
		_rows = page == "Game" && playerTwo ? 1 : rows.Length;
		_index = new int[rows.Length];
		_level = new float[rows.Length];
		for (int row = 0; row < rows.Length; ++row)
		{
			_index[row] = rows[row].Choices > 0 ? Engine_UiPickBoxes.Index(rows[row].Path + PickBoxText) : -1;
			if (rows[row].Choices == 0)
			{
				_level[row] = Engine_UiRowWidgets.Value(rows[row].Path) ?? Committed(committed, rows[row].Setting);
			}
		}

		if (page == "Audio" && _index[3] < 0 && committed.Known)
		{
			_index[3] = committed.Speaker;
		}

		_opened = (float[])_level.Clone();
		return true;
	}

	private static float Committed(Refii.Host.HostBridge.OptionSliders committed, string setting) => setting switch
	{
		"gamma" => committed.Gamma,
		"sfx" => committed.Sfx,
		"music" => committed.Music,
		"speech" => committed.Speech,
		_ => 0f,
	};

	public static void Leave()
	{
		Page = string.Empty;
		_row = 0;
		_rows = 0;
		_index = Array.Empty<int>();
		_level = Array.Empty<float>();
		_opened = Array.Empty<float>();
		_count = Array.Empty<int>();
		Engine_UiHostOptionsPage.Current = null;
	}

	/// <summary>
	/// Enter a HOST page — one with no guest page behind it (<see cref="Engine_UiHostOptionsPage"/>).
	/// Seeded from the page's committed indices.
	/// </summary>
	public static void EnterHost(Engine_UiHostOptionsPage page)
	{
		if (Engine_UiHostOptionsPage.Current == page) return;
		Leave();
		Page = page.Name;
		_index = page.Read();
		_rows = page.Count;
		_count = new int[_index.Length];
		for (int i = 0; i < _count.Length; i++) _count[i] = page.Choices(i);
		Engine_UiHostOptionsPage.Current = page;
	}

	/// <summary>Sets a HOST page row's pending index (a rebind row's captured press, a reset); nothing on a guest page.</summary>
	public static void SetIndex(int row, int index)
	{
		if (Engine_UiHostOptionsPage.Current is not { } host || row < 0 || row >= _index.Length || index < 0)
		{
			return;
		}

		_index[row] = Math.Min(index, Math.Max(0, host.Choices(row) - 1));
	}

	/// <summary>One code on the open page: the cursor, a value step, the commit or the cancel.</summary>
	public static void Track(uint code)
	{
		if (Engine_UiHostOptionsPage.Current is { } host)
		{
			TrackHost(host, code);
			return;
		}

		if (GuestRows is not { } rows)
		{
			return;
		}

		switch (code)
		{
			case Engine_UiScreenInput.Up:
				_row = Math.Max(0, _row - 1);
				break;
			case Engine_UiScreenInput.Down:
				_row = Math.Min(_rows - 1, _row + 1);
				break;
			case Engine_UiScreenInput.Left:
			case Engine_UiScreenInput.Right:
				Step(rows, code == Engine_UiScreenInput.Right ? 1 : -1);
				break;
			case Engine_UiScreenInput.Select:
				Commit(rows);
				Leave();
				break;
			case Engine_UiScreenInput.Back:
				if (Page == "Video")
				{
					Lever("gammalive", _opened[0]);
				}

				Leave();
				break;
		}
	}

	/// <summary>One left/right on the cursor's row; the Audio and Video pages apply it live.</summary>
	private static void Step(GuestRow[] rows, int direction)
	{
		GuestRow row = rows[_row];
		if (row.Choices == 0)
		{
			float stepped = Math.Clamp(_level[_row] + (direction * SliderStep), 0f, 1f);
			if (stepped == _level[_row])
			{
				return;
			}

			_level[_row] = stepped;
			Lever(row.Setting == "gamma" ? "gammalive" : row.Setting, stepped);
			return;
		}

		int index = _index[_row];
		if (index < 0)
		{
			return;
		}

		int next = StepIndex(index, direction, row.Choices, row.Wraps);
		if (next == index)
		{
			return;
		}

		_index[_row] = next;
		if (row.Setting == "speaker")
		{
			Lever(row.Setting, next);
		}
	}

	/// <summary>The list widget's step (`sub_828820B0` → `sub_828821D0`): past an end it wraps when the
	/// widget wraps, else it stays.</summary>
	public static int StepIndex(int index, int direction, int choices, bool wraps)
	{
		int next = index + direction;
		if (next >= 0 && next < choices)
		{
			return next;
		}

		return !wraps ? index : next < 0 ? choices - 1 : 0;
	}

	/// <summary>A on a guest page: its commit's setters, as levers.</summary>
	private static void Commit(GuestRow[] rows)
	{
		switch (Page)
		{
			case "Audio":
				Engine_UiSource.Current.PostPlayerLever("Options", "audio:" +
					$"{Number(_level[0])}:{Number(_level[1])}:{Number(_level[2])}:{Math.Max(0, _index[3])}");
				return;
			case "Video":
				Lever("gamma", _level[0]);
				return;
		}

		for (int row = 0; row < _rows; ++row)
		{
			if (_index[row] >= 0)
			{
				Lever(rows[row].Setting, _index[row]);
			}
		}
	}

	private static void Lever(string setting, float value) =>
		Engine_UiSource.Current.PostPlayerLever("Options", $"{setting}:{Number(value)}");

	private static string Number(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);

	/// <summary>A HOST page's code: its record steps here and its own hooks apply it.</summary>
	private static void TrackHost(Engine_UiHostOptionsPage host, uint code)
	{
		if (code == Engine_UiScreenInput.Select && !host.Commit(IndexOf))
		{
			return;
		}

		switch (code)
		{
			case Engine_UiScreenInput.Up:
				_row = Math.Max(0, _row - 1);
				break;
			case Engine_UiScreenInput.Down:
				_row = Math.Min(host.Count - 1, _row + 1);
				break;
			case Engine_UiScreenInput.Left:
				if (_row < _index.Length && _index[_row] > 0)
				{
					--_index[_row];
					host.Changed(_row, _index[_row]);
				}
				break;
			case Engine_UiScreenInput.Right:
				if (_row < _index.Length && _index[_row] >= 0 && _index[_row] < _count[_row] - 1)
				{
					++_index[_row];
					host.Changed(_row, _index[_row]);
				}
				break;
			case Engine_UiScreenInput.Select:
			case Engine_UiScreenInput.Back:
				// 36 commits, 37 discards — either way the page closes.
				Leave();
				break;
		}
	}
}
