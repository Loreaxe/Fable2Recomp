using System.Runtime.InteropServices;

namespace Refii.Host;

public static partial class HostBridge
{
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern uint RefiiHostGetHeroSaveChoice(byte[] output, uint capacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int RefiiHostAnswerHeroSaveChoice(uint id, int choice);

	/// <summary>
	/// The save choice a joining player's hero waits on (the guest's join holds until it is answered; the
	/// record <see cref="Refii.Save.Engine_HeroSaveChoice.Parse"/> reads), or null when none waits. Reading
	/// it takes the question: the guest then waits for <see cref="AnswerHeroSaveChoice"/>.
	/// </summary>
	public static string? GetHeroSaveChoice()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		// The length first (a null buffer): asked every frame, and nearly always nothing waits.
		uint length = RefiiHostGetHeroSaveChoice(null!, 0u);
		if (length == 0u)
		{
			return null;
		}

		var buffer = new byte[length + 1u];
		return RefiiHostGetHeroSaveChoice(buffer, (uint)buffer.Length) != 0 ? Decode(buffer) : null;
	}

	/// <summary>Answers save choice <paramref name="id"/>: a save's index, or -1 for a new hero.</summary>
	public static bool AnswerHeroSaveChoice(uint id, int choice) =>
		TryGetAbiVersion() is not null && RefiiHostAnswerHeroSaveChoice(id, choice) == 1;
}
