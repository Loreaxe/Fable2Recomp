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
	public void OtherButtonsAndScreensStayScreenInput()
	{
		AssertThat(Fable2PortedScreens.Lever(17, Engine_UiScreenInput.Select, 1)).IsNull();
		AssertThat(Fable2PortedScreens.Lever(14, Engine_UiScreenInput.Back, 1)).IsNull();
		AssertThat(Fable2PortedScreens.Lever(18, Engine_UiScreenInput.TabNext, 1)).IsNull();
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
