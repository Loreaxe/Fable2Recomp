namespace Refii.Tests.Camera;

using GdUnit4;
using Godot;
using Refii.Camera;
using static GdUnit4.Assertions;

/// <summary>
/// The local co-op screen (Engine_CameraScreens): the setting's words, the co-op screen's rows, hybrid's
/// split and rejoin at the stated leash distance, and the swap's progress. Pure.
/// </summary>
[TestSuite]
public class CameraScreensTests
{
	[TestCase]
	public void TheSettingNamesTheThreeModes()
	{
		AssertThat(Engine_CameraScreens.Parse("shared", legacySplit: true)).IsEqual(Engine_CameraScreens.Mode.Shared);
		AssertThat(Engine_CameraScreens.Parse("SPLIT", legacySplit: false)).IsEqual(Engine_CameraScreens.Mode.Split);
		AssertThat(Engine_CameraScreens.Parse(" \"hybrid\" ", legacySplit: false)).IsEqual(Engine_CameraScreens.Mode.Hybrid);
		foreach (Engine_CameraScreens.Mode mode in new[]
		{
			Engine_CameraScreens.Mode.Shared, Engine_CameraScreens.Mode.Split, Engine_CameraScreens.Mode.Hybrid,
		})
		{
			AssertThat(Engine_CameraScreens.Parse(Engine_CameraScreens.Name(mode), legacySplit: false)).IsEqual(mode);
		}
	}

	[TestCase]
	public void WithNoModeTheOldSwitchDecides()
	{
		AssertThat(Engine_CameraScreens.Parse(null, legacySplit: true)).IsEqual(Engine_CameraScreens.Mode.Split);
		AssertThat(Engine_CameraScreens.Parse(string.Empty, legacySplit: false)).IsEqual(Engine_CameraScreens.Mode.Shared);
		// A word it does not know is the default, not the old switch.
		AssertThat(Engine_CameraScreens.Parse("both", legacySplit: false)).IsEqual(Engine_CameraScreens.Mode.Split);
	}

	[TestCase]
	public void TheRowsAreEveryModeAndLayout()
	{
		var seen = new System.Collections.Generic.HashSet<int>();
		for (int row = 0; row < Engine_CameraScreens.Rows; ++row)
		{
			(Engine_CameraScreens.Mode mode, bool stacked) = Engine_CameraScreens.FromRow(row, stacked: false);
			AssertThat(Engine_CameraScreens.Row(mode, stacked)).IsEqual(row);
			seen.Add(row);
		}

		AssertThat(seen.Count).IsEqual(Engine_CameraScreens.Rows);
		// Stepping wraps both ways, and the shared row keeps the layout chosen before.
		(Engine_CameraScreens.Mode back, bool backStacked) = Engine_CameraScreens.FromRow(-1, stacked: false);
		AssertThat(back).IsEqual(Engine_CameraScreens.Mode.Hybrid);
		AssertThat(backStacked).IsTrue();
		(Engine_CameraScreens.Mode round, bool roundStacked) = Engine_CameraScreens.FromRow(Engine_CameraScreens.Rows, stacked: true);
		AssertThat(round).IsEqual(Engine_CameraScreens.Mode.Shared);
		AssertThat(roundStacked).IsTrue();
		AssertThat(Engine_CameraScreens.Row(Engine_CameraScreens.Mode.Shared, stacked: true)).IsEqual(0);
	}

	[TestCase]
	public void TheStatedScreenIsTheModApis()
	{
		AssertThat(Engine_CameraScreens.Screen(Engine_CameraScreens.Mode.Shared, true)).IsEqual(Engine_CameraViews.ScreenShared);
		AssertThat(Engine_CameraScreens.Screen(Engine_CameraScreens.Mode.Split, false)).IsEqual(Engine_CameraViews.ScreenSplitVertical);
		AssertThat(Engine_CameraScreens.Screen(Engine_CameraScreens.Mode.Split, true)).IsEqual(Engine_CameraViews.ScreenSplitHorizontal);
		AssertThat(Engine_CameraScreens.Screen(Engine_CameraScreens.Mode.Hybrid, true)).IsEqual(Engine_CameraViews.ScreenHybrid);
	}

	[TestCase]
	public void HybridSplitsPastTheStatedDistanceAndRejoinsInsideIt()
	{
		const float leash = 13f; // SACCamera.ExteriorZoomLevels MaxCoopDistance
		AssertThat(Engine_CameraScreens.HybridSplits(false, leash, 12.9f)).IsFalse();
		AssertThat(Engine_CameraScreens.HybridSplits(false, leash, 13f)).IsFalse();
		AssertThat(Engine_CameraScreens.HybridSplits(false, leash, 13.01f)).IsTrue();
		// Split, it stays split down to the hysteresis.
		AssertThat(Engine_CameraScreens.HybridSplits(true, leash, 12f)).IsTrue();
		AssertThat(Engine_CameraScreens.HybridSplits(true, leash, leash - Engine_CameraScreens.HysteresisMetres)).IsTrue();
		AssertThat(Engine_CameraScreens.HybridSplits(true, leash, leash - Engine_CameraScreens.HysteresisMetres - 0.01f)).IsFalse();
		// The distance moves with the zoom level: indoors (8) a pair 9 m apart is split.
		AssertThat(Engine_CameraScreens.HybridSplits(false, 8f, 9f)).IsTrue();
	}

	[TestCase]
	public void HybridMeasuresBothWaysAgainstTheSharedCamerasDistance()
	{
		// SACCamera:UpdateZoomLevel scales MaxCoopDistance by CalculateScale(hero) = 1 + 0.125 x min(skill + height, 1)
		// only while the camera frames one hero. The game lane's run read 14.05625 (13 x 1.08125) while split.
		const float shared = 13f;
		const float ownCamera = 13f * 1.125f; // skill + height at 1: 14.625
		// Fed the split camera's own statement, a pair just past the split would rejoin at once: 13.1 m is past
		// 13 (split) and under 14.625 - 1.5 (rejoin) — the screen would swap back and forth every tick.
		AssertThat(Engine_CameraScreens.HybridSplits(false, shared, 13.1f)).IsTrue();
		AssertThat(Engine_CameraScreens.HybridSplits(true, ownCamera, 13.1f)).IsFalse();
		// Against the shared camera's statement held while split, it stays split until 1.5 m inside it.
		AssertThat(Engine_CameraScreens.HybridSplits(true, shared, 13.1f)).IsTrue();
		AssertThat(Engine_CameraScreens.HybridSplits(true, shared, 11.6f)).IsTrue();
		AssertThat(Engine_CameraScreens.HybridSplits(true, shared, 11.4f)).IsFalse();
		AssertThat(Engine_CameraScreens.RejoinBelow(shared)).IsEqual(11.5f);
	}

	[TestCase]
	public void EachDecisionIsLoggedWithTheNumbersItCompared()
	{
		AssertThat(Engine_CameraScreens.Decision(true, 13f, 13.04f))
			.IsEqual("[split-view] hybrid splits: separation 13.04 m past the stated distance 13.00 m");
		AssertThat(Engine_CameraScreens.Decision(false, 14.05625f, 12.5f))
			.IsEqual("[split-view] hybrid rejoins: separation 12.50 m under 12.56 m (stated distance 14.06 m)");
	}

	[TestCase]
	public void HybridKeepsItsStateWithoutANumber()
	{
		foreach (bool split in new[] { false, true })
		{
			AssertThat(Engine_CameraScreens.HybridSplits(split, float.NaN, 40f)).IsEqual(split);
			AssertThat(Engine_CameraScreens.HybridSplits(split, 13f, float.PositiveInfinity)).IsEqual(split);
			AssertThat(Engine_CameraScreens.HybridSplits(split, 0f, 40f)).IsEqual(split);
			AssertThat(Engine_CameraScreens.HybridSplits(split, 13f, -1f)).IsEqual(split);
		}
	}

	[TestCase]
	public void TheSwapRunsBothWaysInItsTime()
	{
		float p = Engine_CameraScreens.Step(0f, apart: true, Engine_CameraScreens.SwapSeconds * 0.5f);
		AssertThat(Mathf.Abs(p - 0.5f) < 1e-6f).IsTrue();
		p = Engine_CameraScreens.Step(p, apart: true, Engine_CameraScreens.SwapSeconds);
		AssertThat(p).IsEqual(1f);
		// Reversed half way, it comes back from where it is.
		p = Engine_CameraScreens.Step(0.25f, apart: false, Engine_CameraScreens.SwapSeconds * 0.1f);
		AssertThat(Mathf.Abs(p - 0.15f) < 1e-6f).IsTrue();
		AssertThat(Engine_CameraScreens.Step(0.1f, apart: false, 10f)).IsEqual(0f);
		// No time, no step; a bad number starts from rest.
		AssertThat(Engine_CameraScreens.Step(0.4f, apart: true, 0f)).IsEqual(0.4f);
		AssertThat(Engine_CameraScreens.Step(0.4f, apart: true, -1f)).IsEqual(0.4f);
		AssertThat(Engine_CameraScreens.Step(float.NaN, apart: false, 0.1f)).IsEqual(0f);
	}

	[TestCase]
	public void TheEaseIsStillAtBothEnds()
	{
		AssertThat(Engine_CameraScreens.Ease(0f)).IsEqual(0f);
		AssertThat(Engine_CameraScreens.Ease(1f)).IsEqual(1f);
		AssertThat(Mathf.Abs(Engine_CameraScreens.Ease(0.5f) - 0.5f) < 1e-6f).IsTrue();
		float last = 0f;
		for (int i = 1; i <= 100; ++i)
		{
			float e = Engine_CameraScreens.Ease(i / 100f);
			AssertThat(e >= last).IsTrue();
			last = e;
		}

		// Its slope is zero at the ends: the first and last hundredth move far less than the middle one.
		float start = Engine_CameraScreens.Ease(0.01f);
		float middle = Engine_CameraScreens.Ease(0.505f) - Engine_CameraScreens.Ease(0.495f);
		AssertThat(start < middle * 0.1f).IsTrue();
	}
}
