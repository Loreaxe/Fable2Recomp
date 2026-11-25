using System.Collections.Generic;

namespace Refii.Titles.Fable2;

/// <summary>
/// THE GUEST'S DISCARD GATE, per abilities row, as the guest states it (payload_bridge.h key 20).
/// The page fill's edge (`sub_826C2F98`'s call at TU1 0x826C3870 / disc 0x8259F810, run by the Update
/// stub on a new highlight) gates the row with `sub_826C3A30` / 0x8259F9D0 — the hero's component 33
/// byte +46 (`SetAbilityDiscardingAsEnabled`) and the level held above 0 — and states the row's record
/// id with bit 0 replaced by the answer. Open: Discard drawn with its level and refund at full opacity
/// and Y asks; shut: the plain Discard at 50 (0x826C3980..0x826C39D0) and Y does nothing (the Y arms
/// fall through, 0x826C2DC0 / 0x826C2EF0).
/// </summary>
public static class Engine_AbilityDiscardGates
{
	private static readonly Dictionary<uint, bool> Answers = new();

	/// <summary>A row's id as the guest reads it: `(IdHigh &lt;&lt; 16) | (IdLow &amp; 0xFFFF)`.</summary>
	public static uint RowId(int idHigh, int idLow) => ((uint)idHigh << 16) | ((uint)idLow & 0xFFFFu);

	/// <summary>Decodes one statement: the row it names (bit 0 cleared) and its answer; null for none.</summary>
	public static (uint Row, bool Open)? Decode(uint stated) =>
		stated == 0u ? null : (stated & ~1u, (stated & 1u) != 0u);

	/// <summary>Takes the guest's latest statement in.</summary>
	public static void Observe(uint stated)
	{
		if (Decode(stated) is { } said)
		{
			Answers[said.Row] = said.Open;
		}
	}

	/// <summary>The gate's last stated answer for <paramref name="rowId"/>; shut until the guest has said.</summary>
	public static bool IsOpen(uint rowId) => Answers.TryGetValue(rowId & ~1u, out bool open) && open;

	/// <summary>Reads the bridge's statement and answers for <paramref name="rowId"/>.</summary>
	public static bool IsOpenNow(uint rowId)
	{
		Observe(Refii.Host.HostBridge.Stated(Refii.Host.HostBridge.StatedValue.AbilityDiscardGate));
		return IsOpen(rowId);
	}

	/// <summary>Forgets every answer (tests).</summary>
	public static void Clear() => Answers.Clear();
}
