using System.Collections.Generic;

namespace Refii.Titles.Fable2;

/// <summary>
/// THE CO-OP SCREEN'S PRESSES (screen 24), as the guest's own action codes for its input slot
/// vf+24 (`CoopAction:&lt;code&gt;:&lt;player&gt;`, guest_tick_pump.cpp DispatchCoopAction): the D-pad and the
/// left stick's directions as 28 up, 29 down, 30 left, 31 right on the press and 32..35 on the
/// release (each page's repeat filter `sub_828818E8` gates 28..31 and is reset by 32..35, TU1
/// 0x82881A88), A 36, B 37, X 38, Y 39. The stick counts as a direction past 0.75 and stops
/// below 0.65, as the shipped `THUMBSTICK_*_THRESHOLD`s and the front end's translator
/// (`sub_826E39C0` ±0.75) do.
/// </summary>
public static class Fable2CoopScreenPresses
{
	public const int Up = 28;
	public const int Down = 29;
	public const int Left = 30;
	public const int Right = 31;
	public const int ReleaseOffset = 4;
	public const int Accept = 36;
	public const int Back = 37;
	public const int ButtonX = 38;
	public const int ButtonY = 39;

	/// <summary>The lever for one code, for the player whose press it is.</summary>
	public static string Lever(int code, int player) => $"CoopAction:{code}:{player}";

	/// <summary>
	/// Which of a pad's codes go to the guest for <paramref name="player"/>. Player one's page in
	/// the remaster has no row the guest draws (the share sliders are hidden: each hero keeps their
	/// own progression), and their left / right steps the screen row Godot draws, so their
	/// directions stay Godot's; player one's Y has no arm (`sub_82438A08`'s table ends at 38).
	/// Player two's X is Godot's own-hero toggle (GameUi.DriveCoopHeroX); in local co-op while player
	/// two is not signed in (<paramref name="askSignIn"/>) it is also the guest's: the right page's X asks
	/// for player two's sign-in (TU1 `sub_82438CF8` case 38: `sub_8243A318` — local co-op, player two
	/// not signed in — then `sub_82882AD8(+300)` 0x8243933C, `XamShowSigninUI(2, 0)` 0x82882B10), which
	/// signs player two in to the shared profile with no box (rexglue shadow xam_user.cpp).
	/// <paramref name="withheld"/> are player two's picker presses Godot answers itself past the guest's
	/// six saves.
	/// </summary>
	public static bool Forwarded(int code, int player, Engine_CoopSavePicker.Press withheld, bool askSignIn = false) => player switch
	{
		1 => code is Accept or Back or ButtonX,
		2 => code switch
		{
			ButtonX => askSignIn,
			Left => (withheld & Engine_CoopSavePicker.Press.Left) == 0,
			Right => (withheld & Engine_CoopSavePicker.Press.Right) == 0,
			Accept => (withheld & Engine_CoopSavePicker.Press.Accept) == 0,
			_ => true,
		},
		_ => false,
	};

	/// <summary>One pad's held directions and buttons, edge-detected.</summary>
	public struct PadEdges
	{
		private bool _up, _down, _left, _right, _a, _b, _x, _y;
		private bool _stickUp, _stickDown, _stickLeft, _stickRight;

		/// <summary>This frame's codes, presses and releases, in a fixed order.</summary>
		public List<int> Feed(bool up, bool down, bool left, bool right, bool a, bool b, bool x, bool y,
			int leftX, int leftY)
		{
			double sx = leftX / 32767.0;
			double sy = leftY / 32767.0;
			_stickUp = Hold(_stickUp, sy);
			_stickDown = Hold(_stickDown, -sy);
			_stickLeft = Hold(_stickLeft, -sx);
			_stickRight = Hold(_stickRight, sx);

			var codes = new List<int>();
			Edge(ref _up, up || _stickUp, Up, codes);
			Edge(ref _down, down || _stickDown, Down, codes);
			Edge(ref _left, left || _stickLeft, Left, codes);
			Edge(ref _right, right || _stickRight, Right, codes);
			Press(ref _a, a, Accept, codes);
			Press(ref _b, b, Back, codes);
			Press(ref _x, x, ButtonX, codes);
			Press(ref _y, y, ButtonY, codes);
			return codes;
		}

		private static bool Hold(bool held, double along) =>
			held ? along > Engine_CoopSavePicker.StickRelease : along >= Engine_CoopSavePicker.StickPush;

		private static void Edge(ref bool was, bool now, int press, List<int> codes)
		{
			if (now && !was)
			{
				codes.Add(press);
			}
			else if (!now && was)
			{
				codes.Add(press + ReleaseOffset);
			}

			was = now;
		}

		private static void Press(ref bool was, bool now, int code, List<int> codes)
		{
			if (now && !was)
			{
				codes.Add(code);
			}

			was = now;
		}
	}
}
