using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Godot;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// A local debug channel into the RUNNING game — one newline-delimited JSON request per line,
/// one response per line, on 127.0.0.1.
///
/// WHY. Every diagnostic in this project is a `GD.Print` decided before the run: a question
/// nobody anticipated costs an edit, a package build, a codegen and a load — ten minutes to ask
/// one thing, which is why so much of the logging is speculative and so much of it is noise.
/// And nothing here could SEE: every visual defect on these screens was found by a person
/// looking at the game and describing it, while the logs said `slide=1 contents.x=0` and left
/// the geometry to be inferred.
///
/// This makes the running game answerable instead. `shot` writes a PNG of the window, `state`
/// says which screen the guest has up and what the owned list holds, `press` drives the pad,
/// and `elements` dumps a presentation's tree with the positions and opacities that a page bug
/// actually lives in.
///
/// The `threads`, `objects`, `perf`, `exports`, `cvars`, `cvar-set`, `vfs` and `sockets` ops
/// reach through the host bridge into the ReXGlue runtime itself — guest backtraces, the kernel
/// handle table, live counters, the export tables, the cvar registry and the VFS — so a frozen
/// run names its own stuck thread instead of needing gdb as a parent.
///
/// OFF UNLESS ASKED, AND LOOPBACK ONLY. `REFII_DEBUG_PORT` turns it on; it binds 127.0.0.1 and
/// nothing else, so it is not reachable from another machine. A shipped run never sets it.
///
/// The socket is served from `_Process`, on Godot's own thread, because everything it reads —
/// the scene tree, the viewport, the UI model — belongs to that thread. There is no locking
/// here and there must not need to be.
/// </summary>
public partial class GameDebugServer : Node
{
	/// <summary>Host-owned profiling uniforms; retained across level reloads and reset by process startup.</summary>
	private static readonly Refii.Core.Engine_ShaderZeroWorkControls ShaderZeroWork = new();
	private static readonly Refii.Core.Engine_ShaderCostDiagnostic ShaderCostDiagnostic = new();

	/// <summary>The fx op's level-double census as a reply object.</summary>
	private static Dictionary<string, object?> DoublesReply((int Doubles, int LevelNodes, List<string> Rows) d) => new()
	{
		["count"] = d.Doubles,
		["levelNodes"] = d.LevelNodes,
		["rows"] = d.Rows,
	};

	/// <summary>
	/// `REFII_DEBUG_PORT=1` (or a port number) turns the channel on; so does `debug_channel =
	/// true` in the package config, which is how a launch from the launcher gets one.
	/// </summary>
	public static bool Wanted =>
		(System.Environment.GetEnvironmentVariable("REFII_DEBUG_PORT") ?? string.Empty).Length != 0 ||
		ConfigWants();

	private Dictionary<string, int>? _cachedProcessingCensus;
	private List<SubViewport>? _cachedSubViewports;
	private ulong _cachedProcessingCensusUntilMs;

	private static bool ConfigWants()
	{
		try
		{
			return Refii.Core.Engine_RefiiConfig.Read(out _).TryGetValue("debug_channel", out string? v) &&
				v.Trim().Equals("true", System.StringComparison.OrdinalIgnoreCase);
		}
		catch (System.Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// The first port to try. A FIXED port was the original design and it was wrong — see
	/// <see cref="HandshakePath"/>.
	///
	/// THE VARIABLE MEANS TWO THINGS AND ONLY ONE OF THEM IS A PORT. `REFII_DEBUG_PORT=1` is
	/// how a run says "on" — and parsing that as a port number asked to bind port 1, which is
	/// privileged, so the scan walked 1..16 and gave up: `no free port in 1..16`. Anything
	/// below the unprivileged floor is a flag, not a port.
	/// </summary>
	private const int DefaultPort = 8787;

	private const int FirstUnprivilegedPort = 1024;

	private static int FirstPort =>
		int.TryParse(System.Environment.GetEnvironmentVariable("REFII_DEBUG_PORT"), out int port)
		&& port >= FirstUnprivilegedPort && port < 65536
			? port
			: DefaultPort;

	/// <summary>
	/// Where this process publishes {pid, port, boot} so a client can find THIS game and no
	/// other.
	///
	/// A FIXED PORT PLUS A RESTARTING GAME IS A STALENESS BUG WAITING TO HAPPEN, and this
	/// project has already paid for that class of mistake twice: months-old logs read as "the
	/// guest logged nothing", and — this session — a `pkill` whose pattern matched its own
	/// command line, leaving a game running for twenty minutes while I believed it dead. On a
	/// fixed port that zombie keeps the socket, the NEW game fails to bind and silently has no
	/// channel, and every question I ask is answered by the corpse. Screenshots of a dead run,
	/// button presses into a dead run, and a completely coherent wrong conclusion.
	///
	/// So the port is not fixed and not assumed: each process takes the first free one, writes
	/// its identity here, and removes the file on the way out. A client reads the file, checks
	/// the pid is alive, and compares the boot id it saw last — so "the game restarted under
	/// you" is a thing it can SAY rather than something it cannot notice.
	/// </summary>
	public static string HandshakePath =>
		System.Environment.GetEnvironmentVariable("REFII_DEBUG_HANDSHAKE") is { Length: > 0 } path
			? path
			: System.IO.Path.Combine(
				System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
				".cache", "refii-scratch", "refii-debug.json");

	/// <summary>Random per process: what tells one run from the next on the same port.</summary>
	private static readonly string BootId = System.Guid.NewGuid().ToString("N")[..12];

	private static readonly System.Diagnostics.Stopwatch Uptime =
		System.Diagnostics.Stopwatch.StartNew();

	private int _port;

	private TcpServer? _server;
	private readonly List<StreamPeerTcp> _clients = new();

	/// <summary>Buttons a `press` asked for, held for a few frames; OR'd in by <see cref="GameInput"/>.</summary>
	private static uint _pressed;

	private static int _pressFrames;

	/// <summary>The key the `key` op is holding and the frames left before its release.</summary>
	private static Key _heldKey = Key.None;
	private static int _keyFrames;

	/// <summary>The window pixel the `mouse` op last put the pointer at; null before its first call.</summary>
	private static Vector2? _mouseAt;

	/// <summary>The button the `mouse` op is holding, the frames left before its release, and the held mask.</summary>
	private static MouseButton _heldMouse = MouseButton.None;
	private static int _mouseFrames;
	private static MouseButtonMask _mouseMask;

	/// <summary>When the `mouse` op last injected anything (engine ticks, ms); 0 never.</summary>
	private static ulong _mouseScriptedAt;

	/// <summary>
	/// The pointer is the debug channel's: a `mouse` op ran within the last two seconds, or its click is
	/// held. The pad presses its leftovers make (a right click's B, the wheel's D-pad steps, a prompt's
	/// button) then reach the guest with the window unfocused, as the channel's own presses do
	/// (GameInput.Publish0); two seconds covers the longest wheel play-out (eight notches).
	/// </summary>
	public static bool PointerScripted =>
		_heldMouse != MouseButton.None ||
		(_mouseScriptedAt != 0 && Time.GetTicksMsec() - _mouseScriptedAt < 2000);

	/// <summary>The guest pad a `press` is aimed at — 0 unless it names one. Player two's
	/// drop-in is a START on a pad that is not player one's.</summary>
	private static int _pressPad;

	/// <summary>Pads a `press` has aimed at, connected from then on: a pad that vanished
	/// between presses would read to the guest as unplugged.</summary>
	private static int _debugPads = 1;

	public static bool PadConnected(int pad) => (_debugPads & (1 << pad)) != 0;

	/// <summary>Stick and trigger values a `press` asked for, XInput-scaled, held like the
	/// buttons; merged by <see cref="GameInput"/> against the real devices.</summary>
	private static int _stickLX, _stickLY, _stickRX, _stickRY;

	private static uint _trigL, _trigR;

	private static int _stickFrames;

	/// <summary>Consumed once per input frame alongside <see cref="Buttons"/>.</summary>
	public static bool Sticks(int pad, out int lx, out int ly, out int rx, out int ry,
		out uint lt, out uint rt)
	{
		bool live = pad == _pressPad && _stickFrames > 0 && --_stickFrames >= 0;
		lx = live ? _stickLX : 0;
		ly = live ? _stickLY : 0;
		rx = live ? _stickRX : 0;
		ry = live ? _stickRY : 0;
		lt = live ? _trigL : 0;
		rt = live ? _trigR : 0;
		return live;
	}

	// ---- input recording and replay ----

	/// <summary>One published pad frame, raw and pre-mask: the masks are game-state driven
	/// and re-apply naturally on replay.</summary>
	public readonly record struct PadFrame(
		uint Buttons, uint Lt, uint Rt, int Lx, int Ly, int Rx, int Ry);

	private static List<PadFrame>? _recording;

	private static PadFrame[]? _replay;

	private static int _replayAt;

	public static void RecordInputFrame(uint buttons, uint lt, uint rt, int lx, int ly,
		int rx, int ry)
	{
		if (_recording is { } tape && tape.Count < 4_000_000)
		{
			tape.Add(new PadFrame(buttons, lt, rt, lx, ly, rx, ry));
		}
	}

	public static bool ReplayFrame(out PadFrame frame)
	{
		frame = default;
		if (_replay is not { } tape || _replayAt >= tape.Length)
		{
			_replay = null;
			return false;
		}

		frame = tape[_replayAt++];
		return true;
	}

	// ---- flight recorder ----

	/// <summary>
	/// Once a second, one compact line into user://logs/refii_flight.jsonl: what the run was
	/// doing. After a freeze or a silent death the LAST LINE says where it stood, and the
	/// thread r1/lr snapshot lets the arena reader walk guest stacks with no channel — the one
	/// gap frozen-run backtraces had. Rotates once at ~2 MB; a run that lives for hours keeps
	/// its most recent history.
	/// </summary>
	private double _flightDue;
	private string? _flightLogPath;
	private int _flightWritesSinceSizeCheck;

	private void RecordFlight(double delta)
	{
		_flightDue -= delta;
		if (_flightDue > 0)
		{
			return;
		}

		_flightDue = 1.0;
		try
		{
			string path = _flightLogPath ??= ProjectSettings.GlobalizePath("user://logs/refii_flight.jsonl");
			if (++_flightWritesSinceSizeCheck >= 60)
			{
				_flightWritesSinceSizeCheck = 0;
				if (System.IO.File.Exists(path) &&
					new System.IO.FileInfo(path).Length > 2_000_000)
				{
					System.IO.File.Move(path, path + ".1", overwrite: true);
				}
			}

			var line = new Dictionary<string, object?>
			{
				["t"] = System.DateTime.UtcNow.ToString("HH:mm:ss"),
				["up"] = (int)Uptime.Elapsed.TotalSeconds,
				["boot"] = BootId,
				["guest"] = HostBridgeUp(),
			};
			if (Engine_UiSource.Current.GetInGameScreens() is { } screens)
			{
				line["screen"] = screens.Current.ToString();
			}

			var threads = new List<string>();
			foreach (Refii.Host.HostBridge.GuestThreadInfo info in
				Refii.Host.HostBridge.GetGuestThreads())
			{
				if (info.IsGuestThread)
				{
					threads.Add($"{info.Tid}:{info.R1:X8}:{info.Lr:X8}");
				}
			}

			if (threads.Count > 0)
			{
				line["threads"] = threads;
			}

			System.IO.File.AppendAllText(path, Refii.Core.Engine_Json.Serialize(line) + "\n");
		}
		catch (System.Exception)
		{
			// The recorder must never take the game down; a missed beat is a missed beat.
		}
	}

	/// <summary>Plain Godot lights spawned by `render testLights=` — see its use.</summary>
	private readonly List<OmniLight3D> _testLights = new();

	/// <summary>The positional shadow filter last set through `render`; null is the project's.</summary>
	private int? _positionalFilter;

	/// <summary>The shader debug view last set through `render`; 0 is off.</summary>
	private int _lightDebug;

	/// <summary>
	/// Brings the plain-light population to <paramref name="want"/>.
	/// </summary>
	/// <remarks>
	/// Only position and range are set; every other property stays at Godot's default, so the
	/// comparison against a placed fixture isolates what we do to a light. The range matches the
	/// fixtures' own scale (`placed_light_range` 1.5 over a shipped 5-6 m torch).
	/// </remarks>
	private void SetTestLights(int want)
	{
		for (int i = _testLights.Count - 1; i >= want; --i)
		{
			if (GodotObject.IsInstanceValid(_testLights[i]))
			{
				_testLights[i].QueueFree();
			}

			_testLights.RemoveAt(i);
		}

		if (want == 0 || _testLights.Count >= want)
		{
			return;
		}

		// Parented to the camera's parent, not the camera, so the lights stay static like the
		// fixtures they are compared against.
		Camera3D? camera = GetViewport()?.GetCamera3D();
		Node3D? host = camera?.GetParent() as Node3D ?? camera;
		if (camera is null || host is null)
		{
			return;
		}

		Vector3 eye = camera.GlobalPosition;
		for (int i = _testLights.Count; i < want; ++i)
		{
			float angle = i * 2.399963f;          // the golden angle, so a ring does not stripe
			float radius = 3.0f + ((i % 7) * 1.5f);
			var light = new OmniLight3D
			{
				Name = $"TestLight{i}",
				OmniRange = 9.0f,
			};

			// In the tree first: `Position` is local, and the level root mirrors X.
			host.AddChild(light);
			light.GlobalPosition = eye + new Vector3(
				Mathf.Cos(angle) * radius, 1.5f + ((i % 3) * 0.75f), Mathf.Sin(angle) * radius);
			_testLights.Add(light);
		}
	}

	/// <summary>The buttons the debug channel is holding on <paramref name="pad"/> this
	/// frame, or 0. Read once per pad per input frame.</summary>
	public static uint Buttons(int pad) =>
		pad == _pressPad && _pressFrames > 0 && --_pressFrames >= 0 ? _pressed : 0u;

	/// <summary>
	/// The `press` op's JOYPAD mode: the press raised as joypad events on the joypad the game treats as
	/// the pad's (<see cref="GameInput.JoypadForPress"/>; `player` names the pad by
	/// <see cref="GameInput.PadOfPlayer"/>), through <see cref="GameDebugJoypad"/>. Nothing is put in
	/// the scripted mask: the guest gets it through the device path, as a player's controller.
	/// `dummy` names the dummy controller's pad (<see cref="GameDebugDummyPad"/>), and so does
	/// player 2 while no pad is player two's yet and the dummy is connected.
	/// </summary>
	private Dictionary<string, object?> JoypadPress(JsonElement root, uint mask, int frames, int pad)
	{
		if (root.TryGetProperty("dummy", out JsonElement dm) && dm.ValueKind == JsonValueKind.True)
		{
			pad = GameDebugDummyPad.PressPad(out string? refused);
			if (refused is not null)
			{
				return new Dictionary<string, object?> { ["ok"] = false, ["error"] = refused };
			}
		}
		else if (root.TryGetProperty("player", out JsonElement pl))
		{
			int player = pl.GetInt32();
			pad = GameInput.PadOfPlayer(player);
			if (pad < 0 && player == 2 && Refii.Core.Engine_DummyJoypad.Connected)
			{
				pad = GameDebugDummyPad.PressPad(out _);
			}

			if (pad < 0)
			{
				return new Dictionary<string, object?>
				{
					["ok"] = false,
					["error"] = $"no guest pad is player {player}'s (name the pad instead)",
				};
			}
		}

		int device = GameInput.JoypadForPress(pad);
		if (device < 0)
		{
			return new Dictionary<string, object?>
			{
				["ok"] = false,
				["error"] = $"no joypad holds guest pad {pad} or would take it on a press: {GameInput.DescribeJoypads()}",
			};
		}

		static float? Axis(JsonElement parent, string name) =>
			parent.TryGetProperty(name, out JsonElement axis) ? (float)axis.GetDouble() : null;
		JoyButton[] buttons = GameDebugJoypad.Buttons(mask);
		(JoyAxis Axis, float Value)[] axes = GameDebugJoypad.Axes(Axis(root, "lx"), Axis(root, "ly"),
			Axis(root, "rx"), Axis(root, "ry"), Axis(root, "lt"), Axis(root, "rt"));
		bool keyboardMouse = Engine_UiInputMode.KeyboardMouse;
		GameDebugJoypad.Press(device, buttons, axes, frames);
		GD.Print($"[input] debug joypad press on joypad {device} '{Refii.Core.Engine_Joypads.Name(device)}' (guest pad {pad}): " +
			$"{string.Join("+", System.Array.ConvertAll(buttons, b => b.ToString()))}" +
			$"{(axes.Length > 0 ? $" axes {string.Join(" ", System.Array.ConvertAll(axes, a => $"{a.Axis}={a.Value:0.##}"))}" : string.Empty)}" +
			$" for {System.Math.Max(1, frames)} frame(s)");
		return Ok(new Dictionary<string, object?>
		{
			["mode"] = "joypad",
			["device"] = device,
			["name"] = Refii.Core.Engine_Joypads.Name(device),
			["known"] = Refii.Core.Engine_Joypads.Known(device),
			["dummy"] = Refii.Core.Engine_DummyJoypad.Is(device),
			["pad"] = pad,
			["buttons"] = System.Array.ConvertAll(buttons, b => b.ToString()),
			["axes"] = System.Array.ConvertAll(axes, a => $"{a.Axis}={a.Value:0.##}"),
			["frames"] = System.Math.Max(1, frames),
			["keyboardMouseBefore"] = keyboardMouse,
			// Bumped on every device switch: two presses a while apart whose generations differ by
			// one at most saw no switch in between.
			["inputModeGenerationBefore"] = Engine_UiInputMode.Generation,
			["focused"] = GetWindow().HasFocus(),
		});
	}

	/// <summary>
	/// Ends the `key` op's hold once its frames are spent: the release goes through the same input
	/// pipeline as the press, so the device switch and the bindings see a real key go up.
	/// </summary>
	private static void ReleaseHeldKey()
	{
		if (_heldKey == Key.None || --_keyFrames > 0)
		{
			return;
		}

		Input.ParseInputEvent(new InputEventKey { Keycode = _heldKey, PhysicalKeycode = _heldKey, Pressed = false });
		_heldKey = Key.None;
	}

	/// <summary>
	/// Ends the `mouse` op's click once its frames are spent, through the same pipeline as the press,
	/// so the bindings and the pointer see the button go up where the pointer is.
	/// </summary>
	private static void ReleaseHeldMouse()
	{
		if (_heldMouse == MouseButton.None || --_mouseFrames > 0)
		{
			return;
		}

		_mouseMask &= ~MaskOf(_heldMouse);
		Vector2 at = _mouseAt ?? Vector2.Zero;
		Input.ParseInputEvent(new InputEventMouseButton
		{
			Position = at, GlobalPosition = at, ButtonIndex = _heldMouse, Pressed = false, ButtonMask = _mouseMask,
		});
		_heldMouse = MouseButton.None;
	}

	private static MouseButtonMask MaskOf(MouseButton button) => button switch
	{
		MouseButton.Left => MouseButtonMask.Left,
		MouseButton.Right => MouseButtonMask.Right,
		MouseButton.Middle => MouseButtonMask.Middle,
		MouseButton.WheelUp => (MouseButtonMask)(1 << ((int)MouseButton.WheelUp - 1)),
		MouseButton.WheelDown => (MouseButtonMask)(1 << ((int)MouseButton.WheelDown - 1)),
		_ => 0,
	};

	/// <summary>Installs any owned samplers before attempting the opt-in loopback listener.</summary>
	public override void _Ready()
	{
		if (!Wanted)
		{
			return;
		}

		// The first FREE port, not a fixed one. A port already in use almost always means a
		// previous instance is still alive — which is worth saying, because that instance is
		// exactly what a client would otherwise be talking to.
		_server = new TcpServer();
		int first = FirstPort;
		for (int port = first; port < first + 16; ++port)
		{
			if (_server.Listen((ushort)port, "127.0.0.1") == Error.Ok)
			{
				_port = port;
				break;
			}
		}

		if (_port == 0)
		{
			_server = null;
			GD.Print($"[debug-server] no free port in {first}..{first + 15} — no debug channel " +
				"this run (is an older instance still running?)");
			return;
		}

		if (_port != first)
		{
			GD.Print($"[debug-server] port {first} was taken — using {_port}. Something else is " +
				"holding it; if that is an older game, it is still answering questions.");
		}

		Publish();
		GD.Print($"[debug-server] listening on 127.0.0.1:{_port} (pid {OS.GetProcessId()}, " +
			$"boot {BootId}) — handshake at {HandshakePath}");
	}

	/// <summary>
	/// Writes {pid, port, boot} so a client can find THIS process — plus, once the guest is up,
	/// the arena identity (see HostBridge.GetGuestArenaInfo). The arena goes in the FILE, not
	/// only in channel replies, because its consumer is the out-of-process reader whose whole
	/// purpose is the frozen run this thread can no longer answer for.
	/// </summary>
	private void Publish()
	{
		try
		{
			var handshake = new Dictionary<string, object?>
			{
				["pid"] = OS.GetProcessId(),
				["port"] = _port,
				["boot"] = BootId,
				["started"] = _started,
			};
			if (Refii.Host.HostBridge.GetGuestArenaInfo() is { } arena)
			{
				handshake["arena"] = arena.Name;
				handshake["membase"] = arena.Membase;
				_arenaPublished = true;
			}

			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(HandshakePath)!);
			System.IO.File.WriteAllText(HandshakePath, Refii.Core.Engine_Json.Serialize(handshake));
		}
		catch (System.Exception error)
		{
			GD.Print($"[debug-server] could not write {HandshakePath}: {error.Message}");
		}
	}

	/// <summary>Stable across republishes: the handshake must not look like a new run.</summary>
	private readonly string _started = System.DateTime.UtcNow.ToString("O");

	/// <summary>The arena exists only once the guest is up, which is after _Ready — so each
	/// frame until it is published, Publish() is retried from _Process.</summary>
	private bool _arenaPublished;

	public override void _ExitTree()
	{
		// The dummy controller is the channel's: it does not outlive it.
		GameDebugDummyPad.Disconnect();
		foreach (StreamPeerTcp client in _clients)
		{
			client.DisconnectFromHost();
		}

		_clients.Clear();
		_server?.Stop();
		_server = null;

		// Take the handshake with it, but ONLY if it is still ours — another game may have
		// started and published over it, and removing that one would strand a live channel.
		try
		{
			if (System.IO.File.Exists(HandshakePath) &&
				System.IO.File.ReadAllText(HandshakePath).Contains(BootId, System.StringComparison.Ordinal))
			{
				System.IO.File.Delete(HandshakePath);
			}
		}
		catch (System.Exception)
		{
			// A stale handshake is survivable: the client checks the pid is alive.
		}
	}

	public override void _Process(double delta)
	{
		using (Refii.Core.Engine_FramePhases.Measure("gamedebugserver"))
		{
			SampleSimRebase();
			ProcessBody(delta);
			if (ShaderCeiling.IsActive)
			{
				ShaderCeiling.Tick(GetTree().Root);
			}
		}
	}

	private void ProcessBody(double delta)
	{
		ReleaseHeldKey();
		ReleaseHeldMouse();
		GameDebugJoypad.Tick();
		if (_server is null)
		{
			return;
		}

		while (_server.IsConnectionAvailable())
		{
			_clients.Add(_server.TakeConnection());
		}

		if (!_arenaPublished)
		{
			Publish();
		}

		RecordFlight(delta);

		ServeWaits();

		for (int i = _clients.Count - 1; i >= 0; --i)
		{
			StreamPeerTcp client = _clients[i];
			client.Poll();
			if (client.GetStatus() != StreamPeerTcp.Status.Connected)
			{
				_clients.RemoveAt(i);
				continue;
			}

			int available = client.GetAvailableBytes();
			if (available <= 0)
			{
				continue;
			}

			string text = Encoding.UTF8.GetString(client.GetData(available)[1].AsByteArray());
			foreach (string line in text.Split('\n'))
			{
				string request = line.Trim();
				if (request.Length != 0)
				{
					Refii.AndroidRuntime.AndroidDebugIdleQuit.Heard();
					Respond(client, request);
				}
			}
		}
	}


	/// <summary>rex XObject::Type, by value — the names the `objects` op renders.</summary>
	private static readonly string[] KernelObjectTypes =
	{
		"Undefined", "Enumerator", "Event", "File", "IOCompletion", "Module", "Mutant",
		"NotifyListener", "Semaphore", "Session", "Socket", "SymbolicLink", "Thread", "Timer",
	};

	/// <summary>rex cvar FlagType and Lifecycle, by value.</summary>
	private static readonly string[] CvarTypes =
	{
		"bool", "int32", "int64", "uint32", "uint64", "double", "string", "command",
	};

	private static readonly string[] CvarLifecycles = { "init-only", "hot-reload", "requires-restart" };

	/// <summary>The marker a deferred op returns: no reply now, ServeWaits sends it later.</summary>
	private static readonly Dictionary<string, object?> Deferred = new() { ["__deferred"] = true };

	/// <summary>
	/// A reply's numbers as they are: a measure with nothing to divide by yet reads as its named
	/// literal ("Infinity", "NaN") instead of failing the whole reply, as metrics did on the S10e.
	/// </summary>
	private static readonly JsonSerializerOptions ReplyOptions = new()
	{
		NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
	};

	private void Respond(StreamPeerTcp client, string request)
	{
		string reply;
		try
		{
			Dictionary<string, object?> answered = Answer(client, request);
			if (ReferenceEquals(answered, Deferred))
			{
				return;
			}

			reply = Refii.Core.Engine_Json.Serialize(answered, ReplyOptions);
		}
		catch (System.Exception error)
		{
			// A bad request must never take the game down with it — this is a debug channel.
			reply = Refii.Core.Engine_Json.Serialize(new Dictionary<string, object?>
			{
				["ok"] = false,
				["error"] = error.Message,
			});
		}

		client.PutData(Encoding.UTF8.GetBytes(reply + "\n"));
	}

	/// <summary>One request: `{"op": "...", ...}`. Unknown ops list what there is.</summary>
	private Dictionary<string, object?> Answer(StreamPeerTcp client, string request)
	{
		using JsonDocument document = JsonDocument.Parse(request);
		JsonElement root = document.RootElement;
		string op = root.TryGetProperty("op", out JsonElement opValue)
			? opValue.GetString() ?? string.Empty
			: string.Empty;

		switch (op)
		{
			case "ping":
			{
				var pong = new Dictionary<string, object?> { ["guest"] = HostBridgeUp() };
				if (Refii.Host.HostBridge.GetGuestArenaInfo() is { } arena)
				{
					pong["arena"] = arena.Name;
					pong["membase"] = arena.Membase;
				}

				return Ok(pong);
			}

			case "hang":
			{
				// What the bridge's hang watchdog wrote this run. Answerable only once the main
				// thread is back (this op is served by it); the report itself was written while
				// it was not. `summary` is the report's headline lines; the file has the rest.
				var reply = new Dictionary<string, object?>();
				if (Refii.Host.HostBridge.HangReport() is not { } hang)
				{
					reply["bridge"] = "down";
					return Ok(reply);
				}

				reply["reports"] = hang.Reports;
				reply["stalled"] = hang.Stalled;
				reply["lastStallSeconds"] = hang.LastStallSeconds;
				reply["lastPath"] = hang.LastPath;
				if (hang.Reports > 0 && hang.LastPath.Length != 0 && System.IO.File.Exists(hang.LastPath))
				{
					var summary = new List<string>();
					foreach (string line in System.IO.File.ReadLines(hang.LastPath))
					{
						if (line.StartsWith("=== ") || line.StartsWith("stall:") ||
							line.StartsWith("in phase") || line.StartsWith("memory:") ||
							line.StartsWith("time base:") || line.StartsWith("--- main thread"))
						{
							summary.Add(line);
						}
					}

					reply["summary"] = summary;
				}

				return Ok(reply);
			}

			case "state":
				return Ok(State());

			case "rows":
				return Ok(Rows());

			case "frontend":
				return Ok(FrontEnd());

			case "wait":
			{
				// THE WAIT BELONGS TO THE GAME, NOT TO A SHELL LOOP. Driving through the
				// channel kept regressing into scripted sleeps because boot, load and settle
				// take minutes — and those blind sequences pressed into gameplay more than
				// once. The game knows its own state; a caller names the condition and this
				// answers when it holds, so one call replaces a polling loop that could not
				// see what it was waiting for.
				string until = root.TryGetProperty("for", out JsonElement f)
					? f.GetString() ?? string.Empty
					: string.Empty;
				double timeout = root.TryGetProperty("timeout", out JsonElement t)
					? t.GetDouble()
					: 120.0;
				uint wantScreen = root.TryGetProperty("screen", out JsonElement w)
					? (uint)w.GetInt64()
					: 0u;
				// Null means DEFERRED: the reply goes out from ServeWaits when the condition
				// holds. Signalled upward with a marker the responder recognises.
				string logMatch = root.TryGetProperty("match", out JsonElement lm)
					? lm.GetString() ?? string.Empty
					: string.Empty;
				return Wait(client, until, wantScreen, timeout, logMatch) ?? Deferred;
			}

			case "peek":
			{
				// Raw guest words. The microscope: a pointer chain's next hop, without another
				// native rebuild. Loopback-only like everything here; read-only by construction.
				uint addr = root.TryGetProperty("addr", out JsonElement a)
					? System.Convert.ToUInt32(a.GetString() ?? "0", 16)
					: 0u;
				uint count = root.TryGetProperty("words", out JsonElement n)
					? (uint)n.GetInt64()
					: 8u;
				uint[] words = Refii.Host.HostBridge.Peek(addr, count);
				var hex = new List<string>(words.Length);
				foreach (uint word in words)
				{
					hex.Add($"{word:X8}");
				}

				return Ok(new Dictionary<string, object?>
				{
					["addr"] = $"{addr:X8}",
					["words"] = hex,
				});
			}

			case "gdb":
			{
				// A GDB record by hash, named and with its fields, from every database the
				// equipment indexes hold. What a guest-stated hash IS, without a rebuild.
				uint hash = root.TryGetProperty("hash", out JsonElement h)
					? System.Convert.ToUInt32(h.GetString() ?? "0", 16)
					: 0u;
				// `name` searches every database's record names (case-insensitive substring)
				// and answers with the matches, hash and name, instead of one record's fields.
				if (root.TryGetProperty("name", out JsonElement nm) && nm.ValueKind == JsonValueKind.String)
				{
					string wanted = nm.GetString() ?? string.Empty;
					var matches = new List<string>();
					if (Refii.Gdb.Engine_GdbCatalog.All is { } catalog)
					{
						foreach (Refii.Gdb.GdbView view in catalog.Views)
						{
							foreach ((uint recordHash, string recordName) in view.RecordNames)
							{
								if (recordName.Contains(wanted, System.StringComparison.OrdinalIgnoreCase) && matches.Count < 200)
								{
									matches.Add($"{recordHash:X8} {recordName}");
								}
							}
						}
					}

					return Ok(new Dictionary<string, object?> { ["name"] = wanted, ["matches"] = matches });
				}

				var found = new List<object?>();
				var databases = new List<(string, Refii.Gdb.GdbMulti?)>
				{
					("all", Refii.Gdb.Engine_GdbCatalog.All),
					("clothing", Refii.Equipment.Engine_ClothingIndex.Database),
					("items", Refii.Equipment.Engine_ItemIndex.Database),
				};
				foreach ((string label, Refii.Gdb.GdbMulti? multi) in databases)
				{
					if (multi is null || !multi.TryLookup(hash, out Refii.Gdb.GdbCursor cursor))
					{
						continue;
					}

					var fields = new List<string>();
					foreach ((uint fieldHash, Refii.Gdb.GdbFieldType type, uint raw) in multi.InheritedFields(cursor))
					{
						string fieldName = multi.NameOf(fieldHash);
						string value = type is Refii.Gdb.GdbFieldType.Reference or Refii.Gdb.GdbFieldType.ResourceReference
							? $"{raw:X8} '{multi.NameOf(raw)}'"
							: $"{raw:X8}";
						fields.Add($"{(fieldName.Length != 0 ? fieldName : fieldHash.ToString("X8"))} [{type}] = {value}");
					}

					found.Add(new Dictionary<string, object?>
					{
						["database"] = label,
						["name"] = multi.NameOf(hash),
						["fields"] = fields,
					});
				}

				Refii.Equipment.ItemDetail? detail = Refii.Entities.Engine_HiddenClusters.DetailFor(hash);
				return Ok(new Dictionary<string, object?>
				{
					["hash"] = $"{hash:X8}",
					["records"] = found,
					["clustersCovered"] = detail?.ClustersCovered,
					["femaleClustersCovered"] = detail?.FemaleClustersCovered,
					["openClustersCovered"] = detail?.OpenClustersCovered,
					["bodyAreasCovered"] = detail is null ? null : $"{detail.BodyAreasCovered:X8}",
					["wornModel"] = detail?.WornModelPath,
				});
			}

			case "threads":
			{
				// Every kernel-registered thread with a guest backtrace, innermost first —
				// the freeze instrument. Raw addresses, hex: IDA owns naming them.
				bool withBacktrace = !root.TryGetProperty("bt", out JsonElement b) || b.GetBoolean();
				var threads = new List<Dictionary<string, object?>>();
				foreach (Refii.Host.HostBridge.GuestThreadInfo info in
					Refii.Host.HostBridge.GetGuestThreads())
				{
					var entry = new Dictionary<string, object?>
					{
						["tid"] = info.Tid,
						["name"] = info.Name,
						["guest"] = info.IsGuestThread,
						["main"] = info.IsMainThread,
						["running"] = info.Running,
						["priority"] = info.Priority,
						["cpu"] = info.Cpu,
						["r1"] = $"{info.R1:X8}",
						["lr"] = $"{info.Lr:X8}",
					};
					if (withBacktrace && info.IsGuestThread)
					{
						var frames = new List<string>();
						foreach (uint frame in Refii.Host.HostBridge.GetGuestBacktrace(info.Tid))
						{
							frames.Add($"{frame:X8}");
						}

						entry["backtrace"] = frames;
					}

					threads.Add(entry);
				}

				return Ok(new Dictionary<string, object?> { ["threads"] = threads });
			}

			case "objects":
			{
				string match = root.TryGetProperty("match", out JsonElement m)
					? m.GetString() ?? string.Empty
					: string.Empty;
				string wantType = root.TryGetProperty("type", out JsonElement ty)
					? ty.GetString() ?? string.Empty
					: string.Empty;
				var objects = new List<Dictionary<string, object?>>();
				List<Refii.Host.HostBridge.KernelObjectInfo> table =
					Refii.Host.HostBridge.GetKernelObjects(out uint total);
				foreach (Refii.Host.HostBridge.KernelObjectInfo info in table)
				{
					string typeName = info.Type < (uint)KernelObjectTypes.Length
						? KernelObjectTypes[info.Type]
						: info.Type.ToString();
					if (wantType.Length != 0 &&
						!typeName.Equals(wantType, System.StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					if (match.Length != 0 &&
						!info.Name.Contains(match, System.StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					objects.Add(new Dictionary<string, object?>
					{
						["handle"] = $"{info.Handle:X8}",
						["type"] = typeName,
						["name"] = info.Name,
					});
				}

				return Ok(new Dictionary<string, object?>
				{
					["total"] = total,
					["listed"] = objects.Count,
					["objects"] = objects,
				});
			}

			case "perf":
			{
				// LIVE values: run totals and gauges, never per-frame rates — the snapshot
				// rotation lives in the compiled-out renderer. Rates are deltas between reads.
				var counters = new Dictionary<string, object?>();
				foreach ((string name, long value) in Refii.Host.HostBridge.GetPerfCounters())
				{
					counters[name] = value;
				}

				// With FrameMark running each host frame, snapshots mean "last frame".
				var lastFrame = new Dictionary<string, object?>();
				foreach ((string name, long value) in Refii.Host.HostBridge.GetPerfSnapshots())
				{
					lastFrame[name] = value;
				}

				return Ok(new Dictionary<string, object?>
				{
					["counters"] = counters,
					["lastFrame"] = lastFrame,
				});
			}

			case "exports":
			{
				bool unimplemented = root.TryGetProperty("unimplemented", out JsonElement u) &&
					u.GetBoolean();
				string match = root.TryGetProperty("match", out JsonElement m)
					? m.GetString() ?? string.Empty
					: string.Empty;
				int limit = root.TryGetProperty("limit", out JsonElement l) ? l.GetInt32() : 200;
				var exports = new List<Dictionary<string, object?>>();
				uint total = 0;
				for (uint first = 0; exports.Count < limit;)
				{
					List<Refii.Host.HostBridge.KernelExportInfo> page =
						Refii.Host.HostBridge.GetKernelExports(first, unimplemented, out total);
					if (page.Count == 0)
					{
						break;
					}

					foreach (Refii.Host.HostBridge.KernelExportInfo info in page)
					{
						if (exports.Count >= limit)
						{
							break;
						}

						if (match.Length != 0 && !info.Name.Contains(
								match, System.StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}

						exports.Add(new Dictionary<string, object?>
						{
							["name"] = info.Name,
							["ordinal"] = info.Ordinal,
							["implemented"] = info.Implemented,
							["variable"] = info.IsVariable,
						});
					}

					first += (uint)page.Count;
					if (first >= total)
					{
						break;
					}
				}

				return Ok(new Dictionary<string, object?>
				{
					["total"] = total,
					["listed"] = exports.Count,
					["exports"] = exports,
				});
			}

			case "cvars":
			{
				string match = root.TryGetProperty("match", out JsonElement m)
					? m.GetString() ?? string.Empty
					: string.Empty;
				var cvars = new List<Dictionary<string, object?>>();
				var seenNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
				int count = Refii.Host.HostBridge.GetCvarCount();
				for (int i = 0; i < count; ++i)
				{
					if (Refii.Host.HostBridge.GetCvar(i) is not { } info)
					{
						continue;
					}

					seenNames.Add(info.Name);
					if (match.Length != 0 &&
						!info.Name.Contains(match, System.StringComparison.OrdinalIgnoreCase) &&
						!info.Category.Contains(match, System.StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					cvars.Add(new Dictionary<string, object?>
					{
						["name"] = info.Name,
						["value"] = info.Value,
						["default"] = info.Default,
						["category"] = info.Category,
						["type"] = info.Type < (uint)CvarTypes.Length
							? CvarTypes[info.Type]
							: info.Type.ToString(),
						["lifecycle"] = info.Lifecycle < (uint)CvarLifecycles.Length
							? CvarLifecycles[info.Lifecycle]
							: info.Lifecycle.ToString(),
						["modified"] = info.Modified,
						["debugOnly"] = info.DebugOnly,
					});
				}

				foreach ((Refii.Launcher.Core.RuntimeCVarDefinition def, string val, bool modified) in
					Refii.Core.Engine_RefiiConfig.GetGodotCvars())
				{
					if (seenNames.Contains(def.Name))
					{
						continue;
					}

					if (match.Length != 0 &&
						!def.Name.Contains(match, System.StringComparison.OrdinalIgnoreCase) &&
						!def.Category.Contains(match, System.StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					cvars.Add(new Dictionary<string, object?>
					{
						["name"] = def.Name,
						["value"] = val,
						["default"] = def.DefaultValue,
						["category"] = def.Category,
						["type"] = def.Type.ToString().ToLowerInvariant(),
						["lifecycle"] = "hot-reload",
						["modified"] = modified,
						["debugOnly"] = false,
					});
				}

				return Ok(new Dictionary<string, object?>
				{
					["count"] = cvars.Count,
					["cvars"] = cvars,
				});
			}

			case "cvar-set":
			{
				string name = root.TryGetProperty("name", out JsonElement n)
					? n.GetString() ?? string.Empty
					: string.Empty;
				string value = root.TryGetProperty("value", out JsonElement v)
					? v.GetString() ?? string.Empty
					: string.Empty;
				if (name.Length == 0)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = "cvar-set needs a name",
					};
				}

				// Update both registries when a CVar exists on both sides (e.g. fx_weapon_augment_always_active)
				// so Godot config overrides and OnConfigChanged listeners stay in sync with the native runtime.
				bool nativeAccepted = Refii.Host.HostBridge.SetCvar(name, value);
				bool godotAccepted = Refii.Core.Engine_RefiiConfig.TrySetGodotCvar(name, value);
				bool accepted = nativeAccepted || godotAccepted;
				return Ok(new Dictionary<string, object?>
				{
					["name"] = name,
					["accepted"] = accepted,
				});
			}

			case "vfs":
			{
				string path = root.TryGetProperty("path", out JsonElement p)
					? p.GetString() ?? string.Empty
					: string.Empty;
				if (path.Length == 0)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = "vfs needs a path",
					};
				}

				if (Refii.Host.HostBridge.VfsResolve(path) is not { } resolved)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = "no VFS — the guest is not up",
					};
				}

				return Ok(new Dictionary<string, object?>
				{
					["path"] = path,
					["found"] = resolved.Found,
					["rootIsLink"] = resolved.RootIsLink,
					["absolute"] = resolved.AbsolutePath,
					["linkTarget"] = resolved.LinkTarget,
					["size"] = resolved.Size,
					["attributes"] = $"{resolved.Attributes:X}",
				});
			}

			case "rumble":
			{
				// Per guest pad: the joypad holding it, the motors the guest states this frame
				// (XInput 0..65535, left = low-frequency), and what the joypad was last told.
				var pads = new List<object?>();
				for (int pad = 0; pad < 4; ++pad)
				{
					(int joypad, uint left, uint right, bool running, float weak, float strong) =
						GameInput.RumbleReadout(pad);
					pads.Add(new Dictionary<string, object?>
					{
						["pad"] = pad,
						["joypad"] = joypad,
						["left"] = left,
						["right"] = right,
						["running"] = running,
						["weak"] = weak,
						["strong"] = strong,
					});
				}

				return Ok(new Dictionary<string, object?>
				{
					// Both gates: the guest's SetRumbleEnabled switch and the launcher's controller_vibration.
					["guestSwitch"] = Refii.Core.Engine_TitleGame.Active.GuestVibrationEnabled,
					["launcherSwitch"] = GameInput.ControllerVibration,
					["pads"] = pads,
					["handheld"] = HandheldRumble(),
				});
			}

			case "sockets":
			{
				var sockets = new List<Dictionary<string, object?>>();
				foreach ((uint handle, uint port, ulong native) in
					Refii.Host.HostBridge.GetGuestSockets())
				{
					sockets.Add(new Dictionary<string, object?>
					{
						["handle"] = $"{handle:X8}",
						["boundPort"] = port,
						["nativeHandle"] = native,
					});
				}

				return Ok(new Dictionary<string, object?> { ["sockets"] = sockets });
			}

			case "menu":
			{
				// The guest's own menu verbs, not synthesized pad input. A direction is the screen's
				// own directional code (28 up, 29 down, 30 left, 31 right; +4 the release), the codes
				// `ScreenAction` accepts — the options page answers them natively, list screens
				// swallow them (their cursor is Godot's).
				string verb = root.TryGetProperty("verb", out JsonElement v)
					? v.GetString() ?? string.Empty
					: string.Empty;
				bool held = root.TryGetProperty("held", out JsonElement h) && h.GetBoolean();
				uint release = held ? 0u : 4u;
				uint result;
				switch (verb)
				{
					case "up":
						result = Refii.Host.HostBridge.ScreenAction(28u + release);
						break;
					case "down":
						result = Refii.Host.HostBridge.ScreenAction(29u + release);
						break;
					case "left":
						result = Refii.Host.HostBridge.ScreenAction(30u + release);
						break;
					case "right":
						result = Refii.Host.HostBridge.ScreenAction(31u + release);
						break;
					case "select":
						// The screen's own A (code 36): its arm reads the row Godot states.
						result = Refii.Host.HostBridge.ScreenAction(36u);
						break;
					case "action":
					{
						uint code = root.TryGetProperty("code", out JsonElement c)
							? (uint)c.GetInt64()
							: 36u;
						result = Refii.Host.HostBridge.ScreenAction(code);
						break;
					}
					case "event":
					{
						string name = root.TryGetProperty("name", out JsonElement n)
							? n.GetString() ?? string.Empty
							: string.Empty;
						int type = root.TryGetProperty("type", out JsonElement t2)
							? t2.GetInt32()
							: 0;
						uint id = root.TryGetProperty("id", out JsonElement i2)
							? (uint)i2.GetInt64()
							: 0u;

						// The title presses the row on its own list when its lists are Godot's (Fable 2:
						// the A path, its levers); otherwise the guest's MenuEvent.
						if (Refii.Core.Engine_TitleGame.Active.DebugMenuEvent(name, type, id) is { } pressed)
						{
							pressed["verb"] = verb;
							return Ok(pressed);
						}

						result = Refii.Host.HostBridge.MenuEvent(name, type, id);
						break;
					}
					default:
						return new Dictionary<string, object?>
						{
							["ok"] = false,
							["error"] = "verb must be up/down/left/right/select/action/event",
						};
				}

				return Ok(new Dictionary<string, object?> { ["verb"] = verb, ["result"] = result });
			}

			case "action":
			{
				string name = root.TryGetProperty("name", out JsonElement n)
					? n.GetString() ?? string.Empty
					: string.Empty;
				if (name.Length == 0)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = "action needs a name",
					};
				}

				// The mods' fx/recipes.json re-read and every dressed effect re-dressed, so a look
				// saved from the FX Lab shows in the running game without a restart.
				if (name == "fx-recipes-reload")
				{
					Refii.Fx.Engine_FxRecipes.Reload();
					return Ok(new Dictionary<string, object?> { ["recipes"] = Refii.Fx.Engine_FxRecipes.All.Count });
				}

				// A mod's tunable, live: "mod-setting <key> <value>" (Engine_ModSettings pushes the
				// files' values; this holds until the next push).
				if (name.StartsWith("mod-setting ", System.StringComparison.Ordinal))
				{
					string[] parts = name.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
					if (parts.Length != 3 || !float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
						System.Globalization.CultureInfo.InvariantCulture, out float setting))
					{
						return new Dictionary<string, object?>
						{
							["ok"] = false, ["error"] = "mod-setting needs <key> <number>",
						};
					}

					Refii.Mods.ModSettings.Set(parts[1], setting);
					return Ok(new Dictionary<string, object?> { ["setting"] = parts[1], ["value"] = setting });
				}

				// What a mod's native code has published, live: "mod-value <key> [<key> ...]" —
				// what the HUD, the abilities page and the recipes are reading right now.
				if (name.StartsWith("mod-value ", System.StringComparison.Ordinal))
				{
					var values = new Dictionary<string, object?>();
					foreach (string key in name.Split(' ', System.StringSplitOptions.RemoveEmptyEntries).Skip(1))
					{
						values[key] = Refii.Host.HostBridge.TryGetModValue(key, out float value) ? value : null;
					}

					return Ok(new Dictionary<string, object?> { ["values"] = values });
				}

				// A player's store, live: "player-value <player> <key> [<key> ...]" — what this
				// machine reads for that player (the owner's values), saved or session alike.
				if (name.StartsWith("player-value ", System.StringComparison.Ordinal))
				{
					string[] words = name.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
					var values = new Dictionary<string, object?>();
					if (words.Length >= 2 && int.TryParse(words[1], out int player))
					{
						foreach (string key in words.Skip(2))
						{
							values[key] = Refii.Host.HostBridge.GetPlayerValue(player, key);
						}
					}

					return Ok(new Dictionary<string, object?> { ["values"] = values });
				}

				return Ok(new Dictionary<string, object?>
				{
					["queued"] = Refii.Host.HostBridge.PostFrontEndAction(name),
				});
			}

			case "call":
			{
				// debug-call on the guest's tick thread; the caller polls call-result until
				// seq passes the one returned here.
				string addr = root.TryGetProperty("addr", out JsonElement a)
					? a.GetString() ?? string.Empty
					: string.Empty;
				var pieces = new List<string> { addr };
				if (root.TryGetProperty("args", out JsonElement args))
				{
					foreach (JsonElement arg in args.EnumerateArray())
					{
						pieces.Add(arg.ValueKind == JsonValueKind.String
							? arg.GetString() ?? "0"
							: ((ulong)arg.GetInt64()).ToString("X"));
					}
				}

				ulong before = Refii.Host.HostBridge.DebugCallResult()?.Seq ?? 0;
				uint queued = Refii.Host.HostBridge.PostFrontEndAction(
					"debug-call:" + string.Join(":", pieces));
				return Ok(new Dictionary<string, object?>
				{
					["queued"] = queued, ["seqBefore"] = before,
				});
			}

			case "lua":
			{
				string chunk = root.TryGetProperty("chunk", out JsonElement c)
					? c.GetString() ?? string.Empty
					: string.Empty;
				if (chunk.Length == 0)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = "lua needs a chunk",
					};
				}

				// "@game <chunk>" runs in the GAME VM (game scripts, mods' Lua) instead of the GUI's;
				// "@frontend <chunk>" in the mods' front-end Lua, as RefiiHost.frontend_lua posts it.
				const string gamePrefix = "@game ";
				const string frontendPrefix = "@frontend ";
				if (chunk.StartsWith(frontendPrefix, System.StringComparison.Ordinal))
				{
					Refii.Host.HostBridge.PostModCommand(Refii.Mods.ModLua.FrontendCommand + chunk.Substring(frontendPrefix.Length));
					return Ok(new Dictionary<string, object?> { ["posted"] = "frontend_lua" });
				}

				string action = chunk.StartsWith(gamePrefix, System.StringComparison.Ordinal)
					? "debug-gamelua:" + chunk.Substring(gamePrefix.Length)
					: "debug-lua:" + chunk;
				ulong before = Refii.Host.HostBridge.DebugCallResult()?.Seq ?? 0;
				uint queued = Refii.Host.HostBridge.PostFrontEndAction(action);
				return Ok(new Dictionary<string, object?>
				{
					["queued"] = queued, ["seqBefore"] = before,
				});
			}

			case "timeofday":
			{
				// {"op":"timeofday","hours":0.83}: the mod API's set_time_of_day command (the guest's own
				// clock setter; runs with no mod mounted, needs the world up). No "hours": asks the clock, and
				// answers the hours the last ask returned (null until one has; send it twice).
				if (root.TryGetProperty("hours", out JsonElement th))
				{
					string? command = TimeOfDayCommand(th.ValueKind == JsonValueKind.Number ? th.GetDouble() : double.NaN);
					if (command is null)
					{
						return new Dictionary<string, object?>
						{
							["ok"] = false, ["error"] = "timeofday needs finite hours",
						};
					}

					Refii.Host.HostBridge.PostModCommand(command);
					return Ok(new Dictionary<string, object?> { ["posted"] = command });
				}

				Refii.Mods.ModHostQueries.Ask("time_of_day", string.Empty, fields =>
				{
					if (fields.TryGetValue("hours", out object? h) && h is System.IConvertible c)
					{
						_lastTimeOfDay = c.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
					}
				});
				return Ok(new Dictionary<string, object?> { ["hours"] = _lastTimeOfDay });
			}

			case "call-result":
			{
				if (Refii.Host.HostBridge.DebugCallResult() is not { } result)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = "no bridge",
					};
				}

				return Ok(new Dictionary<string, object?>
				{
					["seq"] = result.Seq,
					["addr"] = $"{result.Addr:X8}",
					["r3"] = $"{result.R3:X}",
					["note"] = result.Note,
				});
			}

			case "mod-errors":
			{
				// The mods' Lua errors numbered after `since` (0: every one kept), oldest first;
				// `seq` is the newest number, the `since` of the next ask.
				long since = root.TryGetProperty("since", out JsonElement s) && s.TryGetInt64(out long after)
					? after
					: 0;
				var errors = new List<Dictionary<string, object?>>();
				foreach (Refii.Mods.ModErrors.Entry entry in Refii.Mods.ModErrors.Recent(since))
				{
					errors.Add(new Dictionary<string, object?>
					{
						["seq"] = entry.Seq,
						["time"] = entry.Time.ToString("O"),
						["mod"] = entry.Mod,
						["text"] = entry.Text,
					});
				}

				return Ok(new Dictionary<string, object?>
				{
					["seq"] = Refii.Mods.ModErrors.Sequence,
					["errors"] = errors,
				});
			}

			case "mods-remount":
			{
				// The mods folded in again for this session (Refii.Mods.ModRemount); `lua: true` also
				// runs the composed init chunk in the game VM. Answered when the remount finishes —
				// the mount's passes run on its worker — with what refreshed and what needs a relaunch.
				bool lua = root.TryGetProperty("lua", out JsonElement l) && l.ValueKind == JsonValueKind.True;
				if (!Refii.Mods.ModRemount.Start(lua, report =>
					{
						Dictionary<string, object?> body = report.ToJson();
						if (report.Ok)
						{
							body = Ok(body);
						}

						if (client.GetStatus() == StreamPeerTcp.Status.Connected)
						{
							client.PutData(Encoding.UTF8.GetBytes(Refii.Core.Engine_Json.Serialize(body) + "\n"));
						}
					}, out string refused))
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = refused,
					};
				}

				return Deferred;
			}

			case "poke":
			{
				uint addr = root.TryGetProperty("addr", out JsonElement a)
					? System.Convert.ToUInt32(a.GetString() ?? "0", 16)
					: 0u;
				uint value = root.TryGetProperty("value", out JsonElement v)
					? System.Convert.ToUInt32(v.GetString() ?? "0", 16)
					: 0u;
				bool wrote = Refii.Host.HostBridge.Poke(addr, value);
				return Ok(new Dictionary<string, object?>
				{
					["addr"] = $"{addr:X8}",
					["wrote"] = wrote,
					["refused"] = wrote ? null : "page is untracked or not writable",
				});
			}

			case "heap":
			{
				uint addr = root.TryGetProperty("addr", out JsonElement a)
					? System.Convert.ToUInt32(a.GetString() ?? "0", 16)
					: 0u;
				if (Refii.Host.HostBridge.HeapLookup(addr) is not { } info)
				{
					return Ok(new Dictionary<string, object?>
					{
						["addr"] = $"{addr:X8}", ["heap"] = false,
					});
				}

				return Ok(new Dictionary<string, object?>
				{
					["addr"] = $"{addr:X8}",
					["heap"] = true,
					["heapBase"] = $"{info.HeapBase:X8}",
					["heapSize"] = info.HeapSize,
					["pageSize"] = info.PageSize,
					["protect"] = info.Protect,
					["writable"] = (info.Protect & 2u) != 0,
				});
			}

			case "heapcensus":
			{
				// The MANAGED heap (`heap` is the guest's): the GC's split by generation and the
				// known caches counted. `{"op":"heapcensus","collect":true}` first runs a blocking
				// compacting gen2 (large objects compacted too, a long pause) and adds the live split.
				bool collect = root.TryGetProperty("collect", out JsonElement collectValue) &&
					collectValue.ValueKind == JsonValueKind.True;
				return Ok(Refii.Core.Engine_HeapCensus.Describe(collect));
			}

			case "probe":
			{
				string what = root.TryGetProperty("do", out JsonElement d)
					? d.GetString() ?? string.Empty
					: string.Empty;
				if (what == "set")
				{
					uint addr = root.TryGetProperty("addr", out JsonElement a)
						? System.Convert.ToUInt32(a.GetString() ?? "0", 16)
						: 0u;
					int slot = Refii.Host.HostBridge.ProbeSet(addr);
					return Ok(new Dictionary<string, object?>
					{
						["slot"] = slot,
						["error"] = slot switch
						{
							-1 => "all 16 slots armed — clear one",
							-2 => "not a recompiled function start",
							-3 => "guest not up",
							_ => null,
						},
					});
				}

				if (what == "snapshot")
				{
					// Arm (`arm`: true) or read the slot's constant snapshot: the guest device's
					// VS and PS shadows as they stood when the probed function last returned.
					int snapSlot = root.TryGetProperty("slot", out JsonElement ss) ? ss.GetInt32() : -1;
					if (root.TryGetProperty("arm", out JsonElement armValue) && armValue.ValueKind == JsonValueKind.True)
					{
						return Ok(new Dictionary<string, object?> { ["armed"] = Refii.Host.HostBridge.ProbeSnapshotArm(snapSlot) });
					}
					float[]? snap = Refii.Host.HostBridge.ProbeSnapshotRead(snapSlot);
					return Ok(new Dictionary<string, object?>
					{
						["ready"] = snap is not null,
						["vs"] = snap?.Take(1024).Select(v => (double)v).ToArray(),
						["ps"] = snap?.Skip(1024).Select(v => (double)v).ToArray(),
					});
				}

				if (what == "clear")
				{
					int slot = root.TryGetProperty("slot", out JsonElement s)
						? s.GetInt32()
						: -1;
					return Ok(new Dictionary<string, object?>
					{
						["cleared"] = Refii.Host.HostBridge.ProbeClear(slot),
					});
				}

				// read / list
				var probes = new List<Dictionary<string, object?>>();
				for (int slot = 0; slot < 16; ++slot)
				{
					if (Refii.Host.HostBridge.ProbeRead(slot) is { } state && state.Addr != 0)
					{
						var argsHex = new List<string>();
						foreach (ulong arg in state.LastArgs)
						{
							argsHex.Add($"{arg:X}");
						}

						probes.Add(new Dictionary<string, object?>
						{
							["slot"] = slot,
							["addr"] = $"{state.Addr:X8}",
							["hits"] = state.Hits,
							["lastLr"] = $"{state.LastLr:X8}",
							["lastArgs"] = argsHex,
						});
					}
				}

				return Ok(new Dictionary<string, object?> { ["probes"] = probes });
			}

			case "guestlog":
			{
				// The EMBEDDED guest's own segmented logs beside the executable — the ones the
				// usual location does not hold. `which` = guest (refii_*.log*) or launcher.
				string which = root.TryGetProperty("which", out JsonElement w)
					? w.GetString() ?? "guest"
					: "guest";
				int lines = root.TryGetProperty("lines", out JsonElement n) ? n.GetInt32() : 40;
				string match = root.TryGetProperty("match", out JsonElement m)
					? m.GetString() ?? string.Empty
					: string.Empty;
				// The launcher's log stays in the shared logs/; the guest's are the title's own (logs/<Title>).
				string logs = which == "launcher"
					? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(OS.GetExecutablePath())!, "logs")
					: Refii.Core.Engine_Title.Active.LogsDirectory;
				string[] files;
				try
				{
					files = which == "launcher"
						? System.IO.Directory.GetFiles(logs, "refii-launcher.log*")
						: System.IO.Directory.GetFiles(logs, Refii.Core.Engine_Title.Active.GuestLogPattern);
				}
				catch (System.Exception error)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = error.Message, ["dir"] = logs,
					};
				}

				// Newest FILE first, then its segments oldest-to-newest within the tail.
				System.Array.Sort(files, (x, y) =>
					System.IO.File.GetLastWriteTimeUtc(y)
						.CompareTo(System.IO.File.GetLastWriteTimeUtc(x)));
				var kept = new List<string>();
				foreach (string file in files)
				{
					if (kept.Count >= lines)
					{
						break;
					}

					kept.AddRange(TailOf(file, lines - kept.Count, match));
				}

				return Ok(new Dictionary<string, object?> { ["dir"] = logs, ["lines"] = kept });
			}

			case "tracy":
				return AnswerTracy(root);

			case "uirecord":
			{
				// Every UI statement the host receives, to a file (Engine_UiStatementRecord):
				// action start + path, stop, or status.
				string action = Text(root, "action", "status");
				if (action == "start")
				{
					string path = Text(root, "path", string.Empty);
					if (path.Length == 0)
					{
						return Refused("uirecord start needs a path");
					}

					Refii.Ui.Engine_UiStatementRecord.Start(path);
				}
				else if (action == "stop")
				{
					Refii.Ui.Engine_UiStatementRecord.Stop();
				}

				return Ok(new Dictionary<string, object?>
				{
					["recording"] = Refii.Ui.Engine_UiStatementRecord.Recording,
					["path"] = Refii.Ui.Engine_UiStatementRecord.Path,
					["lines"] = Refii.Ui.Engine_UiStatementRecord.Lines,
				});
			}

			case "mark":
			{
				string text = root.TryGetProperty("text", out JsonElement t)
					? t.GetString() ?? string.Empty
					: string.Empty;
				GD.Print($"[mark] {text}");
				Refii.Host.HostBridge.TraceMessage($"mark: {text}");
				Refii.Ui.Engine_UiStatementRecord.Note('M', text);
				return Ok(new Dictionary<string, object?> { ["marked"] = text });
			}

			case "lights":
			{
				// THE NEAREST PLACED LIGHTS, as Godot holds them: what a fixture states and
				// whether it is visible, so "the lamp lights nothing" is answered by its numbers
				// (range, energy, fade) rather than by a screenshot.
				int take = root.TryGetProperty("count", out JsonElement lc) && lc.ValueKind == JsonValueKind.Number
					? System.Math.Clamp(lc.GetInt32(), 1, 64) : 12;
				var snapshot = new List<Light3D>();
				Refii.Lighting.Engine_LevelLights.SnapshotLights(snapshot);
				Vector3 eye = GetViewport()?.GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
				var rows = new List<(float Distance, Light3D Light)>();
				foreach (Light3D light in snapshot)
				{
					if (light.IsInsideTree())
					{
						rows.Add((eye.DistanceTo(light.GlobalPosition), light));
					}
				}

				rows.Sort((a, b) => a.Distance.CompareTo(b.Distance));
				var described = new List<string>();
				foreach ((float distance, Light3D light) in rows.GetRange(0, System.Math.Min(take, rows.Count)))
				{
					float range = light is OmniLight3D omni ? omni.OmniRange : light is SpotLight3D spot ? spot.SpotRange : 0f;
					float softCore = Refii.Lighting.Engine_LevelLights.VolumetricSoftCoreOf(light);
					Vector3 p = light.GlobalPosition;
					described.Add(
						$"{light.Name} d={distance:0.0} at=({p.X:0.0},{p.Y:0.0},{p.Z:0.0}) range={range:0.0} " +
						$"energy={light.LightEnergy:0.00} colour=({light.LightColor.R:0.00},{light.LightColor.G:0.00},{light.LightColor.B:0.00}) " +
						$"fade={(light.DistanceFadeEnabled ? $"{light.DistanceFadeBegin:0}+{light.DistanceFadeLength:0}" : "off")} " +
						$"visible={light.Visible} shadow={light.ShadowEnabled} cull=0x{light.LightCullMask:X8} " +
						$"host={(light.HasMeta(Refii.Lighting.Engine_LightZones.HostRecordMeta) ? $"0x{(uint)light.GetMeta(Refii.Lighting.Engine_LightZones.HostRecordMeta).AsInt64():X8}" : "-")}" +
						(softCore > 0f ? $" softCore={softCore:0.1}" : string.Empty));
				}

				return Ok(new Dictionary<string, object?>
				{
					["total"] = snapshot.Count,
					["visible"] = snapshot.Count(l => l.Visible),
					["fading"] = snapshot.Count(l => l.DistanceFadeEnabled),
					["eye"] = $"({eye.X:0.0},{eye.Y:0.0},{eye.Z:0.0})",
					["lights"] = described,
					["fxLights"] = Refii.Game.GameFx.Current?.DescribeLights(eye, take) ?? new List<string>(),
				});
			}

			case "fx":
			{
				// The drawn live effects nearest the eye: which effect each guest handle resolved to,
				// where we placed it, the intensity the guest states for it, and what our simulation holds.
				int take = root.TryGetProperty("count", out JsonElement fc) && fc.ValueKind == JsonValueKind.Number
					? System.Math.Clamp(fc.GetInt32(), 1, 128) : 16;
				Vector3 eye = GetViewport()?.GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
				GameFx? feed = GameFx.Current;
				return Ok(new Dictionary<string, object?>
				{
					["drawn"] = feed?.DrawnCount ?? 0,
					["recipesActive"] = Refii.Fx.Engine_FxRecipes.ActiveCount,
					["recipesLoaded"] = Refii.Fx.Engine_FxRecipes.All.Count,

					// The walk's saturation. The guest appends new nodes at the end of its vector, so a full
					// buffer means the NEWEST effects were cut.
					["nodesRead"] = feed?.NodesRead ?? 0,
					["nodesSaturated"] = Refii.Host.HostBridge.ActiveFxSaturated,

					// New nodes the per-walk limit (android_fx_spawns_per_frame) holds back, and has so far.
					["spawnsWaiting"] = feed?.SpawnsWaiting ?? 0,
					["spawnsDeferred"] = feed?.SpawnsDeferred ?? 0,

					// The hook against the walk: both key on the particle instance pointer, so the rows line
					// up. `statedCount` of 0 means the hook is not in this build.
					["statedCount"] = Refii.Host.HostBridge.StatedFxCount(),
					["placedFromStated"] = GameFx.StatedPlacements,
					["placedFromWalk"] = GameFx.WalkedPlacements,
					["visualsBuilt"] = Refii.Fx.Engine_FxSystem.VisualsBuilt,
					["visualsChainedAway"] = Refii.Fx.Engine_FxSystem.ChainedNoVisuals,
					["visualsChainedButDrawable"] = Refii.Fx.Engine_FxSystem.ChainedButDrawable,
					["visualsBuiltEmpty"] = Refii.Fx.Engine_FxSystem.BuiltButEmpty,
					["emitZeroSize"] = Refii.Fx.Engine_FxSystem.SkipZeroSize,
					["emitZeroAlpha"] = Refii.Fx.Engine_FxSystem.SkipZeroAlpha,
					["emitEarlyReject"] = Refii.Fx.Engine_FxSystem.SkipEarly,
					["emitEmitterRejects"] = Refii.Fx.Engine_FxSystem.SkipEmitters,
					["emitBillboards"] = Refii.Fx.Engine_FxSystem.EmittedBillboards,
					["batchMaterialHits"] = Refii.Fx.Engine_FxSystem.BatchMaterialHits,
					["batchMaterialMisses"] = Refii.Fx.Engine_FxSystem.BatchMaterialMisses,
					["batchMissedNames"] = string.Join(" ", Refii.Fx.Engine_FxSystem.MissedNames),
					["stated"] = StatedFxRows(),
					["frozenSinceLastRead"] = Refii.Fx.FableFx.FrozenThisFrame,
					["eye"] = $"({eye.X:0.0},{eye.Y:0.0},{eye.Z:0.0})",
					["effects"] = feed?.Describe(eye, take) ?? new List<string>(),
					// `doubles`: a level-spawned node of the same effect within 0.5 m of a live one.
					["levelDoubles"] = feed is null ? null : DoublesReply(feed.DescribeLevelDoubles(0.5f, take)),
				});
			}

			case "fxlayer":
			{
				// The additive layer: whether the viewport is up and resolvable, whether anything drew on
				// it this frame, its pixel statistics, and `gain`, the scalar it joins the scene with
				// (settable live). `dump` writes the layer as a PNG under the scratch directory (never the
				// tree).
				Refii.Fx.Engine_FxAdditiveLayer? layer = Refii.Fx.Engine_FxAdditiveLayer.Current;
				if (root.TryGetProperty("gain", out JsonElement gainElement) && gainElement.ValueKind == JsonValueKind.Number)
				{
					Refii.Fx.Engine_FxAdditiveLayer.Gain = System.Math.Clamp((float)gainElement.GetDouble(), 0f, 64f);
				}

				var reply = new Dictionary<string, object?>
				{
					["present"] = Refii.Fx.Engine_FxAdditiveLayer.Present,
					["usable"] = layer?.Usable ?? false,
					["activeThisFrame"] = Refii.Fx.Engine_FxAdditiveLayer.IsViewActive(0),
					["size"] = layer is null ? "none" : $"{layer.Size.X}x{layer.Size.Y}",
					["gain"] = Refii.Fx.Engine_FxAdditiveLayer.Gain,
					["postfxEnabled"] = Refii.Lighting.Engine_PostFxParams.Enabled,
					["postFxRoute"] = Refii.Lighting.Engine_PostFxRoute.Current.ToString(),
					["fxLegacyLayers"] = Refii.Lighting.Engine_PostFxRoute.LegacyLayers,
				};

				if (layer is not null && layer.Capture() is { } image && !image.IsEmpty())
				{
					int w = image.GetWidth(), h = image.GetHeight();
					double sum = 0; float peak = 0f; int lit = 0;
					for (int y = 0; y < h; y++)
					{
						for (int x = 0; x < w; x++)
						{
							Color c = image.GetPixel(x, y);
							float m = System.Math.Max(c.R, System.Math.Max(c.G, c.B));
							sum += m;
							if (m > peak) peak = m;
							if (m > 0.004f) lit++;
						}
					}

					reply["format"] = image.GetFormat().ToString();
					reply["meanMax"] = sum / System.Math.Max(1, w * h);
					reply["peak"] = peak;
					reply["litPixels"] = lit;
					if (root.TryGetProperty("dump", out JsonElement dumpElement) && dumpElement.ValueKind == JsonValueKind.True)
					{
						string dir = System.IO.Path.Combine(
							System.Environment.GetEnvironmentVariable("HOME") ?? ".", ".cache", "refii-scratch", "fxlayer");
						System.IO.Directory.CreateDirectory(dir);
						string path = System.IO.Path.Combine(dir, $"layer_{Time.GetTicksMsec()}.png");
						Image png = (Image)image.Duplicate();
						png.Convert(Image.Format.Rgba8);
						reply["dumped"] = png.SavePng(path) == Error.Ok ? path : "failed";
					}
				}

				return Ok(reply);
			}

			case "render":
			{
				// Validate before changing any render lever; zero requests full mesh detail.
				float? meshLodThreshold = Refii.Models.Engine_MeshLodPolicy.ReadProfilerThreshold(root);
				bool? fxPackedWrites = Refii.Fx.Engine_FxSurfacePacker.ReadRequest(root);
				bool? fxBulkGeometry = Refii.Fx.Engine_FxGeometryWork.ReadRequest(root);
				bool? fxFastPaths = Refii.Fx.Engine_FxFastPaths.ReadRequest(root);
				(bool? stableLightCounts, bool? stableLightSentinel) = Refii.Lighting.Engine_StableLightCounts.ReadRequest(root);
				bool? canvasLean = Refii.Ui.Engine_CanvasLean.ReadRequest(root);
				bool? postFxEnabled = Refii.Core.Engine_PresentationPolicy.ReadPostFxRequest(root);
				bool? fxLegacyLayers = Refii.Lighting.Engine_PostFxRoute.ReadRequest(root);
				bool? fxNoDepthReads = Refii.Fx.Engine_FxDepthReads.ReadRequest(root);
				bool? skyRadianceLean = Refii.Level.Engine_SkyCosts.ReadRequest(root, Refii.Level.Engine_SkyCosts.RadianceLeanRequestKey);
				Refii.Core.Engine_ShaderCeiling.Request? shaderCeiling = Refii.Core.Engine_ShaderCeiling.Parse(root);
				ShaderCostDiagnostic.ApplyRequest(root, (name, value) => RenderingServer.GlobalShaderParameterSet(name, value));
				ApplyTreeDistanceDiagnostic(root);
				// A category drawn flat, flat with its cutouts, or hidden, to split its cost into
				// geometry and pixels: `"shaderCeiling":{"props":"flat","trees":"flatcut"}`.
				if (shaderCeiling is { } ceiling) ApplyShaderCeiling(ceiling);
				if (fxPackedWrites is { } packedWrites) Refii.Fx.Engine_FxSurfacePacker.Enabled = packedWrites;
				if (fxBulkGeometry is { } bulkGeometry) Refii.Fx.Engine_FxGeometryWork.Enabled = bulkGeometry;
				if (fxFastPaths is { } fastPaths) Refii.Fx.Engine_FxFastPaths.Enabled = fastPaths;
				// Re-pairs every instance: each material on screen compiles its new combination once.
				if (stableLightSentinel is { } sentinel) Refii.Lighting.Engine_StableLightCounts.Sentinel = sentinel;
				if (stableLightCounts is { } stableCounts) Refii.Lighting.Engine_StableLightCounts.Enabled = stableCounts;
				if (canvasLean is { } lean) Refii.Ui.Engine_CanvasLean.Enabled = lean;
				if (postFxEnabled is { } postFx) Refii.Lighting.Engine_PostFxParams.Enabled = postFx;
				if (fxLegacyLayers is { } legacyLayers) Refii.Lighting.Engine_PostFxRoute.LegacyLayers = legacyLayers;
				if (fxNoDepthReads is { } noDepthReads) Refii.Fx.Engine_FxDepthReads.Enabled = noDepthReads;
				if (skyRadianceLean is { } radianceLean) Refii.Level.Engine_SkyCosts.RadianceLean = radianceLean;
				ShaderZeroWork.ApplyRequest(root, (name, value) => RenderingServer.GlobalShaderParameterSet(name, value));
				if (meshLodThreshold is { } requestedLod)
					GetViewport().MeshLodThreshold = requestedLod;
				// LIVE LEVERS for bisecting a frame: each is optional; the reply is the state.
				if (root.TryGetProperty("shadowBudget", out JsonElement sb) && sb.ValueKind == JsonValueKind.Number)
				{
					Refii.Lighting.Engine_LightShadowBudget.Budget = sb.GetInt32();
				}

				if (root.TryGetProperty("lightBudget", out JsonElement lb) && lb.ValueKind == JsonValueKind.Number)
				{
					Refii.Lighting.Engine_LightShadowBudget.LightBudget = lb.GetInt32();
				}

				// The shader's own views of one lighting term: 1 bake, 2 direct, 3 raw albedo, 4 the
				// normal-map darken, 5 bake mode, 8 view normal, 9 summed light on white, 10 facing, 11 combined AO; 0 is off.
				if (root.TryGetProperty("lightDebug", out JsonElement ld) && ld.ValueKind == JsonValueKind.Number)
				{
					_lightDebug = System.Math.Clamp(ld.GetInt32(), 0, 11);
					RenderingServer.GlobalShaderParameterSet("fable_light_debug", _lightDebug);
				}

				// GI toggle for A/B cost comparison.
				if (root.TryGetProperty("gi", out JsonElement giLever) &&
					(giLever.ValueKind == JsonValueKind.True || giLever.ValueKind == JsonValueKind.False))
				{
					Refii.Lighting.Engine_GiRefresh.ForcedOff = giLever.ValueKind == JsonValueKind.False;
				}

				if (root.TryGetProperty("entityBatches", out JsonElement entityBatchLever) &&
					(entityBatchLever.ValueKind == JsonValueKind.True || entityBatchLever.ValueKind == JsonValueKind.False))
				{
					Refii.Entities.Engine_EntityBatches.Enabled = entityBatchLever.ValueKind == JsonValueKind.True;
				}

				// The entity walk's skip index (`entity_skip_index`): unchanged entities answered from the
				// last walk's skip verdicts. The same entities are drawn either way; the next walk starts
				// the index afresh.
				if (root.TryGetProperty("entitySkipIndex", out JsonElement skipIndexLever) &&
					(skipIndexLever.ValueKind == JsonValueKind.True || skipIndexLever.ValueKind == JsonValueKind.False))
				{
					GameEntities.SkipIndexEnabled = skipIndexLever.ValueKind == JsonValueKind.True;
				}

				if (root.TryGetProperty("fxVisible", out JsonElement fxVisibleLever) &&
					(fxVisibleLever.ValueKind == JsonValueKind.True || fxVisibleLever.ValueKind == JsonValueKind.False))
				{
					Refii.Fx.Engine_FxDriver.SetAllVisible(fxVisibleLever.ValueKind == JsonValueKind.True);
				}

				// Takes effect on the reconcile cadence, which re-presents every dressed body.
				if (root.TryGetProperty("bodyVariants", out JsonElement bodyVariantLever) &&
					(bodyVariantLever.ValueKind == JsonValueKind.True || bodyVariantLever.ValueKind == JsonValueKind.False))
				{
					Refii.Entities.Engine_BodyVariants.Enabled = bodyVariantLever.ValueKind == JsonValueKind.True;
				}

				// The guest's reflection contract (Engine_ReflectionLayer): false makes the mirrors see
				// everything but the water again, cascades included, as before. Caster masks never
				// change; only what the mirror cameras see.
				if (root.TryGetProperty("reflectContract", out JsonElement reflectLever) &&
					(reflectLever.ValueKind == JsonValueKind.True || reflectLever.ValueKind == JsonValueKind.False))
				{
					Refii.Level.Engine_ReflectionLayer.FollowGuest = reflectLever.ValueKind == JsonValueKind.True;
					foreach (Node found in GetTree().Root.FindChildren("*", "Engine_WaterReflection", true, false))
					{
						(found as Refii.Level.Engine_WaterReflection)?.ApplyCullMask();
					}
				}

				if (root.TryGetProperty("propShadow", out JsonElement propShadowLever) &&
					(propShadowLever.ValueKind == JsonValueKind.True || propShadowLever.ValueKind == JsonValueKind.False))
				{
					Refii.Level.Engine_PropShadow.Enabled = propShadowLever.ValueKind == JsonValueKind.True;
				}

				// Takes effect on the entity feed's reconcile cadence, not at once.
				if (root.TryGetProperty("shadowProxy", out JsonElement proxyLever) &&
					(proxyLever.ValueKind == JsonValueKind.True || proxyLever.ValueKind == JsonValueKind.False))
				{
					Refii.Entities.Engine_ShadowProxy.Enabled = proxyLever.ValueKind == JsonValueKind.True;
				}

				if (root.TryGetProperty("fillFloor", out JsonElement fillFloorLever) && fillFloorLever.ValueKind == JsonValueKind.Number)
				{
					Refii.Lighting.Engine_LightingFrame.ModernFloorShare = System.Math.Clamp(fillFloorLever.GetSingle(), 0f, 1f);
				}

				if (root.TryGetProperty("giHalf", out JsonElement giHalfLever) &&
					(giHalfLever.ValueKind == JsonValueKind.True || giHalfLever.ValueKind == JsonValueKind.False))
				{
					Refii.Lighting.Engine_LevelLighting.GiHalfResolution = giHalfLever.ValueKind == JsonValueKind.True;
				}

				// Pins the exposure gain so a term can be compared across shots; null hands it back.
				if (root.TryGetProperty("exposureLock", out JsonElement el))
				{
					if (el.ValueKind == JsonValueKind.Number)
					{
						Refii.Lighting.Engine_PostFxParams.ExposureLockValue = el.GetSingle();
						Refii.Lighting.Engine_PostFxParams.ExposureLock = true;
					}
					else if (el.ValueKind == JsonValueKind.Null)
					{
						Refii.Lighting.Engine_PostFxParams.ExposureLock = false;
					}
				}

				// The placed lights' shadow filter, written to every live fixture at once. A null
				// bias hands it back to the per-type default.
				bool retune = false;
				if (root.TryGetProperty("placedShadowBlur", out JsonElement psb) && psb.ValueKind == JsonValueKind.Number)
				{
					Refii.Lighting.Engine_LevelLights.PlacedShadowBlur = psb.GetSingle();
					retune = true;
				}

				if (root.TryGetProperty("placedShadowNormalBias", out JsonElement psn) && psn.ValueKind == JsonValueKind.Number)
				{
					Refii.Lighting.Engine_LevelLights.PlacedShadowNormalBias = psn.GetSingle();
					retune = true;
				}

				if (root.TryGetProperty("placedShadowBias", out JsonElement psd))
				{
					if (psd.ValueKind == JsonValueKind.Number)
					{
						Refii.Lighting.Engine_LevelLights.PlacedShadowBias = psd.GetSingle();
						retune = true;
					}
					else if (psd.ValueKind == JsonValueKind.Null)
					{
						Refii.Lighting.Engine_LevelLights.PlacedShadowBias = float.NaN;
						retune = true;
					}
				}

				if (retune)
				{
					Refii.Lighting.Engine_LevelLights.RetuneShadows();
				}

				// Project-wide, not per light: 0 hard .. 5 ultra. The project setting is only the
				// boot value, so the last write is what the reply reports.
				if (root.TryGetProperty("positionalFilter", out JsonElement pf) && pf.ValueKind == JsonValueKind.Number)
				{
					_positionalFilter = System.Math.Clamp(pf.GetInt32(), 0, 5);
					RenderingServer.PositionalSoftShadowFilterSetQuality(
						(RenderingServer.ShadowQuality)_positionalFilter.Value);
				}

				if (root.TryGetProperty("positionalAtlas", out JsonElement pa) && pa.ValueKind == JsonValueKind.Number
					&& GetViewport() is Viewport atlasViewport)
				{
					atlasViewport.PositionalShadowAtlasSize = System.Math.Clamp(pa.GetInt32(), 256, 16384);
				}

				// How far the radius walk must move before the write reaches the node; 0 is the guest.
				// `OmniRange` is the one light property that changes the AABB, and every change costs a
				// full `PairInstances::pair` re-pair against everything in reach.
				if (root.TryGetProperty("lightFlickerRadius", out JsonElement lfr)
					&& lfr.ValueKind == JsonValueKind.Number)
				{
					Refii.Lighting.Engine_LightFlicker.RadiusEpsilon = (float)lfr.GetDouble();
				}

				// Every fixture's reach, live. Pairing tracks the volume a light covers, so the cost goes
				// with the cube: 0.667 undoes the 1.5 remaster default. One re-pair storm, then settles.
				if (root.TryGetProperty("lightRangeScale", out JsonElement lrs)
					&& lrs.ValueKind == JsonValueKind.Number)
				{
					Refii.Lighting.Engine_LevelLights.RangeScale = (float)lrs.GetDouble();
					Refii.Lighting.Engine_LevelLights.ApplyRangeScale();
				}

				// The radius inside which a rig poses every step (the guest's BoneLODDistance, default 10,
				// floor 8); beyond it the rate halves to 15 Hz. A huge value puts every visible rig on 30 Hz.
				if (root.TryGetProperty("poseFullRate", out JsonElement pfr)
					&& pfr.ValueKind == JsonValueKind.Number)
				{
					GameEntities.PoseFullRateRadius = (float)pfr.GetDouble();
				}

				// The far rigs' write rate in real time (`far_rig_pose_hz`, clamped to 4-30; 0 leaves them
				// on the guest's tick step, which is every walk under 30 fps), and whether each far rig
				// keeps its own phase (`far_rig_pose_spread`) or all share one.
				if (root.TryGetProperty("farRigPoseHz", out JsonElement farHz) && farHz.ValueKind == JsonValueKind.Number)
				{
					GameEntities.FarRigPoseHz = Refii.Entities.Engine_FarRigPoseRate.Clamp((float)farHz.GetDouble());
				}

				if (root.TryGetProperty("farRigPoseSpread", out JsonElement farSpread) &&
					(farSpread.ValueKind == JsonValueKind.True || farSpread.ValueKind == JsonValueKind.False))
				{
					GameEntities.FarRigPoseSpread = farSpread.ValueKind == JsonValueKind.True;
				}

				// Sample clips from the managed key cache, or from the `Animation` resource. Same
				// arithmetic either way (every track is Linear, and Godot's linear Quaternion case is
				// slerp), so a difference on screen means the cache is wrong.
				// Diagnostic A/B lever: retain the same reader and native simulation while
				// comparing early worker scheduling with the synchronous fallback.
				if (root.TryGetProperty("asyncEntityWalk", out JsonElement asyncWalk)
					&& asyncWalk.ValueKind is JsonValueKind.True or JsonValueKind.False)
					Refii.Host.HostEntityWalk.Enabled = asyncWalk.GetBoolean();

				if (root.TryGetProperty("managedClips", out JsonElement mc)
					&& mc.ValueKind != JsonValueKind.Null)
				{
					Refii.Animations.Engine_ClipAnimator.ManagedClipSampling = mc.GetBoolean();
				}

				if (root.TryGetProperty("sunSplits", out JsonElement sp))
				{
					Refii.Lighting.Engine_LevelLighting.SunSplitsOverride = sp.ValueKind == JsonValueKind.Number ? sp.GetInt32() : null;
				}

				// Variable rate shading over the whole view, to price it: `"vrs":0` off, 1 for 2x2, 2 for 4x4,
				// 3 for 1x1 in the middle third and 2x2 around it. Adreno prefers a fragment density map.
				if (root.TryGetProperty("vrs", out JsonElement vrsLever) && vrsLever.ValueKind == JsonValueKind.Number)
				{
					int vrs = vrsLever.GetInt32();
					Viewport view = GetViewport();
					if (vrs <= 0)
					{
						view.VrsMode = Viewport.VrsModeEnum.Disabled;
					}
					else
					{
						var rates = Image.CreateEmpty(48, 27, false, Image.Format.Rgb8);
						Color coarse = vrs == 2 ? Color.Color8(170, 170, 0) : Color.Color8(85, 85, 0);
						rates.Fill(coarse);
						if (vrs == 3)
						{
							rates.FillRect(new Rect2I(16, 9, 16, 9), Color.Color8(0, 0, 0));
						}
						view.VrsTexture = ImageTexture.CreateFromImage(rates);
						view.VrsUpdateMode = Viewport.VrsUpdateModeEnum.Once;
						view.VrsMode = Viewport.VrsModeEnum.Texture;
					}
				}

				// The 2D canvas in HDR, which also makes Mobile's 3D buffer RGBA16F: `"hdr2d":false`.
				if (root.TryGetProperty("hdr2d", out JsonElement hdr2dLever) &&
					hdr2dLever.ValueKind is JsonValueKind.True or JsonValueKind.False)
				{
					GetViewport().UseHdr2D = hdr2dLever.ValueKind == JsonValueKind.True;
				}

				if (root.TryGetProperty("sunBlend", out JsonElement sunBlendLever))
				{
					Refii.Lighting.Engine_LevelLighting.SunBlendOverride = sunBlendLever.ValueKind == JsonValueKind.Null ? null : sunBlendLever.GetBoolean();
				}

				// The regions' grid terrain as a sun caster, to price it: `"terrainShadow":false`.
				if (root.TryGetProperty("terrainShadow", out JsonElement ts) &&
					ts.ValueKind is JsonValueKind.True or JsonValueKind.False)
				{
					var cast = ts.ValueKind == JsonValueKind.True
						? GeometryInstance3D.ShadowCastingSetting.On
						: GeometryInstance3D.ShadowCastingSetting.Off;
					foreach (Node terrain in GetTree().Root.FindChildren(
						Refii.Level.Engine_LevelScene.TerrainNodeName, "Node3D", true, false))
					{
						foreach (MeshInstance3D chunk in Refii.Level.Engine_LevelScene.TerrainChunksOf(terrain.GetParent()))
						{
							chunk.CastShadow = cast;
						}
					}
				}

				if (root.TryGetProperty("sunShadow", out JsonElement ss))
				{
					Refii.Lighting.Engine_LevelLighting.SunShadowOverride = ss.ValueKind == JsonValueKind.Null ? null : ss.GetBoolean();
				}

				// The world's texture sampling, to price it: mip bias and anisotropic level (0, 2, 4, 8, 16).
				if (root.TryGetProperty("mipBias", out JsonElement mb) && mb.ValueKind == JsonValueKind.Number)
				{
					GetViewport().TextureMipmapBias = mb.GetSingle();
				}
				if (root.TryGetProperty("aniso", out JsonElement an) && an.ValueKind == JsonValueKind.Number)
				{
					GetViewport().AnisotropicFilteringLevel = an.GetInt32() switch
					{
						>= 16 => Viewport.AnisotropicFiltering.Anisotropy16X,
						>= 8 => Viewport.AnisotropicFiltering.Anisotropy8X,
						>= 4 => Viewport.AnisotropicFiltering.Anisotropy4X,
						>= 2 => Viewport.AnisotropicFiltering.Anisotropy2X,
						_ => Viewport.AnisotropicFiltering.Disabled,
					};
				}

				// Occlusion culling from the buildings' occluders (`occlusion_culling`), off and back on.
				if (root.TryGetProperty("occlusionCulling", out JsonElement oc) &&
					(oc.ValueKind == JsonValueKind.True || oc.ValueKind == JsonValueKind.False))
				{
					GetViewport().UseOcclusionCulling = oc.ValueKind == JsonValueKind.True;
				}

				if (root.TryGetProperty("reflections", out JsonElement rf) && rf.ValueKind != JsonValueKind.Null)
				{
					Refii.Level.Engine_WaterReflection.Enabled = rf.GetBoolean();
				}

				if (root.TryGetProperty("reflectionCadence", out JsonElement rc) && rc.ValueKind == JsonValueKind.Number)
				{
					Refii.Level.Engine_WaterReflection.Cadence = rc.GetInt32();
				}

				if (root.TryGetProperty("fx", out JsonElement fx) && fx.ValueKind != JsonValueKind.Null)
				{
					Refii.Fx.Engine_FxDisplacement.Current?.SetEnabled(fx.GetBoolean());
				}

				// A/B only: false puts the principals' shadows on the guest's rate too.
				if (root.TryGetProperty("shadowPrincipal", out JsonElement shadowPrincipal) && shadowPrincipal.ValueKind != JsonValueKind.Null)
				{
					Refii.Entities.Engine_ShadowSkeletons.PrincipalEveryFrame = shadowPrincipal.GetBoolean();
				}

				if (root.TryGetProperty("feedEveryFrame", out JsonElement fe) && fe.ValueKind != JsonValueKind.Null)
				{
					GameEntities.FeedEveryFrame = fe.GetBoolean();
				}

				if (root.TryGetProperty("entities", out JsonElement en) && en.ValueKind != JsonValueKind.Null)
				{
					if (GameEntities.Current is { } entities)
					{
						entities.Visible = en.GetBoolean();
					}
				}

				if (root.TryGetProperty("hud", out JsonElement hd) && hd.ValueKind != JsonValueKind.Null)
				{
					Refii.Ui.Engine_HudView.Enabled = hd.GetBoolean();
				}

				// The present mode, switchable live. Under any vsync mode (mailbox included) the frame
				// rate is capped at the refresh rate, so fps measures the display, not the machine.
				if (root.TryGetProperty("vsync", out JsonElement vs) && vs.ValueKind != JsonValueKind.Null)
				{
					DisplayServer.VSyncMode wanted = vs.ValueKind == JsonValueKind.False
						? DisplayServer.VSyncMode.Disabled
						: vs.ValueKind == JsonValueKind.True
							? DisplayServer.VSyncMode.Mailbox
							: vs.GetString() switch
							{
								"off" or "disabled" or "false" => DisplayServer.VSyncMode.Disabled,
								"on" or "fifo" => DisplayServer.VSyncMode.Enabled,
								"adaptive" => DisplayServer.VSyncMode.Adaptive,
								_ => DisplayServer.VSyncMode.Mailbox,
							};

					DisplayServer.WindowSetVsyncMode(wanted, (int)DisplayServer.MainWindowId);
				}

				if (root.TryGetProperty("maxFps", out JsonElement mf) && mf.ValueKind == JsonValueKind.Number)
				{
					Engine.MaxFps = System.Math.Max(0, mf.GetInt32());
				}

				// Clock estimators, switchable live: the per-sample estimator pins the playhead to the
				// tick counter (15 Hz stepping); the fitted clock advances smoothly. Compare with
				// `playheadVsNewest`.
				if (root.TryGetProperty("hostPhase", out JsonElement hp) &&
					hp.ValueKind is JsonValueKind.True or JsonValueKind.False)
				{
					Refii.Core.Engine_TickClock.UseHostPhase = hp.GetBoolean();
				}

				if (root.TryGetProperty("clockFit", out JsonElement cf) && cf.ValueKind != JsonValueKind.Null)
				{
					Refii.Core.Engine_TickClock.Disciplined = cf.GetBoolean();
				}

				// Bodies and the camera drawn by a line fitted through their statements' arrival times
				// (`motion_arrival_fit`), switchable live: a moving thing hands over at the drawn
				// transform it is at. The `camera` op reports `arrivalFits` and `arrivalStops`.
				if (root.TryGetProperty("motionArrivalFit", out JsonElement maf) &&
					maf.ValueKind is JsonValueKind.True or JsonValueKind.False)
				{
					Refii.Core.Engine_TickTimeline.ArrivalFit = maf.GetBoolean();
				}

				// The guest's own clock, switchable live. Off falls back to the estimators, kept only for
				// comparison.
				if (root.TryGetProperty("clockHost", out JsonElement ch) && ch.ValueKind != JsonValueKind.Null)
				{
					Refii.Core.Engine_TickClock.HostOwnsClock = ch.GetBoolean();
				}

				if (root.TryGetProperty("clockStated", out JsonElement cs) && cs.ValueKind != JsonValueKind.Null)
				{
					Refii.Core.Engine_TickClock.UseStatedClock = cs.GetBoolean();
				}

				// Guest time base: 0 real clock, 1 paced, 2 driven (the guest stepped at Godot's rate,
				// deterministically).
				if (root.TryGetProperty("timeBase", out JsonElement tb) && tb.ValueKind == JsonValueKind.Number)
				{
					_timeBase = Refii.Host.HostBridge.TimeBase(
						(Refii.Host.HostBridge.TimeBaseMode)tb.GetInt32());
				}

				// Spawns N plain OmniLight3D, every property at Godot's default, to price a light against
				// a placed fixture: the same cost means the price is Godot's; far less means it is
				// something we set (LightSpecular carrying the table slot, OmniAttenuation pinned to 0, the
				// per-tick OmniRange writes). Placed on a ring around the camera at the fixtures' scale, so
				// they are in view and processed rather than culled.
				if (root.TryGetProperty("testLights", out JsonElement tl) && tl.ValueKind == JsonValueKind.Number)
				{
					SetTestLights(System.Math.Clamp(tl.GetInt32(), 0, 512));
				}

				return Ok(FiniteRenderTelemetry(new Dictionary<string, object?>
				{
					["testLights"] = _testLights.Count,
					["shaderZeroSpecular"] = ShaderZeroWork.Specular,
					["shaderCostDiagnostic"] = ShaderCostDiagnostic.Mask,
					["treeDiagnosticDistance"] = _treeDistanceDiagnostic.Metres,
					["treeDiagnosticHiddenNodes"] = _treeDistanceDiagnostic.Hidden,
					["shaderCeiling"] = ShaderCeiling.Describe(),
					["shaderZeroCliff"] = ShaderZeroWork.Cliff,
					["shaderNearFog"] = ShaderZeroWork.NearFog,
					["shaderSparseTerrain"] = ShaderZeroWork.SparseTerrain,
					["fxPackedWrites"] = Refii.Fx.Engine_FxSurfacePacker.Enabled,
					["fxBulkGeometry"] = Refii.Fx.Engine_FxGeometryWork.Enabled,
					["fxFastPaths"] = Refii.Fx.Engine_FxFastPaths.Enabled,
					["stableLightCounts"] = Refii.Lighting.Engine_StableLightCounts.Enabled,
					["stableLightSentinel"] = Refii.Lighting.Engine_StableLightCounts.Sentinel,
					["stableLightFillers"] = Refii.Lighting.Engine_StableLightCounts.Standing,
					// The share takes effect at the next frame's draw; canvascensus shows it.
					["canvasLean"] = Refii.Ui.Engine_CanvasLean.Enabled,
					["canvasLeanSharedInto"] = Refii.Ui.Engine_CanvasLean.SharedInto,
					["canvasLeanBlocker"] = Refii.Ui.Engine_CanvasLean.Blocker,
					["postFxEnabled"] = Refii.Lighting.Engine_PostFxParams.Enabled,
					["postFxChainRequested"] = Refii.Lighting.Engine_PostFxParams.ChainRequested,
					// Whether the chain can reach the colour layer (Unknown / Usable / Unusable): the
					// additive and highlight layers draw only when Usable, unless fxLegacyLayers.
					["postFxRoute"] = Refii.Lighting.Engine_PostFxRoute.Current.ToString(),
					["fxLegacyLayers"] = Refii.Lighting.Engine_PostFxRoute.LegacyLayers,
					["fxAdditiveLayerPresent"] = Refii.Fx.Engine_FxAdditiveLayer.Present,
					["fxNoDepthReads"] = Refii.Fx.Engine_FxDepthReads.Enabled,
					["fxHardCapableMaterials"] = Refii.Fx.Engine_FxDepthReads.AdoptedCount,
					["skyRadianceLean"] = Refii.Level.Engine_SkyCosts.RadianceLean,
					// The held sky's radiance size and its own, applied on the next frame; null when none is held.
					["skyRadianceHeld"] = Refii.Level.Engine_SkyCosts.LeanRadianceState,
					["skyRadianceInputs"] = Refii.Level.Engine_SkyCosts.LastInputs,
					["meshLodThreshold"] = GetViewport().MeshLodThreshold,
					["scaling3dMode"] = GetViewport().Scaling3DMode.ToString(),
					["scaling3dScale"] = GetViewport().Scaling3DScale,
					["internal3dSize"] = new
					{
						width = System.Math.Max(1, (int)System.MathF.Round(GetTree().Root.Size.X * GetViewport().Scaling3DScale)),
						height = System.Math.Max(1, (int)System.MathF.Round(GetTree().Root.Size.Y * GetViewport().Scaling3DScale)),
					},
					["viewportPhysicalSize"] = new { width = GetTree().Root.Size.X, height = GetTree().Root.Size.Y },
					// Window's ViewportTexture accessor includes canvas stretch; not the RS or internal 3D extent.
					["viewportTextureAccessorSize"] = new { width = GetViewport().GetTexture().GetWidth(), height = GetViewport().GetTexture().GetHeight() },
					["viewportTextureAccessorScope"] = "raw Window ViewportTexture accessor, may include canvas stretch; not internal3D or RenderingServer extent",
					["project3dScale"] = ProjectSettings.GetSetting("rendering/scaling_3d/scale", 1f).AsSingle(),
					["cameraPose"] = CaptureCameraTelemetry(),
					["shadowReach"] = Refii.Lighting.Engine_ShadowReach.Enabled,
					["shadowSuppressed"] = Refii.Lighting.Engine_ShadowReach.Suppressed,
					["shadowConsidered"] = Refii.Lighting.Engine_ShadowReach.Considered,
					["shadowBudget"] = Refii.Lighting.Engine_LightShadowBudget.Budget,
					["shadowLimit"] = Refii.Lighting.Engine_LightShadowBudget.MaxDynamicEntityShadowLights,
					["shadowCasting"] = Refii.Lighting.Engine_LightShadowBudget.Casting,
					["shadowEligible"] = Refii.Lighting.Engine_LightShadowBudget.Eligible,
					["gi"] = !Refii.Lighting.Engine_GiRefresh.ForcedOff,
					["sdfgiConfigured"] = Refii.Lighting.Engine_LevelLighting.SdfgiEnabled,
					["sdfgiLive"] = GetViewport().World3D?.Environment?.SdfgiEnabled ?? false,
					["giHalf"] = Refii.Lighting.Engine_LevelLighting.GiHalfResolution,
					["fillFloor"] = Refii.Lighting.Engine_LightingFrame.ModernFloorShare,
					["giRebuilds"] = Refii.Lighting.Engine_GiRefresh.Rebuilds,
					["shadowProxy"] = Refii.Entities.Engine_ShadowProxy.Enabled,
					["entityBatches"] = Refii.Entities.Engine_EntityBatches.Enabled,
					["entityBatched"] = Refii.Entities.Engine_EntityBatches.Batched,
					["entityBatchCount"] = Refii.Entities.Engine_EntityBatches.Batches,
					["entityBatchPromotions"] = Refii.Entities.Engine_EntityBatches.Promotions,
					["entityBatchDemotions"] = Refii.Entities.Engine_EntityBatches.Demotions,
					["entitySkipIndex"] = GameEntities.SkipIndexEnabled,
					["entitySkipIndexHits"] = GameEntities.SkipIndexHits,
					["entitySkipIndexHeld"] = GameEntities.SkipIndexHeld,
					["bodyVariants"] = Refii.Entities.Engine_BodyVariants.Enabled,
					["bodyVariantsBuilt"] = Refii.Entities.Engine_BodyVariants.Built,
					["bodyVariantSurfacesRemoved"] = Refii.Entities.Engine_BodyVariants.SurfacesRemoved,
					["propShadow"] = Refii.Level.Engine_PropShadow.Enabled,
					["reflectContract"] = Refii.Level.Engine_ReflectionLayer.FollowGuest,
					["propShadowBatches"] = Refii.Level.Engine_PropShadow.Batches,
					["propShadowSurfacesMerged"] = Refii.Level.Engine_PropShadow.SurfacesMerged,
					["shadowProxies"] = Refii.Entities.Engine_ShadowProxy.Active,
					["shadowProxySurfacesMerged"] = Refii.Entities.Engine_ShadowProxy.SurfacesMerged,
					["lightDebug"] = _lightDebug,
					["exposureLock"] = Refii.Lighting.Engine_PostFxParams.ExposureLock
						? Refii.Lighting.Engine_PostFxParams.ExposureLockValue : null,
					["placedShadowBlur"] = Refii.Lighting.Engine_LevelLights.PlacedShadowBlur,
					["placedShadowNormalBias"] = Refii.Lighting.Engine_LevelLights.PlacedShadowNormalBias,
					["placedShadowBias"] = float.IsNaN(Refii.Lighting.Engine_LevelLights.PlacedShadowBias)
						? null : Refii.Lighting.Engine_LevelLights.PlacedShadowBias,
					["positionalFilter"] = _positionalFilter ?? ProjectSettings.GetSetting(
						"rendering/lights_and_shadows/positional_shadow/soft_shadow_filter_quality").AsInt32(),
					["positionalAtlas"] = GetViewport()?.PositionalShadowAtlasSize,
					// The atmosphere, and the one inference that scales it: "interior" is not a stated flag
					// (the guest has none); it is derived as `ModernModel && !HasSky`. These numbers say
					// whether that gate trips where the player is standing.
					["interior"] = Refii.Lighting.Engine_LightingFrame.CurrentInterior,
					["paintedWeight"] =
						Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting?.PaintedWeight,
					["skylightWeight"] =
						Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting?.SkylightWeight,
					["interiorFogScale"] = Refii.Lighting.Engine_LightingFrame.InteriorFogScale,
					["outdoorFogScale"] = Refii.Lighting.Engine_LightingFrame.OutdoorFogScale,
					["interiorVolumetricShaftScale"] = Refii.Lighting.Engine_PostFxParams.InteriorVolumetricShaftScale,
					["outdoorVolumetricShaftScale"] = Refii.Lighting.Engine_PostFxParams.OutdoorVolumetricShaftScale,
					["volumetricShaftScale"] = Refii.Lighting.Engine_PostFxParams.VolumetricShaftScale,
					["bakeWeight"] = Refii.Lighting.Engine_LevelBakedLighting.BakeWeight,
					["lightTable"] = Refii.Lighting.Engine_LightTable.Live,
					["lightBudget"] = Refii.Lighting.Engine_LightShadowBudget.LightBudget,
					["lightFlickerRadius"] = Refii.Lighting.Engine_LightFlicker.RadiusEpsilon,
					["lightRangeScale"] = Refii.Lighting.Engine_LevelLights.RangeScale,
					["poseFullRate"] = GameEntities.PoseFullRateRadius,
					["farRigPoseHz"] = GameEntities.FarRigPoseHz,
					["farRigPoseSpread"] = GameEntities.FarRigPoseSpread,
					["farRigsWritten"] = GameEntities.FarRigsWritten,
					["farRigsHeld"] = GameEntities.FarRigsHeld,
					["managedClips"] = Refii.Animations.Engine_ClipAnimator.ManagedClipSampling,
					["asyncEntityWalk"] = Refii.Host.HostEntityWalk.Enabled,
					["asyncEntityWalkTaken"] = Refii.Host.HostEntityWalk.Taken,
					["asyncEntityWalkWaited"] = Refii.Host.HostEntityWalk.Waited,
					["asyncEntityWalkTorn"] = Refii.Host.HostEntityWalk.TornWalks,
					["flickerRadiusWrites"] = Refii.Lighting.Engine_LightFlicker.RadiusWrites,
					["flickerRadiusCandidates"] = Refii.Lighting.Engine_LightFlicker.RadiusCandidates,
					["hero"] = GameEntities.HeroPosition is { } hero
						? $"({hero.X:0.0}, {hero.Y:0.0}, {hero.Z:0.0})" : null,
					["heroLocal"] = GameEntities.HeroPosition is { } heroWorld &&
						Refii.Level.Engine_SkyEnvironmentNode.Current?.GetParent() is Node3D region
						? region.GlobalTransform.AffineInverse() * heroWorld is { } local
							? $"({local.X:0.0}, {local.Y:0.0}, {local.Z:0.0})" : null
						: null,
					["painted"] = Refii.Level.Engine_SkyEnvironmentNode.Current?.Painted?.Describe(),
					["sunShadow"] = Refii.Lighting.Engine_LevelLighting.SunShadowOverride,
					["sunSplits"] = Refii.Lighting.Engine_LevelLighting.SunSplitsOverride,
					["sunBlend"] = Refii.Lighting.Engine_LevelLighting.SunBlendOverride,
					["vrs"] = GetViewport().VrsMode.ToString(),
					["hdr2d"] = GetViewport().UseHdr2D,
					["reflections"] = Refii.Level.Engine_WaterReflection.Enabled,
					["occlusionCulling"] = GetViewport().UseOcclusionCulling,
					["mipBias"] = GetViewport().TextureMipmapBias,
					["aniso"] = GetViewport().AnisotropicFilteringLevel.ToString(),
					["occluders"] = Refii.Level.Engine_BuildingOccluders.Placed,
					["occluderTriangles"] = Refii.Level.Engine_BuildingOccluders.Triangles,
					["reflectionCadence"] = Refii.Level.Engine_WaterReflection.Cadence,
					["vsync"] = DisplayServer.WindowGetVsyncMode((int)DisplayServer.MainWindowId).ToString(),
					["maxFps"] = Engine.MaxFps,
					["clockFit"] = Refii.Core.Engine_TickClock.Disciplined,
					["motionArrivalFit"] = Refii.Core.Engine_TickTimeline.ArrivalFit,
					["playheadVsNewest"] = Refii.Core.Engine_TickClock.PlayheadVsNewest,
					["refreshHz"] = DisplayServer.ScreenGetRefreshRate(
						DisplayServer.WindowGetCurrentScreen((int)DisplayServer.MainWindowId)),
				}));
			}

			case "guestrender":
			{
				// The guest's own scene draw on or off (diagnostic): with it on, the title's
				// constant setters fill the device shadow the classic feed is checked against.
				bool? on = root.TryGetProperty("on", out JsonElement onValue)
					? onValue.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
					: null;
				return Ok(new Dictionary<string, object?> { ["applied"] = Refii.Host.HostBridge.SetGuestRender(on), ["on"] = on });
			}

			case "classic":
			{
				// THE CLASSIC RENDERER'S FEED, read back: the frame row's registers by name, the
				// lights the manager stated, the counts of every feed. `shadowMaps` is a lever:
				// "capture" (the light's depth), "black" or "white" (a constant map) to tell the
				// cascade lookup's two outcomes apart on screen.
				// `cullHide` / `cullShow`: a render layer (1-based, as the inspector numbers them) off
				// or back on for the drawn camera, to tell which layer draws something on screen.
				if (GetViewport().GetCamera3D() is { } cullCamera)
				{
					if (root.TryGetProperty("cullHide", out JsonElement ch) && ch.ValueKind == JsonValueKind.Number)
					{
						cullCamera.SetCullMaskValue(ch.GetInt32(), false);
					}
					if (root.TryGetProperty("cullShow", out JsonElement cs) && cs.ValueKind == JsonValueKind.Number)
					{
						cullCamera.SetCullMaskValue(cs.GetInt32(), true);
					}
				}
				// `hideNode` / `showNode`: one node by path off or back on, to find what draws a thing.
				if (root.TryGetProperty("hideNode", out JsonElement hn) && hn.ValueKind == JsonValueKind.String &&
					hn.GetString() is { } hidePath && GetTree().Root.GetNodeOrNull(hidePath) is Node3D hideTarget)
				{
					hideTarget.Visible = false;
				}
				if (root.TryGetProperty("showNode", out JsonElement sn) && sn.ValueKind == JsonValueKind.String &&
					sn.GetString() is { } showPath && GetTree().Root.GetNodeOrNull(showPath) is Node3D showTarget)
				{
					showTarget.Visible = true;
				}
				if (root.TryGetProperty("shadowMaps", out JsonElement sm) && sm.ValueKind == JsonValueKind.String)
				{
					string mode = sm.GetString() ?? "capture";
					if (mode is "capture" or "black" or "white")
					{
						Refii.Models.Engine_ClassicShadows.MapMode = mode;
					}
				}

				static Dictionary<string, object?> Registers(bool pixel, (string Name, int Register, int Count)[] list)
				{
					var block = new Dictionary<string, object?>();
					foreach ((string name, int register, int count) in list)
					{
						float[] values = Refii.Models.Engine_ClassicConstants.ReadFrame(pixel, register, count);
						block[$"c{register} {name}"] = values.Select(v => (double)v).ToArray();
					}
					return block;
				}

				var lights = new List<Dictionary<string, object?>>();
				foreach (Refii.Models.Engine_ClassicLights.Light light in Refii.Models.Engine_ClassicLights.Lights.Take(12))
				{
					lights.Add(new Dictionary<string, object?>
					{
						["index"] = light.Index,
						["type"] = light.Type,
						["position"] = new[] { light.Position.X, light.Position.Y, light.Position.Z },
						["radius"] = light.Radius,
						["colour"] = new[] { light.Colour.X, light.Colour.Y, light.Colour.Z },
						["intensity"] = light.Intensity,
						["direction"] = new[] { light.Direction.X, light.Direction.Y, light.Direction.Z },
						["range"] = light.Range,
						["flags"] = light.Flags,
						["zone"] = $"{light.Zone:X8}",
					});
				}
				bool haveSun = Refii.Models.Engine_ClassicFeed.TrySun(out Vector3 sunColour, out Vector3 sunDirection, out float sunIntensity);
				(long feedFrames, long fed) = Refii.Models.Engine_ClassicFeed.Counts;
				(int attached, int lit) = Refii.Models.Engine_ClassicLitInstances.Counts;
				(int built, int fellBack) = Refii.Models.Engine_ClassicMaterial.Counts;
				var particles = Refii.Models.Engine_ClassicParticles.Instance?.Counts ?? (0, 0, 0);
				return Ok(new Dictionary<string, object?>
				{
					["active"] = Refii.Models.Engine_ClassicMaterial.Active,
					// The raw exports, so a run's frame can be saved and replayed in the level viewer
					// (Engine_ClassicPath.LoadReplay).
					["frame"] = Refii.Models.Engine_ClassicFeed.RawFrame.Select(v => (double)v).ToArray(),
					// Our whole frame row, both stages, for a register-by-register diff against
					// the device shadow of a `guestrender` run.
					["psRow"] = Refii.Models.Engine_ClassicConstants.ReadFrame(true, 0, 256).Select(v => (double)v).ToArray(),
					["vsRow"] = Refii.Models.Engine_ClassicConstants.ReadFrame(false, 0, 256).Select(v => (double)v).ToArray(),
					["lightsRaw"] = Refii.Models.Engine_ClassicLights.RawLights().Select(v => (double)v).ToArray(),
					["mistsRaw"] = Refii.Models.Engine_ClassicMist.RawMists().Select(v => (double)v).ToArray(),
					["feedFrames"] = feedFrames,
					["fed"] = fed,
					["seconds"] = Refii.Models.Engine_ClassicFeed.Seconds,
					["lights"] = lights,
					["lightCount"] = Refii.Models.Engine_ClassicLights.Lights.Count,
					["litInstances"] = lit,
					["attachedInstances"] = attached,
					["liveRows"] = Refii.Models.Engine_ClassicConstants.LiveRows,
					["sharedLightRows"] = Refii.Models.Engine_ClassicLitInstances.SharedRows,
					["mists"] = Refii.Models.Engine_ClassicMist.Volumes.Count,
					["particles"] = particles.Item2,
					["particleGroups"] = particles.Item1,
					["waterSurfaces"] = Refii.Level.Engine_ClassicWater.Count,
					["materialsBuilt"] = built,
					["materialsFellBack"] = fellBack,
					["shadowMaps"] = Refii.Models.Engine_ClassicShadows.MapMode,
					["shadowsUp"] = Refii.Models.Engine_ClassicShadows.Instance is not null,
					["sun"] = haveSun ? new Dictionary<string, object?>
					{
						["colour"] = new[] { sunColour.X, sunColour.Y, sunColour.Z },
						["direction"] = new[] { sunDirection.X, sunDirection.Y, sunDirection.Z },
						["intensity"] = sunIntensity,
					} : null,
					// The sky elements' inputs as the title reads them (Engine_ClassicSkyElements):
					// SUN_INTENSITY p7 gates, SUN_GLARE_INTENSITY p100 x facing^2 x visibility^2
					// scales the sky object's colour, SUN_GLARE_SIZE p101 sizes; the visibilities are
					// the sun disc's and moon bloom's counted pixels (GPU readback).
					["sky"] = Refii.Level.Engine_ClassicSkyElements.Active ? SkyReadout() : null,
					["water"] = Refii.Level.Engine_ClassicWater.Describe(),
					["particleGroups"] = Refii.Models.Engine_ClassicParticles.Instance?.Describe(),
					["casters"] = Refii.Models.Engine_ClassicCasters.Census(),
					["litAssigned"] = Refii.Models.Engine_ClassicLitInstances.LastAssigned,
					["litSkipped"] = Refii.Models.Engine_ClassicLitInstances.LastSkipped,
					["ambient"] = new[]
					{
						Refii.Models.Engine_ClassicFeed.GlobalAmbient.X,
						Refii.Models.Engine_ClassicFeed.GlobalAmbient.Y,
						Refii.Models.Engine_ClassicFeed.GlobalAmbient.Z,
					},
					["ps"] = Registers(true, new[]
					{
						("ShadowRangeConstants", 12, 1), ("ShadowMapResolution", 13, 2), ("FadeBeginDistanceAndRange", 15, 1),
						("SimulationTime", 17, 1), ("PerspectiveConstants", 18, 1), ("EyePosition", 19, 1),
						("GlobalAmbientAndBrightness", 20, 1), ("LightmapFactorAndGamma", 21, 1),
						("AmbientNormalMapDarkeningColour", 22, 1), ("NormalMapLightAngleConstants", 23, 1),
						("AtmosphericFactors", 28, 1), ("GlobalTextureGammaRamp", 30, 1),
						("DirectionalLightShadowTransform0", 36, 4), ("DirectionalLightShadowTransform1", 40, 4),
						("Atmosphere", 64, 5), ("Mist", 72, 5),
						("LightPositions", 80, 2), ("LightColors", 87, 2), ("LightType", 94, 1), ("LightInvRadii", 96, 1),
						("LightSpotDirections", 98, 1), ("LightSpotParameters", 105, 1), ("LightPrimaryDirection", 109, 1),
						("LightScreenSpaceShadowMask", 112, 1), ("LightSpotShadowMask", 114, 1),
					}),
					["vs"] = Registers(false, new[]
					{
						("AtmosphericAdditiveFactor", 8, 1), ("EyePosition", 9, 1), ("SimulationTime", 10, 1),
						("TreeConstants", 15, 3), ("LightmapFactorAndGamma", 18, 1), ("AmbientLightReferenceDirection", 19, 1),
						("GlobalAmbientAndBrightness", 54, 1), ("LightPositions", 76, 2), ("LightColors", 83, 2),
						("LightType", 90, 1), ("LightPrimaryDirection", 105, 1), ("Atmosphere", 108, 5),
					}),
				});
			}

			case "looks":
			{
				// The drawn entities nearest the eye and what the guest states for their look.
				int take = root.TryGetProperty("count", out JsonElement lk) && lk.ValueKind == JsonValueKind.Number
					? System.Math.Clamp(lk.GetInt32(), 1, 64) : 12;
				Vector3 eye = GetViewport()?.GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
				return Ok(new Dictionary<string, object?>
				{
					["looks"] = GameEntities.Current?.DescribeLooks(eye, take),
				});
			}

			case "entity":
			{
				// One drawn entity, as the feed sees it: stated parts with their model names, the
				// rig's bone mapping, and the skeleton's current hold state.
				uint address = root.TryGetProperty("addr", out JsonElement ad) && ad.ValueKind == JsonValueKind.String
					? System.Convert.ToUInt32(ad.GetString(), 16)
					: 0u;
				if (root.TryGetProperty("lighting", out JsonElement wantLighting) &&
					wantLighting.ValueKind == JsonValueKind.True)
				{
					// No address is the hero.
					return Ok(new Dictionary<string, object?>
					{
						["lighting"] = GameEntities.Current?.DescribeLighting(address),
					});
				}

				if (address == 0u)
				{
					return Ok(new Dictionary<string, object?> { ["animators"] = GameEntities.Current?.DescribeAnimators() });
				}
				uint[] morph = Refii.Host.HostBridge.GetEntityMorphParts(address, out int built);
				uint[] meshes = Refii.Host.HostBridge.GetEntityMeshParts(address, out int total);
				var parts = new List<object?>();
				foreach (uint part in morph.Concat(meshes))
				{
					parts.Add(new Dictionary<string, object?>
					{
						["hash"] = $"{part:X8}",
						["path"] = Refii.Level.Engine_PropModelIndex.Resolve(part, System.Array.Empty<string>()),
					});
				}

				if (root.TryGetProperty("trace", out JsonElement traceFrames) &&
					traceFrames.ValueKind == JsonValueKind.Number)
				{
					GameEntities.Current?.BeginTrace(address, traceFrames.GetInt32());
					return Ok(new Dictionary<string, object?> { ["tracing"] = $"{address:X8}" });
				}

				if (root.TryGetProperty("traceReport", out JsonElement wantTrace) &&
					wantTrace.ValueKind == JsonValueKind.True)
				{
					return Ok(new Dictionary<string, object?>
					{
						["address"] = $"{address:X8}",
						["trace"] = GameEntities.Current?.TraceReport(),
					});
				}

				if (root.TryGetProperty("sources", out JsonElement wantSources) &&
					wantSources.ValueKind == JsonValueKind.String)
				{
					return Ok(new Dictionary<string, object?>
					{
						["address"] = $"{address:X8}",
						["sources"] = GameEntities.Current?.DescribeLocalSources(address, wantSources.GetString() ?? string.Empty),
					});
				}

				if (root.TryGetProperty("skinning", out JsonElement wantSkinning) &&
					wantSkinning.ValueKind == JsonValueKind.String)
				{
					// `{"op":"entity","addr":"...","skinning":"<bone>"}`: the rig skinned from Godot's own
					// arrays against that bone carrying the mesh rigidly ("" for the bind position).
					return Ok(new Dictionary<string, object?>
					{
						["address"] = $"{address:X8}",
						["skinning"] = GameEntities.Current?.DescribeSkinning(address, wantSkinning.GetString() ?? string.Empty),
					});
				}

				if (root.TryGetProperty("bones", out JsonElement wantBones) &&
					wantBones.ValueKind == JsonValueKind.True)
				{
					return Ok(new Dictionary<string, object?>
					{
						["address"] = $"{address:X8}",
						["rig"] = GameEntities.Current?.DescribeBones(address),
					});
				}

				if (root.TryGetProperty("surfaces", out JsonElement wantSurfaces) &&
					wantSurfaces.ValueKind == JsonValueKind.True)
				{
					return Ok(new Dictionary<string, object?>
					{
						["address"] = $"{address:X8}",
						["surfaces"] = GameEntities.Current?.DescribeSurfaces(address),
					});
				}

				Refii.Host.HostBridge.BonePose? pose = Refii.Host.HostBridge.GetEntityPose(address);
				return Ok(new Dictionary<string, object?>
				{
					["address"] = $"{address:X8}",
					["morphBuilt"] = built,
					["meshTotal"] = total,
					["parts"] = parts,
					["attachments"] = GameEntities.Current?.DescribeAttachments(address),
					["poseBones"] = pose?.BoneCount,
					["poseSpace"] = pose?.Space,
					["poseTorn"] = pose?.Torn,
					["rig"] = GameEntities.Current?.DescribeRig(address),
					["leaves"] = GameEntities.Current?.DescribeLeaves(address),
				});
			}

			case "cloth":
			{
				// Every live cloth body: what it stands in for, its cost and its state.
				return Ok(Refii.Physics.Engine_ClothBody.Describe());
			}

			case "twins":
			{
				// The live entities and the static twins of one model family, with the hide
				// state of each: `{"op":"twins","model":"door"}`.
				string needle = root.TryGetProperty("model", out JsonElement wantModel) &&
					wantModel.ValueKind == JsonValueKind.String
					? wantModel.GetString() ?? string.Empty
					: string.Empty;
				bool apply = root.TryGetProperty("apply", out JsonElement wantApply) &&
					wantApply.ValueKind == JsonValueKind.True;
				return Ok(GameEntities.Current?.DescribeTwins(needle, apply) ?? new Dictionary<string, object?>());
			}

			case "casters":
			{
				// What the shadow passes draw, by owning node: `{"op":"casters"}`. With
				// `"moving":true`, what moved since the previous such call instead.
				Node? world = GameEntities.Current?.GetTree()?.Root;
				bool moving = root.TryGetProperty("moving", out JsonElement wantMoving) &&
					wantMoving.ValueKind == JsonValueKind.True;
				// `"dirtiers":N` samples N frames for casters moving on host-only frames; 0 reads the report.
				if (root.TryGetProperty("dirtiers", out JsonElement wantDirtiers) && wantDirtiers.ValueKind == JsonValueKind.Number)
				{
					int frames = wantDirtiers.GetInt32();
					if (frames > 0 && world is not null)
					{
						Refii.Lighting.Engine_ShadowDirtiers.Start(world, frames);
					}

					return Ok(Refii.Lighting.Engine_ShadowDirtiers.Report());
				}

				if (world is not null && root.TryGetProperty("materials", out JsonElement wantMaterials) &&
					wantMaterials.ValueKind == JsonValueKind.True)
				{
					return Ok(Refii.Lighting.Engine_CasterCensus.Materials(world));
				}

				return Ok(world is null
					? new Dictionary<string, object?>()
					: moving
						? Refii.Lighting.Engine_CasterCensus.Moving(world)
						: Refii.Lighting.Engine_CasterCensus.Describe(world));
			}

			case "doubles":
			{
				// Every drawn entity with a drawn static twin within `radius` metres (default
				// 0.5), any model: `{"op":"doubles","radius":0.5}`.
				float radius = root.TryGetProperty("radius", out JsonElement wantRadius) &&
					wantRadius.ValueKind == JsonValueKind.Number
					? (float)wantRadius.GetDouble()
					: 0.5f;
				return Ok(GameEntities.Current?.DescribeDoubles(radius) ?? new Dictionary<string, object?>());
			}

			case "drawcensus":
			{
				// The main view's surfaces and material passes by owner and family, and the draws
				// left once equal (mesh, surface, material) keys merge: `{"op":"drawcensus"}`.
				Node? world = GameEntities.Current?.GetTree()?.Root;
				Camera3D? camera = GameEntities.Current?.GetViewport()?.GetCamera3D();
				return Ok(world is null || camera is null
					? new Dictionary<string, object?>()
					: Refii.Core.Engine_DrawCensus.Describe(world, camera));
			}

			case "charcensus":
			{
				// Characters in view against the other entities: surfaces, passes, LOD-0 triangles,
				// skinned vertices and shader builds (AndroidCharacterCensus): `{"op":"charcensus"}`.
				Camera3D? camera = GameEntities.Current?.GetViewport()?.GetCamera3D();
				return Ok(GameEntities.Current is not { } entities || camera is null
					? new Dictionary<string, object?>()
					: Refii.AndroidRuntime.AndroidCharacterCensus.Describe(entities, camera));
			}

			case "lightpairs":
				// Omni/spot lights paired per instance in view, Mobile's way (AndroidLightPairCensus): `{"op":"lightpairs"}`.
				return Ok((GameEntities.Current?.GetViewport() ?? GetViewport())?.GetCamera3D() is { } pairCamera ? Refii.AndroidRuntime.AndroidLightPairCensus.Describe(GetTree().Root, pairCamera) : new Dictionary<string, object?>());

			case "instanceuniforms":
			{
				// The instances holding a slot of Godot's instance-uniform buffer, by owner, against
				// its capacity: `{"op":"instanceuniforms"}`. The whole tree, so a scene without
				// GameEntities (the phone's) is counted too.
				return Ok(Refii.Core.Engine_InstanceUniformCensus.Describe(GetTree().Root));
			}

			case "canvascensus":
			{
				// The 2D pass by viewport and CanvasLayer: visible items, the pixels they cover, their
				// materials and screen reads, and each layer's pass priced: `{"op":"canvascensus"}`.
				return Ok(Refii.Ui.Engine_CanvasCensus.Describe(GetTree()));
			}

			case "lodcensus":
			{
				// The main view's triangles by owner and mesh, at the level Godot picks and at the
				// guest's per-object pick: `{"op":"lodcensus"}`.
				Node? world = GameEntities.Current?.GetTree()?.Root;
				Viewport? view = GameEntities.Current?.GetViewport();
				return Ok(world is null || view is null
					? new Dictionary<string, object?>()
					: Refii.Models.Engine_MdlLodCensus.Describe(world, view));
			}

			case "gpuprofile":
			{
				// Godot's per-pass GPU timings: the split of `renderGpuMs`. Present only while the
				// engine captures timestamps (`gpu_profile = true` in refii.toml).
				return Ok(Refii.Core.Engine_GpuPasses.Report());
			}

			case "gcframes":
			{
				// One row per frame from the GC frame ring (Engine_GcFrames): what the main thread and
				// every thread allocated, which frames held a collection and what it paused. Ask once
				// with no `since` for the newest frame, walk, then ask again with that frame as
				// `since`: the window's rows come back without polling during it.
				long since = root.TryGetProperty("since", out JsonElement sinceValue) &&
					sinceValue.TryGetInt64(out long after) ? after : long.MaxValue;
				bool withRows = root.TryGetProperty("rows", out JsonElement rowsValue) &&
					rowsValue.ValueKind == JsonValueKind.True;
				var reply = new Dictionary<string, object?>
				{
					["frame"] = Refii.Core.Engine_GcFrames.LatestFrame,
					["workers"] = Refii.Core.Engine_GcFrames.Workers(),
				};

				if (since != long.MaxValue)
				{
					List<Refii.Core.Engine_GcFrames.Row> rows = Refii.Core.Engine_GcFrames.RowsSince(since);
					reply["summary"] = Refii.Core.Engine_GcFrames.Summarise(rows);
					var collections = new List<object?>();
					foreach (Refii.Core.Engine_GcFrames.Collection c in Refii.Core.Engine_GcFrames.CollectionsSince(since))
					{
						collections.Add(Refii.Core.Engine_GcFrames.Describe(c));
					}

					reply["collections"] = collections;
					if (withRows)
					{
						// Columns rather than objects: a 40 s walk is 800 rows. All double, the array
						// type the other replies already serialize.
						var frames = new double[rows.Count];
						var ms = new double[rows.Count];
						var mainKB = new double[rows.Count];
						var totalKB = new double[rows.Count];
						var gens = new double[rows.Count];
						var pauseMs = new double[rows.Count];
						for (int i = 0; i < rows.Count; ++i)
						{
							Refii.Core.Engine_GcFrames.Row r = rows[i];
							frames[i] = r.Frame;
							ms[i] = System.Math.Round(r.FrameMs, 2);
							mainKB[i] = System.Math.Round(r.MainBytes / 1024.0, 1);
							totalKB[i] = System.Math.Round(r.TotalBytes / 1024.0, 1);
							gens[i] = r.Gen2 > 0 ? 2 : r.Gen1 > 0 ? 1 : r.Gen0 > 0 ? 0 : -1;
							pauseMs[i] = System.Math.Round(r.PauseMs, 2);
						}

						reply["rows"] = new Dictionary<string, object?>
						{
							["frame"] = frames,
							["ms"] = ms,
							["mainKB"] = mainKB,
							["totalKB"] = totalKB,
							["gen"] = gens,
							["pauseMs"] = pauseMs,
						};
					}
				}

				return Ok(reply);
			}

			case "framephases":
			{
				// Read CPU phases independently: an unavailable number elsewhere in the larger
				// metrics reply must not hide the main-thread evidence needed on Android.
				return Ok(Refii.Core.Engine_FramePhases.Report());
			}

			case "capture":
			{
				// RenderDoc: `{"op":"capture"}` captures the next frame; `{"op":"capture",
				// "list":true}` only lists what has been written. The capture finishes at the end
				// of the frame after the trigger, so a caller polls `list` until `captures` grows
				// and `capturing` is false.
				var reply = new Dictionary<string, object?>
				{
					["armed"] = Refii.Core.Engine_RenderDoc.Armed,
					["version"] = Refii.Core.Engine_RenderDoc.Version,
					["captureDir"] = Refii.Core.Engine_RenderDoc.CaptureDirectory,
					["installRoot"] = Refii.Core.Engine_RenderDoc.InstallRoot,
				};

				if (!Refii.Core.Engine_RenderDoc.Armed)
				{
					reply["note"] = "RenderDoc is not armed for this run: set renderdoc = true in " +
						Refii.Core.Engine_Title.Active.ConfigFileName + " (and renderdoc_path if the install is not on PATH) and relaunch";
					return Ok(reply);
				}

				bool listOnly = root.TryGetProperty("list", out JsonElement listValue) &&
					listValue.ValueKind == JsonValueKind.True;
				if (!listOnly)
				{
					reply["triggered"] = Refii.Core.Engine_RenderDoc.TriggerCapture();
				}

				reply["capturing"] = Refii.Core.Engine_RenderDoc.IsCapturing;
				var captures = new List<Dictionary<string, object?>>();
				foreach ((string path, ulong stamp) in Refii.Core.Engine_RenderDoc.Captures())
				{
					captures.Add(new Dictionary<string, object?> { ["path"] = path, ["timestamp"] = stamp });
				}

				reply["captures"] = captures;
				return Ok(reply);
			}

			case "metrics":
			{
				// The HOST's own cost, which the guest counters cannot see. The viewport's own
				// CPU/GPU render measurement is switched on at the first ask and stays on; the
				// visible/shadow split says whether shadow passes are the multiplier.
				SampleStatedClock();
				Viewport? viewport = GetViewport();
				double renderCpuMs = 0, renderGpuMs = 0;
				double visibleCalls = 0, shadowCalls = 0, visiblePrims = 0, shadowPrims = 0, visibleObjects = 0, shadowObjects = 0;
				if (viewport is not null)
				{
					// The render thread's published sample (Engine_RenderStats): a server getter
					// here would block this thread on the whole render queue. The first ask of a
					// viewport answers zeros; the sample lands a frame later.
					Refii.Core.Engine_RenderStats.Sample render = Refii.Core.Engine_RenderStats.Of(viewport);
					renderCpuMs = render.CpuMs;
					renderGpuMs = render.GpuMs;
					visibleCalls = render.VisibleCalls;
					shadowCalls = render.ShadowCalls;
					visiblePrims = render.VisiblePrims;
					shadowPrims = render.ShadowPrims;
					visibleObjects = render.VisibleObjects;
					shadowObjects = render.ShadowObjects;
				}

				// WHAT ELSE RUNS ON THE MAIN THREAD: node classes with process, physics process or
				// INTERNAL process enabled — Skeleton3D, AnimationPlayer and particles do their
				// per-frame work as internal process, which no callback of ours can time.
				ulong censusNowMs = Time.GetTicksMsec();
				if (_cachedProcessingCensus is null || _cachedSubViewports is null || censusNowMs >= _cachedProcessingCensusUntilMs)
				{
					var freshCensus = new Dictionary<string, int>();
					var freshSubViewports = new List<SubViewport>();
					foreach (Node found in GetTree().Root.FindChildren("*", "", true, false))
					{
						if (found is SubViewport sv)
						{
							freshSubViewports.Add(sv);
						}

						string kind = found.IsProcessingInternal() ? "internal:" : found.IsProcessing() ? "process:" : found.IsPhysicsProcessing() ? "physics:" : string.Empty;
						if (kind.Length == 0)
						{
							continue;
						}

						string key = kind + found.GetType().Name;
						freshCensus[key] = freshCensus.TryGetValue(key, out int have) ? have + 1 : 1;
					}
					_cachedProcessingCensus = freshCensus;
					_cachedSubViewports = freshSubViewports;
					_cachedProcessingCensusUntilMs = censusNowMs + 2000;
				}
				Dictionary<string, int> census = _cachedProcessingCensus;

				// EVERY viewport, because the process total is far more than the main one draws.
				var viewports = new List<object?>();
				foreach (SubViewport sub in _cachedSubViewports)
				{
					if (!GodotObject.IsInstanceValid(sub) || !sub.IsInsideTree())
					{
						continue;
					}

					Refii.Core.Engine_RenderStats.Sample subRender = Refii.Core.Engine_RenderStats.Of(sub);
					viewports.Add(new Dictionary<string, object?>
					{
						["path"] = sub.GetPath().ToString(),
						["size"] = $"{sub.Size.X}x{sub.Size.Y}",
						["update"] = sub.RenderTargetUpdateMode.ToString(),
						["own3d"] = sub.OwnWorld3D,
						["cpuMs"] = subRender.CpuMs,
						["gpuMs"] = subRender.GpuMs,
						["visibleCalls"] = subRender.VisibleCalls,
						["visiblePrims"] = subRender.VisiblePrims,
						["shadowCalls"] = subRender.ShadowCalls,
						["shadowPrims"] = subRender.ShadowPrims,
					});
				}

				var lights = new List<Light3D>();
				Refii.Lighting.Engine_LevelLights.SnapshotLights(lights);
				int shadowLights = lights.Count(l => l.ShadowEnabled);
				Refii.Host.HostBridge.UnpackTimeBaseWatchdog(
					_timeBase?.Watchdog ?? 0UL,
					out ulong timeBaseWatchdogLapses,
					out ulong timeBasePendingSteps,
					out bool timeBaseStepInFlight);

				return Ok(new Dictionary<string, object?>
				{
					// The level the numbers below belong to, as the guest states it.
					["level"] = GameWhereabouts.Current is { } where
						? $"{where.DisplayName} ({where.LevelKey})"
						: "none",

					// The last load, from leaving the front end: marks (s), frames split into our
					// process time and the engine's (sync/draw), each streamed kind's items and
					// time, and the server syncs our code made meanwhile; `atRelease` holds the
					// same totals at the hold's release (Engine_LoadTimeline).
					["load"] = Refii.Core.Engine_LoadTimeline.Report(),
					// Every value-returning RenderingServer call our code made since start, by
					// site: count and wait, all threads and main (Engine_ServerSyncs).
					["serverSyncs"] = Refii.Core.Engine_ServerSyncs.Report(),
					// ShaderMaterial copies made without the server, and Godot duplicates taken
					// instead (Engine_MaterialCopy; the latter are MaterialDuplicate above), with
					// each fallback's reason, caller (file.member) and the uniform that decided it.
					["materialCopies"] = Refii.Models.Engine_MaterialCopy.Report(),

					// Paths that render a surface white. Run totals; nonzero means the path fired.
					["whitePaths"] = new Dictionary<string, object?>
					{
						["texDecodeFailures"] = Refii.Textures.Engine_TexLibrary.FailedDecodes,
						["materialsMissingAlbedo"] = Refii.Models.Engine_MdlMaterial.MaterialsMissingAlbedo,
						["bakeVerticesWithoutSource"] = Refii.Lighting.Engine_LevelBakedLighting.VertexBakeUnsourced,
					},
					// Placements whose stated distances were unusable and kept the screen-size cull.
					["propRanges"] = new Dictionary<string, object?>
					{
						["unstated"] = Refii.Level.Engine_PropDrawRange.Unstated,
						["treeShadowReach"] = Refii.Level.Engine_PropDrawRange.TreeShadowStated ? "stated" : "full",
					},
					// A readback is a GPU stall on the render thread; this should stay at zero.
					["surfaceArrays"] = new Dictionary<string, object?>
					{
						["gpuReadbacks"] = Refii.Models.Engine_MdlSurfaceArrays.Readbacks,
						["recordedMB"] = System.Math.Round(Refii.Models.Engine_MdlSurfaceArrays.RecordedBytes / 1048576.0, 1),
					},
					["phases"] = Refii.Core.Engine_FramePhases.Report(),
					["processing"] = census,
					["feedFramesSkipped"] = GameEntities.FeedFramesSkipped,
					["fxFrozen"] = Refii.Fx.FableFx.FrozenThisFrame,
					["poses"] = new Dictionary<string, object?>
					{
						["heldLocal"] = Refii.Entities.Engine_EntitySkeleton.LocalPosesHeld,
						["stuckLocalRigs"] = Refii.Entities.Engine_EntitySkeleton.LocalStuckRigs,
						["mixedHeld"] = Refii.Entities.Engine_EntitySkeleton.MixedPosesHeld,
						["flagLocalDataModel"] = Refii.Entities.Engine_EntitySkeleton.FlagLocalDataModel,
						["jumps"] = Refii.Entities.Engine_EntitySkeleton.PoseJumps,
						["changed"] = Refii.Entities.Engine_EntitySkeleton.PoseChanges,
						["applied"] = Refii.Entities.Engine_EntitySkeleton.PoseApplies,
						["implausible"] = Refii.Entities.Engine_EntitySkeleton.ImplausiblePoses,
						["torn"] = Refii.Entities.Engine_EntitySkeleton.TornPoses,
						["countMismatch"] = Refii.Entities.Engine_EntitySkeleton.CountMismatch,
						["neverPosed"] = Refii.Entities.Engine_EntitySkeleton.NeverPosed,
						["mostActive"] = GameEntities.Current?.MostActiveRigs(),
						["highlighted"] = Refii.Entities.Engine_EntityHighlight.HighlightedThisFrame,
						["highlightPassChanges"] = Refii.Entities.Engine_EntityHighlight.PassChanges,

						// Three separate failure causes: the guest states nothing (words stay 000000FF), the
						// layer never resolved a render-device texture (`layerUsable` false, so the post chain has
						// nothing to blur), or the width never arrived (`widthTexels` 0: the Peek of
						// dword_83318558 came back empty).
						["highlightLayerUsable"] = Refii.Entities.Engine_EntityHighlightLayer.Current?.Usable,
						["highlightLayerPresent"] = Refii.Entities.Engine_EntityHighlightLayer.Present,
						["highlightLayerSize"] =
							Refii.Entities.Engine_EntityHighlightLayer.Current is { } hl
								? $"{hl.Size.X}x{hl.Size.Y}"
								: null,
						["highlightWidthTexels"] = Refii.Entities.Engine_EntityHighlightLayer.WidthTexels,
						["highlightWordsSeen"] = string.Join(",",
							System.Linq.Enumerable.Select(
								Refii.Entities.Engine_EntityHighlight.SeenWords, w => w.ToString("X8"))),
						["attachmentsPlaced"] = Refii.Entities.Engine_EntityAttachments.Placed,
						["attachmentsUnresolved"] = Refii.Entities.Engine_EntityAttachments.Unresolved,
						["attachmentsOnStated"] = GameEntities.AttachedOnStated,
						["attachmentsResolves"] = Refii.Entities.Engine_EntityAttachments.Resolves,
						["clipDrivenFrames"] = Refii.Animations.Engine_ClipAnimator.ClipDrivenFrames,
						["runnerFlips"] = Refii.Animations.Engine_ClipAnimator.RunnerFlips,
						["shareSteps"] = Refii.Animations.Engine_ClipAnimator.ShareSteps,
						["largestShareStep"] = Refii.Animations.Engine_ClipAnimator.LargestShareStep,
						// Why a rig is not clip-driven, by cause. `noBit`/`unresolved` climbing means this walk
						// misses controllers the guest has; `empty` climbing means the guest has nothing to play.
						["animNoComponentBit"] = Refii.Host.HostBridge.AnimWalkStats()?.NoBit,
						["animComponentUnresolved"] = Refii.Host.HostBridge.AnimWalkStats()?.Unresolved,
						["animControllerFound"] = Refii.Host.HostBridge.AnimWalkStats()?.Found,
						["animLinked"] = Refii.Host.HostBridge.AnimWalkStats()?.Linked,
						["animControllerEmpty"] = Refii.Host.HostBridge.AnimWalkStats()?.Empty,
						["heldNoPrimary"] = Refii.Animations.Engine_ClipAnimator.HeldNoPrimaryFrames,
						["fallbackUnknownClip"] = Refii.Animations.Engine_ClipAnimator.FallbackUnknownClip,
						["fallbackProcedural"] = Refii.Animations.Engine_ClipAnimator.FallbackProcedural,
						["fallbackRagdoll"] = Refii.Animations.Engine_ClipAnimator.FallbackRagdoll,
						// Where the pose blend's alpha lands. All at zero or one means rigs are stepping between
						// poses; a healthy blend is overwhelmingly `alphaBetween`.
						["alphaAtZero"] = Refii.Entities.Engine_EntitySkeleton.AlphaAtZero,
						["alphaBetween"] = Refii.Entities.Engine_EntitySkeleton.AlphaBetween,
						["alphaAtOne"] = Refii.Entities.Engine_EntitySkeleton.AlphaAtOne,
						["blendedPoses"] = Refii.Entities.Engine_EntitySkeleton.BlendedPoses,
						["heldPoses"] = Refii.Entities.Engine_EntitySkeleton.HeldPoses,
						["fallbackNoLeaves"] = Refii.Animations.Engine_ClipAnimator.FallbackNoLeaves,
					},
					["shadowReach"] = new Dictionary<string, object?>
					{
						["suppressed"] = Refii.Lighting.Engine_ShadowReach.Suppressed,
						["considered"] = Refii.Lighting.Engine_ShadowReach.Considered,
					},
					["skippedBoneWrites"] = Refii.Animations.Engine_ClipAnimator.SkippedBoneWrites,
					["boneWrites"] = Refii.Animations.Engine_ClipAnimator.BoneWrites,
					["skeletonsWritten"] = Refii.Animations.Engine_ClipAnimator.SkeletonsWritten,
					["flickerUpdates"] = Refii.Lighting.Engine_LightFlicker.Updates,
					// Under any vsync mode (mailbox included) the frame rate is the panel's. `vsync` is the
					// mode actually in force: the driver falls back to plain vsync where it has no mailbox.
					["vsync"] = DisplayServer.WindowGetVsyncMode((int)DisplayServer.MainWindowId).ToString(),
					["maxFps"] = Engine.MaxFps,
					["refreshHz"] = DisplayServer.ScreenGetRefreshRate(
						DisplayServer.WindowGetCurrentScreen((int)DisplayServer.MainWindowId)),
					["frameSetupCpuMs"] = RenderingServer.GetFrameSetupTimeCpu(),
					// NaN on the first ask after a restart; JSON has no spelling for it.
					["renderThreadBusy"] = Refii.Core.Engine_RenderStats.RenderThreadBusy() is var busy &&
						double.IsFinite(busy) ? busy : null,
					["viewports"] = viewports,
					["renderCpuMs"] = renderCpuMs,
					["renderGpuMs"] = renderGpuMs,
					["visible"] = new Dictionary<string, object?> { ["drawCalls"] = visibleCalls, ["primitives"] = visiblePrims, ["objects"] = visibleObjects },
					["shadow"] = new Dictionary<string, object?> { ["drawCalls"] = shadowCalls, ["primitives"] = shadowPrims, ["objects"] = shadowObjects },
					["placedLights"] = lights.Count,
					["shadowLights"] = shadowLights,
					// Pipelines compiled since launch. A specialization compile means a surface drew with the
					// ubershader for the frames it took; a draw-time compile stalled the frame.
					["pipelines"] = new Dictionary<string, object?>
					{
						["canvas"] = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsCanvas),
						["mesh"] = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh),
						["surface"] = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface),
						["draw"] = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw),
						["specialization"] = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization),
					},
					["mdlLods"] = new Dictionary<string, object?>
					{
						["surfacesWithLods"] = Refii.Models.Engine_MdlMesh.SurfacesWithLods,
						["terrainSurfacesWithLods"] = Refii.Level.Engine_LevelMesh.SurfacesWithLods,
						["surfacesWithoutLods"] = Refii.Models.Engine_MdlMesh.SurfacesWithoutLods,
						["surfacesWithGeneratedLods"] = Refii.Models.Engine_MdlAutoLod.Generated,
						["levelsAttached"] = Refii.Models.Engine_MdlMesh.LodLevelsAttached,
						["levelsDropped"] = Refii.Models.Engine_MdlMesh.LodLevelsDropped,
					},
					["meshCompression"] = Refii.AndroidRuntime.AndroidMeshCompression.Stats(),
					["displacement"] = Refii.AndroidRuntime.AndroidDisplacementGate.Describe((GameEntities.Current?.GetViewport() ?? GetViewport())?.GetCamera3D()),
					["fps"] = Performance.GetMonitor(Performance.Monitor.TimeFps),
					// The frames that froze, each with the phases in it. Per-phase peaks decay independently,
					// so two peaking in one report can be seconds apart; this is one frame, whole.
					["hitches"] = Hitches(),
					// The nested-bank cache: what it holds, and whether it is thrashing.
					// `evictions` near zero with a full `residentMB` means the budget can come
					// down and the heap with it; evictions climbing means it is already too
					// small and lowering it would trade heap for allocation rate.
					["bankCache"] = new Dictionary<string, object?>
					{
						["residentMB"] =
							System.Math.Round(Refii.Bnk.BnkFileSystem.NestedResidentBytes / 1048576.0, 1),
						["loads"] = Refii.Bnk.BnkFileSystem.NestedLoads,
						["evictions"] = Refii.Bnk.BnkFileSystem.NestedEvictions,
					},
					// What we wrote into the scene, by source: every transform / bounds write is a
					// Godot instance update, each of which dirties the paired lights' shadows and may
					// re-pair. `perFrame` is a decaying average; `lastFrame` the frame just closed.
					["instanceUpdates"] = Refii.Core.Engine_InstanceUpdates.Report(),
					// The shadow proxies' own skeletons: how many stand, and how often they are
					// mirrored on guest frames versus every frame (the principals).
					["shadowSkeletons"] = Refii.Entities.Engine_ShadowSkeletons.Report(),
					["gc"] = GcReport(),
					["tickClock"] = new Dictionary<string, object?>
					{
						// World playback rate. The draw point is `(now - origin)/period - latency`, so drift in
						// either term plays the world fast or slow. 1.000 is correct; `worst` is the largest
						// single-frame excursion since it was last reset.
						["playbackRate"] = Refii.Core.Engine_TickClock.PlaybackRate,
						// How far behind the newest stated tick the world is drawn: negative is healthy (near
						// minus the draw latency), positive is starvation.
						["playheadVsNewest"] = Refii.Core.Engine_TickClock.PlayheadVsNewest,
						// The draw points: body and camera at `playhead`, rigs at `posePlayhead`. `statedStep` is
						// the guest's own HF step phase: which half-tick the newest pose represents.
						["playhead"] = Refii.Core.Engine_TickClock.Playhead,
						["pairAlpha"] = Refii.Core.Engine_TickClock.PairAlpha,
						// Drawn passes; should climb at the frame rate (ApplyDrawn runs every frame).
						["drawnFrames"] = Refii.Entities.Engine_DrawnFrame.Frames,
						["drawnAlpha"] = Refii.Entities.Engine_DrawnFrame.Alpha,
						["drawnPeriodTicks"] = Refii.Entities.Engine_DrawnFrame.Playhead.PeriodRatio,
						["pairAlphaAnchored"] = Refii.Core.Engine_TickClock.PairAlphaAnchored,
						["hostPhase"] = Refii.Core.Engine_TickClock.HostPhase,
						["useHostPhase"] = Refii.Core.Engine_TickClock.UseHostPhase,
						["pairExtrapolated"] = Refii.Core.Engine_TickClock.PairExtrapolatedFrames,
						["pairHeld"] = Refii.Core.Engine_TickClock.PairHeldFrames,
						["pairFloored"] = Refii.Core.Engine_TickClock.Pair.Floored,
						["pairWorstEarly"] = Refii.Core.Engine_TickClock.Pair.WorstEarly,
						["swapIntervals"] = string.Join(",", Refii.Entities.Engine_DrawnFrame.SwapIntervals),
						["pairPeriodTicks"] = Refii.Core.Engine_TickClock.PairPeriodTicks,

						["guestGameTick"] = Refii.Core.Engine_TickClock.GuestGameTick,
						["statedStep"] = _statedClock is { } sc
							? Refii.Core.Engine_TickClock.StatedStepPhase(sc.Seconds) : -1,
						["statedTicks"] = _statedClock is { } st
							? Refii.Core.Engine_TickClock.StatedTicks(st.Seconds) : (double?)null,
						["clockFit"] = Refii.Core.Engine_TickClock.Disciplined,
						["clockHost"] = Refii.Core.Engine_TickClock.HostOwnsClock,
						["clockHostLive"] = Refii.Core.Engine_TickClock.HostClockLive,
						// Ties the host's granted-step count (from zero) to the guest's counter, which resets on
						// world teardown. Not settling means the playhead is in the wrong units.
						["clockHostOffset"] = Refii.Core.Engine_TickClock.HostTickOffset,
						// A snap teleports the playhead: past `HostOffsetSnapTicks` the alignment adopts the raw
						// difference outright. Right for a world reset or save load; climbing during play is
						// jitter. (`clockRestarts` counts the guest's counter going backwards, not this.)
						// The sample stamp against the guest's true game tick: both non-zero means the stamp
						// counter is not a tick count and what is drawn is being re-timed by the guest's own gate.
						["stampLostTicks"] = Refii.Core.Engine_TickClock.StampLostTicks,
						["stampAheadTicks"] = Refii.Core.Engine_TickClock.StampAheadTicks,
						["clockOffsetSnaps"] = Refii.Core.Engine_TickClock.HostOffsetSnaps,
						["clockOffsetSnapWorst"] = Refii.Core.Engine_TickClock.HostOffsetSnapWorst,
						// Lead should hover under 1 with `withheld` flicking true at the ceiling.
						// Climbing with nothing withheld means the closed loop is not closing.
						["hostGrantLead"] = Refii.Core.Engine_TickClock.HostGrantLead,
						["hostGrantWithheld"] = Refii.Core.Engine_TickClock.HostGrantWithheld,
						["clockStated"] = Refii.Core.Engine_TickClock.UseStatedClock,
						// `pacedCapped` counts reads held back: ticks the guest would otherwise have discarded.
						// `timeBaseWatchdog` counts lapses where no grant arrived and time resumed on its own; it
						// should stay flat while the game is stepping.
						["timeBase"] = _timeBase?.Mode.ToString(),
						["timeBaseGranted"] = (_timeBase?.Granted ?? 0UL) & 0xFFFFFFFFUL,
						// Share of guest clock reads that took the time-base lock, in parts per thousand. Every
						// `mftb` the guest executes comes through there and its loop spins on it; near zero means
						// the lock-free fast path is carrying it.
						["timeBaseLockedPerMille"] = (_timeBase?.Granted ?? 0UL) >> 32,
						// Packed pairs: the low half is the paced-mode counter, the high half the
						// driven-mode one. `timeBaseDropped` rising means the guest cannot
						// consume the rate it is granted — a performance problem, not a clock
						// one. `timeBasePending` not draining means the same thing earlier.
						["timeBaseCapped"] = (_timeBase?.Capped ?? 0UL) & 0xFFFFFFFFUL,
						["timeBaseDropped"] = (_timeBase?.Capped ?? 0UL) >> 32,
						["timeBaseWatchdog"] = timeBaseWatchdogLapses,
						["timeBasePending"] = timeBasePendingSteps,
						["timeBaseStepInFlight"] = timeBaseStepInFlight,
						["clockStatedLive"] = Refii.Core.Engine_TickClock.StatedLive,
						// The guest's own clock, `game+0x110`, stored at 0x82276F18. `statedHz` is its measured
						// advance: 15 while the guest keeps up, lower under load, because the loop advances one
						// step and re-bases (0x82276FA4) rather than catching up.
						["simStepsSeen"] = SimStepsSeen,
						["simRebasedSteps"] = SimRebasedSteps,
						["simSkippedSteps"] = SimSkippedSteps,
						["simRebaseWorstSteps"] = SimRebaseWorstSteps,
						["statedSeconds"] = _statedClock?.Seconds,
						["statedTick"] = _statedClock?.Tick,
						["statedHz"] = _statedHz,
						["periodUsec"] = Refii.Core.Engine_TickClock.PeriodUsec,
						["fittedHz"] = 1e6 / System.Math.Max(Refii.Core.Engine_TickClock.PeriodUsec, 1.0),
						["playbackRateWorst"] = Refii.Core.Engine_TickClock.PlaybackRateWorst,
						["drawLatencyTicks"] = Refii.Core.Engine_TickClock.DrawLatencyTicks,
						["frameTicks"] = Refii.Core.Engine_TickClock.FrameTicks,
						["slackTicks"] = Refii.Core.Engine_TickClock.SlackMarginTicks,
						["sampled"] = Refii.Core.Engine_TickClock.Sampled,
						["pin"] = Refii.Core.Engine_TickClock.Pin,
						["starved"] = Refii.Core.Engine_TickClock.Starved,
						["samples"] = Refii.Core.Engine_TickClock.Samples,
						["restarts"] = Refii.Core.Engine_TickClock.Restarts,
					},
					["guestFrame"] = new Dictionary<string, object?>
					{
						["frame"] = Refii.Core.Engine_GuestFrame.Frame,
						["unchangedHostFrames"] = Refii.Core.Engine_GuestFrame.Unchanged,
					},
					["entityWalk"] = new Dictionary<string, object?>
					{
						["unchangedSkipped"] = GameEntities.UnchangedSkipped,
						// The last walk's entities answered from the skip index, and the verdicts it holds.
						["skipIndex"] = GameEntities.SkipIndexEnabled,
						["skipIndexHits"] = GameEntities.SkipIndexHits,
						["skipIndexHeld"] = GameEntities.SkipIndexHeld,
						// The last walk's far rigs (beyond poseFullRate) written, and those in view held.
						["farRigPoseHz"] = GameEntities.FarRigPoseHz,
						["farRigsWritten"] = GameEntities.FarRigsWritten,
						["farRigsHeld"] = GameEntities.FarRigsHeld,
						// The last walk's rows: entities, rows posed, rigs written, rows past the
						// prologue; the last timed walk's rows past the skip index by kind (their time is
						// the "ent.rows.*" frame phases); rigs left off screen (android_offscreen_rig_walks).
						["entities"] = GameEntities.LastWalkEntities,
						["posed"] = GameEntities.LastWalkPosed,
						["posedWritten"] = GameEntities.LastWalkPosedWritten,
						["pastPrologue"] = GameEntities.LastWalkPastPrologue,
						["rowsCharacter"] = GameEntities.TimedRowCounts[0],
						["rowsRigProp"] = GameEntities.TimedRowCounts[1],
						["rowsChanged"] = GameEntities.TimedRowCounts[2],
						["rowsStill"] = GameEntities.TimedRowCounts[3],
						// The still rows by why: due on the still re-check, no node, a building on its
						// full prologue while the reports count it, the rest. Then the last walk's rigged
						// props and buildings held by the index (android_skip_index_rig_props/_buildings)
						// and the verdicts parked at the end of a full-path row instead of the walk after.
						["rowsStillDue"] = GameEntities.TimedStillRows[0],
						["rowsStillNodeless"] = GameEntities.TimedStillRows[1],
						["rowsStillBuilding"] = GameEntities.TimedStillRows[2],
						["rowsStillOther"] = GameEntities.TimedStillRows[3],
						["rigPropsHeld"] = GameEntities.LastWalkRigPropsHeld,
						// The last walk's rigged props by what their row parked or why not, and each verdict found by why it answered or not.
						["rigPropMiss"] = GameEntities.RigPropMissCounts(),
						["buildingsHeld"] = GameEntities.LastWalkBuildingsHeld,
						["reparked"] = GameEntities.LastWalkReparked,
						["offscreenRigWalks"] = GameEntities.OffscreenRigWalks,
						["offscreenDeferred"] = GameEntities.OffscreenDeferred,
						["movers"] = GameEntities.LastWalkMovers,
						["rigs"] = GameEntities.LastWalkRigs,
						["nodes"] = GameEntities.LastWalkNodes,
						["attached"] = GameEntities.LastWalkAttached,
						["creationsDeferred"] = GameEntities.CreationsDeferred,
						["lastWalkChanged"] = Refii.Host.HostBridge.LastWalkChanged,
						["lastWalkCount"] = Refii.Host.HostBridge.LastWalkCount,
						["taken"] = Refii.Host.HostEntityWalk.Taken,
						// `torn`: walks during which the guest advanced a tick, so some entities are one tick
						// ahead of the stamp. `blended`/`liveOnly`: entities drawn from a stated pair versus taken
						// live. Live-only is correct for physics-owned bodies (the guest clears `obj+0xC9` for
						// those); live-only for everything means the pair is not arriving.
						["blended"] = Refii.Game.GameEntities.BlendedEntities,
						["liveOnly"] = Refii.Game.GameEntities.LiveOnlyEntities,
						// `cameraBlended` near zero with `cameraLive` climbing means the camera is not blending
						// and is stepping at the guest's rate.
						["cameraBlended"] = Refii.Game.GameWorld.BlendedCameras,
						// Frames the camera was drawn from its timeline alone because the guest's step was
						// not complete at frame start (its sample is not a statement until it is).
						["cameraCarried"] = Refii.Game.GameWorld.CarriedCameras,
						["cameraLive"] = Refii.Game.GameWorld.LiveOnlyCameras,
						["cameraNonFinite"] = Refii.Game.GameWorld.NonFiniteCameras,
						// The mouse's turn posted and not yet taken by a camera update, drawn ahead of the
						// guest's camera (degrees right; up, bounded, for the ranged aim only); how that
						// camera turns (0 none, 1 orbit, 2 in place, 3 the ranged aim with its reticule) and
						// its pivot (orbit: focus metres in x; aim: the head along level forward, right, up);
						// everything it has turned and pitched for the mouse (degrees, run totals); the aim
						// turned with the view (0 when none); reads that straddled a camera update (run
						// total); whether the mouse is the look device (its camera keeps its heading); and
						// the target switches it flicked a lock to (run total).
						["mouseLookOutstanding"] = Refii.Game.GameWorld.MouseLookOutstanding,
						["mouseLookOutstandingPitch"] = Refii.Game.GameWorld.MouseLookOutstandingPitch,
						["mouseLookMode"] = Refii.Game.GameWorld.MouseLookMode,
						["mouseLookPivot"] = new[]
						{
							Refii.Game.GameWorld.MouseLookPivot.X,
							Refii.Game.GameWorld.MouseLookPivot.Y,
							Refii.Game.GameWorld.MouseLookPivot.Z,
						},
						["mouseLookAbsorbed"] = Refii.Game.GameWorld.MouseLookAbsorbed,
						["mouseLookAbsorbedPitch"] = Refii.Game.GameWorld.MouseLookAbsorbedPitch,
						["mouseLookAim"] = $"0x{Refii.Game.GameWorld.MouseLookAim:X8}",
						["hostAimActive"] = Refii.Game.GameWorld.HostAimActive,
						["hostAimObject"] = $"0x{Refii.Game.GameWorld.HostAimObject:X8}",
						["mouseLookTorn"] = Refii.Game.GameWorld.MouseLookTornReads,
						["mouseLookDevice"] = Refii.Ui.Engine_UiInputMode.KeyboardMouse,
						["mouseLookFlicks"] = Refii.Game.GameWorld.MouseLookFlicks,
						["torn"] = Refii.Host.HostEntityWalk.TornWalks,
						["tornSpanWorst"] = Refii.Host.HostEntityWalk.WalkTickSpanWorst,
						["waited"] = Refii.Host.HostEntityWalk.Waited,
						// The worker's last walk, ms (native walk + decode), off the main thread.
						["walkMs"] = Refii.Host.HostEntityWalk.LastWalkMs,
					},
					["frameMs"] = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0,
					["physicsMs"] =
						Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0,
					["drawCalls"] =
						Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
					["primitives"] =
						Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),
					["videoMemMB"] =
						Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0,
					["textureMemMB"] =
						Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1048576.0,
					["bufferMemMB"] =
						Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed) / 1048576.0,
					["nodes"] = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
					["staticMemMB"] =
						Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0,
				});
			}

			case "input-record":
			{
				string what = root.TryGetProperty("do", out JsonElement d)
					? d.GetString() ?? string.Empty
					: string.Empty;
				if (what == "start")
				{
					_recording = new List<PadFrame>();
					return Ok(new Dictionary<string, object?> { ["recording"] = true });
				}

				if (what == "stop")
				{
					List<PadFrame>? tape = _recording;
					_recording = null;
					if (tape is null || tape.Count == 0)
					{
						return new Dictionary<string, object?>
						{
							["ok"] = false, ["error"] = "nothing recorded",
						};
					}

					string path = root.TryGetProperty("path", out JsonElement p) &&
						p.GetString() is { Length: > 0 } given
							? given
							: ProjectSettings.GlobalizePath(
								$"user://logs/pad-{System.DateTime.Now:HHmmss}.jsonl");
					var text = new StringBuilder();
					foreach (PadFrame frame in tape)
					{
						text.AppendLine(JsonSerializer.Serialize(frame, Refii.Core.Engine_JsonContext.Default.PadFrame));
					}

					System.IO.File.WriteAllText(path, text.ToString());
					return Ok(new Dictionary<string, object?>
					{
						["frames"] = tape.Count, ["path"] = path,
					});
				}

				return new Dictionary<string, object?>
				{
					["ok"] = false, ["error"] = "do must be start or stop",
				};
			}

			case "input-replay":
			{
				string path = root.TryGetProperty("path", out JsonElement p)
					? p.GetString() ?? string.Empty
					: string.Empty;
				try
				{
					var frames = new List<PadFrame>();
					foreach (string line in System.IO.File.ReadLines(path))
					{
						if (line.Length != 0)
						{
							frames.Add(JsonSerializer.Deserialize(line, Refii.Core.Engine_JsonContext.Default.PadFrame));
						}
					}

					_replay = frames.ToArray();
					_replayAt = 0;
					return Ok(new Dictionary<string, object?> { ["frames"] = frames.Count });
				}
				catch (System.Exception error)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = error.Message,
					};
				}
			}

			case "achievements":
			{
				// The achievement system end to end: `do` = state (default) | list | unlock |
				// lock | reset | toast. `unlock` takes the path the guest's own write takes
				// (store, file, toast queue, the set stated down to the guest's enumerator);
				// `toast` shows the presentation alone and unlocks nothing.
				string verb = root.TryGetProperty("do", out JsonElement dv)
					? dv.GetString() ?? "state"
					: "state";
				uint id = root.TryGetProperty("id", out JsonElement iv) && iv.ValueKind == JsonValueKind.Number
					? iv.GetUInt32()
					: 0u;
				Refii.Ui.Engine_AchievementToast? toast = Refii.Ui.Engine_AchievementToast.Current;
				var reply = new Dictionary<string, object?>();
				switch (verb)
				{
					case "state":
					case "list":
						break;

					case "unlock":
					case "toast":
						if (Refii.Achievements.Engine_Achievements.Definition(id) is null)
						{
							return new Dictionary<string, object?>
							{
								["ok"] = false,
								["error"] = $"{id} is not an achievement this title defines — `do: list` names them",
							};
						}

						if (verb == "unlock")
						{
							// NOT "unlocked": the state merged in below carries that key as the COUNT,
							// and it overwrote this answer — the first test read `unlocked: 3`.
							reply["newlyUnlocked"] = Refii.Achievements.Engine_Achievements.DebugUnlock(id);
							reply["note"] = (bool)reply["newlyUnlocked"]!
								? "unlocked — the toast is queued and the store is written"
								: "already unlocked — `do: lock` first to see it unlock again";
						}
						else
						{
							toast?.ShowNow(id, now: true);
							reply["toasting"] = toast is not null;
						}

						break;

					case "dial":
					{
						// One of the toast's look dials — see Engine_AchievementToast.Dials.
						string dial = root.TryGetProperty("name", out JsonElement dn)
							? dn.GetString() ?? string.Empty
							: string.Empty;
						if (!root.TryGetProperty("value", out JsonElement dval) ||
							dval.ValueKind != JsonValueKind.Number ||
							!Refii.Ui.Engine_AchievementToast.SetDial(dial, dval.GetSingle()))
						{
							return new Dictionary<string, object?>
							{
								["ok"] = false,
								["error"] = $"dial needs a known name and a number — got '{dial}'",
								["dials"] = Refii.Ui.Engine_AchievementToast.Dials,
							};
						}

						break;
					}

					case "lock":
						if (id == 0u)
						{
							return new Dictionary<string, object?>
							{
								["ok"] = false, ["error"] = "lock needs an id — `do: reset` re-locks everything",
							};
						}

						reply["relocked"] = Refii.Achievements.Engine_Achievements.DebugLock(id);
						break;

					case "reset":
						reply["relocked"] = Refii.Achievements.Engine_Achievements.DebugLock(0u);
						break;

					default:
						return new Dictionary<string, object?>
						{
							["ok"] = false,
							["error"] = $"unknown verb '{verb}' — state, list, unlock, lock, reset, toast or dial",
						};
				}

				foreach ((string key, object? value) in
					Refii.Achievements.Engine_Achievements.Describe(rows: verb == "list"))
				{
					reply[key] = value;
				}

				reply["dials"] = Refii.Ui.Engine_AchievementToast.Dials;
				reply["toast"] = toast?.State ?? "no toast node (no game window)";
				reply["toastsShown"] = toast?.Shown ?? 0;
				reply["lastToast"] = toast?.LastShown;
				return Ok(reply);
			}

			case "elements":
			{
				string match = root.TryGetProperty("match", out JsonElement m)
					? m.GetString() ?? string.Empty
					: string.Empty;
				int limit = root.TryGetProperty("limit", out JsonElement l) ? l.GetInt32() : 40;
				if (match.Length == 0)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = "elements needs a match",
					};
				}

				// The RESOLVED state of matching elements across every live book — active,
				// drawn, opacity, node position. The ground truth a bound-but-ineffective
				// channel write is caught against.
				return Ok(new Dictionary<string, object?>
				{
					["elements"] = Engine_UiOwnedBook.DescribeLive(match, limit),
				});
			}

			case "log":
			{
				int lines = root.TryGetProperty("lines", out JsonElement n) ? n.GetInt32() : 40;
				string match = root.TryGetProperty("match", out JsonElement m)
					? m.GetString() ?? string.Empty
					: string.Empty;
				return Ok(Tail(lines, match));
			}

			case "hud":
			{
				string match = root.TryGetProperty("match", out JsonElement hm) ? hm.GetString() ?? string.Empty : string.Empty;
				Refii.Ui.Engine_UiModel.SpellAssignState spells = Refii.Host.HostBridge.GetSpellAssign();
				// The HUD walk's time, in milliseconds since the last read: the sections of
				// `Engine_UiCloneBinding.Apply` summed across every binding. Reading zeroes them,
				// and the first read starts them: the first reply's times are zero.
				double perTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				var bindProfile = new Dictionary<string, object?>
				{
					["calls"] = Refii.Ui.Engine_UiCloneBinding.AppliedCalls,
					["nodes"] = Refii.Ui.Engine_UiCloneBinding.AppliedNodes,
					["transformMs"] = Refii.Ui.Engine_UiCloneBinding.TicksTransform * perTick,
					["drawMs"] = Refii.Ui.Engine_UiCloneBinding.TicksDraw * perTick,
					["materialMs"] = Refii.Ui.Engine_UiCloneBinding.TicksMaterial * perTick,
					["textureMs"] = Refii.Ui.Engine_UiCloneBinding.TicksTexture * perTick,
					["masksMs"] = Refii.Ui.Engine_UiCloneBinding.TicksMasks * perTick,
				};
				Refii.Ui.Engine_UiCloneBinding.ResetProfile();

				// The view's own sections around the binding.
				var walkProfile = new Dictionary<string, object?>
				{
					["walks"] = Refii.Ui.Engine_HudView.Walks,
					["pieces"] = Refii.Ui.Engine_HudView.WalkedPieces,
					["valuesMs"] = Refii.Ui.Engine_HudView.TicksValues * perTick,
					["gatesMs"] = Refii.Ui.Engine_HudView.TicksGates * perTick,
					["applyMs"] = Refii.Ui.Engine_HudView.TicksApply * perTick,
					["pipMs"] = Refii.Ui.Engine_HudView.TicksPip * perTick,
					["textsMs"] = Refii.Ui.Engine_HudView.TicksTexts * perTick,
					["placeMs"] = Refii.Ui.Engine_HudView.TicksPlace * perTick,
					["transformMs"] = Refii.Ui.Engine_HudView.TicksTransform * perTick,
					["driveMs"] = Refii.Ui.Engine_HudView.TicksDrive * perTick,
				};
				Refii.Ui.Engine_HudView.ResetWalkProfile();

				// The guest's stated health meters, 0..100: the bar's fill and its max track.
				Refii.Ui.Engine_HudValues? hudValues = Refii.Ui.Engine_HudView.Current?.Values;
				var healthMeters = new Dictionary<string, object?>();
				foreach (string name in new[] { "PlayerHealth", "PlayerHealthMax", "CoopHealth", "CoopHealthMax" })
				{
					healthMeters[name] = hudValues?.Meter(Refii.Ui.Engine_HudValues.Handle.For(name));
				}

				return Ok(new Dictionary<string, object?>
				{
					["healthMeters"] = healthMeters,
					["bindProfile"] = bindProfile,
					["walkProfile"] = walkProfile,
					["pieces"] = Refii.Ui.Engine_HudView.Current?.Describe(match),

					// Whether the pip marker exists. The pip is a `PrimDescName` inside the meter element in
					// the `.bgf`, which our builder makes no node from.
					["pipAnchorsFound"] = string.Join(",", Refii.Ui.Engine_HudView.PipAnchorsFound),
					["pipAnchorsMissing"] = string.Join(",", Refii.Ui.Engine_HudView.PipAnchorsMissing),

					// The guest's own meter, to split "not running one" from "not drawing it". The anchor
					// sets above are a running record, never cleared.
					["meter"] = Meter(),
					["spell"] = new Dictionary<string, object?>
					{
						["known"] = spells.Known,
						["assigned"] = spells.Assigned,
						["offered"] = spells.Offered,
						["abilitiesMode"] = spells.AbilitiesMode,
						["wheelOpen"] = spells.WheelOpen,
					},
				});
			}

			case "watch":
			{
				// One rig's drawn motion, frame by frame: `{"op":"watch","address":"4286C110"}` pins it,
				// `{"op":"watch"}` reads the ring back. `moved` is the total distance every bone on the
				// rig travelled between two consecutive drawn frames, beside the alpha and crossfade ratio.
				if (root.TryGetProperty("address", out JsonElement wantWatch))
				{
					string text = wantWatch.GetString() ?? string.Empty;
					if (text.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
					{
						text = text[2..];
					}

					Refii.Animations.Engine_ClipAnimator.WatchAddress =
						uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
							System.Globalization.CultureInfo.InvariantCulture, out uint picked) ? picked : 0u;
				}

				return Ok(new Dictionary<string, object?>
				{
					["watching"] = $"{Refii.Animations.Engine_ClipAnimator.WatchAddress:X8}",
					["trace"] = Refii.Animations.Engine_ClipAnimator.WatchTrace(),
				});
			}

			case "alpha":
			{
				// The pose-blended rigs' alpha as a per-frame sequence; for clip-driven rigs, how
				// often a playhead re-timed and how often its correction ran at the speed limit
				// (Engine_ClipPlayhead) — both should stay near zero while a clip plays on.
				return Ok(new Dictionary<string, object?>
				{
					["trace"] = Refii.Entities.Engine_EntitySkeleton.AlphaTrace(),
					["clipRestarts"] = Refii.Animations.Engine_ClipPlayhead.Restarts,
					["clipSaturated"] = Refii.Animations.Engine_ClipPlayhead.Saturated,
				});
			}

			case "camera":
			{
				GameWorld? world = GetTree().Root.FindChild("GameWorld", true, false) as GameWorld;
				if (root.TryGetProperty("trace", out JsonElement ct) && ct.ValueKind == JsonValueKind.Number)
				{
					world?.BeginCameraTrace(ct.GetInt32());
					return Ok(new Dictionary<string, object?> { ["tracing"] = world is not null });
				}

				// The camera, bodies and live effects share one timeline type (Engine_TickTimeline):
				// how often any of them re-placed past the snap bounds, and how often a correction
				// ran at its speed limit. Both should stay near zero while things move normally.
				Camera3D? drawn = GetViewport().GetCamera3D();
				return Ok(new Dictionary<string, object?>
				{
					["position"] = drawn is null ? null : new[] { drawn.GlobalPosition.X, drawn.GlobalPosition.Y, drawn.GlobalPosition.Z },
					["forward"] = drawn is null ? null : new[] { -drawn.GlobalBasis.Z.X, -drawn.GlobalBasis.Z.Y, -drawn.GlobalBasis.Z.Z },
					["up"] = drawn is null ? null : new[] { drawn.GlobalBasis.Y.X, drawn.GlobalBasis.Y.Y, drawn.GlobalBasis.Y.Z },
					["fov"] = drawn?.Fov,
					["trace"] = world?.CameraTraceReport(),
					["timelineSnaps"] = Refii.Core.Engine_TickTimeline.Snaps,
					["timelineSaturated"] = Refii.Core.Engine_TickTimeline.Saturated,
					["pace"] = Refii.Core.Engine_DrawAdvance.Pace,
					// `motion_arrival_fit`: whether it is on, the moving bodies (and camera) it draws
					// now, and the stops it has settled.
					["motionArrivalFit"] = Refii.Core.Engine_TickTimeline.ArrivalFit,
					["arrivalFits"] = Refii.Core.Engine_ArrivalFit.Live,
					["arrivalStops"] = Refii.Core.Engine_ArrivalFit.Stops,
					// The guest's stated pause: whether the drawn world is held this frame, how
					// many frames this hold has lasted, and the run totals.
					["guestPaused"] = Refii.Core.Engine_StatedPause.Latched,
					["pausedFrames"] = Refii.Core.Engine_StatedPause.HeldFrames,
					["pausedFramesTotal"] = Refii.Core.Engine_StatedPause.HeldFramesTotal,
					["pauses"] = Refii.Core.Engine_StatedPause.Pauses,
					// The draw point's latency margin, in ticks: the measured arrival lateness the
					// shared playhead sits behind, and the lateness it is steered toward.
					["marginTicks"] = Refii.Entities.Engine_DrawnFrame.Playhead.MarginTicks,
					["latenessTicks"] = Refii.Entities.Engine_DrawnFrame.Playhead.LatenessTicks,
					["cameraCarried"] = GameWorld.CarriedCameras,
				});
			}

			case "shot":
			{
				string path = root.TryGetProperty("path", out JsonElement p)
					? p.GetString() ?? string.Empty
					: string.Empty;

				// A CLOSE-UP: `addr` frames that entity in a private viewport. Call once with
				// `arm` to queue the render, then again with `path` a frame later to save it.
				if (root.TryGetProperty("addr", out JsonElement ca) && ca.ValueKind == JsonValueKind.String)
				{
					uint who = System.Convert.ToUInt32(ca.GetString(), 16);
					if (root.TryGetProperty("arm", out JsonElement arm) && arm.ValueKind == JsonValueKind.True)
					{
						string headBone = Refii.Core.Engine_TitleGame.Active.Rig.BoneName(Refii.Animations.Engine_TitleRig.RigRole.Head) ?? string.Empty;
						string bone = root.TryGetProperty("bone", out JsonElement bn) ? bn.GetString() ?? headBone : headBone;
						float distance = root.TryGetProperty("distance", out JsonElement di) ? (float)di.GetDouble() : 0.6f;
						int size = root.TryGetProperty("size", out JsonElement sz) ? sz.GetInt32() : 512;
						bool armed = GameEntities.Current?.ArmCloseup(who, bone, distance, size) ?? false;
						return Ok(new Dictionary<string, object?> { ["armed"] = armed });
					}

					Image? image = GameEntities.Current?.CloseupImage();
					if (image is null || path.Length == 0)
					{
						return Ok(new Dictionary<string, object?> { ["error"] = "not armed" });
					}

					System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
					return Ok(new Dictionary<string, object?>
					{
						["path"] = path,
						["saved"] = image.SavePng(path) == Error.Ok,
						["width"] = image.GetWidth(),
					});
				}

				return Ok(Shot(path));
			}

			case "flicker":
			{
				// Write the post-fx flicker trap's frame ring now. Armed by `postfx_flicker_trap`
				// at launch; the write happens on the next rendered frame, so `lastDump` is the
				// PREVIOUS dump on this call and the new one on the next.
				bool armed = Refii.Lighting.Engine_PostFxEffect.TrapArmed;
				if (armed)
				{
					Refii.Lighting.Engine_PostFxEffect.RequestTrapDump();
				}

				return Ok(new Dictionary<string, object?>
				{
					["armed"] = armed,
					["lastDump"] = Refii.Lighting.Engine_PostFxEffect.TrapLastDump,
					["note"] = armed
						? "ring written on the next rendered frame; call again for its path"
						: "not armed: set postfx_flicker_trap to a ratio above 1 and relaunch",
				});
			}

			case "press":
			{
				uint mask = root.TryGetProperty("buttons", out JsonElement b)
					? (uint)b.GetInt64()
					: 0u;
				int frames = root.TryGetProperty("frames", out JsonElement f) ? f.GetInt32() : 8;
				int pad = root.TryGetProperty("pad", out JsonElement p) ? p.GetInt32() : 0;
				if (pad is < 0 or > 3)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false,
						["error"] = $"pad {pad} is not 0..3",
					};
				}

				if (root.TryGetProperty("joypad", out JsonElement jp) && jp.ValueKind == JsonValueKind.True)
				{
					return JoypadPress(root, mask, frames, pad);
				}

				_pressPad = pad;
				_debugPads |= 1 << pad;
				_pressed = mask;
				_pressFrames = frames < 1 ? 1 : frames;

				// Sticks and triggers ride the same hold: -1..1 floats, XInput-scaled here.
				static int Scale(JsonElement parent, string name) =>
					parent.TryGetProperty(name, out JsonElement axis)
						? (int)(Mathf.Clamp((float)axis.GetDouble(), -1f, 1f) * 32767f)
						: 0;
				_stickLX = Scale(root, "lx");
				_stickLY = Scale(root, "ly");
				_stickRX = Scale(root, "rx");
				_stickRY = Scale(root, "ry");
				_trigL = root.TryGetProperty("lt", out JsonElement lt)
					? (uint)(Mathf.Clamp((float)lt.GetDouble(), 0f, 1f) * 255f)
					: 0u;
				_trigR = root.TryGetProperty("rt", out JsonElement rt)
					? (uint)(Mathf.Clamp((float)rt.GetDouble(), 0f, 1f) * 255f)
					: 0u;
				_stickFrames = _stickLX != 0 || _stickLY != 0 || _stickRX != 0 ||
					_stickRY != 0 || _trigL != 0 || _trigR != 0
						? _pressFrames
						: 0;
				return Ok(new Dictionary<string, object?>
				{
					["buttons"] = mask,
					["pad"] = pad,
					["frames"] = _pressFrames,
					["sticks"] = _stickFrames > 0,
				});
			}

			case "input":
				return Ok(new Dictionary<string, object?> { ["keys"] = GameInput.DescribeKeys() });

			case "pads":
			{
				Dictionary<string, object?> pads = GameDebugDummyPad.Answer(
					root.TryGetProperty("do", out JsonElement padsDo) ? padsDo.GetString() : null,
					root.TryGetProperty("pad", out JsonElement padsSeat) ? padsSeat.GetInt32() : null,
					root.TryGetProperty("family", out JsonElement padsFamily) ? padsFamily.GetString() : null);
				return pads.ContainsKey("error") ? pads : Ok(pads);
			}

			case "mouse":
			{
				// THE MOUSE through Godot's own input pipeline, as a player's arrives: a move to a
				// window pixel (or 0..1 of the window), a button held `frames` frames and released by
				// ReleaseHeldMouse, wheel notches as press/release pairs. The device switch
				// (GameInput.DetectDevice), the pointer (Engine_UiPointer) and the play bindings
				// (Input.IsMouseButtonPressed) see what a player's mouse would make.
				_mouseScriptedAt = Time.GetTicksMsec();
				Vector2I window = GetWindow().Size;
				Vector2 from = _mouseAt ?? (Vector2)(DisplayServer.MouseGetPosition() - GetWindow().Position);
				Vector2 at = from;
				bool move = root.TryGetProperty("x", out JsonElement mx) & root.TryGetProperty("y", out JsonElement my);
				if (move)
				{
					bool normalized = root.TryGetProperty("normalized", out JsonElement nz) && nz.ValueKind == JsonValueKind.True;
					(float px, float py) = GameDebugMouse.WindowPoint(
						(float)mx.GetDouble(), (float)my.GetDouble(), normalized, window.X, window.Y);
					at = new Vector2(px, py);
					Input.ParseInputEvent(new InputEventMouseMotion
					{
						Position = at, GlobalPosition = at, Relative = at - from, ScreenRelative = at - from,
						Velocity = (at - from) * 60f, ButtonMask = _mouseMask,
					});
				}

				_mouseAt = at;
				string click = root.TryGetProperty("click", out JsonElement c) ? c.GetString() ?? string.Empty : string.Empty;
				MouseButton button = click switch
				{
					"left" => MouseButton.Left,
					"right" => MouseButton.Right,
					"middle" => MouseButton.Middle,
					_ => MouseButton.None,
				};
				int holdFrames = 0;
				if (button != MouseButton.None)
				{
					if (_heldMouse != MouseButton.None)
					{
						_mouseFrames = 1;
						ReleaseHeldMouse();
					}

					holdFrames = root.TryGetProperty("frames", out JsonElement hf) ? System.Math.Max(1, hf.GetInt32()) : 3;
					_mouseMask |= MaskOf(button);
					Input.ParseInputEvent(new InputEventMouseButton
					{
						Position = at, GlobalPosition = at, ButtonIndex = button, Pressed = true, ButtonMask = _mouseMask,
					});
					_heldMouse = button;
					_mouseFrames = holdFrames;
				}

				int wheel = root.TryGetProperty("wheel", out JsonElement w) ? w.GetInt32() : 0;
				for (int i = 0; i < System.Math.Abs(wheel); ++i)
				{
					MouseButton notch = wheel < 0 ? MouseButton.WheelUp : MouseButton.WheelDown;
					Input.ParseInputEvent(new InputEventMouseButton
					{
						Position = at, GlobalPosition = at, ButtonIndex = notch, Pressed = true, Factor = 1f,
						ButtonMask = _mouseMask | MaskOf(notch),
					});
					Input.ParseInputEvent(new InputEventMouseButton
					{
						Position = at, GlobalPosition = at, ButtonIndex = notch, Pressed = false, Factor = 1f,
						ButtonMask = _mouseMask,
					});
				}

				return Ok(new Dictionary<string, object?>
				{
					["x"] = at.X, ["y"] = at.Y, ["window"] = new[] { window.X, window.Y },
					["moved"] = move, ["click"] = click, ["frames"] = holdFrames, ["wheel"] = wheel,
					["keyboardMouse"] = Engine_UiInputMode.KeyboardMouse,
					["focused"] = GetWindow().HasFocus(),
				});
			}

			case "key":
			{
				// A KEYBOARD key through Godot's own input pipeline, as a player's would arrive: the
				// device switch takes the keyboard, the bindings (key_*) read it down, the prompts
				// turn to keycaps. Held for `frames` frames, then released the same way.
				string name = root.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
				Key key = OS.FindKeycodeFromString(name);
				if (key == Key.None)
				{
					return new Dictionary<string, object?> { ["ok"] = false, ["error"] = $"no key named '{name}'" };
				}

				if (_heldKey != Key.None)
				{
					_keyFrames = 1;
					ReleaseHeldKey();
				}

				int frames = root.TryGetProperty("frames", out JsonElement kf) ? kf.GetInt32() : 8;
				Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
				_heldKey = key;
				_keyFrames = frames < 1 ? 1 : frames;
				return Ok(new Dictionary<string, object?> { ["key"] = OS.GetKeycodeString(key), ["frames"] = _keyFrames });
			}

			default:
				// THE TITLE'S OWN OPS: what only that game has (its co-op stack, its pub).
				if (Refii.Core.Engine_TitleGame.Active.DebugRequest(op, root) is { } titled)
				{
					return Ok(titled);
				}

				return new Dictionary<string, object?>
				{
					["ok"] = false,
					["error"] = $"unknown op '{op}'",
					["titleOps"] = Refii.Core.Engine_TitleGame.Active.DebugOps,
					["ops"] = new[]
					{
						"ping", "state", "rows", "frontend", "elements", "peek", "threads",
						"objects", "perf", "exports", "cvars", "cvar-set", "vfs", "sockets",
						"menu", "action", "call", "lua", "call-result", "poke", "heap", "heapcensus",
						"probe", "guestlog", "mark", "uirecord", "metrics", "input-record", "input-replay",
						"wait", "log", "shot", "flicker", "press", "key", "mouse", "input", "pads", "gdb", "achievements",
						"gpuprofile", "capture", "hang",
					},
				};
		}
	}

	/// <summary>Reads the active drawn camera on the main thread, without camera or input setters.</summary>
	/// <returns>Finite global axes and available active-region/guest mappings, or null without a valid camera.</returns>
	private Dictionary<string, object?>? CaptureCameraTelemetry()
	{
		Camera3D? camera = GetViewport().GetCamera3D();
		if (camera is null || !GodotObject.IsInstanceValid(camera) || !camera.IsInsideTree()) return null;
		Node3D? region = null;
		var sky = Refii.Level.Engine_SkyEnvironmentNode.Current;
		if (sky is not null && GodotObject.IsInstanceValid(sky) && sky.IsInsideTree() &&
			sky.GetParent() is Node3D candidate && GodotObject.IsInstanceValid(candidate) &&
			candidate.IsInsideTree() && candidate.GetParent() is GameWorld world &&
			ReferenceEquals(camera.GetParent(), world)) region = candidate;
		return GameCameraTelemetry.Describe(camera.GlobalTransform, camera.Fov,
			region?.GlobalTransform, usesGuestWorldMap: region is not null);
	}

	/// <summary>The hours the last "timeofday" ask was answered with.</summary>
	private static double? _lastTimeOfDay;

	/// <summary>The mod command that sets the game clock to <paramref name="hours"/> (clamped 0..24); null when not finite.</summary>
	internal static string? TimeOfDayCommand(double hours) => double.IsFinite(hours)
		? "set_time_of_day " + System.Math.Clamp(hours, 0d, 24d).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
		: null;

	/// <summary>Copies render telemetry and represents unavailable floating-point fields as JSON null.</summary>
	/// <remarks>The clock fit is NaN before its first sample and OS-dependent measurements may be
	/// unavailable. Strict JSON rejects NaN and infinity; reporting null preserves that distinction
	/// from a measured zero, although most replies now send them as named literals (ReplyOptions).
	/// Render's fields have scalar values or separately validated finite camera objects, so only
	/// its top-level floats need adaptation here.</remarks>
	/// <param name="source">Borrowed render fields; its values and dictionary remain unchanged.</param>
	/// <returns>A detached scalar dictionary retaining finite values, their types and explicit nulls.</returns>
	internal static Dictionary<string, object?> FiniteRenderTelemetry(
		IReadOnlyDictionary<string, object?> source)
	{
		System.ArgumentNullException.ThrowIfNull(source);
		var snapshot = new Dictionary<string, object?>(source.Count, System.StringComparer.Ordinal);
		foreach ((string key, object? value) in source)
		{
			snapshot[key] = value switch
			{
				double number when !double.IsFinite(number) => null,
				float number when !float.IsFinite(number) => null,
				_ => value,
			};
		}
		return snapshot;
	}

	/// <summary>
	/// Every response carries WHO ANSWERED IT. Without this a client cannot tell a fresh game
	/// from the previous one on the same port, and a stale answer looks exactly like a current
	/// one — the failure this whole handshake exists to prevent.
	/// </summary>
	private static Dictionary<string, object?> SkyReadout()
	{
		Vector3 sun = Refii.Models.Engine_ClassicTheme.SunDirection;
		Vector3 colour = Refii.Models.Engine_ClassicTheme.SunColour;
		Camera3D? camera = ((SceneTree)Engine.GetMainLoop()).Root.GetViewport().GetCamera3D();
		// The guest's axes to Godot's: (-x, z, y).
		float facing = camera is null ? -2f : (-camera.GlobalBasis.Z).Dot(new Vector3(-sun.X, sun.Z, sun.Y));
		float[] visibility = { -1f, -1f };
		if (Refii.Lighting.Engine_ClassicSkyLayers.Visibility?.GetImage() is { } image && image.GetWidth() >= 2)
		{
			visibility[0] = image.GetPixel(0, 0).R;
			visibility[1] = image.GetPixel(1, 0).R;
		}
		float Param(int index) => Refii.Models.Engine_ClassicTheme.Param(index);
		return new Dictionary<string, object?>
		{
			["sunIntensity"] = Param(7),
			["glareIntensity"] = Param(100),
			["glareSize"] = Param(101),
			["discSize"] = Param(92),
			["discIntensity"] = Param(94),
			["sunColour"] = new[] { colour.X, colour.Y, colour.Z },
			["sunDirection"] = new[] { sun.X, sun.Y, sun.Z },
			["facing"] = facing,
			["sunVisibility"] = visibility[0],
			["moonVisibility"] = visibility[1],
			["glareColour"] = new[]
			{
				colour.X * Param(100) * facing * facing * System.MathF.Max(visibility[0], 0f) * System.MathF.Max(visibility[0], 0f),
				colour.Y * Param(100) * facing * facing * System.MathF.Max(visibility[0], 0f) * System.MathF.Max(visibility[0], 0f),
				colour.Z * Param(100) * facing * facing * System.MathF.Max(visibility[0], 0f) * System.MathF.Max(visibility[0], 0f),
			},
			["glareTexture"] = $"{Refii.Models.Engine_ClassicTheme.TopHash(Refii.Models.Engine_ClassicTheme.SlotSunGlare):X8}",
			// Where the camera says the sun is (sign-free, as the halo layer projects) against
			// where the post chain's sprites put it, and the projection those sprites read.
			["sunScreenCamera"] = camera is not null && Refii.Lighting.Engine_PostFxParams.ProjectSun(new Vector3(-sun.X, sun.Z, sun.Y), camera, out Vector2 uv, out _)
				? new[] { uv.X, uv.Y } : null,
			["sunScreenSprites"] = new[] { Refii.Lighting.Engine_PostFxEffect.LastSunDisc.X, Refii.Lighting.Engine_PostFxEffect.LastSunDisc.Y },
			["spriteProjection"] = new[] { Refii.Lighting.Engine_PostFxEffect.LastProjection.X, Refii.Lighting.Engine_PostFxEffect.LastProjection.Y,
				Refii.Lighting.Engine_PostFxEffect.LastProjection.Z, Refii.Lighting.Engine_PostFxEffect.LastProjection.W },
			["cameraFov"] = camera?.Fov ?? -1f,
		};
	}

	private static Dictionary<string, object?> Ok(Dictionary<string, object?> body)
	{
		body["ok"] = true;
		body["pid"] = OS.GetProcessId();
		body["boot"] = BootId;
		body["uptime"] = (int)Uptime.Elapsed.TotalSeconds;
		return body;
	}

	/// <summary>
	/// The stated effect transforms beside what the walk placed for the same instance, so the
	/// two can be compared on one screen. `walk` is our drawn position; `p0..p3` are the raw
	/// lanes the guest handed `sub_832325A8`. Whichever three lanes track `walk` on the effects
	/// the walk gets RIGHT are x, y and z.
	/// </summary>
	private static List<Dictionary<string, object?>> StatedFxRows()
	{
		var rows = new List<Dictionary<string, object?>>();
		var stated = new List<Refii.Host.HostBridge.GuestStatedFx>();
		if (Refii.Host.HostBridge.GetStatedFx(stated) == 0)
		{
			return rows;
		}

		for (int i = 0; i < stated.Count && i < 24; ++i)
		{
			Refii.Host.HostBridge.GuestStatedFx fx = stated[i];
			rows.Add(new Dictionary<string, object?>
			{
				["instance"] = $"{fx.Instance:X8}",
				["seq"] = fx.Sequence,
				["p"] = new[] { fx.P0, fx.P1, fx.P2, fx.P3 },
				["q"] = new[] { fx.Q0, fx.Q1, fx.Q2, fx.Q3 },
				["walk"] = GameFx.Current?.DescribePlacement(fx.Instance),
			});
		}

		return rows;
	}

	/// <summary>The guest's expression meter as the host reads it, plus what the view did with it.</summary>
	private static Dictionary<string, object?> Meter()
	{
		Refii.Host.HostBridge.ExpressionMeter m = Refii.Host.HostBridge.GetExpressionMeter();
		(string Binding, float Degrees)? pip = Refii.Ui.Engine_HudView.Current?.ExpressionPip;
		return new Dictionary<string, object?>
		{
			["known"] = m.Known,
			["on"] = m.On,
			["up"] = m.Up,
			["mode"] = m.Mode,
			["armedA"] = m.ArmedA,
			["armedB"] = m.ArmedB,
			["latched"] = m.Latched,
			["position"] = m.Position,
			["speed"] = m.Speed,
			["band"] = m.Band,
			["pipRunning"] = m.PipRunning,
			["viewPipBinding"] = pip?.Binding,
			["viewPipDegrees"] = pip?.Degrees,
		};
	}

	private static bool HostBridgeUp() => Refii.Host.HostBridge.TryGetAbiVersion() is not null;

	/// <summary>What the guest states about the UI right now — the reading, not an inference.</summary>
	private static Dictionary<string, object?> State()
	{
		var state = new Dictionary<string, object?> { ["guest"] = HostBridgeUp() };

		// Where the player is, as the guest states it.
		if (GameWhereabouts.Current is { } place)
		{
			state["level"] = new Dictionary<string, object?>
			{
				["name"] = place.DisplayName,
				["key"] = place.LevelKey,
				["world"] = place.World,
				["entrance"] = place.Entrance,
				["hash"] = $"{place.Key:X8}",
			};
		}
		else
		{
			state["level"] = "none (before the first load, or between levels)";
		}
		if (Engine_UiSource.Current.GetInGameScreens() is { } screens)
		{
			state["screen"] = screens.Current.ToString();
			state["screenId"] = (int)screens.Current;

			var owned = new Dictionary<string, object?>();
			foreach (KeyValuePair<uint, int> entry in screens.Owned)
			{
				owned[entry.Value.ToString()] = $"{entry.Key:X8}";
			}

			state["owned"] = owned;
		}
		else
		{
			state["screen"] = "no manager";
		}

		if (Engine_UiSource.Current.GetPauseState() is { } pause)
		{
			state["pauseScreenId"] = pause.ScreenId;
			state["modalId"] = pause.ModalId;
			state["modalMode"] = pause.ModalMode;
			state["tab"] = pause.CurrentTab;
		}

		state["captureScreen"] = Engine_UiSource.Current.MenuScreenId();

		// The world-map region sample as the C# side reads it RIGHT NOW — which is the half of
		// the round-trip a blank region page cannot distinguish on its own: a nameless sample
		// is the pump never publishing; a named one that stays blank is the match refusing it.
		Refii.Host.HostBridge.WorldMapRegion region = Refii.Host.HostBridge.GetWorldMapRegion();
		// Count discriminates the halves: 7 with no name means the pump's walk works and the
		// WANTED index never matched (the request side); 0 means the walk itself is failing.
		state["regionCount"] = region.Count;
		state["regionKnown"] = region.Name.Length != 0;
		state["regionIndex"] = region.Index;
		state["regionName"] = region.Name;

		// Two of the eleven provider fields, verbatim: empty here with a NAMED sample means the
		// pump's provider calls return nothing; text here with a blank page means the values
		// channel loses them between the bridge and the book.
		state["regionDistance"] = region.Distance;
		state["regionTravel"] = region.Travel;
		return state;
	}

	/// <summary>The spawn capture, as the guest last stated it.</summary>
	private static Dictionary<string, object?> Rows()
	{
		var rows = new List<Dictionary<string, object?>>();
		List<Engine_UiModel.MenuRow> captured =
			Engine_UiSource.Current.GetMenuRows(out ulong generation);
		foreach (Engine_UiModel.MenuRow row in captured)
		{
			rows.Add(new Dictionary<string, object?>
			{
				["name"] = row.Name,
				["parent"] = row.Parent,
				["label"] = row.Label,
				["id"] = row.Id,
				["eventType"] = row.EventType,
				["rating"] = row.Rating,
			});
		}

		return new Dictionary<string, object?>
		{
			["generation"] = generation,
			["screen"] = Engine_UiSource.Current.MenuScreenId(),
			["rows"] = rows,
		};
	}

	/// <summary>One caller's outstanding wait: answered from _Process when its condition holds.
	/// A class, not a record: a log wait carries a read offset that advances every frame.</summary>
	private sealed class PendingWait
	{
		public PendingWait(StreamPeerTcp client, string until, uint wantScreen,
			System.DateTime deadline, string logMatch, long logOffset)
		{
			Client = client;
			Until = until;
			WantScreen = wantScreen;
			Deadline = deadline;
			LogMatch = logMatch;
			LogOffset = logOffset;
		}

		public StreamPeerTcp Client { get; }
		public string Until { get; }
		public uint WantScreen { get; }
		public System.DateTime Deadline { get; }
		public string LogMatch { get; }
		public long LogOffset;
	}

	private readonly List<PendingWait> _waits = new();

	/// <summary>
	/// DEFERRED, NEVER BLOCKING: this server is served from `_Process` on Godot's main thread,
	/// so a wait that slept here would freeze the game for its own duration — the tool built to
	/// watch the game must not stop it. The condition is recorded and re-checked once per frame;
	/// the reply goes out on the frame it first holds.
	/// </summary>
	private Dictionary<string, object?>? Wait(
		StreamPeerTcp client, string until, uint wantScreen, double timeout, string logMatch = "")
	{
		if (until.Length == 0)
		{
			return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "wait needs 'for'" };
		}

		long offset = 0;
		if (until == "log")
		{
			if (logMatch.Length == 0)
			{
				return new Dictionary<string, object?>
				{
					["ok"] = false, ["error"] = "wait for='log' needs 'match'",
				};
			}

			// From HERE forward: a wait must never be satisfied by history.
			try
			{
				offset = new System.IO.FileInfo(
					ProjectSettings.GlobalizePath("user://logs/godot.log")).Length;
			}
			catch (System.Exception)
			{
				offset = 0;
			}
		}

		_waits.Add(new PendingWait(
			client, until, wantScreen,
			System.DateTime.UtcNow.AddSeconds(System.Math.Clamp(timeout, 1.0, 570.0)),
			logMatch, offset));
		return null;   // answered later, from ServeWaits
	}

	/// <summary>New godot.log bytes since the wait began, scanned for its match.</summary>
	private static bool LogMatched(PendingWait wait)
	{
		try
		{
			string path = ProjectSettings.GlobalizePath("user://logs/godot.log");
			using var stream = new System.IO.FileStream(
				path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
				System.IO.FileShare.ReadWrite);
			if (stream.Length <= wait.LogOffset)
			{
				return false;
			}

			stream.Seek(wait.LogOffset, System.IO.SeekOrigin.Begin);
			using var reader = new System.IO.StreamReader(stream);
			string added = reader.ReadToEnd();
			wait.LogOffset = stream.Length;
			return added.Contains(wait.LogMatch, System.StringComparison.OrdinalIgnoreCase);
		}
		catch (System.Exception)
		{
			return false;
		}
	}

	/// <summary>Once per frame: answer every wait whose condition holds or whose time is up.</summary>
	private void ServeWaits()
	{
		for (int i = _waits.Count - 1; i >= 0; --i)
		{
			PendingWait wait = _waits[i];
			if (wait.Client.GetStatus() != StreamPeerTcp.Status.Connected)
			{
				_waits.RemoveAt(i);
				continue;
			}

			bool met = wait.Until switch
			{
				// The title's armed byte — the press that "does not land" is this being false.
				"frontend-armed" => Refii.Host.HostBridge.TryGetFrontEnd(
					out _, out _, out bool canPressA, out _) && canPressA,

				// The front end exists at all (title or menu).
				"frontend" => Refii.Host.HostBridge.TryGetFrontEnd(out _, out _, out _, out _),

				// The in-game screen manager exists — the world is up.
				"world" => Engine_UiSource.Current.GetInGameScreens() is not null,

				// A specific in-game screen is current (the pause root is 0).
				"screen" => Engine_UiSource.Current.GetInGameScreens() is { } screens &&
					(uint)screens.Current == wait.WantScreen,

				// No screen is up — back in gameplay.
				"gameplay" => Engine_UiSource.Current.GetInGameScreens() is { } up &&
					up.Current == Engine_UiModel.InGameScreen.None,

				// A line containing the match arrived in godot.log AFTER the wait began.
				"log" => LogMatched(wait),

				_ => true,
			};

			bool expired = System.DateTime.UtcNow >= wait.Deadline;
			if (!met && !expired)
			{
				continue;
			}

			var reply = new Dictionary<string, object?>
			{
				["for"] = wait.Until,
				["met"] = met,
			};
			if (!met)
			{
				reply["timeout"] = true;
			}

			wait.Client.PutData(Encoding.UTF8.GetBytes(
				Refii.Core.Engine_Json.Serialize(Ok(reply)) + "\n"));
			_waits.RemoveAt(i);
		}
	}

	/// <summary>
	/// THE FRONT END, which `state` cannot describe: it is a different GUI object from the
	/// in-game screen manager, so a run parked on the title or the gender card reports "no
	/// manager" and nothing more. That is precisely the state an unattended run gets stuck in,
	/// and the one this could not see.
	/// </summary>
	private static Dictionary<string, object?> FrontEnd()
	{
		if (!Refii.Host.HostBridge.TryGetFrontEnd(
				out Engine_UiModel.FrontEndScreen screen, out uint state, out bool canPressA,
				out uint cursor))
		{
			return new Dictionary<string, object?> { ["up"] = false };
		}

		return new Dictionary<string, object?>
		{
			["up"] = true,
			["screen"] = screen.ToString(),
			["state"] = state,

			// The title's A is gated by an armed byte; a press that "does not land" is usually
			// this being false rather than the press being lost.
			["canPressA"] = canPressA,
			["cursor"] = cursor,
		};
	}

	/// <summary>The filtered tail of ONE file, read shared — its writer is still writing.</summary>
	private static List<string> TailOf(string path, int take, string match)
	{
		var kept = new List<string>();
		try
		{
			using var stream = new System.IO.FileStream(
				path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
				System.IO.FileShare.ReadWrite);
			using var reader = new System.IO.StreamReader(stream);
			while (reader.ReadLine() is { } line)
			{
				if (match.Length != 0 &&
					!line.Contains(match, System.StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				kept.Add(line);
				if (kept.Count > 4000)
				{
					kept.RemoveRange(0, 2000);
				}
			}
		}
		catch (System.Exception)
		{
			// A rotating segment can vanish mid-read; the other segments still answer.
		}

		if (kept.Count > take)
		{
			kept.RemoveRange(0, kept.Count - take);
		}

		return kept;
	}

	/// <summary>
	/// The tail of the game's own log, so a question can be asked without leaving the channel.
	/// `match` filters to lines containing it, which is what makes 40 lines enough.
	/// </summary>
	private static Dictionary<string, object?> Tail(int lines, string match)
	{
		string path = GameLogPaths.GodotLog;
		var kept = new List<string>();
		try
		{
			// Read-shared: the engine is writing this file while we read it.
			using var stream = new System.IO.FileStream(
				path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
				System.IO.FileShare.ReadWrite);
			using var reader = new System.IO.StreamReader(stream);
			while (reader.ReadLine() is { } line)
			{
				if (match.Length != 0 &&
					!line.Contains(match, System.StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				kept.Add(line);
				if (kept.Count > 4000)
				{
					kept.RemoveRange(0, 2000);
				}
			}
		}
		catch (System.Exception error)
		{
			return new Dictionary<string, object?> { ["error"] = error.Message, ["path"] = path };
		}

		int take = lines < 1 ? 1 : lines;
		if (kept.Count > take)
		{
			kept.RemoveRange(0, kept.Count - take);
		}

		return new Dictionary<string, object?> { ["path"] = path, ["lines"] = kept };
	}

	/// <summary>
	/// A PNG of the game window. Written where the caller asks — never into the package or the
	/// repo, because these are frames of the player's own game data.
	/// </summary>
	private Dictionary<string, object?> Shot(string path)
	{
		if (path.Length == 0)
		{
			return new Dictionary<string, object?> { ["error"] = "no path" };
		}

		if (!IsInsideTree() || GetWindow() is not { } window)
		{
			return new Dictionary<string, object?> { ["error"] = "no window" };
		}

		System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
		Image image = window.GetTexture().GetImage();
		// AN HDR-2D VIEWPORT HOLDS LINEAR LIGHT, whatever the display: the PNG takes the sRGB
		// encode (values past 1 clip). Skipped on an HDR display before, every shot there was
		// linear bytes read as sRGB, ~6x too dark at night. A non-HDR viewport is sRGB already.
		if (window.UseHdr2D && image.GetFormat() is Image.Format.Rgbah or Image.Format.Rgbaf or Image.Format.Rgba8)
		{
			image.LinearToSrgb();
		}

		Error error = image.SavePng(path);
		return new Dictionary<string, object?>
		{
			["path"] = path,
			["width"] = image.GetWidth(),
			["height"] = image.GetHeight(),
			["saved"] = error == Error.Ok,
		};
	}

	/// <summary>
	/// The metrics reply's `gc` block: run totals, the last collection, the last 200 frames
	/// folded (bytes per frame, frames with a collection), and the GC's own configuration.
	/// </summary>
	private static Dictionary<string, object?> GcReport()
	{
		System.GCMemoryInfo info = System.GC.GetGCMemoryInfo();
		System.ReadOnlySpan<System.GCGenerationInfo> generations = info.GenerationInfo;
		long latest = Refii.Core.Engine_GcFrames.LatestFrame;
		var recent = new List<object?>();
		List<Refii.Core.Engine_GcFrames.Collection> collections =
			Refii.Core.Engine_GcFrames.CollectionsSince(latest - 200);
		foreach (Refii.Core.Engine_GcFrames.Collection c in collections)
		{
			recent.Add(Refii.Core.Engine_GcFrames.Describe(c));
		}

		return new Dictionary<string, object?>
		{
			["gen0"] = System.GC.CollectionCount(0),
			["gen1"] = System.GC.CollectionCount(1),
			["gen2"] = System.GC.CollectionCount(2),
			["pauseMsTotal"] = System.GC.GetTotalPauseDuration().TotalMilliseconds,
			["allocatedMB"] = System.GC.GetTotalAllocatedBytes() / 1048576.0,
			["steadyAllocMBPerSec"] = System.Math.Round(Refii.Core.Engine_FramePhases.SteadyAllocMBPerSec, 2),
			["steadyGen0PerMin"] = System.Math.Round(Refii.Core.Engine_FramePhases.SteadyGen0PerMin, 1),
			["steadyGen2PerMin"] = System.Math.Round(Refii.Core.Engine_FramePhases.SteadyGen2PerMin, 2),
			["heapMB"] = info.HeapSizeBytes / 1048576.0,
			["gen0MB"] = generations[0].SizeAfterBytes / 1048576.0,
			["gen1MB"] = generations[1].SizeAfterBytes / 1048576.0,
			["gen2MB"] = generations[2].SizeAfterBytes / 1048576.0,
			["lohMB"] = generations[3].SizeAfterBytes / 1048576.0,
			["lastPauseMs"] = info.PauseDurations[0].TotalMilliseconds,
			// Committed, not just live: the gap between this and `heapMB` is large-object
			// fragmentation.
			["committedMB"] = System.Math.Round(info.TotalCommittedBytes / 1048576.0, 1),
			["lastPromotedMB"] = info.PromotedBytes / 1048576.0,
			// The GC's own view of the phone's memory. At or over the high-load threshold (90% of
			// physical by default; GCHighMemPercent moves it) it treats memory as scarce: a
			// collection with enough gen2 fragmentation to reclaim is escalated to a compacting
			// full one. Android keeps MemAvailable low by design, so a phone can sit above it.
			["memoryLoadPercent"] = info.TotalAvailableMemoryBytes > 0
				? System.Math.Round(100.0 * info.MemoryLoadBytes / info.TotalAvailableMemoryBytes, 1) : null,
			["highMemoryLoadPercent"] = info.TotalAvailableMemoryBytes > 0
				? System.Math.Round(100.0 * info.HighMemoryLoadThresholdBytes / info.TotalAvailableMemoryBytes, 1) : null,
			["totalAvailableMB"] = info.TotalAvailableMemoryBytes / 1048576,
			// The last 200 frames (about 7 s at the phone's 30 FPS cap) from the frame ring.
			["recentFrames"] = Refii.Core.Engine_GcFrames.Summarise(
				Refii.Core.Engine_GcFrames.RowsSince(latest - 200)),
			["recentCollections"] = recent,
			["workers"] = Refii.Core.Engine_GcFrames.Workers(),
			["config"] = Refii.Core.Engine_GcFrames.Configuration(),
		};
	}

	/// <summary>The frames that exceeded the hitch threshold, each with its phases.</summary>
	private static List<object?> Hitches()
	{
		var rows = new List<object?>();
		lock (Refii.Core.Engine_FramePhases.Hitches)
		{
			foreach (Refii.Core.Engine_FramePhases.Hitch hitch in Refii.Core.Engine_FramePhases.Hitches)
			{
				rows.Add(new Dictionary<string, object?>
				{
					["totalMs"] = System.Math.Round(hitch.TotalMs, 1),
					["phases"] = hitch.Phases,
					// The widest stretch of that frame no phase accounts for. `process_total`
					// spans the whole _Process pass, so this is where to look when the named
					// phases sum to nothing like the frame's cost.
					["gap"] = hitch.Gap,
				});
			}
		}

		return rows;
	}

	private (Refii.Host.HostBridge.TimeBaseMode Mode, ulong Capped, ulong Granted, ulong Watchdog)? _timeBase;
	private (double Seconds, uint Tick)? _statedClock;
	/// <summary>Sim-clock advances observed, and how many landed off the 1/30 s grid.</summary>
	public static long SimStepsSeen;

	public static long SimRebasedSteps;

	/// <summary>World time thrown away by re-bases, in HF steps — the lurch, summed.</summary>
	public static double SimSkippedSteps;

	/// <summary>The largest single off-grid advance, in HF steps.</summary>
	public static double SimRebaseWorstSteps;

	private double _statedHz;
	private double _statedPrevSeconds;
	private uint _statedPrevTick;
	private ulong _statedPrevUsec;

	private double _rebasePrevSeconds;

	/// <summary>
	/// Counts every step the guest re-based across — the lurch, as it happens.
	/// </summary>
	/// <remarks>
	/// Reads one word, the clock at `game+0x110`: the tick counter at `game+0xFC` is written at
	/// a different instruction, so comparing the two from Godot's thread races.
	///
	/// A step advances the clock by exactly 1/30 s
	/// (`82276f14 fmadd f2, f3, f27, f26`, stored at `82276f18`), so any advance is a whole
	/// number of steps — unless the guest took the branch at `82276fb4` and slammed the clock
	/// to now, which lands off the grid. Drawing at a rate above the step rate means an
	/// occasional advance of exactly two steps, and that is legitimate, so the test is
	/// "integer multiple", not "one step".
	/// </remarks>
	private void SampleSimRebase()
	{
		if (Refii.Host.HostBridge.SimClock() is not { } clock || clock.Seconds <= 0.0)
		{
			return;
		}

		double previous = _rebasePrevSeconds;
		_rebasePrevSeconds = clock.Seconds;
		if (previous <= 0.0 || clock.Seconds <= previous)
		{
			return;  // first sample, or the world restarted its clock
		}

		double steps = (clock.Seconds - previous) * Refii.Core.Engine_TickClock.HighFrequencyStepsPerSecond;
		double off = steps - System.Math.Round(steps);
		++SimStepsSeen;
		if (System.Math.Abs(off) > 0.02)
		{
			++SimRebasedSteps;
			SimSkippedSteps += System.Math.Abs(off);
			if (System.Math.Abs(steps) > SimRebaseWorstSteps)
			{
				SimRebaseWorstSteps = System.Math.Abs(steps);
			}
		}
	}

	/// <summary>
	/// The guest's own sim clock, and how fast it is actually advancing.
	/// </summary>
	/// <remarks>
	/// Measured because it varies: `sub_82276D10` advances the clock by exactly 1/30 s per
	/// high-frequency step, but when it falls more than one step behind it takes ONE step and
	/// re-bases to now, discarding the lost time (0x82276FA4). The rate is 15 a second while the
	/// guest keeps up and lower under load, with discontinuities.
	/// </remarks>
	private void SampleStatedClock()
	{
		_statedClock = Refii.Host.HostBridge.SimClock();
		_timeBase = Refii.Host.HostBridge.TimeBase();
		if (_statedClock is not { } clock)
		{
			return;
		}

		ulong now = Godot.Time.GetTicksUsec();
		if (_statedPrevUsec != 0UL && now > _statedPrevUsec && clock.Tick > _statedPrevTick)
		{
			double seconds = (now - _statedPrevUsec) / 1e6;
			_statedHz = (clock.Tick - _statedPrevTick) / seconds;
		}

		_statedPrevSeconds = clock.Seconds;
		_statedPrevTick = clock.Tick;
		_statedPrevUsec = now;
	}

}
