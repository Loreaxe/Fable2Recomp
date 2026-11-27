namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;
using Refii.Ui;

/// <summary>
/// The ported screens' book buttons as spine levers, and the credits roll's end as the guest's
/// update reaches it. Pure model code.
/// </summary>
[TestSuite]
public class PortedScreensTests
{
	[TestCase]
	public void TheLogbooksBookButtonsAreLevers()
	{
		AssertThat(Fable2PortedScreens.Lever(17, Engine_UiScreenInput.Back, 1)).IsEqual("PauseBack");
		AssertThat(Fable2PortedScreens.Lever(17, Engine_UiScreenInput.TabPrev, 1)).IsEqual("PauseCycle:1:prev");
		AssertThat(Fable2PortedScreens.Lever(17, Engine_UiScreenInput.TabNext, 2)).IsEqual("PauseCycle:2:next");
	}

	[TestCase]
	public void TheOptionsBooksButtonsAreLevers()
	{
		AssertThat(Fable2PortedScreens.Lever(18, Engine_UiScreenInput.Back, 1)).IsEqual("PauseBack");
		AssertThat(Fable2PortedScreens.Lever(18, Engine_UiScreenInput.TabNext, 1)).IsEqual("PauseCycle:1:next");
	}

	[TestCase]
	public void TheAbilitiesBooksButtonsAreLevers()
	{
		AssertThat(Fable2PortedScreens.Lever(14, Engine_UiScreenInput.Back, 1)).IsEqual("PauseBack");
		AssertThat(Fable2PortedScreens.Lever(14, Engine_UiScreenInput.TabPrev, 1)).IsEqual("PauseCycle:1:prev");
		AssertThat(Fable2PortedScreens.Lever(14, Engine_UiScreenInput.TabNext, 2)).IsEqual("PauseCycle:2:next");
		AssertThat(Fable2PortedScreens.Lever(14, Engine_UiScreenInput.Select, 1)).IsNull();
	}

	/// <summary>The row keeps its command as the identity mods read; the lever carries the player.</summary>
	[TestCase]
	public void AnAbilityRowPostsItsPlayer()
	{
		AssertThat(Fable2PortedScreens.Ability(1, "Ability:learn:7")).IsEqual("Ability:1:learn:7");
		AssertThat(Fable2PortedScreens.Ability(2, "Ability:unlearn:14")).IsEqual("Ability:2:unlearn:14");
		AssertThat(Fable2PortedScreens.Ability(1, "Mod:buy:Stamina")).IsNull();
		AssertThat(Fable2PortedScreens.Ability(1, "Ability:")).IsNull();
	}

	[TestCase]
	public void TheItemBooksButtonsAreLevers()
	{
		foreach (uint book in new[] { Fable2PortedScreens.Items, Fable2PortedScreens.Clothing, Fable2PortedScreens.WorldMap })
		{
			AssertThat(Fable2PortedScreens.Lever(book, Engine_UiScreenInput.Back, 1)).IsEqual("PauseBack");
			AssertThat(Fable2PortedScreens.Lever(book, Engine_UiScreenInput.TabPrev, 2)).IsEqual("PauseCycle:2:prev");
			AssertThat(Fable2PortedScreens.Lever(book, Engine_UiScreenInput.TabNext, 1)).IsEqual("PauseCycle:1:next");
			AssertThat(Fable2PortedScreens.Lever(book, Engine_UiScreenInput.Select, 1)).IsNull();
		}
	}

	/// <summary>The row keeps `Inventory:…` as the identity mods read; the lever carries the player, the
	/// row's ticket and its record.</summary>
	[TestCase]
	public void AnItemsRowPostsItsPlayerTicketAndRecord()
	{
		AssertThat(Fable2PortedScreens.ItemLever(15, 1, "Inventory:use:2512167794", 7u)).IsEqual("Items:1:use:7:2512167794");
		AssertThat(Fable2PortedScreens.ItemLever(15, 2, "Inventory:equip:12345", 0u)).IsEqual("Items:2:equip:0:12345");
		AssertThat(Fable2PortedScreens.ItemLever(15, 1, "Inventory:wear:12345", 3u)).IsNull();
	}

	[TestCase]
	public void AClothingRowPostsItsPlayerTicketAndRecord()
	{
		AssertThat(Fable2PortedScreens.ItemLever(16, 1, "Inventory:wear:12345", 4u)).IsEqual("Clothing:1:wear:4:12345");
		AssertThat(Fable2PortedScreens.ItemLever(16, 2, "Inventory:outfit:777", 9u)).IsEqual("Clothing:2:outfit:9:777");
		// A dog item's row is `equip` on the clothing book: the same row answer as a garment's.
		AssertThat(Fable2PortedScreens.ItemLever(16, 1, "Inventory:equip:555", 2u)).IsEqual("Clothing:1:wear:2:555");
		// A None row takes a slot off by its event type; it has no ticket.
		AssertThat(Fable2PortedScreens.ItemLever(16, 1, "Inventory:unequip:91", 0u)).IsEqual("Clothing:1:remove:91");
	}

	/// <summary>A gift row is the gift screen's own row answer; the shop's and the gift screen's B is the
	/// guest's back, and nothing else of theirs is a lever.</summary>
	[TestCase]
	public void TheGiftAndShopScreensPostTheirLevers()
	{
		AssertThat(Fable2PortedScreens.ItemLever(25, 1, "Inventory:gift:12345", 8u)).IsEqual("Gift:1:item:8:12345");
		AssertThat(Fable2PortedScreens.Lever(22, Engine_UiScreenInput.Back, 1)).IsEqual("Shop:1:close");
		AssertThat(Fable2PortedScreens.Lever(25, Engine_UiScreenInput.Back, 2)).IsEqual("Gift:2:close");
		AssertThat(Fable2PortedScreens.Lever(22, Engine_UiScreenInput.TabNext, 1)).IsNull();
		AssertThat(Fable2PortedScreens.Lever(25, Engine_UiScreenInput.Select, 1)).IsNull();
	}

	/// <summary>Upgrade furniture, decoration and real estate: B is the screen's own close, a row its
	/// screen's lever with the acting player; a real-estate row is its event (110..120) and never its
	/// name.</summary>
	[TestCase]
	public void TheHouseScreensPostTheirLevers()
	{
		AssertThat(Fable2PortedScreens.Lever(26, Engine_UiScreenInput.Back, 1)).IsEqual("Furniture:1:close");
		AssertThat(Fable2PortedScreens.Lever(27, Engine_UiScreenInput.Back, 1)).IsEqual("Decoration:1:close");
		AssertThat(Fable2PortedScreens.Lever(28, Engine_UiScreenInput.Back, 2)).IsEqual("RealEstate:2:close");
		AssertThat(Fable2PortedScreens.Lever(28, Engine_UiScreenInput.Select, 1)).IsNull();
		AssertThat(Fable2PortedScreens.Lever(27, Engine_UiScreenInput.Unlearn, 1)).IsNull();

		AssertThat(Fable2PortedScreens.RowLever(26, 1, "Furniture:upgrade:3", 0)).IsEqual("Furniture:1:upgrade:3");
		AssertThat(Fable2PortedScreens.RowLever(27, 1, "Decoration:mount:268435455", 0)).IsEqual("Decoration:1:mount:268435455");
		AssertThat(Fable2PortedScreens.RowLever(27, 2, "Decoration:takedown", 0)).IsEqual("Decoration:2:takedown");
		AssertThat(Fable2PortedScreens.RowLever(28, 1, "RealEstate:event", 111)).IsEqual("RealEstate:1:event:111");
		AssertThat(Fable2PortedScreens.RowLever(28, 2, "RealEstate:event", 120)).IsEqual("RealEstate:2:event:120");

		AssertThat(Fable2PortedScreens.RowLever(28, 1, "RealEstate:event", 0)).IsNull();
		AssertThat(Fable2PortedScreens.RowLever(28, 1, "RealEstate:event", 140)).IsNull();
		AssertThat(Fable2PortedScreens.RowLever(28, 1, "Furniture:upgrade:3", 111)).IsNull();
		AssertThat(Fable2PortedScreens.RowLever(26, 1, "Furniture:upgrade:", 0)).IsNull();
		AssertThat(Fable2PortedScreens.RowLever(26, 1, "Furniture:upgrade:x", 0)).IsNull();
		AssertThat(Fable2PortedScreens.RowLever(27, 1, "Decoration:mount:", 0)).IsNull();
		AssertThat(Fable2PortedScreens.RowLever(22, 1, "Shop:close", 0)).IsNull();
	}

	/// <summary>The crime screen's rows are their events (50 pay, 51 resist, 52 community service) and
	/// it has no B; the villager card's B and Y are its two levers; the HUD's two levers carry the
	/// player the guest's own gate then judges.</summary>
	[TestCase]
	public void TheCrimeScreenTheCardAndTheHudPostTheirLevers()
	{
		AssertThat(Fable2PortedScreens.RowLever(21, 1, "Crime:choice", 50)).IsEqual("Crime:1:pay");
		AssertThat(Fable2PortedScreens.RowLever(21, 2, "Crime:choice", 51)).IsEqual("Crime:2:resist");
		AssertThat(Fable2PortedScreens.RowLever(21, 1, "Crime:choice", 52)).IsEqual("Crime:1:service");
		AssertThat(Fable2PortedScreens.RowLever(21, 1, "Crime:choice", 0)).IsNull();
		AssertThat(Fable2PortedScreens.RowLever(21, 1, "Crime:choice", 140)).IsNull();
		AssertThat(Fable2PortedScreens.RowLever(21, 1, "Crime:pay", 50)).IsNull();
		AssertThat(Fable2PortedScreens.Lever(21, Engine_UiScreenInput.Back, 1)).IsNull();

		AssertThat(Fable2PortedScreens.RowLever(10, 1, "Query:close", 0)).IsEqual("Query:1:close");
		AssertThat(Fable2PortedScreens.RowLever(10, 2, "Query:rename", 0)).IsEqual("Query:2:rename");
		AssertThat(Fable2PortedScreens.RowLever(10, 1, "Query:other", 0)).IsNull();
		AssertThat(Fable2PortedScreens.RowLever(21, 1, "Query:close", 0)).IsNull();

		AssertThat(Fable2PortedScreens.SafetyToggle(1)).IsEqual("SafetyToggle:1");
		AssertThat(Fable2PortedScreens.TargetQuery(1)).IsEqual("TargetQuery:1");
	}

	/// <summary>A real-estate row's key is its event's name whatever its localised String1 holds (GitHub
	/// #81: a Russian label cut to bytes carries separators and control bytes, a CJK one may be empty);
	/// every other row keeps its stated name.</summary>
	[TestCase]
	public void ARealEstateRowIsKeyedByItsEvent()
	{
		AssertThat(Engine_UiMenuConstants.RealEstateRowName(110)).IsEqual("RealEstateSell");
		AssertThat(Engine_UiMenuConstants.RealEstateRowName(111)).IsEqual("RealEstateBuy");
		AssertThat(Engine_UiMenuConstants.RealEstateRowName(116)).IsEqual("RealEstateSetMarital");
		AssertThat(Engine_UiMenuConstants.RealEstateRowName(120)).IsEqual("RealEstateBack");
		AssertThat(Engine_UiMenuConstants.RealEstateRowName(109)).IsNull();
		AssertThat(Engine_UiMenuConstants.RealEstateRowName(121)).IsNull();
		AssertThat(Engine_UiMenuConstants.RealEstateRowName(0)).IsNull();
		AssertThat(Engine_UiMenuConstants.RealEstateRowName(301)).IsNull();
	}

	[TestCase]
	public void WhatIsNotAnItemBooksRowIsPostedAsItStands()
	{
		AssertThat(Fable2PortedScreens.ItemLever(22, 1, "Inventory:use:12345", 1u)).IsNull();
		AssertThat(Fable2PortedScreens.ItemLever(0, 1, "Inventory:use:12345", 0u)).IsNull();
		AssertThat(Fable2PortedScreens.ItemLever(16, 1, "Inventory:dye:11:22:1", 0u)).IsNull();
		AssertThat(Fable2PortedScreens.ItemLever(15, 1, "Inventory:augment:11:22", 0u)).IsNull();
		AssertThat(Fable2PortedScreens.ItemLever(15, 1, "Inventory:use:0", 0u)).IsNull();
		AssertThat(Fable2PortedScreens.ItemLever(15, 1, "Inventory:use:", 0u)).IsNull();
		AssertThat(Fable2PortedScreens.ItemLever(15, 1, "Ability:learn:7", 0u)).IsNull();
	}

	/// <summary>An item row's export words: flags bit 0 the row stands for an item, bit 1 viewed, bit 2 the
	/// count, category and viewed are the row builder's; no item without bit 0.</summary>
	[TestCase]
	public void ARowsItemIsItsBuildersStatement()
	{
		AssertThat(Engine_UiModel.MenuRowItem.FromWords(0u, 5u, 0x40001000u, 2u, 17u) is null).IsTrue();
		Engine_UiModel.MenuRowItem? item = Engine_UiModel.MenuRowItem.FromWords(1u | 2u | 4u, 0x12345678u, 0x40001000u, 3u, 17u);
		AssertThat(item is { Record: 0x12345678u, Instance: 0x40001000u, Count: 3, Category: 17, Viewed: true, Counted: true }).IsTrue();
		AssertThat(Engine_UiModel.MenuRowItem.FromWords(1u, 7u, 9u, 0u, 0u) is { Viewed: false, Counted: false }).IsTrue();
	}

	/// <summary>A category whose rows are a capturing source's under its own name takes the population's caption.</summary>
	[TestCase]
	public void AnItemBooksCategoryIsItsCapture()
	{
		Refii.Core.Engine_TitleGame.EnsureInstalled();
		AssertThat(Engine_UiMenuSources.CapturesFor("capture:Quests", "Quests")).IsTrue();
		AssertThat(Engine_UiMenuSources.CapturesFor("items:use:KeyItems", "KeyItems")).IsTrue();
		AssertThat(Engine_UiMenuSources.CapturesFor("items::Dye Hero Hair", "Dye Hero Hair")).IsTrue();
		AssertThat(Engine_UiMenuSources.CapturesFor("items:use:KeyItems", "Books")).IsFalse();
		AssertThat(Engine_UiMenuSources.CapturesFor("inventory:usable", "usable")).IsFalse();
	}

	/// <summary>Y on the clothing book is its own code 39 as the dye's second-layer lever; no other book's.</summary>
	[TestCase]
	public void TheClothingBooksYIsTheSecondDyeLayer()
	{
		AssertThat(Fable2PortedScreens.Lever(Fable2PortedScreens.Clothing, Engine_UiScreenInput.Unlearn, 2)).IsEqual("Clothing:2:dye2");
		AssertThat(Fable2PortedScreens.Lever(Fable2PortedScreens.Items, Engine_UiScreenInput.Unlearn, 1) is null).IsTrue();
	}

	[TestCase]
	public void ThePauseRootsBookButtonsAreLevers()
	{
		AssertThat(Fable2PortedScreens.Lever(0, Engine_UiScreenInput.Back, 1)).IsEqual("PauseClose:1");
		AssertThat(Fable2PortedScreens.Lever(0, Engine_UiScreenInput.Back, 2)).IsEqual("PauseClose:2");
		AssertThat(Fable2PortedScreens.Lever(0, Engine_UiScreenInput.TabPrev, 1)).IsEqual("PauseCycle:1:prev");
		AssertThat(Fable2PortedScreens.Lever(0, Engine_UiScreenInput.TabNext, 1)).IsEqual("PauseCycle:1:next");
		AssertThat(Fable2PortedScreens.Lever(0, Engine_UiScreenInput.Select, 1)).IsNull();
	}

	[TestCase]
	public void StartAndTheSavingRowsCarryThePlayer()
	{
		AssertThat(Fable2PortedScreens.Start(1)).IsEqual("Start:1");
		AssertThat(Fable2PortedScreens.Start(2)).IsEqual("Start:2");
		AssertThat(Fable2PortedScreens.Saving(1, "2")).IsEqual("PauseSaving:1:2");
	}

	/// <summary>A pad's own press acts for its local player; every pad of an online client is player
	/// two (the GUI input's vf+16 on a joiner).</summary>
	[TestCase]
	public void APadsPressActsForItsPlayer()
	{
		AssertThat(Fable2ActingPlayer.OfPad(false, 1)).IsEqual(1);
		AssertThat(Fable2ActingPlayer.OfPad(false, 2)).IsEqual(2);
		AssertThat(Fable2ActingPlayer.OfPad(true, 1)).IsEqual(2);
	}

	[TestCase]
	public void OtherButtonsAndScreensStayScreenInput()
	{
		AssertThat(Fable2PortedScreens.Lever(17, Engine_UiScreenInput.Select, 1)).IsNull();
		AssertThat(Fable2PortedScreens.Lever(22, Engine_UiScreenInput.TabNext, 1)).IsNull();
	}

	/// <summary>The guest quits once its wrap counter reaches lines + 24: wrap lines + 23, at
	/// 32 units each, 30 units a second.</summary>
	[TestCase]
	public void TheRollEndsAtTheGuestsLastWrap()
	{
		AssertThat(Engine_UiCreditsTiming.EndSeconds(0)).IsEqual(32.0 * 23 / 30.0);
		AssertThat(Engine_UiCreditsTiming.EndSeconds(100)).IsEqual(32.0 * 123 / 30.0);
	}
}
