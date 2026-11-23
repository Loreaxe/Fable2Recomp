using Refii.Host;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The game's own rumble switch as a host options page under the Controls fold. Fable 2 shipped
/// the switch without a page behind it: its one writer is the Lua native <c>SetRumbleEnabled</c>
/// (TU1 0x823B3A78, disc 0x823A5488), no shipped script calls it, and the switch is on in
/// <c>.data</c>. The row shows what that native was last called with
/// (<see cref="HostBridge.StatedValue.Rumble"/>) and a commit calls it on the guest's thread
/// (pump action <c>Rumble:n</c>).
/// </summary>
/// <remarks>
/// The switch is not in the profile or the save, so the guest forgets it between runs — the page
/// is per session, and the launcher's <c>controller_vibration</c> is the persistent switch. Both
/// gate: the guest emits only while its switch is set, the host applies only while the launcher
/// setting is on.
/// </remarks>
public sealed class Engine_UiVibrationOptions : Engine_UiHostOptionsPage
{
	public const string PageName = "ControllerVibration";
	public const string OpenCommand = "options:vibration";
	public const string PageTitle = "Controller Vibration";

	public static readonly Engine_UiVibrationOptions Page = new();

	public override string Name => PageName;

	public override string Title => PageTitle;

	public override int Count => 1;

	public override string RowTitle(int row) => "Controller vibration";

	public override int Choices(int row) => 2;

	public override string Caption(int row, int index) => index != 0 ? "On" : "Off";

	/// <summary>The switch as the guest's setter last stated it; the game's default (on) until it runs.</summary>
	public static bool GuestEnabled()
	{
		uint stated = HostBridge.Stated(HostBridge.StatedValue.Rumble);
		return (stated & 2u) == 0u || (stated & 1u) != 0u;
	}

	public override int[] Read() => new[] { GuestEnabled() ? 1 : 0 };

	/// <summary>A on the page calls the guest's setter.</summary>
	public override bool Commit(System.Func<int, int> index)
	{
		HostBridge.PostFrontEndAction(index(0) != 0 ? "Rumble:1" : "Rumble:0");
		return true;
	}
}
