using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The two HUD widgets the `GUI` natives drive and nothing else does: the golf swing meter
/// and the minigame-completion tally. The expression meter is the guest's own object, stated by
/// its methods (`@Hud.ExpressionMeter`) and drawn by Engine_UiHudExpressionMeter.
/// </summary>
/// <remarks>
/// The natives set targets; the widget runs a clock. `StartBarJobFill` states a RATE and the
/// bar travels on its own, so each widget here carries the per-frame arithmetic.
/// </remarks>
public sealed class Engine_UiGuiWidgets
{
	/// <summary>Bumped whenever anything changes, so a view can skip untouched frames.</summary>
	public ulong Generation { get; private set; }

	// ================= THE GOLF SWING METER =================================================

	/// <summary>The angular limits the swing travels between, in RADIANS.</summary>
	public const float GolfAngleMin = -2.7415926f;

	public const float GolfAngleMax = -0.4f;

	public const float GolfAngleCentre = -1.5707964f;

	/// <summary>`GolfAngleMax - GolfAngleCentre`, the span a "percentage" is taken against.</summary>
	public const float GolfAngleRange = 1.1707964f;

	/// <summary>The meter is configured and can be opened.</summary>
	public bool GolfConfigured { get; private set; }

	/// <summary>The meter is up and swinging.</summary>
	public bool GolfActive { get; private set; }

	/// <summary>`Display` opens it directly; otherwise a button press does.</summary>
	public bool GolfPressToClose { get; private set; }

	/// <summary>The swing's angle, radians, travelling between the two limits.</summary>
	public float GolfAngle { get; private set; }

	/// <summary>Angular velocity, radians per second; its sign flips at each limit.</summary>
	public float GolfSpeed { get; private set; }

	/// <summary>The sweet spot's HALF-width in radians, shrinking as the swing runs.</summary>
	public float GolfSweetHalfWidth { get; private set; }

	private float _golfMinHalfWidth;

	private float _golfMaxHalfWidth;

	private float _golfShrinkRate;

	/// <summary>Recomputed every frame — a live flag, never a latch.</summary>
	public bool GolfInSweetSpot { get; private set; }

	/// <summary>
	/// Sound names the widgets have asked for since the last drain.
	/// </summary>
	/// <remarks>
	/// The guest plays the golf meter's sounds from inside the widget, so owning the widget
	/// means owning them. A queue, not a flag: a bounce and a close can land in one frame.
	/// </remarks>
	private readonly List<string> _sounds = new();

	/// <summary>Takes the sounds asked for since the last call.</summary>
	public IReadOnlyList<string> TakeSounds()
	{
		if (_sounds.Count == 0)
		{
			return System.Array.Empty<string>();
		}

		var taken = _sounds.ToArray();
		_sounds.Clear();
		return taken;
	}

	/// <summary>
	/// `GUI.DisplayGolfSwingMeter(table)` — six keys, and every one of them is an ANGLE.
	/// </summary>
	/// <remarks>
	/// The key names say pixels and percent; the units are RADIANS. `BarSizePixels`,
	/// `SweetSpotShrinkPixelsPerSecond` and `SweetSpotMovementPixelsPerSecond` are scaled into
	/// the angular range before storing. The clamps are the guest's own.
	/// </remarks>
	public void ConfigureGolf(
		float barSize, float minPercent, float initialPercent,
		float shrink, float movement, bool pressToClose)
	{
		_golfMinHalfWidth = GolfAngleRange * Math.Clamp(minPercent, 0.1f, 0.5f);
		_golfMaxHalfWidth = GolfAngleRange * Math.Clamp(barSize, 0.5f, 1.0f);
		GolfSweetHalfWidth = _golfMaxHalfWidth * Math.Clamp(initialPercent, minPercent, 1.0f);

		// Negative is legal and means the spot grows; the clamp is symmetric.
		_golfShrinkRate = Math.Clamp(shrink, -0.2f, 0.2f);
		GolfSpeed = Math.Clamp(movement, 1.0f, 10.0f);
		GolfPressToClose = pressToClose;
		GolfConfigured = true;

		// Press-to-close opens immediately; otherwise Display only configures.
		if (pressToClose)
		{
			OpenGolf();
		}

		++Generation;
	}

	/// <summary>Puts the meter up and starts the swing at its far end.</summary>
	public void OpenGolf()
	{
		GolfActive = true;
		GolfInSweetSpot = false;
		GolfAngle = GolfAngleMin;
		++Generation;
	}

	/// <summary>
	/// `GUI.CloseGolfSwingMeter()` — and the close is what says whether the swing landed.
	/// </summary>
	public void CloseGolf()
	{
		if (!GolfActive)
		{
			return;
		}

		GolfActive = false;
		_sounds.Add(GolfInSweetSpot
			? "SE_GUI_SWING_METER_SUCCESS"
			: "SE_GUI_SWING_METER_FAIL");
		++Generation;
	}

	// ================= THE MINIGAME TALLY ===================================================
	//
	// Screen 11's handler (vtable TU1 0x820AE3F0, disc 0x820C09D4) holds five 20-byte rows from
	// `handler+28`: +0 claimed, +4 shown, +8 target, +12 the `MiniGameAmount%d` name (toggle and
	// text), +16 `MiniGameName%d`. This is that model, drawn by the `minigame` page provider.

	/// <summary>One row of the tally: a label and a number that counts up to its target.</summary>
	public sealed class MiniGameRow
	{
		public string Label = string.Empty;

		/// <summary>What is on screen right now (`row+4`).</summary>
		public int Displayed;

		/// <summary>Where it is counting to (`row+8`).</summary>
		public int Target;
	}

	/// <summary>How many rows the tally has — a sixth is silently dropped.</summary>
	public const int MiniGameRows = 5;

	private readonly List<MiniGameRow> _miniGame = new();

	/// <summary>The rows in use, in the order they were claimed.</summary>
	public IReadOnlyList<MiniGameRow> MiniGame => _miniGame;

	/// <summary>The tally is up.</summary>
	public bool MiniGameUp { get; private set; }

	/// <summary>`MiniGameTitle` as `TurnOnMiniGameComplete` stated it, resolved.</summary>
	public string MiniGameTitle { get; private set; } = string.Empty;

	/// <summary>`MiniGameButton` as `TurnOnMiniGameComplete` stated it, resolved.</summary>
	public string MiniGameButton { get; private set; } = string.Empty;

	/// <summary>Screen 11 has been seen up since the tally opened: its leaving is the close.</summary>
	private bool _miniGameSeen;

	/// <summary>
	/// `GUI.TurnOnMiniGameComplete(title, button)` (TU1 0x82306BF0 → `sub_823CDCB0`, disc 0x82908818 →
	/// `sub_8258CBE8`): latches `handler+40` (0x823CDCC4 / 0x8258CBFC), sets screen 11 (0x823CDD08 /
	/// 0x8258CC40) and publishes `MiniGameTitle` (0x823CDD58 / 0x8258CC90) and `MiniGameButton`
	/// (0x823CDD9C / 0x8258CCD4); the rows stay as they were. The caller resolves both tags.
	/// </summary>
	public void TurnOnMiniGame(string title, string button)
	{
		if (!MiniGameUp)
		{
			_miniGameSeen = false;
		}

		MiniGameUp = true;
		MiniGameTitle = title;
		MiniGameButton = button;
		++Generation;
	}

	/// <summary>
	/// `GUI.AddAmountToMiniGameComplete(label, amount)` (TU1 0x82306CA0 → `sub_823CDDB8`, disc
	/// 0x829088C8 → `sub_8258CCF0`) — "add" means CLAIM A ROW: the first free one is claimed
	/// (0x823CDE1C / 0x8258CD54), toggled on (0x823CDE5C / 0x8258CD94), labelled (0x823CDE9C /
	/// 0x8258CDD4), given the amount as its target (0x823CDEA0 / 0x8258CDD8) and shown at 0
	/// (0x823CDEAC / 0x8258CDE4). With no free row the call does nothing.
	/// </summary>
	public MiniGameRow? AddMiniGameAmount(string label, int amount)
	{
		if (_miniGame.Count >= MiniGameRows)
		{
			return null;
		}

		var row = new MiniGameRow { Label = label, Target = amount, Displayed = 0 };
		_miniGame.Add(row);
		++Generation;
		return row;
	}

	/// <summary>
	/// The close (TU1 `sub_823CDF10`, disc `sub_8258CE48`): every row released and shown at 0
	/// (0x823CDF4C / 0x8258CE84), its toggle off (0x823CDF8C / 0x8258CEC4), the screen exited
	/// (0x823CDFC4 / 0x8258CEFC). `GUI.TurnOffMiniGameComplete()` is one caller.
	/// </summary>
	public void TurnOffMiniGame()
	{
		if (!MiniGameUp)
		{
			return;
		}

		MiniGameUp = false;
		_miniGameSeen = false;
		_miniGame.Clear();
		++Generation;
	}

	/// <summary>
	/// Screen 11 is or is not the in-game GUI's screen. The close's other callers — the A press
	/// (0x823CDCA0 / 0x8258CBD8) and the handler's vtable slot 9 — exit the screen (0x823CDFC4 /
	/// 0x8258CEFC), so the tally that was seen up and is up no more has closed.
	/// </summary>
	public void ObserveMiniGameScreen(bool up)
	{
		if (!MiniGameUp)
		{
			return;
		}

		if (up)
		{
			_miniGameSeen = true;
		}
		else if (_miniGameSeen)
		{
			TurnOffMiniGame();
		}
	}

	/// <summary>
	/// A on the tally (`lhGuiMinigameCompletedScreen_HandleAction`, code 6): every row shown below
	/// its target, signed (`cmpw` / `bge` 0x823CDC3C / 0x8258CB74), is shown at its target
	/// (0x823CDC44 / 0x8258CB7C) — a counting-down row never snaps. The close it makes when no row
	/// moved (0x823CDC98 / 0x8258CBD0) is the guest's own.
	/// </summary>
	/// <returns>True when a row snapped.</returns>
	public bool ConfirmMiniGame()
	{
		bool snapped = false;
		foreach (MiniGameRow row in _miniGame)
		{
			if (row.Displayed < row.Target)
			{
				row.Displayed = row.Target;
				snapped = true;
			}
		}

		if (snapped)
		{
			++Generation;
		}

		return snapped;
	}

	/// <summary>
	/// Advances the tally by <paramref name="frames"/> GUI ticks. The handler's update (TU1
	/// `sub_823CDBA0`, disc `sub_8258CAD8`, vtable +16, run by the GUI tick for the current screen at
	/// 0x8229AD70 / 0x8229A930) steps the rows in order and stops at the first that moved
	/// (0x823CDBDC / 0x8258CB14). A row (TU1 `sub_823CD688`, disc `sub_8258C5C0`) moves while it is
	/// short of its target — below a positive one, above a negative one — by 1, 19, 199 … one order
	/// per digit of what it shows, clamped onto the target (0x823CD73C..0x823CD770 /
	/// 0x8258C674..0x8258C6A8).
	/// </summary>
	public void StepMiniGame(uint frames)
	{
		if (!MiniGameUp)
		{
			return;
		}

		bool moved = false;
		for (uint f = 0; f < frames && StepMiniGameOnce(); ++f)
		{
			moved = true;
		}

		if (moved)
		{
			++Generation;
		}
	}

	private bool StepMiniGameOnce()
	{
		foreach (MiniGameRow row in _miniGame)
		{
			bool down = row.Target < 0;
			if (down ? row.Displayed <= row.Target : row.Displayed >= row.Target)
			{
				continue;
			}

			int step = down ? -1 : 1;
			for (int value = row.Displayed; Math.Abs(value) >= 10; value /= 10)
			{
				step = (10 * step) + (down ? -9 : 9);
			}

			int next = row.Displayed + step;
			row.Displayed = down ? Math.Max(next, row.Target) : Math.Min(next, row.Target);
			return true;
		}

		return false;
	}

	// ================= THE CLOCKS ===========================================================

	/// <summary>
	/// Runs the golf meter's own motion for one frame; the tally steps on GUI ticks (<see cref="StepMiniGame"/>).
	/// </summary>
	public void Tick(double delta)
	{
		float dt = (float)delta;
		if (TickGolf(dt))
		{
			++Generation;
		}
	}

	private bool TickGolf(float dt)
	{
		if (!GolfActive)
		{
			return false;
		}

		// The sweet spot shrinks (or grows, for a negative rate) and is held between its limits.
		GolfSweetHalfWidth = Math.Clamp(
			GolfSweetHalfWidth - (_golfShrinkRate * dt), _golfMinHalfWidth, _golfMaxHalfWidth);

		GolfAngle += GolfSpeed * dt;
		if (GolfAngle > GolfAngleMax)
		{
			GolfAngle = GolfAngleMax;
			GolfSpeed = -GolfSpeed;
			_sounds.Add("SE_GUI_SWING_METER_TARGET_RIGHT");
		}
		else if (GolfAngle < GolfAngleMin)
		{
			GolfAngle = GolfAngleMin;
			GolfSpeed = -GolfSpeed;
			_sounds.Add("SE_GUI_SWING_METER_TARGET_LEFT");
		}

		// Strict at both ends, unlike the expression meter's inclusive test.
		GolfInSweetSpot = GolfAngle > GolfAngleCentre - GolfSweetHalfWidth &&
			GolfAngle < GolfAngleCentre + GolfSweetHalfWidth;
		return true;
	}
}
