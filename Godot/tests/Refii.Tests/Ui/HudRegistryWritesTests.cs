namespace Refii.Tests.Ui;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;
using Refii.Ui;

/// <summary>
/// The GUI registries' writers as the HUD takes them (<see cref="Engine_UiHudRegistryWrites"/>):
/// a meter or text stated at its writer's entry reaches the HUD's values with nothing published
/// by the registry walk — the headless HUD's registries answer no names. No Godot type is built.
/// </summary>
[TestSuite]
public class HudRegistryWritesTests
{
	/// <summary>A guest whose registries publish nothing.</summary>
	private sealed class EmptySource : Engine_UiSource
	{
		public override Dictionary<uint, float> GetHudMeters() => new();
	}

	private static readonly Engine_HudValues.Handle FillPos = Engine_HudValues.Handle.For("ExpressionFillPos");
	private static readonly Engine_HudValues.Handle FillSize = Engine_HudValues.Handle.For("ExpressionFillSize");
	private static readonly Engine_HudValues.Handle Charge = Engine_HudValues.Handle.For("PlayerSpellcharge1");

	[TestCase]
	public void ASetSnapsAndReachesTheHudWithNoRegistryName()
	{
		Engine_UiSource.Current = new EmptySource();
		var values = new Engine_HudValues();

		// `sub_823D1F98` 0x823d2098: `sub_823FE210(reg[1], ExpressionFillPos, 50.0)`.
		Engine_UiHudRegistryWrites.StateMeter(values, 1, FillPos.Hash, "set", 50f, 0f, 0f);
		values.Advance(1f / 60f);
		values.Read();
		AssertThat(values.Meter(FillPos)).IsEqual(50f);
	}

	[TestCase]
	public void ARunMovesAtItsRateAndStopsOnTheTarget()
	{
		Engine_UiSource.Current = new EmptySource();
		var values = new Engine_HudValues();

		// `sub_823D1F98` 0x823d20f4: the sweet spot shrinks from its initial size toward its
		// minimum at a negated rate (`a2 * -1.0`), `sub_82265D20` stepping current by rate × dt.
		// The rate is per unit of the bindings' clock, a millisecond: -0.01 is -10 percent a second.
		Engine_UiHudRegistryWrites.StateMeter(values, 1, FillSize.Hash, "run", 30f, 10f, -0.01f);
		values.Advance(1f);
		AssertThat(values.StatedMeter(FillSize.Hash) ?? -1f).IsEqualApprox(20f, 0.01f);
		values.Advance(5f);
		AssertThat(values.StatedMeter(FillSize.Hash)).IsEqual(10f);
		values.Read();
		AssertThat(values.Meter(FillSize)).IsEqual(10f);
	}

	[TestCase]
	public void ASeekReachesItsTargetOverItsMilliseconds()
	{
		Engine_UiSource.Current = new EmptySource();
		var values = new Engine_HudValues();
		Engine_UiHudRegistryWrites.StateMeter(values, 1, Charge.Hash, "seek", 100f, 1000f, 0f);
		values.Advance(0.5f);
		AssertThat(values.StatedMeter(Charge.Hash) ?? -1f).IsEqualApprox(50f, 0.01f);
		values.Advance(1f);
		AssertThat(values.StatedMeter(Charge.Hash)).IsEqual(100f);
	}

	[TestCase]
	public void TheWriterClampsAsTheGuestDoes()
	{
		var values = new Engine_HudValues();
		Engine_UiHudRegistryWrites.StateMeter(values, 1, Charge.Hash, "run", 140f, -5f, 0f);
		AssertThat(values.StatedMeter(Charge.Hash)).IsEqual(100f);
		values.Advance(0.1f);
		AssertThat(values.StatedMeter(Charge.Hash)).IsEqual(0f);
	}

	[TestCase]
	public void OnlyTheMeterRegistrysNamesAreMeters()
	{
		var values = new Engine_HudValues();
		Engine_UiHudRegistryWrites.StateMeter(values, 4, Charge.Hash, "set", 50f, 0f, 0f);
		AssertThat(values.StatedMeter(Charge.Hash)).IsNull();
	}

	[TestCase]
	public void TheSlideRegistriesDriveTheirOwnBindingKinds()
	{
		AssertThat(Engine_UiHudRegistryWrites.SlideKind(2)).IsEqual(Engine_UiBindings.Kind.SlideFader);
		AssertThat(Engine_UiHudRegistryWrites.SlideKind(3)).IsEqual(Engine_UiBindings.Kind.SlideFader2);
		AssertThat(Engine_UiHudRegistryWrites.SlideKind(9)).IsEqual(Engine_UiBindings.Kind.SlideToggle);
		AssertThat(Engine_UiHudRegistryWrites.SlideKind(10)).IsEqual(Engine_UiBindings.Kind.SlideSet);
		AssertThat(Engine_UiHudRegistryWrites.SlideKind(1)).IsNull();
	}

	[TestCase]
	public void TheHooksNumbersParseInvariantly()
	{
		AssertThat(Engine_UiHudRegistryWrites.Number("1.5e-05")).IsEqualApprox(1.5e-5f, 1e-9f);
		AssertThat(Engine_UiHudRegistryWrites.Number("-12.25")).IsEqual(-12.25f);
		AssertThat(Engine_UiHudRegistryWrites.Number(null)).IsEqual(0f);
	}

	[TestCase]
	public void AStatedTextReachesTheHudWithoutTheBook()
	{
		Engine_UiSource.Current = new EmptySource();
		var values = new Engine_HudValues();
		var title = Engine_HudValues.Handle.For("TopTitle");
		values.StateText(title.Hash, "Blacksmith");
		values.Read();
		AssertThat(values.Text(title)).IsEqual("Blacksmith");
	}

	/// <summary>
	/// The TopBox's title is the front record's (`sub_821787D0` hands Babel(rec+4) to the box fit,
	/// which reaches the text registry only through the box's own record): stated from the record,
	/// blank when the tag has no string, so the previous box's title never stands under a new one.
	/// </summary>
	[TestCase]
	public void TheTopBoxStatesItsTitleFromTheRecord()
	{
		Engine_UiSource.Current = new EmptySource();
		Engine_UiHudTopBox.Log = _ => { };
		var values = new Engine_HudValues();
		var title = Engine_HudValues.Handle.For("TopTitle");
		values.StateText(title.Hash, "a previous box's title");
		var front = new Fable2Bridge.TopBoxFront(
			Known: true, Present: true, TitleTag: 0x1234u, ShowA: true, HoldA: true, ShowY: true,
			HoldY: false, Channel: 1, NameHash: 0xFC5B52CEu, LifetimeMs: -1000);
		Engine_UiHudTopBox.Publish(front, false, false, values, null, null, () => default);
		values.Read();
		AssertThat(values.Text(title)).IsEqual(string.Empty);
	}
}
