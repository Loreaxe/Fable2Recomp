namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using System.Globalization;
using Probe = Fable3ScreenModel.Probe;

/// <summary>
/// The pages of the box-manager screens past the five <see cref="Fable3ScreenModel"/> composes —
/// join and visibility options, the friends and chat channels, the treasury statement, the bed
/// menu, the food query, the wedding picker, co-op partners, the marriage proposal, the divorce
/// message — and the save menu. The texts are the guest's stated texts (the text funnels at their
/// entry); the cursor row, the ghosting and the choices are what the screen's own calls stated
/// (<see cref="Fable3ScreenState"/>: its Show's and pad handler's fields, its opener's arguments,
/// its row writers' records), never the elements' slides. Pure, so the tests run it against a
/// stated set.
/// </summary>
/// <remarks>
/// <para>THE CLASSES (Fable 3 IDB, disc build; slot 1 Show, 4 input with the EGameAction in `*ev`,
/// 7 the box type that picks the presentation through sub_8291F4C8 and the file through
/// sub_82B3EC40's table): join/visibility 0x820C35F8 (sub_82976A08 / sub_82976BC8, type = +48:
/// 18 JoinOptions.bgf, 19 Visability.bgf), friends channel 0x820C3640 (sub_82977A48 /
/// sub_82977B78, 21), chat channel 0x820C2F58 (sub_82972408 / sub_829727C8, 22), treasury
/// 0x820D84A4 (sub_82B59FA8 / sub_82B5A410, 17 TreasureAccounts.bgf), bed 0x820D7508
/// (sub_82B58F98 / sub_82B59348, 14), food 0x820D72F4 (sub_82B579E8 / sub_82B58048, 9), wedding
/// 0x820D85A4 (sub_82B5BE98 / sub_82B5C260, 23), partners 0x820D8754 (sub_82B5C868 /
/// sub_82B5CA70, 24), proposal 0x820B4640 (sub_8281CCC8 / sub_8281D238, 10), divorce 0x820C0088
/// (sub_829284D8 / sub_82928958, 11). Paths are the classes' static CCharStrings.</para>
/// <para>The save menu is not a box: it is the object at `*(SM+56)` (ctor sub_8296C4D0), opened by
/// sub_8296C5D0 on presentation slot 11 (SaveMenu.bgf), and its root state is the statement —
/// 1..4 the cursor row, 5 closed (sub_8296C858 at 0x8296C88C).</para>
/// <para>Labels the guest never writes are its own tags where it has them (the join/visibility
/// choices are hashed from TEXT_GUI_JOIN_OPTIONS_* / TEXT_GUI_ORB_VISIBILITY_* by the initialisers
/// at 0x831DE340 / 0x831DE500), else the screen's .bgf textstring.</para>
/// </remarks>
public static class Fable3BoxScreenModel
{
	public const string JoinOptions = "OrbEmote";
	public const string Visibility = "Visibilities";
	public const string FriendsChannel = "FriendsChannel";
	public const string ChatChannel = "ChatChannel";
	public const string Treasury = "TreasureAccounts";
	public const string BedMenu = "BedMenu";
	public const string FoodQuery = "InfoBox";
	public const string WeddingPicker = "WeddingPicker";
	public const string Partners = "CoopPartnersInfoBox";
	public const string Proposal = "InfoBox.MarriageProposal";
	public const string Divorce = "DivorceMessageBox";
	public const string SaveMenu = "SaveMenu";

	private const string Gap = "    ";

	/// <summary>The element root each display_box_hooks.cpp `screen:*` kind writes under, or null.</summary>
	public static string? RootOf(string kind) => kind switch
	{
		"screen:join" => JoinOptions,
		"screen:visibility" => Visibility,
		"screen:friends" => FriendsChannel,
		"screen:chat" => ChatChannel,
		"screen:treasury" => Treasury,
		"screen:bed" => BedMenu,
		"screen:food" => FoodQuery,
		"screen:wedding" => WeddingPicker,
		"screen:partners" => Partners,
		"screen:proposal" => Proposal,
		"screen:divorce" => Divorce,
		_ => null,
	};

	/// <summary>The page of the screen whose element root is <paramref name="root"/>; null for a root this model does not draw.</summary>
	public static Fable3ScreenPage? Compose(string root, IReadOnlyDictionary<string, Probe> stated, Fable3ScreenState screen,
		System.Func<string, string> resolve) => root switch
	{
		JoinOptions => ChoicePage(stated, screen, resolve, "TEXT_GUI_JOIN_OPTIONS_TITLE", JoinOptions + ".ThisEmote",
			new[] { ("Any1", "TEXT_GUI_JOIN_OPTIONS_ANYONE"), ("Invite", "TEXT_GUI_JOIN_OPTIONS_INVITE"), ("Off", "TEXT_GUI_JOIN_OPTIONS_OFF") }),
		Visibility => ChoicePage(stated, screen, resolve, "TEXT_GUI_ORB_VISIBILITY_TITLE", Visibility + ".ThisEmote",
			new[] { ("Everyone", "TEXT_GUI_ORB_VISIBILITY_EVERYONE"), ("Friends", "TEXT_GUI_ORB_VISIBILITY_FRIENDS"), ("None", "TEXT_GUI_ORB_VISIBILITY_NONE") }),
		FriendsChannel => FriendsPage(screen, resolve),
		ChatChannel => ChatPage(stated, screen, resolve),
		Treasury => TreasuryPage(stated, screen, resolve),
		BedMenu => BedPage(stated, screen, resolve),
		FoodQuery => FoodPage(stated, resolve),
		WeddingPicker => WeddingPage(stated, screen, resolve),
		Partners => PartnersPage(stated, screen, resolve),
		Proposal => ProposalPage(stated, resolve),
		Divorce => DivorcePage(stated, screen, resolve),
		SaveMenu => SaveMenuPage(screen, resolve),
		_ => null,
	};

	/// <summary>
	/// JOIN OPTIONS and VISIBILITY: one class, three choices, fields (type `+48`, cursor `+52`).
	/// The Show sub_82976A08 starts the cursor on the SAVED setting (sub_829770A8: the profile's
	/// +108 join / +104 visibility 0/1/2 to 1/2/3, 0x829771C8..0x829772E8) and states it as the
	/// tick (`<root>.Emotes` / `.Visibilities`, 0x82976AD0) — the Show's field is the saved one; the
	/// stick moves `+52` 1..3 (sub_82976E58 / sub_82976F80, 0x82976EF0 / 0x82977018). Input
	/// sub_82976BC8: A (74) commits through sub_82977370 and closes, B (75) closes.
	/// </summary>
	private static Fable3ScreenPage ChoicePage(IReadOnlyDictionary<string, Probe> stated, Fable3ScreenState screen,
		System.Func<string, string> resolve, string titleTag, string currentPath, (string Name, string Tag)[] choices)
	{
		int cursor = screen.Field(1, 1);
		int saved = screen.ShownField(1, 0);
		var rows = new List<Fable3ScreenPage.Row>(choices.Length);
		for (int i = 0; i < choices.Length; ++i)
		{
			string label = resolve(choices[i].Tag + "_TITLE");
			rows.Add(new(choices[i].Name, saved == i + 1 ? label + Gap + "✓" : label, false, choices[i].Tag + "_DESC"));
		}

		string current = Text(stated, currentPath) ?? string.Empty;
		return new Fable3ScreenPage(resolve(titleTag), rows, Clamp(cursor - 1, rows.Count), current,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_ACCEPT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, true);
	}

	/// <summary>
	/// FRIENDS CHANNEL. The cursor `+36` 1..3: the Show sets 1 (0x82977A64), the stick moves it
	/// (0x82977D94 / 0x82977DE8); 4 parks it. Input sub_82977B78: the stick (26/27,
	/// 0x82977D10 / 0x82977D18) moves 1..3, A (74, 0x82977D04) runs the row (sub_82365190 /
	/// sub_82365220 / sub_82365328) and closes, B (75, 0x82977F04) closes. Labels: friendschannel.bgf.
	/// </summary>
	private static Fable3ScreenPage FriendsPage(Fable3ScreenState screen, System.Func<string, string> resolve)
	{
		int cursor = screen.Field(0, 1);
		var rows = new List<Fable3ScreenPage.Row>
		{
			new("Friends", "Friends"),
			new("Join", "Join a Party"),
			new("Invite", "Invite into Party"),
		};

		return new Fable3ScreenPage("Friends Channel", rows, cursor is >= 1 and <= 3 ? cursor - 1 : -1, string.Empty,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_ACCEPT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, true);
	}

	/// <summary>
	/// CHAT CHANNEL. The working mute byte `+36`: the Show starts it on the saved setting
	/// (0x8297245C), left/right toggle it (0x82972C28); sub_82972AB0 slides `ChatChannel.Emotes` and
	/// `ChatChannel.OnOff` 2 muted / 1 audible from it (0x82972AF4 / 0x82972B08), writes the SAVED setting's
	/// tag into `ChatChannel.CurrSetting` (0x82972B60) and the working one's description into
	/// `ChatChannel.SelectionDescription.Mute_Description` (0x82972B90). Input sub_829727C8: left/right (31/32 via
	/// sub_82972BA0) toggle `+36`, A (72, 0x8297294C) saves it and closes, B (73, 0x82972A58) closes.
	/// </summary>
	private static Fable3ScreenPage ChatPage(IReadOnlyDictionary<string, Probe> stated, Fable3ScreenState screen, System.Func<string, string> resolve)
	{
		bool muted = screen.Field(0, 0) != 0;
		string value = resolve(muted ? "TEXT_GUI_AUDIO_CHAT_MUTED" : "TEXT_GUI_AUDIO_CHAT_AUDIBLE");
		var rows = new List<Fable3ScreenPage.Row> { new("Mute", "Mute" + Gap + "◀ " + value + " ▶") };
		string description = string.Join("\n", Present(
			Text(stated, ChatChannel + ".CurrSetting") is { } current ? "Currently set to: " + current : null,
			Text(stated, ChatChannel + ".SelectionDescription.Mute_Description")));
		return new Fable3ScreenPage("Chat Channel", rows, 0, description,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_ACCEPT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, true);
	}

	/// <summary>
	/// THE TREASURY STATEMENT. The page `+28` (fields: page, direction `+32`, amount `+56`): the
	/// ctor sub_82B59C40 sets 3, or 2 with the transfer widget (0x82B59CB0 / 0x82B59F04), the input's
	/// query sub_82B5A6C8 moves it (0x82B5A6F0); the Show and the Update slide the root to it
	/// (0x82B5A134 / 0x82B5A388): 2 with the transfer widget, 3 without, 4/5 the confirmation query.
	/// sub_82B5AEA8 writes the balances (`Personal.Personal Balance Value`, `Treasury.Treasury Balance
	/// Value`, the `Treasury Balance` label) and the transfer row's tags; sub_82B5B410 /
	/// sub_82B5B628 the direction's title, transfer label and X caption; sub_82B5A6C8 the query
	/// text (`QueryBox.Group.QueryBox.Text` at 0x82B5AAE0, root state 5 at 0x82B5AACC). Input sub_82B5A410: A (74,
	/// 0x82B5A5A0) steps the transfer, B (75) backs out of the query or closes, X (76) flips the
	/// direction, left/right reach the amount widget sub_82B56830.
	/// </summary>
	private static Fable3ScreenPage TreasuryPage(IReadOnlyDictionary<string, Probe> stated, Fable3ScreenState screen, System.Func<string, string> resolve)
	{
		int page = screen.Field(0, 3);
		var rows = new List<Fable3ScreenPage.Row>();
		if (Text(stated, Treasury + ".Personal.Personal Balance Value") is { } personal)
		{
			rows.Add(new("Personal", "Personal Balance" + Gap + personal));
		}

		if (Text(stated, Treasury + ".Treasury.Treasury Balance Value") is { } treasury)
		{
			rows.Add(new("Treasury", (Text(stated, Treasury + ".Treasury.Treasury Balance") ?? "Treasury Balance") + Gap + treasury));
		}

		if (page == 2 && Text(stated, Treasury + ".MoneyTransfer.TransferFundsComponent.Text.Text") is { } transfer)
		{
			rows.Add(new("Transfer", transfer + Gap + "◀ " + (Text(stated, Treasury + ".MoneyTransfer.TransferFundsComponent.Value.Value") ?? "0") + " ▶"));
		}

		foreach ((int index, string path, Probe row) in Fable3ScreenModel.Indexed(stated, Treasury + ".Transactions.Transaction Name.Transaction "))
		{
			if (row.Hidden || row.Text is not { Length: > 0 } name)
			{
				continue;
			}

			string value = Text(stated, Treasury + ".Transactions.Transaction Value.Transaction " + index.ToString(CultureInfo.InvariantCulture)) ?? string.Empty;
			rows.Add(new("Transaction" + index.ToString(CultureInfo.InvariantCulture), name + Gap + value));
		}

		var prompts = new List<Fable3ScreenPage.Prompt> { new("A", resolve("GUI_ACCEPT")), new("B", resolve("GUI_BACK")) };
		if (page == 2 && Text(stated, Treasury + ".Buttons.X_Button.Group.Text") is { } flip)
		{
			prompts.Add(new("X", flip));
		}

		Fable3ScreenPage.QueryBox? query = page is 4 or 5 && Text(stated, Treasury + ".QueryBox.Group.QueryBox.Text") is { } question
			? new Fable3ScreenPage.QueryBox(string.Empty, question,
				new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_ACCEPT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) })
			: null;
		return new Fable3ScreenPage(Text(stated, Treasury + ".Title") ?? resolve("TEXT_GUI_MONEY_SCREEN_TITLE"), rows,
			page == 2 ? rows.FindIndex(row => row.Name == "Transfer") : -1,
			page == 2 ? Text(stated, Treasury + ".MoneyTransfer.DescriptionText") ?? string.Empty : string.Empty, prompts, page == 2, query);
	}

	/// <summary>
	/// THE BED MENU. Fields (row `+44` 0 Sleep / 1 Sex, sleep option `+48`, sex option `+52`) and
	/// the opener's (offered `+56`, group `+57`, cycle `+58`; sub_825FB858's r6..r8). sub_82B591E8
	/// slides `BedMenu.Sleep` / `BedMenu.Sex` 2 for the cursor row, 1 for the other
	/// (0x82B5922C..0x82B59264); without `offered` the Show halves the Sex row's opacity
	/// (sub_82B59BB8, 0x82B59048 / 0x82B59054) and the stick never reaches it (0x82B59800) — the row
	/// is ghosted. The Show writes the row titles by tag (TEXT_GUI_BED_MENU_SLEEP, the Sex one by
	/// `group`, 0x82B58FE8); sub_82B590C8 writes each row's option and description by tag
	/// (`.Widget.Text`, `.Description`). Input sub_82B59348: up/down (29/30) move, left/right
	/// (31/32) cycle the option, 41 accepts, 42 cancels.
	/// </summary>
	private static Fable3ScreenPage BedPage(IReadOnlyDictionary<string, Probe> stated, Fable3ScreenState screen, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		int cursor = screen.Field(0, 0);
		int selected = -1;
		for (int index = 0; index < 2; ++index)
		{
			string name = index == 0 ? "Sleep" : "Sex";
			string path = BedMenu + "." + name;
			if (Text(stated, path + ".Title") is not { } title)
			{
				continue;
			}

			if (cursor == index)
			{
				selected = rows.Count;
			}

			string option = Text(stated, path + ".Widget.Text") is { } widget ? Gap + "◀ " + widget + " ▶" : string.Empty;
			bool ghosted = index == 1 && !screen.OpenedFlag(0, true);
			rows.Add(new(name, title + option, ghosted, Text(stated, path + ".Description") ?? string.Empty));
		}

		return new Fable3ScreenPage("Bed Activities", rows, selected, string.Empty,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_ACCEPT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, true);
	}

	/// <summary>
	/// THE FOOD QUERY. The Show sub_82B579E8 writes the item eaten now and the one offered
	/// (`InfoBox.Current.*` / `InfoBox.Replacement.*`: Item_Name, Item_Health, Item_Fatness) and the
	/// root state. Input sub_82B58048: 41 (0x82B581D8) takes the replacement and 42 (0x82B581E0)
	/// declines, each posting its reply (0x82B5843C / 0x82B58E44). Labels: foodquerybox.bgf.
	/// </summary>
	private static Fable3ScreenPage FoodPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		foreach ((string group, string label) in new[] { ("Current", "Current:"), ("Replacement", "Replacement:") })
		{
			string path = FoodQuery + "." + group;
			if (Text(stated, path + ".Item_Name") is not { } item)
			{
				continue;
			}

			string line = label + " " + item;
			if (Text(stated, path + ".Item_Health") is { } health)
			{
				line += Gap + "Health: " + health;
			}

			if (Text(stated, path + ".Item_Fatness") is { } fatness)
			{
				line += Gap + "Fatness: " + fatness;
			}

			rows.Add(new(group, line));
		}

		return new Fable3ScreenPage(string.Empty, rows, -1, Text(stated, FoodQuery + ".Text") ?? string.Empty,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_YES")), new Fable3ScreenPage.Prompt("B", resolve("GUI_NO")) }, false);
	}

	/// <summary>
	/// THE WEDDING PICKER. The Show sub_82B5BE98 writes the level's name (`WeddingPicker.Title`) and
	/// three tiers `WeddingPicker.Details.WeddingDetails%d.Group.{Title,Details,Cost}` (titles and
	/// descriptions by tag name, sub_82819230; the price as text, marked when unaffordable). The tier
	/// `+68` is the field (the stick, 0x82B5C33C / 0x82B5C3A8; sub_82B5C158 slides it, 0x82B5C18C);
	/// the ctor's three affordable bytes (sub_82B5BCF0, `+88..+90`) ghost a tier 41 cannot book
	/// (event 69 only for an affordable one, 0x82B5C408..0x82B5C43C); 42 backs out (event 70, 0x82B5C524).
	/// </summary>
	private static Fable3ScreenPage WeddingPage(IReadOnlyDictionary<string, Probe> stated, Fable3ScreenState screen, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		int tier = screen.Field(0, 0);
		int selected = -1;
		for (int i = 0; i < 3; ++i)
		{
			string path = WeddingPicker + ".Details.WeddingDetails" + i.ToString(CultureInfo.InvariantCulture);
			if (Text(stated, path + ".Group.Title") is not { } title)
			{
				continue;
			}

			if (tier == i)
			{
				selected = rows.Count;
			}

			string cost = Text(stated, path + ".Group.Cost") is { } price ? Gap + price : string.Empty;
			rows.Add(new("Tier" + i.ToString(CultureInfo.InvariantCulture), title + cost, !screen.OpenedFlag(i, true),
				Text(stated, path + ".Group.Details") ?? string.Empty));
		}

		return new Fable3ScreenPage(Text(stated, WeddingPicker + ".Title") ?? resolve("TEXT_GUI_SELECT_WEDDING_LOCATION"), rows, selected, string.Empty,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_ACCEPT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, true);
	}

	/// <summary>
	/// CO-OP PARTNERS. sub_82B5D6A8 writes `CoopPartnersInfoBox.NumOnline` (0x82B5D7E4) and a window
	/// of eight rows `CoopPartnersInfoBox.Details.PartnerDetails%d` from the scroll `+60`: hidden past
	/// the list (0x82B5D96C), else written by sub_82B5D9E0(box, row path, record) — `.GamerTag`,
	/// `.NumBuildings`, `.NumChildren` only for a marriage (record+40, 0x82B5DC30), `.Online` /
	/// `.Offline` from record+43 (0x82B5DC9C / 0x82B5DCCC). The cursor `+56` is window-relative (the
	/// row equal to it slides 2, 0x82B5D914); the cursor row's joinable record+42 shows the A button
	/// (0x82B5D830). Fields (cursor, scroll); the rows are sub_82B5D9E0's records
	/// (<see cref="Fable3ScreenState.PartnerRows"/>). Input sub_82B5CA70: the stick (26/27) moves,
	/// A (74) / X (76) / Y (77) act on the row, B (75) closes.
	/// </summary>
	private static Fable3ScreenPage PartnersPage(IReadOnlyDictionary<string, Probe> stated, Fable3ScreenState screen, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		int cursor = screen.Field(0, 0);
		int selected = -1;
		bool joinable = false;
		for (int index = 0; index < 8; ++index)
		{
			string path = Partners + ".Details.PartnerDetails" + index.ToString(CultureInfo.InvariantCulture);
			if (!screen.PartnerRows.TryGetValue(path, out (bool Married, bool Online, bool Joinable) record) ||
				At(stated, path) is { Hidden: true } || Text(stated, path + ".GamerTag") is not { } gamer)
			{
				continue;
			}

			if (index == cursor)
			{
				selected = rows.Count;
				joinable = record.Joinable;
			}

			string line = gamer;
			if (Text(stated, path + ".NumBuildings") is { } buildings)
			{
				line += Gap + buildings;
			}

			if (record.Married && Text(stated, path + ".NumChildren") is { } children)
			{
				line += Gap + children;
			}

			line += Gap + (record.Online ? "Online" : "Offline");
			rows.Add(new("Partner" + index.ToString(CultureInfo.InvariantCulture), line));
		}

		string description = string.Join("\n", Present(
			Text(stated, Partners + ".Details.LocationFormedOn.Location") is { } location ? "Location: " + location : null,
			Text(stated, Partners + ".Details.LocationFormedOn.Date") is { } date ? "Formed on: " + date : null));
		var prompts = new List<Fable3ScreenPage.Prompt>();
		if (joinable)
		{
			prompts.Add(new("A", resolve("GUI_SELECT")));
		}

		prompts.Add(new("B", resolve("GUI_BACK")));
		string title = "Partnerships" + (Text(stated, Partners + ".NumOnline") is { } online ? Gap + "Online: " + online : string.Empty);
		return new Fable3ScreenPage(title, rows, selected, description, prompts, true);
	}

	/// <summary>
	/// THE MARRIAGE PROPOSAL. The Show sub_8281CCC8 writes the proposer line
	/// `InfoBox.MarriageProposal.GamerTag.From` (0x8281CD60) and six figures
	/// `….GamerInfo.{Gold,TimesMarried,TimesDivorced,TimesWidowed,NumberOfChildren,CrimesCommitted}`
	/// from `+40..+60` (0x8281CD80..0x8281CE48), the X caption by tag (0x8281CEB0) and `InfoBox` state 2
	/// (0x8281CEC4). Input
	/// sub_8281D238: 41 accepts, 42 refuses (0x8281D3D4 / 0x8281D3DC), X (76, 0x8281D3C8) shows the
	/// proposer's card. Labels: proposalquerybox.bgf (`….GamerInfo.*2`).
	/// </summary>
	private static Fable3ScreenPage ProposalPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		foreach ((string name, string label) in new[]
		{
			("Gold", "Current Gold"), ("TimesMarried", "Times Married"), ("TimesDivorced", "Times Divorced"),
			("TimesWidowed", "Times Widowed"), ("NumberOfChildren", "Number of Children"), ("CrimesCommitted", "Number of Crimes Committed"),
		})
		{
			if (Text(stated, Proposal + ".GamerInfo." + name) is { } value)
			{
				rows.Add(new(name, label + Gap + value));
			}
		}

		var prompts = new List<Fable3ScreenPage.Prompt> { new("A", resolve("GUI_YES")), new("B", resolve("GUI_NO")) };
		if (Text(stated, Proposal + ".Buttons.X_Button.small.Text") is { } card)
		{
			prompts.Add(new("X", card));
		}

		return new Fable3ScreenPage("Marriage Proposal", rows, -1, Text(stated, Proposal + ".GamerTag.From") ?? string.Empty, prompts, false);
	}

	/// <summary>
	/// THE DIVORCE MESSAGE. Fields (reason `+136`, page `+172`). The page is the ctor's 6th argument
	/// (sub_829281C8, `stw r26,0xAC(r31)` 0x8292832C — nothing else writes it) and the Show writes it
	/// out at 0x8292859C..0x82928684: the title/action pair through sub_82928BB0 (`Foreground.Title`,
	/// `Foreground.ActionDescription`), then the page's writer — <see cref="DivorcePageOf"/>. Input
	/// sub_82928958: A (74) closes every page (sub_82929628 sets +176/+177, 0x8292963C/0x82929640),
	/// on an asking page first queueing the confirmation query (0x82929760) and recording the
	/// request with the reason (sub_825C6660 at 0x829297C4); B (75, 0x82928A10) and left/right
	/// (31/32, the reason +136 wrapped at the GDB list's count, 0x82928AB4 / 0x82928B80) only on an
	/// asking page (`cmpwi r11,3` / `cmpwi r11,0` at 0x829289E4 / 0x829289EC).
	/// </summary>
	private static Fable3ScreenPage DivorcePage(IReadOnlyDictionary<string, Probe> stated, Fable3ScreenState screen, System.Func<string, string> resolve)
	{
		string fore = Divorce + ".Foreground.";
		DivorceKind kind = DivorcePageOf(screen.Field(1, 0));
		var rows = new List<Fable3ScreenPage.Row>();
		int selected = -1;
		void Line(string name, params string?[] parts)
		{
			var present = new List<string>(Present(parts));
			if (present.Count > 0)
			{
				rows.Add(new(name, string.Join(Gap, present)));
			}
		}

		switch (kind)
		{
			case DivorceKind.Asking:
				// sub_82928C90: both pictures (sub_82929888(box, 1) at 0x82928CE0), the reason picked
				// now (sub_829297D8 at 0x82928CE8), the joint assets +124 (0x82928D50) and the
				// orphaned children line (0x82928F64).
				Line("Names", Text(stated, fore + "GamerTagPics.Pic1.Tag"), Text(stated, fore + "GamerTagPics.Pic2.Tag"));
				if (Text(stated, fore + "ReasonsPicker.Reasons.ReasonText") is { } reason)
				{
					selected = rows.Count;
					rows.Add(new("Reason", "◀ " + reason + " ▶"));
				}

				Line("Assets1", Text(stated, fore + "JointAssetsBreakdown.Tag"), Text(stated, fore + "JointAssetsBreakdown.Amount1"));
				Line("Kids", Text(stated, fore + "Kids"));
				break;
			case DivorceKind.Told:
				// sub_82928F78: the partner's picture only (sub_82929888(box, 0) at 0x82928FC8), the
				// reason the partner gave (0x82929074 / 0x829290D0), both shares +124 / +128
				// (0x82929194 / 0x829291D4) under their names (0x829292A8 / 0x829292D0).
				Line("Names", Text(stated, fore + "GamerTagPics.Pic2.Tag"));
				Line("Reason", Text(stated, fore + "ReasonReport.ReasonReportTitle"), Text(stated, fore + "ReasonReport.ReasonText"));
				Line("Assets1", Text(stated, fore + "JointAssetsBreakdown.Tag"), Text(stated, fore + "JointAssetsBreakdown.Amount1"));
				Line("Assets2", Text(stated, fore + "JointAssetsBreakdown.Tag2"), Text(stated, fore + "JointAssetsBreakdown.Amount2"));
				break;
			case DivorceKind.Confirmed:
				// sub_82929378: the partner's picture, one share +128 (0x82929484) under the
				// player's name (0x829295B0); Amount2 / Tag2 / Kids emptied (0x829294C8..0x829295FC).
				Line("Names", Text(stated, fore + "GamerTagPics.Pic2.Tag"));
				Line("Assets1", Text(stated, fore + "JointAssetsBreakdown.Tag"), Text(stated, fore + "JointAssetsBreakdown.Amount1"));
				break;
		}

		var prompts = new List<Fable3ScreenPage.Prompt> { new("A", resolve("GUI_ACCEPT")) };
		if (kind == DivorceKind.Asking)
		{
			prompts.Add(new("B", resolve("GUI_BACK")));
		}

		return new Fable3ScreenPage(Text(stated, fore + "Title") ?? string.Empty, rows, selected,
			Text(stated, fore + "ActionDescription") ?? string.Empty, prompts, kind == DivorceKind.Asking);
	}

	/// <summary>What a divorce page shows: asking for a reason, told of the partner's, or the outcome confirmed.</summary>
	public enum DivorceKind
	{
		Asking,
		Told,
		Confirmed,
	}

	/// <summary>
	/// The divorce box's page `+172` as the Show dispatches it (0x8292859C..0x82928684): 0 divorce /
	/// 3 partnership split ask (sub_82928C90, root slide 2 at 0x82928CB4), 1 divorced / 4 split up are
	/// told (sub_82928F78, slide 3 at 0x82928F9C), 2 / 5 confirmed (sub_82929378, slide 4 at
	/// 0x8292939C); above 5 the Show writes nothing (`cmplwi r11,5` / `bgt` 0x829285A0).
	/// </summary>
	public static DivorceKind DivorcePageOf(int page) => page switch
	{
		1 or 4 => DivorceKind.Told,
		2 or 5 => DivorceKind.Confirmed,
		_ => DivorceKind.Asking,
	};

	/// <summary>
	/// THE SAVE MENU (<see cref="Fable3UiStatements"/>: `@SaveMenu.Open(allowed, row)`,
	/// `@SaveMenu.Row`). The open sub_8296C5D0 slides `SaveMenu.Foreground.Text.Save` and
	/// `.SaveAndQuit` 1 when saving is allowed, 2 when not (0x8296C76C..0x8296C790) — those two rows
	/// are ghosted without it — and starts the row `+12` on 1, or 3 when not allowed
	/// (0x8296C6F4 / 0x8296C700); sub_8296CB08 (TU3 sub_829B5350) sends the selected row's `.Layout` to
	/// slide 2 and the other three to slide 1: 1 Save, 2 SaveAndQuit, 4 CharacterManagement, else Quit
	/// (TU3 0x829B5374..0x829B53A4, the four SetStates 0x829B53E8 / 0x829B5414 / 0x829B5440 /
	/// 0x829B546C). THE ROWS STAND IN THE GUEST'S ORDER: up is `row - 1` clamped to 1..4 and only while
	/// saving is allowed (TU3 sub_829B5480: `lbz r11,1(r31)` 0x829B54E0, `addi r11,r11,-1` 0x829B54F8),
	/// down `row + 1` clamped (sub_829B5540: `addi r11,r11,1` 0x829B55AC), each behind 0.3 s
	/// (flt_8206CBF4) — so Quit (3) is below Save and Quit and Character Management (4) is last, and
	/// the page's index is the row less one. 72 runs the row (sub_8296CDB0), 73 closes (sub_8296C858).
	/// NO DOCUMENT SHIPS FOR IT: the open asks for presentation type 11 (`li r4,0xB` 0x8296C71C →
	/// sub_825C79A0), sub_82B3EC40 names it `SaveMenu.bgf` (0x82B3EE9C..0x82B3EEAC), and that file is
	/// in no bank on the disc; the loader stores a presentation only when the open succeeds
	/// (sub_825C7A90: `LHSFS__CLegacyFile_vf10` 0x825C7AE8, `beq` 0x825C7AF4, store 0x825C7B30), and
	/// every write above is skipped while the slot is null (0x8296C734, 0x8296CB68, 0x8296C87C;
	/// TU3 `beq` 0x829B53B0). No build ships it (the disc's and the PC build's guiscripts.bnk, TU3's
	/// tu1.bnk): the 360 draws no save menu and no lit row. So there are no authored captions,
	/// frame or highlight: the element names stand in for the captions.
	/// </summary>
	private static Fable3ScreenPage SaveMenuPage(Fable3ScreenState screen, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>
		{
			new("Save", "Save", !screen.SaveAllowed),
			new("SaveAndQuit", "Save and Quit", !screen.SaveAllowed),
			new("Quit", "Quit"),
			new("CharacterManagement", "Character Management"),
		};

		int selected = screen.SaveRow is >= 1 and <= 4 ? screen.SaveRow - 1 : 2;
		return new Fable3ScreenPage(string.Empty, rows, selected, string.Empty,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_SELECT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, true);
	}

	private static int Clamp(int index, int count) => index >= 0 && index < count ? index : -1;

	private static Probe? At(IReadOnlyDictionary<string, Probe> stated, string path) =>
		stated.TryGetValue(path, out Probe probe) ? probe : null;

	private static string? Text(IReadOnlyDictionary<string, Probe> stated, string path) =>
		stated.TryGetValue(path, out Probe probe) && probe.Text is { Length: > 0 } text ? text : null;

	private static IEnumerable<string> Present(params string?[] texts)
	{
		foreach (string? text in texts)
		{
			if (text is { Length: > 0 })
			{
				yield return text;
			}
		}
	}
}
