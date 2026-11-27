using GdUnit4;
using Refii.Entities;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// Which view draws a targeting highlight. The guest raises one highlight per target for whichever
/// player targets it (lhECTargeted_SetTargetedByPlayer); split, each view keeps its own player's,
/// one screen draws every one. Pure: no Godot object is built.
/// </summary>
[TestSuite]
public sealed class Engine_EntityHighlightViewTests
{
    private const int One = 1;
    private const int Two = 2;
    private const int Both = 3;

    [TestCase]
    public void OneScreenDrawsEveryHighlight()
    {
        foreach (int mask in new[] { 0, One, Two, Both })
        {
            AssertThat(Engine_EntityHighlight.DrawnInView(0, split: false, mask)).IsTrue();
        }
    }

    [TestCase]
    public void SplitDrawsEachPlayersOwnTarget()
    {
        AssertThat(Engine_EntityHighlight.DrawnInView(0, split: true, One)).IsTrue();
        AssertThat(Engine_EntityHighlight.DrawnInView(1, split: true, One)).IsFalse();
        AssertThat(Engine_EntityHighlight.DrawnInView(0, split: true, Two)).IsFalse();
        AssertThat(Engine_EntityHighlight.DrawnInView(1, split: true, Two)).IsTrue();
    }

    [TestCase]
    public void SplitDrawsAThingBothTargetInBothViews()
    {
        AssertThat(Engine_EntityHighlight.DrawnInView(0, split: true, Both)).IsTrue();
        AssertThat(Engine_EntityHighlight.DrawnInView(1, split: true, Both)).IsTrue();
    }

    [TestCase]
    public void AHighlightNoTargetingStatesIsDrawnEverywhere()
    {
        // A troll's weak spot lit through its redirect, a title's own stand-in, or a title that
        // states no targeting (Fable 3): as on one screen.
        AssertThat(Engine_EntityHighlight.DrawnInView(0, split: true, 0)).IsTrue();
        AssertThat(Engine_EntityHighlight.DrawnInView(1, split: true, 0)).IsTrue();
    }
}
