using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;
using static Refii.Ui.Engine_UiPageProviders;

/// <summary>
/// Fable 2's screen providers: the adapters from its book screens' page classes to the shell's
/// <see cref="Engine_UiPageProvider"/> registry, registered when the title installs.
/// </summary>
public static class Fable2PageProviders
{
	public const string Abilities = "abilities";
	public const string Logbook = "logbook";
	public const string Crime = "crime";
	public const string WorldMap = "worldmap";
	public const string Options = "options";
	public const string ItemBook = "item-book";
	public const string Achievements = "achievements";
	public const string RealEstate = "realestate";
	public const string QuestBox = "questbox";
	public const string InfoCard = "infocard";
	public const string Multiplayer = "multiplayer";
	public const string Coop = "coop";
	public const string MiniGame = "minigame";

	/// <summary>The `GUI` natives' widget model the minigame tally draws from — set by the shell.</summary>
	public static Engine_UiGuiWidgets? Widgets { get; set; }

	/// <summary>
	/// Whether player two is currently standing in their OWN hero's body rather than the
	/// pickbox henchman — the co-op screen's X toggle, held here so the prompt and the action
	/// agree about which way the next press goes.
	///
	/// Host state on purpose: the guest has no such concept. It spawns one of eight fixed
	/// henchmen and nothing else, so "is this their own hero" is a question only we are asking.
	/// </summary>
	public static bool CoopOwnHero { get; set; }

	/// <summary>
	/// Has a hero actually been captured to offer? Set by the shell — <c>Engine_</c> never reaches
	/// for the host bridge itself.
	///
	/// Without one there is nothing for X to switch TO, so the prompt says so rather than offering
	/// a swap that would land player two in a bare default body.
	/// </summary>
	public static bool CoopHeroAvailable { get; set; }

	/// <summary>
	/// Each page's hero status while that player's hero record crosses the wire ("Receiving
	/// hero… 3/7", "Hero ready"), or empty — set by the shell, shown under the player's name.
	/// </summary>
	public static string CoopPlayerOneStatus { get; set; } = string.Empty;

	/// <inheritdoc cref="CoopPlayerOneStatus"/>
	public static string CoopPlayerTwoStatus { get; set; } = string.Empty;

	/// <summary>Player two has accepted on the co-op screen (`+160`) — their prompts stand dimmed.</summary>
	public static bool CoopPlayerTwoAccepted { get; set; }

	/// <summary>
	/// This machine is the online host, so player two's page is a remote player's: the guest
	/// writes no save prompt there (it lights `LoadGroup` only for local and joiner pads), and the
	/// remaster draws the same page on both machines.
	/// </summary>
	public static bool CoopRemotePlayerTwo { get; set; }

	/// <summary>The two players' display names, as the shell knows them — left page is player one.</summary>
	public static string CoopPlayerOneName { get; set; } = string.Empty;
	public static string CoopPlayerTwoName { get; set; } = string.Empty;

	/// <summary>Fable 2's screen providers, under the names its templates use.</summary>
	public static void Register()
	{
		Engine_UiPageProviders.RegisterShipped(Abilities, new AbilitiesProvider());
		Engine_UiPageProviders.RegisterShipped(Logbook, new LogbookProvider());
		Engine_UiPageProviders.RegisterShipped(Crime, new CrimeProvider());
		Engine_UiPageProviders.RegisterShipped(WorldMap, new WorldMapProvider());
		Engine_UiPageProviders.RegisterShipped(Options, new OptionsProvider());
		Engine_UiPageProviders.RegisterShipped(ItemBook, new ItemBookProvider());
		Engine_UiPageProviders.RegisterShipped(Achievements, new AchievementsProvider());
		Engine_UiPageProviders.RegisterShipped(RealEstate, new RealEstateProvider());
		Engine_UiPageProviders.RegisterShipped(QuestBox, new QuestBoxProvider());
		Engine_UiPageProviders.RegisterShipped(InfoCard, new InfoCardProvider());
		Engine_UiPageProviders.RegisterShipped(Multiplayer, new MultiplayerProvider());
		Engine_UiPageProviders.RegisterShipped(Coop, new CoopProvider());
		Engine_UiPageProviders.RegisterShipped(MiniGame, new MiniGameProvider());
	}

	/// <summary>
	/// The co-op screen (24) — the two share sliders and the profile blocks on both pages.
	///
	/// THIS IS WHAT MAKES THE SCREEN VISIBLE, not only correct. `Engine_UiOwnedBook.Opaqued`
	/// drops the guest's stated opacity for any element the host writes — a value, a texture or
	/// a switch — and falls back to the authored one. The profile blocks are authored at 100 and
	/// held at 0 by the guest's slides (181/173/108), so claiming them here is what lights them.
	/// Their TEXT stays the guest's data; we are stating that they are on screen, not what they
	/// say.
	///
	/// The slider labels are ours outright, because taking them from the guest did not hold: a
	/// run showed `tag='Text' shows='Text'` on all six, the authored placeholder, once the guest
	/// stopped writing them. That is the documented failure of `GuestTextUnits` and the reason
	/// this screen no longer uses it.
	/// </summary>
	private sealed class CoopProvider : Engine_UiPageProvider
	{
		/// <summary>
		/// THE GOLD AND EXPERIENCE SHARE SLIDERS, HIDDEN. Each player keeps their own progression
		/// (Fable 3's rule), so the guest's split (`coopMgr+176`/`+172`) decides nothing and a
		/// slider for it would offer a choice that does not exist. The whole group goes: switched
		/// off, and its subtree stated at 0 so no slide can bring a child back.
		/// </summary>
		private const string Sliders = "Foreground.LeftPage.Sliders";

		/// <summary>
		/// XBOX LIVE VESTIGES, OFF BY DECISION. Reputation and Gamerscore are console-profile
		/// concepts with nothing behind them in a RexNet session, and inventing numbers for them
		/// would be worse than leaving the space empty — so they are switched off outright rather
		/// than left dark by accident.
		/// </summary>
		private static readonly string[] ProfileVestiges =
		{
			"Foreground.LeftPage.ProfileSelector.gamertag.reputation",
			"Foreground.LeftPage.ProfileSelector.gamertag.gamerscore",
			"Foreground.RightPage.ProfileSelector.gamertag.reputation",
			"Foreground.RightPage.ProfileSelector.gamertag.gamerscore",

			// The LEFT page's X prompt is `Select Gamer Profile` — the same console-profile
			// vestige, and there is no profile to select over RexNet. Its glyph goes with it so the
			// row does not sit there orphaned.
			"Foreground.LeftPage.ProfileSelector.Buttons.Title2",
			"Foreground.LeftPage.ProfileSelector.Buttons.icon_button_x2",
		};

		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page)
		{
			// No slider values: the share sliders are hidden (see `Sliders`).
			var values = new Dictionary<string, string>(System.StringComparer.Ordinal);

			// BOTH NAMES, ALWAYS, on both machines: player one on the left page, player two on the
			// right, as the shell knows them, or "Player 1" / "Player 2" until it does — a partner's
			// name arrives with the session, and a couch partner may have none. Writing them is also
			// what lights them: the gamertag block is authored at 100 and slid to 0 by the guest,
			// whose own text there is its stub profile name.
			values["Foreground.LeftPage.ProfileSelector.gamertag.gamertag"] =
				CoopPlayerOneName.Length != 0 ? CoopPlayerOneName : Refii.Text.Engine_OwnWords.Say("REFII_PLAYER_1");
			values["Foreground.RightPage.ProfileSelector.gamertag.gamertag"] =
				CoopPlayerTwoName.Length != 0 ? CoopPlayerTwoName : Refii.Text.Engine_OwnWords.Say("REFII_PLAYER_2");

			// X ON THE RIGHT PAGE IS THE HERO-FROM-SAVE PROMPT.
			//
			// The button is already authored — `icon_button_x` at (307,212) with `Title2` beside it
			// at (281,214), the pairing the two positions make plain; `Title` at (314,256) belongs
			// to `icon_button_y` at (340,254). So this is a relabel, not a new element.
			//
			// The ACTION is not wired yet: loading a visitor's own hero needs the capture and
			// transport stages of docs/coop_native_hero_plan.md. The prompt is placed now so the
			// layout is final and the stages have somewhere to land.
			// The prompt names the DESTINATION, not the current state — the way every other
			// toggle on these screens reads.
			values["Foreground.RightPage.ProfileSelector.Buttons.Title2"] =
				CoopOwnHero ? "Use Henchman" : "Load Hero";

			// EACH PLAYER'S HERO, ON ITS WAY. The shipped Reputation line under the name carries
			// the hero record's progress across the wire, on both pages and both machines.
			values["Foreground.LeftPage.ProfileSelector.gamertag.reputation"] = CoopPlayerOneStatus;
			values["Foreground.RightPage.ProfileSelector.gamertag.reputation"] = CoopPlayerTwoStatus;

			// Player two's save prompt on the host's copy of their page, where the guest writes none.
			if (CoopRemotePlayerTwo)
			{
				values["Foreground.RightPage.buttons.LoadGroup.LoadText"] = "Select Save";
			}

			// The guest-decided captions (pickboxes, the load caption, cancel, connecting), from
			// the screen's own data — the provider's own statements above win.
			foreach ((string key, string text) in Coop(page).Values)
			{
				values.TryAdd(key, text);
			}

			return values;
		}

		private static Engine_UiCoopPage.Statements Coop(in Engine_UiPageContext page) =>
			Engine_UiCoopPage.State(page.Strings, CoopPlayerTwoName, CoopRemotePlayerTwo);

		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page) =>
			Coop(page).Slides;

		/// <summary>
		/// The two `ProfileSelector.Buttons` groups are AUTHORED AT ZERO (`auth=0`, read live),
		/// so nothing that merely drops the guest's opacity can reach them or their children —
		/// the X and Y prompts and the profile titles all inherit it. Stating the number is the
		/// only lever that does.
		/// </summary>
		/// <summary>
		/// NAME THE LEAVES, NOT THE CONTAINER. Opacity does NOT inherit down this tree and the
		/// key match is on a path's TRAILING SEGMENTS, so `…ProfileSelector.Buttons` lights that
		/// one node and nothing under it. Measured live: `ProfileSelector` sat at 1.00 while
		/// every child — the gamertag, the title and the whole button-icon chain — stayed at
		/// 0.00/0.01, which is why this block was invisible however right the text was.
		///
		/// The icon is a chain, not a quad: `icon_button_x` → `Material2270` → `abyx`, and all
		/// three carry their own number. Player two's prompt is the RIGHT page's
		/// `Title2` + `icon_button_x` pair; the left page's is `icon_button_x2` and stays dark,
		/// because the action is player two's.
		/// </summary>
		public IReadOnlyDictionary<string, float> Opacities(in Engine_UiPageContext page)
		{
			Dictionary<string, float> opacities = OwnOpacities();
			foreach ((string key, float value) in Coop(page).Opacities)
			{
				opacities.TryAdd(key, value);
			}

			return opacities;
		}

		private static Dictionary<string, float> OwnOpacities() =>
			new Dictionary<string, float>(System.StringComparer.Ordinal)
			{
				// THE NAME ONLY. Lighting the whole `gamertag` subtree brought the Xbox gamerpic,
				// the gamerscore figure and the five-star rating up with it — none of which this
				// remaster has any source for. The two exact keys light the container and the
				// name text; every sibling stays at its authored 0.01, which is invisible.
				["Foreground.LeftPage.ProfileSelector.gamertag"] = 1f,
				["Foreground.LeftPage.ProfileSelector.gamertag.gamertag"] = 1f,
				["Foreground.RightPage.ProfileSelector.gamertag"] = 1f,
				["Foreground.RightPage.ProfileSelector.gamertag.gamertag"] = 1f,
				// THE ANCESTORS TOO. Opacity accumulates down the tree (`Opacity[i] =
				// parentOpacity * Own[i]`), and the landing probe showed the whole `Buttons`
				// group at own=1.00 but op=0.00: `ProfileSelector` is stated at 0 by the
				// guest's slide, so everything under it drew at nothing however right its
				// own number was. Claiming the container makes the claims below reach the screen.
				["Foreground.RightPage.ProfileSelector"] = 1f,
				["Foreground.RightPage.ProfileSelector.Buttons"] = 1f,
				["Foreground.RightPage.ProfileSelector.Buttons.*"] = 1f,
				["Foreground.LeftPage.ProfileSelector"] = 1f,
				["Foreground.LeftPage.ProfileSelector.gamertag.reputation"] = 1f,
				["Foreground.RightPage.ProfileSelector.gamertag.reputation"] = 1f,
				[Sliders + ".*"] = 0f,

				// THE SAVE PROMPT ON BOTH MACHINES, at the guest's own strength: full, or half once
				// player two has accepted (`sub_82439988`, 0x82439A68).
				["Foreground.RightPage.buttons.LoadGroup"] = CoopPlayerTwoAccepted ? 0.5f : 1f,
				["Foreground.RightPage.buttons.LoadGroup.*"] = 1f,
			};

		/// <summary>
		/// Authored placements this screen cannot use as authored.
		///
		/// The X prompt (`Title2` + `icon_button_x`) is authored ~42 canvas units BELOW the
		/// "Change Save" row — exactly where the `menu` unit draws "Accept A", later, on top. A
		/// live read had the pair lit and drawn and the screen showed nothing there. It goes above
		/// the Y row instead. The two `model_proxy` anchors stay where the file puts them, below
		/// the page: that is where the original stands its 3.5× figures' feet
		/// (Engine_UiFigureStage).
		/// </summary>
		public IReadOnlyDictionary<string, Vector3> Positions(in Engine_UiPageContext page) =>
			new Dictionary<string, Vector3>(System.StringComparer.Ordinal)
			{
				// BETWEEN Y AND B, from the file's own numbers (coopscreen.bgf, verified against the
				// live tree): the bottom block is `RightPage.buttons` at (188,−235) under Foreground
				// 1.3 × RightPage 0.8 (= 1.04); its rows are `LoadGroup` (Y, local (22,3), text at
				// canvas (290,−205)), `CancelGroup` (−243) and `AcceptGroup` (−281) — 38 canvas /
				// 36.5 local apart. The Y row moves up one row and the host's X pair (authored
				// under `ProfileSelector.Buttons`, origin (63.4,329.3), scale 0.832) takes its
				// canvas spot: local = (canvas − origin) / 0.832. THE CHANNEL IS PARENT-LOCAL.
				// Y stays authored (canvas −205; the pickbox rows end at ≈ −162). The X pair takes
				// Cancel's row (−243 → local −688) and Cancel and Accept move down one row each.
				["Foreground.RightPage.ProfileSelector.Buttons.Title2"] = new(272f, -688f, float.NaN),
				["Foreground.RightPage.ProfileSelector.Buttons.icon_button_x"] = new(306f, -688f, float.NaN),
				["Foreground.RightPage.buttons.CancelGroup"] = new(float.NaN, -36.5f, float.NaN),
				["Foreground.RightPage.buttons.AcceptGroup"] = new(float.NaN, -36.5f, float.NaN),

				// The right frame reaches the 16:9 edge: coopscreen.bgf's frames end at +630 / −672
				// canvas (the console's overscan hid the shortfall), so its group (parent scale
				// 1.04) moves out by the shortfall. Wider than 16:9 is the template's wide layout.
				["Foreground.RightFrame2.Group"] = new(
					263f + System.MathF.Max(0f, Engine_UiSceneBuilder.DesignHalfWidth - 629.7f) / 1.04f,
					float.NaN, float.NaN),
			};

		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page)
		{
			var active = new Dictionary<string, bool>(System.StringComparer.Ordinal);

			// OFF: the profile vestiges this remaster has no source for.
			foreach (string side in new[] { "LeftPage", "RightPage" })
			{
				active[$"Foreground.{side}.ProfileSelector.gamertag.reputation"] = false;
				active[$"Foreground.{side}.ProfileSelector.gamertag.gamerscore"] = false;
				active[$"Foreground.{side}.ProfileSelector.gamertag.star"] = false;

				// The guest's stub profile lights these itself — "18471" and the Xbox gamerpic —
				// and this remaster has no source for either.
				active[$"Foreground.{side}.ProfileSelector.gamertag.score"] = false;
				active[$"Foreground.{side}.ProfileSelector.gamertag.gamerpic"] = false;
			}

			active[Sliders] = false;

			// OFF: the "Player 1 ▲  Player 2 ▲" labels under the left page. Every page carries its
			// player's name at the top, so the arrows pointing the pages out say nothing more.
			active["Foreground.LeftPage.PlayerArrowLabels"] = false;
			active["Foreground.LeftPage.ProfileSelector.Buttons.Title2"] = false;
			active["Foreground.LeftPage.ProfileSelector.Buttons.icon_button_x2"] = false;

			// OFF: "Use Player 1's Heroes" — the original's Y for a couch partner with no profile of
			// their own, borrowing player one's saves. Every player picks their own save here
			// (Select Save), and plays their own hero or a henchman.
			active["Foreground.RightPage.ProfileSelector.Buttons.Title"] = false;
			active["Foreground.RightPage.ProfileSelector.Buttons.icon_button_y"] = false;

			// Player two's X prompt, only once their hero is on both machines — until then the
			// status line under their name says how far it has got. The draw gate is `own && (slid
			// || forced)` and this pair sits on a slide the guest keeps off; a host switch is the
			// `forced` half, both ways.
			active["Foreground.RightPage.ProfileSelector.Buttons"] = true;
			active["Foreground.RightPage.ProfileSelector.Buttons.Title2"] = CoopHeroAvailable;
			active["Foreground.RightPage.ProfileSelector.Buttons.icon_button_x"] = CoopHeroAvailable;

			// The status lines, while there is something to say.
			active["Foreground.LeftPage.ProfileSelector.gamertag.reputation"] = CoopPlayerOneStatus.Length != 0;
			active["Foreground.RightPage.ProfileSelector.gamertag.reputation"] = CoopPlayerTwoStatus.Length != 0;

			// Player two's save prompt on every machine; the henchman rows only for a henchman.
			active["Foreground.RightPage.buttons.LoadGroup"] = true;
			active["Foreground.RightPage.Pickbox"] = !CoopOwnHero;

			// Connecting hides the right page's selector, pickboxes and buttons (`sub_82437810`).
			foreach ((string key, bool on) in Coop(page).Active)
			{
				active[key] = on && (!active.TryGetValue(key, out bool own) || own);
			}

			// The save picker's cards are drawn by the shared Load Game fan
			// (Engine_FrontEndSaveCards), as on the main menu; the guest's own copy stays dark.
			active["loadgame"] = false;
			return active;
		}

	}

	private sealed class AbilitiesProvider : Engine_UiPageProvider
	{
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiAbilityPage.Values(
				page.Row, page.Vitals.AbilityLevels, page.Vitals.ExperienceAvailable,
				page.Hero.SpellDamage, page.Strings,
				page.Row is { } row && Engine_AbilityDiscardGates.IsOpenNow(
					Engine_AbilityDiscardGates.RowId(row.IdHigh, row.IdLow)));

		/// <summary>Discard at 100 when the guest's gate is open for the row, at 50 when shut.</summary>
		public IReadOnlyDictionary<string, float> Opacities(in Engine_UiPageContext page) =>
			Engine_UiAbilityPage.DiscardOpacities(page.Row, page.Row is { } row &&
				Engine_AbilityDiscardGates.IsOpenNow(Engine_AbilityDiscardGates.RowId(row.IdHigh, row.IdLow)));

		public IReadOnlyDictionary<string, uint> Textures(in Engine_UiPageContext page) =>
			Engine_UiAbilityPage.Textures(page.Row);

		/// <summary>
		/// The abilities book authors `Page1`, `Page2` and `Page4` on one leaf. A CATEGORY row
		/// (`Strength`) shows the category page; an ability under it pops its own page out.
		/// </summary>
		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page)
		{
			// THE SPELL BLADE LIVES ON PAGE 4, and opening it is a PAGE PUSH.
			//
			// `sub_826C2480` does five things at once, and this was the missing one:
			// `sub_82990E48(screen+48, presentation, 3)` pushes book page index 3 — `Page4` —
			// alongside hiding the row list and routing input to the widget. Godot did the
			// first two and not the third, so activating `Spell Selector` took the list away
			// and left the page it was replaced by switched OFF: an empty leaf with the orbs
			// still on it, which is exactly what the screen showed.
			//
			// The blade's own drawing and input already work; it simply had no page to stand on.
			string[] pages = PagesOf(page, AbilityPageElements);
			if (Fable2Bridge.AbilitiesAssignmentOpen())
			{
				var blade = new Dictionary<string, bool>(pages.Length);
				foreach (string path in pages)
				{
					blade[path] = path.EndsWith(BladePage, System.StringComparison.Ordinal);
				}

				return blade;
			}

			return BookPages(BookPage, page.Row, pages);
		}

		/// <summary>The page the spell blade is authored on — `sub_826C2480` pushes index 3.</summary>
		private const string BladePage = "Page4";

		/// <summary>
		/// THE PAGE THAT IS UP SITS IN THE VISIBLE SLOT — the same parking as the item books:
		/// `abilitiesscreen.bgf` authors `Page1`, `Page2` and `Page4` on one leaf, and the
		/// `Intro` slide of `Page2` and of `Page4` writes `Contents position.x = 430` (off the
		/// 4:3 canvas) while their `Outro` writes 0; `Page1`'s writes 0 both ways. The guest
		/// played `Outro` on its own highlight edge, which no longer fires under an owned cursor,
		/// so a page flipped to (or the blade pushed) stayed parked off-screen. Godot owns the
		/// page: the one that is up is placed at the `Outro` numbers; the others are switched
		/// off by <see cref="Active"/>.
		/// </summary>
		public IReadOnlyDictionary<string, Vector3> Positions(in Engine_UiPageContext page)
		{
			string[] pages = PagesOf(page, AbilityPageElements);
			string? up = null;
			if (Fable2Bridge.AbilitiesAssignmentOpen())
			{
				foreach (string path in pages)
				{
					if (path.EndsWith(BladePage, System.StringComparison.Ordinal))
					{
						up = path;
					}
				}
			}
			else if (BookPage >= 0 && BookPage < pages.Length)
			{
				up = pages[BookPage];
			}

			if (up is null)
			{
				return NoPositions;
			}

			var placed = new Dictionary<string, Vector3>(2) { [$"{up}.Contents"] = Vector3.Zero };
			if (up.EndsWith(BladePage, System.StringComparison.Ordinal))
			{
				// THE INSTRUCTIONS SIT UNDER THE WILL'S NAME. The blade's own text is authored
				// at y -109, and the widget's name plate (`WillName`, under the bottom slot) ends
				// at -134 on the page, so the authored first line ran under the plate. Godot
				// owns the placement: the text starts at -140, three wrapped lines reach -206,
				// and the prompt band at -230 is untouched.
				placed[BladeInstructions] = new Vector3(float.NaN, BladeInstructionsY, float.NaN);
			}

			return placed;
		}

		/// <summary>`Page4.Contents.Blade.Same` (ordinal 304) — the page's instructions text, not the A prompt's `Blade.Buttons.Same`.</summary>
		private const string BladeInstructions = "Foreground.Book.Pages.Page4.Contents.Blade.Same";

		private const float BladeInstructionsY = -140f;

		/// <summary>Which experience orb is lit — see <see cref="Engine_UiAbilityPage.Slides"/>.</summary>
		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page) =>
			Engine_UiAbilityPage.Slides(
				page.Row, page.Vitals.AbilityLevels, page.Vitals.ExperienceAvailable);
	}

	private sealed class LogbookProvider : Engine_UiPageProvider
	{
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiLogbookPage.Values(
				page.Row, page.Hero, page.Quests, page.Strings, Engine_UiLogbookPage.OtherPlayer);

		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page) =>
			Engine_UiLogbookPage.Active(page.Row);

		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page) =>
			Engine_UiLogbookPage.Slides(page.Row, page.Hero);

		public IReadOnlyDictionary<string, Vector3> Positions(in Engine_UiPageContext page) =>
			Engine_UiLogbookPage.Positions(page.Row, page.Hero);

		public IReadOnlyDictionary<string, uint> Textures(in Engine_UiPageContext page) =>
			Engine_UiLogbookPage.Textures(page.Row);
	}

	/// <summary>
	/// The achievements screen — see <see cref="Engine_UiAchievementPage"/>. It states NO slides
	/// on purpose; the page class says why.
	/// </summary>
	private sealed class MultiplayerProvider : Engine_UiPageProvider
	{
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiMultiplayerPage.Values(page.Row);

		public IReadOnlyDictionary<string, uint> Textures(in Engine_UiPageContext page) =>
			Engine_UiMultiplayerPage.Textures(page.Row);

		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page) =>
			Engine_UiMultiplayerPage.Active(page.Row);
	}


	private sealed class AchievementsProvider : Engine_UiPageProvider
	{
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiAchievementPage.Values(page.Row);

		public IReadOnlyDictionary<string, uint> Textures(in Engine_UiPageContext page) =>
			Engine_UiAchievementPage.Textures(page.Row);

		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page) =>
			Engine_UiAchievementPage.Active(page.Row);
	}

	/// <summary>
	/// Screen 28 — see <see cref="Engine_UiRealEstatePage"/>. NOT cursor-driven: the left column
	/// is a list of commands and there is one building, so the row is ignored entirely and both
	/// pages describe the same record.
	/// </summary>
	private sealed class RealEstateProvider : Engine_UiPageProvider
	{
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiRealEstatePage.StatedValues();

		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page) =>
			Engine_UiRealEstatePage.StatedSlides();

		/// <summary>The page that is up — the host's page turn, as on the item books. The guest
		/// parks the other page's `Contents` at x 430; a switched-off page needs no parking.</summary>
		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page) =>
			BookPagesByOpacity(BookPage, RealEstatePageElements);

		public IReadOnlyDictionary<string, Vector3> Positions(in Engine_UiPageContext page) =>
			BookPage >= 0 && BookPage < RealEstatePageElements.Length
				? new Dictionary<string, Vector3>(1) { [$"{RealEstatePageElements[BookPage]}.Contents"] = Vector3.Zero }
				: NoPositions;
	}

	private static readonly string[] RealEstatePageElements =
	{
		"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
	};

	/// <summary>
	/// The minigame tally (screen 11), filled whenever it draws from what `TurnOnMiniGameComplete` and
	/// `AddAmountToMiniGameComplete` state (<see cref="Engine_UiGuiWidgets"/>). `Title` and
	/// `Select.Select` bind `MiniGameTitle` / `MiniGameButton`; `Amounts.Amount{n}` toggles on
	/// `MiniGameAmount{n}`, slide 2 `On` for a claimed row and 1 `Off` for a free one
	/// (`lhGuiRegistry_SetToggle` 0x823FE898 / disc 0x8257B458 enters 2 for on, 1 for off), its `Name`
	/// the row's label and its `Amount` the `%d` of what the row shows (`sub_823CD790` 0x823CD7A4,
	/// disc `sub_8258C6C8` 0x8258C6DC).
	/// </summary>
	private sealed class MiniGameProvider : Engine_UiPageProvider
	{
		public bool FillsOnDraw => true;

		private static string RowPath(int row, string field) =>
			"Amounts.Amount" + (row + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + field;

		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page)
		{
			if (Widgets is not { } widgets)
			{
				return NoValues;
			}

			var values = new Dictionary<string, string>
			{
				["Title"] = widgets.MiniGameTitle,
				["Select.Select"] = widgets.MiniGameButton,
			};
			IReadOnlyList<Engine_UiGuiWidgets.MiniGameRow> rows = widgets.MiniGame;
			for (int i = 0; i < Engine_UiGuiWidgets.MiniGameRows; ++i)
			{
				bool claimed = i < rows.Count;
				values[RowPath(i, ".Name")] = claimed ? rows[i].Label : string.Empty;
				values[RowPath(i, ".Amount")] = claimed
					? rows[i].Displayed.ToString(System.Globalization.CultureInfo.InvariantCulture)
					: string.Empty;
			}

			return values;
		}

		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page)
		{
			if (Widgets is not { } widgets)
			{
				return NoSlides;
			}

			var slides = new Dictionary<string, int>();
			for (int i = 0; i < Engine_UiGuiWidgets.MiniGameRows; ++i)
			{
				slides[RowPath(i, string.Empty)] = i < widgets.MiniGame.Count ? 2 : 1;
			}

			return slides;
		}
	}

	/// <summary>The quest box (screen 5): a box page, filled whenever it draws, from the box's own
	/// Open — see <see cref="Engine_UiQuestBoxPage"/>.</summary>
	private sealed class QuestBoxProvider : Engine_UiPageProvider
	{
		public bool FillsOnDraw => true;

		private static Engine_UiQuestBoxPage.Statement? Stated(in Engine_UiPageContext page) =>
			Engine_UiQuestBoxPage.Of(
				Engine_UiDisplayBoxes.ShowingOf(Engine_UiDisplayBoxes.Quest), page.Strings);

		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiQuestBoxPage.Values(Stated(page));

		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page) =>
			Engine_UiQuestBoxPage.Active(Stated(page));

		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page) =>
			Engine_UiQuestBoxPage.Slides(Stated(page));
	}

	/// <summary>
	/// The display box's info card (screen 7, `Paper`): the hold-to-accept card (class 0x820EBF1C —
	/// a style-0 `DisplayInfoBoxParams`, photo mode's upload notice), stated by its own Open
	/// `sub_8299E490` (`@DisplayBox.Open` kind 8, display_box_hooks.cpp), which writes `Paper.Text`
	/// (`box+8`), `Paper.Title` (the tag `+32`, resolved), `Paper.Accept` (the tag `+40`) shown iff
	/// ShowA `+36` with `Paper.HoldButton` to match and `HoldMeter` iff HoldA `+38`, `Paper.Info`
	/// (the tag `+44`) shown iff ShowY `+37` with `Paper.abyx`, and the root on slide 7. Filled
	/// while that box is the one showing.
	/// </summary>
	private sealed class InfoCardProvider : Engine_UiPageProvider
	{
		public bool FillsOnDraw => true;

		/// <summary>The card's stated fields: title tag, ShowA, ShowY, HoldA, accept tag, help tag.</summary>
		private static Engine_UiStatedBoxes.Box? Card =>
			Engine_UiDisplayBoxes.ShowingOf(Engine_UiDisplayBoxes.AcceptHold);

		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page)
		{
			if (Card is not { } card)
			{
				return NoValues;
			}

			return new Dictionary<string, string>
			{
				["Paper.Text"] = card.Body,
				["Paper.Title"] = Engine_UiGuiText.Resolve(card.Field(0), page.Strings),
				["Paper.Accept"] = Engine_UiGuiText.Resolve(card.Field(4), page.Strings),
				["Paper.Info"] = Engine_UiGuiText.Resolve(card.Field(5), page.Strings),
			};
		}

		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page)
		{
			if (Card is not { } card)
			{
				return NoActive;
			}

			return new Dictionary<string, bool>
			{
				["Paper.Accept"] = card.Flag(1),
				["Paper.Info"] = card.Flag(2),
				["Paper.abyx"] = card.Flag(2),
			};
		}

		public IReadOnlyDictionary<string, float> Opacities(in Engine_UiPageContext page) =>
			Card is { } card
				? new Dictionary<string, float>
				{
					["Paper.HoldButton"] = card.Flag(1) ? 1f : 0f,
					["Paper.HoldButton.HoldMeter"] = card.Flag(3) ? 1f : 0f,
				}
				: NoOpacities;

		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page) =>
			Card is null ? NoSlides : new Dictionary<string, int> { ["#0"] = 7 };
	}

	private sealed class CrimeProvider : Engine_UiPageProvider
	{
		/// <summary>The pages' text. The purse is the guest's told write (Engine_UiScreenTemplate.Purse).</summary>
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiCrimePage.Values(page.Crime, page.Strings);

		/// <summary>
		/// THE PAGES PARK BY SLIDE (`sub_82990B30` 0x82990c78..d0): for the book's second and
		/// third page, slide 2 when it is the one up and slide 1 otherwise; the first page owns
		/// none.
		/// </summary>
		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page)
		{
			string up = page.Row?.Name ?? string.Empty;
			return new Dictionary<string, int>
			{
				["Foreground.Book.Pages.Page2"] = up == "ResistArrest" ? 2 : 1,
				["Foreground.Book.Pages.Page3"] = up == "CommunityService" ? 2 : 1,
			};
		}

		/// <summary>
		/// `crimescreen.bgf` authors THREE pages of identical shape, one per row, and
		/// `Engine_UiCrimePage` already writes all three texts — nothing chose between them, so
		/// the page shown was whichever the guest last left.
		///
		/// The mapping is taken from what each page SAYS, not from row order: Page1 carries the
		/// fine, and Page3's terms branch is gated on the same flag that gates the
		/// CommunityService row, which leaves the bare-charges Page2 for ResistArrest.
		/// </summary>
		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page)
		{
			Dictionary<string, bool> active = BookPagesByOpacity(
				page.Row?.Name switch
				{
					"PayFine" => 0,
					"ResistArrest" => 1,
					"CommunityService" => 2,
					_ => 0,
				},
				PagesOf(page, CrimePageElements));

			// The A prompt on the pay page iff the fine can be paid, on the service page iff
			// service is available (`sub_826A90D8`: screen+0x2C / +0x2D).
			active["Page1.Contents.Select.icon_button_a"] = page.Crime.CanPayFine;
			active["Page1.Contents.Select.Select"] = page.Crime.CanPayFine;
			active["Page3.Contents.Select.icon_button_a"] = page.Crime.CanServe;
			active["Page3.Contents.Select.Select"] = page.Crime.CanServe;
			return active;
		}
	}

	private sealed class WorldMapProvider : Engine_UiPageProvider
	{
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiMinimapPage.Merge(
				Engine_UiWorldMapPage.Values(page.Row, page.Strings),
				Engine_UiMinimapPage.Values(page.Row, page.WorldMap, page.Strings));

		public IReadOnlyDictionary<string, uint> Textures(in Engine_UiPageContext page) =>
			Engine_UiMinimapPage.Textures(page.Row, page.WorldMap);

		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page) =>
			Engine_UiMinimapPage.Merge(
				Engine_UiWorldMapPage.Active(page.Row),
				Engine_UiMinimapPage.Active(page.Row, page.WorldMap));

		public IReadOnlyDictionary<string, float> Rotations(in Engine_UiPageContext page) =>
			Engine_UiMinimapPage.Rotations(page.Row, page.WorldMap);

		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page) =>
			Engine_UiWorldMapPage.Slides(page.Row);

		/// <summary>Name the highlighted region so the pump's next minimap keys are this cursor's.</summary>
		public void Prepare(in Engine_UiPageContext page)
		{
			int region = Engine_UiWorldMapPage.RegionOf(page.Row);
			if (region >= 0)
			{
				Fable2Bridge.SetWorldMapRegion((uint)region);
			}

			// The property under the cursor, for its sign's numbers (0 when the row is not one).
			ulong property = Engine_UiPropertySource.UidOf(page.Row?.Name);
			if (property != _propertyAsked)
			{
				_propertyAsked = property;
				Fable2Bridge.RequestPropertyEstate(property);
			}

			// Change-keyed: which row Prepare saw and what it asked for. A blank region page
			// cannot say whether the request fired; this can.
			string saw = page.Row is { } row
				? $"'{row.Name}' type={row.EventType} id={(uint)row.IdHigh << 16 | (uint)row.IdLow & 0xFFFFu}"
				: "no row";
			if (saw != _preparedFor)
			{
				_preparedFor = saw;
				Godot.GD.Print($"[worldmap-prepare] {saw} -> region {region}");
			}
		}

		private string _preparedFor = string.Empty;

		private ulong _propertyAsked;
	}

	private sealed class OptionsProvider : Engine_UiPageProvider
	{
		/// <summary>
		/// The options screen is not a two-page book — `optionsscreen.bgf` authors FOUR pages,
		/// one per row, and the row picks it. Pinned by this file's own field constants rather
		/// than by row order, which differs: `Page2.Contents.pickbox1` is the invert-camera
		/// control, `Page3.Contents.slider1` is Gamma, and `Page4`'s three sliders are the
		/// volumes — so Game is Page1, Controls Page2, Video Page3, Audio Page4, while the rows
		/// read Game, Video, Controls, Audio.
		///
		/// Nothing selected between them before, so the right page was whatever the guest last
		/// left up — which on this screen is a page of the wrong category.
		/// </summary>
		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page)
		{
			string[] pages = PagesOf(page, OptionsPageElements);
			IReadOnlyDictionary<string, bool> shown = BookPagesByOpacity(PageIndexOf(PickedRow(page)), pages);
			if (!GameUi.PauseIsPlayerTwos() || pages.Length == 0)
			{
				return shown;
			}

			// PLAYER TWO'S GAME PAGE IS SUBTITLES ALONE: the page's build `sub_829BE130` makes
			// widgets 1..4 only while the pause root's player (`FindScreenHandler(gui, 0) + 4`) is
			// zero (0x829BE1AC..0x829BE1B8; disc `sub_828D7470` 0x828D749C..0x828D74BC), so player
			// two's page cursor has one row to stand on.
			var active = new Dictionary<string, bool>(shown);
			for (int row = 1; row < OptionWidgets[0].Length; ++row)
			{
				active[$"{pages[0]}.{OptionWidgets[0][row]}"] = false;
			}

			return active;
		}

		/// <summary>
		/// The row that picks the page: the highlighted list row, or — once code 36 has opened
		/// the page level and the list is dormant with no highlight — the page the pending
		/// tracker entered. Without the second half the page level pinned Page1 while Page2's
		/// contents stayed parked at x 430, off the leaf.
		/// </summary>
		private static string? PickedRow(in Engine_UiPageContext page) =>
			Engine_UiOptionsPending.Open ? Engine_UiOptionsPending.Page : page.Row?.Name;

		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
			Engine_UiOptionsPage.Values(page.Sliders, page.Strings, page.OptionsPageOpen);

		public IReadOnlyDictionary<string, Vector3> Positions(in Engine_UiPageContext page)
		{
			// THE PAGE'S CONTENTS SIT ON THE LEAF. `optionsscreen.bgf` parks each page's
			// `Contents` for the fold (Page1/2 at 0, Page3/4 at -400) and the stack itself at
			// 405 — the row picks the page, so the picked page's contents are pinned where the
			// pinned stack shows them, as the item books pin theirs.
			var placed = new Dictionary<string, Vector3>(Engine_UiOptionsPage.Positions(page.Sliders));
			string[] pages = PagesOf(page, OptionsPageElements);
			int index = PageIndexOf(PickedRow(page));
			if (index >= 0 && index < pages.Length)
			{
				placed[$"{pages[index]}.Contents"] = Vector3.Zero;
			}

			return placed;
		}

		private static int PageIndexOf(string? row) => row switch
		{
			"Game" => 0,
			"Controls" => 1,
			"Video" => 2,
			"Audio" => 3,
			_ => 0,
		};

		/// <summary>
		/// Each page's value widgets, top to bottom — the order the guest's own page cursor
		/// (`page+40`) counts them: Game's five pickboxes; Controls' invert-camera pickbox;
		/// Video's gamma slider; Audio's three volume sliders and the speaker pickbox.
		/// </summary>
		private static readonly string[][] OptionWidgets =
		{
			new[]
			{
				"Contents.Options.Subtitles_pickbox", "Contents.Options.BreadcrumbSize_pickbox",
				"Contents.Options.TutorialBoxes_pickbox", "Contents.Options.MultiplayerOrbs_pickbox",
				"Contents.Options.Auto_joinable_pickbox",
			},
			new[] { "Contents.pickbox1" },
			new[] { "Contents.slider1" },
			new[] { "Contents.slider1", "Contents.slider2", "Contents.slider3", "Contents.pickbox1" },
		};

		/// <summary>
		/// THE PAGE CURSOR'S ROW IS `selected` (slide 2), every other row `unselected` (1) — the
		/// widgets' own slides, whose entries show the selection frame on the cursor's row alone.
		/// The cursor is the page's (the pending tracker's row, which follows the guest's
		/// `page+40`), the first row while the list level is up — as the guest leaves it.
		/// A host options page draws its own rows.
		/// </summary>
		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page)
		{
			if (Engine_UiHostOptionsPage.Open)
			{
				return NoSlides;
			}

			string[] pages = PagesOf(page, OptionsPageElements);
			int index = PageIndexOf(PickedRow(page));
			if (index < 0 || index >= pages.Length || index >= OptionWidgets.Length)
			{
				return NoSlides;
			}

			int cursor = Engine_UiOptionsPending.Open ? Engine_UiOptionsPending.Row : 0;
			var slides = new Dictionary<string, int>(OptionWidgets[index].Length);
			for (int row = 0; row < OptionWidgets[index].Length; ++row)
			{
				slides[$"{pages[index]}.{OptionWidgets[index][row]}"] = row == cursor ? 2 : 1;
			}

			return slides;
		}

		public bool NeedsOptionSliders => true;
	}

	/// <summary>
	/// Items, clothing, gift giving, the shop, furniture and decoration: five ids over three
	/// `.bgf` files, one skeleton, one provider. The shop's star rating is the one thing only it
	/// has, keyed on the row rather than the screen.
	/// </summary>
	private sealed class ItemBookProvider : Engine_UiPageProvider
	{
		/// <summary>The item's lines; on the decoration book also the list title, which its enter
		/// writes once (`sub_826B6FE8` 0x826b70b8..e8: `GUI_SCREEN_TITLE_DECORATION`).</summary>
		public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page)
		{
			// Upgrade furniture: a row is a LEVEL, not an item — its pages are what the level's
			// two fills wrote (Engine_UiFurniturePage), with the `Done` prompt both page inits write
			// (`sub_8299BF48`, `sub_8299CEA8`). The purse is the guest's told write.
			if (page.ScreenId == Engine_UiFurniturePage.ScreenId)
			{
				var furniture = new Dictionary<string, string>(
					Engine_UiFurniturePage.State(page.Row)?.Values ?? new Dictionary<string, string>());
				string done = page.Strings is { } doneBook && doneBook.TryGetString("GUI_DONE", out string caption) ? caption : string.Empty;
				furniture["Page1.Contents.PageButtons.B.B.Text"] = done;
				furniture["Page2.Contents.PageButtons.B.B.Text"] = done;
				return furniture;
			}

			// Items, clothing and gift: their own fills' texts for the row (Engine_UiItemPage.StatedValues).
			IReadOnlyDictionary<string, string> lines = Engine_UiItemPage.StatesPages(page.ScreenId)
				? Engine_UiItemPage.StatedValues(page.ScreenId, page.Row)
				: Engine_UiItemPage.Values(page.Row, page.Strings);

			// The shop's front page: what its fill wrote (Engine_UiShopFrontPage).
			if (page.ScreenId == ShopScreenId && Engine_UiShopFrontPage.State() is { } front)
			{
				var shop = new Dictionary<string, string>(lines);
				foreach ((string key, string text) in front.Values)
				{
					shop[key] = text;
				}

				return shop;
			}

			if (page.ScreenId != DecorationScreenId)
			{
				return lines;
			}

			var values = new Dictionary<string, string>(lines);
			values["ExpandableMenu.Title.Group.Title"] =
				page.Strings is { } book && book.TryGetString("GUI_SCREEN_TITLE_DECORATION", out string title)
					? title
					: string.Empty;
			return values;
		}

		/// <summary>
		/// The item's picture; on the GIFT book a gold row (type 7) shows the bag
		/// (`sub_829AA588` 0x829aa738: `Art\GUI\WheelIcons\Items\icon_bag.tex`).
		/// </summary>
		public IReadOnlyDictionary<string, uint> Textures(in Engine_UiPageContext page) =>
			page.ScreenId == GiftScreenId && page.Row?.EventType == GiftGoldEventType
				? GoldBag
				: page.ScreenId == Engine_UiFurniturePage.ScreenId
					? NoTextures
					: Engine_UiItemPage.Textures(page.Row);

		private static readonly IReadOnlyDictionary<string, uint> GoldBag =
			new Dictionary<string, uint>
			{
				["Contents.ItemImage"] = Refii.Core.Engine_GameHash.ResourcePath(@"Art\GUI\WheelIcons\Items\icon_bag.tex"),
			};

		/// <summary>Elements the guest's page refresh switches by the row, per book:
		/// <list type="bullet">
		/// <item>items (15): the A prompt on both pages shows iff the item is
		/// `UsableFromInventory` (`sub_827649C8`; 0x829b50b0, 0x829b6c58).</item>
		/// <item>gift (25): a gold row hides the value lines and the stars (0x829aa008..30).</item>
		/// <item>decoration (27): the picture is hidden on every refresh (0x8299c4dc); the value
		/// lines and stars are hidden at enter and never shown (0x8299cf40..6c).</item>
		/// </list></summary>
		private static void RowSwitches(Dictionary<string, bool> active, in Engine_UiPageContext page)
		{
			// Items, clothing and gift: what their own fills showed and hid for the row — the A prompt
			// (`sub_829B4EB8` by `sub_827649C8`, 0x829B5084..0x829B5114), the dye swatch, the value lines, the dye
			// footer's parts, the Gold row's.
			if (Engine_UiItemPage.Stated(page.ScreenId, page.Row) is { } stated)
			{
				foreach ((string path, bool on) in stated.Shown)
				{
					active[path] = on;
				}

				return;
			}

			switch (page.ScreenId)
			{
				case GiftScreenId when page.Row?.EventType == GiftGoldEventType:
				case DecorationScreenId:
				case Engine_UiFurniturePage.ScreenId:
					active["Contents.ItemValue"] = false;
					active["Contents.ItemValueName"] = false;
					active["Contents.StarRating"] = false;
					if (page.ScreenId == DecorationScreenId)
					{
						active["Page1.Contents.ItemImage"] = false;
					}

					break;
			}
		}

		/// <summary>The dye swatch on the items book: shown and tinted to the dye's colour for a
		/// dye (0x829b53dc, 0x829b5418), at rest (hidden) otherwise.</summary>
		public IReadOnlyDictionary<string, Color> Tints(in Engine_UiPageContext page) =>
			Engine_UiItemPage.Stated(page.ScreenId, page.Row) is { } stated
				? stated.Tints
				: page.ScreenId == Engine_UiFurniturePage.ScreenId &&
					Engine_UiFurniturePage.State(page.Row) is { } furniture
						? furniture.Tints
						: NoTints;

		private const string DyeSwatch = "Foreground.Book.Pages.Page1.Contents.DyeSwatch";

		/// <summary>The page that is up, plus the star strip.</summary>
		public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page)
		{
			Dictionary<string, bool> active = BookPages(
				BookPage, page.Row,
				page.ScreenId == ShopScreenId ? ShopPageElements : BookPageElements);
			AddItemStars(active, page.Row);
			RowSwitches(active, page);

			// THE SHOP'S FRONT PAGE IS ITS OWN UNIT. `Foreground.FrontPage` (name, owner,
			// keeper, discount, wealth) is not one of the book's pages; the guest shows it on
			// a category row (type 301, the front page) and hides it on an item. Without the
			// switch it stayed drawn under every item page.
			if (page.ScreenId == ShopScreenId)
			{
				active["Foreground.FrontPage"] =
					page.Row is null ||
					page.Row.EventType == (int)Engine_UiMenuConstants.EventType.Submenu;
				Engine_UiItemPage.ShopStatsActive(active, ShopPageElements[ShopStatsPage]);
				if (Engine_UiShopFrontPage.State() is { } front)
				{
					foreach ((string key, bool on) in front.Active)
					{
						active[key] = on;
					}
				}
			}

			return active;
		}

		/// <summary>
		/// THE SHOP'S FRONT PAGE STANDS ON PARCHMENT. `HidePages` slides the page stack off to
		/// x 405 and leaves `Foreground.FrontPage` — text and a frame strip, no paper of its own.
		/// The paper authored under it is `Foreground.PageTextureFar` (canvas z 655), which our
		/// depth-tested quads put BEHIND the opaque, screen-wide `Layer.Background` cloth (BC1,
		/// z 1220), so the shop's name and owner read on green. `PageTextureNear` is the same
		/// page-sized sheet at the front page's own place (z 1245, between the cloth and the
		/// frame); the root `Intro` sweeps it in and fades it out. The remaster holds it up while
		/// the front page is, the way the item pages keep their `ModelHider` parchment.
		/// </summary>
		public IReadOnlyDictionary<string, float> Opacities(in Engine_UiPageContext page)
		{
			bool folder = page.Row is null ||
				page.Row.EventType == (int)Engine_UiMenuConstants.EventType.Submenu;
			if (page.ScreenId == ShopScreenId)
			{
				return folder ? ShopFrontPageParchment : NoOpacities;
			}

			// Items, clothing, gift: what their fills wrote (the dye footer's Select / icon parts at 50 or 100,
			// `sub_82426140` 0x829B5F4C..; the worn Select on page 2, 0x829B7938..), over the book's own resting
			// opacities — the swatch held up when the fill tinted it, the clothing footer's frames.
			if (Engine_UiItemPage.Stated(page.ScreenId, page.Row) is { } stated)
			{
				var opacities = new Dictionary<string, float>(
					page.ScreenId == EquipmentScreenId && !folder ? EquipmentFooter : NoOpacities, System.StringComparer.Ordinal);
				if (stated.Tints.Count != 0)
				{
					opacities[DyeSwatch] = 1f;
				}

				foreach ((string path, float opacity) in stated.Opacities)
				{
					opacities[path] = opacity;
				}

				return opacities;
			}

			if (page.ScreenId == Engine_UiFurniturePage.ScreenId)
			{
				return Engine_UiFurniturePage.State(page.Row)?.Opacities ?? NoOpacities;
			}

			return page.ScreenId == EquipmentScreenId && !folder ? EquipmentFooter : NoOpacities;
		}

		/// <summary>
		/// THE CLOTHING PAGE'S FOOTER — the `1 / 2` page turn and the `Select` prompt. Its slide
		/// switches every piece on, but the guest's Page1 filler (`sub_829B6310`) blanks the page
		/// through opacity whenever ITS cursor sits on a category row (id 301), and Godot owns
		/// the cursor, so that blank is never lifted: the footer stood switched on at opacity 0
		/// on every item. Released, not restated — the pieces keep their authored numbers (the
		/// arrows at 50, the stick frames at 0).
		/// </summary>
		private static readonly IReadOnlyDictionary<string, float> EquipmentFooter =
			new Dictionary<string, float>(System.StringComparer.Ordinal)
			{
				["Foreground.Book.Pages.Page1.Contents.Footer.*"] = float.NaN,
			};

		private static readonly IReadOnlyDictionary<string, float> ShopFrontPageParchment =
			new Dictionary<string, float>(System.StringComparer.Ordinal)
			{
				["Foreground.PageTextureNear"] = 1f,
			};

		/// <summary>
		/// THE PAGE THAT IS UP SITS IN THE VISIBLE SLOT. `giftgivingscreen.bgf` authors the two
		/// right-hand pages on one leaf and PARKS the second: `Page2`'s own `Intro` slide writes
		/// `Page2.Contents position.x = 430` (past the 4:3 canvas) and its `Outro` writes
		/// `x = 0`; `Page1`'s `Intro`/`Outro` both write 0. The guest played `Outro` on a
		/// highlight-change edge keyed off ITS Lua cursor, which stops moving once Godot owns
		/// the list — so `Page2` stayed parked at 430 and every description, stat and value on
		/// it rendered off the right edge of the screen (mesh built, visible, full opacity, at
		/// x 637). Godot owns the page, so the page that is up is placed at the slide's own
		/// numbers here; the other page is switched off by <see cref="Active"/>, so where it
		/// parks is immaterial.
		/// </summary>
		public IReadOnlyDictionary<string, Vector3> Positions(in Engine_UiPageContext page)
		{
			string[] pages = page.ScreenId == ShopScreenId ? ShopPageElements : BookPageElements;
			if (BookPage < 0 || BookPage >= pages.Length)
			{
				return NoPositions;
			}

			var positions = new Dictionary<string, Vector3>(5)
			{
				[$"{pages[BookPage]}.Contents"] = Vector3.Zero,

				// THE RING OVER THE PAGE. `ModelHider` is authored at (200, 0, 0) with its ring
				// 39 units AWAY (behind the parchment, which sits 1 unit in front of
				// `Contents`): Gameface paints in tree order and never depth-tests, so the
				// artist could park the ring back there for the 3D entity's sake and still see
				// it. Our quads depth-test, so the group comes 41 units toward the viewer —
				// the ring lands 2 in front of the parchment, the page texture on it, and the
				// proxy oval stays far behind where its depth keeps it out of the way.
				[$"{pages[BookPage]}.{HiderOf(page.ScreenId)}"] = new Vector3(200f, 0f, -41f),
				// The curtain is kept as the page's parchment (see Slides), so it goes BEHIND the
				// page's text and the ring instead of over them: authored (-204, 0, +41) under the
				// hider puts it on the text's plane (readout z 2250), where it wins by draw order.
				// It cannot go far back either: the book's cloth `background_green` is a
				// screen-wide quad at readout z 2220 (`refii_elements paint:217,120`), so the
				// parchment lives between the cloth and the page's shadow (2246) and frame (2249).
				[$"{pages[BookPage]}.{HiderOf(page.ScreenId)}.PageTexture"] = new Vector3(-204f, 0f, ModelHiderParchmentDepth),
			};

			if (page.ScreenId == ShopScreenId)
			{
				Engine_UiItemPage.ShopStatsPositions(positions, ShopPageElements[ShopStatsPage]);
			}

			if (page.ScreenId == EquipmentScreenId)
			{
				string hider = $"{pages[BookPage]}.{HiderOf(page.ScreenId)}";
				// The equipment book keeps its pale oval INSIDE the pinned group (the item books
				// author it beside it), so it would ride 41 units forward over the ring: it goes
				// back to just in front of the parchment. And the proxy — the 3D preview's anchor
				// — stays where the guest's Hero slide put it (20, −170: a mannequin's feet),
				// because a stated transform outranks the host slide's own command; the item
				// slides' anchor (30, 30, +350) is stated outright so the item hangs in the ring.
				positions[$"{hider}.MapHighlight"] = new Vector3(16f, 50f, ModelHiderParchmentDepth - 1f);
				// Garment and outfit alike sit in the ring at the Item slides' anchor; an outfit
				// is the mannequin wearing it, fitted to the same panel.
				positions[$"{hider}.model_proxy"] = new Vector3(30f, 30f, 350f);
			}

			return positions;
		}

		/// <summary>
		/// The star strip's own slides — a rating, not a page. The SHOP takes it from the row's
		/// `Float4`, which its populator fills and its filler range-checks; every other item
		/// book reads the record's GDB `Rating` straight through. Neither path bins anything.
		/// </summary>
		public IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page)
		{
			IReadOnlyDictionary<string, int> stars = page.ScreenId == ShopScreenId
				? ShopStarSlides(page.Row, page.Strings)
				: Engine_UiItemPage.StatesPages(page.ScreenId)
					? Engine_UiItemPage.Stated(page.ScreenId, page.Row)?.Slides ?? Engine_UiPageProviders.NoSlides
					: Engine_UiItemPage.Slides(page.Row, page.Strings);

			// THE PARCHMENT STAYS. `ModelHider` authors two slides: `Show` (1) fades
			// `PageTexture.opacity` 100→0 — the page opens as a window for the 3D entity the
			// guest draws BEHIND it — and `Hide` (2) fades it back to 100. The guest puts the
			// group on `Show` on every item row (`sub_82992998` → `sub_82426D18(…ModelHider, 1)`),
			// so its own final state is the cloth showing through the page. Godot draws the
			// model OVER the page, the window buys nothing, and the remaster keeps the
			// parchment: the up page's hider is stated on `Hide`, which also releases the
			// guest's stale opacity on everything that slide commands.
			string[] pages = page.ScreenId == ShopScreenId ? ShopPageElements : BookPageElements;
			if (BookPage < 0 || BookPage >= pages.Length)
			{
				return stars;
			}

			var slides = new Dictionary<string, int>(stars.Count + 1);
			foreach ((string key, int value) in stars)
			{
				slides[key] = value;
			}

			// `ItemOff` for an outfit too: `HeroOff` switches off the ring, the oval and the
			// item's icon (the original's ringless standing figure), and the remaster keeps
			// every clothing page in one layout — the mannequin stands in the ring instead.
			slides[$"{pages[BookPage]}.{HiderOf(page.ScreenId)}"] = HideSlideOf(page.ScreenId);

			// THE FOOTER'S `Normal` SLIDE: the `1 / 2` turn and `Select`. The guest states
			// `Clothing` (2) in non-dye mode (`sub_829B5D68`, 0x829b5f08), which adds a
			// Back-button "Take Picture" prompt this remaster does not answer from this page.
			if (page.ScreenId == EquipmentScreenId)
			{
				slides["Foreground.Book.Pages.Page1.Contents.Footer"] = FooterNormalSlide;
			}

			// THE DECORATION PROMPTS (`sub_8299BF48` 0x8299bf7c, `sub_8299CEA8` 0x8299cef0: both
			// pages' `PageButtons` on slide 10; `sub_8299C008` / `sub_8299CF80`): on the placed
			// item (row id 0x0FFFFFFF) Y is offered (Y 1, A 2), otherwise A (Y 2, A 1).
			if (page.ScreenId == ShopScreenId && Engine_UiShopFrontPage.State() is { } front)
			{
				// The front page's stars are its writer's (`2·trunc(stars)`), not a row's.
				slides.Remove("FrontPage.Contents.StarRating");
				foreach ((string key, int slide) in front.Slides)
				{
					slides[key] = slide;
				}
			}

			// Upgrade furniture's pages put `PageButtons` on slide 10 too (`sub_8299BF48`, `sub_8299CEA8`).
			if (page.ScreenId == Engine_UiFurniturePage.ScreenId)
			{
				foreach (string side in BookPageElements)
				{
					slides[$"{side}.Contents.PageButtons"] = DecorationButtonsSlide;
				}
			}

			if (page.ScreenId == DecorationScreenId)
			{
				bool placed = page.Row is { } row &&
					(((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu)) == DecorationPlacedId;
				foreach (string side in BookPageElements)
				{
					slides[$"{side}.Contents.PageButtons"] = DecorationButtonsSlide;
					slides[$"{side}.Contents.PageButtons.Y"] = placed ? 1 : 2;
					slides[$"{side}.Contents.PageButtons.A"] = placed ? 2 : 1;
				}
			}

			return slides;
		}

		/// <summary>
		/// THE SAME CURTAIN UNDER TWO NAMES. `equipmentscreen.bgf` (screen 16) authors the
		/// item books' `ModelHider` group as `TypeSpecific`, with the same children at the same
		/// local offsets (parchment at (-204, 0, +41), ring at (14, 30, +39), proxy at
		/// (20, -170, +350)) and FOUR slides instead of two: `HeroOn` (1) and `HeroOff` (3)
		/// are the mannequin mode (ring off, proxy low), `ItemOn` (2) and `ItemOff` (4) the
		/// item mode (ring on, proxy centred); the `On` slides fade the parchment out, the
		/// `Off` slides fade it back. The guest parks it on a Hero slide; the remaster shows
		/// the item in its ring on the parchment like every other book — `ItemOff`.
		/// </summary>
		private static string HiderOf(uint screenId) =>
			screenId == EquipmentScreenId ? "Contents.TypeSpecific" : "Contents.ModelHider";

		private static int HideSlideOf(uint screenId) =>
			screenId == EquipmentScreenId ? TypeSpecificItemOffSlide : ModelHiderHideSlide;

		private const int ModelHiderHideSlide = 2;
		private const int TypeSpecificItemOffSlide = 4;
		private const int FooterNormalSlide = 1;
		private const float ModelHiderParchmentDepth = 43f;
	}

	private const uint ShopScreenId = 22;

	private const uint ItemsScreenId = 15;

	private const uint GiftScreenId = 25;

	private const uint DecorationScreenId = 27;

	/// <summary>The gift book's gold row (`sub_826C4468` 0x826c454c).</summary>
	private const int GiftGoldEventType = 7;

	/// <summary>The decoration list's row for the item already placed (`sub_8299C008`).</summary>
	private const uint DecorationPlacedId = 0x0FFFFFFFu;

	/// <summary>`PageButtons` slide 10 on the decoration book's pages.</summary>
	private const int DecorationButtonsSlide = 10;

	/// <summary>The shop's stats page — `Page2` of `shopscreen.bgf`.</summary>
	private const int ShopStatsPage = 1;

	/// <summary>The shop's transaction row types (`sub_826AE5C0`): 30 buy, 31 sell.</summary>
	private const int ShopBuyEventType = 30;
	private const int ShopSellEventType = 31;
	private const uint EquipmentScreenId = 16;

	/// <summary>
	/// Which right-hand page of the book that is up is showing — 0 Page1, 1 Page2. Reset with
	/// the screen; moved by the page-turn lever.
	///
	/// Shared, because every book screen is the same shape: two authored pages on one leaf, and
	/// the guest slides between them. Only one screen is up at a time, so one index serves.
	/// </summary>
	public static int BookPage { get; set; }

	/// <summary>
	/// Which right-hand page is up — as a plain switch, because GODOT OWNS THE PAGE.
	///
	/// This used to state a SLIDE on `Foreground.Book` and on each `PageN`, reproducing the
	/// mechanism the shipped book uses to park a page at `position.x = 430` and slide it back.
	/// That was the wrong shape twice over. It did not work — `Page1`, `Page2` and `Pages` own
	/// no slides at all, and the numbers differ per book (`ShowPages` is slide 1 in
	/// `abilitiesscreen`, 7 in `giftgivingscreen`, 2 in `equipmentscreen`), so both pages stayed
	/// on the leaf and drew over each other. And it was the wrong JOB: Godot builds and draws
	/// this book, so where a page sits is ours to decide, not something to recover from the
	/// original's authored slides.
	///
	/// So the page is switched, the way the crime and options screens already switch theirs.
	/// One mechanism for every book, and a modded book needs no `ShowPages` slide to work.
	///
	/// A FOLDER SHOWS NO RIGHT PAGE — the shipped rule, kept because it is about the DATA (this
	/// row has children, so there is nothing of its own to show), not about presentation.
	/// </summary>
	private static Dictionary<string, bool> BookPages(
		int page, Engine_UiExpandableMenu.Item? row, params string[] pages)
	{
		// 301 OR 23 — `sub_826C3C78` at 0x826c3d0c hides the pages for both: a category and the
		// spell-selector row alike show no right page. The shipped mechanism is the Book's
		// `HidePages` slide, which HIDES NOTHING — it moves `Pages` to x 0, a position the host's
		// own `PageStackAt` pins anyway — so the page switch here is the one that must say it.
		bool folder = row is { Children.Count: > 0 } ||
			row?.EventType == (int)Engine_UiMenuConstants.EventType.Submenu ||
			row?.EventType == (int)Engine_UiMenuConstants.EventType.AssignSpellsAbilityItem;

		var active = new Dictionary<string, bool>(pages.Length);
		for (int i = 0; i < pages.Length; ++i)
		{
			active[pages[i]] = !folder && i == page;
		}

		return active;
	}

	/// <summary>
	/// The same switch for a screen whose rows are all leaves — crime, options — so there is no
	/// folder rule to apply. One mechanism; the only difference is that nothing here can be a
	/// folder.
	/// </summary>
	private static Dictionary<string, bool> BookPagesByOpacity(int page, params string[] pages) =>
		BookPages(page, row: null, pages);

	/// <summary>
	/// WHICH PAGE IS UP IS STATED, both halves of it, and that is the fix for a right page that
	/// "appeared and then hid".
	///
	/// `giftgivingscreen.bgf` authors Page1 and Page2 on the SAME right-hand leaf — Page2 parked
	/// at x 430, beyond the 4:3 canvas the console showed — and the guest slides one in as it
	/// slides the other out. It does that on a highlight-change edge keyed off ITS Lua cursor,
	/// which stops moving the moment Godot owns the list, so whichever page the guest last left
	/// up stayed up and nothing ever turned Page1 back on. Saying only "Page2 off" left the
	/// other half to a writer that had stopped writing.
	///
	/// Both are named now, so the page the host is filling is the page that draws.
	/// </summary>
	/// <summary>
	/// HOW MANY PAGES A BOOK HAS IS PER BOOK, and every one of them must be named here.
	///
	/// | screen | pages |
	/// |---|---|
	/// | items / gift / equipment 15, 16, 25, 26, 27 | Page1, Page2 |
	/// | shop 22 | Page1, Page2, **Page3** |
	/// | abilities 14 | Page1, Page2, **Page4** (there is no Page3) |
	/// | world map 23 | Page1, Page2 + five named |
	/// | options 18 | Page1..Page4 |
	/// | crime 21 | Page1..Page3 |
	///
	/// OMITTING A PAGE IS NOT NEUTRAL: a page nobody switches off keeps drawing, over whichever
	/// page is up. That is the shop drawing three layouts at once and the abilities book drawing
	/// over itself.
	/// </summary>
	private static readonly string[] BookPageElements =
	{
		"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
	};

	private static readonly string[] OptionsPageElements =
	{
		"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
		"Foreground.Book.Pages.Page3", "Foreground.Book.Pages.Page4",
	};

	private static readonly string[] CrimePageElements =
	{
		"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
		"Foreground.Book.Pages.Page3",
	};

	/// <summary>The shop authors a third page.</summary>
	private static readonly string[] ShopPageElements =
	{
		"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
		"Foreground.Book.Pages.Page3",
	};

	/// <summary>
	/// The abilities book authors `Page4` — there is no `Page3` — and leaving it out draws its
	/// blade over whichever page is up.
	/// </summary>
	private static readonly string[] AbilityPageElements =
	{
		"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
		"Foreground.Book.Pages.Page4",
	};

	/// <summary>
	/// THE ITEM BOOKS' STAR STRIP, which is not the shop's.
	///
	/// `shopscreen.bgf` builds its rating as a component with five SLIDES; `equipmentscreen.bgf`
	/// and `giftgivingscreen.bgf` build theirs as two sibling groups of five elements —
	/// `StarRating/staron/staron1..5` over `StarRating/staroff/staroff1..5` — and show N stars by
	/// lighting the first N of one and the last 5-N of the other. Dumped from the shipped files
	/// rather than assumed; the two spellings are why the shop keeps its own path.
	///
	/// The row's rating is in HALF-star units (1..10), the same number `sub_826CE230` passes as
	/// `Float4`, so the whole-star count is half of it — matching `ShopStarSlides`.
	/// </summary>
	/// <summary>
	/// ONE BUTTON PROMPT, NOT FOUR. `PageButtons` authors a DIAMOND of four — `A` at (0, -30),
	/// `B` at (30, 0), `X` at (-30, 0), `Y` at (0, 30) — each with its own `Text`, and every one
	/// authored `act=1`. Drawing them all put `Select`, `Cancel`, `Use` and `Remove` on top of
	/// each other and over the item's own value line, because each `Text` sits at the same
	/// (-24, 1) offset inside its parent and the words are wider than the 30-unit spacing.
	///
	/// Only `A` is offered. That is a statement about what THIS book does — one action, taken
	/// with A — not a reproduction of the original's per-item prompt logic; a book that offers
	/// more switches more on.
	/// </summary>
	/// <summary>
	/// The pages a SCREEN RECORD names, or the built-in set when it names none yet.
	///
	/// The record is the source of truth — see <see cref="Engine_UiScreenTemplate.Pages"/> — so
	/// a modded screen declares its own pages instead of matching a table in here. The arrays
	/// remain only for screens nobody has authored a record for.
	/// </summary>
	private static string[] PagesOf(in Engine_UiPageContext page, string[] fallback) =>
		Engine_UiTemplates.Screen(page.ScreenId) is { Pages.Count: > 0 } screen
			? screen.Pages.ToArray()
			: fallback;



	private static void AddItemStars(
		Dictionary<string, bool> active, Engine_UiExpandableMenu.Item? row)
	{
		if (row is null || row.RatingSlide < Engine_UiMenuConstants.OneStarEmptyRatingSlide ||
			row.RatingSlide > Engine_UiMenuConstants.FiveStarFullRatingSlide)
		{
			// An item that states no rating draws no stars at all rather than one — the same
			// choice `Engine_UiInventorySource.RatingOf` makes for the row itself.
			for (int star = 1; star <= 5; ++star)
			{
				active[$"StarRating.staron.staron{star}"] = false;
				active[$"StarRating.staroff.staroff{star}"] = false;
			}

			return;
		}

		int stars = System.Math.Clamp((row.RatingSlide + 1) / 2, 1, 5);
		for (int star = 1; star <= 5; ++star)
		{
			active[$"StarRating.staron.staron{star}"] = star <= stars;
			active[$"StarRating.staroff.staroff{star}"] = star > stars;
		}
	}

	/// <summary>
	/// The shop's `StarRating` components — one per page, five slides `1star`..`5star` after the
	/// master in `shopscreen.bgf`. The row's `RatingSlide` is the same number in half-star units
	/// (1..10), so the whole-star slide is half of it, rounded.
	/// </summary>
	private static readonly string[] ShopStarRatings =
	{
		"FrontPage.Contents.StarRating",
		"Page1.Contents.StarRating",
		"Page2.Contents.StarRating",
		"Page3.Contents.StarRating",
	};

	private static IReadOnlyDictionary<string, int> ShopStarSlides(
		Engine_UiExpandableMenu.Item? row, Refii.Text.Engine_BabelDatabase? strings)
	{
		// AN ITEM ROW'S STARS ARE ITS RECORD'S. `sub_82992998` slides `StarRating` to the GDB
		// `Rating` (`detail+0x44`) when it is 1..5 and leaves it alone otherwise; the row's
		// `RatingSlide` answers only for the front page's own rating.
		int stars;
		if (row?.EventType is (int)ShopBuyEventType or (int)ShopSellEventType)
		{
			stars = Engine_UiItemPage.ShopRating(row, strings);
			if (stars < 1 || stars > 5)
			{
				return NoSlides;
			}
		}
		else if (row is null || row.RatingSlide < Engine_UiMenuConstants.OneStarEmptyRatingSlide ||
			row.RatingSlide > Engine_UiMenuConstants.FiveStarFullRatingSlide)
		{
			return NoSlides;
		}
		else
		{
			stars = System.Math.Clamp((row.RatingSlide + 1) / 2, 1, 5);
		}

		var slides = new Dictionary<string, int>(ShopStarRatings.Length);
		foreach (string path in ShopStarRatings)
		{
			slides[path] = stars;
		}

		return slides;
	}
}
