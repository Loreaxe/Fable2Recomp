namespace Refii.Titles.Fable2;

/// <summary>
/// The statement of player two's own HUD (their d-pad icons and targeted title) that player two's
/// half shows, for as long as that half is drawn. Pure: no guest, no Godot.
/// </summary>
/// <remarks>
/// <para>The guest's builders state hero two's d-pad and title only while the guest is told the screen
/// is split (guest_tick_pump.cpp SamplePlayerTwoHud asks RefiiCoopSplitScreen, which follows
/// <c>CoopSplitScreen:</c>, GameSplitView.GuestSplit). Hybrid's swap draws player two's half for up
/// to Engine_CameraScreens.SwapSeconds on either side of that: at the rejoin the guest is told
/// "shared" at once while the half runs back into the shared picture, and at the split the half is
/// drawn from the decision's frame while the guest takes the split on its next tick.</para>
/// <para>So the half keeps the last statement it was given until it is parked, and before the
/// first statement of a split it has none: its d-pad slots stand empty. It never falls back to
/// the shared HUD's, which are player one's.</para>
/// </remarks>
public struct Fable2PlayerTwoHudHold<T>
	where T : struct
{
	private T? _held;

	/// <summary>
	/// This frame's statement for player two's half: the one stated now, else the last one stated
	/// since the half was last parked; null while the half is parked or nothing has been stated for it.
	/// </summary>
	public T? Take(bool halfDrawn, T? stated)
	{
		if (!halfDrawn)
		{
			_held = null;
			return null;
		}

		if (stated is { } now)
		{
			_held = now;
		}

		return _held;
	}
}
