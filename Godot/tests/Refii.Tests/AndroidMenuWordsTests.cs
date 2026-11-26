using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>A phone's quit rows say they close the game; the desktop's keep their words.</summary>
[TestSuite]
public sealed class AndroidMenuWordsTests
{
    [TestCase]
    public void TestAPhoneQuitsTheGameNotToADesktop()
    {
        Engine_Title f2 = Engine_Title.Fable2;
        AssertThat(AndroidMenuWords.ForPhone("REFII_QUIT_TO_DESKTOP", true, f2)).IsEqual("REFII_F2_QUIT_GAME");
        AssertThat(AndroidMenuWords.ForPhone("REFII_F2_SAVE_QUIT_DESKTOP", true, f2)).IsEqual("REFII_F2_SAVE_QUIT_GAME");
        AssertThat(AndroidMenuWords.ForPhone("REFII_F2_QUIT_DESKTOP_NO_SAVE", true, f2)).IsEqual("REFII_F2_QUIT_GAME_NO_SAVE");
        AssertThat(AndroidMenuWords.ForPhone("REFII_QUIT_TO_DESKTOP", false, f2)).IsEqual("REFII_QUIT_TO_DESKTOP");
        AssertThat(AndroidMenuWords.ForPhone("GUI_SCREEN_PAUSE_QUIT_DONT_SAVE", true, f2)).IsEqual("GUI_SCREEN_PAUSE_QUIT_DONT_SAVE");

        // The phone's keys are said in the language in force, as every key of ours is.
        AssertThat(Refii.Text.Engine_OwnWords.Fable2.Get("REFII_F2_QUIT_GAME", "en-uk")).IsEqual("Quit Game");
        AssertThat(Refii.Text.Engine_OwnWords.Fable2.Get("REFII_F2_SAVE_QUIT_GAME", "en-uk")).IsEqual("Save and Quit Game");
        AssertThat(Refii.Text.Engine_OwnWords.Fable2.Get("REFII_F2_QUIT_GAME_NO_SAVE", "en-uk")).IsEqual("Quit Game and Don't Save");
    }

    [TestCase]
    public void TestFable3KeepsItsOwnLabelsOnAPhone()
    {
        AssertThat(AndroidMenuWords.ForPhone("REFII_QUIT_TO_DESKTOP", true, Engine_Title.Fable3)).IsEqual("REFII_QUIT_TO_DESKTOP");
        AssertThat(Engine_Title.Fable3.PhoneHostLabels.Count).IsEqual(0);
    }
}
