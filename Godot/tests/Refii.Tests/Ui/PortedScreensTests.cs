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
		foreach (uint book in new[] { Fable2PortedScreens.Items, Fable2PortedScreens.Clothing })
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

	[TestCase]
	public void WhatIsNotAnItemBooksRowIsPostedAsItStands()
	{
		AssertThat(Fable2PortedScreens.ItemLever(25, 1, "Inventory:gift:12345", 1u)).IsNull();
		AssertThat(Fable2PortedScreens.ItemLever(0, 1, "Inventory:use:12345", 0u)).IsNull();
		AssertThat(Fable2PortedScreens.ItemLever(16, 1, "Inventory:dye:11:22:1", 0u)).IsNull();
		AssertThat(Fable2PortedScreens.ItemLever(15, 1, "Inventory:augment:11:22", 0u)).IsNull();
		AssertThat(Fable2PortedScreens.ItemLever(15, 1, "Inventory:use:0", 0u)).IsNull();
		AssertThat(Fable2PortedScreens.ItemLever(15, 1, "Inventory:use:", 0u)).IsNull();
		AssertThat(Fable2PortedScreens.ItemLever(15, 1, "Ability:learn:7", 0u)).IsNull();
	}

	/// <summary>The ticket is the id the guest spawned the row of that name with; the first when two
	/// rows share a name, none when the capture holds no such row.</summary>
	[TestCase]
	public void ARowsTicketIsItsCapturedId()
	{
		var captured = new System.Collections.Generic.List<Engine_UiModel.MenuRow>
		{
			new("Items", "Root", 0u, 301, 0, "Items"),
			new("INV_ITEM_HAT", "Hats", 5u, 3, 0, "Hat"),
			new("INV_ITEM_COAT", "Coats", 6u, 3, 0, "Coat"),
			new("INV_ITEM_HAT", "Hats", 9u, 3, 0, "Hat"),
		};
		AssertThat(Fable2PortedScreens.TicketOf(captured, "INV_ITEM_COAT")).IsEqual(6u);
		AssertThat(Fable2PortedScreens.TicketOf(captured, "INV_ITEM_HAT")).IsEqual(5u);
		AssertThat(Fable2PortedScreens.TicketOf(captured, "INV_ITEM_BOOTS")).IsEqual(0u);
		AssertThat(Fable2PortedScreens.TicketOf(captured, string.Empty)).IsEqual(0u);
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
