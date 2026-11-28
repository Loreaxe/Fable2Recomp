using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// Screen 21's three pages, owned — arrest notice, fine and parole terms. Not cursor-driven:
/// the text is written once in the screen's activate (`sub_826A8C78`) and `sub_826A8E28` only
/// toggles page visibility off a `screen+196` detector holding `Float3` (modes 50/51/52).
/// The screen's one opener `sub_823C4128` states its arguments (`@CrimeScreen.Open`,
/// crime_screen_stated.cpp); the two notices it composes from them are composed here the same
/// way: `Babel(GUI_SCREEN_CRIME_YOU_HAVE_BEEN_CHARGED_WITH) + "\n" + charge` and
/// `Babel(GUI_SCREEN_CRIME_COMMUNITY_SERVICE) + "\n" + Babel(serviceKey)` (`sub_826A96F0`).
/// </summary>
public static class Engine_UiCrimePage
{
	/// <summary>The three pages' text elements — `…PageN.Contents.Text`.</summary>
	private const string Page1Text = "Page1.Contents.Text";

	private const string Page2Text = "Page2.Contents.Text";

	private const string Page3Text = "Page3.Contents.Text";

	/// <summary>The fine's caption; takes the amount as a parameter.</summary>
	private const string FineAmountTag = "GUI_SCREEN_CRIME_FINE_AMOUNT";

	/// <summary>
	/// Page three's parole notice, used when community service is not on offer. `TEXT_` prefixed,
	/// unlike every other tag on this screen.
	/// </summary>
	private const string BrokenParoleTag = "TEXT_AI_CRIME_THREE_STRIKES_BROKEN_PAROLE";

	private const string ChargedWithTag = "GUI_SCREEN_CRIME_YOU_HAVE_BEEN_CHARGED_WITH";

	private const string CommunityServiceTag = "GUI_SCREEN_CRIME_COMMUNITY_SERVICE";

	/// <summary>What the opener last stated: `Charge` its charge text, `Service` its service key.</summary>
	public static Refii.Host.HostBridge.CrimeScreen Stated { get; private set; }

	/// <summary>The arrested player the opener stated (1, 2; 0 when its opener was no arrest).</summary>
	public static int Arrested { get; private set; }

	/// <summary>The opener's speaker (the guard) by its UniqueID ("0x…", Refii.uid's text), empty for none.</summary>
	public static string GuardUid { get; private set; } = string.Empty;

	/// <summary>
	/// Each time the opener states a screen (after <see cref="Stated"/> is set): the co-op relay
	/// (GameUiCoopConsent) answers an online host's arrest of player two on the joiner's screen.
	/// </summary>
	public static System.Action? Opened { get; set; }

	/// <summary>
	/// ON AN ONLINE JOINER: the host's arrest this machine's crime screen shows (its ticket), 0 for none.
	/// A row's A there goes to the host as well (<see cref="Answered"/>), which posts the choice for player two.
	/// </summary>
	public static uint RelayedTicket { get; set; }

	/// <summary>A row's choice ("pay", "resist", "service") on a relayed screen, for the co-op relay.</summary>
	public static System.Action<string>? Answered { get; set; }

	/// <summary>`@CrimeScreen.Open`: charge, fine, payFine, communityService, service key[, arrested, guard uid].</summary>
	public static void Open(Engine_UiModel.GuiNativeCall call)
	{
		if (call.Arguments.Count is 5 or 7 &&
			int.TryParse(call.Argument(1).Text, System.Globalization.NumberStyles.Integer,
				System.Globalization.CultureInfo.InvariantCulture, out int fine))
		{
			Stated = new Refii.Host.HostBridge.CrimeScreen(
				true, fine, call.Argument(2).Text == "1", call.Argument(3).Text == "1",
				call.Argument(0).Text ?? string.Empty, call.Argument(4).Text ?? string.Empty);
			Arrested = call.Arguments.Count == 7 && int.TryParse(call.Argument(5).Text,
				System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
				out int arrested) ? arrested : 0;
			GuardUid = call.Arguments.Count == 7 ? call.Argument(6).Text ?? string.Empty : string.Empty;
			Opened?.Invoke();
		}
	}

	/// <summary>A crime row's lever (`Crime:&lt;p&gt;:&lt;choice&gt;`): on a relayed screen its choice goes to the host too.</summary>
	public static void Chosen(string? lever)
	{
		if (RelayedTicket != 0u && lever is { Length: > 0 } && lever.LastIndexOf(':') is var colon and > 0)
		{
			Answered?.Invoke(lever[(colon + 1)..]);
		}
	}

	/// <summary>The values for the crime screen's three pages, or blanks when none was opened.</summary>
	public static IReadOnlyDictionary<string, string> Values(
		Refii.Host.HostBridge.CrimeScreen crime,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		var values = new Dictionary<string, string>(3)
		{
			[Page1Text] = string.Empty,
			[Page2Text] = string.Empty,
			[Page3Text] = string.Empty,
		};

		if (!crime.Known)
		{
			return values;
		}

		// Page one — the fine, then the charges. The `\n\n` is the guest's own spacing
		// (`sub_826A8C78`); this screen uses newlines, not the completed-quests page's `<br>`.
		string caption = Babel(strings, FineAmountTag);
		string fine = crime.Fine.ToString(System.Globalization.CultureInfo.InvariantCulture);
		string charge = $"{Babel(strings, ChargedWithTag)}\n{crime.Charge}";
		values[Page1Text] = $"{Format(caption, fine)}\n\n{charge}";

		// Page two — the charges alone.
		values[Page2Text] = charge;

		// Page three — the charges, then the community-service terms or the broken-parole notice.
		// The branch is the opener's communityService argument, which also gates that row.
		string terms = crime.CanServe
			? $"{Babel(strings, CommunityServiceTag)}\n{BabelKey(strings, crime.Service)}"
			: Babel(strings, BrokenParoleTag);
		values[Page3Text] = $"{charge}\n{terms}";
		return values;
	}

	/// <summary>
	/// A key as the guest's lookup `sub_8230E0E8` resolves it: the key itself, then `TEXT_` + key
	/// (0x8230e1a4).
	/// </summary>
	private static string BabelKey(Refii.Text.Engine_BabelDatabase? strings, string key)
	{
		string value = Babel(strings, key);
		return value.Length != 0 ? value : Babel(strings, "TEXT_" + key);
	}

	/// <summary>
	/// Substitutes the amount into the caption's `%d`. A caption carrying none is not an error —
	/// some localisations place the amount elsewhere — so the number is appended.
	/// </summary>
	private static string Format(string caption, string amount)
	{
		if (caption.Length == 0)
		{
			return amount;
		}

		return caption.Contains("%d", System.StringComparison.Ordinal)
			? caption.Replace("%d", amount, System.StringComparison.Ordinal)
			: $"{caption} {amount}";
	}

	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is not null && strings.TryGetString(tag, out string value) ? value : string.Empty;
}
