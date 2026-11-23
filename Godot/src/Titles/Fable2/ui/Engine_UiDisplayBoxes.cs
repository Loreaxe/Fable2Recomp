namespace Refii.Titles.Fable2;

using System.Collections.Generic;
using Refii.Ui;
using Box = Refii.Ui.Engine_UiStatedBoxes.Box;
using MenuBox = Refii.Ui.Engine_UiStatedBoxes.MenuBox;

/// <summary>
/// THE GUEST'S DISPLAY BOXES, as each class's own Open and Close state them — not read out of its
/// queues. Every box on the owner's two queues is shown through its class's Open (vf1) and taken
/// down through its Close (vf2); those are hooked (Fable2/src/vfs/src/hooks/display_box_hooks.cpp)
/// and post <c>@DisplayBox.Open</c> / <c>.Close</c> (the MessageBox <c>@MessageBox.Open</c> /
/// <c>.Close</c>; a box destroyed while open states its Close from its destructor) with the box's address, its identity, its body and its kind, then the class's
/// own fields. The owned surfaces and the box pages stand on these statements and answer the
/// stated address.
/// </summary>
/// <remarks>
/// <para>The kind is the class's own type — vtable slot 7, <c>li r3,N</c>, which <c>sub_823D0B90</c>
/// maps to a screen: 0 message, 2 query, 3 numeric adjuster, 4 quest box, 5 intermission, 6
/// acceptance, 7 button, 8 the hold-to-accept card.</para>
/// <para>Two queues, each showing one box: the owner's +4 queue takes the message, query, adjuster
/// and quest boxes (`sub_826B99D8(owner + 4, box)`: `GUI.DisplayYesNoQuestion` 0x82304520,
/// `sub_823A22B0`, `CQuestInfoBox_ctor`), the +0x20 queue the intermission, acceptance, button and
/// hold-to-accept boxes (`sub_823A2228` 0x823a2294). A queue's tick opens its head only when its
/// current box is down, so the newest box still open in a queue is the one it shows. The box the
/// router hands input to is the +4 queue's current, else the +0x20 queue's (`owner+24`, else
/// `owner+52` — `ShowingDisplayBox`, gui_display_box.h).</para>
/// <para>The menu box is a different object — one controller at <c>*(guiOwner+56)</c> — stated by
/// its populate and close (<c>@MenuBox.Open</c> / <c>.Close</c>): see <see cref="Menu"/>.</para>
/// <para>A box's record (<see cref="Engine_UiStatedBoxes.Box"/>): its address, kind, the identity at
/// `box+4` (the name `RemoveDisplayBox` matches), the resolved body at `box+8` and the class's own
/// fields. The shared surfaces read these through <see cref="Statements"/>.</para>
/// <para>Pure .NET: held by tests without a Godot type in the host.</para>
/// </remarks>
public static class Engine_UiDisplayBoxes
{
	public const int Message = 0;
	public const int Query = 2;
	public const int Adjuster = 3;
	public const int Quest = 4;
	public const int Intermission = 5;
	public const int Acceptance = 6;
	public const int Button = 7;
	public const int AcceptHold = 8;

	/// <summary>
	/// The TU1 vtable of each kind's class: Message 0x820AE668, Query 0x820AE688, NumericAdjuster
	/// 0x820AE6A8, QuestInfoBox 0x820AE6C8, AcceptHold 0x820EBF1C; 0 for the others.
	/// </summary>
	public static uint VtableOf(int kind) => kind switch
	{
		Message => 0x820AE668u,
		Query => 0x820AE688u,
		Adjuster => 0x820AE6A8u,
		Quest => 0x820AE6C8u,
		AcceptHold => 0x820EBF1Cu,
		_ => 0u,
	};

	private static readonly List<Box> _open = new();

	/// <summary>Moves whenever a box opens or closes.</summary>
	public static ulong Generation { get; private set; }

	/// <summary>Every box stated open, oldest first.</summary>
	public static IReadOnlyList<Box> Open => _open;

	/// <summary>Which queue a kind's boxes go on: 0 the +4 queue, 1 the +0x20 queue.</summary>
	public static int QueueOf(int kind) => kind is Intermission or Acceptance or Button or AcceptHold ? 1 : 0;

	/// <summary>The box one queue shows: the newest of its boxes still open, null when none is.</summary>
	public static Box? ShowingIn(int queue)
	{
		for (int i = _open.Count - 1; i >= 0; --i)
		{
			if (QueueOf(_open[i].Kind) == queue)
			{
				return _open[i];
			}
		}

		return null;
	}

	/// <summary>The box the router hands input to: the +4 queue's, else the +0x20 queue's.</summary>
	public static Box? Showing => ShowingIn(0) ?? ShowingIn(1);

	/// <summary>The box its kind's queue shows, when it is of <paramref name="kind"/>; else null.</summary>
	public static Box? ShowingOf(int kind) => ShowingIn(QueueOf(kind)) is { } box && box.Kind == kind ? box : null;

	/// <summary>A class's Open stated a box.</summary>
	public static Box Opened(uint address, int kind, string name, string body, IReadOnlyList<string> fields)
	{
		_open.RemoveAll(b => b.Address == address);
		var box = new Box(address, kind, name, body, fields)
		{
			Adjuster = kind == Adjuster ? AdjusterFrom(fields) : null,
		};
		_open.Add(box);
		++Generation;
		return box;
	}

	/// <summary>The adjuster widget as the box holds it: value, min, max, step, flags (1 money,
	/// 2 percent, 4 sign) — the ctor's `+68`, `+56`, `+60`, `+64`, `+52`.</summary>
	private static Engine_UiGuiBoxes.AdjusterState AdjusterFrom(IReadOnlyList<string> fields)
	{
		int At(int index) =>
			index < fields.Count && int.TryParse(fields[index], System.Globalization.NumberStyles.Integer,
				System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : 0;
		int flags = At(4);
		return new Engine_UiGuiBoxes.AdjusterState(At(1), At(2), At(3), flags & 3, (flags & 4) != 0)
		{
			Value = At(0),
		};
	}

	/// <summary>A class's Close stated the box down, or its destructor a box still open: it goes.</summary>
	public static bool Closed(uint address)
	{
		if (_open.RemoveAll(b => b.Address == address) == 0)
		{
			return false;
		}

		++Generation;
		return true;
	}

	/// <summary>The menu box stated open, null once its close is stated.</summary>
	public static MenuBox? Menu { get; private set; }

	/// <summary>The populate stated the box — again on a row's enable, so a repeat replaces it and
	/// keeps the card's stated name.</summary>
	public static void MenuOpened(uint address, Engine_UiGuiBoxes.MenuBoxModel model, bool card)
	{
		Menu = new MenuBox(address, model, card)
		{
			GamerName = card && Menu is { } open && open.Address == address ? open.GamerName : null,
		};
		++Generation;
	}

	/// <summary>
	/// The Orb's player card stated its name (`sub_823DB850` r5, after the box's populate): the text
	/// its fill writes into the card's gamertag element.
	/// </summary>
	public static void GamerCardNamed(uint address, string name)
	{
		if (Menu is { } menu && menu.Address == address)
		{
			Menu = menu with { GamerName = name };
			++Generation;
		}
	}

	/// <summary>The box's cursor write (`@MenuBox.Select`).</summary>
	public static void MenuSelected(uint address, int index)
	{
		if (Menu is { } menu && menu.Address == address && menu.Selected != index)
		{
			Menu = menu with { Selected = index };
			++Generation;
		}
	}

	/// <summary>
	/// The stated box in the shape the owned list reads: its rows' count and enabled bytes, the
	/// cursor, cancelable and the player card. Null when none is open.
	/// </summary>
	public static Engine_UiModel.MenuBoxState? MenuState()
	{
		if (Menu is not { } menu || menu.Model.Options.Count == 0)
		{
			return null;
		}

		uint enabled = 0u;
		for (int i = 0; i < menu.Model.Options.Count; ++i)
		{
			if (menu.Model.Options[i].Enabled)
			{
				enabled |= 1u << i;
			}
		}

		return new Engine_UiModel.MenuBoxState(
			menu.Model.Options.Count, menu.Selected, menu.Model.Cancelable, false, enabled, menu.Card);
	}

	/// <summary>The close stated the box down.</summary>
	public static void MenuClosed(uint address)
	{
		if (Menu is { } menu && (address == 0u || menu.Address == address))
		{
			Menu = null;
			++Generation;
		}
	}

	/// <summary>
	/// Parses one stated menu row — its tag prefixed <c>+</c> enabled or <c>-</c> disabled — into
	/// the tag and the flag; null for an empty slot.
	/// </summary>
	public static (string Tag, bool Enabled)? Row(string row) =>
		row.Length == 0 ? null : row[0] switch
		{
			'+' => (row[1..], true),
			'-' => (row[1..], false),
			_ => (row, true),
		};

	/// <summary>Forgets everything — the guest left the game.</summary>
	public static void Clear()
	{
		_open.Clear();
		Menu = null;
		++Generation;
	}

	/// <summary>These statements as the shared box surfaces read them (<see cref="Fable2Title.StatedBoxes"/>).</summary>
	public static readonly Engine_UiStatedBoxes Statements = new Stated();

	private sealed class Stated : Engine_UiStatedBoxes
	{
		public override Box? Showing => Engine_UiDisplayBoxes.Showing;

		public override Box? ShowingQuery => ShowingOf(Query);

		public override Box? ShowingAdjuster => ShowingOf(Adjuster);

		public override MenuBox? Menu => Engine_UiDisplayBoxes.Menu;
	}
}
