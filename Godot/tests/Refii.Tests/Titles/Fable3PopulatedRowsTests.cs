namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// The populated rows (Fable3PopulatedRows: the real-estate and crime modes'): rows add up per mode until a clear, as
/// the guest's vector does; the list is the mode's only once its populate said so and while the
/// guest writes a list; the close and a new mode end them; the lever names the player and the id;
/// a label is the tag's text in the book, else the tag. No Godot type is built.
/// </summary>
[TestSuite]
public class Fable3PopulatedRowsTests
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
		Fable3UiStatements.Clear();
		Fable3PopulatedRows.Clear();
	}

	private static void ListUp(bool up)
	{
		Fable3UiStatements.Take(Call("@Interaction.ListUp", B(up)));
		Fable3PopulatedRows.Observe(up);
	}

	private static void Row(string mode, int id, string tag) =>
		Fable3PopulatedRows.TakeAny(Call("@RealEstate.Row", S(mode), N(id), S(tag), S("icon")));

	[TestCase]
	public void RowsListOnlyOnceThePopulateSaidSoAndAListIsUp()
	{
		Fresh();
		Row("40100000", 2, "GUI_SCREEN_REAL_ESTATE_BUY");
		Row("40100000", 11, "TEXT_GUI_REAL_ESTATE_REPAIR");
		AssertThat(Fable3PopulatedRows.RealEstate.Listing).IsFalse();

		Fable3PopulatedRows.TakeAny(Call("@RealEstate.Listed", S("40100000")));
		AssertThat(Fable3PopulatedRows.RealEstate.Listing).IsFalse();

		ListUp(true);
		AssertThat(Fable3PopulatedRows.RealEstate.Listing).IsTrue();
		AssertThat(Fable3PopulatedRows.RealEstate.Rows.Count).IsEqual(2);
		AssertThat(Fable3PopulatedRows.RealEstate.At(0, 1)?.Id).IsEqual(11);
		AssertThat(Fable3PopulatedRows.RealEstate.At(1, 1)).IsNull();

		// The list goes down: another mode's list is not this one's until its populate speaks again.
		ListUp(false);
		AssertThat(Fable3PopulatedRows.RealEstate.Listing).IsFalse();
		ListUp(true);
		AssertThat(Fable3PopulatedRows.RealEstate.Listing).IsFalse();
	}

	[TestCase]
	public void RowsAddUpUntilAClearAndEndWithTheClose()
	{
		Fresh();
		ListUp(true);
		Row("40100000", 1, "GUI_SCREEN_REAL_ESTATE_SELL");
		Fable3PopulatedRows.TakeAny(Call("@RealEstate.Listed", S("40100000")));
		Row("40100000", 5, "GUI_SCREEN_REAL_ESTATE_RENT_OUT");
		Fable3PopulatedRows.TakeAny(Call("@RealEstate.Listed", S("40100000")));
		AssertThat(Fable3PopulatedRows.RealEstate.Rows.Count).IsEqual(2);

		// Another mode's clear and close are not this one's.
		Fable3PopulatedRows.TakeAny(Call("@RealEstate.Rows.Clear", S("40200000")));
		Fable3PopulatedRows.TakeAny(Call("@RealEstate.Close", S("40200000")));
		AssertThat(Fable3PopulatedRows.RealEstate.Rows.Count).IsEqual(2);

		Fable3PopulatedRows.TakeAny(Call("@RealEstate.Rows.Clear", S("40100000")));
		AssertThat(Fable3PopulatedRows.RealEstate.Rows.Count).IsEqual(0);
		AssertThat(Fable3PopulatedRows.RealEstate.Listing).IsFalse();

		Row("40100000", 6, "GUI_SCREEN_REAL_ESTATE_SET_BUDGET");
		Fable3PopulatedRows.TakeAny(Call("@RealEstate.Listed", S("40100000")));
		AssertThat(Fable3PopulatedRows.RealEstate.Listing).IsTrue();
		Fable3PopulatedRows.TakeAny(Call("@RealEstate.Close", S("40100000")));
		AssertThat(Fable3PopulatedRows.RealEstate.Listing).IsFalse();
		AssertThat(Fable3PopulatedRows.RealEstate.Mode).IsEqual(0u);
	}

	[TestCase]
	public void ANewModeStartsClean()
	{
		Fresh();
		Row("40100000", 1, "GUI_SCREEN_REAL_ESTATE_SELL");
		Row("40300000", 2, "GUI_SCREEN_REAL_ESTATE_BUY");
		AssertThat(Fable3PopulatedRows.RealEstate.Mode).IsEqual(0x40300000u);
		AssertThat(Fable3PopulatedRows.RealEstate.Rows.Count).IsEqual(1);
		AssertThat(Fable3PopulatedRows.RealEstate.Rows[0].Tag).IsEqual("GUI_SCREEN_REAL_ESTATE_BUY");
	}

	[TestCase]
	public void TheLeverAndTheLabel()
	{
		AssertThat(Fable3PopulatedRows.RealEstate.Lever(Fable3ActingPlayer.One, 2)).IsEqual("realestate:1:row:2");
		AssertThat(Fable3PopulatedRows.RealEstate.Lever(Fable3ActingPlayer.Two, 14)).IsEqual("realestate:2:row:14");
		var row = new Fable3PopulatedRows.Row(2, "GUI_SCREEN_REAL_ESTATE_BUY", string.Empty);
		AssertThat(Fable3PopulatedRows.Label(row, tag => tag == "GUI_SCREEN_REAL_ESTATE_BUY" ? "Buy" : null)).IsEqual("Buy");
		AssertThat(Fable3PopulatedRows.Label(row, _ => null)).IsEqual("GUI_SCREEN_REAL_ESTATE_BUY");
	}

	[TestCase]
	public void CrimeRowsAreCrimesAndClearAtItsPopulate()
	{
		Fresh();
		ListUp(true);
		Fable3PopulatedRows.TakeAny(Call("@Crime.Rows.Clear", S("40800000")));
		Fable3PopulatedRows.TakeAny(Call("@Crime.Row", S("40800000"), N(0), S("TEXT_GUI_SCREEN_CRIME_PAY_FINE"), S("i")));
		Fable3PopulatedRows.TakeAny(Call("@Crime.Row", S("40800000"), N(2), S("TEXT_GUI_SCREEN_CRIME_RESIST_ARREST"), S("i")));
		Fable3PopulatedRows.TakeAny(Call("@Crime.Listed", S("40800000")));
		AssertThat(Fable3PopulatedRows.Up).IsEqual(Fable3PopulatedRows.Crime);
		AssertThat(Fable3PopulatedRows.Crime.AsksById).IsFalse();
		AssertThat(Fable3PopulatedRows.Crime.Lever(Fable3ActingPlayer.One, 0)).IsNull();
		AssertThat(Fable3PopulatedRows.RealEstate.Listing).IsFalse();

		// The next populate empties them first.
		Fable3PopulatedRows.TakeAny(Call("@Crime.Rows.Clear", S("40800000")));
		AssertThat(Fable3PopulatedRows.Crime.Rows.Count).IsEqual(0);
		AssertThat(Fable3PopulatedRows.Up).IsNull();
	}

	[TestCase]
	public void OnlyItsOwnStatements()
	{
		Fresh();
		AssertThat(Fable3PopulatedRows.TakeAny(Call("@Interaction.ListUp", B(true)))).IsFalse();
	}
}
