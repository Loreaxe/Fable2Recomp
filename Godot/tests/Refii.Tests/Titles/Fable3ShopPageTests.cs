namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// The shop page's pure rules (Fable3ShopPageRules, Fable3HudRegistry.DrawnPanel, Fable3CommerceStores'
/// open shop): the page is the shop's in states 1..3 alone; the document rests the info box's
/// shop slide closed only while the page draws it; a line shows unless its writer hid it and it
/// holds a text or a detail; the open shop follows the state stores and ends at the close. No Godot
/// type is built.
/// </summary>
[TestSuite]
public class Fable3ShopPageTests
{
	private static readonly List<string> Logged = new();

	private static Engine_UiModel.GuiNativeArgument S(string text) => new(Engine_UiModel.GuiNativeArgumentKind.String, text);

	private static Engine_UiModel.GuiNativeArgument N(float value) =>
		new(Engine_UiModel.GuiNativeArgumentKind.Number, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

	private static Engine_UiModel.GuiNativeCall Call(string name, params Engine_UiModel.GuiNativeArgument[] arguments) => new(name, arguments);

	[TestCase]
	public void ThePageIsTheShopsInStatesOneToThree()
	{
		AssertThat(Fable3ShopPageRules.Owns(0x40500000u, 1)).IsTrue();
		AssertThat(Fable3ShopPageRules.Owns(0x40500000u, 3)).IsTrue();
		AssertThat(Fable3ShopPageRules.Owns(0x40500000u, 0)).IsFalse();
		AssertThat(Fable3ShopPageRules.Owns(0x40500000u, 4)).IsFalse();
		AssertThat(Fable3ShopPageRules.Owns(0x40500000u, null)).IsFalse();
		AssertThat(Fable3ShopPageRules.Owns(0u, 1)).IsFalse();
	}

	[TestCase]
	public void TheDocumentRestsOnlyTheShopSlide()
	{
		AssertThat(Fable3HudRegistry.DrawnPanel(2, shopPageOwned: true)).IsEqual(1);
		AssertThat(Fable3HudRegistry.DrawnPanel(2, shopPageOwned: false)).IsEqual(2);
		AssertThat(Fable3HudRegistry.DrawnPanel(5, shopPageOwned: true)).IsEqual(5);
		AssertThat(Fable3HudRegistry.DrawnPanel(6, shopPageOwned: true)).IsEqual(6);
	}

	[TestCase]
	public void ALineShowsUnlessHiddenAndWhenItHoldsSomething()
	{
		var elements = new Dictionary<string, (bool? Visible, string? Text)>
		{
			[Fable3ShopPageRules.Line(1) + ".Text"] = (null, "Sword"),
			[Fable3ShopPageRules.Line(1) + ".Detail"] = (true, "120"),
			[Fable3ShopPageRules.Line(2) + ".Text"] = (false, "Steal"),
			[Fable3ShopPageRules.Line(3)] = (false, null),
			[Fable3ShopPageRules.Line(3) + ".Text"] = (true, "Hidden line"),
			[Fable3ShopPageRules.Line(4) + ".Detail"] = (null, "x2"),
		};
		List<Fable3ShopPageRules.PageLine> lines = Fable3ShopPageRules.LinesOf(path => elements.TryGetValue(path, out var e) ? e : (null, null));
		AssertThat(lines.Count).IsEqual(2);
		AssertThat(lines[0]).IsEqual(new Fable3ShopPageRules.PageLine("Sword", "120"));
		AssertThat(lines[1]).IsEqual(new Fable3ShopPageRules.PageLine(string.Empty, "x2"));
	}

	[TestCase]
	public void TheOpenShopFollowsItsStateAndEndsAtTheClose()
	{
		Logged.Clear();
		Fable3Elements.Log = Logged.Add;
		Fable3CommerceStores.Clear();
		Fable3CommerceStores.Take(Call("@Shop.State", S("40500000"), N(0)));
		AssertThat(Fable3CommerceStores.OpenShop).IsEqual(0x40500000u);
		Fable3CommerceStores.Take(Call("@Shop.State", S("40500000"), N(1)));
		AssertThat(Fable3CommerceStores.Take(Call("@Shop.Close", S("40600000")))).IsTrue();
		AssertThat(Fable3CommerceStores.OpenShop).IsEqual(0x40500000u);
		Fable3CommerceStores.Take(Call("@Shop.Close", S("40500000")));
		AssertThat(Fable3CommerceStores.OpenShop).IsEqual(0u);
		AssertThat(Fable3CommerceStores.Of(Fable3CommerceStores.Field.ShopState, 0x40500000u)).IsNull();
	}
}
