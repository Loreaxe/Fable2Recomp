namespace Refii.Titles.Fable3;

using System.Collections.Generic;

/// <summary>
/// Who targets each thing, as the guest's targeted setter leaves it: `@Target.By(thing, players,
/// interact)` (Fable3/src/hooks/src/target_statements.cpp; TU3 sub_826F6298 / disc sub_826C8FA8,
/// player one's byte +0x21, player two's +0x22, the "Interact" icon's gate +0x28).
/// </summary>
/// <remarks>
/// The guest raises ONE highlight and ONE "Interact" icon per target, for whichever player targets
/// it. Split, each half keeps its own player's: the highlight through
/// <see cref="Refii.Core.Engine_TitleGame.TargetedBy"/>, the icon through <see cref="InteractBy"/>
/// (Fable3WorldIcons). A thing no player targets is dropped; one left behind by a destroyed entity is
/// harmless, because both the highlight and the icon are raised only through the setter, which
/// restates a reused address before either shows.
/// </remarks>
public static class Fable3Targeting
{
	/// <summary>Rows kept before the table starts over: well past what two players can target at once.</summary>
	private const int MaxRows = 256;

	private static readonly Dictionary<uint, (int Players, bool Interact)> Rows = new();

	/// <summary>`@Target.By(thing, players, interact)`.</summary>
	public static void State(uint thing, int players, bool interact)
	{
		if (thing == 0u)
		{
			return;
		}

		players &= 3;
		if (players == 0)
		{
			Rows.Remove(thing);
			return;
		}

		if (Rows.Count >= MaxRows && !Rows.ContainsKey(thing))
		{
			Rows.Clear();
		}

		Rows[thing] = (players, interact);
	}

	/// <summary>The players targeting <paramref name="thing"/>: bit 0 player one, bit 1 player two; 0 none.</summary>
	public static int TargetedBy(uint thing) =>
		thing != 0u && Rows.TryGetValue(thing, out (int Players, bool Interact) row) ? row.Players : 0;

	/// <summary>
	/// The players whose targeting raises <paramref name="thing"/>'s "Interact" icon: the targeting
	/// players when its targeted component shows the icon (+0x28), else 0 (its pooled prompts are
	/// not that icon's).
	/// </summary>
	public static int InteractBy(uint thing) =>
		thing != 0u && Rows.TryGetValue(thing, out (int Players, bool Interact) row) && row.Interact ? row.Players : 0;

	/// <summary>Forgets every row (a new session; the tests).</summary>
	public static void Clear() => Rows.Clear();
}
