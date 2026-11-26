namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Game;
using Refii.Ui;

/// <summary>
/// A key action's pad chords: the <c>Pad:</c> tokens of its setting, when a chord is held, what it
/// withholds, the Controls page's controller row, and that the shell's own table declares none.
/// </summary>
[TestSuite]
public class PadActionBindingTests
{
	private static ushort Mask(params PadInput[] inputs) => (ushort)inputs.Aggregate(0, (mask, input) => mask | (1 << (int)input));

	[TestCase]
	public void TheKeysAreTheSettingWithoutItsPadTokens()
	{
		AssertThat(Engine_PadActionBinding.KeysOf("F9")).IsEqual("F9");
		AssertThat(Engine_PadActionBinding.KeysOf(" \"E,Space\" ")).IsEqual("E,Space");
		AssertThat(Engine_PadActionBinding.KeysOf("Pad:Back+Y")).IsEqual(string.Empty);
		AssertThat(Engine_PadActionBinding.KeysOf("none, pad:R3")).IsEqual("none");
		AssertThat(Engine_PadActionBinding.KeysOf("F8,Pad:Back+Y,K")).IsEqual("F8,K");
		AssertThat(Engine_PadActionBinding.KeysOf(null)).IsEqual(string.Empty);
	}

	[TestCase]
	public void AChordIsEveryNamedGameButton()
	{
		IReadOnlyList<ushort> chords = Engine_PadActionBinding.Chords("F9,Pad:Back+Y,pad:r3,Pad:Nothing,Pad:");
		AssertThat(chords.ToArray()).IsEqual(new[] { Mask(PadInput.Back, PadInput.Y), Mask(PadInput.R3) });
		AssertThat(Engine_PadActionBinding.Chords("F9").Count).IsEqual(0);
		AssertThat(Engine_PadActionBinding.Format(chords)).IsEqual("Pad:Y+Back,Pad:R3");
		AssertThat(Engine_PadActionBinding.Chords(Engine_PadActionBinding.Format(chords)).ToArray()).IsEqual(chords.ToArray());
	}

	[TestCase]
	public void TheSettingIsItsKeysAndItsChords()
	{
		AssertThat(Engine_PadActionBinding.Combine(null, string.Empty)).IsNull();
		AssertThat(Engine_PadActionBinding.Combine("none", string.Empty)).IsEqual("none");
		AssertThat(Engine_PadActionBinding.Combine(null, "Pad:R3")).IsEqual("Pad:R3");
		AssertThat(Engine_PadActionBinding.Combine("F8", "Pad:R3")).IsEqual("F8,Pad:R3");
	}

	[TestCase]
	public void AChordIsHeldOnlyWithEveryInputDownAndTakesOnlyItsOwnInputs()
	{
		IReadOnlyList<ushort> chords = new[] { Mask(PadInput.Back, PadInput.Y), Mask(PadInput.LT, PadInput.A) };
		ushort one = Engine_PadActionBinding.Held(0x0020u, 0, 0);
		AssertThat(Engine_PadActionBinding.Active(chords, one)).IsEqual((ushort)0);

		// Back + Y + X down: the chord is Back and Y; X stays the game's.
		uint buttons = 0x0020u | 0x8000u | 0x4000u, lt = 20, rt = 255;
		ushort held = Engine_PadActionBinding.Held(buttons, lt, rt);
		ushort active = Engine_PadActionBinding.Active(chords, held);
		AssertThat(active).IsEqual(Mask(PadInput.Back, PadInput.Y));
		Engine_PadActionBinding.Withhold(active, ref buttons, ref lt, ref rt);
		AssertThat(buttons).IsEqual(0x4000u);
		AssertThat(rt).IsEqual(255u);

		// A trigger counts past the XInput threshold, and a held trigger chord rests the trigger.
		buttons = 0x1000u;
		lt = 200;
		active = Engine_PadActionBinding.Active(chords, Engine_PadActionBinding.Held(buttons, lt, 0));
		AssertThat(active).IsEqual(Mask(PadInput.LT, PadInput.A));
		Engine_PadActionBinding.Withhold(active, ref buttons, ref lt, ref rt);
		AssertThat(buttons).IsEqual(0u);
		AssertThat(lt).IsEqual(0u);
	}

	[TestCase]
	public void AnActionsKeysIgnoreItsPadTokens()
	{
		var action = new GameInput.KeyAction("quick_save", "F9", "Save.") { PadBindable = true };
		AssertThat(action.IsDefault("Pad:Back+Y")).IsTrue();
		AssertThat(action.Bound("Pad:Back+Y")).IsEqual("F9");
		AssertThat(action.Bound("F8,Pad:Back+Y")).IsEqual("F8");
		AssertThat(action.IsDefault("none,Pad:R3")).IsFalse();
		// A setting with no token is handed back as stored.
		AssertThat(action.Bound(" F8 ")).IsEqual(" F8 ");
	}

	private static Func<string, string> Stored(string key, string value) => k => k == key ? value : string.Empty;

	private static Engine_UiControlsPage.PadActionLine Row() =>
		new("quick_save", () => GlyphStyle.Xbox, () => false);

	[TestCase]
	public void TheControllerRowIsUnboundByDefaultAndStoresBesideTheKeys()
	{
		Engine_UiControlsPage.PadActionLine row = Row();
		AssertThat(row.Label).IsEqual("Quick save (controller)");
		AssertThat(row.DefaultIndex).IsEqual(Engine_UiControlsPage.PadActionLine.UnboundIndex);
		AssertThat(row.IndexOfStored(_ => string.Empty)).IsEqual(Engine_UiControlsPage.PadActionLine.UnboundIndex);
		AssertThat(row.IndexOfStored(Stored("key_quick_save", "F8"))).IsEqual(Engine_UiControlsPage.PadActionLine.UnboundIndex);
		AssertThat(row.Caption(Engine_UiControlsPage.PadActionLine.UnboundIndex)).IsEqual("Unbound");

		int r3 = row.IndexOfStored(Stored("key_quick_save", "F8,Pad:R3"));
		AssertThat(r3).IsEqual(Engine_UiControlsPage.PadActionLine.FirstInput + (int)PadInput.R3);

		// The key row stores first: the default keys (null), then a key; the chord joins either.
		var values = new Dictionary<string, string?> { ["key_quick_save"] = null };
		row.Store(r3, values);
		AssertThat(values["key_quick_save"]).IsEqual("Pad:R3");
		values["key_quick_save"] = "F8";
		row.Store(r3, values);
		AssertThat(values["key_quick_save"]).IsEqual("F8,Pad:R3");
		row.Store(Engine_UiControlsPage.PadActionLine.UnboundIndex, values);
		AssertThat(values["key_quick_save"]).IsEqual("F8");
		values["key_quick_save"] = null;
		row.Store(Engine_UiControlsPage.PadActionLine.UnboundIndex, values);
		AssertThat(values["key_quick_save"]).IsNull();
	}

	[TestCase]
	public void TheControllerRowKeepsAChordWhole()
	{
		Engine_UiControlsPage.PadActionLine row = Row();
		int at = row.IndexOfCaptured("Back+Y");
		AssertThat(at).IsEqual(row.Choices - 1);
		AssertThat(row.Caption(at)).IsEqual("Y + Back");
		var values = new Dictionary<string, string?>();
		row.Store(at, values);
		AssertThat(values["key_quick_save"]).IsEqual("Pad:Y+Back");
		AssertThat(row.IndexOfStored(Stored("key_quick_save", "Pad:Y+Back"))).IsEqual(row.Choices - 1);
		AssertThat(row.Bound(at).ToArray()).IsEqual(new[] { "padaction:Y+Back" });
		AssertThat(row.IndexOfCaptured("RT")).IsEqual(Engine_UiControlsPage.PadActionLine.FirstInput + (int)PadInput.RT);
		AssertThat(row.IndexOfCaptured("Guide")).IsEqual(-1);
	}

	/// <summary>A key row reads its own keys beside a chord, so the chord never shows as a key.</summary>
	[TestCase]
	public void TheKeyRowReadsItsKeysBesideAChord()
	{
		var row = new Engine_UiControlsPage.KeyLine("quick_save", "F9", new[] { "F8", "F9" });
		AssertThat(row.IndexOfStored(Stored("key_quick_save", "Pad:R3"))).IsEqual(Engine_UiControlsPage.KeyLine.DefaultPosition);
		AssertThat(row.IndexOfStored(Stored("key_quick_save", "F8,Pad:R3"))).IsEqual(Engine_UiControlsPage.KeyLine.FirstInput);
		AssertThat(row.IndexOfStored(Stored("key_quick_save", "none,Pad:R3"))).IsEqual(Engine_UiControlsPage.KeyLine.UnboundIndex);
	}

	/// <summary>The shell's table (Fable 2's) declares no pad-bindable action: it gains no row and reads no chord.</summary>
	[TestCase]
	public void TheShellsTableHasNoPadBindableAction()
	{
		foreach (GameInput.KeyAction action in GameInput.DefaultKeyActions)
		{
			AssertThat(action.PadBindable).IsFalse();
			AssertThat(action.HostActionFor is null).IsTrue();
		}
	}
}
