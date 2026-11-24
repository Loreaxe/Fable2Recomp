using System.Collections.Generic;

using Godot;

namespace Refii.Ui;

/// <summary>
/// The UI cut's dispatcher — one `GUI.*` call in, one owned surface driven.
/// </summary>
/// <remarks>
/// Fable 2's game VM calls ~120 semantic natives — <c>DisplayMessageBox</c>,
/// <c>FadeScreenIn</c>, <c>SetCounter</c> — and the UI is cut there, discarding the Gameface
/// DOM, the 52 `.bgf` presentations at runtime and the UI Lua VM's 62 chunks. See
/// docs/ui_native_cut.md.
///
/// A call arriving here STATES what happened, where the older path inferred it from a result —
/// a modal latch, the element pool, a per-frame walk of 3,431 elements.
///
/// A handler is a `Register` call and nothing else; the router owes each one only its arguments.
/// Anything unhandled is reported once WITH ITS ARGUMENTS, so the routing table is built from
/// what the running game calls rather than from a static name census.
/// </remarks>
public sealed class Engine_UiGuiNatives
{
	/// <summary>What a handler is given: the call, already parsed.</summary>
	public delegate void Handler(Engine_UiModel.GuiNativeCall call);

	private readonly Dictionary<string, Handler> _handlers =
		new(System.StringComparer.Ordinal);

	private readonly Dictionary<string, int> _unhandled =
		new(System.StringComparer.Ordinal);

	private readonly Dictionary<string, int> _handled =
		new(System.StringComparer.Ordinal);

	private ulong _seen;

	private bool _reportedLive;

	/// <summary>Routes <paramref name="name"/> to <paramref name="handler"/>.</summary>
	public void Register(string name, Handler handler) => _handlers[name] = handler;

	/// <summary>
	/// Marks a native as deliberately unrouted, with the reason.
	/// </summary>
	/// <remarks>
	/// "Not routed" and "handled elsewhere" are different census answers. The fade family is the
	/// case: the guest starts the fade, advances it at 15 Hz and reads it back through five
	/// non-Lua consumers, so a host driving its own timeline would hang level change and
	/// save-load — the fade is drawn by READING that state instead. The reason prints once.
	/// </remarks>
	public void Observe(string name, string reason) => _observed[name] = reason;

	private readonly Dictionary<string, string> _observed =
		new(System.StringComparer.Ordinal);

	/// <summary>True when a native has a host handler.</summary>
	public bool Handles(string name) => _handlers.ContainsKey(name);

	/// <summary>
	/// Drains one frame's calls and dispatches them in order — a script that opens a box and
	/// immediately removes it must leave nothing on screen.
	/// </summary>
	public void Drive()
	{
		List<Engine_UiModel.GuiNativeCall> calls = Engine_UiSource.Current.TakeGuiNativeCalls();
		if (calls.Count == 0)
		{
			ReportLiveOnce();
			return;
		}

		foreach (Engine_UiModel.GuiNativeCall call in calls)
		{
			++_seen;
			if (Engine_UiStatementRecord.Recording)
			{
				Engine_UiStatementRecord.Note('G', call.Name + Describe(call));
			}

			if (_handlers.TryGetValue(call.Name, out Handler? handler))
			{
				NoteHandled(call);
				handler(call);
				continue;
			}

			NoteUnhandled(call);
		}

		ReportLiveOnce();
		ReportCensusPeriodically();
	}

	private double _sinceCensus;

	/// <summary>
	/// Re-states the whole census every half minute. Periodic rather than on exit, because a
	/// killed run never reaches teardown; only prints while calls are arriving.
	/// </summary>
	private void ReportCensusPeriodically()
	{
		_sinceCensus += 1.0;
		if (_sinceCensus < 600.0)
		{
			return;
		}

		_sinceCensus = 0.0;
		GD.Print($"[ui-cut] census — {Census()}");
	}

	/// <summary>
	/// Says the cut is live, once, with the counters that separate its three failure modes: the
	/// registration hook never saw the `GUI` namespace (named = 0), the namespace registered but
	/// nothing has been called (named &gt; 0, intercepted = 0), or calls arrive and are not
	/// drained (pending climbing).
	/// </summary>
	private void ReportLiveOnce()
	{
		if (_reportedLive || !Engine_UiSource.Current.IsLive)
		{
			return;
		}

		(ulong intercepted, uint pending, uint named) =
			Engine_UiSource.Current.GuiNativeCounters();
		if (named == 0)
		{
			return;
		}

		_reportedLive = true;
		GD.Print($"[ui-cut] the GUI seam is live — {named} native(s) named by the registration " +
			$"hook, {intercepted} call(s) intercepted so far ({pending} pending). " +
			$"{_handlers.Count} of them routed to an owned surface.");
	}

	private void NoteHandled(Engine_UiModel.GuiNativeCall call)
	{
		_handled.TryGetValue(call.Name, out int count);
		_handled[call.Name] = count + 1;
		if (count == 0)
		{
			GD.Print($"[ui-cut] GUI.{call.Name} is OWNED — first call {Describe(call)}");
		}
	}

	/// <summary>
	/// Reports a native nothing routes, once per name, with the arguments it carried — the
	/// arguments say what a handler would have to do and whether the recorded shape matches.
	/// </summary>
	private void NoteUnhandled(Engine_UiModel.GuiNativeCall call)
	{
		_unhandled.TryGetValue(call.Name, out int count);
		_unhandled[call.Name] = count + 1;
		if (count != 0)
		{
			return;
		}

		if (_observed.TryGetValue(call.Name, out string? reason))
		{
			GD.Print($"[ui-cut] GUI.{call.Name} observed, not routed — {reason}");
			return;
		}

		GD.Print($"[ui-cut] GUI.{call.Name} NOT ROUTED — {Describe(call)} " +
			$"({_unhandled.Count} distinct unrouted so far)");
	}

	/// <summary>One call as a readable line — the name and every argument with its kind.</summary>
	public static string Describe(Engine_UiModel.GuiNativeCall call)
	{
		if (call.Arguments is not { Count: > 0 } arguments)
		{
			return "()";
		}

		var parts = new List<string>(arguments.Count);
		foreach (Engine_UiModel.GuiNativeArgument argument in arguments)
		{
			parts.Add(argument.Kind switch
			{
				Engine_UiModel.GuiNativeArgumentKind.Number => argument.Text,
				Engine_UiModel.GuiNativeArgumentKind.Boolean => argument.Boolean ? "true" : "false",
				Engine_UiModel.GuiNativeArgumentKind.String => $"'{argument.Text}'",
				Engine_UiModel.GuiNativeArgumentKind.None => "nil",
				_ => "<table/fn>",
			});
		}

		return $"({string.Join(", ", parts)})";
	}

	/// <summary>
	/// The census this session measured — every native called, routed or not, with its count.
	/// </summary>
	public string Census()
	{
		var routed = new List<string>();
		foreach ((string name, int count) in _handled)
		{
			routed.Add($"{name}={count}");
		}

		var pending = new List<string>();
		foreach ((string name, int count) in _unhandled)
		{
			pending.Add($"{name}={count}");
		}

		routed.Sort(System.StringComparer.Ordinal);
		pending.Sort(System.StringComparer.Ordinal);

		var observed = new List<string>();
		for (int i = pending.Count - 1; i >= 0; --i)
		{
			string name = pending[i][..pending[i].IndexOf('=')];
			if (_observed.ContainsKey(name))
			{
				observed.Add(pending[i]);
				pending.RemoveAt(i);
			}
		}

		observed.Sort(System.StringComparer.Ordinal);

		return $"{_seen} GUI call(s): " +
			$"ROUTED [{string.Join(" ", routed)}] " +
			$"OBSERVED [{string.Join(" ", observed)}] " +
			$"NOT ROUTED [{string.Join(" ", pending)}]";
	}
}
