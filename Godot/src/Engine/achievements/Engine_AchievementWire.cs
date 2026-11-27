using System;
using System.Collections.Generic;
using System.Linq;

namespace Refii.Achievements;

/// <summary>
/// A PLAYER'S ACHIEVEMENTS AS THEIR MACHINE STATES THEM to whoever views their orb or plays with them
/// (RexNet presence, the rich KV <c>kRichKeyPlayerAchievements</c>): their hero's unlocked ids and their
/// profile total's. Read-only on the viewer's side; nothing is stored there.
/// </summary>
/// <remarks>
/// Wire form: a version byte (1), a bitmap length <c>L</c> (1..32), then the hero's bitmap and the
/// profile's, <c>L</c> bytes each; bit <c>id</c> (byte <c>id / 8</c>, bit <c>id % 8</c>) is achievement
/// <c>id</c>. Ids 1..255 travel; at most 66 bytes. Pure: no Godot type.
/// </remarks>
public static class Engine_AchievementWire
{
	public const byte Version = 1;
	public const uint MaxId = 255;

	/// <summary>One player's two sets.</summary>
	public sealed record View(IReadOnlySet<uint> Hero, IReadOnlySet<uint> Profile);

	public static byte[] Encode(IEnumerable<uint> hero, IEnumerable<uint> profile)
	{
		uint[] heroIds = hero.Where(Travels).Distinct().ToArray();
		uint[] profileIds = profile.Where(Travels).Distinct().ToArray();
		uint highest = heroIds.Concat(profileIds).DefaultIfEmpty(0u).Max();
		int length = (int)(highest / 8) + 1;
		var bytes = new byte[2 + (2 * length)];
		bytes[0] = Version;
		bytes[1] = (byte)length;
		foreach (uint id in heroIds)
		{
			bytes[2 + (id / 8)] |= (byte)(1 << (int)(id % 8));
		}

		foreach (uint id in profileIds)
		{
			bytes[2 + length + (id / 8)] |= (byte)(1 << (int)(id % 8));
		}

		return bytes;
	}

	/// <summary>The two sets, or null for bytes that are not this form.</summary>
	public static View? Decode(byte[]? bytes)
	{
		if (bytes is not { Length: >= 2 } || bytes[0] != Version || bytes[1] is 0 or > 32 ||
			bytes.Length != 2 + (2 * bytes[1]))
		{
			return null;
		}

		int length = bytes[1];
		return new View(Read(bytes, 2, length), Read(bytes, 2 + length, length));
	}

	private static bool Travels(uint id) => id is >= 1 and <= MaxId;

	private static HashSet<uint> Read(byte[] bytes, int at, int length)
	{
		var ids = new HashSet<uint>();
		for (int i = 0; i < length; ++i)
		{
			for (int bit = 0; bit < 8; ++bit)
			{
				if ((bytes[at + i] & (1 << bit)) != 0 && (i * 8) + bit != 0)
				{
					ids.Add((uint)((i * 8) + bit));
				}
			}
		}

		return ids;
	}
}
