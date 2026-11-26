namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;

/// <summary>
/// The front end's levers in the form the pump parses (`TitleStart:&lt;pad&gt;`,
/// `FrontEndDialog:&lt;pad&gt;:&lt;code&gt;`), and which one an A press on the title posts.
/// </summary>
[TestSuite]
public class FrontEndLeversTests
{
	[TestCase]
	public void TheLeversCarryThePad()
	{
		AssertThat(Fable2FrontEndLevers.TitleStart(0)).IsEqual("TitleStart:0");
		AssertThat(Fable2FrontEndLevers.TitleStart(3)).IsEqual("TitleStart:3");
		AssertThat(Fable2FrontEndLevers.Dialog(1, 36)).IsEqual("FrontEndDialog:1:36");
		AssertThat(Fable2FrontEndLevers.Dialog(0, 37)).IsEqual("FrontEndDialog:0:37");
		AssertThat(Fable2FrontEndLevers.Back(2)).IsEqual("FrontEndBack:2");
	}

	[TestCase]
	public void ATitleWithNoDialogTakesTheStart()
	{
		AssertThat(Fable2FrontEndLevers.TitleA(2, titleDialogRaised: false, dialogAccepting: false))
			.IsEqual("TitleStart:2");
	}

	[TestCase]
	public void TheTitlesDialogTakesAOnlyWhileItAccepts()
	{
		AssertThat(Fable2FrontEndLevers.TitleA(1, titleDialogRaised: true, dialogAccepting: true))
			.IsEqual("FrontEndDialog:1:36");
		AssertThat(Fable2FrontEndLevers.TitleA(1, titleDialogRaised: true, dialogAccepting: false)).IsNull();
	}
}
