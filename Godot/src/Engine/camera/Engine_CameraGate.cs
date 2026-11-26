using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;

namespace Refii.Camera;

/// <summary>
/// THE GATE ON THE REBUILT CAMERA: every block the guest's getter built, side by side with the
/// block <see cref="Engine_CameraRig"/> rebuilt from the statements at that point in the guest's
/// order, and the errors between them. C1's gate is every row within <see cref="PositionMetres"/>,
/// <see cref="AngleDegrees"/> (forward and up) and <see cref="FovDegrees"/>.
/// </summary>
/// <remarks>
/// Two kinds of row: a READ is a block the getter built for one of its readers (the rebuild at that
/// statement); a PUSH compares the block the readers last saw with the block the push leaves (the
/// rebuild after the step), which differ wherever the getter's per-millisecond memo handed a reader
/// an older block. Rows whose rebuild is not yet known (a component first seen mid-life, a gap in
/// the ring) are counted apart, never as passes.
/// </remarks>
public sealed class Engine_CameraGate
{
	public const double PositionMetres = 0.001;
	public const double AngleDegrees = 0.01;
	public const double FovDegrees = 0.01;
	public const int Kept = 4096;

	public readonly record struct Row(
		uint Sequence, uint GuestFrame, uint Manager, uint Entity, uint Activation, bool AtPush,
		Engine_CameraRig.Block Guest, Engine_CameraRig.Block? Rebuilt,
		double PositionError, double ForwardError, double UpError, double FovError, bool CutAgrees)
	{
		public bool Known => Rebuilt is { Known: true };

		public bool Passes => Known && PositionError < PositionMetres && ForwardError < AngleDegrees &&
			UpError < AngleDegrees && FovError < FovDegrees && CutAgrees;
	}

	/// <summary>The totals of one kind of row since the gate was armed.</summary>
	public sealed class Totals
	{
		public long Rows;
		public long Unknown;
		public long Passed;
		public long CutDisagreements;
		public double MaxPosition;
		public double MaxForward;
		public double MaxUp;
		public double MaxFov;
		public Row? Worst;

		public Dictionary<string, object?> Report() => new()
		{
			["rows"] = Rows,
			["unknown"] = Unknown,
			["passed"] = Passed,
			["failed"] = Rows - Unknown - Passed,
			["cutDisagreements"] = CutDisagreements,
			["maxPositionMetres"] = MaxPosition,
			["maxForwardDegrees"] = MaxForward,
			["maxUpDegrees"] = MaxUp,
			["maxFovDegrees"] = MaxFov,
			["worst"] = Worst is { } w ? Describe(w) : null,
		};
	}

	private readonly Row[] _rows = new Row[Kept];
	private int _next;
	private int _count;

	public Totals Reads { get; private set; } = new();

	public Totals Pushes { get; private set; } = new();

	/// <summary>Clears the totals and the kept rows.</summary>
	public void Arm()
	{
		Reads = new Totals();
		Pushes = new Totals();
		_next = 0;
		_count = 0;
	}

	public Row Observe(in Engine_CameraRig.Observation observation)
	{
		Engine_CameraRig.Block guest = observation.Guest;
		Row row;
		if (observation.Rebuilt is { } rebuilt)
		{
			row = new Row(observation.Sequence, observation.GuestFrame, observation.Manager, observation.Entity,
				observation.Activation, observation.AtPush, guest, rebuilt,
				guest.Position.DistanceTo(rebuilt.Position),
				AngleBetween(guest.Forward, rebuilt.Forward),
				AngleBetween(guest.Up, rebuilt.Up),
				System.Math.Abs(((double)guest.HorizontalFov - rebuilt.HorizontalFov) * (180.0 / System.Math.PI)),
				guest.Cut == rebuilt.Cut);
		}
		else
		{
			row = new Row(observation.Sequence, observation.GuestFrame, observation.Manager, observation.Entity,
				observation.Activation, observation.AtPush, guest, null, 0, 0, 0, 0, true);
		}

		Totals totals = observation.AtPush ? Pushes : Reads;
		++totals.Rows;
		if (!row.Known)
		{
			++totals.Unknown;
		}
		else
		{
			if (row.Passes)
			{
				++totals.Passed;
			}

			if (!row.CutAgrees)
			{
				++totals.CutDisagreements;
			}

			bool worse = totals.Worst is not { } w || Badness(row) > Badness(w);
			totals.MaxPosition = System.Math.Max(totals.MaxPosition, row.PositionError);
			totals.MaxForward = System.Math.Max(totals.MaxForward, row.ForwardError);
			totals.MaxUp = System.Math.Max(totals.MaxUp, row.UpError);
			totals.MaxFov = System.Math.Max(totals.MaxFov, row.FovError);
			if (worse)
			{
				totals.Worst = row;
			}
		}

		_rows[_next] = row;
		_next = (_next + 1) % Kept;
		_count = System.Math.Min(_count + 1, Kept);
		return row;
	}

	/// <summary>The kept rows, oldest first.</summary>
	public IEnumerable<Row> Rows()
	{
		int start = _count < Kept ? 0 : _next;
		for (int i = 0; i < _count; i++)
		{
			yield return _rows[(start + i) % Kept];
		}
	}

	/// <summary>The kept rows as CSV, the guest's block and the rebuild side by side.</summary>
	public void WriteCsv(string path)
	{
		var text = new StringBuilder();
		text.AppendLine("sequence,frame,kind,manager,entity,activation,known,passes," +
			"guest_px,guest_py,guest_pz,guest_fx,guest_fy,guest_fz,guest_ux,guest_uy,guest_uz,guest_fov,guest_cut," +
			"rig_px,rig_py,rig_pz,rig_fx,rig_fy,rig_fz,rig_ux,rig_uy,rig_uz,rig_fov,rig_cut," +
			"err_pos_m,err_fwd_deg,err_up_deg,err_fov_deg");
		foreach (Row row in Rows())
		{
			Engine_CameraRig.Block g = row.Guest;
			Engine_CameraRig.Block r = row.Rebuilt ?? default;
			text.AppendLine(string.Join(",",
				row.Sequence, row.GuestFrame, row.AtPush ? "push" : "read",
				row.Manager.ToString("X8"), row.Entity.ToString("X8"), row.Activation.ToString("X8"),
				row.Known ? 1 : 0, row.Passes ? 1 : 0,
				F(g.Position.X), F(g.Position.Y), F(g.Position.Z), F(g.Forward.X), F(g.Forward.Y), F(g.Forward.Z),
				F(g.Up.X), F(g.Up.Y), F(g.Up.Z), F(g.HorizontalFov), g.Cut ? 1 : 0,
				F(r.Position.X), F(r.Position.Y), F(r.Position.Z), F(r.Forward.X), F(r.Forward.Y), F(r.Forward.Z),
				F(r.Up.X), F(r.Up.Y), F(r.Up.Z), F(r.HorizontalFov), r.Cut ? 1 : 0,
				F(row.PositionError), F(row.ForwardError), F(row.UpError), F(row.FovError)));
		}

		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text.ToString());
	}

	/// <summary>The angle between two directions in degrees, exact for small angles.</summary>
	public static double AngleBetween(Vector3 a, Vector3 b)
	{
		double ax = a.X, ay = a.Y, az = a.Z, bx = b.X, by = b.Y, bz = b.Z;
		double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
		double cross = System.Math.Sqrt(cx * cx + cy * cy + cz * cz);
		double dot = ax * bx + ay * by + az * bz;
		return System.Math.Atan2(cross, dot) * (180.0 / System.Math.PI);
	}

	private static double Badness(Row row) =>
		row.PositionError / PositionMetres + row.ForwardError / AngleDegrees + row.UpError / AngleDegrees +
		row.FovError / FovDegrees + (row.CutAgrees ? 0 : 1);

	private static string F(double value) => value.ToString("G9", CultureInfo.InvariantCulture);

	public static Dictionary<string, object?> Describe(Row row) => new()
	{
		["sequence"] = row.Sequence,
		["frame"] = row.GuestFrame,
		["kind"] = row.AtPush ? "push" : "read",
		["manager"] = row.Manager.ToString("X8"),
		["entity"] = row.Entity.ToString("X8"),
		["activation"] = row.Activation.ToString("X8"),
		["guest"] = BlockFields(row.Guest),
		["rebuilt"] = row.Rebuilt is { } r ? BlockFields(r) : null,
		["positionMetres"] = row.PositionError,
		["forwardDegrees"] = row.ForwardError,
		["upDegrees"] = row.UpError,
		["fovDegrees"] = row.FovError,
		["cutAgrees"] = row.CutAgrees,
	};

	private static Dictionary<string, object?> BlockFields(Engine_CameraRig.Block block) => new()
	{
		["position"] = new[] { block.Position.X, block.Position.Y, block.Position.Z },
		["forward"] = new[] { block.Forward.X, block.Forward.Y, block.Forward.Z },
		["up"] = new[] { block.Up.X, block.Up.Y, block.Up.Z },
		["fov"] = block.HorizontalFov,
		["cut"] = block.Cut,
		["known"] = block.Known,
	};
}
