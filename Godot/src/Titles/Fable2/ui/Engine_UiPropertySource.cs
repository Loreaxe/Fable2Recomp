using System.Collections.Generic;
using System.Globalization;
using Refii.Host;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE WORLD MAP'S PROPERTIES: the properties the player whose menus are open OWNS — as the guest's own
/// calls state each hero's list (the purchases, the loader's per-record read, the sale's removal and the
/// per-property setters; hooks/src/property_list_hooks.cpp, `Fable2Bridge.GetProperties`) — each with its
/// condition, and Fable 3's repair from the map (its `REPAIR_RENT_REMINDER`: "You can repair houses
/// via the map"). Fable 2 has no condition; the native side keeps Fable 3's
/// (hooks/src/property_hooks.cpp, core/property_condition.h): rented homes lose 0.01 a rent drop,
/// pay nothing at 0, and a full repair costs half the value lost.
/// </summary>
/// <remarks>
/// FABLE 3'S PARTNERSHIPS head the list while both heroes are in the game: a proposal of marriage
/// or an offer of business partnership (the other player is asked, <see cref="AskPartner"/>),
/// divorce or the partnership's end (the asker's alone). Partners share what either buys from then
/// on — its rent and sale split half and half (core/hero_partners.cpp, property_hooks.cpp) — and
/// a shared property says so here. A proposal needs what Fable 3 asks (a home of either hero's to
/// marry in: `GUI_MARRIAGE_PROPOSAL_NEED_HOUSE`, `sub_824C4658`; the household tick publishes each
/// hero's home count as `refii.homes.<p>`) and, our Fable 2 touch, a wedding ring of the proposer's
/// (<see cref="Rings"/>), given when the partner accepts; the marital home is then theirs together.
///
/// EVERY ROW IS THE ONE MENU TEMPLATE'S PLAIN ROW: a name, its details on its page (<see cref="FillPage"/>).
/// A property is never ghosted — it is there to be read whether or not it needs repairing; A repairs one that
/// does and does nothing on one that does not (no action, no identity: the screen menu commits nothing). Only
/// Repair All with nothing to repair is ghosted, an action that is not available. The shared row view
/// draws a label too long for its slot smaller (Engine_FrontEndMenuView.SetLabel), so a row whose label
/// carried its condition and price drew smaller than its neighbours, and a ghosted one dark: the names alone
/// keep the column one size, and the page states the condition, the bill and what A does.
/// </remarks>
public static class Engine_UiPropertySource
{
	public const string SourceName = "properties";

	/// <summary>`PropertyRepair:&lt;uid hex&gt;` — a row's repair, confirmed first.</summary>
	public const string RepairAction = "PropertyRepair:";

	/// <summary>The co-op channel message an online joiner sends the host for its repair.</summary>
	public const string RepairMessage = "property.repair=";

	/// <summary>`PropertyRepairAll` — every building below full condition, cheapest first, while the gold lasts.</summary>
	public const string RepairAllAction = "PropertyRepairAll";

	/// <summary>The co-op channel message an online joiner sends the host for its Repair All.</summary>
	public const string RepairAllMessage = "property.repairall=";

	/// <summary>`Partner:&lt;propose|business|divorce|end&gt;` — a partnership row.</summary>
	public const string PartnerAction = "Partner:";

	/// <summary>Consent kinds for the partner's question (GameUiCoopConsent): a proposal, an offer.</summary>
	public const int ProposalKind = 3;
	public const int BusinessKind = 4;

	/// <summary>Fable 2's wedding rings (the gift rings villagers accept a proposal with), cheapest first.</summary>
	public static readonly string[] Rings =
	{
		"ObjectInventoryGiftRing", "ObjectInventoryGiftRing_1", "ObjectInventoryGiftRing_2",
		"ObjectInventoryGiftRing_3", "ObjectInventoryGiftRing_4", "ObjectInventoryGiftRing_5Star",
	};

	/// <summary>The player whose menus are open on this machine: the one proposing.</summary>
	public static int MenuPlayer() =>
		CoopSession.LocalCoop ? Fable2Bridge.GetMenuOwner() : (CoopSession.IsClient ? 2 : 1);

	/// <summary>The first wedding ring <paramref name="player"/>'s hero carries, or null.</summary>
	public static string? RingOf(int player)
	{
		foreach (string ring in Rings)
		{
			if (Fable2Bridge.GetItemCount(player, ring) > 0)
			{
				return ring;
			}
		}

		return null;
	}

	/// <summary>Whether either hero owns a home to marry in (the counts the host publishes).</summary>
	private static bool EitherOwnsAHome() =>
		(HostBridge.GetPlayerValue(1, "refii.homes.1") ?? 0f) > 0f ||
		(HostBridge.GetPlayerValue(1, "refii.homes.2") ?? 0f) > 0f;

	/// <summary>Asks the other player (a consent kind above); set by the game's UI.</summary>
	public static System.Action<int>? AskPartner { get; set; }

	private const string Icon = @"Art\GUI\WheelIcons\icon_logbook.tga";

	/// <summary>The sales page's title and body, which a Properties row's page writes into.</summary>
	private const string SaleTitle = "SalesPage.Contents.PageTitle";

	private const string SaleBody = "SalesPage.Contents.Description";

	/// <summary>The last list read, by UID, for the confirm box and the notices.</summary>
	private static readonly Dictionary<ulong, HostBridge.Property> _known = new();

	public static IReadOnlyList<Engine_UiMenuSources.Row> Rows(string argument)
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		if (Fable2Bridge.GetPartnership() is { } partnership)
		{
			// Each row's label is its page's title (FillPage).
			if (partnership < 2)
			{
				rows.Add(PartnerRow("propose", "Marriage"));
			}

			rows.Add(partnership switch
			{
				0 => PartnerRow("business", "Business Partnership"),
				1 => PartnerRow("end", "End Partnership"),
				_ => PartnerRow("divorce", "Divorce"),
			});
		}

		List<HostBridge.Property> properties = Fable2Bridge.GetProperties(0);
		int worn = 0;
		foreach (HostBridge.Property property in properties)
		{
			if (property.Condition < 1f && property.RepairCost > 0)
			{
				++worn;
			}
		}

		// REPAIR ALL, under the partnership rows: one confirmation and one bill for every building
		// that needs it.
		if (properties.Count != 0)
		{
			rows.Add(new Engine_UiMenuSources.Row(
				Name: RepairAllRow,
				Label: "Repair All",
				Icon: Icon,
				EventType: 0,
				Id: 0u,
				Rating: Engine_UiMenuConstants.EmptyRatingSlide,
				Committable: worn != 0,
				Ghosted: worn == 0,
				Command: worn != 0 ? RepairAllAction : null));
		}

		foreach (HostBridge.Property property in properties)
		{
			_known[property.Uid] = property;
			bool repairable = property.Condition < 1f && property.RepairCost > 0;
			rows.Add(new Engine_UiMenuSources.Row(
				Name: $"Property:{property.Uid:X16}",
				Label: Name(property),
				Icon: Icon,
				EventType: 0,
				Id: 0u,
				Rating: Engine_UiMenuConstants.EmptyRatingSlide,
				Committable: repairable,
				Ghosted: false,
				Command: repairable ? RepairAction + property.Uid.ToString("X16", CultureInfo.InvariantCulture) : null));
		}

		return rows;
	}

	private static Engine_UiMenuSources.Row PartnerRow(string verb, string label) => new(
		Name: PartnerAction + verb,
		Label: label,
		Icon: Icon,
		EventType: 0,
		Id: 0u,
		Rating: Engine_UiMenuConstants.EmptyRatingSlide,
		Committable: true,
		Ghosted: false,
		Command: PartnerAction + verb);

	/// <summary>A property's display name: its BuildingName tag through the string book.</summary>
	public static string Name(HostBridge.Property property) =>
		Engine_UiInventorySource.Strings is { } book && property.NameTag.Length != 0 &&
		book.TryGetString(property.NameTag, out string resolved) && resolved.Length != 0
			? resolved
			: property.NameTag.Length != 0 ? property.NameTag : "A property";

	/// <summary>The name of a property seen in the last list, for a notice.</summary>
	public static string NameOf(ulong uid)
	{
		if (!_known.ContainsKey(uid))
		{
			foreach (HostBridge.Property listed in Fable2Bridge.GetProperties(0))
			{
				_known[listed.Uid] = listed;
			}
		}

		return _known.TryGetValue(uid, out HostBridge.Property property) ? Name(property) : "one of your properties";
	}

	private static string Percent(float condition) =>
		((int)System.MathF.Round(condition * 100f)).ToString(CultureInfo.InvariantCulture) + "%";

	/// <summary>
	/// A row's repair: Fable 3's confirmation ("Do you want to fully repair this house? It will
	/// cost %1 gold."), then the repair for the player whose menus are open — on the host's world,
	/// which an online joiner reaches over the co-op channel.
	/// </summary>
	public static bool Run(string action)
	{
		if (action.StartsWith(PartnerAction, System.StringComparison.Ordinal))
		{
			RunPartner(action[PartnerAction.Length..]);
			return true;
		}

		if (action == RepairAllAction)
		{
			RunRepairAll();
			return true;
		}

		if (!action.StartsWith(RepairAction, System.StringComparison.Ordinal) ||
			!ulong.TryParse(action[RepairAction.Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong uid))
		{
			return false;
		}

		int cost = _known.TryGetValue(uid, out HostBridge.Property property) ? property.RepairCost : 0;
		string uidText = uid.ToString("X16", CultureInfo.InvariantCulture);
		Engine_UiHostBoxes.Ask(
			$"Do you want to fully repair {NameOf(uid)}? It will cost {cost.ToString(CultureInfo.InvariantCulture)} gold.",
			() =>
			{
				if (CoopSession.IsClient)
				{
					HostBridge.CoopSend(RepairMessage + uidText);
					return;
				}

				int player = CoopSession.LocalCoop && Fable2Bridge.GetMenuOwner() == 2 ? 2 : 1;
				HostBridge.PostFrontEndAction($"Property:repair:{player}:{uidText}");
			},
			null,
			"property:repair:" + uidText);
		return true;
	}

	/// <summary>The row name of Repair All, for its page.</summary>
	private const string RepairAllRow = "Property:all";

	/// <summary>The buildings a Repair All would mend, cheapest first, from the last list read.</summary>
	private static List<HostBridge.Property> Worn()
	{
		var worn = new List<HostBridge.Property>();
		foreach (HostBridge.Property property in _known.Values)
		{
			if (property.Condition < 1f && property.RepairCost > 0)
			{
				worn.Add(property);
			}
		}

		worn.Sort((a, b) => a.RepairCost.CompareTo(b.RepairCost));
		return worn;
	}

	/// <summary>Repair All: confirmed with the whole bill, then run for the player whose menus are open.</summary>
	private static void RunRepairAll()
	{
		List<HostBridge.Property> worn = Worn();
		int bill = 0;
		foreach (HostBridge.Property property in worn)
		{
			bill += property.RepairCost;
		}

		Engine_UiHostBoxes.Ask(
			$"Do you want to fully repair all {worn.Count.ToString(CultureInfo.InvariantCulture)} of your properties? " +
			$"It will cost {bill.ToString(CultureInfo.InvariantCulture)} gold. If you cannot pay it all, the cheapest are repaired first.",
			() =>
			{
				if (CoopSession.IsClient)
				{
					HostBridge.CoopSend(RepairAllMessage);
					return;
				}

				int player = CoopSession.LocalCoop && Fable2Bridge.GetMenuOwner() == 2 ? 2 : 1;
				HostBridge.PostFrontEndAction($"Property:repairall:{player}");
			},
			null,
			"property:repairall");
	}

	/// <summary>
	/// A partnership row: a proposal or an offer goes to the other player as a question; divorce and
	/// the partnership's end are confirmed and done.
	/// </summary>
	private static void RunPartner(string verb)
	{
		switch (verb)
		{
			case "propose":
				if (RingOf(MenuPlayer()) is null)
				{
					Told?.Invoke("You need a wedding ring to propose.");
					break;
				}

				if (!EitherOwnsAHome())
				{
					Told?.Invoke("You need a home to marry in. One of you must own a house.");
					break;
				}

				Engine_UiHostBoxes.Ask("Ask your partner to marry you? You will give them your ring if they accept.",
					() => AskPartner?.Invoke(ProposalKind), null, "partner:propose");
				break;
			case "business":
				Engine_UiHostBoxes.Ask(
					"Offer your partner a business partnership? You will share every property either of you buys from now on, and its rent.",
					() => AskPartner?.Invoke(BusinessKind), null, "partner:business");
				break;
			case "divorce":
			case "end":
				bool divorce = verb == "divorce";
				Engine_UiHostBoxes.Ask(
					divorce
						? "Divorce your partner? You will no longer share property."
						: "End your business partnership? You will no longer share property.",
					() =>
					{
						if (Fable2Bridge.SetPartnership(0))
						{
							Told?.Invoke(divorce ? "You are divorced." : "Your business partnership has ended.");
						}
					},
					null,
					"partner:" + verb);
				break;
		}
	}

	/// <summary>The property a row names, or 0.</summary>
	public static ulong UidOf(string? rowName) =>
		rowName is not null && rowName.StartsWith(RowPrefix, System.StringComparison.Ordinal) &&
		ulong.TryParse(rowName[RowPrefix.Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong uid)
			? uid
			: 0ul;

	private const string RowPrefix = "Property:";

	private const string Gold = "<gold_img>";

	/// <summary>
	/// THE WORLD MAP'S PAGE FOR A PROPERTIES ROW, on the map's Sales page (a title and one text block).
	/// A property shows its stated ownership record — its type, value, rent, bought-for price, marital or
	/// rented-out state — then Fable 3's condition, repair bill and the partnership it is shared under. No
	/// guest page states a building whose sign is not open, so nothing of screen 28's is sampled for it. A
	/// partnership row says what it does. False for any other row.
	/// </summary>
	public static bool FillPage(
		Dictionary<string, string> values, string? rowName, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (rowName is not null && rowName.StartsWith(PartnerAction, System.StringComparison.Ordinal))
		{
			(string title, string body) = rowName[PartnerAction.Length..] switch
			{
				"propose" => ("Marriage",
					"Marry your co-op partner. You need a wedding ring, which you give them when they accept, and a " +
					"home of either of yours to marry in.<br><br>Your marital home becomes yours together, and from " +
					"then on you share every property either of you buys: its rent and its sale are split half and half."),
				"business" => ("Business Partnership",
					"Go into business with your co-op partner. It costs nothing.<br><br>From then on you share every " +
					"property either of you buys: its rent and its sale are split half and half. A partner who is " +
					"away is paid their share when they return."),
				"divorce" => ("Divorce",
					"End your marriage to your co-op partner. You keep what you own, but no longer share what " +
					"either of you buys from then on."),
				"end" => ("End Partnership",
					"End your business partnership. You keep what you own, but no longer share what either of you " +
					"buys from then on."),
				_ => (string.Empty, string.Empty),
			};
			values[SaleTitle] = title;
			values[SaleBody] = body;
			return title.Length != 0;
		}

		if (rowName == RepairAllRow)
		{
			var listed = new List<string>();
			int total = 0;
			foreach (HostBridge.Property each in Worn())
			{
				total += each.RepairCost;
				listed.Add($"{Name(each)} <s1>{Percent(each.Condition)}</s1> — {Gold}{each.RepairCost.ToString(CultureInfo.InvariantCulture)}");
			}

			values[SaleTitle] = "Repair All";
			values[SaleBody] = listed.Count == 0 ? "Repair all — nothing needs repairing" :
				$"Total <s1>{Gold}{total.ToString(CultureInfo.InvariantCulture)}</s1> — press A to repair them all, cheapest first while your gold lasts.<br><br>" +
				string.Join("<br>", listed);
			return true;
		}

		ulong uid = UidOf(rowName);
		if (uid == 0ul)
		{
			return false;
		}

		if (!_known.TryGetValue(uid, out HostBridge.Property property))
		{
			return false;
		}

		var lines = new List<string>(12);
		string type = property.IsHome ? Babel(strings, "GUI_SCREEN_REAL_ESTATE_BUILDING_TYPE_HOME") : string.Empty;
		if (type.Length != 0)
		{
			lines.Add(type);
		}

		lines.Add($"Value <s1>{Gold}{property.Value.ToString(CultureInfo.InvariantCulture)}</s1>");
		if (property.IsRented || property.HasTenants)
		{
			lines.Add($"{Babel(strings, "GUI_RENT")} <s1>{Gold}{property.Income.ToString(CultureInfo.InvariantCulture)}</s1>");
		}

		if (property.BoughtFor > 0)
		{
			lines.Add($"Bought for <s1>{Gold}{property.BoughtFor.ToString(CultureInfo.InvariantCulture)}</s1>");
		}

		if (property.IsMarital)
		{
			lines.Add("Your marital home");
		}
		else if (property.IsHome)
		{
			lines.Add(property.IsRented ? "Rented out" : "Not rented out");
		}

		if (property.IsShared)
		{
			lines.Add("Shared with your partner: its rent and sale are split half and half.");
		}

		bool decays = property.IsHome && property.IsRented;
		if (decays || property.Condition < 1f)
		{
			lines.Add($"Condition <s1>{Percent(property.Condition)}</s1>");
			if (property.Condition < 1f && property.RepairCost > 0)
			{
				lines.Add($"Full repair <s1>{Gold}{property.RepairCost.ToString(CultureInfo.InvariantCulture)}</s1> — press A to repair.");
			}

			if (decays)
			{
				lines.Add(property.Condition <= 0f
					? "Your tenants have stopped paying rent until it is repaired."
					: "Each rent collection wears it down; tenants stop paying at 0%.");
			}
		}

		values[SaleTitle] = Name(property);
		values[SaleBody] = string.Join("<br><br>", lines);
		return true;
	}

	/// <summary>Repair All's outcome, packed by the pump: player &lt;&lt; 56 | still worn &lt;&lt; 48 | repaired &lt;&lt; 32 | gold spent.</summary>
	private static string RepairAllNotice(ulong packed)
	{
		int left = (int)((packed >> 48) & 0xFFul);
		int repaired = (int)((packed >> 32) & 0xFFFFul);
		uint spent = (uint)(packed & 0xFFFFFFFFul);
		string done = repaired == 0
			? "You don't have enough gold to repair any of your properties."
			: $"Repaired {repaired.ToString(CultureInfo.InvariantCulture)} {(repaired == 1 ? "property" : "properties")} for {spent.ToString(CultureInfo.InvariantCulture)} gold.";
		return left > 0 && repaired > 0
			? $"{done} You didn't have enough gold for the other {left.ToString(CultureInfo.InvariantCulture)}."
			: done;
	}

	/// <summary>The player a Repair All notice is for.</summary>
	public static int RepairAllPlayer(ulong packed) => (int)(packed >> 56);

	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is { } book && book.TryGetString(tag, out string resolved) && resolved.Length != 0
			? resolved
			: string.Empty;

	/// <summary>Tells the player something (the game's notices); set by the game's UI.</summary>
	public static System.Action<string>? Told { get; set; }

	/// <summary>The words for a property notice (Fable 3's, where it has them).</summary>
	public static string? Notice(int kind, ulong uid) => kind switch
	{
		0 => $"Your tenants in {NameOf(uid)} are refusing to pay rent because the house has fallen into disrepair. You can repair houses via the map.",
		1 => $"{NameOf(uid)} has been fully repaired.",
		2 => "You don't have enough gold to repair this house.",
		4 => $"While you were away, your share of the rent and sales from your shared properties came to {uid.ToString(CultureInfo.InvariantCulture)} gold.",
		5 => $"Half of the sale, {uid.ToString(CultureInfo.InvariantCulture)} gold, goes to your partner.",
		6 => $"You and your partner now share {NameOf(uid)}.",
		7 => RepairAllNotice(uid),
		_ => null,
	};
}
