using Godot;

namespace Refii.Camera;

/// <summary>
/// THE CAMERA BETWEEN TWO STATED STEPS. The guest eases its camera once per camera update; a frame
/// drawn between two updates shows the pose part-way from the first step's to the second's. This
/// is that pose, in the component's own terms, as a function of the phase between the two steps.
/// </summary>
/// <remarks>
/// <para>THE CONTRACT. <see cref="Evaluate"/> at phase 0 or below IS the first step's block and at
/// phase 1 or above IS the second's, value for value: the picture at every stated step is the block
/// the gate checked (<see cref="Engine_CameraGate"/>). In between nothing leaves the span of the two
/// steps: the position stays on their segment, the orientation on their arc, the FOV between
/// theirs.</para>
/// <para>THE LAW BETWEEN STEPS (<see cref="Engine_CameraRig"/>'s step, factor f as it started):
/// the step moved the position, roll and FOV the fraction f of the way to their targets and the
/// unrolled orientation by slerp(current, target, f) (four times f under a repulsion). The same
/// law run for a part <c>a</c> of the step leaves the fraction (1 − f)^a of the way still to go,
/// so the part of THIS step's motion done by phase a is <see cref="Progress"/> =
/// (1 − (1 − f)^a) / f: 0 at 0, 1 at 1, and two parts in a row compose to the whole step. A step
/// at f ≥ 1 arrives whole (the following camera: its target is restated every update), the law
/// gives it no shape, and the phase is the progress.</para>
/// <para>COMPOSED AS THE STEP COMPOSES. The unrolled orientation is slerped by the guest's own
/// slerp, the roll eased beside it and applied about forward after, as the step does
/// (rows → quaternion ⊗ axis-angle(Y, roll)); what the stated shake turned the rows by is the
/// residual between the stated orientation and that product, blended across the step.</para>
/// <para>A STEP THE LAW DOES NOT SHAPE (<see cref="Shaped"/> false: f ≥ 1, no roll, or the pose was
/// placed by a cut setter rather than eased) is <see cref="Blend"/>, the straight blend of the two
/// blocks — position lerp, orientation slerp — which is what the guest's own draw blend makes of
/// a pair (entities: `vmaddfp` prev·(1 − a) + live·a at 0x822113BC..0x822113E8 and
/// `bl sub_8220CF20` at 0x82211444, Fable 2 TU1).</para>
/// </remarks>
public static class Engine_CameraDisplay
{
	/// <summary>One stated step of a manager's camera: the block it pushed and how the step made it.</summary>
	public readonly record struct Step(Engine_CameraRig.Block Block, Engine_CameraRig.Eased Ease);

	/// <summary>
	/// The part of a step's motion done by <paramref name="phase"/> (0..1) under the law at
	/// <paramref name="factor"/>: (1 − (1 − f)^a) / f for 0 &lt; f &lt; 1, the phase itself otherwise.
	/// Monotone, 0 at 0 and 1 at 1.
	/// </summary>
	public static float Progress(float factor, float phase)
	{
		if (!(phase > 0f))
		{
			return 0f;
		}

		if (phase >= 1f)
		{
			return 1f;
		}

		if (!(factor > 0f) || factor >= 1f)
		{
			return phase;
		}

		double done = 1.0 - System.Math.Pow(1.0 - factor, phase);
		return (float)System.Math.Clamp(done / factor, 0.0, 1.0);
	}

	/// <summary>Whether the law gives the step from <paramref name="from"/> to <paramref name="to"/> a shape of its own.</summary>
	public static bool Shaped(in Step from, in Step to) =>
		from.Block.Known && to.Block.Known && from.Ease.Stepped && to.Ease.Stepped && !to.Block.Cut &&
		(to.Ease.Factor < 1f || to.Ease.TurnFactor < 1f || from.Ease.Roll != 0f || to.Ease.Roll != 0f) &&
		to.Ease.Factor > 0f;

	/// <summary>The straight blend of two blocks: position and FOV lerped, orientation slerped; the ends exact.</summary>
	public static Engine_CameraRig.Block Blend(Engine_CameraRig.Block from, Engine_CameraRig.Block to, float phase)
	{
		if (!(phase > 0f))
		{
			return from;
		}

		if (phase >= 1f)
		{
			return to;
		}

		Engine_CameraQuat a = Engine_CameraQuat.FromStepRows(from.Up, from.Forward);
		Engine_CameraQuat b = Engine_CameraQuat.FromStepRows(to.Up, to.Forward);
		Engine_CameraQuat q = Normalised(Engine_CameraQuat.Slerp(a, b, phase));
		return new Engine_CameraRig.Block(
			Lerp(from.Position, to.Position, phase), q.Up, q.Forward,
			Lerp(from.HorizontalFov, to.HorizontalFov, phase), to.Cut, to.Shake, from.Known && to.Known);
	}

	/// <summary>The camera at <paramref name="phase"/> between two stated steps, by the contract above.</summary>
	public static Engine_CameraRig.Block Evaluate(in Step from, in Step to, float phase)
	{
		if (!(phase > 0f))
		{
			return from.Block;
		}

		if (phase >= 1f)
		{
			return to.Block;
		}

		if (!Shaped(from, to))
		{
			return Blend(from.Block, to.Block, phase);
		}

		float moved = Progress(to.Ease.Factor, phase);
		float turned = Progress(to.Ease.TurnFactor, phase);

		Engine_CameraQuat current = Normalised(Engine_CameraQuat.Slerp(from.Ease.Current, to.Ease.Current, turned));
		float roll = Lerp(from.Ease.Roll, to.Ease.Roll, moved);
		Engine_CameraQuat unshaken = Compose(current, roll);
		Engine_CameraQuat shake = Normalised(Engine_CameraQuat.Slerp(Residual(from.Ease), Residual(to.Ease), phase));
		Engine_CameraQuat orientation = Normalised(shake * unshaken);

		return new Engine_CameraRig.Block(
			Lerp(from.Block.Position, to.Block.Position, moved), orientation.Up, orientation.Forward,
			Lerp(from.Block.HorizontalFov, to.Block.HorizontalFov, moved), to.Block.Cut, to.Block.Shake, true);
	}

	/// <summary>The step's product: the unrolled rows as a quaternion, then the roll about forward.</summary>
	public static Engine_CameraQuat Compose(Engine_CameraQuat current, float roll) =>
		Engine_CameraQuat.FromStepRows(current.Up, current.Forward) *
		Engine_CameraQuat.AxisAngle(Engine_CameraQuat.AxisY, roll);

	/// <summary>What stands between the stated orientation and the step's product of its unrolled
	/// orientation and roll: the stated shake's turn (identity for a step that had none).</summary>
	public static Engine_CameraQuat Residual(in Engine_CameraRig.Eased ease) =>
		Normalised(ease.Orientation * Compose(ease.Current, ease.Roll).Conjugate);

	private static Engine_CameraQuat Normalised(Engine_CameraQuat q)
	{
		float length = System.MathF.Sqrt(q.Dot(q));
		if (!(length > Engine_CameraQuat.Epsilon))
		{
			return Engine_CameraQuat.Identity;
		}

		float inverse = 1f / length;
		return new Engine_CameraQuat(q.X * inverse, q.Y * inverse, q.Z * inverse, q.W * inverse);
	}

	private static float Lerp(float from, float to, float t) => (to - from) * t + from;

	private static Vector3 Lerp(Vector3 from, Vector3 to, float t) => new(
		(to.X - from.X) * t + from.X,
		(to.Y - from.Y) * t + from.Y,
		(to.Z - from.Z) * t + from.Z);
}
