using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.Save;

/// <summary>
/// THE SAVE A JOINING PLAYER'S HERO COMES FROM, as that player chooses it. The guest's drop-in join holds
/// while it asks (native achievement/hero_save_choice.h): the saves of the shared profile its own qualify
/// step passes, player one's excluded, in the order its walk takes them — the first is the one the guest
/// would have adopted — and a new hero, its own no-save path. The answer goes back as the save's index
/// or <see cref="NewHero"/>; the guest then adopts that save itself. Pure, so the tests run it.
/// </summary>
public static class Engine_HeroSaveChoice
{
	/// <summary>The answer for the new-hero row.</summary>
	public const int NewHero = -1;

	/// <summary>
	/// The answer for B: the joining player does not join after all — the guest's own cancel of its join (its
	/// sign-in warning's Cancel), so no hero is made and no save is adopted; START joins again.
	/// </summary>
	public const int Cancel = -3;

	/// <summary>
	/// The most saves a profile holds before the guest's new hero gets no save of its own (Fable 3
	/// sub_822F85A8: `bl sub_822F5BD0` / `cmpwi r3,5` / `bge` 0x822F85F8..0x822F8600).
	/// </summary>
	public const int SaveSlots = 5;

	/// <summary>One question: its id, the joining user (the guest pad they play on), how many saves the profile holds, the saves to choose from.</summary>
	public sealed record Question(uint Id, int User, int Listed, IReadOnlyList<string> Saves);

	/// <summary>One row of the choice: the save it names (null for the new hero), its label and its line's value.</summary>
	public readonly record struct Row(string? Save, string Label, string Value, string Description);

	/// <summary>
	/// A save's own facts the label is made from: its hero's name (the sidecar's character), when it was last
	/// written, and the display name its own header carries (<see cref="Engine_SaveHeader"/>, "" for none).
	/// </summary>
	public readonly record struct SaveFacts(string HeroName, DateTime? Written, string DisplayName = "");

	/// <summary>
	/// The record RefiiHostGetHeroSaveChoice gives: id, user, listed, count, then each save's name, split by
	/// 0x1F. Null for a record that is not one.
	/// </summary>
	public static Question? Parse(string? record)
	{
		if (string.IsNullOrEmpty(record))
		{
			return null;
		}

		string[] fields = record.Split('\x1F');
		if (fields.Length < 4 ||
			!uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ||
			!int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int user) ||
			!int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int listed) ||
			!int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
			count <= 0 || fields.Length != 4 + count)
		{
			return null;
		}

		var saves = new List<string>(count);
		for (int i = 0; i < count; ++i)
		{
			if (fields[4 + i].Length == 0)
			{
				return null;
			}

			saves.Add(fields[4 + i]);
		}

		return new Question(id, user, listed, saves);
	}

	/// <summary>Whether the guest's new hero gets a save of its own: the profile has a free slot.</summary>
	public static bool NewHeroIsSaved(Question question) => question.Listed < SaveSlots;

	/// <summary>
	/// The rows: each save in the guest's order — its hero's name, else the name its own header gives it, else
	/// the package's; the date it was last written; its header's name and the package as its description —
	/// then the new hero. <paramref name="facts"/> answers a save's own facts; <paramref name="say"/>
	/// our words.
	/// </summary>
	public static List<Row> Rows(Question question, Func<string, SaveFacts> facts, Func<string, string> say)
	{
		var rows = new List<Row>(question.Saves.Count + 1);
		foreach (string save in question.Saves)
		{
			SaveFacts known = facts(save);
			string label = known.HeroName.Length > 0 ? known.HeroName : known.DisplayName.Length > 0 ? known.DisplayName : save;
			string value = known.Written is { } when ? when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : string.Empty;
			string description = known.DisplayName.Length > 0 && known.DisplayName != label ? known.DisplayName + " (" + save + ")" : save;
			rows.Add(new Row(save, label, value, description));
		}

		rows.Add(new Row(null, say("REFII_NEW_HERO"), string.Empty,
			say(NewHeroIsSaved(question) ? "REFII_NEW_HERO_SAVED" : "REFII_NEW_HERO_UNSAVED")));
		return rows;
	}

	/// <summary>The answer for 0-based <paramref name="row"/> of <see cref="Rows"/>: the save's index, <see cref="NewHero"/>, or null for no row.</summary>
	public static int? Answer(Question question, int row) =>
		row >= 0 && row < question.Saves.Count ? row : row == question.Saves.Count ? NewHero : null;
}
