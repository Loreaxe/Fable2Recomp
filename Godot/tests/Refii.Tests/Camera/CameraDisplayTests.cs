namespace Refii.Tests.Camera;

using GdUnit4;
using Godot;
using Refii.Camera;
using static GdUnit4.Assertions;
using static Refii.Camera.Engine_CameraStatements;

/// <summary>
/// The camera between two stated steps (Engine_CameraDisplay) and what the rig keeps for it
/// (Engine_CameraRig's Eased, the manager's cut count). Pure model code.
/// </summary>
[TestSuite]
public class CameraDisplayTests
{
	private const uint Comp = 0x40001000u;
	private const uint Entity = 0x40002000u;
	private const uint Manager = 0x40003000u;
	private uint _sequence;

	private Statement S(Call call, uint word = 0, uint depth = 0, params float[] values)
	{
		var s = new Statement { Sequence = ++_sequence, Call = call, Depth = (byte)depth, Component = Comp, Word = word };
		for (int i = 0; i < values.Length; i++)
		{
			s.Values[i] = values[i];
		}

		return s;
	}

	private Statement Ease(float factor, bool cut = false)
	{
		var s = new Statement
		{
			Sequence = ++_sequence, Call = Call.EaseBegin, Component = Comp, Entity = Entity, Word = 1u,
		};
		s.Values[0] = factor;
		s.Values[3] = cut ? 1f : 0f;
		return s;
	}

	private Statement End() => S(Call.EaseEnd);

	private Statement Push() => new()
	{
		Sequence = ++_sequence, Call = Call.Push, Manager = Manager, Player = 1, Flags = FlagDrawn,
	};

	private static void Run(Engine_CameraRig rig, params Statement[] statements)
	{
		foreach (Statement s in statements)
		{
			rig.Apply(s);
		}
	}

	private static Engine_CameraDisplay.Step Live(Engine_CameraRig rig) =>
		new(rig.Managers[Manager].Live!.Value, rig.Managers[Manager].LiveEase);

	private static Engine_CameraDisplay.Step Previous(Engine_CameraRig rig) =>
		new(rig.Managers[Manager].Previous!.Value, rig.Managers[Manager].PreviousEase);

	private static float Degrees(Vector3 a, Vector3 b) =>
		Mathf.RadToDeg(System.MathF.Acos(System.Math.Clamp(a.Normalized().Dot(b.Normalized()), -1f, 1f)));

	/// <summary>A camera cut into place, then two eased steps of a four-tick blend toward a new
	/// position, direction, roll and FOV, each pushed.</summary>
	private Engine_CameraRig Blending(float roll = 0f)
	{
		var rig = new Engine_CameraRig();
		Run(rig,
			S(Call.InterpolateFor, word: 0),
			S(Call.MoveToCut, values: new[] { 0f, 0f, 2f }),
			S(Call.SetDirectionCut, values: new[] { 0f, 1f, 0f }),
			S(Call.SetFovCut, values: new[] { 70f }),
			Ease(1f), End(), Push(),
			S(Call.InterpolateFor, word: 4),
			S(Call.MoveTo, values: new[] { 8f, 4f, 3f }),
			S(Call.SetDirection, values: new[] { 1f, 0.2f, -0.1f }),
			S(Call.SetRoll, values: new[] { roll }),
			S(Call.SetFov, values: new[] { 50f }),
			Ease(0f), End(), Push(),
			Ease(0.25f), End(), Push(),
			Ease(0.5f), End(), Push());
		return rig;
	}

	[TestCase]
	public void ProgressIsTheLawsAndComposes()
	{
		AssertThat(Engine_CameraDisplay.Progress(0.5f, 0f)).IsEqual(0f);
		AssertThat(Engine_CameraDisplay.Progress(0.5f, 1f)).IsEqual(1f);
		AssertThat(Engine_CameraDisplay.Progress(0.5f, -1f)).IsEqual(0f);
		AssertThat(Engine_CameraDisplay.Progress(0.5f, 2f)).IsEqual(1f);

		// A step at f = 1 arrives whole and a step that did not move has no shape: the phase.
		AssertThat(Engine_CameraDisplay.Progress(1f, 0.3f)).IsEqual(0.3f);
		AssertThat(Engine_CameraDisplay.Progress(0f, 0.3f)).IsEqual(0.3f);
		AssertThat(Engine_CameraDisplay.Progress(float.NaN, 0.3f)).IsEqual(0.3f);

		// (1 − (1 − f)^a) / f: half a step at f = 0.75 leaves half of the way (0.25^0.5) still to go.
		AssertThat(System.MathF.Abs(Engine_CameraDisplay.Progress(0.75f, 0.5f) - (0.5f / 0.75f)) < 1e-6f).IsTrue();

		// Two parts in a row are the whole step: what a leaves, b takes the same law's share of.
		foreach (float f in new[] { 0.1f, 0.25f, 0.5f, 0.9f })
		{
			float previous = 0f;
			for (int i = 1; i <= 20; i++)
			{
				float phase = i / 20f;
				float done = Engine_CameraDisplay.Progress(f, phase);
				AssertThat(done > previous).OverrideFailureMessage($"f {f} phase {phase} did not advance").IsTrue();
				previous = done;
				double left = 1.0 - (done * f);
				AssertThat(System.Math.Abs(left - System.Math.Pow(1.0 - f, phase)) < 1e-5)
					.OverrideFailureMessage($"f {f} phase {phase} leaves {left}").IsTrue();
			}
		}
	}

	[TestCase]
	public void TheEndsAreTheStatedBlocksValueForValue()
	{
		Engine_CameraRig rig = Blending(roll: 12f);
		Engine_CameraDisplay.Step from = Previous(rig);
		Engine_CameraDisplay.Step to = Live(rig);
		AssertThat(Engine_CameraDisplay.Shaped(from, to)).IsTrue();

		AssertThat(Engine_CameraDisplay.Evaluate(from, to, 0f).Equals(from.Block)).IsTrue();
		AssertThat(Engine_CameraDisplay.Evaluate(from, to, -0.5f).Equals(from.Block)).IsTrue();
		AssertThat(Engine_CameraDisplay.Evaluate(from, to, 1f).Equals(to.Block)).IsTrue();
		AssertThat(Engine_CameraDisplay.Evaluate(from, to, 1.5f).Equals(to.Block)).IsTrue();
		AssertThat(Engine_CameraDisplay.Blend(from.Block, to.Block, 0f).Equals(from.Block)).IsTrue();
		AssertThat(Engine_CameraDisplay.Blend(from.Block, to.Block, 1f).Equals(to.Block)).IsTrue();
	}

	[TestCase]
	public void BetweenStepsThePoseIsTheLawsAndMeetsTheEnds()
	{
		Engine_CameraRig rig = Blending(roll: 12f);
		Engine_CameraDisplay.Step from = Previous(rig);
		Engine_CameraDisplay.Step to = Live(rig);
		float f = to.Ease.Factor;
		AssertThat(f).IsEqual(0.5f);

		// The position is on the segment, at the law's share of it; never behind an earlier phase.
		Vector3 span = to.Block.Position - from.Block.Position;
		float last = 0f;
		for (int i = 1; i < 16; i++)
		{
			float phase = i / 16f;
			Engine_CameraRig.Block drawn = Engine_CameraDisplay.Evaluate(from, to, phase);
			float along = (drawn.Position - from.Block.Position).Dot(span) / span.LengthSquared();
			AssertThat(System.MathF.Abs(along - Engine_CameraDisplay.Progress(f, phase)) < 1e-5f).IsTrue();
			AssertThat(((drawn.Position - from.Block.Position) - span * along).Length() < 1e-4f).IsTrue();
			AssertThat(along > last).IsTrue();
			last = along;

			float fov = drawn.HorizontalFov;
			AssertThat(fov <= from.Block.HorizontalFov + 1e-6f && fov >= to.Block.HorizontalFov - 1e-6f).IsTrue();
			AssertThat(System.MathF.Abs(drawn.Up.Dot(drawn.Forward)) < 1e-4f).IsTrue();
			AssertThat(System.MathF.Abs(drawn.Forward.Length() - 1f) < 1e-4f).IsTrue();
		}

		// It meets both ends: a hair inside either is a hair from the stated block.
		Engine_CameraRig.Block start = Engine_CameraDisplay.Evaluate(from, to, 1e-4f);
		Engine_CameraRig.Block end = Engine_CameraDisplay.Evaluate(from, to, 1f - 1e-4f);
		AssertThat(start.Position.DistanceTo(from.Block.Position) < 1e-2f).IsTrue();
		AssertThat(end.Position.DistanceTo(to.Block.Position) < 1e-2f).IsTrue();
		AssertThat(Degrees(start.Forward, from.Block.Forward) < 0.05f).IsTrue();
		AssertThat(Degrees(start.Up, from.Block.Up) < 0.05f).IsTrue();
		AssertThat(Degrees(end.Forward, to.Block.Forward) < 0.05f).IsTrue();
		AssertThat(Degrees(end.Up, to.Block.Up) < 0.05f).IsTrue();
	}

	[TestCase]
	public void TheLawRunInTwoPartsIsTheStep()
	{
		// The component's own step from the first block with the factor a part of the step leaves
		// reaches the same place the display shows at that part: position E + (T − E)(1 − (1 − f)^a).
		Engine_CameraRig rig = Blending();
		Engine_CameraDisplay.Step from = Previous(rig);
		Engine_CameraDisplay.Step to = Live(rig);
		var target = new Vector3(8f, 4f, 3f);
		float f = to.Ease.Factor;
		foreach (float phase in new[] { 0.25f, 0.5f, 0.75f })
		{
			float part = 1f - System.MathF.Pow(1f - f, phase);
			Vector3 law = from.Block.Position + (target - from.Block.Position) * part;
			Vector3 drawn = Engine_CameraDisplay.Evaluate(from, to, phase).Position;
			AssertThat(drawn.DistanceTo(law) < 1e-4f).OverrideFailureMessage($"{drawn} is not {law} at {phase}").IsTrue();
		}
	}

	[TestCase]
	public void AStepTheLawDoesNotShapeIsTheStraightBlend()
	{
		// The following camera: the target restated and taken whole every update (f = 1, no roll).
		var rig = new Engine_CameraRig();
		Run(rig,
			S(Call.InterpolateFor, word: 0),
			S(Call.MoveToCut, values: new[] { 0f, 0f, 2f }),
			S(Call.SetDirectionCut, values: new[] { 0f, 1f, 0f }),
			Ease(1f), End(), Push(),
			S(Call.MoveTo, values: new[] { 0.5f, 0f, 2f }),
			S(Call.SetDirection, values: new[] { 0.1f, 1f, 0f }),
			Ease(1f), End(), Push(),
			S(Call.MoveTo, values: new[] { 1f, 0f, 2f }),
			S(Call.SetDirection, values: new[] { 0.2f, 1f, 0f }),
			Ease(1f), End(), Push());
		Engine_CameraDisplay.Step from = Previous(rig);
		Engine_CameraDisplay.Step to = Live(rig);
		AssertThat(Engine_CameraDisplay.Shaped(from, to)).IsFalse();
		foreach (float phase in new[] { 0.1f, 0.5f, 0.9f })
		{
			AssertThat(Engine_CameraDisplay.Evaluate(from, to, phase).Equals(Engine_CameraDisplay.Blend(from.Block, to.Block, phase))).IsTrue();
		}

		Engine_CameraRig.Block half = Engine_CameraDisplay.Blend(from.Block, to.Block, 0.5f);
		AssertThat(half.Position.DistanceTo(new Vector3(0.75f, 0f, 2f)) < 1e-5f).IsTrue();
	}

	[TestCase]
	public void ACutIsNeverShapedAndIsCountedOncePerPush()
	{
		var rig = new Engine_CameraRig();
		Run(rig,
			S(Call.InterpolateFor, word: 0),
			S(Call.MoveToCut, values: new[] { 0f, 0f, 2f }),
			S(Call.SetDirectionCut, values: new[] { 0f, 1f, 0f }),
			Ease(1f), End(), Push());
		AssertThat(rig.Managers[Manager].Cuts).IsEqual(0u);

		// A cut on one push and a plain step on the next, both taken before a frame is drawn: the
		// newest block carries no cut byte, the count still moved.
		Run(rig,
			S(Call.MoveToCut, values: new[] { 2f, 0f, 2f }),
			S(Call.SetCutThisFrame, word: 1),
			Ease(1f, cut: true), End(), Push(),
			S(Call.SetCutThisFrame, word: 0),
			S(Call.MoveTo, values: new[] { 2.1f, 0f, 2f }),
			Ease(1f), End(), Push());
		AssertThat(rig.Managers[Manager].Live!.Value.Cut).IsFalse();
		AssertThat(rig.Managers[Manager].Cuts).IsEqual(1u);

		// The step into a cut block has no shape, whatever its factor.
		Run(rig,
			S(Call.InterpolateFor, word: 4),
			S(Call.MoveTo, values: new[] { 9f, 0f, 2f }),
			Ease(0.25f, cut: true), End(), Push());
		AssertThat(rig.Managers[Manager].Cuts).IsEqual(2u);
		AssertThat(Engine_CameraDisplay.Shaped(Previous(rig), Live(rig))).IsFalse();
	}

	[TestCase]
	public void APosePlacedByACutSetterIsNotTheStepsOwn()
	{
		Engine_CameraRig rig = Blending();
		AssertThat(rig.Managers[Manager].LiveEase.Stepped).IsTrue();
		Run(rig, Ease(0.75f), End(), S(Call.MoveToCut, values: new[] { 30f, 0f, 2f }), Push());
		AssertThat(rig.Managers[Manager].LiveEase.Stepped).IsFalse();
		AssertThat(Engine_CameraDisplay.Shaped(Previous(rig), Live(rig))).IsFalse();
	}

	[TestCase]
	public void AStatedShakeIsBlendedAcrossItsStep()
	{
		// A shaken step: the rows the shake turned are stated; the residual carries the turn and
		// the display reaches the shaken block at the step.
		var rig = new Engine_CameraRig();
		Run(rig,
			S(Call.InterpolateFor, word: 0),
			S(Call.MoveToCut, values: new[] { 0f, 0f, 2f }),
			S(Call.SetDirectionCut, values: new[] { 0f, 1f, 0f }),
			Ease(1f), End(), Push(),
			S(Call.InterpolateFor, word: 4),
			S(Call.MoveTo, values: new[] { 4f, 0f, 2f }),
			Ease(0f), End(), Push());
		var up = new Vector3(0.05f, 0f, 1f).Normalized();
		var forward = new Vector3(0f, 1f, 0f);
		// The records in the order the guest states them, numbered in that order: the rig forgets
		// everything at a gap in the sequence (a lost record), so the shake is numbered after its step's begin.
		Statement begin = Ease(0.25f);
		var shake = new Statement
		{
			Sequence = ++_sequence, Call = Call.ShakeRotation, Depth = 1, Component = Comp,
		};
		shake.Values[6] = up.X; shake.Values[7] = up.Y; shake.Values[8] = up.Z;
		shake.Values[9] = forward.X; shake.Values[10] = forward.Y; shake.Values[11] = forward.Z;
		Run(rig, begin, shake, End(), Push());

		Engine_CameraDisplay.Step from = Previous(rig);
		Engine_CameraDisplay.Step to = Live(rig);
		AssertThat(Engine_CameraDisplay.Shaped(from, to)).IsTrue();
		AssertThat(Degrees(to.Block.Up, up) < 0.01f).IsTrue();
		Engine_CameraQuat residual = Engine_CameraDisplay.Residual(to.Ease);
		AssertThat(System.MathF.Abs(residual.W) < 0.99999f).IsTrue();
		AssertThat(System.MathF.Abs(Engine_CameraDisplay.Residual(from.Ease).W) > 0.999999f).IsTrue();

		Engine_CameraRig.Block end = Engine_CameraDisplay.Evaluate(from, to, 1f - 1e-4f);
		AssertThat(Degrees(end.Up, to.Block.Up) < 0.01f).IsTrue();
		Engine_CameraRig.Block half = Engine_CameraDisplay.Evaluate(from, to, 0.5f);
		AssertThat(Degrees(half.Up, to.Block.Up) < Degrees(from.Block.Up, to.Block.Up)).IsTrue();
	}
}
