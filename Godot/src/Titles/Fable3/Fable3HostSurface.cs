namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Refii.Game;
using Refii.Ui;

/// <summary>
/// A page of the host's own over Fable 3's screens, on the one menu template (<see cref="Fable3ListScreen"/>):
/// the joining player's save choice, the achievements page, another player's card. The host drives it:
/// up / down move its cursor (held, they repeat), A accepts the cursor row, B leaves, the shoulders and
/// triggers step its tabs, the pointer places the cursor and a click accepts. It is read from the guest
/// pads it names (<see cref="Pads"/>) — the player it is for, or either local player — and while one is
/// up the guest's pad is neutral (<see cref="Fable3HostOptions.IsOpen"/>), as under a host options page.
/// </summary>
public abstract partial class Fable3HostSurface : Fable3ListScreen
{
	private const double RepeatDelay = 0.35;
	private const double RepeatEvery = 0.09;
	private const int StickDeadZone = 16000;
	private const uint TriggerDown = 128u;

	private static int _up;

	/// <summary>Whether any host surface is up.</summary>
	public static bool AnyUp => _up > 0;

	private Engine_UiPressEdges _edges;
	private bool _shown;
	private bool _wasLeftTrigger = true;
	private bool _wasRightTrigger = true;
	private int _held;
	private double _repeat;
	private int _rows;

	/// <summary>The 0-based cursor row.</summary>
	protected int Cursor { get; set; }

	/// <summary>The guest pads that drive the surface while it is up.</summary>
	protected abstract IReadOnlyList<int> Pads { get; }

	/// <summary>The page this frame (rows, title, prompts, tabs), or null while the surface is not up.</summary>
	protected abstract Fable3ScreenPage? Page();

	/// <summary>A (or a click) on <paramref name="row"/>.</summary>
	protected abstract void Accept(int row);

	/// <summary>B: leave. Nothing by default (a surface the player may not leave).</summary>
	protected virtual void Back()
	{
	}

	/// <summary>A tab step (-1 back, +1 on) by the shoulders or triggers. Nothing by default.</summary>
	protected virtual void StepTab(int step)
	{
	}

	/// <summary>Tab <paramref name="tab"/> picked by the pointer. Nothing by default.</summary>
	protected virtual void PickTab(int tab)
	{
	}

	protected sealed override Fable3ScreenPage? Compose(double delta)
	{
		Fable3ScreenPage? page = Page();
		if (page is null)
		{
			Shown(false);
			return null;
		}

		if (!_shown)
		{
			// The press that opened it is not a press on it.
			Shown(true);
			_edges.Hold();
			_wasLeftTrigger = true;
			_wasRightTrigger = true;
			_held = 0;
		}

		_rows = page.Value.Rows.Count;
		Cursor = System.Math.Clamp(Cursor, 0, System.Math.Max(0, _rows - 1));
		Drive(delta);

		// The answer may have closed it or moved its rows: what is drawn is the page after the press.
		Fable3ScreenPage? after = Page();
		if (after is not { } now)
		{
			Shown(false);
			return null;
		}

		_rows = now.Rows.Count;
		Cursor = System.Math.Clamp(Cursor, 0, System.Math.Max(0, _rows - 1));
		return now with { Selected = Cursor };
	}

	/// <summary>A surface leaving the tree while up is down: the guest's pad is not held neutral for it.</summary>
	public override void _ExitTree()
	{
		Shown(false);
		base._ExitTree();
	}

	private void Shown(bool shown)
	{
		if (shown != _shown)
		{
			_shown = shown;
			_up += shown ? 1 : -1;
		}
	}

	private void Drive(double delta)
	{
		uint down = 0u;
		uint trigger = 0u;
		int vertical = 0;
		foreach (int pad in Pads)
		{
			GameInput.PadState seen = GameInput.Seen(pad);
			down |= seen.Buttons;
			trigger |= (seen.LeftTrigger >= TriggerDown ? 1u : 0u) | (seen.RightTrigger >= TriggerDown ? 2u : 0u);
			if (vertical == 0)
			{
				vertical = (seen.Buttons & GameInput.DpadUp) != 0 || seen.LeftY > StickDeadZone ? -1 :
					(seen.Buttons & GameInput.DpadDown) != 0 || seen.LeftY < -StickDeadZone ? 1 : 0;
			}
		}

		uint pressed = _edges.Take(down);
		bool leftTrigger = (trigger & 1u) != 0;
		bool rightTrigger = (trigger & 2u) != 0;
		bool tabBack = (leftTrigger && !_wasLeftTrigger) || (pressed & GameInput.LeftShoulder) != 0;
		bool tabOn = (rightTrigger && !_wasRightTrigger) || (pressed & GameInput.RightShoulder) != 0;
		_wasLeftTrigger = leftTrigger;
		_wasRightTrigger = rightTrigger;

		if ((pressed & GameInput.ButtonA) != 0)
		{
			Accept(Cursor);
			Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
			return;
		}

		if ((pressed & GameInput.ButtonB) != 0)
		{
			Back();
			Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
			return;
		}

		if (tabBack || tabOn)
		{
			StepTab(tabOn ? 1 : -1);
			return;
		}

		if (vertical != _held)
		{
			_held = vertical;
			_repeat = RepeatDelay;
		}
		else if (vertical == 0 || (_repeat -= delta) > 0.0)
		{
			return;
		}
		else
		{
			_repeat = RepeatEvery;
		}

		if (vertical != 0 && _rows > 0)
		{
			Cursor = System.Math.Clamp(Cursor + vertical, 0, _rows - 1);
		}
	}

	/// <summary>The pointer places the cursor; a click accepts the row.</summary>
	protected override void PointAt(int row, bool clicked)
	{
		if (row < 0 || row >= _rows)
		{
			return;
		}

		Cursor = row;
		if (clicked)
		{
			Accept(row);
		}
	}

	protected override void PointTab(int tab) => PickTab(tab);
}
