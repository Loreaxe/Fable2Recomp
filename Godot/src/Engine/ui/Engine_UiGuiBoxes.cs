using System.Collections.Generic;
using Godot;

using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// The display boxes and counters, owned — driven by the `GUI.*` calls rather than read back out
/// of the guest's queues.
/// </summary>
/// <remarks>
/// Guest functions: `sub_823027B0` (`DisplayMessageBox`), `sub_82303340` (`DisplayInfoBox`),
/// `sub_823033D0` (`DisplayInfoBoxParams`), `sub_82303740` (`DisplayInfoBoxEx`), `sub_82304C48`
/// (`RemoveDisplayBox`), `sub_823052D8` (`SetCounter`), `sub_82305918` (`RemoveCounter`). See
/// docs/ui_native_cut.md.
///
/// Removal matches on two keys: `RemoveDisplayBox(name)` sweeps the display-box queues by the
/// 32-bit FNV-1 hash at `desc+0`, and separately compares the raw name string against the
/// message-box queues' `obj+4` (cached length plus case-sensitive `strcmp`). Both are stored per
/// box and either matches.
///
/// FNV-1 here is case-sensitive: `DisplayMessageBox` names itself `"Unnamed"`,
/// `DisplayInfoBoxParams` defaults to `"unnamed"` — different boxes, different hashes.
///
/// Lifetimes are normalised to seconds at the door: `DisplayInfoBoxEx` and
/// `DisplayInfoBoxParams`(style ≠ 0) multiply by 1000 into ms, `DisplayInfoBoxParams`(style 0)
/// passes seconds raw, `DisplayInfoBox` hard-codes 6000 ms.
///
/// `DisplayInfoBox` cannot be removed by name: its id is a literal `0` (`82303370 li r4, 0`) and
/// `Fnv1("")` is `0x811C9DC5`, so no argument can produce 0. It is fire-and-forget on its
/// 6-second timer.
/// </remarks>
public sealed class Engine_UiGuiBoxes
{
	/// <summary>FNV-1 of the empty string — what a missing name argument hashes to.</summary>
	public const uint EmptyNameHash = 0x811C9DC5u;

	/// <summary>The name `DisplayMessageBox` gives itself — `sub_83246FC0`, capital U.</summary>
	public const string MessageBoxName = "Unnamed";

	/// <summary>The name `DisplayInfoBoxParams` defaults to — lowercase, a different box.</summary>
	public const string ParamsDefaultName = "unnamed";

	/// <summary>`DisplayInfoBox`'s hard-coded lifetime, `flt_82000C28` = 6000 ms.</summary>
	public const float InfoBoxSeconds = 6f;

	/// <summary>Seconds at which a countdown starts flashing — 20000.0 ms in `sub_822CE028`.</summary>
	public const float FlashSeconds = 20f;

	/// <summary>
	/// How many counters and timers can be on screen at once — a hard ceiling. `sub_823CB2E8`
	/// builds exactly four entries, each bound at construction to its own
	/// `CounterN`/`CounterFlashN` identities, and `sub_823CC240` is a slot finder: same identity,
	/// else first inactive, else the request is silently dropped.
	/// </summary>
	public const int MaxCounters = 4;

	/// <summary>
	/// Every box fades for 0.75 s after it closes. `sub_823D0810`: closing sets the open byte to 0
	/// and the lifetime float to 0.75; an open box never expires.
	/// </summary>
	public const float CloseFadeSeconds = 0.75f;

	/// <summary>One box on screen.</summary>
	/// <param name="Name">The identity string, as the guest stored it at `obj+4`.</param>
	/// <param name="Id">`Fnv1(Name)`, as the guest stored it at `desc+0`.</param>
	/// <param name="Text">Resolved and substituted display text.</param>
	/// <param name="Remaining">Seconds left, or negative for persistent.</param>
	/// <param name="Removable">False only for `DisplayInfoBox`, whose id is literally 0.</param>
	public sealed record Box(string Name, uint Id, string Text, float Remaining, bool Removable)
	{
		public float Remaining { get; set; } = Remaining;

		/// <summary>A negative lifetime is the guest's own "stay until removed".</summary>
		public bool Persistent => Remaining < 0f;

		/// <summary>The heading, resolved. Empty for every box but the params form.</summary>
		public string Title { get; set; } = string.Empty;

		/// <summary>
		/// `DisplayBoxStyle` — a discriminator: style 0 takes a different guest path
		/// (`sub_82302270` plus `sub_823A2228`) from styles 1 and 2, which go through
		/// `sub_8230C908(*(gui+32)+88, kind, box, 1)` with kind 1, 2 or 3.
		/// </summary>
		public int Style { get; set; }

		/// <summary>The prompt row, or null for a box that states none.</summary>
		public BoxButtons? Buttons { get; set; }

		/// <summary>Which kind this box is — see <see cref="Engine_UiGuiBoxes.BoxKind"/>.</summary>
		public BoxKind Kind { get; set; }

		/// <summary>A quest box's risk level; 1 unless the table said otherwise.</summary>
		public int RiskLevel { get; set; } = 1;

		/// <summary>
		/// The confirm button's caption — `DisplayDownloadMessageBox`'s second argument, stored
		/// verbatim: the guest writes it into the button element with no Babel lookup.
		/// </summary>
		public string AcceptCaption { get; set; } = string.Empty;

		/// <summary>
		/// A marketplace offer id, for the download box only: the guest's button calls
		/// `XamShowMarketplaceUI` with it. Carried, never performed.
		/// </summary>
		public string MarketplaceOffer { get; set; } = string.Empty;
	}

	/// <summary>
	/// The prompt row a `DisplayInfoBoxParams` box states. `ShowAButton` and `ShowYButton` default
	/// to true (`823034d0 li r5,1`, `82303518 li r5,1`); every other boolean in the table defaults
	/// to false. `AcceptText` and `HelpText` are Babel tags, not display text (pushed through
	/// `Text_HashKeyNameFnv1`), defaulting to `GUI_DIALOG_ACCEPT` and `GUI_DIALOG_HELP`.
	/// </summary>
	public sealed record BoxButtons(
		bool ShowA, bool HoldA, bool HoldAReduceDialogue,
		bool ShowY, bool HoldY, string AcceptText, string HelpText);

	/// <summary>One selectable row of a menu box.</summary>
	public sealed record MenuOption(string Label, bool Enabled);

	/// <summary>
	/// The modal `DisplayMenuBox` puts up: a body and one to five options, one box at a time. The
	/// arity gate is 2..6 arguments (`82303c14 cmpwi cr6, r29, 2` / `82303c1c cmpwi cr6, r29, 6`);
	/// outside that range the native does nothing. `Cancelable` is the only difference between the
	/// two natives — `DisplayMenuBox` (`sub_82303EE0`) sets `r4 = 1`,
	/// `DisplayUncancelableMenuBox` (`sub_823044A0`) sets `r4 = 0` — and it lands at `box+0x0E`.
	/// </summary>
	public sealed record MenuBoxModel(
		string Text, IReadOnlyList<MenuOption> Options, bool Cancelable)
	{
		/// <summary>Which row the cursor is on. Host-owned, like every other menu cursor.</summary>
		public int Highlight { get; set; }
	}

	/// <summary>
	/// One counter. `GUI.RemoveTimer` is literally the same function as `RemoveCounter`
	/// (`sub_82305918` is registered under both names), so timers and counters share one pool
	/// and one namespace here too.
	/// </summary>
	public sealed record Counter(string Name, uint Id, string Tag)
	{
		/// <summary>Substituted for `%1` — truncated to int by the guest's own `fctiwz`.</summary>
		public int First { get; set; }

		/// <summary>Substituted for `%2`.</summary>
		public int Second { get; set; }

		/// <summary>The resolved, substituted line.</summary>
		public string Text { get; set; } = string.Empty;

		/// <summary>Seconds on a timer, or null when this entry is a plain counter.</summary>
		public float? Remaining { get; set; }

		/// <summary>A stopwatch rather than a countdown — `SetCountUpTimerWithText`.</summary>
		public bool CountsUp { get; set; }

		/// <summary>Held by `GUI.PauseTimer` until `GUI.PlayTimer`.</summary>
		public bool Paused { get; set; }

		/// <summary>
		/// The low-time flash — the guest shows `CounterFlashN` beside the counter. `sub_822CE028`
		/// latches it below 20000.0 ms remaining; the branch is guarded off for a count-up timer.
		/// </summary>
		public bool Flashing { get; set; }

		/// <summary>
		/// False once a countdown has run out; not the same as having been removed. An expired
		/// timer keeps its slot and identity, so `IsCounterShown` still answers true — it tests
		/// only the identity, never the active byte.
		/// </summary>
		public bool Active { get; set; } = true;
	}

	private readonly List<Box> _boxes = new();

	private readonly List<Counter> _counters = new();

	/// <summary>Boxes on screen, oldest first.</summary>
	public IReadOnlyList<Box> Boxes => _boxes;

	/// <summary>Counters on screen, in the order they were first set.</summary>
	public IReadOnlyList<Counter> Counters => _counters;

	/// <summary>
	/// The headline — one line at the top of the screen, and only ever one. `DisplayHeadlineText`
	/// writes a fixed slot at `*(GUI+64)`: resolved text at `+24`, duration at `+28`, mode byte at
	/// `+32`. `ClearHeadlineText` zeroes the duration and the byte and leaves the text, so what
	/// says a headline is up is the duration and the flag. An unresolved tag produces no headline:
	/// the guest writes the text, then clears the duration and flag again.
	/// </summary>
	public sealed record HeadlineText(string Text, float Seconds, bool Mode)
	{
		public float Seconds { get; set; } = Seconds;
	}

	/// <summary>The headline up right now, or null.</summary>
	public HeadlineText? Headline { get; private set; }

	/// <summary>
	/// `GUI.DisplayHeadlineText(tag, seconds, mode)` — the caller resolves the tag.
	/// </summary>
	/// <param name="text">
	/// The resolved text, or empty when the tag did not resolve, which puts no headline up.
	/// </param>
	public void ShowHeadline(string text, float seconds, bool mode)
	{
		Headline = text.Length == 0 ? null : new HeadlineText(text, seconds, mode);
		++Generation;
	}

	/// <summary>`GUI.ClearHeadlineText()`.</summary>
	public void ClearHeadline()
	{
		if (Headline is null)
		{
			return;
		}

		Headline = null;
		++Generation;
	}

	/// <summary>
	/// `GUI.SetTopBoxHidden(hidden)` — drives the `TopBox` and `TargetedQuery` groups. The
	/// argument is Lua-truthy (`lua_toboolean`), so the number 0 hides the box.
	/// </summary>
	public bool TopBoxHidden { get; private set; }

	/// <summary>`GUI.SetTopBoxHidden(hidden)`.</summary>
	public void SetTopBoxHidden(bool hidden)
	{
		if (TopBoxHidden == hidden)
		{
			return;
		}

		TopBoxHidden = hidden;
		++Generation;
	}

	/// <summary>
	/// The job multiplier — the `x N` on the HUD (`GUI+80+20`, the level at `+16`). The argument is
	/// the value, not an index: `SetJobMultiplierLevel` (TU1 0x82306B18 → `sub_823CAC20`, disc
	/// 0x82908740 → `sub_82819740`) does nothing for the held value (0x823CAC40 / 0x82819760), else
	/// writes `<gold_img>` + `%d` onto `JobMultiplier.Group.Text` (0x823CAC84 / 0x828197A4) and plays
	/// the root's slide by how the value moved: 1 from zero (0x823CACC0 / 0x828197E0), 2 up with
	/// `SE_GUI_MULTIPLIER_UP` (0x823CACFC / 0x8281981C), 3 down (0x823CAD38 / 0x82819858).
	/// `TurnJobMulitplierOff` (0x82306B78 / 0x829087A0) plays 4 or 5 on its boolean and clears the
	/// level, nothing at level 0 (0x82306BB0 / 0x829087D8).
	/// </summary>
	public int JobMultiplier { get; private set; }

	/// <summary>How the multiplier last moved: 0 none, 1 appeared, 2 up, 3 down, 4/5 off.</summary>
	public int JobMultiplierSlide { get; private set; }

	/// <summary>The text the last level change wrote onto `JobMultiplier.Group.Text`, empty before one.</summary>
	public string JobMultiplierText { get; private set; } = string.Empty;

	/// <summary>Bumped by every move, so a repeated slide (two increases) is a fresh entry.</summary>
	public ulong JobMultiplierMoves { get; private set; }

	/// <summary>`GUI.SetJobMultiplierLevel(value)`.</summary>
	public void SetJobMultiplier(int value)
	{
		if (value == JobMultiplier)
		{
			return;
		}

		// Unsigned, as the guest compares it (`cmplw`/`ble`): a negative level reads as enormous
		// and counts as an increase.
		JobMultiplierSlide = JobMultiplier == 0 ? 1 : (uint)value > (uint)JobMultiplier ? 2 : 3;
		JobMultiplier = value;
		JobMultiplierText = "<gold_img>" + value.ToString(System.Globalization.CultureInfo.InvariantCulture);
		++JobMultiplierMoves;
		++Generation;
	}

	/// <summary>A new in-game GUI: no level, nothing written yet.</summary>
	public void ResetJobMultiplier()
	{
		JobMultiplier = 0;
		JobMultiplierSlide = 0;
		JobMultiplierText = string.Empty;
		++Generation;
	}

	/// <summary>`GUI.TurnJobMulitplierOff(flag)` — the guest's own spelling.</summary>
	public void TurnJobMultiplierOff(bool flag)
	{
		if (JobMultiplier == 0)
		{
			return;
		}

		JobMultiplier = 0;
		JobMultiplierSlide = flag ? 4 : 5;
		++JobMultiplierMoves;
		++Generation;
	}

	/// <summary>The menu box up right now, or null.</summary>
	public MenuBoxModel? Menu { get; private set; }

	/// <summary>True when anything is up — the cheap gate a drawer can test per frame.</summary>
	public bool Any => _boxes.Count != 0 || _counters.Count != 0 || Menu is not null ||
		_guestMessages.Count != 0 ||
		Headline is not null || JobMultiplier != 0;

	/// <summary>Rebuilt whenever the model changes, so a view can skip untouched frames.</summary>
	public ulong Generation { get; private set; }

	/// <summary>`GUI.DisplayMessageBox(tag, …)` — one box, named `Unnamed`. It stays until the
	/// guest's own box closes — action 36 or `RemoveDisplayBox("Unnamed")` — which the class
	/// states through <see cref="GuestMessageClosed"/>. `obj+24 = -1` is the pad the box accepts
	/// (any), not a lifetime: `sub_823CEDD8` returns at 0x823cee00 unless it is -1 or the pad.</summary>
	public void MessageBox(string text) => Add(MessageBoxName, text, -1f, removable: true);

	/// <summary>A persistent message box under its own name, rather than `Unnamed`.</summary>
	public Box MessageBoxNamed(string name, string text) =>
		Add(name, text, -1f, removable: true);

	/// <summary>`GUI.DisplayInfoBox(tag, …)` — six seconds, not removable. See the class note.</summary>
	public void InfoBox(string text) =>
		Add(string.Empty, text, InfoBoxSeconds, removable: false);

	/// <summary>`GUI.DisplayInfoBoxEx(name, seconds, tag, …)` — the removable info box.</summary>
	public void InfoBoxEx(string name, float seconds, string text) =>
		Add(name, text, seconds, removable: true);

	/// <summary>
	/// `GUI.DisplayInfoBoxParams(table, tag, …)` — the configured info box. `LifeTime` is seconds
	/// here (the guest multiplies by 1000 at `82303618`), defaulting to `-1.0`, persistent.
	/// Removable, unlike `DisplayInfoBox`: `Name` defaults to `"unnamed"`.
	/// </summary>
	public Box InfoBoxParams(string name, string title, float seconds, string text, int style,
		BoxButtons buttons)
	{
		Box box = Add(name, text, seconds, removable: true);
		box.Title = title;
		box.Style = style;
		box.Buttons = buttons;
		return box;
	}

	/// <summary>`GUI.DisplayMenuBox` / `DisplayUncancelableMenuBox` — puts up the modal.</summary>
	public void ShowMenuBox(string text, IReadOnlyList<MenuOption> options, bool cancelable)
	{
		Menu = new MenuBoxModel(text, options, cancelable);
		++Generation;
	}

	/// <summary>
	/// Answers the menu box: 0 cancels, 1..N pick that option. Records the choice and takes the
	/// box down only — the caller must also tell the guest, since scripts read the answer off the
	/// message-event bus and only the guest's own commit posts there.
	/// </summary>
	public int? AnswerMenuBox(int answer)
	{
		if (Menu is null)
		{
			return null;
		}

		// A cancel is only available on a cancelable box.
		if (answer <= 0 && !Menu.Cancelable)
		{
			return null;
		}

		int clamped = answer <= 0 ? 0 : System.Math.Min(answer, Menu.Options.Count);
		CloseMenuBox();
		return clamped;
	}

	/// <summary>Takes the menu box down. A no-op when none is up.</summary>
	public bool CloseMenuBox()
	{
		if (Menu is null)
		{
			return false;
		}

		Menu = null;
		++Generation;
		return true;
	}

	/// <summary>What kind of box this is, where that changes how it is drawn or answered.</summary>
	public enum BoxKind
	{
		/// <summary>A plain message or info box. Dismissed with 36.</summary>
		Message,

		/// <summary>A yes/no query. Answered with 46 or 47, posting message type 36.</summary>
		Question,

		/// <summary>The numeric adjuster. 28/29 step it, 46/47 finish it (type 37/38).</summary>
		Adjuster,

		/// <summary>A quest description. Answers only 36 and posts nothing.</summary>
		Quest,
	}

	/// <summary>
	/// The adjuster's live value and its bounds. The guest substitutes `MinVal` 111, `MaxVal` 666,
	/// `Increment` 33 and `DefaultVal` 55 for a missing key. Arrows are gated by the increment, not
	/// the bounds: up while `Max - value >= Increment`, down while `value - Min >= Increment`.
	/// </summary>
	public sealed record AdjusterState(int Min, int Max, int Increment, int Type, bool ShowSign)
	{
		public int Value { get; set; }

		public bool CanIncrease => Max - Value >= Increment;

		public bool CanDecrease => Value - Min >= Increment;

		/// <summary>
		/// The value as the guest renders it: an optional sign, then money or percent. The sign is
		/// only ever `+`, and only when `ShowSign` is set and the value is positive.
		/// </summary>
		public string Display
		{
			get
			{
				string body = Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
				if (ShowSign && Value > 0)
				{
					body = "+" + body;
				}

				// `sub_826B8BB0`: the money form builds "$" + text into a temporary it discards
				// (0x826b8c30..48), so money shows the bare number; only percent appends.
				return (Type & 1) == 0 && (Type & 2) != 0 ? body + "%" : body;
			}
		}
	}

	/// <summary>The adjuster's state when one is up, else null.</summary>
	public AdjusterState? Adjusting { get; private set; }

	/// <summary>`GUI.DisplayYesNoQuestion` and its two siblings — a persistent query box.</summary>
	public Box Question(string name, string text)
	{
		Box box = Add(name, text, -1f, removable: true);
		box.Kind = BoxKind.Question;
		return box;
	}

	/// <summary>`GUI.DisplayNumericAdjusterParams(table, tag, ...)`.</summary>
	public Box Adjuster(
		string text, int min, int max, int increment, int start, int type, bool showSign)
	{
		Box box = Add(MessageBoxName, text, -1f, removable: true);
		box.Kind = BoxKind.Adjuster;

		// `Type` picks the units: 1 money, 2 percent; `ShowSign` is an independent flag.
		Adjusting = new AdjusterState(min, max, increment, type, showSign)
		{
			Value = System.Math.Clamp(start, min, max),
		};

		return box;
	}

	/// <summary>`GUI.DisplayQuestBoxParams(table, tag, ...)` — a display, not a question.</summary>
	public Box QuestBox(string name, string text, int type, int riskLevel)
	{
		Box box = Add(name, text, -1f, removable: true);
		box.Kind = BoxKind.Quest;

		// `Type` is the slide index the quest presentation plays — passed straight to goto-slide.
		box.Style = type;
		box.RiskLevel = riskLevel;
		return box;
	}

	/// <summary>Steps the adjuster, as the guest's own arrows do.</summary>
	public bool StepAdjuster(bool up)
	{
		if (Adjusting is not { } state ||
			(up && !state.CanIncrease) || (!up && !state.CanDecrease))
		{
			return false;
		}

		state.Value += up ? state.Increment : -state.Increment;
		++Generation;
		return true;
	}

	/// <summary>
	/// `GUI.SetTimer(name, seconds)` and the two text forms — one entry in the counter pool. A
	/// repeat call updates in place, as `SetCounter` does.
	/// </summary>
	public Counter SetTimer(string name, string tag, float seconds, bool countsUp)
	{
		Counter timer = SetCounter(name, tag, 0, 0);
		timer.Remaining = seconds;
		timer.CountsUp = countsUp;
		timer.Paused = false;
		return timer;
	}

	/// <summary>`GUI.PauseTimer(name)` / `GUI.PlayTimer(name)`.</summary>
	public bool HoldTimer(string name, bool paused)
	{
		uint id = Engine_GameHash.Fnv1(name);
		bool changed = false;
		foreach (Counter counter in _counters)
		{
			if (counter.Remaining is null ||
				(counter.Id != id && !string.Equals(counter.Name, name, System.StringComparison.Ordinal)))
			{
				continue;
			}

			if (counter.Paused != paused)
			{
				counter.Paused = paused;
				changed = true;
			}
		}

		if (changed)
		{
			++Generation;
		}

		return changed;
	}

	/// <summary>
	/// `GUI.RemoveDisplayBox(name)` — sweeps by hash and by name, as the guest does (message-box
	/// list first, stopping on a hit at `82304c84 bne cr6`, then the second list).
	/// </summary>
	public int Remove(string name)
	{
		uint id = Engine_GameHash.Fnv1(name);
		int removed = _boxes.RemoveAll(box =>
			box.Removable &&
			(box.Id == id || string.Equals(box.Name, name, System.StringComparison.Ordinal)));

		if (removed != 0)
		{
			++Generation;
		}

		return removed;
	}

	/// <summary>
	/// Drops one box the guest stated gone — for a title whose box manager states the box itself
	/// (Fable 3's `@Box.Close`), not a name.
	/// </summary>
	public bool Remove(Box box)
	{
		if (!_boxes.Remove(box))
		{
			return false;
		}

		if (box.Kind == BoxKind.Adjuster)
		{
			Adjusting = null;
		}

		++Generation;
		return true;
	}

	/// <summary>
	/// `GUI.SetCounter(name, tag, v1, v2)` — sets or updates in place. `sub_823CC240` walks a
	/// fixed pool and, on a name match, inherits the existing counter's live UI element handles
	/// (`a2[5] = *(v10+20)`) before replacing the record, so a repeat call keeps the widget.
	/// </summary>
	public Counter SetCounter(string name, string tag, int first, int second)
	{
		uint id = Engine_GameHash.Fnv1(name);
		Counter? existing = _counters.Find(c => c.Id == id);
		if (existing is null)
		{
			existing = new Counter(name, id, tag);
			_counters.Add(existing);
		}

		existing.First = first;
		existing.Second = second;
		++Generation;
		return existing;
	}

	/// <summary>`GUI.RemoveCounter(name)` — and `GUI.RemoveTimer`, which is the same function.</summary>
	public bool RemoveCounter(string name)
	{
		uint id = Engine_GameHash.Fnv1(name);
		int removed = _counters.RemoveAll(c => c.Id == id);
		if (removed != 0)
		{
			++Generation;
		}

		return removed != 0;
	}

	/// <summary>
	/// Ages the timed boxes and drops the expired ones. The guest's own queue still ages its copy,
	/// but nothing reads it — this model owns the lifetime it draws.
	/// </summary>
	public void Tick(double delta)
	{
		bool changed = false;

		// Timers first: they outlive the boxes, and a timer with no box up is the normal case,
		// so an early return on an empty box list would freeze every clock.
		foreach (Counter counter in _counters)
		{
			// The guest's gate: ticks only while active, has-clock and not paused. An expired
			// timer stops moving but keeps its pool slot.
			if (counter.Remaining is not { } remaining || counter.Paused || !counter.Active)
			{
				continue;
			}

			// A count-up timer has no end: a stopwatch cleared only by `RemoveCounter`.
			float next = counter.CountsUp
				? remaining + (float)delta
				: remaining - (float)delta;

			if (!Mathf.IsEqualApprox(next, remaining))
			{
				counter.Remaining = next;
				changed = true;
			}

			if (counter.CountsUp)
			{
				continue;
			}

			if (!counter.Flashing && next < FlashSeconds)
			{
				counter.Flashing = true;
				changed = true;
			}

			// Expiry is `< 0`, not `<= 0`: the guest lets a countdown reach zero and tick past.
			if (next < 0f)
			{
				counter.Active = false;
				counter.Flashing = false;
				counter.Remaining = 0f;
				changed = true;
			}
		}

		if (_boxes.Count == 0)
		{
			if (changed)
			{
				++Generation;
			}

			return;
		}

		for (int i = _boxes.Count - 1; i >= 0; --i)
		{
			Box box = _boxes[i];
			if (box.Persistent)
			{
				continue;
			}

			box.Remaining -= (float)delta;
			if (box.Remaining <= 0f)
			{
				_boxes.RemoveAt(i);
				changed = true;
			}
		}

		if (changed)
		{
			++Generation;
		}
	}

	/// <summary>
	/// The oldest timed box under one of <paramref name="names"/> — Refii's own notices, which no
	/// guest manager holds and so only the host can put on screen. Null when none is up.
	/// </summary>
	public string? FrontOf(IReadOnlySet<string> names)
	{
		foreach (Box box in _boxes)
		{
			if (!box.Persistent && names.Contains(box.Name) && box.Text.Length != 0)
			{
				return box.Text;
			}
		}

		return null;
	}

	/// <summary>Drops everything — a level change, or the UI going away.</summary>
	public void Clear()
	{
		if (!Any)
		{
			return;
		}

		_boxes.Clear();
		_counters.Clear();
		++Generation;
	}

	/// <summary>
	/// The display MessageBoxes the guest has open, newest last — stated by the class's own Open
	/// and Close (`@MessageBox.Open` / `@MessageBox.Close`, hooks/display_box_hooks.cpp), not
	/// read out of its queue. Most never pass a `GUI.*` native: modal hints, the pause menu's
	/// notices and the sign-in box are built natively.
	/// </summary>
	private readonly List<(uint Box, string Identity)> _guestMessages = new();

	/// <summary>The newest open guest MessageBox's address, 0 when none is open.</summary>
	public uint GuestMessageBox => _guestMessages.Count == 0 ? 0u : _guestMessages[^1].Box;

	/// <summary>
	/// The player each guest box was built for, by its address — stated as `@Box.Seat` when the core
	/// co-op Lua builds a box for player two on split screen (display_box_hooks.cpp BoxSeatLeave).
	/// The box classes name no player of their own; an unstated box is nobody's in particular.
	/// </summary>
	private readonly Dictionary<uint, int> _seats = new();

	/// <summary>A box was built for <paramref name="player"/> (1 or 2).</summary>
	public void SeatBox(uint box, int player)
	{
		if (_seats.Count > 64)
		{
			_seats.Clear();
		}

		_seats[box] = player;
	}

	/// <summary>The player a guest box was built for, 0 when none was stated.</summary>
	public int SeatOf(uint box) => box != 0u && _seats.TryGetValue(box, out int player) ? player : 0;

	/// <summary>The guest opened a MessageBox — it now holds GameFlow state 8, both players'
	/// movement, until action 36 closes it.</summary>
	public void GuestMessageOpened(uint box, string identity, string body = "")
	{
		_guestMessages.RemoveAll(m => m.Box == box);
		_guestMessages.Add((box, identity));
		if (body.Length != 0)
		{
			StatedMessageBodies[box] = body;
		}

		++Generation;
	}

	/// <summary>A guest MessageBox's body as its Open stated it (display_box_hooks.cpp), by box.</summary>
	public static readonly Dictionary<uint, string> StatedMessageBodies = new();

	/// <summary>
	/// The guest closed a MessageBox, or destroyed one still open. The owned copy a
	/// `GUI.DisplayMessageBox` call stood up under the same identity goes with it.
	/// </summary>
	public void GuestMessageClosed(uint box, string identity)
	{
		StatedMessageBodies.Remove(box);
		_guestMessages.RemoveAll(m => m.Box == box);
		_seats.Remove(box);
		_boxes.RemoveAll(b => b.Kind == BoxKind.Message && b.Persistent && b.Name == identity);
		++Generation;
	}

	/// <summary>One line for the report.</summary>
	public string Describe()
	{
		var parts = new List<string>();
		foreach ((uint box, string identity) in _guestMessages)
		{
			parts.Add($"guest message box '{identity}'@{box:X8}");
		}

		foreach (Box box in _boxes)
		{
			parts.Add($"box '{box.Name}'#{box.Id:X8}" +
				(box.Persistent ? " (persistent)" : $" ({box.Remaining:0.0}s)") +
				$" \"{box.Text}\"");
		}

		foreach (Counter counter in _counters)
		{
			parts.Add($"counter '{counter.Name}'#{counter.Id:X8} \"{counter.Text}\"");
		}

		return parts.Count == 0 ? "nothing up" : string.Join(" | ", parts);
	}

	private Box Add(string name, string text, float seconds, bool removable)
	{
		var box = new Box(name, Engine_GameHash.Fnv1(name), text, seconds, removable);
		_boxes.Add(box);
		++Generation;
		return box;
	}
}
