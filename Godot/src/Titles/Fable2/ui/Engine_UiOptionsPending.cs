using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The open options page's row cursor and row values, for the host's captions, pips and pointer.
///
/// A GUEST page (Game, Controls, Video, Audio) owns all of it, and STATES it. Its handler moves
/// the cursor on 28/29 through the row selector, which calls SetSelected on the old and the new
/// row's widget (`@Widget.Select`); 30/31 go to the cursor's widget, whose pickbox steps through
/// SetIndex (`@PickBox.Set`) or slider through SetValue (`@Slider.Set`); 36 commits the page and
/// 37 re-populates it through the same calls. Each row is the element path its widget was
/// initialised on, so <see cref="Row"/>, <see cref="IndexOf"/> and <see cref="SliderValue"/> are
/// what the guest last stated for those paths (<see cref="Engine_UiRowWidgets"/>,
/// <see cref="Engine_UiPickBoxes"/>) and nothing here steps on a code sent to a guest page. The
/// guest's captions are a DIRECT element text write that the owned clone does not carry, which is
/// why the host paints them at all — from the same tables, at the guest's index. The page closes
/// when the guest's slide does, never on a code the host sent.
///
/// A HOST page (<see cref="Engine_UiHostOptionsPage"/>) has no guest behind it, so the record
/// kept here IS its state: <see cref="Track"/> steps the cursor and the indices with the
/// pickbox's own arithmetic, and 36/37 close it.
/// </summary>
public static class Engine_UiOptionsPending
{
	/// <summary>Which category page is open, or empty when the list level is up.</summary>
	public static string Page { get; private set; } = string.Empty;

	public static bool Open => Page.Length != 0;

	/// <summary>The page's row cursor — the row the guest last selected on a guest page, the host's on a host page.</summary>
	public static int Row
	{
		get
		{
			if (GuestRows is not { } rows)
			{
				return _row;
			}

			for (int row = 0; row < rows.Length; ++row)
			{
				if (Engine_UiRowWidgets.IsSelected(rows[row].Path))
				{
					return row;
				}
			}

			return 0;
		}
	}

	/// <summary>A HOST page's row cursor.</summary>
	private static int _row;

	/// <summary>A HOST page's pending index per row.</summary>
	private static int[] _index = Array.Empty<int>();

	/// <summary>Each row's choice count — a HOST page's, the only one the host steps.</summary>
	private static int[] _count = Array.Empty<int>();

	/// <summary>The suffix a pickbox's text element carries (`lhGuiPickBox_Init`).</summary>
	private const string PickBoxText = ".grp.text";

	private const string Pages = "Foreground.Book.Pages.";

	/// <summary>
	/// The guest's pages, by the name the list row carries: per row in the guest's key order, the
	/// path its widget is initialised on (the page populates, TU1 `sub_829BE130`, `sub_829BFCF0`,
	/// `sub_829C08B8` and the Audio page's) and whether it is a pickbox (else a slider).
	/// </summary>
	private static readonly Dictionary<string, (string Path, bool Pickbox)[]> GuestPages = new(StringComparer.Ordinal)
	{
		["Game"] = new[]
		{
			(Pages + "Page1.Contents.Options.Subtitles_pickbox", true),
			(Pages + "Page1.Contents.Options.BreadcrumbSize_pickbox", true),
			(Pages + "Page1.Contents.Options.TutorialBoxes_pickbox", true),
			(Pages + "Page1.Contents.Options.MultiplayerOrbs_pickbox", true),
			(Pages + "Page1.Contents.Options.Auto_joinable_pickbox", true),
		},
		["Controls"] = new[] { (Pages + "Page2.Contents.pickbox1", true) },
		["Video"] = new[] { (Pages + "Page3.Contents.slider1", false) },
		["Audio"] = new[]
		{
			(Pages + "Page4.Contents.slider1", false),
			(Pages + "Page4.Contents.slider2", false),
			(Pages + "Page4.Contents.slider3", false),
			(Pages + "Page4.Contents.pickbox1", true),
		},
	};

	/// <summary>The open guest page's rows, or null for a host page or none.</summary>
	private static (string Path, bool Pickbox)[]? GuestRows =>
		Engine_UiHostOptionsPage.Current is null && GuestPages.TryGetValue(Page, out var rows) ? rows : null;

	/// <summary>A guest page's slider row's stated value, or null for a pickbox row, one not yet stated or no page.</summary>
	public static float? SliderValue(int row) =>
		GuestRows is { } rows && row >= 0 && row < rows.Length && !rows[row].Pickbox
			? Engine_UiRowWidgets.Value(rows[row].Path)
			: null;

	/// <summary>
	/// The levels the options screen SHOWS: each of its widgets' stated value over the level the
	/// game runs with. The screen populates its Audio widgets from the profile when it opens and
	/// again on cancel (TU1 `sub_829C0F40`), after handing the edited levels to the audio manager
	/// (`sub_829C15E8` 0x829c16fc), and its Video slider on entering the page (`sub_829C08B8`) —
	/// so the widgets, not the running levels, are what the page draws.
	/// </summary>
	public static Refii.Host.HostBridge.OptionSliders Pending(Refii.Host.HostBridge.OptionSliders committed)
	{
		if (!committed.Known)
		{
			return committed;
		}

		int speaker = Engine_UiPickBoxes.Index(Pages + "Page4.Contents.pickbox1" + PickBoxText);
		return committed with
		{
			Gamma = Engine_UiRowWidgets.Value(Pages + "Page3.Contents.slider1") ?? committed.Gamma,
			Sfx = Engine_UiRowWidgets.Value(Pages + "Page4.Contents.slider1") ?? committed.Sfx,
			Music = Engine_UiRowWidgets.Value(Pages + "Page4.Contents.slider2") ?? committed.Music,
			Speech = Engine_UiRowWidgets.Value(Pages + "Page4.Contents.slider3") ?? committed.Speech,
			Speaker = speaker >= 0 ? speaker : committed.Speaker,
		};
	}

	/// <summary>
	/// A guest page's pickbox row's index as the guest last stated it, open or not; -1 for a slider
	/// row, one not yet stated or no such page.
	/// </summary>
	public static int StatedIndex(string page, int row) =>
		GuestPages.TryGetValue(page, out var rows) && row >= 0 && row < rows.Length && rows[row].Pickbox
			? Engine_UiPickBoxes.Index(rows[row].Path + PickBoxText)
			: -1;

	/// <summary>A row's pickbox index, or -1 for a slider row or one not yet stated.</summary>
	public static int IndexOf(int row)
	{
		if (GuestRows is { } rows)
		{
			return row >= 0 && row < rows.Length && rows[row].Pickbox
				? Engine_UiPickBoxes.Index(rows[row].Path + PickBoxText)
				: -1;
		}

		return row >= 0 && row < _index.Length ? _index[row] : -1;
	}

	/// <summary>
	/// Enter the guest page named by the list row. Nothing is seeded: the rows are what the guest
	/// states. An empty name is no page and is refused — the list's highlight can be null for a
	/// frame, and that must not close or reopen the page.
	/// </summary>
	public static void Enter(string page)
	{
		if (page.Length == 0 || string.Equals(Page, page, StringComparison.Ordinal))
		{
			return;
		}

		Page = page;
		_row = 0;
		_index = Array.Empty<int>();
		_count = Array.Empty<int>();
	}

	public static void Leave()
	{
		Page = string.Empty;
		_row = 0;
		_index = Array.Empty<int>();
		_count = Array.Empty<int>();
		Engine_UiHostOptionsPage.Current = null;
	}

	/// <summary>
	/// Enter a HOST page — one with no guest page behind it (<see cref="Engine_UiHostOptionsPage"/>).
	/// Seeded from the page's committed indices; the codes that follow are tracked here and never
	/// sent to the guest.
	/// </summary>
	public static void EnterHost(Engine_UiHostOptionsPage page)
	{
		if (Engine_UiHostOptionsPage.Current == page) return;
		Page = page.Name;
		_row = 0;
		_index = page.Read();
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

	/// <summary>
	/// One screen action the host just sent to a HOST page. A guest page's codes move the
	/// guest's own record, which it states — nothing is stepped here for one.
	/// </summary>
	public static void Track(uint code)
	{
		if (Engine_UiHostOptionsPage.Current is not { } host)
		{
			return;
		}

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
