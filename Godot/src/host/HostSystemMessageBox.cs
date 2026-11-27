using System.Runtime.InteropServices;

namespace Refii.Host;

public static partial class HostBridge
{
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern uint RefiiHostGetSystemMessageBox(byte[] output, uint capacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int RefiiHostAnswerSystemMessageBox(uint id, int button);

	/// <summary>
	/// The oldest system message box the guest waits on (its XamShowMessageBoxUI call, the record
	/// <see cref="Refii.Ui.Engine_UiSystemBoxes.Parse"/> reads), or null when none waits.
	/// </summary>
	public static string? GetSystemMessageBox()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		// The length first (a null buffer): asked every frame, and nearly always nothing waits.
		uint length = RefiiHostGetSystemMessageBox(null!, 0u);
		if (length == 0u)
		{
			return null;
		}

		var buffer = new byte[length + 1u];
		return RefiiHostGetSystemMessageBox(buffer, (uint)buffer.Length) != 0 ? Decode(buffer) : null;
	}

	/// <summary>Answers system message box <paramref name="id"/> with a button's index, or -1 to cancel it.</summary>
	public static bool AnswerSystemMessageBox(uint id, int button) =>
		TryGetAbiVersion() is not null && RefiiHostAnswerSystemMessageBox(id, button) == 1;
}
