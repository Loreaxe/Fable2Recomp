using System;
using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The ROW CONDITIONS an entry's <c>"when"</c> names (<see cref="Engine_UiMenuEntry.When"/>): a
/// name, <c>!name</c>, or several joined by <c>,</c> that must all hold. A name is asked first of
/// the facts the list's builder states (the pause list's <see cref="PlayerTwo"/>), then of the
/// conditions the game registers (<see cref="Register"/>). A name nobody states does not hold, so a
/// misspelt condition hides its row rather than showing it everywhere.
/// </summary>
public static class Engine_UiRowConditions
{
	/// <summary>The pause list is player two's (an online joiner's, or local player two's own book).</summary>
	public const string PlayerTwo = "player-two";

	/// <summary>
	/// Player two brought a hero (a character from a save) rather than standing as the plain henchman
	/// — the one statement every hero-only feature keys on (the payload's refii_bridge_two_is_hero).
	/// The henchman's book is the 360's: Abilities, Options and their way out.
	/// </summary>
	public const string PlayerTwoHero = "player-two-hero";

	/// <summary>A co-op partner stands (online or local).</summary>
	public const string Coop = "coop";

	/// <summary>An online partner stands (not local co-op).</summary>
	public const string CoopOnline = "coop.online";

	/// <summary>This machine joined someone else's game.</summary>
	public const string CoopClient = "coop.client";

	/// <summary>A game is running (not the title).</summary>
	public const string InGame = "in-game";

	/// <summary>The menu's hero carries gold — the gift list's Gold row, spawned only then.</summary>
	public const string HasGold = "has-gold";

	/// <summary>Screen 28 has resolved a building record.</summary>
	public const string EstateKnown = "estate.known";

	/// <summary>Screen 28's resolved building is owned by the player.</summary>
	public const string EstateOwned = "estate.owned";

	/// <summary>Screen 28's resolved building currently has no tenant.</summary>
	public const string EstateVacant = "estate.vacant";

	/// <summary>Screen 28's resolved building currently has a tenant.</summary>
	public const string EstateTenanted = "estate.tenanted";

	/// <summary>Screen 28's resolved building is a business (not a home or unique building).</summary>
	public const string EstateBusiness = "estate.business";

	/// <summary>Screen 28's resolved building is a home or unique building (not a business).</summary>
	public const string EstateHome = "estate.home";

	/// <summary>The facts a list's builder states, for the validator and the Studio.</summary>
	public static readonly string[] BuilderFacts = { PlayerTwo };

	/// <summary>Every name the game states — the builders' facts and what the in-game UI registers.</summary>
	public static readonly string[] Game = { PlayerTwo, PlayerTwoHero, Coop, CoopOnline, CoopClient, InGame, HasGold };

	private static readonly Dictionary<string, Func<bool>> Registered = new(StringComparer.Ordinal);

	/// <summary>Registers (or replaces) a condition every list can name.</summary>
	public static void Register(string name, Func<bool> holds)
	{
		lock (Registered)
		{
			Registered[name] = holds;
		}
	}

	/// <summary>Every name a row can state: the builders' facts and the registered conditions.</summary>
	public static IReadOnlyList<string> Names
	{
		get
		{
			var names = new List<string>(BuilderFacts);
			lock (Registered)
			{
				foreach (string name in Registered.Keys)
				{
					if (!names.Contains(name))
					{
						names.Add(name);
					}
				}
			}

			return names;
		}
	}

	/// <summary>The names <paramref name="when"/> states, without their <c>!</c>; for the validator.</summary>
	public static IEnumerable<string> NamesIn(string when)
	{
		foreach (string part in when.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			string name = part.TrimStart('!').Trim();
			if (name.Length != 0)
			{
				yield return name;
			}
		}
	}

	/// <summary>Whether a row stating <paramref name="when"/> is built.</summary>
	public static bool Holds(string when, IReadOnlyDictionary<string, bool>? facts = null)
	{
		if (string.IsNullOrWhiteSpace(when))
		{
			return true;
		}

		foreach (string part in when.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			bool negated = part.StartsWith('!');
			string name = negated ? part[1..].Trim() : part;
			if (Fact(name, facts) == negated)
			{
				return false;
			}
		}

		return true;
	}

	private static bool Fact(string name, IReadOnlyDictionary<string, bool>? facts)
	{
		if (facts is not null && facts.TryGetValue(name, out bool stated))
		{
			return stated;
		}

		Func<bool>? holds;
		lock (Registered)
		{
			Registered.TryGetValue(name, out holds);
		}

		return holds is not null && holds();
	}

	/// <summary>The one fact the pause list states.</summary>
	public static IReadOnlyDictionary<string, bool> PauseFacts(bool playerTwo) =>
		new Dictionary<string, bool>(StringComparer.Ordinal) { [PlayerTwo] = playerTwo };
}
