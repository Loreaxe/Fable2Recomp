namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// Fable 3's stated UI (Fable3Elements keyed by document, Fable3UiStatements): a presentation's
/// statements land under the document its load named and a reload forgets them; a text stated at
/// its writer's entry outranks the box read back, which stays the cross-check; the visibility
/// toggle's request hides and shows as the guest's 1024 scale does; the HUD root is its writers'
/// state; the TopBox queue's message up is the back of the first non-empty channel; an
/// interaction button lands its label, opacity, glyph and show on its elements; the interaction
/// mode's stated channels hold only what its writers were called with. No Godot type is built.
/// </summary>
[TestSuite]
public class Fable3StatedUiTests
{
	private static readonly List<string> Logged = new();

	private static Engine_UiModel.GuiNativeArgument S(string text) => new(Engine_UiModel.GuiNativeArgumentKind.String, text);

	private static Engine_UiModel.GuiNativeArgument N(float value) =>
		new(Engine_UiModel.GuiNativeArgumentKind.Number, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

	private static Engine_UiModel.GuiNativeArgument B(bool value) => new(Engine_UiModel.GuiNativeArgumentKind.Boolean, value ? "1" : "0");

	private static Engine_UiModel.GuiNativeCall Call(string name, params Engine_UiModel.GuiNativeArgument[] arguments) => new(name, arguments);

	private static void Fresh()
	{
		Logged.Clear();
		Fable3Elements.Log = Logged.Add;
		Fable3Elements.TagText = tag => tag == 0x1234u ? "Are you sure?" : null;
		Fable3Elements.TagNameText = name => name == "GUI_YES" ? "Yes" : name;
		Fable3Elements.Clear();
		Fable3UiStatements.Clear();
		Fable3HudRegistry.Clear();
	}

	[TestCase]
	public void StatementsLandUnderTheLoadedDocument()
	{
		Fresh();
		Fable3Elements.Take(Call("@Presentation.Load", S("40001000"), S("GAME:\\data\\art\\gui\\gameface\\Options.bgf")));
		Fable3Elements.Take(Call("@Ui.Text", S("40001000"), S("00000000"), S("Options.Title"), S("Options"), S("82819070")));

		AssertThat(Fable3Elements.DocumentOf(0x40001000u)).IsEqual("options");
		AssertThat(Fable3Elements.Get("options", "Options.Title")?.StatedTextValue).IsEqual("Options");
		AssertThat(Fable3Elements.Latest("Options.Title")?.StatedTextValue).IsEqual("Options");

		// A reload of the document — at another address — starts clean.
		Fable3Elements.Take(Call("@Presentation.Load", S("40002000"), S("Options.bgf")));
		AssertThat(Fable3Elements.Get("options", "Options.Title")).IsNull();
	}

	[TestCase]
	public void ADocumentNamedStatementNeedsNoLoad()
	{
		Fresh();
		// The native names the document once it saw the load: a dropped load statement costs nothing.
		Fable3Elements.Take(Call("@Ui.Text", S("=hud"), S("00000000"), S("Layer.JobMultiplier.Text"), S("x3"), S("82819070")));
		AssertThat(Fable3Elements.Get("hud", "Layer.JobMultiplier.Text")?.StatedTextValue).IsEqual("x3");
	}

	[TestCase]
	public void AStatementWithNoPresentationStillLands()
	{
		Fresh();
		// No load named the presentation: keyed by its address, not dropped, not waiting.
		Fable3Elements.Take(Call("@Ui.Text", S("40003000"), S("00000000"), S("SaveMenu.Title"), S("Save"), S("82819070")));
		AssertThat(Fable3Elements.Get("#40003000", "SaveMenu.Title")?.StatedTextValue).IsEqual("Save");
	}

	[TestCase]
	public void TheBoxReadBackIsNotAStatement()
	{
		// The text box's read-back (`@Element.Text`) has no route: the stated tag is the only text.
		Fresh();
		Fable3Elements.Take(Call("@Presentation.Load", S("40001000"), S("InteractionMode.bgf")));
		Fable3Elements.Take(Call("@Ui.TextTag", S("40001000"), S("50000000"), S("LowerElements.Query"), S("00001234"), S("82819288")));
		AssertThat(Fable3Elements.Take(Call("@Element.Text", S("40001000"), S("50000000"), S("LowerElements.Query"), S("Something else")))).IsFalse();

		AssertThat(Fable3Elements.Get("interactionmode", "LowerElements.Query")?.StatedTextValue).IsEqual("Are you sure?");
	}

	[TestCase]
	public void TheToggleHidesAndShowsAsTheGuestScales()
	{
		// An outright opacity is a writer's own call (`@Ui.Opacity`, the world map button's
		// sub_82910D60 at 0x82910E00); the toggle scales over it.
		Fresh();
		Fable3Elements.Take(Call("@Presentation.Load", S("40001000"), S("InteractionMode.bgf")));
		const string path = "LowerElements.InteractionMenuRight.Elements.Buttons.Button1";

		Fable3Elements.Take(Call("@Ui.Opacity", S("40001000"), S("50000020"), S(path), N(50f), S("82910D60")));
		Fable3Elements.Take(Call("@Ui.Visible", S("40001000"), S("50000020"), S(path), B(false), S("82819AE0")));
		Fable3Elements.Element element = Fable3Elements.Get("interactionmode", path)!;
		AssertThat(element.Hidden).IsTrue();

		Fable3Elements.Take(Call("@Ui.Visible", S("40001000"), S("50000020"), S(path), B(true), S("82819AE0")));
		AssertThat(element.Hidden).IsFalse();

		// Shown over an outright 0 stays hidden (0 * 1024).
		Fable3Elements.Take(Call("@Ui.Opacity", S("40001000"), S("50000020"), S(path), N(0f), S("82910D60")));
		Fable3Elements.Take(Call("@Ui.Visible", S("40001000"), S("50000020"), S(path), B(true), S("82819AE0")));
		AssertThat(element.Hidden).IsTrue();

		// No document takes the opacity setter's report any more, Options included.
		Fable3Elements.Take(Call("@Presentation.Load", S("40002000"), S("Options.bgf")));
		const string label = "Options.Sections.QueryBox.Buttons.Buttons.A_Button.small.Text";
		AssertThat(Fable3Elements.Take(Call("@Element.Opacity", S("40002000"), S("50000030"), S(label), N(0f)))).IsFalse();
		AssertThat(Fable3Elements.Take(Call("@Element.StoredOpacity", S("40002000"), S("50000030"), S(label), N(0f)))).IsFalse();
		AssertThat(Fable3Elements.Get("options", label)).IsNull();
	}

	[TestCase]
	public void TheSettersReportLandsOnNoStatedDocument()
	{
		Fresh();
		Fable3Elements.Take(Call("@Presentation.Load", S("40001000"), S("HUD.bgf")));
		const string path = "JobMultiplier";
		Fable3Elements.Take(Call("@Element.Opacity", S("40001000"), S("50000020"), S(path), N(0f)));
		AssertThat(Fable3Elements.Get("hud", path)).IsNull();

		// The multiplier root's own call (sub_82950AF0 at 0x82950B40) is the statement.
		Fable3Elements.Take(Call("@Ui.Opacity", S("=hud"), S("50000020"), S(path), N(100f), S("82950AF0")));
		AssertThat(Fable3Elements.Get("hud", path)?.StatedDrawnOpacity).IsEqual(100f);
		Fable3Elements.Take(Call("@Ui.Visible", S("=hud"), S("50000020"), S(path), B(false), S("82819AE0")));
		AssertThat(Fable3Elements.Get("hud", path)?.StatedDrawnOpacity).IsEqual(0f);
		Fable3Elements.Take(Call("@Ui.Opacity", S("=hud"), S("50000020"), S(path), N(0f), S("82950AF0")));
		AssertThat(Fable3Elements.Get("hud", path)?.Visible).IsNull();
	}

	[TestCase]
	public void TheHudRootIsItsWritersState()
	{
		Fresh();
		AssertThat(Fable3Elements.HudHidden).IsFalse();
		Fable3Elements.Take(Call("@Presentation.Load", S("40001000"), S("HUD.bgf")));
		Fable3Elements.Take(Call("@Presentation.Load", S("40002000"), S("InteractionMode.bgf")));

		// The interaction mode's root hidden is not the HUD hidden.
		Fable3Elements.Take(Call("@Ui.State", S("40002000"), S("50000030"), S("Scene"), N(2f), S("825C3A78")));
		AssertThat(Fable3Elements.HudHidden).IsFalse();

		Fable3UiStatements.Take(Call("@Hud.Root", N(2f), S("825C1CB0")));
		AssertThat(Fable3Elements.HudHidden).IsTrue();
		AssertThat(Fable3Elements.Get("hud", "Scene")?.State).IsEqual(2);

		Fable3UiStatements.Take(Call("@Hud.Root", N(1f), S("825C1D28")));
		AssertThat(Fable3Elements.HudHidden).IsFalse();
	}

	[TestCase]
	public void TheSaveMenuGoesWithItsScreenManager()
	{
		Fresh();
		Fable3UiStatements.Take(Call("@SaveMenu.Open", B(true), N(1f)));
		Fable3UiStatements.Take(Call("@SaveMenu.Row", N(2f)));
		AssertThat(Fable3UiStatements.SaveMenuOpen).IsTrue();
		AssertThat(Fable3UiStatements.SaveMenuRow).IsEqual(2);

		// Save and Quit: the guest frees SM (+0x38 the save menu) without the menu's own close.
		ulong before = Fable3UiStatements.Generation;
		Fable3UiStatements.ScreensDestroyed();
		AssertThat(Fable3UiStatements.SaveMenuOpen).IsFalse();
		AssertThat(Fable3UiStatements.SaveMenuAllowed).IsFalse();
		AssertThat(Fable3UiStatements.SaveMenuRow).IsEqual(0);
		AssertThat(Fable3UiStatements.Generation > before).IsTrue();

		// The rebuilt GUI's menu opens as new.
		Fable3UiStatements.Take(Call("@SaveMenu.Open", B(false), N(3f)));
		AssertThat(Fable3UiStatements.SaveMenuOpen).IsTrue();
		AssertThat(Fable3UiStatements.SaveMenuRow).IsEqual(3);
	}

	[TestCase]
	public void ARebuiltHudStartsWithNoRootState()
	{
		Fresh();
		Fable3Elements.Take(Call("@Presentation.Load", S("40001000"), S("HUD.bgf")));
		Fable3UiStatements.Take(Call("@Hud.Root", N(2f), S("825C1CB0")));
		AssertThat(Fable3Elements.HudHidden).IsTrue();

		// The interaction mode's reload leaves the HUD's root alone.
		Fable3Elements.Take(Call("@Presentation.Load", S("40002000"), S("InteractionMode.bgf")));
		AssertThat(Fable3Elements.HudRootState).IsEqual(2);

		// The quit to the front end rebuilds the GUI: the new hud.bgf's root has no writer yet.
		Fable3Elements.Take(Call("@Presentation.Load", S("40003000"), S("HUD.bgf")));
		AssertThat(Fable3Elements.HudRootState).IsNull();
		AssertThat(Fable3Elements.HudHidden).IsFalse();
		AssertThat(Fable3Elements.Get("hud", "Scene")).IsNull();

		Fable3UiStatements.Take(Call("@Hud.Root", N(1f), S("825C1D28")));
		AssertThat(Fable3Elements.HudRootState).IsEqual(1);
	}

	[TestCase]
	public void TheTopBoxUpIsTheBackOfTheFirstNonEmptyChannel()
	{
		Fresh();
		var queue = new Fable3UiStatements.TopBoxQueue();
		Fable3UiStatements.TopBoxMessage Message(uint id, int channel) =>
			new(id, channel, 0u, 0u, 0u, 5000f, 0u, 0u, true, false, false, false, 0);

		queue.Post(Message(1u, 8));
		queue.Post(Message(2u, 3));
		queue.Post(Message(3u, 3));
		AssertThat(queue.Current?.Id).IsEqual(3u);

		queue.Remove(3u);
		AssertThat(queue.Current?.Id).IsEqual(2u);
		queue.Remove(2u);
		AssertThat(queue.Current?.Id).IsEqual(1u);
		queue.Clear();
		AssertThat(queue.Current).IsNull();
	}

	[TestCase]
	public void ATopBoxPostParsesItsMessage()
	{
		Fresh();
		Fable3UiStatements.Take(Call("@TopBox.Post", S("00000007"), N(8f), S("AABBCCDD"), S("820A0000"), S("11223344"), N(4000f),
			S("0000A00A"), S("0000B00B"), B(true), B(true), B(false), B(false), N(0f)));
		AssertThat(Fable3UiStatements.TopBox.Current?.Id).IsEqual(7u);
		AssertThat(Fable3UiStatements.TopBox.Current?.HoldA).IsTrue();
		AssertThat(Fable3UiStatements.TopBox.Current?.LabelA).IsEqual(0xA00Au);
		Fable3UiStatements.Take(Call("@TopBox.Remove", S("00000007")));
		AssertThat(Fable3UiStatements.TopBox.Count).IsEqual(0);
	}

	[TestCase]
	public void AnInteractionButtonLandsOnItsElements()
	{
		Fresh();
		Fable3Elements.Take(Call("@Presentation.Load", S("40001000"), S("InteractionMode.bgf")));
		const string button = "LowerElements.InteractionMenuRight.Elements.Button2";
		Fable3UiStatements.Take(Call("@Interaction.Button", S("40001000"), S(button), S("Talk"), S("gui\\a_text.tex"), B(false)));

		AssertThat(Fable3Elements.Get("interactionmode", button + ".Title")?.StatedTextValue).IsEqual("Talk");
		AssertThat(Fable3Elements.Get("interactionmode", button)?.StatedDrawnOpacity).IsEqual(50f);
		AssertThat(Fable3Elements.Get("interactionmode", button)?.Hidden).IsFalse();
		AssertThat(Fable3Elements.Get("interactionmode", button + ".Icon.Container.A_Text")?.Texture).IsEqual("gui\\a_text.tex");
	}

	/// <summary>
	/// The list writer sub_82963DE0 at the last row: Button3 takes the cursor's label through the
	/// button writer, Button2 and Button1 are past the end — their `Title` committed "" (stated at
	/// the box guards 0x82963FA4 / 0x82964098) and hidden. No row keeps the label it held before:
	/// cursor-1, cursor, cursor+1 never read "Feet / Feet / Feet". A row filled again with the label
	/// it held before it was emptied shows it again.
	/// </summary>
	[TestCase]
	public void AnEmptiedInteractionRowNeverKeepsItsLastLabel()
	{
		Fresh();
		Fable3Elements.Take(Call("@Presentation.Load", S("40001000"), S("InteractionMode.bgf")));
		const string rows = "LowerElements.InteractionMenuScrollableList.Buttons.";
		string Title(string row) => Fable3Elements.Get("interactionmode", rows + row + ".Title")?.StatedTextValue ?? "<none>";
		void Fill(string row, string label) =>
			Fable3UiStatements.Take(Call("@Interaction.Button", S("=interactionmode"), S(rows + row), S(label), S("gui\\a_text.tex"), B(true)));
		void Empty(string row, string title)
		{
			Fable3Elements.Take(Call("@Ui.Text", S("=interactionmode"), S(title), S(rows + row + ".Title"), S(string.Empty), S("82963DE0")));
			Fable3Elements.Take(Call("@Ui.Visible", S("=interactionmode"), S("43000000"), S(rows + row), B(false), S("82819AE0")));
		}

		// Cursor on Lower Body, the row before the last (Feet): Button1 is past the end.
		Fill("Button3", "Lower Body");
		Fill("Button2", "Feet");
		Empty("Button1", "43000110");

		// Cursor on Feet: Button3 Feet, Button2 past the end.
		Fill("Button3", "Feet");
		Empty("Button2", "43000120");
		Empty("Button1", "43000110");

		AssertThat(Title("Button3")).IsEqual("Feet");
		AssertThat(Title("Button2")).IsEqual(string.Empty);
		AssertThat(Title("Button1")).IsEqual(string.Empty);
		AssertThat(Fable3Elements.Get("interactionmode", rows + "Button2")!.Hidden).IsTrue();

		// Back up: Button2 takes Feet again, the label it held before it was emptied.
		Fill("Button3", "Lower Body");
		Fill("Button2", "Feet");
		AssertThat(Title("Button2")).IsEqual("Feet");
		AssertThat(Fable3Elements.Get("interactionmode", rows + "Button2")!.Hidden).IsFalse();
		AssertThat(Title("Button1")).IsEqual(string.Empty);
	}

	[TestCase]
	public void TheInteractionModesStatedChannelsHoldOnlyItsCalls()
	{
		Fresh();
		Fable3Elements.Take(Call("@Presentation.Load", S("40001000"), S("InteractionMode.bgf")));
		const string bar = "UpperElements.TriggerButtons";
		const string button = "LowerElements.InteractionMenuRight.Elements.Buttons.Button1";

		// The setters' after-the-fact reports land nowhere.
		Fable3Elements.Take(Call("@Element.StoredOpacity", S("=interactionmode"), S("42000020"), S(button), N(0f)));
		AssertThat(Fable3Elements.Get("interactionmode", button)).IsNull();

		// The bar's show (vf0) states its slide; its label commit (vf1) its text.
		Fable3Elements.Take(Call("@Ui.State", S("=interactionmode"), S("42000010"), S(bar), N(3f), S("82B3EA18")));
		Fable3Elements.Take(Call("@Ui.Text", S("=interactionmode"), S("42000030"), S(bar + ".Triggers.Text"), S("Suits"), S("82B3EB28")));
		AssertThat(Fable3Elements.Get("interactionmode", bar)?.StatedState).IsEqual(3);

		// The same slide again is a new entry: its stamp moves, an unrelated write's does not.
		ulong entered = Fable3Elements.Get("interactionmode", bar)!.StatedStateStamp;
		Fable3Elements.Take(Call("@Element.Opacity", S("=interactionmode"), S("42000010"), S(bar), N(50f)));
		AssertThat(Fable3Elements.Get("interactionmode", bar)!.StatedStateStamp).IsEqual(entered);
		Fable3Elements.Take(Call("@Ui.State", S("=interactionmode"), S("42000010"), S(bar), N(3f), S("8281A500")));
		AssertThat(Fable3Elements.Get("interactionmode", bar)!.StatedStateStamp > entered).IsTrue();
		AssertThat(Fable3Elements.Get("interactionmode", bar + ".Triggers.Text")?.StatedTextValue).IsEqual("Suits");

		// The button: hidden by the toggle, then offered disabled at 50, then the root deactivated.
		Fable3Elements.Take(Call("@Ui.Visible", S("=interactionmode"), S("42000020"), S(button), B(false), S("82819AE0")));
		AssertThat(Fable3Elements.Get("interactionmode", button)?.StatedDrawnOpacity).IsEqual(0f);
		Fable3UiStatements.Take(Call("@Interaction.Button", S("=interactionmode"), S(button), S("Undress"), S("gui\\b_text.tex"), B(false)));
		AssertThat(Fable3Elements.Get("interactionmode", button)?.StatedDrawnOpacity).IsEqual(50f);
		AssertThat(Fable3Elements.Get("interactionmode", button + ".Icon.Container.A_Text")?.StatedTexture).IsEqual("gui\\b_text.tex");
		Fable3Elements.Take(Call("@Ui.Texture", S("=interactionmode"), S("42000040"), S(button + ".Icon.Container.A_Orb"), S("gui\\b_orb.tex"), S("82818EF0")));
		AssertThat(Fable3Elements.Get("interactionmode", button + ".Icon.Container.A_Orb")?.StatedTexture).IsEqual("gui\\b_orb.tex");

		Fable3Elements.Take(Call("@Ui.Active", S("=interactionmode"), S("42000000"), S("Scene"), B(false), S("828196E0")));
		AssertThat(Fable3Elements.Get("interactionmode", "Scene")?.StatedDrawnOpacity).IsEqual(0f);
		Fable3Elements.Take(Call("@Ui.Active", S("=interactionmode"), S("42000000"), S("Scene"), B(true), S("828196E0")));
		AssertThat(Fable3Elements.Get("interactionmode", "Scene")?.StatedDrawnOpacity).IsNull();
	}

	[TestCase]
	public void TheMoneyAlertCountsToTheClampedTarget()
	{
		Fresh();
		Fable3UiStatements.Take(Call("@Hud.Money.Change", N(0f), N(999999990f), N(500f), B(false), B(false)));
		AssertThat(Fable3UiStatements.Money[0].Amount).IsEqual(999999999);
		AssertThat(Fable3UiStatements.Money[0].Shown).IsTrue();

		// A change made while the alert is busy is queued by the guest: nothing shown changes yet.
		Fable3UiStatements.Take(Call("@Hud.Money.Change", N(0f), N(10f), N(5f), B(false), B(true)));
		AssertThat(Fable3UiStatements.Money[0].Amount).IsEqual(999999999);

		Fable3UiStatements.Take(Call("@Hud.Money.Hide", N(0f)));
		AssertThat(Fable3UiStatements.Money[0].Shown).IsFalse();
	}

	[TestCase]
	public void TheSaveMenuIsItsCalls()
	{
		Fresh();
		Fable3UiStatements.Take(Call("@SaveMenu.Open", B(false), N(3f)));
		AssertThat(Fable3UiStatements.SaveMenuOpen).IsTrue();
		AssertThat(Fable3UiStatements.SaveMenuAllowed).IsFalse();
		AssertThat(Fable3UiStatements.SaveMenuRow).IsEqual(3);
		Fable3UiStatements.Take(Call("@SaveMenu.Row", N(4f)));
		AssertThat(Fable3UiStatements.SaveMenuRow).IsEqual(4);
		Fable3UiStatements.Take(Call("@SaveMenu.Close"));
		AssertThat(Fable3UiStatements.SaveMenuOpen).IsFalse();
	}

	[TestCase]
	public void TheLinesAndTheEpilogueTextsLandByName()
	{
		Fresh();
		uint topMain = Refii.Core.Engine_GameHash.Fnv1("TopMain");
		Fable3UiStatements.Take(Call("@Hud.Lines", S("CAFEF00D"), N(1f), S(topMain.ToString("X8")), S("Find the music box"), B(false)));
		AssertThat(Fable3HudRegistry.Line(topMain)).IsEqual("Find the music box");

		uint title = Refii.Core.Engine_GameHash.Fnv1("EpilogueTitle");
		Fable3EndScreens.Opened("The Rebel", "You did it.", 0, title, 0u);
		bool landed = false;
		foreach (Fable3HudStatements.Statement statement in Fable3HudRegistry.StatementsFor("questcompletion"))
		{
			landed |= statement.Kind == Engine_UiBindings.Kind.Text && statement.NameHash == title && statement.Text == "The Rebel";
		}

		AssertThat(landed).IsTrue();
	}

	[TestCase]
	public void TheJobMultiplierPlaysTheStatedTransitions()
	{
		Fresh();
		Fable3UiStatements.Take(Call("@Hud.Job", N(2f), S("x")));
		AssertThat(Fable3Elements.Get("hud", "JobMultiplier")?.State).IsEqual(1);
		Fable3UiStatements.Take(Call("@Hud.Job", N(3f), S("x")));
		AssertThat(Fable3Elements.Get("hud", "JobMultiplier")?.State).IsEqual(2);
		Fable3UiStatements.Take(Call("@Hud.Job", N(3f), S("x")));
		AssertThat(Fable3Elements.Get("hud", "JobMultiplier")?.State).IsEqual(3);
		Fable3UiStatements.Take(Call("TurnJobMultiplierOff", B(true)));
		AssertThat(Fable3Elements.Get("hud", "JobMultiplier")?.State).IsEqual(4);
		// At level 0 the off-switch does nothing (0x8251CE28).
		Fable3UiStatements.Take(Call("TurnJobMultiplierOff", B(false)));
		AssertThat(Fable3Elements.Get("hud", "JobMultiplier")?.State).IsEqual(4);
	}

	[TestCase]
	public void TheCrimeTimerIsItsManagersDecisionEnteredEachTime()
	{
		Fresh();
		Fable3Elements.Take(Call("@Presentation.Load", S("40001000"), S("HUD.bgf")));
		Fable3UiStatements.Take(Call("@Hud.Crime.Shown", B(true)));
		Fable3Elements.Element root = Fable3Elements.Get("hud", "CrimeTimer")!;
		AssertThat(root.StatedState).IsEqual(2);
		ulong shown = root.StatedStateStamp;

		// The setter's own SetState has no route; the next decision is a new entry.
		AssertThat(Fable3Elements.Take(Call("@Element.State", S("40001000"), S("40005000"), S("CrimeTimer"), N(1f)))).IsFalse();
		AssertThat(root.StatedState).IsEqual(2);
		Fable3UiStatements.Take(Call("@Hud.Crime.Shown", B(false)));
		AssertThat(root.StatedState).IsEqual(1);
		AssertThat(root.StatedStateStamp > shown).IsTrue();
	}

	[TestCase]
	public void TheCreditsTakeOnlyTheRollsPositions()
	{
		Fresh();
		Fable3Elements.Take(Call("@Presentation.Load", S("40003000"), S("CreditsScreen.bgf")));
		Fable3Elements.Take(Call("@Ui.Position", S("40003000"), S("40007000"), S("Text.Text0"), N(0f), N(328f), N(0f), S("82B3FD70")));
		AssertThat(Fable3Elements.Take(Call("@Element.Position", S("40003000"), S("40007000"), S("Text.Text0"), N(0f), N(5f), N(0f)))).IsFalse();
		AssertThat(Fable3Elements.Get("creditsscreen", "Text.Text0")?.Position?.Y).IsEqual(328f);
	}
}
