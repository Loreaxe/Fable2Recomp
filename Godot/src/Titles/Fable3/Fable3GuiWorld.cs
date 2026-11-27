namespace Refii.Titles.Fable3;

using Godot;
using Refii.Game;
using Refii.Host;
using Refii.Ui;

/// <summary>
/// THE SANCTUARY'S INPUT (mode 3): Godot owns its pad, keys and pointer and drives the guest through
/// `guiworld:&lt;p&gt;:&lt;verb&gt;` (Fable3/src/hooks/src/interaction_guiworld.cpp), which runs the guest's
/// own HandleInput, bars, selection-changed slot and gesture poster. What it shows is what the mode keeps,
/// as the guest states it once its parser (`GUI.OpenInteractionMode` / `GUI.RefreshInteractionMode`) or the
/// partner's copy (`DisplayItemsForOtherMachine`) has filled it: `@Interaction.World.Parsed(player, cursor,
/// holds, rows)` (<see cref="Fable3GuiWorldModel.Stated"/>), until the mode is freed
/// (`@Interaction.World.Closed`). A Refresh that only sends the table to the
/// partner fills nothing here. The list's cursor is Godot's; every move is told to the guest (event 137
/// with the row).
/// </summary>
/// <remarks>
/// <para>WHO PRESSES. Every pad's buttons go as that pad's player: the guest's own owner gate in the base
/// input (sub_829704A0) and the scripts' `IsMessageSentBy(…, GUIHeroEntity)` decide, as for the 360's
/// pads. The sticks and the list cursor go from the Sanctuary's owner's pad alone (the stated player):
/// the guest keeps one set of stamps and one cursor. The mouse is player one's.</para>
/// <para>WHAT TAKES A PRESS FIRST. A TopBox message showing A or Y has player one's (channel 4 is ahead of
/// the interaction mode's channel 8; Fable3Boxes hands them to the TopBox, as Fable3HudInput leaves them); a showing box or the menu box
/// has A / B / X / Y (the native refuses the lever by the guest's own queues); a screen over the mode
/// refuses it in the channel's own gate (IM+7).</para>
/// <para>WHAT IS NOT SENT. A hold slot's press (the guest's hold step takes it from the pad;
/// <see cref="Fable3GuiWorldModel.Verb"/>); a label or an enable is the guest's HandleInput's to test. Pad
/// BACK has no consumer in the Sanctuary (game action 98, which neither HandleInput nor the bars take) and
/// sends nothing.</para>
/// </remarks>
public static class Fable3GuiWorld
{
	public static Fable3GuiWorldModel? Model { get; private set; }

	/// <summary>The Sanctuary's owner: the IM hero's player as the parser's statement named it.</summary>
	public static int Player { get; private set; } = Fable3ActingPlayer.One;

	/// <summary>The list's cursor, Godot's.</summary>
	public static int Cursor { get; private set; }

	public static ulong Generation { get; private set; }

	public static bool Up => Model is not null && HostBridge.IsInGame;

	/// <summary>The Sanctuary's list holds rows.</summary>
	public static bool ListUp => Up && Model!.Rows.Count > 0;

	public static void Register(Engine_UiGuiNatives router)
	{
		router.Register("@Interaction.World.Parsed", call => Parsed((int)call.Argument(0).Number,
			Fable3GuiWorldModel.Stated((int)call.Argument(1).Number, (int)call.Argument(2).Number, call.Argument(3))));
		router.Register("@Interaction.World.Closed", _ => Closed());
	}

	private static void Parsed(int player, Fable3GuiWorldModel model)
	{
		Model = model;
		Player = player == Fable3ActingPlayer.Two ? Fable3ActingPlayer.Two : Fable3ActingPlayer.One;
		Cursor = Model.Cursor;
		ReleaseSticks();
		++Generation;
		GD.Print($"[fable3-guiworld] table for player {Player}: {Model.Rows.Count} row(s), cursor {Cursor}" +
			(Model.Rows.Count > 0 ? $", first '{Model.Rows[0].TextTag}'" : string.Empty) +
			$"; holds A {Model.Holds(Fable3GuiWorldModel.Button.A)} B {Model.Holds(Fable3GuiWorldModel.Button.B)} " +
			$"X {Model.Holds(Fable3GuiWorldModel.Button.X)} Y {Model.Holds(Fable3GuiWorldModel.Button.Y)}");
	}

	private static void Closed()
	{
		ReleaseSticks();
		Model = null;
		Cursor = 0;
		++Generation;
		GD.Print("[fable3-guiworld] the Sanctuary's mode closed");
	}

	private static void Post(string lever)
	{
		HostBridge.PostFrontEndAction(lever);
		GD.Print($"[fable3-guiworld] lever '{lever}'");
	}

	// ------------------------------------------------------------------ the pointer (the list view)

	/// <summary>The cursor onto <paramref name="row"/> for <paramref name="player"/>, then A when <paramref name="accept"/>.</summary>
	public static void Point(int row, bool accept, int player)
	{
		if (Model is not { } model || model.Rows.Count == 0 || player != Player)
		{
			return;
		}

		int target = System.Math.Clamp(row, 0, model.Rows.Count - 1);
		MoveCursor(target, player);
		if (accept && model.Verb(Fable3GuiWorldModel.Button.A) is { } verb)
		{
			Post(Fable3GuiWorldModel.Lever(player, verb));
		}
	}

	private static void MoveCursor(int row, int player)
	{
		if (row == Cursor)
		{
			return;
		}

		Cursor = row;
		++Generation;
		Post(Fable3GuiWorldModel.CursorLever(player, row));
	}

	// ------------------------------------------------------------------ the pads and the keys

	private const uint LeftTriggerBit = 0x10000u;
	private const uint RightTriggerBit = 0x20000u;
	private const int Pads = 4;

	private static readonly uint[] _was = new uint[Pads];
	private static readonly ulong[] _fedAt = new ulong[Pads];
	private static string _left = "none";
	private static string _right = "none";
	private static readonly Fable3GuiWorldModel.StepRepeat _listStep = new();

	private static void ReleaseSticks()
	{
		if (_left != "none" && Model is not null)
		{
			Post(Fable3GuiWorldModel.Lever(Player, "stick:none"));
		}

		if (_right != "none" && Model is not null)
		{
			Post(Fable3GuiWorldModel.Lever(Player, "rs:none"));
		}

		_left = _right = "none";
		_listStep.Step(0, 0.0);
	}

	/// <summary>
	/// One pad's frame as the guest is handed it (<see cref="Fable3Input.PublishPad"/>: the pad and the keys
	/// bound to it). Presses of A / B / X / Y / START and releases of LB / RB / LT / RT become levers; the
	/// owner's sticks become held directions and the list's steps.
	/// </summary>
	public static void Feed(int pad, in GameInput.PadState guest)
	{
		if (pad < 0 || pad >= Pads)
		{
			return;
		}

		ulong frame = Godot.Engine.GetProcessFrames();
		uint buttons = guest.Buttons | (guest.LeftTrigger > 30 ? LeftTriggerBit : 0u) |
			(guest.RightTrigger > 30 ? RightTriggerBit : 0u);
		// A pad not fed last frame (its pad was held neutral) starts from what it holds: no edge.
		uint was = _fedAt[pad] + 1 < frame ? buttons : _was[pad];
		_was[pad] = buttons;
		_fedAt[pad] = frame;
		if (!Up)
		{
			if (Model is null)
			{
				_left = _right = "none";
			}

			return;
		}

		Fable3GuiWorldModel model = Model!;
		// The two local players' pads (Fable3ActingPlayer.LocalOfPad); a pad no local player holds posts nothing.
		int player = Fable3ActingPlayer.LocalOfPad(pad);
		if (player == 0)
		{
			return;
		}

		uint pressed = buttons & ~was;
		uint released = was & ~buttons;
		Fable3UiStatements.TopBoxMessage? topBox = Fable3UiStatements.TopBox.Current;

		// The Sanctuary is its owner's (Fable3GuiWorldModel.TakesPress): another player's pad posts only B.
		void Press(uint bit, Fable3GuiWorldModel.Button button, bool takenAhead)
		{
			if ((pressed & bit) != 0u && !takenAhead && Fable3GuiWorldModel.TakesPress(player, Player, button) &&
				model.Verb(button) is { } verb)
			{
				Post(Fable3GuiWorldModel.Lever(player, verb));
			}
		}

		// The TopBox takes player one's A and Y while its message shows them (Fable3Boxes, Fable3HudInput).
		bool one = pad == 0;
		Press(GameInput.ButtonA, Fable3GuiWorldModel.Button.A, one && topBox is { ShowA: true });
		Press(GameInput.ButtonB, Fable3GuiWorldModel.Button.B, false);
		Press(GameInput.ButtonX, Fable3GuiWorldModel.Button.X, false);
		Press(GameInput.ButtonY, Fable3GuiWorldModel.Button.Y, one && topBox is { ShowY: true });
		Press(GameInput.Start, Fable3GuiWorldModel.Button.Start, false);

		void Release(uint bit, string verb)
		{
			if ((released & bit) != 0u && player == Player)
			{
				Post(Fable3GuiWorldModel.Lever(player, verb));
			}
		}

		Release(GameInput.LeftShoulder, "lb");
		Release(GameInput.RightShoulder, "rb");
		Release(LeftTriggerBit, "lt");
		Release(RightTriggerBit, "rt");

		if (player != Player)
		{
			return;
		}

		float lx = guest.LeftX / 32767f, ly = guest.LeftY / 32767f;
		string left = Fable3GuiWorldModel.StickDirection(lx, ly);
		if (left != _left)
		{
			_left = left;
			Post(Fable3GuiWorldModel.Lever(player, "stick:" + left));
		}

		string right = Fable3GuiWorldModel.StickDirection(guest.RightX / 32767f, guest.RightY / 32767f);
		if (right != _right)
		{
			_right = right;
			Post(Fable3GuiWorldModel.Lever(player, "rs:" + right));
		}

		// The list's step: the event's value past ±0.5, down below (sub_82972ED8 0x829730F0 / 0x8297312C).
		if (model.Rows.Count > 0)
		{
			int direction = ly < -0.5f ? 1 : ly > 0.5f ? -1 : 0;
			int step = _listStep.Step(direction, Time.GetTicksMsec() / 1000.0);
			if (step != 0)
			{
				MoveCursor(System.Math.Clamp(Cursor + step, 0, model.Rows.Count - 1), player);
			}
		}
	}
}
