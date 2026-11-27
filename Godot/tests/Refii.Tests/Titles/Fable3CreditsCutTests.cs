namespace Refii.Tests.Titles;

using GdUnit4;
using Refii.Titles.Fable3;
using Refii.Ui;
using static GdUnit4.Assertions;

/// <summary>
/// The credits are cut from `creditsscreen.bgf`'s plain nodes under `Layer` (Background, Text, credits_logos,
/// Fade: none is a component), so every one of them is cut as any node; the backdrop stands behind the lines and
/// the fades in front, as their z (10, 0, -4) has them.
/// </summary>
[TestSuite]
public class Fable3CreditsCutTests
{
	[TestCase]
	public void EveryCreditsWidgetIsCutAsTheNodeItIs()
	{
		string[] names = { "Background", "Text", "credits_logos", "Fade" };
		AssertThat(Fable3EndScreens.CreditsGroups.Length).IsEqual(names.Length);
		for (int i = 0; i < names.Length; ++i)
		{
			Engine_HudLayout.GroupSubtree group = Fable3EndScreens.CreditsGroups[i];
			AssertThat(group.Name).IsEqual(names[i]);
			AssertThat(group.AnyNode).OverrideFailureMessage(group.Name).IsTrue();
			AssertThat(group.Parent).IsNull();
		}

		AssertThat(Fable3EndScreens.CreditsGroups[0].Depth < Fable3EndScreens.CreditsGroups[1].Depth).IsTrue();
		AssertThat(Fable3EndScreens.CreditsGroups[3].Depth > Fable3EndScreens.CreditsGroups[1].Depth).IsTrue();
	}

	[TestCase]
	public void TheBackdropAndTheLogoSheetAreScreenSizedArt()
	{
		// `creditsscreen.bgf` scales them 13 x 8 and 10.24 x 5.12 over the 100-unit quad: past half the design box,
		// which the widget bounds take for a container, so the group report names them screen-sized art (int7g:
		// both read NOT DRAWN while the backdrop blacked the window). The logo sheet starts at y -664, off screen,
		// and rises once every line is committed (TU3 0x82B74EF8 / 0x82B75124, disc 0x82B3FCD0 / 0x82B3FEFC).
		AssertThat(13f * Engine_UiSceneBuilder.QuadBaseSize > Engine_HudLayout.DesignWidth * 0.5f).IsTrue();
		AssertThat(10.24f * Engine_UiSceneBuilder.QuadBaseSize > Engine_HudLayout.DesignWidth * 0.5f).IsTrue();
	}

	[TestCase]
	public void TheEpiloguesCardIsCutAsTheNodeItIs()
	{
		AssertThat(Fable3EndScreens.EpilogueGroups[0].Name).IsEqual("elements");
		AssertThat(Fable3EndScreens.EpilogueGroups[0].AnyNode).IsTrue();
		AssertThat(Fable3EndScreens.EpilogueGroups[1].Name).IsEqual("Buttons");
		AssertThat(Fable3EndScreens.EpilogueGroups[1].AnyNode).IsFalse();
	}
}
