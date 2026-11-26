namespace Refii.Titles.Fable3;

using Godot;
using Refii.Core;
using Refii.Game;
using Refii.Host;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// FABLE 3'S LITE PAUSE (the PC build's P): the HOST stops stepping the simulation and says so.
/// </summary>
/// <remarks>
/// <para>WHY THE HOST'S. The PC function behind the key is in neither 360 build and the 360's own
/// lite pause is inline code behind a cutscene test (Fable3/src/hooks/src/pc_extras.cpp), so there
/// is no guest call to make. The guest is a simulation server Godot steps: a pause is Godot not
/// stepping it. The hold is the one photo mode takes — the session's pause
/// (<see cref="GameGuestHold"/>) beside the engine's stated-pause latch
/// (<see cref="Engine_StatedPause.HostHold"/>), so the drawn world holds exactly as under a pause
/// the guest states. Godot itself keeps running: the debug channel, the window, this notice.</para>
/// <para>WHEN IT IS REFUSED (<see cref="Refusal"/>): in an online session, where two machines
/// must not drift apart; outside play; and whenever the guest already holds the world or the
/// input (a box, a screen, a cutscene, the Sanctuary, photo mode). Local co-op is one simulation,
/// so it pauses for both players.</para>
/// <para>WHILE IT LASTS the guest reads no input (it is not stepped). The key again, or A, B or
/// Start on player one's pad, ends it, and that press is held back from the guest
/// (<see cref="GameTitleInput.HoldUntilReleased"/>). The notice is the 360's own lite-pause line,
/// `GUI_SCREEN_PAUSE_ICS`, in the title's font on the boxes' panel.</para>
/// </remarks>
public partial class Fable3LitePause : Control
{
	/// <summary>The key action's name (<see cref="Fable3Input"/>).</summary>
	public const string Action = "lite_pause";

	/// <summary>The 360's own lite-pause text (its cutscene pause box).</summary>
	public const string TextTag = "GUI_SCREEN_PAUSE_ICS";

	private const float ReferenceHeight = 720f;
	private const float Padding = 16f;
	private static readonly Color Panel = new(0f, 0f, 0f, 0.7f);

	/// <summary>Whether the lite pause holds the simulation.</summary>
	public static bool Paused { get; private set; }

	private GameGuestHold? _hold;
	private bool _keyWas;
	private uint _padWas;
	private UiFont? _font;
	private string _culture = string.Empty;

	/// <summary>
	/// Why the lite pause may not start, or null when it may. <paramref name="playerOwned"/> is
	/// who this machine plays (3 both players: offline or the couch; 1 or 2: one side of an online
	/// session); <paramref name="worldHasInput"/> whether the player is in the world with nothing
	/// holding the input; <paramref name="guestHolds"/> whether a pause is already stated.
	/// </summary>
	public static string? Refusal(uint playerOwned, bool inGame, bool worldHasInput, bool guestHolds, bool photoMode) =>
		!inGame ? "not in a game" :
		playerOwned != 3u ? "an online session cannot be paused" :
		photoMode ? "photo mode holds the game" :
		guestHolds ? "the game already holds the world" :
		!worldHasInput ? "a screen or a box has the input" :
		null;

	/// <summary>Whether a pad press ends the pause: A, B or Start going down.</summary>
	public static bool PadEnds(uint down, uint was) =>
		(down & ~was & (GameInput.ButtonA | GameInput.ButtonB | GameInput.Start)) != 0u;

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
	}

	public override void _ExitTree() => Resume();

	public override void _Process(double delta)
	{
		// A key typed into the dev menu, a rebind row or a text entry is not the pause key.
		bool typing = GameDevMenu.KeyboardCaptured || Engine_UiInputCapture.Blocking || Engine_UiTextEntry.AnyOpen;
		bool key = !typing && GameInput.KeyActionHeld(Action);
		bool pressed = key && !_keyWas;
		_keyWas = key;
		uint pad = GameInput.Pad0.Buttons;
		bool padEnds = PadEnds(pad, _padWas);
		_padWas = pad;

		if (Paused)
		{
			// Photo mode, opened over the pause, lets the latch go when it closes: it is kept.
			Engine_StatedPause.HostHold = true;
			if ((pressed || padEnds) && !GamePhotoMode.Active)
			{
				Resume();
			}

			return;
		}

		if (!pressed)
		{
			return;
		}

		string? refusal = Refusal(HostBridge.PlayerOwned(), HostBridge.IsInGame,
			GameInput.Playing && Engine_TitleGame.Active.Input.WorldHasInput, Engine_StatedPause.Latched, GamePhotoMode.Active);
		if (refusal is not null)
		{
			GD.Print($"[fable3-lite-pause] refused: {refusal}");
			return;
		}

		if (GameGuestHold.Take(null, GameSession.Current) is not { } hold)
		{
			GD.Print("[fable3-lite-pause] refused: no game session to hold");
			return;
		}

		_hold = hold;
		Engine_StatedPause.HostHold = true;
		Paused = true;
		QueueRedraw();
		GD.Print("[fable3-lite-pause] paused: the host is not stepping the simulation");
	}

	private void Resume()
	{
		if (!Paused)
		{
			return;
		}

		Paused = false;
		Engine_StatedPause.HostHold = false;
		_hold?.Release();
		_hold = null;
		// The press that ended it is not the game's.
		Engine_TitleGame.Active.Input.HoldUntilReleased();
		QueueRedraw();
		GD.Print("[fable3-lite-pause] resumed");
	}

	public override void _Draw()
	{
		if (!Paused)
		{
			return;
		}

		if (_culture != Engine_GameCulture.Current || _font is null)
		{
			_culture = Engine_GameCulture.Current;
			_font = Fable3Fonts.For(_culture).Get(UiFontBucket.Normal);
		}

		if (_font is null)
		{
			return;
		}

		Vector2 size = GetViewportRect().Size;
		float scale = size.Y / ReferenceHeight;
		string text = Engine_UiGuiText.Resolve(TextTag, Fable3Strings.Current);
		float width = Engine_UiCanvasText.Measure(_font, text) * scale;
		float height = _font.Font.LineHeight * scale;
		var box = new Rect2(
			new Vector2((size.X - width) * 0.5f - (Padding * scale), (size.Y - height) * 0.5f - (Padding * scale)),
			new Vector2(width + (2f * Padding * scale), height + (2f * Padding * scale)));
		DrawRect(box, Panel);
		Engine_UiCanvasText.DrawLine(this, _font, text, box.Position + new Vector2(Padding * scale, Padding * scale), scale, Colors.White);
	}
}
