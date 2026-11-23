using Godot;
using Refii.Host;
using Refii.Core;
using Refii.Text;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Launcher.Tools;

/// <summary>
/// Runs the guest with GODOT DRIVING IT — the plan's "Refii becomes a library".
///
/// The difference from <see cref="HostBootCheck"/> is who owns the thread. There, the
/// guest was handed one and ran its own blocking SDL loop inside it; Godot merely
/// watched. Here Godot initialises the guest once and then advances it a slice per
/// frame from <c>_Process</c>, which is what makes the guest a simulation server rather
/// than an app that happens to live in the same process.
///
/// Initialisation is SYNCHRONOUS and stalls the first frame by seconds — it loads the
/// XEX and runs the guest's init. That is deliberate: SDL wants the main thread, and
/// running the guest on a worker left that question open.
///
/// <b>Opt-in.</b> Without <c>REFII_HOST_BOOT=1</c> this reports and exits.
/// </summary>
public partial class HostDrivenCheck : Node
{
	/// <summary>Seconds to drive the guest before reporting.</summary>
	[Export] public double WatchSeconds { get; set; } = 30.0;

	/// <summary>
	/// Optionally start a game part-way through the watch, so the in-game UI can be observed.
	///
	/// OPT-IN, and off by default. It exists because the question "does the guest stand up a
	/// HUD at all under a null device" cannot be answered from the front end, and answering it
	/// otherwise costs a human a playthrough. This does NOT drive input — it posts a front-end
	/// action onto the guest's tick pump, which is the same route the Godot menu uses, so the
	/// guest reaches the game through its own code rather than through synthetic key presses.
	///
	/// <c>REFII_HOST_BOOT_NEWGAME=1</c> turns it on; <c>REFII_HOST_BOOT_SECONDS</c> lengthens
	/// the watch, since a level load and its intro movie take far longer than the default 30s.
	/// </summary>
	private bool _startRequested;

	private bool _startPosted;

	private double _elapsed;
	private bool _driving;
	private long _steps;

	public override void _Ready()
	{
		int? abi = HostBridge.TryGetRawAbiVersion();
		if (abi != HostBridge.ExpectedAbiVersion)
		{
			GD.Print($"host bridge ABI {abi?.ToString() ?? "unavailable"}, " +
				$"expected {HostBridge.ExpectedAbiVersion} — rebuild refii_hostbridge.");
			GetTree().Quit(1);
			return;
		}

		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.Print("no game data found; set REFII_GAME_DATA or stage assets/Fable2/game.");
			GetTree().Quit(1);
			return;
		}

		GD.Print($"host bridge ABI {abi}");
		GD.Print($"game directory: {dataRoot}");

		_startRequested =
			System.Environment.GetEnvironmentVariable("REFII_HOST_BOOT_NEWGAME") == "1";
		if (double.TryParse(
				System.Environment.GetEnvironmentVariable("REFII_HOST_BOOT_SECONDS"),
				out double seconds) && seconds > 0.0)
		{
			WatchSeconds = seconds;
		}

		if (System.Environment.GetEnvironmentVariable("REFII_HOST_BOOT") != "1")
		{
			GD.Print("DRY RUN — set REFII_HOST_BOOT=1 to initialise and drive the guest.");
			GetTree().Quit(0);
			return;
		}

		GD.Print("initialising (this blocks for a few seconds)...");
		HostBridge.ProbeResult init = HostBridge.Initialize(dataRoot);
		GD.Print($"initialize: {init.Message}");
		if (!init.Success)
		{
			GetTree().Quit(1);
			return;
		}

		// A LIVE-GUEST TOOL, so it points the engine UI at the guest the same way the game
		// does. Without this it would read the authored stand-in and report an empty screen
		// while a real one was up.
		HostUiSource.Install();

		_driving = true;
	}

	public override void _Process(double delta)
	{
		if (!_driving)
			return;

		_elapsed += delta;

		// One slice of the guest's loop, from Godot's frame. This is the whole point.
		if (!HostBridge.Step())
		{
			GD.Print($"guest stopped after {_elapsed:F1}s and {_steps:N0} steps: " +
				HostBridge.HostedStatus);
			GD.Print("HOST DRIVEN FAILED — guest stopped on its own");
			_driving = false;
			GetTree().Quit(1);
			return;
		}

		_steps++;

		// A third of the way in, so the boot movies and the front-end handover are done. The
		// pump holds the action rather than faulting if the front end is not up yet, so an
		// early post is safe — this is only about not waiting longer than necessary.
		if (_startRequested && !_startPosted && _elapsed >= WatchSeconds / 3.0)
		{
			_startPosted = true;
			uint queued = HostBridge.PostFrontEndAction("New Game");
			GD.Print($"[host-driven] posted 'New Game' to the guest tick pump (queue {queued})");
		}

		if (_startRequested && _startPosted && _elapsed >= WatchSeconds / 2.0 &&
			_elapsed - _lastPresentationReport >= 5.0)
		{
			// Sampled on the way in as well as at the end. A presentation the guest stands up
			// during the load and tears down before the finish would otherwise never be seen,
			// and "the HUD exists briefly" and "the HUD never exists" are different problems.
			_lastPresentationReport = _elapsed;
			ReportPresentations($"t={_elapsed:F0}s");
		}

		if (_elapsed >= WatchSeconds)
		{
			GD.Print($"drove the guest for {_elapsed:F1}s across {_steps:N0} steps " +
				$"({_steps / _elapsed:F0} steps/s): {HostBridge.HostedStatus}");
			GD.Print("HOST DRIVEN OK — Godot owns the loop and the guest is alive");

			ReportPresentations("final");
			_driving = false;
			GetTree().Quit(0);
		}
	}

	private double _lastPresentationReport;

	/// <summary>
	/// What UI the guest has actually STOOD UP, from the one registry that holds every live
	/// presentation.
	///
	/// This is the half of "the in-game UI is missing" that can be settled without a human
	/// playing: either the guest built a HUD and Godot is not drawing it, or it never built
	/// one under a null device. Those need opposite fixes and look identical on screen.
	/// </summary>
	private static void ReportPresentations(string when)
	{
		System.Collections.Generic.List<Engine_UiModel.Presentation> live =
			HostBridge.GetPresentations();

		GD.Print($"[presentation] {when}: {live.Count} live " +
			$"(inGame={HostBridge.IsInGame})");
		foreach (Engine_UiModel.Presentation p in live)
		{
			GD.Print($"[presentation]   {p.Address:X8} " +
				$"{(p.IsShowing ? "SHOWING" : "hidden ")} " +
				$"{(p.IsFrozen ? "FROZEN " : string.Empty)}{(p.IsSuppressed ? "PARKED " : string.Empty)}" +
				$"'{p.Name}'");
		}
	}

	public override void _ExitTree()
	{
		// Always, even on the failure paths: leaving the guest's threads live at process
		// exit is what produced the abort on teardown.
		if (HostBridge.TryGetAbiVersion() is not null)
		{
			GD.Print($"shutdown: {HostBridge.Shutdown().Message}");
		}
	}
}
