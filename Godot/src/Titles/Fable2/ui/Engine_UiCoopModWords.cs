using System.Collections.Generic;
using Refii.Text;

namespace Refii.Titles.Fable2;

/// <summary>
/// What the co-op mod sync says to the player, in our own words (<see cref="Engine_OwnWords.Fable2"/>): the
/// refusals written on the multiplayer row's page and the notices told while a session takes the host's
/// mods on (<c>GameUi</c>'s mod sync says them).
/// </summary>
/// <remarks>
/// PURE. The technical detail a refusal carries (how the two sets differ, what blocks the plan, why the
/// transfer failed, what the joiner stated) is composed elsewhere, in English, and is passed through as
/// given; only the sentence around it is translated.
/// </remarks>
public static class Engine_UiCoopModWords
{
	/// <summary>Every key the mod sync says, each stated in <see cref="Engine_OwnWords.Fable2"/>.</summary>
	public static readonly IReadOnlyList<string> Keys = new[]
	{
		"REFII_F2_COOP_ROLE_HOST", "REFII_F2_COOP_ROLE_JOINER", "REFII_F2_COOP_REFUSED_UNSTATED",
		"REFII_F2_COOP_REFUSED_OLD_JOINER", "REFII_F2_COOP_REFUSED_JOINER_DECLINED",
		"REFII_F2_COOP_REFUSED_JOINER_CANNOT", "REFII_F2_COOP_REFUSED_JOINER_SLOW",
		"REFII_F2_COOP_REFUSED_HOST_BLOCKED", "REFII_F2_COOP_REFUSED_YOU_DECLINED",
		"REFII_F2_COOP_REFUSED_SYNC_FAILED", "REFII_F2_COOP_MODS_PARTNER_RUNNING",
		"REFII_F2_COOP_MODS_PARTNER_RECEIVING", "REFII_F2_COOP_MODS_SUSPENDED", "REFII_F2_COOP_MODS_RUNNING_OF",
		"REFII_F2_COOP_MODS_RUNNING_HOST",
	};

	/// <summary>The key of a role's word: the host, or the player who joined.</summary>
	public static string RoleKey(bool host) => host ? "REFII_F2_COOP_ROLE_HOST" : "REFII_F2_COOP_ROLE_JOINER";

	/// <summary>The other player's build never stated its mods; <paramref name="otherIsHost"/> says which player that is.</summary>
	public static string Unstated(bool otherIsHost) =>
		Engine_OwnWords.Say("REFII_F2_COOP_REFUSED_UNSTATED", Engine_OwnWords.Say(RoleKey(otherIsHost)));

	/// <summary>Host: the sets differ and the joiner's build states no mod sync. <paramref name="difference"/> as given.</summary>
	public static string OldJoiner(string difference) =>
		Engine_OwnWords.Say("REFII_F2_COOP_REFUSED_OLD_JOINER", Capitalised(difference));

	/// <summary>Host: the joiner's player declined this game's mods.</summary>
	public static string JoinerDeclined() => Engine_OwnWords.Say("REFII_F2_COOP_REFUSED_JOINER_DECLINED");

	/// <summary>Host: the joiner's sync is blocked or failed. <paramref name="detail"/> is the joiner's stated reason, as given.</summary>
	public static string JoinerCannot(string detail) =>
		Engine_OwnWords.Say("REFII_F2_COOP_REFUSED_JOINER_CANNOT", Capitalised(detail));

	/// <summary>Host: the joiner's sync outlasted the wait, said in whole minutes.</summary>
	public static string JoinerSlow(double waitSeconds) =>
		Engine_OwnWords.Say("REFII_F2_COOP_REFUSED_JOINER_SLOW",
			(waitSeconds / 60.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture));

	/// <summary>Joiner: the host's set cannot be planned. <paramref name="blocked"/> is the plan's reason, as given.</summary>
	public static string HostBlocked(string blocked) =>
		Engine_OwnWords.Say("REFII_F2_COOP_REFUSED_HOST_BLOCKED", Capitalised(blocked));

	/// <summary>Joiner: this player declined the host's mods.</summary>
	public static string YouDeclined() => Engine_OwnWords.Say("REFII_F2_COOP_REFUSED_YOU_DECLINED");

	/// <summary>Joiner: the transfer or the mount failed. <paramref name="why"/> as given.</summary>
	public static string SyncFailed(string why) =>
		Engine_OwnWords.Say("REFII_F2_COOP_REFUSED_SYNC_FAILED", Capitalised(why));

	/// <summary>Host: the joiner now runs this game's mods.</summary>
	public static string PartnerRunning() => Engine_OwnWords.Say("REFII_F2_COOP_MODS_PARTNER_RUNNING");

	/// <summary>Host: the joiner is downloading this game's mods.</summary>
	public static string PartnerReceiving() => Engine_OwnWords.Say("REFII_F2_COOP_MODS_PARTNER_RECEIVING");

	/// <summary>
	/// Joiner: the session set is mounted. An empty host set only suspends this player's own mods; otherwise
	/// the host's mods run, named by the partner's name when the node knows it.
	/// </summary>
	public static string SessionMounted(int hostMods, string? partnerName) =>
		hostMods == 0
			? Engine_OwnWords.Say("REFII_F2_COOP_MODS_SUSPENDED")
			: string.IsNullOrEmpty(partnerName)
				? Engine_OwnWords.Say("REFII_F2_COOP_MODS_RUNNING_HOST")
				: Engine_OwnWords.Say("REFII_F2_COOP_MODS_RUNNING_OF", partnerName);

	/// <summary>A detail sentence with its first letter raised, as the page writes it.</summary>
	public static string Capitalised(string? text) =>
		string.IsNullOrEmpty(text) ? string.Empty : char.ToUpperInvariant(text[0]) + text[1..];
}
