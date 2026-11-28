namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The GUI glint rule (<see cref="Engine_UiParticleRule"/>): CHUDParticle's wanted test, its
/// create / kill edges, the opacity clamp, the on-screen test and the stage placement at one unit
/// from the GUI camera. Pure arithmetic — no Godot type is built.
/// </summary>
[TestSuite]
public class UiParticleRuleTests
{
	/// <summary>Every term must hold: drawn, opacity above 0, scale.x above 0, on screen.</summary>
	[TestCase]
	public void WantedNeedsAllFourTerms()
	{
		AssertThat(Engine_UiParticleRule.Wanted(true, 1f, 0.5f, true)).IsTrue();
		AssertThat(Engine_UiParticleRule.Wanted(false, 1f, 0.5f, true)).IsFalse();
		AssertThat(Engine_UiParticleRule.Wanted(true, 0f, 0.5f, true)).IsFalse();
		AssertThat(Engine_UiParticleRule.Wanted(true, 1f, 0f, true)).IsFalse();
		AssertThat(Engine_UiParticleRule.Wanted(true, 1f, -0.5f, true)).IsFalse();
		AssertThat(Engine_UiParticleRule.Wanted(true, 1f, 0.5f, false)).IsFalse();
		AssertThat(Engine_UiParticleRule.Wanted(true, float.NaN, 0.5f, true)).IsFalse();
	}

	/// <summary>The `fsel` clamp: below 0 is 0, above 1 is 1, NaN is 0.</summary>
	[TestCase]
	public void OpacityClampsToUnit()
	{
		AssertThat(Engine_UiParticleRule.Opacity01(-0.2f)).IsEqual(0f);
		AssertThat(Engine_UiParticleRule.Opacity01(0.4f)).IsEqual(0.4f);
		AssertThat(Engine_UiParticleRule.Opacity01(1.7f)).IsEqual(1f);
		AssertThat(Engine_UiParticleRule.Opacity01(float.NaN)).IsEqual(0f);
	}

	/// <summary>Create on the rising edge, kill on the falling one, nothing while they agree.</summary>
	[TestCase]
	public void EdgesOnlyWhenWantedAndAliveDiffer()
	{
		AssertThat(Engine_UiParticleRule.Step(false, true)).IsEqual(Engine_UiParticleRule.Edge.Create);
		AssertThat(Engine_UiParticleRule.Step(true, false)).IsEqual(Engine_UiParticleRule.Edge.Kill);
		AssertThat(Engine_UiParticleRule.Step(true, true)).IsEqual(Engine_UiParticleRule.Edge.None);
		AssertThat(Engine_UiParticleRule.Step(false, false)).IsEqual(Engine_UiParticleRule.Edge.None);
	}

	/// <summary>
	/// A box glint's tracks (querybox `fx2`: opacity 100 until 440 ms, 0 at 450 ms): one instance from
	/// the box's entry to 450 ms, and a NEW one when the box enters again — each sweep starts at 0.
	/// </summary>
	[TestCase]
	public void ABoxGlintSweepsOncePerEntry()
	{
		bool alive = false;
		int created = 0;
		int killed = 0;
		foreach ((int ms, float opacity) in new[] { (0, 100f), (200, 100f), (440, 100f), (450, 0f), (900, 0f), (0, 100f), (300, 100f), (460, 0f) })
		{
			switch (Engine_UiParticleRule.Step(alive, Engine_UiParticleRule.Wanted(true, opacity / 100f, 0.5f, true)))
			{
				case Engine_UiParticleRule.Edge.Create:
					++created;
					alive = true;
					break;
				case Engine_UiParticleRule.Edge.Kill:
					++killed;
					alive = false;
					break;
			}
		}

		AssertThat(created).IsEqual(2);
		AssertThat(killed).IsEqual(2);
		AssertThat(alive).IsFalse();
	}

	/// <summary>The visible canvas, edges included; anything not finite is off it.</summary>
	[TestCase]
	public void OnCanvasIsTheVisibleRect()
	{
		AssertThat(Engine_UiParticleRule.OnCanvas(0f, 0f, 633f, 356f)).IsTrue();
		AssertThat(Engine_UiParticleRule.OnCanvas(633f, -356f, 633f, 356f)).IsTrue();
		AssertThat(Engine_UiParticleRule.OnCanvas(634f, 0f, 633f, 356f)).IsFalse();
		AssertThat(Engine_UiParticleRule.OnCanvas(0f, 357f, 633f, 356f)).IsFalse();
		AssertThat(Engine_UiParticleRule.OnCanvas(float.NaN, 0f, 633f, 356f)).IsFalse();
	}

	/// <summary>A canvas point lands one unit in front of the camera, on the ray through its canvas-plane point.</summary>
	[TestCase]
	public void StagePointIsOnTheRayAtDepthOne()
	{
		(float x, float y, float z) = Engine_UiParticleRule.StagePoint(-24f, 109f);
		AssertThat(z).IsEqual(-1f);

		// The canvas plane point: (x·0.01, y·0.01, −40); the same direction from the camera.
		AssertThat(x * Engine_UiParticleRule.CanvasPlane).IsEqualApprox(-24f * Engine_UiParticleRule.MetresPerCanvasUnit, 1e-5f);
		AssertThat(y * Engine_UiParticleRule.CanvasPlane).IsEqualApprox(109f * Engine_UiParticleRule.MetresPerCanvasUnit, 1e-5f);
	}

	/// <summary>
	/// The stage camera's angle at the remaster's 712-unit canvas is the guest's 10.2° to a fraction,
	/// and a stage unit at the glint plane is 4000 canvas units on ANY window height.
	/// </summary>
	[TestCase]
	public void TheStageIsTheGuestsTenPointTwoDegreeCamera()
	{
		float height = 720f / (640f / 633f);
		AssertThat(Engine_UiParticleRule.FieldOfViewDegrees(height)).IsEqualApprox(10.2f, 0.1f);
		AssertThat(Engine_UiParticleRule.CanvasUnitsPerStageUnit(height)).IsEqualApprox(4000f, 0.5f);
		AssertThat(Engine_UiParticleRule.CanvasUnitsPerStageUnit(900f)).IsEqualApprox(4000f, 0.5f);
	}

	/// <summary>π about the GUI world's up, in the stage's axes: x mirrored, y and z kept.</summary>
	[TestCase]
	public void TheStageBasisIsPiAboutUp()
	{
		AssertThat(Engine_UiParticleRule.StageBasis[0]).IsEqual((-1f, 0f, 0f));
		AssertThat(Engine_UiParticleRule.StageBasis[1]).IsEqual((0f, 1f, 0f));
		AssertThat(Engine_UiParticleRule.StageBasis[2]).IsEqual((0f, 0f, 1f));
	}

	/// <summary>A one-shot ends when it spawned and nothing of it lives past its settle time, or at the cap.</summary>
	[TestCase]
	public void ABurstEndsWithItsLastParticle()
	{
		AssertThat(Engine_UiParticleRule.BurstDone(0.1f, 0, 0)).IsFalse();
		AssertThat(Engine_UiParticleRule.BurstDone(0.5f, 0, 0)).IsFalse();
		AssertThat(Engine_UiParticleRule.BurstDone(0.5f, 12, 3)).IsFalse();
		AssertThat(Engine_UiParticleRule.BurstDone(0.5f, 12, 0)).IsTrue();
		AssertThat(Engine_UiParticleRule.BurstDone(10f, 0, 0)).IsTrue();
	}
}
