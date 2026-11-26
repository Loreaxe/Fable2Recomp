using System.Collections.Generic;
using Refii.Core;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// THE PAD BINDINGS OF KEY ACTIONS: an action that declares itself pad-bindable
/// (<see cref="KeyAction.PadBindable"/>) may carry <c>Pad:</c> chords in its <c>key_*</c> setting
/// (<see cref="Engine_PadActionBinding"/>). While a chord is held on a player's pad, in the action's
/// context, the action is down for that player and the chord's inputs are withheld from the pad —
/// from the guest and from the host's own readers of it alike.
/// </summary>
/// <remarks>
/// <para>A chord names the GAME's buttons, as a mod's pad binding does: it is read from the
/// joypads' buttons after each player's remap (<see cref="PadState.PhysicalButtons"/>), never from
/// the keys that press the same buttons. Until the last input of a chord goes down the others
/// reach the game as themselves.</para>
/// <para>A <see cref="KeyPress.HostAction"/> action posts once per press, for the player of the
/// pad that pressed it (<see cref="KeyAction.HostActionFor"/>): on the frame the chord goes down
/// in the action's context, and not again until it is released, whatever the context does
/// meanwhile (<see cref="Engine_PressLatch{TInput}"/>). An action a title reads itself
/// (<see cref="KeyPress.None"/>) is read through <see cref="KeyActionHeld"/>.</para>
/// <para>A title with no pad-bindable action holds no chord and nothing here runs.</para>
/// </remarks>
public partial class GameInput
{
	/// <summary>Each pad-bindable action's chords, for the actions that have any.</summary>
	private static readonly Dictionary<string, IReadOnlyList<ushort>> _padChords = new();

	/// <summary>The actions whose chord is down, per guest pad.</summary>
	private static readonly HashSet<(string Action, int Pad)> _padActionsHeld = new();

	/// <summary>The host actions' chord presses, per guest pad: each posts once per press (<see cref="Engine_PressLatch{TInput}"/>).</summary>
	private static readonly Engine_PressLatch<(string Action, int Pad)> _padActionPresses = new();

	/// <summary>Reads one action's chords from its stored setting.</summary>
	private static void ReadPadBinding(KeyAction action, string stored)
	{
		IReadOnlyList<ushort> chords = action.PadBindable ? Engine_PadActionBinding.Chords(stored) : System.Array.Empty<ushort>();
		if (chords.Count == 0)
		{
			_padChords.Remove(action.Name);
		}
		else
		{
			_padChords[action.Name] = chords;
		}
	}

	/// <summary>Whether <paramref name="action"/>'s pad chord is down on any player's pad.</summary>
	public static bool PadActionHeld(string action)
	{
		foreach ((string name, int _) in _padActionsHeld)
		{
			if (name == action)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// One pad's chords, after its joypads are merged and before anything reads it: marks the
	/// actions held, posts a host action's press, and takes the held chords' inputs out of
	/// <paramref name="state"/>. <paramref name="joypad"/> is the joypad holding the pad (−1 for
	/// none), whose triggers are read through its player's remap.
	/// </summary>
	private static void ApplyPadActions(int pad, int joypad, ref PadState state)
	{
		if (_padChords.Count == 0)
		{
			return;
		}

		int player = Engine_TitleGame.Active.Input.ModPlayer(pad);
		uint left = 0, right = 0;
		if (joypad >= 0 && player != 0)
		{
			// The joypad's own triggers, and only while the pad state took them (focus, a rebind capture).
			(_, left, right) = GameButtonsOf(joypad);
			left = System.Math.Min(left, state.LeftTrigger);
			right = System.Math.Min(right, state.RightTrigger);
		}

		ushort held = player == 0 ? (ushort)0 : Engine_PadActionBinding.Held(state.PhysicalButtons, left, right);
		ushort withheld = 0;
		foreach (KeyAction action in KeyActions)
		{
			if (!_padChords.TryGetValue(action.Name, out IReadOnlyList<ushort>? chords))
			{
				continue;
			}

			ushort down = held != 0 ? Engine_PadActionBinding.Active(chords, held) : (ushort)0;
			bool inContext = InContext(action.When);
			if (action.Press == KeyPress.HostAction &&
				_padActionPresses.Press((action.Name, pad), down != 0, inContext))
			{
				string post = action.HostActionFor?.Invoke(player) ?? action.HostAction;
				if (post.Length != 0)
				{
					HostBridge.PostFrontEndAction(post);
				}
			}

			ushort active = inContext ? down : (ushort)0;
			if (active == 0)
			{
				_padActionsHeld.Remove((action.Name, pad));
				continue;
			}

			withheld |= active;
			_padActionsHeld.Add((action.Name, pad));
		}

		if (withheld != 0)
		{
			uint before = state.Buttons;
			Engine_PadActionBinding.Withhold(withheld, ref state.Buttons, ref state.LeftTrigger, ref state.RightTrigger);
			uint taken = before & ~state.Buttons;
			state.PhysicalButtons &= ~taken;
			state.OtherButtons &= ~taken;
		}
	}
}
