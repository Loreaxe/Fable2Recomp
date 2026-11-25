using System.Collections.Generic;
using System.Diagnostics;
using Godot;

namespace Refii.Core;

/// <summary>
/// Where the main thread's frame goes, by named phase: a stopwatch around each per-frame
/// body, kept as an exponential average and a peak. Read back by the debug channel's
/// `metrics` reply. The render thread is measured by the viewport itself; this is the rest.
/// </summary>
public static class Engine_FramePhases
{
	private sealed class Phase
	{
		public double AverageMs;
		public double PeakMs;
		public double LastMs;

		/// <summary>The frame this phase last ran in, so a hitch snapshot only names live phases.</summary>
		public long LastFrame;

		/// <summary>Managed bytes this thread allocated inside the scope, averaged; laps and records have none.</summary>
		public double AverageAllocBytes;
	}

	private static readonly Dictionary<string, Phase> Phases = new();
	private static readonly Refii.Core.Engine_TracedLock Gate = new("frame phases");

	/// <summary>
	/// Opens a profiler zone for a phase, or 0 when no profiler is installed.
	/// </summary>
	/// <remarks>
	/// A HOOK RATHER THAN A CALL. This file is core and the profiler lives behind the game's
	/// host bridge, so calling it from here would point the dependency the wrong way. The game
	/// installs the pair once at startup and EVERY existing `Measure` site becomes a profiler
	/// zone — one annotation per phase instead of two to keep in step, which is the only way a
	/// codebase stays instrumented.
	///
	/// A ulong handle rather than an IDisposable: a phase opens on every frame of every screen,
	/// and boxing one per scope would make the instrument a cost of its own.
	/// </remarks>
	public static System.Func<string, ulong>? ZoneBegin;

	/// <summary>Closes what <see cref="ZoneBegin"/> opened.</summary>
	public static System.Action<ulong>? ZoneEnd;

	/// <summary>
	/// The phase just entered, for the hang watchdog: installed by the game like the zone hooks,
	/// so a report can say which phase the main thread stopped in. Same reason it is a hook.
	/// </summary>
	public static System.Action<string>? PhaseEnter;

	/// <summary>
	/// The main thread's frame stamp, called at frame start and end with the frame index; the
	/// watchdog behind it fires when the stamp stops moving.
	/// </summary>
	public static System.Action<long>? FrameAlive;

	public readonly struct Scope : System.IDisposable
	{
		private readonly string _name;
		private readonly long _start;
		private readonly long _allocStart;
		private readonly ulong _zone;

		public Scope(string name)
		{
			_name = name;
			_start = Stopwatch.GetTimestamp();
			_allocStart = System.GC.GetAllocatedBytesForCurrentThread();
			_zone = ZoneBegin is { } begin ? begin(name) : 0UL;
			PhaseEnter?.Invoke(name);
		}

		public void Dispose()
		{
			if (_zone != 0UL)
			{
				ZoneEnd?.Invoke(_zone);
			}

			double ms = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
			long alloc = System.GC.GetAllocatedBytesForCurrentThread() - _allocStart;
			using (Gate.Hold())
			{
				if (!Phases.TryGetValue(_name, out Phase? phase))
				{
					phase = new Phase { AverageMs = ms, AverageAllocBytes = alloc };
					Phases[_name] = phase;
				}

				phase.LastMs = ms;
				phase.LastFrame = _frameIndex;
				phase.AverageMs += (ms - phase.AverageMs) * 0.05;
				phase.PeakMs = System.Math.Max(phase.PeakMs * 0.98, ms);
				phase.AverageAllocBytes += (alloc - phase.AverageAllocBytes) * 0.05;
				if (_markCount < _marks.Length)
				{
					_marks[_markCount++] = (_name, Stopwatch.GetTimestamp());
				}
			}
		}
	}

	public static Scope Measure(string name) => new(name);

	/// <summary>Records the time since <paramref name="since"/> under a name and restarts it.</summary>
	/// <remarks>
	/// PLOTTED, NOT ZONED. A lap is a duration recorded after the fact, and a profiler zone has
	/// to be opened and closed around the work as it happens — there is no retroactive zone. A
	/// plot is the honest shape for it: the profiler draws the millisecond value per frame
	/// beside the zones, which is what a lap actually is.
	/// </remarks>
	public static void Lap(string name, ref long since)
	{
		long now = Stopwatch.GetTimestamp();
		double ms = (now - since) * 1000.0 / Stopwatch.Frequency;
		Record(name, ms);
		Plot?.Invoke(name, ms);
		since = now;
	}

	/// <summary>Draws a named value on the profiler's timeline; installed with the zone hooks.</summary>
	public static System.Action<string, double>? Plot;

	private static long _frameStart;

	private static long _frameIndex;

	/// <summary>Frames begun so far: a cheap per-frame key for caches (no engine call).</summary>
	public static long FrameIndex => System.Threading.Volatile.Read(ref _frameIndex);

	/// <summary>Each phase completion this frame, in order, so a hitch can be localised.</summary>
	/// <remarks>
	/// A frame also contains what Godot does between our nodes (scene-tree walk, signal
	/// dispatch, Skeleton3D processing), which no `Measure` covers, and phases nest so they
	/// cannot be summed. Recording when each phase finished lets the largest gap between two
	/// consecutive finishes bracket the unaccounted time between two named points.
	/// </remarks>
	private static readonly (string Name, long At)[] _marks = new (string, long)[192];

	private static int _markCount;

	/// <summary>One caught hitch: what the frame cost, and which phases were in it.</summary>
	public sealed class Hitch
	{
		public double TotalMs;
		public string Phases = string.Empty;

		/// <summary>The widest stretch no phase accounts for, and the two phases around it.</summary>
		public string Gap = string.Empty;
	}

	/// <summary>
	/// The worst frames seen, with the phases that were running in them.
	/// </summary>
	/// <remarks>
	/// Per-phase peaks decay independently and may come from different frames, so a frame over
	/// the threshold is snapshotted whole at the next frame's start, where its cost is known and
	/// the phases still hold its values. Only phases that ran in that frame are named — `LastMs`
	/// on one that did not is stale. Kept sorted worst-first and capped.
	/// </remarks>
	public static readonly List<Hitch> Hitches = new();

	/// <summary>A frame over this many milliseconds is worth explaining.</summary>
	public static double HitchThresholdMs = 25.0;

	private const int MaxHitches = 12;

	private static void CatchHitch(double totalMs)
	{
		if (totalMs < HitchThresholdMs)
		{
			return;
		}

		// Cheapest first: if it cannot displace the weakest entry there is nothing to build.
		if (Hitches.Count >= MaxHitches && totalMs <= Hitches[Hitches.Count - 1].TotalMs)
		{
			return;
		}

		var parts = new List<(string Name, double Ms)>();
		foreach ((string name, Phase phase) in Phases)
		{
			if (phase.LastFrame == _frameIndex && name != "frame_total" && phase.LastMs >= 1.0)
			{
				parts.Add((name, phase.LastMs));
			}
		}

		parts.Sort((a, b) => b.Ms.CompareTo(a.Ms));
		var text = new System.Text.StringBuilder();
		for (int i = 0; i < parts.Count && i < 5; i++)
		{
			text.Append(i == 0 ? string.Empty : " ").Append(parts[i].Name)
				.Append(' ').Append(parts[i].Ms.ToString("0.0"));
		}

		// The widest stretch of the frame that no phase accounts for, and what bracketed it.
		string gap = string.Empty;
		double worstGapMs = 0.0;
		long previousAt = _frameStart;
		string previousName = "frame start";
		for (int i = 0; i < _markCount; i++)
		{
			double sinceMs = (_marks[i].At - previousAt) * 1000.0 / Stopwatch.Frequency;
			if (sinceMs > worstGapMs)
			{
				worstGapMs = sinceMs;
				gap = $"{sinceMs:0.0} ms between '{previousName}' and '{_marks[i].Name}'";
			}

			previousAt = _marks[i].At;
			previousName = _marks[i].Name;
		}

		Hitches.Add(new Hitch { TotalMs = totalMs, Phases = text.ToString(), Gap = gap });
		Hitches.Sort((a, b) => b.TotalMs.CompareTo(a.TotalMs));
		if (Hitches.Count > MaxHitches)
		{
			Hitches.RemoveAt(Hitches.Count - 1);
		}
	}

	/// <summary>
	/// What the renderer and the GC did during one frame: sampled at every frame start, so the
	/// difference between two samples is exactly the hitch frame's share.
	/// </summary>
	/// <remarks>
	/// The worst-frames list above says WHICH phase a hitch sat in but not WHY. A 1.5 s wait in
	/// `draw` is the main thread waiting for the render thread, and the render thread's work is
	/// invisible to every `Measure`: pipeline compilations, texture and buffer uploads, and
	/// the GC's stop-the-world pauses are the candidates, and each has a counter.
	/// </remarks>
	public struct FrameCounters
	{
		public double PipelinesCanvas, PipelinesMesh, PipelinesSurface, PipelinesDraw, PipelinesSpecialization;
		public double VideoMemMB, TextureMemMB, BufferMemMB;
		public double ObjectsDrawn, Primitives;
		public int Gen0, Gen1, Gen2;
		public double GcPauseMs;
		public double Resources, Nodes;

		public static FrameCounters Sample()
		{
			return new FrameCounters
			{
				PipelinesCanvas = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsCanvas),
				PipelinesMesh = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh),
				PipelinesSurface = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface),
				PipelinesDraw = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw),
				PipelinesSpecialization = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization),
				VideoMemMB = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0,
				TextureMemMB = Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / 1048576.0,
				BufferMemMB = Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed) / 1048576.0,
				ObjectsDrawn = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame),
				Primitives = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),
				Gen0 = System.GC.CollectionCount(0),
				Gen1 = System.GC.CollectionCount(1),
				Gen2 = System.GC.CollectionCount(2),
				GcPauseMs = System.GC.GetTotalPauseDuration().TotalMilliseconds,
				Resources = Performance.GetMonitor(Performance.Monitor.ObjectResourceCount),
				Nodes = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
			};
		}
	}

	private static FrameCounters _previousCounters;

	/// <summary>The counters sampled at the latest frame start, for the in-game Tracy feed.</summary>
	public static FrameCounters LatestCounters => _previousCounters;
	private static bool _haveCounters;

	/// <summary>A frame over this is logged with its counters, not only kept in the list.</summary>
	/// <remarks>`hitch_log_ms` in refii.toml (default 200): lower it to see the 80-150 ms frames a
	/// walk through town has, each with what it did for the first time (Engine_SpikeNotes).</remarks>
	public static double HitchLogThresholdMs = ReadHitchLogThreshold();

	private static double ReadHitchLogThreshold()
	{
		try
		{
			int ms = Engine_RefiiConfig.Int(Engine_RefiiConfig.Read(out _), "hitch_log_ms", 200);
			return ms > 0 ? ms : 200.0;
		}
		catch (System.Exception)
		{
			return 200.0;
		}
	}

	/// <summary>
	/// Logs a frame over <see cref="HitchLogThresholdMs"/> with what moved during it. Called
	/// once per frame start with the finished frame's cost; the sample it takes is kept as the
	/// baseline for the next frame either way.
	/// </summary>
	private static void LogHitch(double totalMs)
	{
		FrameCounters now = FrameCounters.Sample();
		if (_haveCounters && totalMs >= HitchLogThresholdMs)
		{
			FrameCounters was = _previousCounters;
			string phases = string.Empty;
			string gap = string.Empty;
			foreach (Hitch h in Hitches)
			{
				if (System.Math.Abs(h.TotalMs - totalMs) < 0.001)
				{
					phases = h.Phases;
					gap = h.Gap;
					break;
				}
			}

			GD.Print($"[hitch] {System.DateTime.Now:HH:mm:ss.fff} frame {totalMs:0} ms" +
				$" | {phases}" +
				$" | gap: {gap}" +
				$" | pipelines +{now.PipelinesSurface - was.PipelinesSurface:0} surface" +
				$" +{now.PipelinesSpecialization - was.PipelinesSpecialization:0} spec" +
				$" +{now.PipelinesDraw - was.PipelinesDraw:0} draw" +
				$" +{now.PipelinesMesh - was.PipelinesMesh:0} mesh" +
				$" +{now.PipelinesCanvas - was.PipelinesCanvas:0} canvas" +
				$" | gc gen0 +{now.Gen0 - was.Gen0} gen1 +{now.Gen1 - was.Gen1} gen2 +{now.Gen2 - was.Gen2}" +
				$" pause +{now.GcPauseMs - was.GcPauseMs:0.0} ms" +
				$" | vram {now.VideoMemMB:0} MB ({now.VideoMemMB - was.VideoMemMB:+0;-0} MB)" +
				$" tex {now.TextureMemMB - was.TextureMemMB:+0;-0} MB buf {now.BufferMemMB - was.BufferMemMB:+0;-0} MB" +
				$" | objects {now.ObjectsDrawn:0} ({now.ObjectsDrawn - was.ObjectsDrawn:+0;-0})" +
				$" prims {now.Primitives / 1e6:0.0}M" +
				$" | resources {now.Resources:0} ({now.Resources - was.Resources:+0;-0}) nodes {now.Nodes:0} ({now.Nodes - was.Nodes:+0;-0})" +
				$" | updates: {Engine_InstanceUpdates.LastFrameSummary()}" +
				$" | events: {Engine_SpikeNotes.LastFrameSummary()}");
		}

		_previousCounters = now;
		_haveCounters = true;
	}

	/// <summary>The first process callback of the frame (lowest priority node).</summary>
	public static void MarkFrameStart()
	{
		long now = Stopwatch.GetTimestamp();
		Engine_LoadTimeline.FrameStart(now);
		double totalMs = 0.0;
		if (_frameStart != 0)
		{
			totalMs = (now - _frameStart) * 1000.0 / Stopwatch.Frequency;
			Record("frame_total", totalMs);

			// Before the index moves on: the phases still hold the finished frame's values.
			using (Gate.Hold())
			{
				// The instance-update window closes with the frame, so the hitch line below can
				// name what this frame wrote into the scene.
				Engine_InstanceUpdates.EndFrame();
				Engine_SpikeNotes.EndFrame();
				CatchHitch(totalMs);
				LogHitch(totalMs);
			}
		}
		else
		{
			using (Gate.Hold())
			{
				Engine_InstanceUpdates.EndFrame();
				Engine_SpikeNotes.EndFrame();
				LogHitch(0.0);
			}
		}

		_markCount = 0;
		++_frameIndex;
		Engine_GcFrames.FrameStart(_frameIndex, totalMs);
		FrameAlive?.Invoke(_frameIndex);
		SampleSteadyGc(now);

		// Everything between the renderer finishing and the next frame's first callback:
		// swap/present wait, OS events, physics, deferred calls.
		if (_drawEnd != 0)
		{
			Record("after_draw", (now - _drawEnd) * 1000.0 / Stopwatch.Frequency);
		}

		_frameStart = now;
	}

	private static long _steadyWindowStart;
	private static long _steadyStartAllocBytes;
	private static int _steadyStartGen0;
	private static int _steadyStartGen2;

	/// <summary>Rolling 5-second steady-state managed allocation rate in MB/sec.</summary>
	public static double SteadyAllocMBPerSec { get; private set; }

	/// <summary>Rolling 5-second steady-state Gen0 collections per minute.</summary>
	public static double SteadyGen0PerMin { get; private set; }

	/// <summary>Rolling 5-second steady-state Gen2 collections per minute.</summary>
	public static double SteadyGen2PerMin { get; private set; }

	private static void ResetSteadyGc(long now)
	{
		_steadyWindowStart = now;
		_steadyStartAllocBytes = System.GC.GetTotalAllocatedBytes();
		_steadyStartGen0 = System.GC.CollectionCount(0);
		_steadyStartGen2 = System.GC.CollectionCount(2);
		SteadyAllocMBPerSec = 0.0;
		SteadyGen0PerMin = 0.0;
		SteadyGen2PerMin = 0.0;
	}

	private static void SampleSteadyGc(long now)
	{
		if (_steadyWindowStart == 0)
		{
			ResetSteadyGc(now);
			return;
		}

		double elapsedSec = (now - _steadyWindowStart) / (double)Stopwatch.Frequency;
		if (elapsedSec < 1.0)
		{
			return;
		}

		long allocNow = System.GC.GetTotalAllocatedBytes();
		int gen0Now = System.GC.CollectionCount(0);
		int gen2Now = System.GC.CollectionCount(2);
		SteadyAllocMBPerSec = System.Math.Max(0L, allocNow - _steadyStartAllocBytes) / 1048576.0 / elapsedSec;
		SteadyGen0PerMin = System.Math.Max(0, gen0Now - _steadyStartGen0) * 60.0 / elapsedSec;
		SteadyGen2PerMin = System.Math.Max(0, gen2Now - _steadyStartGen2) * 60.0 / elapsedSec;

		if (elapsedSec >= 5.0)
		{
			_steadyWindowStart = now;
			_steadyStartAllocBytes = allocNow;
			_steadyStartGen0 = gen0Now;
			_steadyStartGen2 = gen2Now;
		}
	}

	/// <summary>
	/// Clears loading-screen spikes and stale one-shot phase averages once the world hold releases.
	/// </summary>
	public static void ResetMetrics()
	{
		using (Gate.Hold())
		{
			Phases.Clear();
			Hitches.Clear();
			_haveCounters = false;
			_frameStart = 0;
			_processEnd = 0;
			_drawEnd = 0;
			_deferredEnd = 0;
			_preDraw = 0;
			ResetSteadyGc(Stopwatch.GetTimestamp());
		}
	}

	/// <summary>The last process callback of the frame (highest priority node).</summary>
	private static long _processEnd;
	private static long _drawEnd;
	private static readonly bool TraceAllDraws =
		string.Equals(System.Environment.GetEnvironmentVariable("REFII_DX12_TRACE"), "1", System.StringComparison.Ordinal);
	private static int _traceFramesRemaining;
	private static string _traceStage = string.Empty;
	private static bool _activeDrawTraced;
	private static string _activeDrawStage = string.Empty;
	private static FrameCounters _preDrawCounters;

	/// <summary>Optional game-layer provider supplying D3D12 pre-draw isolation state for transition diagnostics.</summary>
	public static System.Func<string>? PreDrawDetailProvider;

	/// <summary>
	/// Arms pre-draw and post-draw isolation logging for the next <paramref name="frames"/> frames
	/// at key milestones (world mount, first guest camera drive, warmup ready, loading cover release).
	/// </summary>
	public static void ArmDrawTrace(string stage, int frames = 1)
	{
		if (!TraceAllDraws || Engine_AppRoot.IsTestHost || frames <= 0)
		{
			return;
		}

		using (Gate.Hold())
		{
			_traceStage = stage;
			if (frames > _traceFramesRemaining)
			{
				_traceFramesRemaining = frames;
			}
		}
	}

	public static void MarkFrameEnd()
	{
		_processEnd = Stopwatch.GetTimestamp();
		Engine_LoadTimeline.FrameEnd(_processEnd);
		if (_frameStart != 0)
		{
			Record("process_total", (_processEnd - _frameStart) * 1000.0 / Stopwatch.Frequency);
		}

		bool shouldTrace;
		string stage;
		using (Gate.Hold())
		{
			shouldTrace = !Engine_AppRoot.IsTestHost && (_traceFramesRemaining > 0 || TraceAllDraws);
			if (_traceFramesRemaining > 0)
			{
				_traceFramesRemaining--;
			}
			stage = string.IsNullOrEmpty(_traceStage) ? "frame" : _traceStage;
			_activeDrawTraced = shouldTrace;
			_activeDrawStage = stage;
		}

		if (shouldTrace)
		{
			FrameCounters pre = FrameCounters.Sample();
			using (Gate.Hold())
			{
				_preDrawCounters = pre;
			}
			string detail = string.Empty;
			try
			{
				detail = PreDrawDetailProvider?.Invoke() ?? string.Empty;
			}
			catch
			{
				// Never let diagnostics disturb frame submission.
			}

			GD.Print($"[dx12-trace] pre-draw frame={_frameIndex} stage={stage} obj={pre.ObjectsDrawn:0} prims={pre.Primitives / 1e6:0.00}M " +
				$"vram={pre.VideoMemMB:0}MB(tex={pre.TextureMemMB:0},buf={pre.BufferMemMB:0}) " +
				$"pendingUploads={Engine_ServerSyncs.PendingUploadCount} uploadKB={Engine_ServerSyncs.FrameUploadBytes / 1024L} {detail}");
		}

		FrameAlive?.Invoke(_frameIndex);
	}

	/// <summary>
	/// THE "draw" SPAN IN THREE, for where the main thread waits. Each part is recorded only
	/// when the marks before it came in order this frame, so the three sum to "draw":
	/// <list type="bullet">
	/// <item>draw.deferred — the deferred calls queued during the frame's processing (Godot's own
	/// skeleton updates, call_deferred), up to the mark the last process callback queued behind them.</item>
	/// <item>draw.sync — the transform notifications, timers and tweens, then RenderingServer.sync():
	/// with a render thread, the wait for it to finish everything queued so far, the last frame's
	/// draw included. Up to FramePreDraw, which RenderingServer.draw() emits on this thread.</item>
	/// <item>draw.post — from there to FramePostDraw: the audio and frame-rate cap, and, with a
	/// render thread, the next frame's input and physics up to the queue flush that delivers it.</item>
	/// </list>
	/// </summary>
	private static long _deferredEnd;
	private static long _preDraw;

	/// <summary>Run as a deferred call queued by the last process callback: the deferred calls before it have run.</summary>
	public static void MarkDeferredEnd()
	{
		_deferredEnd = Stopwatch.GetTimestamp();
		if (_processEnd != 0)
		{
			Record("draw.deferred", (_deferredEnd - _processEnd) * 1000.0 / Stopwatch.Frequency);
		}
	}

	/// <summary>RenderingServer.FramePreDraw: sync() has returned and draw() is about to be queued.</summary>
	public static void MarkPreDraw()
	{
		_preDraw = Stopwatch.GetTimestamp();
		if (_processEnd != 0 && _deferredEnd >= _processEnd)
		{
			Record("draw.sync", (_preDraw - _deferredEnd) * 1000.0 / Stopwatch.Frequency);
		}
	}

	/// <summary>RenderingServer.FramePostDraw: the renderer has submitted the frame.</summary>
	public static void MarkDrawEnd()
	{
		_drawEnd = Stopwatch.GetTimestamp();
		double drawMs = 0.0;
		if (_processEnd != 0)
		{
			drawMs = (_drawEnd - _processEnd) * 1000.0 / Stopwatch.Frequency;
			Record("draw", drawMs);
			if (_deferredEnd >= _processEnd && _preDraw >= _deferredEnd)
			{
				Record("draw.post", (_drawEnd - _preDraw) * 1000.0 / Stopwatch.Frequency);
			}
		}

		bool wasTraced;
		string stage;
		FrameCounters pre;
		using (Gate.Hold())
		{
			wasTraced = _activeDrawTraced;
			stage = _activeDrawStage;
			pre = _preDrawCounters;
			_activeDrawTraced = false;
		}

		if (!Engine_AppRoot.IsTestHost && (wasTraced || drawMs >= 1000.0))
		{
			FrameCounters after = FrameCounters.Sample();
			GD.Print($"[dx12-trace] post-draw frame={_frameIndex} stage={stage} draw={drawMs:0.0}ms obj={after.ObjectsDrawn:0} prims={after.Primitives / 1e6:0.00}M " +
				$"vram={after.VideoMemMB:0}MB({after.VideoMemMB - pre.VideoMemMB:+0;-0},tex={after.TextureMemMB - pre.TextureMemMB:+0;-0},buf={after.BufferMemMB - pre.BufferMemMB:+0;-0}) " +
				$"pipelines=+{after.PipelinesSurface - pre.PipelinesSurface:0}s/+{after.PipelinesSpecialization - pre.PipelinesSpecialization:0}sp/+{after.PipelinesDraw - pre.PipelinesDraw:0}d " +
				$"deviceLost={Engine_RdHealth.IsDeviceLost}");
		}
	}

	/// <summary>
	/// Records a duration a caller measured itself, and plots it.
	/// </summary>
	/// <remarks>
	/// For a cost ACCUMULATED across a loop, where neither a scope nor a lap fits: a scope would
	/// open once per iteration and a lap would record one value per iteration, and the question
	/// is what the whole walk spent.
	/// </remarks>
	public static void Record(string name, double ms, bool plot)
	{
		Record(name, ms);
		if (plot)
		{
			Plot?.Invoke(name, ms);
		}
	}

	private static void Record(string name, double ms)
	{
		using (Gate.Hold())
		{
			if (!Phases.TryGetValue(name, out Phase? phase))
			{
				phase = new Phase { AverageMs = ms };
				Phases[name] = phase;
			}

			phase.LastMs = ms;
			phase.LastFrame = _frameIndex;
			if (_markCount < _marks.Length)
			{
				_marks[_markCount++] = (name, Stopwatch.GetTimestamp());
			}
			phase.AverageMs += (ms - phase.AverageMs) * 0.05;
			phase.PeakMs = System.Math.Max(phase.PeakMs * 0.98, ms);
		}
	}


	/// <summary>One phase's rolling average and decaying peak, or (0, 0) before it has run.</summary>
	public static (double AverageMs, double PeakMs) Get(string name)
	{
		using (Gate.Hold())
		{
			return Phases.TryGetValue(name, out Phase? phase)
				? (phase.AverageMs, phase.PeakMs)
				: (0.0, 0.0);
		}
	}

	/// <summary>One phase's most recent duration, or 0 before it has run.</summary>
	public static double Last(string name)
	{
		using (Gate.Hold())
		{
			return Phases.TryGetValue(name, out Phase? phase) ? phase.LastMs : 0.0;
		}
	}

	/// <summary>Every phase as {name: {avgMs, peakMs, lastMs}}, for the metrics reply.</summary>
	public static Dictionary<string, object?> Report()
	{
		var report = new Dictionary<string, object?>();
		using (Gate.Hold())
		{
			foreach ((string name, Phase phase) in Phases)
			{
				report[name] = new Dictionary<string, object?>
				{
					["avgMs"] = System.Math.Round(phase.AverageMs, 2),
					["peakMs"] = System.Math.Round(phase.PeakMs, 2),
					["lastMs"] = System.Math.Round(phase.LastMs, 2),
					["allocKB"] = System.Math.Round(phase.AverageAllocBytes / 1024.0, 1),
				};
			}
		}

		return report;
	}
}

/// <summary>
/// Godot's process-frame counter (<c>Engine.GetProcessFrames()</c>), read once for a scope
/// instead of once per caller: the entity walk stamps and compares it per rig, several times
/// each, and every read is a crossing into the engine.
/// </summary>
/// <remarks>
/// The counter only moves between two iterations of the main loop, never inside a node's
/// callback, so inside a <see cref="Latch"/> the held value is exactly what the engine would
/// answer. Outside one, <see cref="Current"/> asks the engine as before. Main thread.
/// </remarks>
public static class Engine_ProcessFrame
{
	private static ulong _frame;
	private static int _depth;

	/// <summary>The process frame: the latched value inside a <see cref="Latch"/>, else the engine's.</summary>
	public static ulong Current => _depth > 0 ? _frame : Godot.Engine.GetProcessFrames();

	/// <summary>Whether a latch is open.</summary>
	public static bool Latched => _depth > 0;

	/// <summary>Holds the counter until the returned latch is disposed; a nested latch keeps the outer value.</summary>
	public static Latch Begin() => Begin(Godot.Engine.GetProcessFrames());

	/// <summary><see cref="Begin()"/> with the counter given, for tests.</summary>
	internal static Latch Begin(ulong frame)
	{
		if (_depth++ == 0)
		{
			_frame = frame;
		}

		return default;
	}

	/// <summary>Closes what <see cref="Begin()"/> opened.</summary>
	public readonly struct Latch : System.IDisposable
	{
		public void Dispose()
		{
			if (_depth > 0)
			{
				--_depth;
			}
		}
	}
}

/// <summary>First process callback of the frame (lowest priority node).</summary>
public sealed partial class Engine_FrameStartNode : Godot.Node
{
	public override void _Ready()
	{
		ProcessPriority = int.MinValue;
		Engine_ServerSyncs.MarkMainThread();
		Godot.RenderingServer.Singleton.FramePreDraw += Engine_ServerSyncs.MarkDrawBegin;
		Godot.RenderingServer.Singleton.FramePreDraw += Engine_FramePhases.MarkPreDraw;
		Godot.RenderingServer.Singleton.FramePostDraw += Engine_ServerSyncs.MarkDrawEnd;
		Godot.RenderingServer.Singleton.FramePostDraw += Engine_FramePhases.MarkDrawEnd;
	}

	public override void _Process(double delta)
	{
		Engine_ServerSyncs.BeginMainFrameUploadBudget();
		Engine_FramePhases.MarkFrameStart();
		using (Engine_FramePhases.Measure("gpu_upload_pump"))
		{
			Engine_ServerSyncs.PumpWorkerGpuUploads(Engine_ServerSyncs.LoadCoverActive ? 8.0 : 4.0);
		}

		// The finished frame's GPU pass table, when the engine captures one (`--gpu-profile`),
		// and every tracked viewport's render measurement — both read on the render thread.
		Engine_GpuPasses.Sample();
		Engine_RenderStats.SampleFrame();

		double now = Godot.Time.GetTicksUsec();

		// Sample the guest frame and true game tick once at the very start of the frame
		// (ProcessPriority = int.MinValue) before any clock discipline or reader runs.
		Refii.Host.HostBridge.SampleGuestFrame();

		// The guest's pause statement, latched before any swap or draw this frame: the in-game
		// GUI's active screen (RefiiHostGetPauseState; null off the in-game GUI, 31 when no
		// screen is up) or the guest's last game-flow step running no world tick (WorldHeld).
		// Not GameSession.Paused — the guest's steps keep running the GUI tick.
		Engine_StatedPause.BeginFrame(
			Refii.Host.HostBridge.GetPauseState() is { } pause &&
			Engine_StatedPause.StatesPause(pause.ScreenId,
				Refii.Host.HostBridge.Stated(Refii.Host.HostBridge.StatedValue.WorldHeld)));

		// The guest's own clock first: it states its simulation time every iteration at
		// `game+0x110` (0x82276F18). It cannot be fitted from sample arrivals because the guest
		// does not catch up — more than one step behind, it takes one step and re-bases.
		(double Seconds, uint Tick)? sampledClock = Refii.Host.HostBridge.LastSampledSimClock;
		double guestSeconds = sampledClock is { } clock ? clock.Seconds : double.NaN;
		if (Engine_TickClock.UseStatedClock)
		{
			Engine_TickClock.ObserveStatedClock(guestSeconds, now);
		}

		// Stamp the frame instant and advance the shared playhead before Engine_TickClock.BeginFrame
		// so DrawTick is current for both frame readers and playback-rate measurement. Only swap
		// when SampleGuestFrame observed a completed HF pass (never on an in-flight deferral).
		Refii.Entities.Engine_DrawnFrame.BeginFrame((ulong)now, guestSeconds);
		if (Engine_GuestFrame.Changed || Engine_GuestFrame.Frame == 0u)
		{
			Refii.Entities.Engine_DrawnFrame.NoteRestated();
		}
		Engine_TickClock.BeginFrame(now);
	}
}

/// <summary>Last process callback of the frame (highest priority node).</summary>
public sealed partial class Engine_FrameEndNode : Godot.Node
{
	private static readonly Godot.StringName DeferredMark = nameof(MarkDeferredEnd);

	public override void _Ready() => ProcessPriority = int.MaxValue;

	/// <summary>Queued behind the frame's deferred calls (Engine_FramePhases.MarkDeferredEnd).</summary>
	public void MarkDeferredEnd() => Engine_FramePhases.MarkDeferredEnd();

	public override void _Process(double delta)
	{
		Refii.Host.HostBridge.EndTimeBaseReadWindow();
		Engine_ServerSyncs.NotifyMainFrameTick();
		using (Engine_FramePhases.Measure("gpu_upload_pump_end"))
		{
			Engine_ServerSyncs.PumpWorkerGpuUploads(Engine_ServerSyncs.LoadCoverActive ? 12.0 : 6.0, waitForWorkers: false);
		}
		using (Engine_FramePhases.Measure("cloth_land"))
		{
			if (!Refii.AndroidRuntime.AndroidClothLand.AtPreDraw) Refii.Physics.Engine_ClothBody.LandAll();
		}
		Engine_FramePhases.MarkFrameEnd();
		CallDeferred(DeferredMark);
	}
}
