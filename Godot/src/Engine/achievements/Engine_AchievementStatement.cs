using System;

namespace Refii.Achievements;

/// <summary>
/// A PLAYER'S ACHIEVEMENTS ON THE CO-OP CHANNEL: the machine that plays a player's hero writes this
/// statement into that player's store as a session text (native core/achievement_statement.h, the same
/// form), so it is the same on both co-op machines. Both mod surfaces answer from it — Lua's
/// <c>Refii.achievement_unlocked</c> / <c>achievements</c> / <c>character_id</c> natively, Godot's
/// <see cref="Refii.Mods.RefiiHost"/> twins here — and the page reads the online partner's sets from it
/// when their RexNet presence does not reach this node.
/// </summary>
/// <remarks>
/// Form: <c>&lt;character&gt;|&lt;wire hex&gt;</c>, the wire being <see cref="Engine_AchievementWire"/>'s bytes (at
/// most 66), the character the id its saves carry (32 hex). An empty text: no hero played. At most 256
/// bytes (the store's text limit). Pure: no Godot type.
/// </remarks>
public static class Engine_AchievementStatement
{
	public const string Key = "refii.achievements";

	/// <summary>One player's statement: their hero's character id and their two sets.</summary>
	public sealed record Statement(string Character, Engine_AchievementWire.View View);

	/// <summary>The text for a hero of <paramref name="character"/> with <paramref name="wire"/>; "" for no hero (null).</summary>
	public static string Format(string? character, byte[] wire) =>
		character is null ? string.Empty : character + "|" + Convert.ToHexString(wire).ToLowerInvariant();

	/// <summary>The statement a store text holds, or null for one that is not.</summary>
	public static Statement? Parse(string? text)
	{
		int bar = text?.IndexOf('|') ?? -1;
		if (text is null || bar < 0)
		{
			return null;
		}

		byte[] wire;
		try
		{
			wire = Convert.FromHexString(text.AsSpan(bar + 1));
		}
		catch (FormatException)
		{
			return null;
		}

		return Engine_AchievementWire.Decode(wire) is { } view ? new Statement(text[..bar], view) : null;
	}
}
