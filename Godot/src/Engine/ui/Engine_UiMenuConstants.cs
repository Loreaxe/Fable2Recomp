using Godot;

namespace Refii.Ui;

/// <summary>
/// `expandablemenustateandconstants.lua` and the formatter's tables, translated. Every number is
/// quoted from the shipped bytecode (`~/.cache/refii-scratch/lua/luadis2.py`, not the repo's
/// summariser), never measured off a run.
/// </summary>
public static class Engine_UiMenuConstants
{
	// ---------------------------------------------------------------------------------------
	// g_Menu.Const — expandablemenustateandconstants.lua
	// ---------------------------------------------------------------------------------------

	/// <summary>`Const.ROOTNAME`. The spawn passes it as String4 to mean "top level".</summary>
	public const string RootName = "Root";

	/// <summary>
	/// `Const.NUM_VISIBLE` = 10 — how many entries the window carries. Not `NUM_VIS_SLOTS` = 12:
	/// slots 1 and 12 are zero-opacity fade gutters, ten carry content.
	/// </summary>
	public const int NumVisible = 10;

	/// <summary>`Const.ONE_STAR_EMPTY_RATING_SLIDE`, `FIVE_STAR_FULL_RATING_SLIDE`,
	/// `EMPTY_RATING_SLIDE` — slide indices on a row's `Tab.Stars` group.</summary>
	public const int OneStarEmptyRatingSlide = 1;

	public const int FiveStarFullRatingSlide = 10;

	public const int EmptyRatingSlide = 11;

	/// <summary>`UPPER_HOTSPOT` / `LOWER_HOTSPOT` — where the window starts scrolling rather
	/// than moving the cursor within it.</summary>
	public const int UpperHotspot = 1;

	public const int LowerHotspot = 3;

	/// <summary>`ANIMATE_IN_TIME` / `ANIMATE_OUT_TIME` — an item entering or leaving the LIST,
	/// which is a different event from one changing slot.</summary>
	public const float AnimateInTime = 0.08f;

	public const float AnimateOutTime = 0.08f;

	/// <summary>`g_Menu.AutomaticGhostingOnSubmenus` starts TRUE; a screen turns it off through
	/// `TurnOffAutomaticGhostingOnSubmenus`.</summary>
	public const bool AutomaticGhostingOnSubmenusDefault = true;

	// ---------------------------------------------------------------------------------------
	// g_Menu.SlideConst / EmphasisConst / FolderConst
	// ---------------------------------------------------------------------------------------

	public const int SlideIntro = 1;

	public const int SlideOutro = 2;

	/// <summary>`FolderConst` — the submenu folder box's own geometry.</summary>
	public const float FolderMinWidth = 180f;

	public const float FolderMaxWidth = 280f;

	public const float FolderTextOffset = 25f;

	public const float FolderMaxHeight = 304f;

	public const float FolderItemGap = 60f;

	/// <summary>`g_Menu.EmphasisConst`.</summary>
	public enum Emphasis
	{
		None = 0,
		PrimaryQuest = 1,
		ActiveQuest = 2,
		SuggestedMenuItem = 3,
	}

	// ---------------------------------------------------------------------------------------
	// g_MenuFormatter — expandablemenuformatting.lua, the globals at chunk load
	// ---------------------------------------------------------------------------------------

	/// <summary>`HIGHLIGHT_INDEX` = 4 — the slot the cursor is pinned to, 1-based.</summary>
	public const int HighlightSlot = 4;

	/// <summary>`ANIMATE_TIME` = 0.15 — one entry changing slot; a plain clamped lerp, no
	/// easing curve.</summary>
	public const float AnimateTime = 0.15f;

	/// <summary>`NUM_VIS_SLOTS` = 12 — the slot TABLE's length. See <see cref="NumVisible"/>.</summary>
	public const int SlotCount = 12;

	/// <summary>
	/// `GetMenuItemTabOffset` — `-65 - 5*(depth-1)`, plus 24 when the item is NOT a heading.
	///
	/// `expandablemenuformatting.dis main.38` pc14..16 is `TEST A=7 C=1; JMP -> 17;
	/// ADD R3 = R3 + R6` with R6 = 24 (pc7); `C=1` skips the JMP when false, so the add is
	/// reached only when `IsHeading(item)` is false. Heading at depth 1 = -65; submenu row at
	/// depth 2 = `-65 - 5 + 24` = -46. `IsHeading` is `item.ParentItem == g_Menu.MenuRoot`
	/// (`main.34`), so a root entry is depth 1. Moves together with <see cref="HeadingLabelX"/>.
	/// </summary>
	public const float TabOffset = -65f;

	public const float TabOffsetPerDepth = -5f;

	/// <summary>The 24 at `main.38` pc7, added to a row that is NOT a heading.</summary>
	public const float HeadingTabBonus = 24f;

	/// <summary>`onInitialize`: `HEADER_SCALE` and `DEFAULT_SCALE`. `GetMenuItemScaleDelta`
	/// returns these MINUS ONE, added to the slot's own scale — so a root row in slot 4 draws
	/// at 1.025 and a submenu row at 0.925.</summary>
	public const float HeaderScale = 1.025f;

	public const float DefaultScale = 0.925f;

	/// <summary>
	/// `SetAttributesToVisibleIndex` puts `Icon.Title` at these x. `main.33`, same `TEST … C=1`
	/// polarity as the tab offset — the JMP is taken when `IsHeading` is TRUE and lands on -30:
	/// <code>
	///  96  TEST A=7 C=1     ; R7 = IsHeading(item)
	///  97  JMP  -> 104
	///  98  setAttribute(label, 'position.x', -54)   ; child
	/// 104  setAttribute(label, 'position.x', -30)   ; heading
	/// </code>
	/// These nearly cancel against the tab offset for the label's absolute x (heading -95, child
	/// at depth 2 -100), so this and <see cref="Engine_UiMenuFormatter.TabOffsetFor"/> must move
	/// together — fixing either alone reintroduces the error at full size.
	/// </summary>
	public const float HeadingLabelX = -30f;

	public const float ChildLabelX = -54f;

	/// <summary>
	/// `SetGhosting` — a live entry's title colour at full opacity; a ghosted HEADING keeps the
	/// colour at opacity 40; a ghosted CHILD goes black at 90.
	/// </summary>
	public static readonly Color LiveTextColour = Rgb(255, 224, 128);

	public static readonly Color GhostedChildColour = Rgb(0, 0, 0);

	public const float GhostedHeadingOpacity = 40f;

	public const float GhostedChildOpacity = 90f;

	/// <summary>
	/// `UpdateEmphasisForItem` / `UpdateTabInnerTextureSubMenu` — the border tints, as
	/// `SetTintingAmountAndDiffuseColour(GetFirstChild(border), amount, r, g, b)`. A root row is
	/// `Tab.InnerRed` 100 / `Tab.InnerBrown` 0 at emphasis NONE and swaps for any other; a
	/// submenu row is always brown and its emphasis picks the tint.
	/// </summary>
	public static (float Amount, Color Colour) SubmenuTint(Emphasis emphasis) => emphasis switch
	{
		Emphasis.PrimaryQuest => (0.9f, Rgb(40, 40, 120)),
		Emphasis.ActiveQuest => (0.9f, Rgb(10, 10, 40)),
		Emphasis.SuggestedMenuItem => (0f, Rgb(255, 255, 255)),
		_ => (0.9f, Rgb(30, 30, 30)),
	};

	/// <summary>A heading's own mark tint, when its emphasis is not NONE.</summary>
	public static (float Amount, Color Colour) HeadingTint(bool marked) =>
		marked ? (0.9f, Rgb(10, 10, 40)) : (0f, Rgb(255, 255, 255));

	// ---------------------------------------------------------------------------------------
	// The slot tables
	// ---------------------------------------------------------------------------------------

	/// <summary>One entry of `g_MenuOpacityScaleSlots`.</summary>
	public readonly record struct Style(float Scale, float Opacity, int FontSize, float Truncate);

	/// <summary>
	/// `g_MenuOpacityScaleSlots`, five entries. Point size is 18 in every record, so a row
	/// changing slot never re-lays its glyphs. `truncate` is a clip width nothing here applies
	/// (wrap mode 0 is one unfolded line), so a long label overhangs.
	/// </summary>
	public static readonly Style[] Styles =
	{
		new(0.65f, 0f, 18, 200f),
		new(0.75f, 50f, 18, 210f),
		new(0.85f, 75f, 18, 240f),
		new(1f, 100f, 18, 305f),
		new(0.65f, 0f, 18, 200f),
	};

	/// <summary>One entry of `g_MenuPositionSlots`: where a slot sits and which style it wears.</summary>
	public readonly record struct SlotRecord(Vector2 Position, int StyleIndex)
	{
		/// <summary>The style, resolved. `opacity_scale_index` is 1-based in the script.</summary>
		public Style Style => Styles[StyleIndex - 1];
	}

	/// <summary>
	/// `g_MenuPositionSlots`, twelve entries, ALREADY REBASED by the chunk's load-time
	/// `x + 345, y - 185` loop (opcodes 132-147) — the values the formatter uses.
	///
	/// The column is not straight: x runs 0, 22, 42, 56 … 56, 50, 42, 22, 0, so the list bows
	/// around the highlight. Slots 1 and 12 are style 1 (scale 0.65, opacity 0) fade gutters,
	/// 2 and 11 are 50% at 0.75, 3 and 10 are 75% at 0.85, 4..9 full.
	/// </summary>
	public static readonly SlotRecord[] Slots =
	{
		new(new Vector2(0f, 85f), 1),
		new(new Vector2(22f, 69f), 2),
		new(new Vector2(42f, 15f), 3),
		new(new Vector2(56f, -45f), 4),
		new(new Vector2(56f, -106f), 4),
		new(new Vector2(56f, -164f), 4),
		new(new Vector2(56f, -222f), 4),
		new(new Vector2(56f, -280f), 4),
		new(new Vector2(50f, -338f), 4),
		new(new Vector2(42f, -396f), 3),
		new(new Vector2(22f, -454f), 2),
		new(new Vector2(0f, -475f), 1),
	};

	/// <summary>
	/// The three border pieces of each tab face, by element name. The two faces spell their parts
	/// differently as authored; the names are how the tint reaches them, so do not normalise.
	/// </summary>
	public static readonly string[] BrownBorders = { "left", "bar", "right" };

	public static readonly string[] RedBorders = { "LeftBorder", "BarBorder", "RightBorder" };


	private static Color Rgb(float r, float g, float b) => new(r / 255f, g / 255f, b / 255f);

	// ---------------------------------------------------------------------------------------
	// g_Menu.EventConst — every event type a row can carry
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// The name a real-estate row is keyed by: its event's own name (`RealEstateBuy` .. `RealEstateBack`,
	/// 110..120), the identity the guest's row builder states (`sub_82998300`: each row is
	/// `sub_826CE848(list, tag, icon, "Root", 0, event, …)`). Null for any other event type: that row
	/// keeps its stated name.
	/// </summary>
	public static string? RealEstateRowName(int eventType) =>
		eventType is >= (int)EventType.RealEstateSell and <= (int)EventType.RealEstateBack
			? ((EventType)eventType).ToString()
			: null;

	/// <summary>
	/// `g_Menu.EventConst`, complete — half the row's identity. `SelectMenuItem` commits
	/// `PushButtonEvent(Name, EventType, IDLow, IDHigh)` and the receiving screen switches on
	/// this alone (`sub_826C2660` reads `*(event+4)`).
	/// </summary>
	public enum EventType
	{
		MenuItemNode = 0,
		MenuItemFolder = 1,
		MenuButton = 2,
		InventoryItem = 3,
		QuestItem = 4,
		SuggestionItem = 5,
		SpellItem = 6,
		GoldItem = 7,

		StrengthAbilityItem = 20,
		SkillAbilityItem = 21,
		WillAbilityItem = 22,
		AssignSpellsAbilityItem = 23,

		BuyInventoryItemFromShop = 30,
		SellInventoryItemToShop = 31,

		WorldMapRegionsItem = 41,
		WorldMapQuestsItem = 42,
		WorldMapJobsItem = 43,
		WorldMapSalesItem = 44,
		WorldMapFamilyItem = 45,
		WorldMapFriendsItem = 46,

		CrimePayFineItem = 50,
		CrimeResistArrestItem = 51,
		CrimeCommunityServiceItem = 52,

		LogbookPersonalityItem = 60,
		LogbookExpressionItem = 61,
		LogbookCompletedQuestItem = 62,
		LogbookStorySoFarItem = 63,
		LogbookPersonalityMoralityItem = 64,
		LogbookPersonalityRenownItem = 65,
		LogbookPersonalityAttractivenessItem = 66,
		LogbookPersonalityStatsItem = 67,
		LogbookPersonalityDogItem = 68,

		OptionsGameItem = 70,
		OptionsControlsItem = 71,
		OptionsVideoItem = 72,
		OptionsAudioItem = 73,

		InventoryItemDyeClothing = 80,
		InventoryItemDyeHero = 81,
		InventoryItemAugment = 83,
		InventoryItemApplyDyeClothing = 84,
		InventoryItemApplyAugment = 85,
		InventoryItemApplyDyeHair = 86,
		InventoryItemApplyDyeBeard = 87,
		InventoryItemApplyDyeMustache = 88,
		InventoryItemClothingSuit = 89,
		InventoryItemBirthdaySuit = 90,
		InventoryItemRemoveHats = 91,
		InventoryItemRemoveCoats = 92,
		InventoryItemRemoveShirts = 93,
		InventoryItemRemoveGloves = 94,
		InventoryItemRemovePants = 95,
		InventoryItemRemoveShoes = 96,
		InventoryItemRemoveAccessories = 97,

		RealEstateSell = 110,
		RealEstateBuy = 111,
		RealEstateRentOut = 112,
		RealEstateStartRedecorate = 113,
		RealEstateEndRedecorate = 114,
		RealEstateAdjustBudget = 115,
		RealEstateSetMarital = 116,
		RealEstateAdjustRent = 117,
		RealEstateAdjustProfits = 118,
		RealEstateEvictTennant = 119,
		RealEstateBack = 120,

		CoopScreenLoadSave = 130,

		MenuScrollUp = 140,
		MenuScrollDown = 141,

		LogbookAchievements = 150,

		Navigation = 201,
		ButtonRemove = 202,
		ButtonSet = 203,

		EnterScreen = 300,
		Submenu = 301,
		LanguageSelection = 302,

		ItemAugmentRemover = 310,
		ItemApplyAugmentRemover = 311,
	}
}
