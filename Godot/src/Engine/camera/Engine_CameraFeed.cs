using System.Collections.Generic;
using Refii.Host;
using static Refii.Camera.Engine_CameraStatements;

namespace Refii.Camera;

/// <summary>
/// The camera statements, drained from the title's bridge every frame into the one
/// <see cref="Engine_CameraRig"/>, with every guest block checked by the one
/// <see cref="Engine_CameraGate"/>. Each camera manager's last two pushes are its live and previous
/// blocks; the manager its push says this machine draws is the drawn camera.
/// </summary>
public static class Engine_CameraFeed
{
	private const int BatchRecords = 4096;

	private static readonly Engine_CameraRig Rig = new();
	private static readonly Engine_CameraGate Gate = new();
	private static readonly uint[] Words = new uint[BatchRecords * RecordWords];
	private static readonly List<Statement> Batch = new(BatchRecords);

	static Engine_CameraFeed()
	{
		Rig.Observed = observation => Gate.Observe(observation);
	}

	/// <summary>Statements taken since the session began.</summary>
	public static long Statements { get; private set; }

	/// <summary>Pushes taken since the session began; it moves when a manager's live block does.</summary>
	public static long PushCount { get; private set; }

	public static Engine_CameraRig CameraRig => Rig;

	/// <summary>Takes every statement the bridge holds past the last one taken.</summary>
	public static void Pump()
	{
		while (true)
		{
			uint taken = HostBridge.GetCameraStatements(Rig.LastSequence, Words);
			if (taken == 0u)
			{
				return;
			}

			Batch.Clear();
			Parse(Words, taken, Batch);
			foreach (Statement statement in Batch)
			{
				if (statement.Call == Call.Push)
				{
					++PushCount;
				}

				Rig.Apply(statement);
			}

			Statements += taken;
			if (taken < BatchRecords)
			{
				return;
			}
		}
	}

	/// <summary>The manager this machine draws (the last push that said so), or null.</summary>
	public static Engine_CameraRig.Manager? Drawn => Latest(manager => manager.Drawn);

	/// <summary>The manager of the player keyed <paramref name="player"/> in the guest's player map.</summary>
	public static Engine_CameraRig.Manager? ForPlayer(int player) =>
		Latest(manager => manager.Player == player);

	private static Engine_CameraRig.Manager? Latest(System.Func<Engine_CameraRig.Manager, bool> which)
	{
		Engine_CameraRig.Manager? best = null;
		foreach (Engine_CameraRig.Manager manager in Rig.Managers.Values)
		{
			if (which(manager) && manager.Live is { Known: true } &&
				(best is null || manager.LastPushFrame >= best.LastPushFrame))
			{
				best = manager;
			}
		}

		return best;
	}

	/// <summary>A rebuilt block in the shell's camera shape.</summary>
	public static HostBridge.GuestCamera? AsGuestCamera(Engine_CameraRig.Block? block) =>
		block is { Known: true } b
			? new HostBridge.GuestCamera(b.Position, b.Up, b.Forward, b.HorizontalFov, b.Cut)
			: null;

	/// <summary>Clears the gate's totals and kept rows, so a run measures from here.</summary>
	public static void ArmGate() => Gate.Arm();

	/// <summary>
	/// The gate's totals, the rig's counts, and the kept rows written to
	/// <c>camera_gate.csv</c> in <paramref name="logsDirectory"/> (when given).
	/// </summary>
	public static Dictionary<string, object?> Report(string? logsDirectory)
	{
		string? csv = null;
		if (!string.IsNullOrEmpty(logsDirectory))
		{
			csv = System.IO.Path.Combine(logsDirectory, "camera_gate.csv");
			Gate.WriteCsv(csv);
		}

		int factorDisagreements = 0;
		int cutDisagreements = 0;
		foreach (Engine_CameraRig.Component component in Rig.Components.Values)
		{
			factorDisagreements += component.FactorDisagreements;
			cutDisagreements += component.CutDisagreements;
		}

		var managers = new List<Dictionary<string, object?>>();
		foreach (Engine_CameraRig.Manager manager in Rig.Managers.Values)
		{
			managers.Add(new Dictionary<string, object?>
			{
				["manager"] = manager.Address.ToString("X8"),
				["player"] = manager.Player,
				["drawn"] = manager.Drawn,
				["pushes"] = manager.Pushes,
				["active"] = manager.Active?.Address.ToString("X8"),
				["activation"] = manager.Activation.ToString("X8"),
				["known"] = manager.Live is { Known: true },
			});
		}

		return new Dictionary<string, object?>
		{
			["tolerance"] = new Dictionary<string, object?>
			{
				["positionMetres"] = Engine_CameraGate.PositionMetres,
				["angleDegrees"] = Engine_CameraGate.AngleDegrees,
				["fovDegrees"] = Engine_CameraGate.FovDegrees,
			},
			["reads"] = Gate.Reads.Report(),
			["pushes"] = Gate.Pushes.Report(),
			["statements"] = Statements,
			["gaps"] = Rig.Gaps,
			["components"] = Rig.Components.Count,
			["factorDisagreements"] = factorDisagreements,
			["cutDisagreements"] = cutDisagreements,
			["managers"] = managers,
			["csv"] = csv,
		};
	}
}
