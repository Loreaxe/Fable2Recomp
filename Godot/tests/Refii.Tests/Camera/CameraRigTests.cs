namespace Refii.Tests.Camera;

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using GdUnit4;
using Godot;
using Refii.Camera;
using static GdUnit4.Assertions;
using static Refii.Camera.Engine_CameraStatements;

/// <summary>
/// The camera rebuilt from the guest's statements (Engine_CameraRig), the guest's rotation
/// arithmetic it uses (Engine_CameraQuat) and the gate that checks it (Engine_CameraGate). Pure
/// model code.
/// </summary>
[TestSuite]
public class CameraRigTests
{
	private const uint Comp = 0x40001000u;
	private const uint Entity = 0x40002000u;
	private const uint Manager = 0x40003000u;
	private uint _sequence;

	private Statement S(Call call, uint depth = 0, uint comp = Comp, uint word = 0, params float[] values)
	{
		var s = new Statement { Sequence = ++_sequence, Call = call, Depth = (byte)depth, Component = comp, Word = word };
		for (int i = 0; i < values.Length; i++)
		{
			s.Values[i] = values[i];
		}

		return s;
	}

	private static void Run(Engine_CameraRig rig, params Statement[] statements)
	{
		foreach (Statement s in statements)
		{
			rig.Apply(s);
		}
	}

	private Statement Ease(float factor, bool cut = false, bool active = true, float radius = 0f) =>
		new Statement
		{
			Sequence = ++_sequence, Call = Call.EaseBegin, Component = Comp, Entity = Entity,
			Word = active ? 1u : 0u,
		}.With(0, factor).With(2, radius).With(3, cut ? 1f : 0f);

	private Statement End() => S(Call.EaseEnd);

	private Statement Push(bool drawn = true) => new()
	{
		Sequence = ++_sequence, Call = Call.Push, Manager = Manager, Player = 1, Flags = drawn ? FlagDrawn : (byte)0,
	};

	private Statement Block(Engine_CameraRig.Block b) => new Statement
	{
		Sequence = ++_sequence, Call = Call.GuestBlock, Manager = Manager, Entity = Entity, Word = b.Cut ? 1u : 0u,
	}.With(0, b.Position.X).With(1, b.Position.Y).With(2, b.Position.Z)
		.With(3, b.Up.X).With(4, b.Up.Y).With(5, b.Up.Z)
		.With(6, b.Forward.X).With(7, b.Forward.Y).With(8, b.Forward.Z)
		.With(9, b.HorizontalFov);

	private static void Near(Vector3 actual, Vector3 expected, float within = 1e-5f) =>
		AssertThat(actual.DistanceTo(expected) < within)
			.OverrideFailureMessage($"{actual} is not {expected}").IsTrue();

	[TestCase]
	public void TheCallIdsAreTheHeaders()
	{
		string? header = FindUp(Path.Combine("src", "runtime", "include", "core", "camera_statements.h"));
		AssertThat(header).OverrideFailureMessage("camera_statements.h not found").IsNotNull();
		var stated = new Dictionary<string, int>();
		foreach (Match m in Regex.Matches(File.ReadAllText(header!), @"\bk([A-Z]\w+)\s*=\s*(\d+),"))
		{
			stated[m.Groups[1].Value] = int.Parse(m.Groups[2].Value);
		}

		AssertThat(stated.Count).IsEqual(33);
		foreach ((string name, int id) in stated)
		{
			AssertThat(System.Enum.TryParse(name, out Call call) && (int)call == id)
				.OverrideFailureMessage($"{name} = {id} is not Engine_CameraStatements.Call's").IsTrue();
		}

		AssertThat(RecordWords).IsEqual(24);
	}

	[TestCase]
	public void TheRotationArithmeticIsTheGuests()
	{
		Engine_CameraQuat ahead = Engine_CameraQuat.FromDirection(new Vector3(0f, 2f, 0f));
		Near(ahead.Forward, Engine_CameraQuat.AxisY);
		Near(ahead.Up, Engine_CameraQuat.AxisZ);

		Engine_CameraQuat east = Engine_CameraQuat.FromDirection(new Vector3(3f, 0f, 0f));
		Near(east.Forward, Engine_CameraQuat.AxisX);
		Near(east.Up, Engine_CameraQuat.AxisZ);

		Vector3 slant = new Vector3(1f, -2f, 0.5f).Normalized();
		Engine_CameraQuat aimed = Engine_CameraQuat.FromDirection(slant);
		Near(aimed.Forward, slant);
		AssertThat(System.MathF.Abs(aimed.Up.Dot(slant)) < 1e-5f).IsTrue();
		AssertThat(aimed.Up.Z > 0f).IsTrue();

		(Vector3 up, Vector3 forward) = Engine_CameraQuat.FromAngles(0f, 0f, 0f);
		Near(up, Engine_CameraQuat.AxisZ);
		Near(forward, Engine_CameraQuat.AxisY);
		(Vector3 _, Vector3 yawed) = Engine_CameraQuat.FromAngles(Mathf.Pi / 2f, 0f, 0f);
		Near(yawed, Engine_CameraQuat.AxisX);

		Engine_CameraQuat rolled = Engine_CameraQuat.Identity *
			Engine_CameraQuat.AxisAngle(Engine_CameraQuat.AxisY, Mathf.Pi / 2f);
		Near(rolled.Forward, Engine_CameraQuat.AxisY);
		Near(rolled.Up, new Vector3(1f, 0f, 0f));

		Engine_CameraQuat half = Engine_CameraQuat.Slerp(Engine_CameraQuat.Identity, east, 0.5f);
		Near(half.Forward, new Vector3(1f, 1f, 0f).Normalized());
		Engine_CameraQuat negated = new(-east.X, -east.Y, -east.Z, -east.W);
		Near(Engine_CameraQuat.Slerp(Engine_CameraQuat.Identity, negated, 0.5f).Forward, half.Forward);
	}

	[TestCase]
	public void ACutLandsWholeAndTheGateSeesItPass()
	{
		var rig = new Engine_CameraRig();
		var gate = new Engine_CameraGate();
		rig.Observed = o => gate.Observe(o);
		var position = new Vector3(10f, 20f, 3f);
		var direction = new Vector3(0f, 1f, -0.2f);
		Run(rig,
			S(Call.InterpolateFor, word: 0),
			S(Call.MoveToCut, values: new[] { position.X, position.Y, position.Z }),
			S(Call.SetDirectionCut, values: new[] { direction.X, direction.Y, direction.Z }),
			S(Call.SetFovCut, values: new[] { 60f }),
			S(Call.SetCutThisFrame, word: 1),
			Ease(1f, cut: true), End());

		Engine_CameraQuat aimed = Engine_CameraQuat.FromDirection(direction);
		var expected = new Engine_CameraRig.Block(position, aimed.Up, aimed.Forward,
			60f * Engine_CameraRig.DegreesToRadians, true, 0f, true);
		Run(rig, Block(expected), Push());

		Engine_CameraRig.Manager? drawn = null;
		foreach (Engine_CameraRig.Manager m in rig.Managers.Values)
		{
			drawn = m;
		}

		AssertThat(drawn!.Live is { Known: true }).IsTrue();
		Near(drawn.Live!.Value.Position, position);
		Near(drawn.Live!.Value.Forward, aimed.Forward);
		AssertThat(gate.Reads.Rows).IsEqual(1);
		AssertThat(gate.Reads.Passed).IsEqual(1);
		AssertThat(gate.Pushes.Passed).IsEqual(1);
	}

	[TestCase]
	public void ABlendFollowsTheFactorTheStepStates()
	{
		var rig = new Engine_CameraRig();
		var start = new Vector3(0f, 0f, 0f);
		var end = new Vector3(8f, 0f, 0f);
		Run(rig,
			S(Call.MoveToCut, values: new[] { start.X, start.Y, start.Z }),
			S(Call.InterpolateFor, word: 4),
			S(Call.MoveTo, values: new[] { end.X, end.Y, end.Z }),
			Ease(0f), End());
		Engine_CameraRig.Component c = rig.Components[Comp];
		Near(c.Position, start);
		AssertThat(c.Factor).IsEqual(0.25f);

		Run(rig, Ease(0.25f), End());
		Near(c.Position, new Vector3(2f, 0f, 0f));
		AssertThat(c.Factor).IsEqual(0.5f);
		AssertThat(c.FactorDisagreements).IsEqual(0);

		Run(rig, Ease(0.4f), End());
		AssertThat(c.FactorDisagreements).IsEqual(1);
		Near(c.Position, new Vector3(2f + 6f * 0.4f, 0f, 0f));
	}

	[TestCase]
	public void TheDirectionBodyStandsForTheSetTargetThatCalledIt()
	{
		var rig = new Engine_CameraRig();
		Run(rig,
			S(Call.MoveToCut, values: new[] { 0f, 0f, 0f }),
			S(Call.SetDirection, depth: 1, values: new[] { 1f, 0f, 0f }),
			S(Call.SetTarget, depth: 0, values: new[] { 0f, 50f, 0f }));
		Near(rig.Components[Comp].TargetOrientation.Forward, Engine_CameraQuat.AxisX);

		Run(rig, S(Call.SetTarget, depth: 0, values: new[] { 0f, 50f, 0f }));
		Near(rig.Components[Comp].TargetOrientation.Forward, Engine_CameraQuat.AxisY);
	}

	[TestCase]
	public void AStatedShakeIsAddedInsideItsStep()
	{
		var rig = new Engine_CameraRig();
		Run(rig,
			S(Call.MoveToCut, values: new[] { 1f, 1f, 1f }),
			S(Call.InterpolateFor, word: 0),
			Ease(1f),
			S(Call.ShakePosition, depth: 1, values: new[] { 0.01f, -0.02f, 0.03f }),
			End());
		Near(rig.Components[Comp].Position, new Vector3(1.01f, 0.98f, 1.03f));
	}

	[TestCase]
	public void AGapForgetsUntilRestated()
	{
		var rig = new Engine_CameraRig();
		var gate = new Engine_CameraGate();
		rig.Observed = o => gate.Observe(o);
		Run(rig,
			S(Call.MoveToCut, values: new[] { 1f, 2f, 3f }),
			S(Call.SetDirectionCut, values: new[] { 0f, 1f, 0f }),
			Ease(1f), End());
		AssertThat(rig.Components[Comp].Block.Known).IsTrue();
		_sequence += 10;
		Run(rig, Ease(1f), End());
		AssertThat(rig.Gaps).IsEqual(1);
		AssertThat(rig.Components[Comp].Block.Known).IsFalse();
		Run(rig, Block(rig.Components[Comp].Block), Push());
		AssertThat(gate.Reads.Unknown).IsEqual(1);
		AssertThat(gate.Reads.Passed).IsEqual(0);
	}

	[TestCase]
	public void TheGateFailsAMillimetreOut()
	{
		var rig = new Engine_CameraRig();
		var gate = new Engine_CameraGate();
		rig.Observed = o => gate.Observe(o);
		Run(rig,
			S(Call.MoveToCut, values: new[] { 0f, 0f, 0f }),
			S(Call.SetDirectionCut, values: new[] { 0f, 1f, 0f }),
			Ease(1f), End());
		Engine_CameraRig.Block b = rig.Components[Comp].Block;
		Run(rig, Block(b with { Position = b.Position + new Vector3(0.0015f, 0f, 0f) }));
		AssertThat(gate.Reads.Passed).IsEqual(0);
		AssertThat(gate.Reads.MaxPosition > 0.001).IsTrue();
		AssertThat(Engine_CameraGate.AngleBetween(new Vector3(1f, 0f, 0f), new Vector3(1f, 0.0001f, 0f)) < 0.01).IsTrue();
	}

	[TestCase]
	public void TheStatementWordsRoundTrip()
	{
		Statement s = Ease(0.5f, cut: true, radius: 2f);
		s.Player = 2;
		s.Flags = FlagDrawn;
		Statement back = Statement.From(s.ToWords(), 0);
		AssertThat(back.Call).IsEqual(Call.EaseBegin);
		AssertThat(back.Player).IsEqual((byte)2);
		AssertThat(back.Flags).IsEqual(FlagDrawn);
		AssertThat(back.Values[0]).IsEqual(0.5f);
		AssertThat(back.Values[2]).IsEqual(2f);
		AssertThat(back.Entity).IsEqual(Entity);
	}

	private static string? FindUp(string relative)
	{
		for (string? at = System.AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
		{
			if (File.Exists(Path.Combine(at, relative)))
			{
				return Path.Combine(at, relative);
			}
		}

		return null;
	}
}

internal static class StatementTestExtensions
{
	public static Statement With(this Statement s, int at, float value)
	{
		s.Values[at] = value;
		return s;
	}
}
