namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The query box stands the root slide its opener states (Engine_UiBoxConstants.QueryBoxSlideFor),
/// found in the file by goToSlide's numbering (Engine_UiCloneSlide.Numbered). The document below is
/// `querybox.bgf`'s shape: the root's master, then Intro / Outro / IntroXA / OutroXA / IntroReload /
/// OutroReload, with another owner's slides between them, and the XA row's choice slide-gated like
/// the row itself — inactive at the book's preview slide, active on IntroXA.
/// </summary>
[TestSuite]
public class QueryBoxSlideTests
{
	private const int Root = 0, Choices = 1, XAChoices = 2, EquipAndContinue = 3, Other = 4;

	private static BgfSlideEntry Entry(int ordinal, bool active) =>
		new() { ElementOrdinal = ordinal, Active = active, Commands = System.Array.Empty<BgfAttribute>(), AnimationEvents = System.Array.Empty<BgfAnimationEvent>() };

	private static BgfSlide Slide(string name, int owner, params BgfSlideEntry[] entries) =>
		new() { Name = name, OwnerElement = owner, Entries = entries };

	private static Engine_BgfDocument Document() => new()
	{
		Elements = new[]
		{
			new BgfElement { ParentOrdinal = -1, Flags = 1 },
			new BgfElement { ParentOrdinal = Root, Flags = 1 },
			new BgfElement { ParentOrdinal = Root, Flags = 1 },
			new BgfElement { ParentOrdinal = XAChoices, Flags = 1 },
			new BgfElement { ParentOrdinal = Root, Flags = 1 },
		},
		Slides = new[]
		{
			Slide("Master Slide", Root, Entry(Choices, false), Entry(XAChoices, false), Entry(EquipAndContinue, false)),
			Slide("Intro", Root, Entry(Choices, true), Entry(XAChoices, false), Entry(EquipAndContinue, false)),
			Slide("Master Slide", Other),
			Slide("Selected", Other, Entry(Other, true)),
			Slide("Outro", Root, Entry(Choices, true), Entry(XAChoices, false), Entry(EquipAndContinue, false)),
			Slide("IntroXA", Root, Entry(Choices, false), Entry(XAChoices, true), Entry(EquipAndContinue, true)),
			Slide("OutroXA", Root, Entry(Choices, false), Entry(XAChoices, true), Entry(EquipAndContinue, true)),
			Slide("IntroReload", Root, Entry(Choices, false), Entry(XAChoices, false), Entry(EquipAndContinue, false)),
			Slide("OutroReload", Root),
		},
	};

	[TestCase]
	public void TheRouterStandsSlideThreeForBoxTwentyAndFiveForThirteen()
	{
		AssertThat(Engine_UiBoxConstants.QueryBoxSlideFor(20u, display: false)).IsEqual(3);
		AssertThat(Engine_UiBoxConstants.QueryBoxSlideFor(13u, display: false)).IsEqual(5);
		AssertThat(Engine_UiBoxConstants.QueryBoxSlideFor(15u, display: false)).IsEqual(1);
		AssertThat(Engine_UiBoxConstants.QueryBoxSlideFor(3u, display: false)).IsEqual(1);
	}

	[TestCase]
	public void TheDisplayQueryKeepsSlideOne()
	{
		AssertThat(Engine_UiBoxConstants.QueryBoxSlideFor(20u, display: true)).IsEqual(1);
	}

	[TestCase]
	public void SlidesAreNumberedAsGoToSlideCountsThem()
	{
		Engine_BgfDocument doc = Document();
		AssertThat(Engine_UiCloneSlide.Numbered(doc, Root, 1)?.Name).IsEqual("Intro");
		AssertThat(Engine_UiCloneSlide.Numbered(doc, Root, 3)?.Name).IsEqual("IntroXA");
		AssertThat(Engine_UiCloneSlide.Numbered(doc, Root, 5)?.Name).IsEqual("IntroReload");
		AssertThat(Engine_UiCloneSlide.Numbered(doc, Root, 7)).IsNull();
		AssertThat(Engine_UiCloneSlide.Numbered(doc, Root, 0)).IsNull();
		AssertThat(Engine_UiCloneSlide.Numbered(null, Root, 1)).IsNull();
	}

	[TestCase]
	public void ANamedSlideIsTheOwnersOwn()
	{
		Engine_BgfDocument doc = Document();
		AssertThat(Engine_UiCloneSlide.Named(doc, Other, "Selected")?.OwnerElement).IsEqual(Other);
		AssertThat(Engine_UiCloneSlide.Named(doc, Root, "Selected")).IsNull();
		AssertThat(Engine_UiCloneSlide.Named(null, Root, "Intro")).IsNull();
	}

	/// <summary>The defect's premise: the book's preview slide parks the XA row's CHILD as well as
	/// the row, and only the stated slide switches it on — standing the row by name cannot.</summary>
	[TestCase]
	public void TheXARowsChoiceIsParkedInTheBookAndSwitchedOnByItsSlide()
	{
		Engine_BgfDocument doc = Document();
		BgfSlideState book = doc.ComputeSlideState(null);
		AssertThat(book.Active[EquipAndContinue]).IsFalse();

		BgfSlide? stated = Engine_UiCloneSlide.Numbered(doc, Root, Engine_UiBoxConstants.QueryBoxSlideFor(20u, false));
		AssertThat(stated).IsNotNull();
		bool switchedOn = false;
		foreach (BgfSlideEntry entry in stated!.Entries)
		{
			switchedOn |= entry.ElementOrdinal == EquipAndContinue && entry.Active;
		}

		AssertThat(switchedOn).IsTrue();
	}
}
