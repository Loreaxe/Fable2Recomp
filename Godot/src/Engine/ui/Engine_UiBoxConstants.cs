namespace Refii.Ui;

/// <summary>
/// Constants from `displayboxes/*.lua` and `page.lua`: the modal cards and the scrolling book page.
/// </summary>
public static class Engine_UiBoxConstants
{
	/// <summary>
	/// `displaybox.lua` box kinds; intro slide `n`, outro `n + 3`
	/// (`displaybox.dis main pc5..20`). The root authors eight slides, see
	/// <see cref="DisplayBoxAcceptanceNewIntro"/>.
	/// </summary>
	public enum DisplayBox
	{
		Info = 1,
		Objective = 2,
		Acceptance = 3,
	}

	/// <summary>
	/// Slides 7 `AcceptanceNewInt` / 8 `AcceptanceNewOut`, unnamed by the Lua; the only two
	/// that activate the `Paper` group (element 87) — the hold-to-accept card.
	/// </summary>
	public const int DisplayBoxAcceptanceNewIntro = 7;

	public const int DisplayBoxAcceptanceNewOutro = 8;

	/// <summary>The intro slide for a kind.</summary>
	public static int DisplayBoxIntro(DisplayBox kind) => (int)kind;

	/// <summary>The outro slide for a kind — always the intro plus three.</summary>
	public static int DisplayBoxOutro(DisplayBox kind) => (int)kind + 3;

	/// <summary>`g_DisplayBox.Close`: outro for the current slide; unexpected slides take the Info outro.</summary>
	public static int DisplayBoxOutroFor(int currentSlide) =>
		currentSlide >= (int)DisplayBox.Info && currentSlide <= (int)DisplayBox.Acceptance
			? currentSlide + 3
			: DisplayBoxOutro(DisplayBox.Info);

	/// <summary>
	/// Title tags `OpenAs*Box` hands to `GUI:GetText` (`displaybox.dis main.2/3/4 pc7..11`).
	/// `GUI.GetText` is a stub returning `'...'` (`uicontractlib.dis main.3`), and `Title` is
	/// inactive on all six named slides.
	/// </summary>
	public static string DisplayBoxTitleTag(DisplayBox kind) => kind switch
	{
		DisplayBox.Objective => "GUI_OBJECTIVE_BOX",
		DisplayBox.Acceptance => "GUI_ACCEPTANCE_BOX",
		_ => "GUI_INFO_BOX",
	};

	/// <summary>
	/// `PrepareDisplayBox`'s `boxwidth = 1.4`; inert — `sub_82871050` never reads it, the wrap
	/// is the constructor's `.fac` box, 0.9 × 960 = 864.
	/// </summary>
	public const float DisplayBoxTextWidth = 1.4f;

	/// <summary>Per-update re-centre: `position.y = -50 + measuredHeight / 2` on the frame's parent.</summary>
	public static float DisplayBoxCentreY(float measuredHeight) => -50f + (measuredHeight / 2f);

	/// <summary>
	/// `TilStopUpdating`: `onInitialize` writes 0 (`displaybox.dis main.0 pc14`),
	/// `SetFrameToBeUpdated` writes 3 (`main.7 pc1`), `onUpdate` counts down while > 0.
	/// </summary>
	public const int DisplayBoxUpdatesInitial = 0;

	public const int DisplayBoxUpdatesAfterStop = 3;

	/// <summary>
	/// `MessageBox.lua` slides (`messagebox.dis main pc5, pc8`). The script is carried by
	/// `intermissionbox.bgf` (root slides `Intro`, `Outro`), not `messagebox*.bgf`.
	/// </summary>
	public const int MessageBoxIntroSlide = 1;

	public const int MessageBoxOutroSlide = 2;

	/// <summary>
	/// `MessageBox.QuarterbackOpen`'s `boxwidth`/`boxheight` pairs (`messagebox.dis main.4
	/// pc6..13`, `pc31..38`); the game's own `Open` (`main.2`) writes neither.
	/// </summary>
	public const float MessageBoxShortTextWidth = 0.25f;

	public const float MessageBoxShortTextHeight = 0.15f;

	public const float MessageBoxLongTextWidth = 0.6f;

	public const float MessageBoxLongTextHeight = 0.3f;

	/// <summary>
	/// `querybox.lua`'s `FRAME_SIZE`/`FRAME_OVERLAP` (`querybox.dis main pc5, pc8`). Nothing
	/// reads them and no `.bgf` loads the script; record only, do not compute with them.
	/// </summary>
	public const float QueryBoxFrameSize = 50f;

	public const float QueryBoxFrameOverlap = 30f;

	/// <summary>
	/// `QueryBox.Open` → slide 1, `Close` → slide 2 (`querybox.dis main.1 pc58..61`, `main.2
	/// pc0..3`). Root slide order: Intro, Outro, IntroXA, OutroXA, IntroReload, OutroReload
	/// (matches <see cref="Engine_UiQueryBox.ChoiceRows"/>).
	/// </summary>
	public const int QueryBoxIntroSlide = 1;

	public const int QueryBoxOutroSlide = 2;

	/// <summary>The router's slide for box 20 (`IntroXA`) and box 13 (`IntroReload`).</summary>
	public const int QueryBoxXASlide = 3;

	public const int QueryBoxReloadSlide = 5;

	/// <summary>
	/// The root slide a query box's opener stands, by modal id: the router's `lhGuiManager_OpenModal`
	/// (`sub_8236FA68`) slides the root for two ids only — 20 to slide 3 (TU1 0x8236FB00 `cmpwi
	/// r28,0x14`, 0x8236FB10 `li r5,3`, 0x8236FB18 `bl lhGuiElement_GoToSlide`; disc 0x82337CFC /
	/// 0x82337D0C / 0x82337D14), 13 to slide 5 (0x8236FB28 / 0x8236FB38 / 0x8236FB40; disc 0x82337D24
	/// / 0x82337D34 / 0x82337D3C) — and every other box keeps `QueryBox.Open`'s slide 1, as the
	/// display query does (`CQueryBox_vf1` 0x823CF284 `li r5,1`).
	/// </summary>
	public static int QueryBoxSlideFor(uint modalId, bool display) => display ? QueryBoxIntroSlide : modalId switch
	{
		20u => QueryBoxXASlide,
		13u => QueryBoxReloadSlide,
		_ => QueryBoxIntroSlide,
	};

	/// <summary>`page.lua` — which axes a page scrolls on.</summary>
	public enum PageType
	{
		Vertical = 1,
		Horizontal = 2,
		TwoDimensional = 3,
	}

	/// <summary>`page.lua` — scroll rate and the icon grid's cell size.</summary>
	public const float PageScrollSpeed = 15f;

	public const float PageIconSize = 180f;

	/// <summary>`page.lua` initial state: visual range 100, height 100 (ratio 1, nothing scrolls until measured).</summary>
	public const float PageDefaultVisualRange = 100f;

	public const float PageDefaultHeight = 100f;

	// `theme/themetext.lua` palette: aliases onto `Engine_UiThemeText`, the port of record.

	public static Godot.Color MenuItemDefaultText => Engine_UiThemeText.MenuItemDefault;

	public static Godot.Color MenuItemFolderText => Engine_UiThemeText.MenuItemFolder;

	public static Godot.Color MenuItemEmphasisedText => Engine_UiThemeText.MenuItemEmphasised;

	public static Godot.Color MenuItemGhostedText => Engine_UiThemeText.MenuItemGhosted;

	public static Godot.Color MenuItemHighlightedText => Engine_UiThemeText.MenuItemHighlighted;

	public static Godot.Color PageTitleText => Engine_UiThemeText.PageTitle;

	public static Godot.Color SubheadingText => Engine_UiThemeText.Subheading;

	public static Godot.Color NormalText => Engine_UiThemeText.Normal;

	public static Godot.Color NegativeText => Engine_UiThemeText.Negative;

}
