namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// The shop's and the pawn shop's stored fields (Fable3CommerceStores): each lands under its own
/// field and object; the flags read as 0 / 1; another model's statement is not taken. No Godot
/// type is built.
/// </summary>
[TestSuite]
public class Fable3CommerceStoresTests
{
	private static readonly List<string> Logged = new();

	private static Engine_UiModel.GuiNativeArgument S(string text) => new(Engine_UiModel.GuiNativeArgumentKind.String, text);

	private static Engine_UiModel.GuiNativeArgument N(float value) =>
		new(Engine_UiModel.GuiNativeArgumentKind.Number, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

	private static Engine_UiModel.GuiNativeArgument B(bool value) => new(Engine_UiModel.GuiNativeArgumentKind.Boolean, value ? "1" : "0");

	private static Engine_UiModel.GuiNativeCall Call(string name, params Engine_UiModel.GuiNativeArgument[] arguments) => new(name, arguments);

	[TestCase]
	public void EachFieldUnderItsObject()
	{
		Logged.Clear();
		Fable3Elements.Log = Logged.Add;
		Fable3CommerceStores.Clear();
		AssertThat(Fable3CommerceStores.Take(Call("@Shop.State", S("40500000"), N(1)))).IsTrue();
		Fable3CommerceStores.Take(Call("@Shop.StealAllowed", S("40500000"), B(true)));
		Fable3CommerceStores.Take(Call("@Pawn.State", S("40600000"), N(2)));
		Fable3CommerceStores.Take(Call("@Pawn.Page", S("40700000"), N(3)));
		Fable3CommerceStores.Take(Call("@Pawn.Category", S("40700000"), N(5)));

		AssertThat(Fable3CommerceStores.Of(Fable3CommerceStores.Field.ShopState, 0x40500000u)).IsEqual(1);
		AssertThat(Fable3CommerceStores.Of(Fable3CommerceStores.Field.ShopStealAllowed, 0x40500000u)).IsEqual(1);
		AssertThat(Fable3CommerceStores.Of(Fable3CommerceStores.Field.PawnState, 0x40600000u)).IsEqual(2);
		AssertThat(Fable3CommerceStores.Of(Fable3CommerceStores.Field.PawnPage, 0x40700000u)).IsEqual(3);
		AssertThat(Fable3CommerceStores.Of(Fable3CommerceStores.Field.PawnCategory, 0x40700000u)).IsEqual(5);
		AssertThat(Fable3CommerceStores.Of(Fable3CommerceStores.Field.ShopState, 0x40600000u)).IsNull();

		Fable3CommerceStores.Take(Call("@Shop.StealAllowed", S("40500000"), B(false)));
		AssertThat(Fable3CommerceStores.Of(Fable3CommerceStores.Field.ShopStealAllowed, 0x40500000u)).IsEqual(0);
		AssertThat(Fable3CommerceStores.Take(Call("@RealEstate.Row", S("40500000"), N(1), S("t"), S("i")))).IsFalse();
	}
}
