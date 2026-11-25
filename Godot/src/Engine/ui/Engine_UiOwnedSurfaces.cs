using Godot;

namespace Refii.Ui;

// The concrete owned surfaces — one per presentation Godot draws instead of mirroring. Each
// states which presentation it owns, how to build its view, how to resolve its join against the
// file, and what the guest stated for it to show. Frame: Engine_UiSurface.cs; views:
// Engine_UiBoxes.cs; the stated boxes: Engine_UiStatedBoxes.cs (the title's statements).

/// <summary>
/// The owned MenuBox (screen id 9) — the option list `GUI.DisplayMenuBox`, the Orb, the sleep
/// menu and the rest open through `sub_823DC100`. Its populate states the title and rows
/// (`@MenuBox.Open`, Engine_UiStatedBoxes.Menu) and its close the answer; Godot draws them,
/// moves the highlight, and answers through the guest's own accept, which posts message 35.
///
/// Navigation moves the guest's own selection, not a host copy: its selector reslides the two
/// affected buttons, so the accept lever and the drawn card always agree.
/// </summary>
public sealed class Engine_UiMenuBoxSurface : Engine_UiModalSurface<Engine_UiMenuBox>
{
	/// <summary>
	/// The guest's throttle: `sub_823DBC70` keeps one timestamp at `box+36` and refuses up, down,
	/// A and B alike until 0.3 s have passed since the last action taken.
	/// </summary>
	private const double StepInterval = 0.3;

	public override string Presentation => Engine_UiMenuBox.Presentation;

	public override string Label => "menu box";

	protected override float Depth => PanelDepth;

	private Engine_UiMenuBox.Joins? _joins;

	private Engine_UiModel.MenuBoxState _box;

	private double _repeat;

	protected override bool Joined => _joins is not null;

	protected override Engine_UiMenuBox Build(Engine_UiTheme? art) =>
		Engine_UiMenuBox.FromPatternBook(art);

	protected override void Join(Engine_UiScreen document)
	{
		_joins = Engine_UiMenuBox.Resolve(document);
		_rootHash = document.Nodes.Length > 0 ? document.Nodes[0].Element.NameHash : 0u;
	}

	/// <summary>The document root's name hash — how told statements key this presentation.</summary>
	private uint _rootHash;

	/// <summary>The told sequence when this box opened: the card's reveal must come after it.</summary>
	private ulong _openedAt;

	private bool _open;

	/// <summary>
	/// Whether the player card is up — when the GUEST REVEALED it. The Orb box builds a card
	/// (`sub_823DB850`) and the box's +44 holds its record from the open, but the card's fill
	/// (`sub_8281F010`) runs only once the gamerpic it asked Xbox Live for has loaded
	/// (`sub_82A3F280(card+8) == 3`), and only then writes the name and score and puts
	/// `Group.GamerTag` on slide 2. A peer's profile is not served here, so the guest never
	/// reveals it and the card stays at its authored opacity 0 — the sample gamertag, score and
	/// picture in the file are the artist's.
	/// </summary>
	private bool GamerTagShown() =>
		_box.GamerCard && _joins!.GamerTag >= 0 && _joins.GamerTagReveal >= 0 &&
		Engine_UiSlideStatements.Of(Address, _rootHash, _joins.GamerTagReveal) is { Slide: 2 } reveal &&
		reveal.Sequence > _openedAt;

	/// <summary>
	/// The text the guest told an element since this box opened. Only the card's SCORE is read so:
	/// the fill writes `card+44` (`sub_8281F010` 0x8281f1d0), which the card's own profile read
	/// sets (`sub_8281EF80` 0x8281eff0, setting 0x10040006) — no call states it.
	/// </summary>
	private string? Told(int ordinal) =>
		ordinal < 0 ? null : Engine_UiSlideStatements.TextOf(Address, _rootHash, ordinal, _openedAt);

	protected override bool IsOpen(Engine_UiModel.PauseState? pause)
	{
		if (Engine_UiSource.Current.GetMenuBox() is not { } box)
		{
			_open = false;
			return false;
		}

		if (!_open)
		{
			_open = true;
			_openedAt = Engine_UiSlideStatements.Sequence;
		}

		_box = box;
		return true;
	}

	protected override void Released()
	{
		// Nothing to reset: `box+36` is a wall-clock stamp, not a release latch — a fresh press
		// within 0.3 s is refused whether or not the stick recentred.
	}

	protected override string Unstated => "no populate was stated for the box that is up";

	protected override bool ApplyHost(Engine_UiPresentationHost host)
	{
		if (View is null || _joins is null)
		{
			return false;
		}

		// THE POPULATE'S TITLE AND ROWS, as its statement carries them: `sub_823DC400` (disc
		// `sub_8240C630`) is the one writer of `Group.Title` (`box+32`, 0x823dc444 / 0x8240c674) and
		// each row's `Choice1.Frame.Choice1` (its resolved tag, 0x823dc580 / 0x8240c7b0) — those names'
		// only users (0x83497CF0 / 0x83497CFC, disc 0x83497BE0 / 0x83497BEC) — and every populate that
		// returns 1 is stated, a row's enable (`sub_823DBBD0`) included. The controller is one object,
		// so an open statement with the box's row count is the box that is up. The selection stays
		// the guest's, the lever its accept reads.
		if (Engine_UiStatedBoxes.Current.Menu is not { } stated || stated.Model.Options.Count != _box.Count)
		{
			return false;
		}

		var labels = new string?[Engine_UiMenuBox.MaxOptions];
		string title = stated.Model.Text;
		for (int i = 0; i < labels.Length && i < stated.Model.Options.Count; ++i)
		{
			labels[i] = stated.Model.Options[i].Label;
		}

		bool gamerTag = GamerTagShown();
		string? gamerName = gamerTag ? stated.GamerName : null;
		string? gamerScore = gamerTag ? Told(_joins!.GamerScore) : null;
		if (!Changed($"{title}|{string.Join('\u001F', labels)}|{_box.Count}|{_box.Selected}|" +
			$"{_box.EnabledMask}|{_box.Cancelable}|{gamerTag}|{gamerName}|{gamerScore}"))
		{
			return true;
		}

		View.ShowStated(title, labels, _box.Count, _box.Selected,
			_box.EnabledMask, _box.Cancelable, gamerTag, gamerName, gamerScore);
		GD.Print($"[game-ui] menu box up — '{title}', {_box.Count} option(s), " +
			$"selected {_box.Selected}, enabled 0x{_box.EnabledMask:X} (populate of 0x{stated.Address:X8})" +
			(gamerTag ? $", card '{gamerName}' (stated) score '{gamerScore}' (told)" : string.Empty));
		return true;
	}

	/// <summary>
	/// A click on an option answers it, the way a select then A would; the cancel prompt is B.
	/// Disabled options are offered to the guest regardless — it re-checks the enabled byte.
	/// </summary>
	protected override Engine_UiSurfaceInput Point(Engine_UiSurfaceInput input, Vector2 at)
	{
		int option = View?.OptionAt(at) ?? -1;
		if (option == -2)
		{
			return input with { Back = true };
		}

		if (option >= 0 && option < _box.Count)
		{
			Engine_UiSource.Current.PostFrontEndAction($"MenuBoxSelect:{option}");
			Engine_UiSource.Current.PostFrontEndAction($"MenuBoxAnswer:{option + 1}");
			GD.Print($"[game-ui] menu box option {option} clicked");
		}

		return input;
	}

	protected override void Route(Engine_UiSurfaceInput input)
	{
		// One clock for every action, as `sub_823DBC70` keeps it: nothing — step, A or B — is
		// honoured until `_repeat` reaches zero. The pump's cases 109/114 (up/down) wrap:
		// `sel - 1 < 0 → count - 1`, `sel + 1 >= count → 0`.
		_repeat -= input.Delta;
		if (_repeat > 0.0)
		{
			return;
		}

		if (input.Direction != 0 && _box.Count > 0)
		{
			_repeat = StepInterval;
			int next = (_box.Selected + input.Direction + _box.Count) % _box.Count;
			Engine_UiSource.Current.PostFrontEndAction($"MenuBoxSelect:{next}");
			return;
		}

		if (input.Accept)
		{
			// The guest re-checks the enabled byte; a disabled row does not answer.
			_repeat = StepInterval;
			Engine_UiSource.Current.PostFrontEndAction($"MenuBoxAnswer:{_box.Selected + 1}");
			GD.Print($"[game-ui] menu box option {_box.Selected} chosen" +
				(_box.IsEnabled(_box.Selected)
					? string.Empty
					: " — DISABLED, the guest will refuse"));
		}
		else if (input.Back && _box.Cancelable)
		{
			_repeat = StepInterval;
			Engine_UiSource.Current.PostFrontEndAction("MenuBoxAnswer:0");
			GD.Print("[game-ui] menu box cancelled");
		}
	}
}

/// <summary>
/// The owned MessageBox (modal mode 2) — the dismiss-only sibling of the query box.
///
/// The guest fits its text into one of six authored size variants and states the choice as the
/// variant carrying live text, plus the nine-slice's live scale. The dismiss code is per (box
/// id, underlying screen): query id 0 dismisses on 36 at the shop and 46 at the real-estate
/// screen; PHOTOMODE's copy has no dismiss (the upload closes it), so it stays unowned and
/// unmasked.
/// </summary>
public sealed class Engine_UiMessageSurface : Engine_UiModalSurface<Engine_UiMessageBox>
{
	public override string Presentation => Engine_UiMessageBox.Presentation;

	public override string Label => "message box";

	private Engine_UiMessageBox.Joins? _joins;

	/// <summary>The router box's dismiss code, null when the table states none.</summary>
	private uint? _dismiss;

	protected override bool TakesInput => _display != 0 || _dismiss is not null;

	private uint _modalId;

	private uint _screenId;

	/// <summary>
	/// The display MessageBox the guest stated open (vtable 0x820AE668), 0 when none — set by the
	/// owner each frame. The same `messageboxmulti` art as the router's box, but it holds no modal
	/// id: it answers action 36 through its own handler, so it is answered by address.
	/// </summary>
	public uint StatedDisplayBox { get; set; }

	/// <summary>The display box this surface opened for, 0 when it is the router's.</summary>
	private uint _display;

	protected override bool Joined => _joins is not null;

	protected override Engine_UiMessageBox Build(Engine_UiTheme? art) =>
		Engine_UiMessageBox.FromPatternBook(art);

	private Engine_UiScreen? _document;

	protected override void Join(Engine_UiScreen document)
	{
		_document = document;
		_joins = Engine_UiMessageBox.Resolve(document);
	}

	/// <summary>
	/// THE PAPER'S SIZE IS A SLIDE, NOT A TRANSFORM. The guest's fitter puts the `paper`
	/// element on one of its `type1`..`type6` slides and writes no scale or position; this
	/// is that slide, from the file.
	/// </summary>
	private BgfSlide? SizeSlide(int chosen)
	{
		if (_document?.Document is not { } file || _joins is null)
		{
			return null;
		}

		string want = $"type{chosen + 1}";
		foreach (BgfSlide slide in file.Slides)
		{
			if (slide.OwnerElement == _joins.Paper &&
				slide.Name.Equals(want, System.StringComparison.Ordinal))
			{
				return slide;
			}
		}

		return null;
	}

	protected override bool IsOpen(Engine_UiModel.PauseState? pause)
	{
		// DRAWN WHETHER OR NOT ITS CODE IS KNOWN: an unlisted box keeps the guest's own input
		// (see Engine_UiModals), not the guest's hidden tree.
		if (pause is { MessageBoxUp: true } state)
		{
			_dismiss = Engine_UiTemplates.Modal(state.ModalId, state.ScreenId, dismissal: true)
				?.Confirm;
			_modalId = state.ModalId;
			_screenId = state.ScreenId;
			_display = 0;
			return true;
		}

		// A HOST notice is not this box's: it stands on the query box's paper, which is sized to
		// its text (Engine_UiQuerySurface), as the front end's dialog is (GameUi.DriveFrontEndHostBox).
		_display = StatedDisplayBox;
		return _display != 0;
	}

	protected override string Unstated => _display != 0
		? $"the display message box 0x{_display:X8} stated no body"
		: $"router message box {_modalId}'s open was not stated (@Modal.Open)";

	protected override bool ApplyHost(Engine_UiPresentationHost host)
	{
		if (View is null || _joins is null)
		{
			return false;
		}

		// THE OPENER'S OWN TEXT: the router's (`sub_8236FA68`'s argument, Engine_UiModalText), or a
		// display box's own Open (`box+8`, Engine_UiGuiBoxes.StatedMessageBodies).
		string? body = _display == 0
			? Engine_UiModalText.For(2u, _modalId)
			: Engine_UiGuiBoxes.StatedMessageBodies.TryGetValue(_display, out string? opened) ? opened : null;
		if (body is null)
		{
			return false;
		}

		if (!Changed($"{_display:X8}|{_modalId}|{body}"))
		{
			return true;
		}

		// THE SIZE IS OURS: the guest's fitter's rule over our own layout (Engine_UiMessageBox.Fit),
		// so the variant the guest activated is not read.
		int chosen = View.Fit(body) is int fitted and >= 0 ? fitted : 0;

		BgfSlide? size = SizeSlide(chosen);
		string paperSource = size is null ? "authored" : $"slide type{chosen + 1}";

		View.ShowStated(body, chosen, size);
		GD.Print((_display != 0
				? $"[game-ui] display message box {_display:X8} up "
				: $"[game-ui] message box {_modalId} up over screen {_screenId} ") +
			$"(size {chosen + 1}, paper {paperSource}) — " + View.LastReport);
		return true;
	}

	protected override void Route(Engine_UiSurfaceInput input)
	{
		if (!input.Accept)
		{
			return;
		}

		if (_display != 0)
		{
			Engine_UiSource.Current.PostPlayerLever("BoxAction",
				$"{(int)Engine_UiSource.BoxAction.Dismiss}@{_display:X8}");
			GD.Print($"[game-ui] display message box {_display:X8} dismissed (code 36)");
			return;
		}

		if (_dismiss is not { } code)
		{
			return;
		}

		Engine_UiSource.Current.PostPlayerLever("QueryAnswer", $"{code}@{_modalId}");
		GD.Print($"[game-ui] message box {_modalId} dismissed (code {code})");
	}
}

/// <summary>
/// The owned in-game query box, game-wide: the modal router's (modal id at guiOwner+44, QueryBox
/// mode at +40, its text stated by the opener `sub_8236FA68` as `@Modal.Open`) or a display
/// question (class 0x820AE688, stated by its Open `sub_823CF228` as `@DisplayBox.Open` kind 2).
/// Godot draws it from that statement and answers through the guest's own router or the box's own
/// handler. A router box whose answer codes are not in the modal table is still drawn but keeps
/// its own input — see <see cref="Engine_UiModals"/>.
/// </summary>
public sealed class Engine_UiQuerySurface : Engine_UiModalSurface<Engine_UiQueryBox>
{
	public override string Presentation => Engine_UiQueryBox.Presentation;

	public override string Label => "query box";

	private Engine_UiQueryBox.Joins? _joins;

	/// <summary>The codes the box that is up answers with, latched at the gate so the routing
	/// does not look them up again.</summary>
	private Engine_UiModalTemplate? _codes;

	private uint _modalId;

	private uint _screenId;

	/// <summary>
	/// The box up is a DISPLAY question, not the modal router's — as its Open stated it. Same
	/// presentation, different answer: its own handler `sub_823CF3B0` takes 46/47 and posts reply
	/// 1/0, addressed to this box (`BoxAction:<code>@<box>`).
	/// </summary>
	private Engine_UiStatedBoxes.Box? _display;

	protected override bool TakesInput => _display is not null || _codes is not null || _host;

	/// <summary>
	/// The box up is the HOST's — a question (<see cref="Engine_UiHostBoxes.Question"/>), or a
	/// notice (<see cref="Engine_UiHostBoxes.Notice"/>) when no question is up: one paper for both
	/// in game, sized to the text, the notice with its first choice alone.
	/// </summary>
	private bool _host;

	/// <summary>The host box up is a notice: one answer, A or B dismiss it.</summary>
	private bool _notice;

	protected override bool Joined => _joins is not null;

	protected override Engine_UiQueryBox Build(Engine_UiTheme? art) =>
		Engine_UiQueryBox.FromPatternBook(art);

	protected override void Join(Engine_UiScreen document)
	{
		_joins = Engine_UiQueryBox.Resolve(document);
		_document = document;
	}

	private Engine_UiScreen? _document;

	/// <summary>
	/// The row the opener stands up, by modal id: the router's `sub_8236FA68` slides the root for
	/// two ids only — 20 to slide 3, `XAChoices` (0x8236fb00..18), 13 to slide 5, `ReloadChoices`
	/// (0x8236fb28..40) — and every other box, and the display query (`sub_823CF228`, slide 1),
	/// keeps the default `Choices` pair. No code writes a caption: each row's are the file's.
	/// </summary>
	private Engine_UiQueryBox.RowJoin RowFor(uint modalId)
	{
		string wanted = _display is not null ? Engine_UiQueryBox.ChoiceRows[0].Row : modalId switch
		{
			20u => Engine_UiQueryBox.ChoiceRows[1].Row,
			13u => Engine_UiQueryBox.ChoiceRows[2].Row,
			_ => Engine_UiQueryBox.ChoiceRows[0].Row,
		};
		foreach (Engine_UiQueryBox.RowJoin row in _joins!.Rows)
		{
			if (row.Name == wanted)
			{
				return row;
			}
		}

		return _joins.Rows[0];
	}

	/// <summary>An element's authored caption, resolved; null where it authors none.</summary>
	private string? Authored(int ordinal) =>
		_document is { } document && ordinal >= 0 && ordinal < document.Nodes.Length &&
			document.Nodes[ordinal].Text is { Display.Length: > 0 } text
				? text.Display
				: null;

	/// <summary>The paper's scroll bar: the received-item card's `Scrollbar`, a box's own.</summary>
	protected override void Dress(Engine_UiPresentationHost host)
	{
		if (View?.Paper is { } paper)
		{
			paper.BarArt = host.PatternBookFor(Engine_UiReceiveItems.Presentation);
		}
	}

	protected override void Released() => View?.Release();

	protected override bool IsOpen(Engine_UiModel.PauseState? pause)
	{
		// DRAWN WHETHER OR NOT ITS CODES ARE KNOWN — see the message surface.
		// The guest's box outranks a host question, and the flag is re-derived on every path: one
		// left over from a host question routed every press of the guest's next box to nothing.
		_host = false;
		if (pause is { QueryBoxUp: true } state)
		{
			_codes = Engine_UiTemplates.Modal(state.ModalId, uint.MaxValue, dismissal: false);
			_display = null;
			_modalId = state.ModalId;
			_screenId = state.ScreenId;
			return true;
		}

		_codes = null;
		_display = Engine_UiStatedBoxes.Current.ShowingQuery;
		// A host box needs the box's join to be shown; without one it must not hold the frame.
		// In game only: out of it the front end's own dialog draws it (GameUi.DriveFrontEndHostBox).
		// A question stands before a notice, as on the front end.
		_notice = Engine_UiHostBoxes.Question is null;
		_host = _display is null && (Engine_UiHostBoxes.Question ?? Engine_UiHostBoxes.Notice) is not null &&
			Refii.Host.HostBridge.IsInGame && (View is null || _joins is not null);
		return _display is not null || _host;
	}

	protected override string Unstated => _display is not null
		? $"the display question 0x{_display.Address:X8} stated nothing"
		: $"router query box {_modalId}'s open was not stated (@Modal.Open)";

	protected override bool ApplyHost(Engine_UiPresentationHost host)
	{
		if (!_host)
		{
			return ApplyGuest();
		}

		if ((Engine_UiHostBoxes.Question ?? Engine_UiHostBoxes.Notice) is not { } body || _joins is null)
		{
			return false;
		}

		// The default A/B row; its captions the question's own where it states them, else the
		// authored ones. A notice stands its A alone, captioned as the message box's dismiss.
		string? accept = _notice ? View?.Theme?.Resolve("GUI_CONTINUE") : Engine_UiHostBoxes.AcceptCaption;
		string? cancel = _notice ? null : Engine_UiHostBoxes.CancelCaption;
		if (Changed($"host:{(_notice ? "N" : "Q")}\u0001{body}\u0001{accept}\u0001{cancel}"))
		{
			View!.ShowStated(body, _joins.Rows[0].Name, accept, cancel, single: _notice);
			GD.Print($"[game-ui] host {(_notice ? "notice" : "query")} up — {View.LastReport}");
		}

		return true;
	}

	/// <summary>The guest's box: the opener's own text — the router's (Engine_UiModalText) or the
	/// display question's Open (`box+8`) — on the row its opener stands, captioned by the file.</summary>
	private bool ApplyGuest()
	{
		if (View is null || _joins is null)
		{
			return false;
		}

		string? body = _display is { } box ? box.Body : Engine_UiModalText.For(3u, _modalId);
		if (body is null)
		{
			return false;
		}

		Engine_UiQueryBox.RowJoin row = RowFor(_modalId);
		string? first = Authored(row.First);
		string? second = Authored(row.Second);
		if (!Changed($"{_display?.Address:X8}\u0001{_modalId}\u0001{row.Name}\u0001{body}\u0001{first}\u0001{second}"))
		{
			return true;
		}

		View.ShowStated(body, row.Name, first, second);
		GD.Print((_display is { } shown
				? $"[game-ui] display query 0x{shown.Address:X8} up "
				: $"[game-ui] query box {_modalId} up over screen {_screenId} ") +
			$"({row.Name}) — {View.LastReport}");
		return true;
	}

	/// <summary>A click on the A choice confirms, on the second choice cancels — or answers the
	/// alternate where the box's second choice is X (`XAChoices`) — on the scroll bar drags it;
	/// elsewhere nothing.</summary>
	protected override Engine_UiSurfaceInput Point(Engine_UiSurfaceInput input, Vector2 at) =>
		View is not null && View.PressBar(at)
			? input
			: View?.AnswerAt(at) switch
			{
				1 => input with { Accept = true },
				-1 when _codes is { Cancel: null, Alternate: not null } => input with { Equip = true },
				-1 => input with { Back = true },
				_ => input,
			};

	protected override void Route(Engine_UiSurfaceInput input)
	{
		// The body's scroll, for a text taller than the paper's window: the right stick and the
		// d-pad at the page rate, the wheel and the bar through the pointer.
		View?.Scroll(input.Scroll, input.Direction, input.Delta);

		if (_host)
		{
			if (_notice && (input.Accept || input.Back))
			{
				// One answer: B clears a notice as A does — a box only one key could close is a deadlock.
				GD.Print("[game-ui] host notice dismissed");
				Engine_UiHostBoxes.Dismiss();
			}
			else if (input.Accept || input.Back)
			{
				GD.Print($"[game-ui] host query {(input.Accept ? "CONFIRMED" : "cancelled")}");
				Engine_UiHostBoxes.Answer(input.Accept);
			}

			return;
		}

		if (_display is { } box)
		{
			if (input.Accept || input.Back)
			{
				Engine_UiSource.BoxAction code = input.Accept
					? Engine_UiSource.BoxAction.Confirm
					: Engine_UiSource.BoxAction.Cancel;
				Engine_UiSource.Current.PostPlayerLever("BoxAction", $"{(int)code}@{box.Address:X8}");
				GD.Print($"[game-ui] display query 0x{box.Address:X8} {(input.Accept ? "CONFIRMED" : "cancelled")}");
			}

			return;
		}

		if (_codes is not { } codes)
		{
			return;
		}

		if (input.Accept)
		{
			Engine_UiSource.Current.PostPlayerLever("QueryAnswer", $"{codes.Confirm}@{_modalId}");
			GD.Print($"[game-ui] query box {_modalId} CONFIRMED (code {codes.Confirm})");
		}
		else if (input.Back && codes.Cancel is { } cancel)
		{
			Engine_UiSource.Current.PostPlayerLever("QueryAnswer", $"{cancel}@{_modalId}");
			GD.Print($"[game-ui] query box {_modalId} cancelled (code {cancel})");
		}
		else if (input.Equip && codes.Alternate is { } alternate)
		{
			Engine_UiSource.Current.PostPlayerLever("QueryAnswer", $"{alternate}@{_modalId}");
			GD.Print($"[game-ui] query box {_modalId} answered the alternate (code {alternate})");
		}
	}
}

/// <summary>
/// The owned numeric adjuster (modal mode 5, or a DISPLAY adjuster from
/// `GUI.DisplayNumericAdjusterParams`). Opened by what the opener STATED: the router's
/// `sub_8236FCA8(…, value, min, max, step, flags)` or the display box's Open, each naming its
/// widget (display_box_hooks.cpp). Drawn from that widget's render statement
/// (<see cref="Engine_UiNumericRender"/>): value, bounds, step and flags as `sub_826B8BB0` reads
/// them. The steps go to the guest as its own codes, each with its release — its handler steps and
/// commits its own value, and re-renders it.
/// </summary>
public sealed class Engine_UiNumericSurface : Engine_UiModalSurface<Engine_UiNumericBox>
{
	/// <summary>
	/// The step auto-repeat clock, host-side: the guest's own repeat lives in its stick
	/// translator, which the masked D-pad never reaches.
	/// </summary>
	private const double StepInterval = 0.15;

	/// <summary>
	/// The guest's step codes — `sub_826B8EB0`: 28 → `sub_826B8CF0` increment, 29 → decrement.
	/// D-pad up is 28.
	/// </summary>
	private const uint StepIncrement = 28;

	private const uint StepDecrement = 29;

	/// <summary>
	/// A step's release: the step code + 4 (32 up, 33 down). The widget's repeat gate
	/// `sub_828818E8` (behind `sub_826B8EB0`, both adjusters) drops a second step inside its
	/// first-repeat delay unless a release came between — cases 32..35 reset its clocks
	/// (0x82881a88..0x82881a9c) — so every step goes out with its release and every one lands.
	/// </summary>
	private const uint ReleaseOffset = 4;

	/// <summary>Commit and cancel, as the adjuster's handler reads them.</summary>
	private const uint Commit = 46;

	private const uint Cancel = 47;

	public override string Presentation => Engine_UiNumericBox.Presentation;

	public override string Label => "numeric adjuster";

	private Engine_UiNumericBox.Joins? _joins;

	private uint _modalId;

	/// <summary>
	/// The adjuster up is a DISPLAY box, not the router's, as its Open stated it. Same codes,
	/// different lever: its own handler `sub_823CF8A8` takes them — 28/29 step, 46 commits and
	/// posts 37, 47 cancels and posts 38 — addressed to this box (`BoxAction:<code>@<box>`).
	/// </summary>
	private Engine_UiStatedBoxes.Box? _display;

	private double _repeat;

	protected override bool Joined => _joins is not null;

	protected override Engine_UiNumericBox Build(Engine_UiTheme? art) =>
		Engine_UiNumericBox.FromPatternBook(art);

	protected override void Join(Engine_UiScreen document) =>
		_joins = Engine_UiNumericBox.Resolve(document);

	protected override bool IsOpen(Engine_UiModel.PauseState? pause)
	{
		if (pause is { AdjusterUp: true } state)
		{
			_modalId = state.ModalId;
			_display = null;
			return true;
		}

		_display = Engine_UiStatedBoxes.Current.ShowingAdjuster;
		return _display is not null;
	}

	/// <summary>The widget the open named: the router's `gui28[16]`, the display box's `box+24`.</summary>
	private uint Widget =>
		_display is { } box
			? uint.TryParse(box.Field(5), System.Globalization.NumberStyles.HexNumber,
				System.Globalization.CultureInfo.InvariantCulture, out uint widget) ? widget : 0u
			: Engine_UiModalText.AdjusterWidget(_modalId);

	protected override string Unstated => _display is not null
		? $"the display adjuster 0x{_display.Address:X8} stated no widget"
		: $"router adjuster {_modalId}'s open was not stated (@Modal.Open)";

	protected override void Released() => _repeat = 0.0;

	/// <summary>
	/// THE WIDGET'S LATEST RENDER (`sub_826B8BB0`), re-stated by every step: the guest's step is its
	/// own — it divides by ten near a bound (0x826b8d50..0x826b8d6c) — so the value is never stepped
	/// here. Before the first render, the open's own numbers; null when the open was not stated.
	/// </summary>
	private Engine_UiGuiBoxes.AdjusterState? Stated =>
		_display is { } box
			? box.Adjuster is null ? null : Engine_UiNumericRender.Of(Widget) ?? box.Adjuster
			: Engine_UiModalText.Adjuster(_modalId) is { } opened
				? Engine_UiNumericRender.Of(Widget) ?? opened
				: null;

	protected override bool ApplyHost(Engine_UiPresentationHost host)
	{
		string? body = _display is { } box ? box.Body : Engine_UiModalText.For(5u, _modalId);
		if (View is null || _joins is null || body is null || Stated is not { } stated)
		{
			return false;
		}

		string value = stated.Display;
		bool up = stated.CanIncrease;
		bool down = stated.CanDecrease;
		if (Changed($"{_display?.Address:X8}|{_modalId}|{body}|{value}|{up}|{down}"))
		{
			View.ShowStated(body, value, up, down);
		}

		return true;
	}

	/// <summary>A click on an arrow steps; anywhere else on the box commits the value.</summary>
	protected override Engine_UiSurfaceInput Point(Engine_UiSurfaceInput input, Vector2 at) =>
		View?.ArrowAt(at) is { } step && step != 0
			? input with { Direction = step }
			: base.Point(input, at);

	protected override void Route(Engine_UiSurfaceInput input)
	{
		if (input.Direction == 0)
		{
			_repeat = 0.0;
		}
		else
		{
			_repeat -= input.Delta;
			if (_repeat <= 0.0)
			{
				_repeat = StepInterval;
				uint step = input.Direction < 0 ? StepIncrement : StepDecrement;
				Send(step);
				Send(step + ReleaseOffset);
			}
		}

		string which = _display is { } shown
			? $"display numeric adjuster 0x{shown.Address:X8}"
			: $"numeric adjuster {_modalId}";
		if (input.Accept)
		{
			Send(Commit);
			GD.Print($"[game-ui] {which} COMMITTED");
		}
		else if (input.Back)
		{
			Send(Cancel);
			GD.Print($"[game-ui] {which} cancelled");
		}
	}

	private void Send(uint code)
	{
		if (_display is { } box)
		{
			Engine_UiSource.Current.PostPlayerLever("BoxAction", $"{code}@{box.Address:X8}");
		}
		else
		{
			Engine_UiSource.Current.PostPlayerLever("QueryAnswer", $"{code}@{_modalId}");
		}
	}
}

/// <summary>
/// The owned received-item card (screen id 8), drawn from what the guest STATES at its own
/// entry points. The card's draw (`sub_82538910(handler)`, run for an item, the co-op partner's
/// item, money, a discount and "work harder" alike) is replaced by its statement
/// (gui_screen_stub_receive_items.cpp): the handler it is handed, as a host-only record — the title
/// and description TEXT KEYS, the icon's resource hash, the NameAndIcon line, the extra line and its
/// amount, the usable flag and the peer flag; the handler's leave states the card down. Nothing here
/// reads the presentation's elements. Done and Equip are the levers `Card:&lt;player&gt;:dismiss|equip`
/// (the guest's own dismiss behind its one-second gate); the side is turned here, the guest's card
/// has none.
/// </summary>
public sealed class Engine_UiReceiveItemsSurface : Engine_UiModalSurface<Engine_UiReceiveItems>
{
	/// <summary>The host-only record kind the draw posts (gui_screen_stub_receive_items.cpp kCardKind).</summary>
	public const int StateKind = 1006;

	/// <summary>The guest's screen id for the card, read beside the stated record as the sanity
	/// that the screen the record belongs to is still the current one.</summary>
	private const uint CardScreen = 8;

	/// <summary>The card's lever and its two verbs (guest_tick_pump.cpp `Card:&lt;p&gt;:…`).</summary>
	public const string Lever = "Card";

	public const string Dismiss = "dismiss";

	public const string Equip = "equip";

	/// <summary>The prompt tags the card's file authors (`receiveitemsscreen.bgf`: GUI_USE,
	/// GUI_INFO, GUI_FLIP, GUI_TAKE); the partner's card captions Done as GUI_DONE (+324).</summary>
	private const string UseTag = "GUI_USE";

	private const string InfoTag = "GUI_INFO";

	private const string FlipTag = "GUI_FLIP";

	private const string TakeTag = "GUI_TAKE";

	private const string DoneTag = "GUI_DONE";

	/// <summary>One stated card: the handler's fields at its draw; the side is the surface's own.</summary>
	public sealed record CardState(
		bool Up, bool Peer, bool Front, bool Usable,
		uint Record, uint Model, uint Icon, uint Title, uint Description, uint Extra, uint Amount,
		string Name);

	private static readonly CardState Down = new(false, false, true, false, 0, 0, 0, 0, 0, 0, 0, string.Empty);

	/// <summary>What the guest last stated; read by the drive each frame.</summary>
	public static CardState State { get; private set; } = Down;

	/// <summary>The card shows its back: turned by the player, front again with every card stated.</summary>
	public static bool Reversed { get; private set; }

	/// <summary>Turns the standing card over; nothing to turn when none stands.</summary>
	public static void Turn() => Reversed = State.Up && !Reversed;

	/// <summary>A record from the hooks: `up=1,peer=,front=,usable=,record=,model=,icon=,title=,desc=,extra=,amount=,name='…'` or `up=0`.</summary>
	public static void Stated(string fields)
	{
		var values = new System.Collections.Generic.Dictionary<string, object>(System.StringComparer.Ordinal);
		foreach (System.Collections.Generic.KeyValuePair<string, object> pair in Refii.Mods.RefiiHost.ParseFieldsPlain(fields))
		{
			values[pair.Key] = pair.Value;
		}

		uint U(string key) => values.TryGetValue(key, out object? v) && v is long n ? unchecked((uint)n) : 0u;
		bool B(string key) => U(key) != 0u;
		if (!B("up"))
		{
			Reversed = false;
			State = Down;
			GD.Print("[game-ui] received-item card stated DOWN");
			return;
		}

		Reversed = false;
		State = new CardState(
			true, B("peer"), true, B("usable"),
			U("record"), U("model"), U("icon"), U("title"), U("desc"), U("extra"), U("amount"),
			values.TryGetValue("name", out object? name) ? name as string ?? name?.ToString() ?? string.Empty : string.Empty);
		GD.Print($"[game-ui] received-item card stated UP — record 0x{State.Record:X8} title 0x{State.Title:X8} " +
			$"icon 0x{State.Icon:X8} {(State.Front ? "front" : "back")} {(State.Usable ? "usable" : "NOT usable")}" +
			$"{(State.Peer ? " (partner's)" : string.Empty)}");
	}

	public override string Presentation => Engine_UiReceiveItems.Presentation;

	public override string Label => "received-item card";

	protected override float Depth => PanelDepth;

	private Engine_UiReceiveItems.Joins? _joins;

	protected override bool Joined => _joins is not null;

	protected override Engine_UiReceiveItems Build(Engine_UiTheme? art) =>
		Engine_UiReceiveItems.FromPatternBook(art);

	protected override void Join(Engine_UiScreen document) =>
		_joins = Engine_UiReceiveItems.Resolve(document);

	/// <summary>Open while the guest's set-item has stated a card and its leave has not taken it
	/// down, and the card is the current screen: a latch over it (a box) does not close it.</summary>
	protected override bool IsOpen(Engine_UiModel.PauseState? pause) =>
		State.Up && pause is { ScreenId: CardScreen };

	/// <summary>The stated card on screen: the strings by their text keys, the icon by its
	/// resource hash, the prompts by the file's tags.</summary>
	protected override bool ApplyHost(Engine_UiPresentationHost host)
	{
		CardState s = State with { Front = !Reversed };
		if (!s.Up || View is null)
		{
			return false;
		}

		if (!Changed($"stated:{s.Peer}|{s.Front}|{s.Usable}|{s.Icon:X8}|{s.Title:X8}|{s.Description:X8}|" +
			$"{s.Extra:X8}|{s.Amount}|{s.Name}"))
		{
			return true;
		}

		Engine_UiTheme? theme = View.Theme;
		string? title = Text(theme, s.Title);
		string? back = Text(theme, s.Description);
		// The money and discount cards' line is their text (`+68`) with every `%1` replaced by
		// the amount (`+72`): `sub_825383D0` / `sub_825384B8` build `+76` exactly so —
		// `sub_82B3EB68(+76, L"%1", amount)` over the localised text (0x82538460, 0x8253854c),
		// "A bag containing %1 gold." → "A bag containing 30 gold.". Composed here because that
		// `+76` is a wide string the card record does not carry; every other card's line is the
		// string the guest composed, empty for most items.
		string name = s.Name.Length == 0 && s.Extra != 0
			? (Text(theme, s.Extra) ?? string.Empty).Replace(
				"%1", s.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
				System.StringComparison.Ordinal)
			: s.Name;

		Texture2D? icon = null;
		if (s.Icon != 0 && host.TextureNames is { } names && names.TryResolve(s.Icon, out string path))
		{
			icon = host.LoadTexture(path);
		}

		var labels = new string?[]
		{
			theme?.Resolve(UseTag),
			theme?.Resolve(s.Front ? InfoTag : FlipTag),
			theme?.Resolve(s.Peer ? DoneTag : TakeTag),
		};

		View.BackClip = _joins?.BackClip;
		View.ShowStated(title, name, back, s.Icon, icon, up: true, reverse: !s.Front, usable: s.Usable, labels);
		GD.Print($"[game-ui] received-item card up — '{title}' " +
			$"(icon {(icon is null ? $"0x{s.Icon:X8} unresolved" : "resolved")}, " +
			$"{(s.Front ? "front" : "back")}, {(s.Usable ? "usable" : "NOT usable")})");
		return true;
	}

	/// <summary>A text by the key hash the guest holds (`Text_HashKeyNameFnv1`), or null.</summary>
	private static string? Text(Engine_UiTheme? theme, uint key) =>
		key != 0 && theme?.Strings is { } strings && strings.TryGetString(key, out string value) ? value : null;

	/// <summary>A click on a prompt is its button; the card itself answers nothing.</summary>
	protected override Engine_UiSurfaceInput Point(Engine_UiSurfaceInput input, Vector2 at) =>
		View?.PromptAt(at) switch
		{
			"Done" => input with { Accept = true },
			"Info" => input with { Flip = true },
			"Action" => input with { Equip = true },
			_ => input,
		};

	protected override void Route(Engine_UiSurfaceInput input)
	{
		// The wheel scrolls the description, a notch a step; the card takes every notch while it
		// stands, so none falls through to the guest as a D-pad press.
		int notches = Engine_UiPointer.TakeWheel();
		View?.ScrollBack(input.Scroll, input.Delta, notches);

		if (input.Accept)
		{
			Engine_UiSource.Current.PostPlayerLever(Lever, Dismiss);
			GD.Print("[game-ui] received-item card DONE");
		}
		else if (input.Flip)
		{
			Turn();
			GD.Print($"[game-ui] received-item card turned to its {(Reversed ? "back" : "front")}");
		}
		else if (input.Equip && State.Usable)
		{
			// `sub_82537128` code 75: honoured only while `+80` (usable now) is set — it activates the
			// item through `sub_8246FFF0` and then takes the same close path as Done.
			Engine_UiSource.Current.PostPlayerLever(Lever, Equip);
			GD.Print("[game-ui] received-item card EQUIP");
		}
	}
}

/// <summary>
/// A display-only owned surface: the intermission strip and the acceptance / button display box.
/// Both are box-queue requests the guest opens, writes and closes itself; each class's own Open
/// and Close state the box (`@DisplayBox.Open` / `.Close`, display_box_hooks.cpp), and the panel
/// stands on that statement. They take no input and have no modal state.
///
/// One class for both: they differ only by table — which authored groups to clone and which paths
/// carry the stated text.
/// </summary>
public sealed class Engine_UiStatedSurface : Engine_UiSurface<Engine_UiStatedPanel>
{
	/// <summary>
	/// The intermission strip: one group, one line of text, nothing to veto. `intermissionbox.bgf`
	/// carries the `MessageBox` behaviour, the `MessageBoxContract` and `MessageBox.lua`; its root
	/// authors the `Intro` and `Outro` slides. `Open` (`messagebox.dis main.2`) writes only
	/// `textstring`, so the strip takes no theme colour.
	/// </summary>
	public static Engine_UiStatedSurface Intermission() => new(
		"intermission strip",
		"Intermission",
		new Engine_UiStatedPanel.Config
		{
			Presentation = "intermissionbox",
			Groups = new[] { "Group" },
			HudStyle = true,
			Texts = new[] { new Engine_UiStatedPanel.TextBinding("Group", "Text") },
		},
		acceptanceText: false);

	/// <summary>
	/// The acceptance / button display box: body plus its title. The hold-to-accept `Paper` card
	/// (`AcceptanceNewInt` / `AcceptanceNewOut`, kind 8) is a class of its own and not this panel.
	///
	/// The two colours are `PrepareDisplayBox`'s (`displaybox.dis main.6 pc3..56`): subheading on
	/// the title, normal text on the body.
	/// </summary>
	public static Engine_UiStatedSurface DisplayBox() => new(
		"display box",
		"DisplayBox",
		new Engine_UiStatedPanel.Config
		{
			Presentation = "displaybox",
			Groups = new[] { "DefaultGroup" },
			HudStyle = true,
			Texts = new[]
			{
				new Engine_UiStatedPanel.TextBinding(
					"DefaultGroup", "FramePositioningGroup/Text",
					Engine_UiBoxConstants.NormalText),
				new Engine_UiStatedPanel.TextBinding(
					"DefaultGroup", "FramePositioningGroup/Title",
					Engine_UiBoxConstants.SubheadingText),
			},
		},
		acceptanceText: true);

	private Engine_UiStatedSurface(
		string label, string nodeName, Engine_UiStatedPanel.Config config, bool acceptanceText)
	{
		Label = label;
		_nodeName = nodeName;
		_config = config;
		_acceptanceText = acceptanceText;
	}

	public override string Presentation => _config.Presentation;

	public override string Label { get; }

	/// <summary>The text the last acceptance or button box call stated (`DisplayAcceptanceBox` /
	/// `DisplayButtonBox`), drawn as the display box's body while that box is up.</summary>
	public static string? StatedDisplayText { get; set; }

	protected override float Depth => PanelDepth;

	private readonly string _nodeName;

	private readonly Engine_UiStatedPanel.Config _config;

	/// <summary>Whether the acceptance / button natives' text stands in for an empty stated body.</summary>
	private readonly bool _acceptanceText;

	private bool _wasUp;

	private Engine_UiStatedPanel.Joins? _joins;


	protected override bool Joined => _joins is not null;

	protected override Engine_UiStatedPanel Build(Engine_UiTheme? art) =>
		Engine_UiStatedPanel.FromPatternBook(art, _config, _nodeName);

	protected override void Join(Engine_UiScreen document)
	{
		_joins = Engine_UiStatedPanel.Resolve(document, _config);
	}

	/// <summary>The box the guest last opened on this surface, 0 when none is up.</summary>
	private uint _statedBox;

	private string?[]? _statedTexts;

	private int _statedKind;

	/// <summary>
	/// The guest opened one of this surface's boxes (`@DisplayBox.Open`, display_box_hooks.cpp):
	/// its class's Open wrote <paramref name="body"/> (`box+8`) into the panel's text element and
	/// played its intro (intermission slide 1 `sub_8299EB40`; acceptance / button
	/// `g_DisplayBox:OpenAsAcceptanceBox()` from `sub_8299EE40`, whose title is that function's
	/// `GUI_ACCEPTANCE_BOX` — <paramref name="title"/>).
	/// </summary>
	public void Opened(uint box, int kind, string body, string? title = null)
	{
		_statedBox = box;
		_statedKind = kind;
		_statedTexts = new string?[_config.Texts.Length];
		if (_statedTexts.Length > 0)
		{
			_statedTexts[0] = body.Length != 0 ? body : _acceptanceText ? StatedDisplayText : body;
		}

		if (_statedTexts.Length > 1)
		{
			_statedTexts[1] = title;
		}
	}

	/// <summary>The guest closed box <paramref name="box"/> (its class's Close, `@DisplayBox.Close`).</summary>
	public void Closed(uint box)
	{
		if (box == _statedBox)
		{
			_statedBox = 0;
			_statedTexts = null;
			StatedDisplayText = null;
		}
	}

	/// <summary>
	/// The panel stands while the guest's box is open, with the texts its Open stated. True while
	/// it is up.
	/// </summary>
	/// <param name="hiddenByGuest">The guest holds this box's presentation hidden — the pause
	/// screen hides the intermission and display boxes' presentations without closing the boxes
	/// (registry `hidden … FROZEN` under the pause), and the panel follows that statement.</param>
	public bool Stand(Engine_UiPresentationHost? host, bool hiddenByGuest = false)
	{
		bool up = !hiddenByGuest && _statedBox != 0 && _statedTexts is { Length: > 0 } texts &&
			!string.IsNullOrEmpty(texts[0]);
		if (host is null || (!up && View is null))
		{
			_wasUp = false;
			return false;
		}

		Ensure(host);
		if (!View!.HasShippedArt || _joins is null)
		{
			return false;
		}

		if (up && !_wasUp)
		{
			View.ShowStated(_statedTexts!);
			GD.Print($"[ui-surface] the {Label} is up (kind {_statedKind}, box 0x{_statedBox:X8})");
		}

		_wasUp = up;
		View.Visible = up;
		return up;
	}

	/// <summary>Takes the panel down when its presentation has left the live set.</summary>
	public void Withdraw()
	{
		Close();
		_wasUp = false;
	}
}
