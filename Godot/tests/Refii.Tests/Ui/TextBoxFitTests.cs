using System;
using System.IO;
using GdUnit4;
using Refii.Bnk;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests.Ui;

/// <summary>
/// A text box's first line from the presentation file (<see cref="Engine_UiTextBoxFit"/>): the
/// fit's sums as its getters walk them (TU1 `sub_824253A0` / `sub_82425980`, Fable 3
/// `sub_82818760` / `sub_82818C30`), and both titles' shipped TopBox — Fable 2's line 0 at y 326
/// for every box height, the value its fit writes (TU1 0x823FCA08) and Godot drew from that write
/// until now. No Godot type is built.
/// </summary>
[TestSuite]
public sealed class TextBoxFitTests
{
	private static string? Bank(string title)
	{
		string data = Path.Combine("assets", title, "game", "data", "guiscripts.bnk");
		for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
		{
			if (File.Exists(Path.Combine(at, data)))
			{
				return Path.Combine(at, data);
			}

			string packages = Path.Combine(at, "out", "package");
			if (Directory.Exists(packages))
			{
				foreach (string package in Directory.EnumerateDirectories(packages))
				{
					if (File.Exists(Path.Combine(package, data)))
					{
						return Path.Combine(package, data);
					}
				}
			}
		}

		return null;
	}

	private static Engine_UiScreen? Hud(string title)
	{
		string? bank = Bank(title);
		if (bank is null || !BnkArchive.TryOpen(bank, out BnkArchive? archive, out _))
		{
			return null;
		}

		using (archive)
		{
			foreach (BnkEntry entry in archive!.Entries)
			{
				if (string.Equals(Path.GetFileName(entry.RelativePath.Replace('\\', '/')), "hud.bgf", StringComparison.OrdinalIgnoreCase))
				{
					return Engine_UiScreen.Parse("hud", archive.ReadEntryBytes(entry), null);
				}
			}
		}

		return null;
	}

	/// <summary>Scene(0) → Layer(1) → Holder(2: y 40, pivot.y 25, scale.y 2) → Box(3: y 10, pivot.y 50, scale.y 4, BoxName 'Box').</summary>
	private static Engine_BgfDocument Synthetic()
	{
		Engine_BgfDocument doc = Engine_BgfEdit.NewDocument("Scene", 1280, 720, true);
		int layer = Engine_BgfEdit.AddChild(doc, 0, "Layer");
		int holder = Engine_BgfEdit.AddChild(doc, layer, "Holder", 0f, 40f);
		int box = Engine_BgfEdit.AddChild(doc, holder, "Box", 0f, 10f);
		Engine_BgfEdit.SetFloat(doc, holder, "pivot.x", 0f);
		Engine_BgfEdit.SetFloat(doc, holder, "pivot.y", 25f);
		Engine_BgfEdit.SetFloat(doc, holder, "scale.x", 1f);
		Engine_BgfEdit.SetFloat(doc, holder, "scale.y", 2f);
		Engine_BgfEdit.SetFloat(doc, box, "pivot.x", 0f);
		Engine_BgfEdit.SetFloat(doc, box, "pivot.y", 50f);
		Engine_BgfEdit.SetFloat(doc, box, "scale.x", 1f);
		Engine_BgfEdit.SetFloat(doc, box, "scale.y", 4f);
		BgfElement element = doc.Elements[box];
		var attributes = new System.Collections.Generic.List<BgfAttribute>(element.Attributes)
		{
			new(Refii.Core.Engine_GameHash.AttributeId(Engine_UiTextBoxFit.BoxNameAttribute), BgfValueType.String,
				unchecked((uint)Engine_BgfEdit.StringIndex(doc, "Box"))),
		};
		element.Attributes = attributes.ToArray();
		return doc;
	}

	/// <summary>
	/// The getters' sums: positions add up the chain; a pivot is divided by the running scale,
	/// which starts at 100 and takes each element's scale only after its own pivot (0x82818D30
	/// before 0x82818D7C) — the box's 50 over 100, its holder's 25 over 100 × 4.
	/// </summary>
	[TestCase]
	public void TheFitSumsTheChainAsTheGetterWalksIt()
	{
		Engine_BgfDocument doc = Synthetic();
		int box = Engine_UiTextBoxFit.BoxElement(doc, "Box");
		AssertThat(box).IsGreaterEqual(0);
		AssertThat(Engine_UiTextBoxFit.ChainPosition(doc, box).Y).IsEqual(50f);
		AssertThat(Engine_UiTextBoxFit.ChainPivot(doc, box).Y).IsEqualApprox(0.5f + (25f / 400f), 1e-6f);

		// y = h · (0.5 − pivot.y) + pos.y.
		AssertThat(Engine_UiTextBoxFit.FirstLineY(doc, "Box", 80f)!.Value).IsEqualApprox((80f * -0.0625f) + 50f, 1e-4f);
		AssertThat(Engine_UiTextBoxFit.HeightFree(doc, "Box")).IsFalse();
		AssertThat(Engine_UiTextBoxFit.FirstLineY(doc, "Nobody", 80f)).IsNull();

		// A script's statement of a chain position (the widescreen adapter) replaces the file's.
		AssertThat(Engine_UiTextBoxFit.ChainPosition(doc, box, at => at == box ? (null, 30f) : (null, null)).Y).IsEqual(70f);
	}

	/// <summary>
	/// FABLE 2'S TOPBOX DOES NOT MOVE. Its fit (TU1 `sub_823FC8F0`) states line 0 at
	/// `(0.5 − pivot.y) · h + pos.y` over the `BoxName 'TopBox'` element `Bar`: pos.y 0 + 50 + 276 =
	/// 326 and pivot.y 50 / 100 = 0.5 exactly, so the height term is 0 and the line stands at 326 for
	/// any box the text fits — the value the view anchored on when it took the guest's write. No
	/// slide moves it: the chain's tracks are opacity and the TopBoxX/TopBoxY meters' scales, and a
	/// scale reaches only a pivot above it, of which this chain has none.
	/// </summary>
	[TestCase]
	public void FableTwosTopBoxLineStaysAt326()
	{
		if (Hud("Fable2") is not { } screen)
		{
			Console.WriteLine("[text-box-fit] no Fable 2 bank beside the tree; the shipped TopBox is not checked here");
			return;
		}

		Engine_BgfDocument doc = screen.Document;
		int box = Engine_UiTextBoxFit.BoxElement(doc, "TopBox");
		AssertThat(screen.Nodes[box].Name).IsEqual("Bar");
		AssertThat(Engine_UiTextBoxFit.ChainPosition(doc, box).Y).IsEqual(326f);
		AssertThat(Engine_UiTextBoxFit.ChainPivot(doc, box).Y).IsEqual(0.5f);
		AssertThat(Engine_UiTextBoxFit.HeightFree(doc, "TopBox")).IsTrue();
		foreach (float height in new[] { 0f, 24f, 37.5f, 96f, 220f, 1000f })
		{
			AssertThat(Engine_UiTextBoxFit.FirstLineY(doc, "TopBox", height)!.Value).IsEqual(326f);
		}

		AssertThat(Engine_UiTextBoxFit.Moved(doc, "TopBox").Count).IsEqual(0);
		AssertThat(WidescreenMovesLineY(doc, box)).IsFalse();
	}

	/// <summary>
	/// Fable 3's TopBox (`TopBoxRefined.TopBox.TopBox.Bar.Bar`, [1519]): pos.y 0 + 2 + 0 + 10 = 12,
	/// pivot.y 50 / 100 — line 0 at 12 for any height, unmoved by its slides.
	/// </summary>
	[TestCase]
	public void FableThreesTopBoxLineStandsAt12()
	{
		if (Hud("Fable3") is not { } screen)
		{
			Console.WriteLine("[text-box-fit] no Fable 3 bank beside the tree; the shipped TopBox is not checked here");
			return;
		}

		Engine_BgfDocument doc = screen.Document;
		int box = Engine_UiTextBoxFit.BoxElement(doc, "TopBox");
		AssertThat(box).IsEqual(1519);
		AssertThat(Engine_UiTextBoxFit.HeightFree(doc, "TopBox")).IsTrue();
		AssertThat(Engine_UiTextBoxFit.FirstLineY(doc, "TopBox", 64f)!.Value).IsEqual(12f);
		AssertThat(Engine_UiTextBoxFit.Moved(doc, "TopBox").Count).IsEqual(0);
		AssertThat(WidescreenMovesLineY(doc, box)).IsFalse();

		// The counters and the subtitles stand by their box's height: pivot.y −0.5, so line 0 is
		// at h − 251 and h − 166.
		AssertThat(Engine_UiTextBoxFit.FirstLineY(doc, "Corner", 40f)!.Value).IsEqual(40f - 251f);
		AssertThat(Engine_UiTextBoxFit.FirstLineY(doc, "Subtitles", 40f)!.Value).IsEqual(40f - 166f);
	}

	/// <summary>
	/// FABLE 3'S BOX EXTENTS, as the expandable box's calibration (TU3 `sub_8227AEB0`) stores them:
	/// the box's scale up its chain × 100 with its meters at 100 (max) and 0 (min) — the numbers the
	/// guest's own reading off the track pass gave in the run that moved the reader to Godot.
	/// </summary>
	[TestCase]
	public void FableThreesBoxExtentsAreTheCalibrations()
	{
		if (Hud("Fable3") is not { } screen)
		{
			Console.WriteLine("[text-box-fit] no Fable 3 bank beside the tree; the shipped box extents are not checked here");
			return;
		}

		Engine_UiBindings bindings = Engine_UiBindings.Read(screen.Document);
		(float, float, float, float) Of(string name)
		{
			var meters = new System.Collections.Generic.List<int>(bindings.Elements(Engine_UiBindings.Kind.Meter, name + "X"));
			meters.AddRange(bindings.Elements(Engine_UiBindings.Kind.Meter, name + "Y"));
			return Engine_UiTextBoxFit.Extents(screen, Engine_UiTextBoxFit.BoxElement(screen.Document, name), meters);
		}

		AssertThat(Of("TopBox")).IsEqual((156f, 38f, 861.9000244f, 110f));
		AssertThat(Of("Subtitles")).IsEqual((124.5000153f, 30.0000019f, 747.0000610f, 150f));
		AssertThat(Of("Corner")).IsEqual((117.7100067f, 30.0000019f, 367.3500061f, 198f));
	}

	/// <summary>
	/// Whether a widescreen adapter states a `position.y` on the box's chain — the only adapter
	/// write line 0 reads (its `scale.x` reaches pivot.x alone). Fable 2's TopBox carries one
	/// (`TopBox` [1527], WidescreenXWidth 1.38, no y), and its TU never runs the adapter anyway
	/// (`g_WidescreenMode` 0x8336032C, reads only).
	/// </summary>
	private static bool WidescreenMovesLineY(Engine_BgfDocument doc, int box)
	{
		var chain = new System.Collections.Generic.HashSet<int>();
		for (int at = box; at >= 0; at = doc.Elements[at].ParentOrdinal)
		{
			chain.Add(at);
		}

		foreach (Engine_UiWidescreen.Override over in Engine_UiWidescreen.Read(doc).Overrides)
		{
			if (chain.Contains(over.ParentOrdinal) && over.Y is not null)
			{
				return true;
			}
		}

		return false;
	}
}
