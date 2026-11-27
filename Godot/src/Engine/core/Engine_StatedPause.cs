namespace Refii.Core;

/// <summary>
/// The guest's statement that its world is paused, latched once per frame for everything Godot
/// draws on its own clock. While latched the drawn world holds: the shared pair does not swap
/// (<see cref="Refii.Entities.Engine_DrawnFrame.NoteRestated"/>), every playhead advances by
/// zero (<see cref="Engine_DrawAdvance"/>), and every `_Process` consumer's world delta is 0.
/// </summary>
/// <remarks>
/// THE STATEMENT, NOT THE SYMPTOM. The guest's tick counter is not a pause signal: on an
/// ordinary pause it starves and the world coasts to a stop over a second; under a paused
/// cutscene it keeps counting, so pairs swapped onto unchanged poses and every rig played its
/// last rate and snapped a quarter second later. The statement is the in-game GUI's active
/// screen (`RefiiHostGetPauseState`): a real screen up means the GameFlow GUI mode is in force;
/// and the world held: the guest's game-flow step ran no world tick (the lite pause box).
/// `GameSession.Paused` is NOT touched — the guest's steps keep running the pause book's GUI tick.
/// </remarks>
public static class Engine_StatedPause
{
	/// <summary>Whether a pause is stated this frame, by the guest or by the host.</summary>
	public static bool Latched { get; private set; }

	/// <summary>
	/// A pause the HOST states — photo mode — held exactly as the guest's: the same latch, the
	/// same zero advance, the same unswapped pair. <see cref="Refii.Game.GameSession.Paused"/>
	/// stops the guest's steps beside it; this stops what Godot draws on its own clock.
	/// </summary>
	public static bool HostHold { get; set; }

	/// <summary>Frames the latch has held in the current pause; 0 when not latched.</summary>
	public static long HeldFrames { get; private set; }

	/// <summary>Frames held over the run; for the debug channel.</summary>
	public static long HeldFramesTotal { get; private set; }

	/// <summary>Pauses latched over the run; for the debug channel.</summary>
	public static long Pauses { get; private set; }

	/// <summary>Once per frame, before anything swaps or draws: takes the guest's statement.</summary>
	public static void BeginFrame(bool stated)
	{
		if (stated || HostHold)
		{
			if (!Latched)
			{
				Latched = true;
				HeldFrames = 0;
				++Pauses;
			}

			++HeldFrames;
			++HeldFramesTotal;
			return;
		}

		Latched = false;
		HeldFrames = 0;
	}

	/// <summary>
	/// The in-game GUI's statement read as a pause: a screen is up, or the guest's last game-flow
	/// step ran no world tick (`worldHeld` 1 — the lite pause box and every other state whose step
	/// slot does not call the world, payload_bridge.h key 19). 31 is the id `ExitScreen` leaves
	/// when nothing is up; `uint.MaxValue` is the bridge's "no screen manager". `worldHeld` 2 is the
	/// guest's statement that its world ran under a pause (the player's own pause in an online
	/// session): a screen is up and nothing is held.
	/// </summary>
	public static bool StatesPause(uint screenId, uint worldHeld = 0u) =>
		worldHeld == 1u || (worldHeld != 2u && screenId != 31u && screenId != uint.MaxValue);
}
