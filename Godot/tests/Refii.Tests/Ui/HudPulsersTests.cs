using GdUnit4;
using Refii.Titles.Fable2;
using static GdUnit4.Assertions;

namespace Refii.Tests.Ui;

/// <summary>
/// The HUD's pulsers as the guest steps them (<see cref="Engine_UiHudPulsers"/>): stopped at 0
/// until started, wrapping while looping, and stopping at 0 at the end of the loop a stop found
/// them in.
/// </summary>
[TestSuite]
public sealed class HudPulsersTests
{
    private const uint Heart = 0x1234u;

    [TestCase]
    public void ABoundPulserStandsStoppedUntilTheGuestStartsIt()
    {
        Engine_UiHudPulsers.Clear();
        Engine_UiHudPulsers.Know(Heart);
        Engine_UiHudPulsers.Advance(5f);
        AssertThat(Engine_UiHudPulsers.Of(Heart)!.Value.Running).IsFalse();
        AssertThat(Engine_UiHudPulsers.Of(Heart)!.Value.Phase).IsEqual(0f);
    }

    [TestCase]
    public void ALoopingPulserWrapsAndAStopLetsItFinishItsLoop()
    {
        Engine_UiHudPulsers.Clear();

        // 0.001 a clock unit (a millisecond) is one loop a second.
        Engine_UiHudPulsers.Start(Heart, 0.001f);
        Engine_UiHudPulsers.Advance(0.25f);
        AssertThat(Engine_UiHudPulsers.Of(Heart)!.Value.Phase).IsEqualApprox(0.25f, 0.001f);
        Engine_UiHudPulsers.Advance(1f);
        AssertThat(Engine_UiHudPulsers.Of(Heart)!.Value.Phase).IsEqualApprox(0.25f, 0.001f);
        AssertThat(Engine_UiHudPulsers.Of(Heart)!.Value.Running).IsTrue();

        Engine_UiHudPulsers.Stop(Heart);
        Engine_UiHudPulsers.Advance(0.5f);
        AssertThat(Engine_UiHudPulsers.Of(Heart)!.Value.Running).IsTrue();
        Engine_UiHudPulsers.Advance(0.5f);
        AssertThat(Engine_UiHudPulsers.Of(Heart)!.Value.Running).IsFalse();
        AssertThat(Engine_UiHudPulsers.Of(Heart)!.Value.Phase).IsEqual(0f);

        // A stop on a stopped pulser changes nothing; a start runs it again.
        Engine_UiHudPulsers.Stop(Heart);
        Engine_UiHudPulsers.Start(Heart, 0.002f);
        Engine_UiHudPulsers.Advance(0.25f);
        AssertThat(Engine_UiHudPulsers.Of(Heart)!.Value.Phase).IsEqualApprox(0.5f, 0.001f);
    }
}
