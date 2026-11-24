namespace Refii.Tests.Titles;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// A stated opacity against a later told slide entry, in the guest's own order: GoTo applies the
/// entered slide's entries in the call (TU3 0x82267224 / 0x82267250 -> `sub_821CA6D8` -> the store
/// 0x821CA7A4), so BedMenu's Show — `.Sex` halved (0x82B8DBF8 -> SetOpacity 0x82B8E878), then
/// BedMenu's slide entered (0x82B8DC0C), whose entry sets `.Sex` to 100 — ends at 100.
/// No Godot type is built.
/// </summary>
[TestSuite]
public class Fable3StatedOpacityOrderTests
{
	[TestCase]
	public void AWriteBeforeAToldEntryYieldsToTheEntry()
	{
		var halved = new Fable3HudStatements.OpacitySource(0f, WriteStamp: 4, ActiveOff: false, ActiveStamp: 0);
		AssertThat(halved.After(opacityEntered: 0, activeEntered: 0)).IsEqual(0f);
		AssertThat(halved.After(opacityEntered: 5, activeEntered: 5)).IsNull();
	}

	[TestCase]
	public void AWriteAfterTheEntryStands()
	{
		var written = new Fable3HudStatements.OpacitySource(50f, WriteStamp: 6, ActiveOff: false, ActiveStamp: 0);
		AssertThat(written.After(opacityEntered: 5, activeEntered: 5)).IsEqual(50f);
	}

	[TestCase]
	public void ActiveOffYieldsOnlyToALaterEntryOfTheElement()
	{
		var off = new Fable3HudStatements.OpacitySource(80f, WriteStamp: 3, ActiveOff: true, ActiveStamp: 7);
		AssertThat(off.After(opacityEntered: 9, activeEntered: 5)).IsEqual(0f);
		AssertThat(off.After(opacityEntered: 5, activeEntered: 9)).IsNull();
		AssertThat(off.After(opacityEntered: 0, activeEntered: 9)).IsEqual(80f);
	}

	/// <summary>
	/// BedMenu's shape: the owner's slide 1 entry writes `.Sex`'s opacity 100 over the file's 0. Only a
	/// TOLD entry (the guest's slide call) is recorded against the statements; the walk's entry of the
	/// owner coming live is not the guest's call.
	/// </summary>
	[TestCase]
	public void AToldSlideEntryIsRecordedAtItsStatement()
	{
		Engine_BgfDocument doc = Engine_BgfEdit.NewDocument("Scene", 1280, 720, true);
		int menu = Engine_BgfEdit.AddChild(doc, 0, "BedMenu");
		int sex = Engine_BgfEdit.AddChild(doc, menu, "Sex");
		Engine_BgfEdit.SetFloat(doc, sex, "opacity", 0f);
		uint opacity = Engine_UiSlidePlayback.AttributeOf(Engine_UiSlidePlayback.Channel.Opacity);
		BgfSlideEntry SexAt(float value) => new()
		{
			ElementOrdinal = sex,
			Active = true,
			Commands = new[] { new BgfAttribute(opacity, BgfValueType.Float, BitConverter.SingleToUInt32Bits(value)) },
			AnimationEvents = Array.Empty<BgfAnimationEvent>(),
		};
		doc.Slides = new[]
		{
			new BgfSlide { Name = "Master Slide", OwnerElement = menu, Entries = Array.Empty<BgfSlideEntry>() },
			new BgfSlide { Name = "Sleep", OwnerElement = menu, Entries = new[] { SexAt(100f) }, EndTime = 1000 },
			new BgfSlide { Name = "Sex", OwnerElement = menu, Entries = new[] { SexAt(100f) }, EndTime = 1000 },
		};

		Engine_UiScreen screen = Engine_UiScreen.Parse("bedmenubox", Engine_BgfWriter.Write(doc), null);
		var play = new Engine_UiSlidePlayback(screen);
		int[] slides = new int[screen.Nodes.Length];
		slides[menu] = 1;

		// The walk's entry (no told slide): nothing recorded against the statements.
		play.Step(slides, null, 0, null, null, null, 3);
		AssertThat(play.ToldEntryWrote(sex, Engine_UiSlidePlayback.Channel.Opacity)).IsEqual(0UL);

		// The Show's SetStateAt, stated at 5, after the halving stated at 4.
		var told = new Engine_UiSlideStatements.Told(1, 5, null, 0);
		play.Step(slides, null, 10, null, null, i => i == menu ? told : null, 5);
		ulong entered = play.ToldEntryWrote(sex, Engine_UiSlidePlayback.Channel.Opacity);
		AssertThat(entered).IsEqual(5UL);
		AssertThat(play.ToldEntryActive(sex)).IsEqual(5UL);

		var halved = new Fable3HudStatements.OpacitySource(0f, WriteStamp: 4, ActiveOff: false, ActiveStamp: 0);
		AssertThat(halved.After(entered, play.ToldEntryActive(sex))).IsNull();
	}
}
