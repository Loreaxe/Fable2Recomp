using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Core;

/// <summary>
/// A PAD BINDING OF A KEY ACTION: the <c>Pad:</c> tokens of a <c>key_&lt;action&gt;</c> setting, beside
/// its key and mouse names. Each token is one chord of the GAME's buttons
/// (<see cref="PadInput"/> names joined by <c>+</c>): <c>Pad:R3</c>, <c>Pad:Back+Y</c>. A chord is
/// held while every input of it is down on one player's pad, through that player's remap; an
/// action with several tokens is held while any chord is.
/// </summary>
/// <remarks>
/// <para>The setting's other tokens (<see cref="KeysOf"/>) keep their meaning unchanged: empty is
/// the action's default keys, <c>none</c> unbinds them. So <c>Pad:Back+Y</c> alone is the default
/// keys with the chord; <c>none,Pad:R3</c> is the chord and no key.</para>
/// <para>Only an action that declares itself pad-bindable reads its tokens
/// (<c>GameInput.KeyAction.PadBindable</c>); the default is no token: unbound.</para>
/// <para>No Godot types: what the tests cover.</para>
/// </remarks>
public static class Engine_PadActionBinding
{
	public const string Prefix = "Pad:";

	private const char ChordJoin = '+';

	/// <summary>Whether one comma-separated part of a setting is a pad token.</summary>
	public static bool IsPadToken(string part) =>
		part.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

	/// <summary>Whether a stored setting holds any pad token.</summary>
	public static bool HasPadToken(string? stored) =>
		(stored ?? string.Empty).IndexOf(Prefix, StringComparison.OrdinalIgnoreCase) >= 0;

	private static string Unquoted(string? stored) => (stored ?? string.Empty).Trim().Trim('"').Trim();

	/// <summary>
	/// A stored setting without its pad tokens: the key and mouse names, as stored. A setting that
	/// holds no pad token comes back as it is (trimmed and unquoted).
	/// </summary>
	public static string KeysOf(string? stored)
	{
		string text = Unquoted(stored);
		if (!HasPadToken(text))
		{
			return text;
		}

		var keys = new StringBuilder();
		foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (!IsPadToken(part))
			{
				keys.Append(keys.Length == 0 ? string.Empty : ",").Append(part);
			}
		}

		return keys.ToString();
	}

	/// <summary>The chords a stored setting names, each a mask over <see cref="PadInput"/>; tokens naming nothing known are dropped.</summary>
	public static IReadOnlyList<ushort> Chords(string? stored)
	{
		var chords = new List<ushort>();
		foreach (string part in Unquoted(stored).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (IsPadToken(part) && TryParseChord(part[Prefix.Length..], out ushort chord) && !chords.Contains(chord))
			{
				chords.Add(chord);
			}
		}

		return chords;
	}

	/// <summary>One chord's mask from its names (<c>Back+Y</c>); false when any name is unknown or none is given.</summary>
	public static bool TryParseChord(string text, out ushort chord)
	{
		chord = 0;
		foreach (string name in text.Split(ChordJoin, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (!Engine_PadRemap.TryParse(name, out PadInput input))
			{
				chord = 0;
				return false;
			}

			chord |= (ushort)(1 << (int)input);
		}

		return chord != 0;
	}

	/// <summary>The inputs of a chord, in <see cref="PadInput"/> order.</summary>
	public static IReadOnlyList<PadInput> Inputs(ushort chord)
	{
		var inputs = new List<PadInput>();
		for (int i = 0; i < Engine_PadRemap.Count; i++)
		{
			if ((chord & (1 << i)) != 0)
			{
				inputs.Add((PadInput)i);
			}
		}

		return inputs;
	}

	/// <summary>A chord as the settings spell it, without the prefix: <c>Y+Back</c>.</summary>
	public static string ChordName(ushort chord)
	{
		var text = new StringBuilder();
		foreach (PadInput input in Inputs(chord))
		{
			text.Append(text.Length == 0 ? string.Empty : ChordJoin.ToString()).Append(Engine_PadRemap.Name(input));
		}

		return text.ToString();
	}

	/// <summary>The pad tokens for <paramref name="chords"/>, comma-joined; empty for none.</summary>
	public static string Format(IReadOnlyList<ushort> chords)
	{
		var text = new StringBuilder();
		foreach (ushort chord in chords)
		{
			if (chord != 0)
			{
				text.Append(text.Length == 0 ? string.Empty : ",").Append(Prefix).Append(ChordName(chord));
			}
		}

		return text.ToString();
	}

	/// <summary>
	/// The setting's stored value from its two halves: <paramref name="keys"/> as a key row stores
	/// it (null for the default keys) and <paramref name="pad"/> as <see cref="Format"/> gives it.
	/// Null — remove the setting — when both are the default.
	/// </summary>
	public static string? Combine(string? keys, string pad)
	{
		string k = (keys ?? string.Empty).Trim();
		if (pad.Length == 0)
		{
			return k.Length == 0 ? null : k;
		}

		return k.Length == 0 ? pad : k + "," + pad;
	}

	/// <summary>The inputs down on a pad, as a mask over <see cref="PadInput"/>: its game buttons (XInput bits) and its triggers of 255.</summary>
	public static ushort Held(uint buttons, uint leftTrigger, uint rightTrigger)
	{
		ushort held = 0;
		for (int i = 0; i < Engine_PadRemap.Count; i++)
		{
			var input = (PadInput)i;
			bool down = input switch
			{
				PadInput.LT => leftTrigger > Engine_PadRemap.TriggerPressed,
				PadInput.RT => rightTrigger > Engine_PadRemap.TriggerPressed,
				_ => (buttons & Engine_PadRemap.Bit(input)) != 0,
			};
			if (down)
			{
				held |= (ushort)(1 << i);
			}
		}

		return held;
	}

	/// <summary>The inputs of every chord of <paramref name="chords"/> wholly within <paramref name="held"/>; 0 when none is held.</summary>
	public static ushort Active(IReadOnlyList<ushort> chords, ushort held)
	{
		ushort active = 0;
		foreach (ushort chord in chords)
		{
			if (chord != 0 && (held & chord) == chord)
			{
				active |= chord;
			}
		}

		return active;
	}

	/// <summary>Takes the inputs of <paramref name="mask"/> out of a pad: their bits cleared, their triggers at rest.</summary>
	public static void Withhold(ushort mask, ref uint buttons, ref uint leftTrigger, ref uint rightTrigger)
	{
		for (int i = 0; i < Engine_PadRemap.Count; i++)
		{
			if ((mask & (1 << i)) == 0)
			{
				continue;
			}

			switch ((PadInput)i)
			{
				case PadInput.LT: leftTrigger = 0; break;
				case PadInput.RT: rightTrigger = 0; break;
				default: buttons &= ~Engine_PadRemap.Bit((PadInput)i); break;
			}
		}
	}
}
