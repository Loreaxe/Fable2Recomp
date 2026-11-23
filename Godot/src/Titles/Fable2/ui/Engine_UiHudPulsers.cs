namespace Refii.Titles.Fable2;

using System.Collections.Generic;
using Refii.Ui;

/// <summary>
/// The HUD's pulsers, run from the guest's own start and stop.
/// </summary>
/// <remarks>
/// A pulser binding holds a mode (`+12`: 0 looping, 1 finishing its loop, 2 stopped), a phase
/// (`+20`, 0..1) and a rate (`+24`, per unit of the bindings' clock). Its two writers are
/// `sub_828F09C0(registry, hash, rate)` — rate set, mode 0 — and `sub_828F0A28(registry, hash)` — a
/// looping one becomes finishing (gui_registry_hooks.cpp states both as `@Gui.Pulser`). The step
/// `GuiPulser_Update` (0x822c21e0) is reproduced here: unless stopped, phase += rate × Δclock; past
/// 1 a finishing pulser goes to phase 0 and stops, a looping one wraps. A binding starts stopped at
/// 0 (`CHUDPulserManager_vf4` 0x827f0aa0).
/// </remarks>
public static class Engine_UiHudPulsers
{
	private sealed class Pulser
	{
		public float Phase;
		public float RatePerSecond;
		public int Mode = Stopped;
	}

	private const int Looping = 0;
	private const int Finishing = 1;
	private const int Stopped = 2;

	private static readonly Dictionary<uint, Pulser> Pulsers = new();

	private static ulong _advancedUsec;

	/// <summary>A pulser name the HUD's file binds: stopped at 0 until the guest starts it.</summary>
	public static void Know(uint hash)
	{
		if (!Pulsers.ContainsKey(hash))
		{
			Pulsers[hash] = new Pulser();
		}
	}

	/// <summary>`@Gui.Pulser … start rate`: the rate as the writer was handed it, per clock unit.</summary>
	public static void Start(uint hash, float rate)
	{
		Know(hash);
		Pulser pulser = Pulsers[hash];
		pulser.RatePerSecond = rate / Engine_UiHudRegistryWrites.ClockUnit;
		pulser.Mode = Looping;
	}

	/// <summary>`@Gui.Pulser … stop`: a looping pulser finishes its loop; any other is left as it is.</summary>
	public static void Stop(uint hash)
	{
		if (Pulsers.TryGetValue(hash, out Pulser? pulser) && pulser.Mode == Looping)
		{
			pulser.Mode = Finishing;
		}
	}

	/// <summary>The guest destroyed or rebuilt its registries: every binding is new, stopped at 0.</summary>
	public static void Reset()
	{
		foreach (Pulser pulser in Pulsers.Values)
		{
			pulser.Phase = 0f;
			pulser.RatePerSecond = 0f;
			pulser.Mode = Stopped;
		}
	}

	/// <summary>Advances every pulser to <paramref name="nowUsec"/> and states each one's phase and whether it runs.</summary>
	public static void Fill(Dictionary<uint, Engine_UiSource.HudPulser> into, ulong nowUsec)
	{
		float seconds = _advancedUsec == 0ul || nowUsec <= _advancedUsec ? 0f : (nowUsec - _advancedUsec) * 1e-6f;
		_advancedUsec = nowUsec;
		Advance(seconds);

		into.Clear();
		foreach ((uint hash, Pulser pulser) in Pulsers)
		{
			into[hash] = new Engine_UiSource.HudPulser(pulser.Phase, pulser.Mode != Stopped);
		}
	}

	/// <summary>One step of every pulser, as the guest steps it.</summary>
	public static void Advance(float seconds)
	{
		if (seconds <= 0f)
		{
			return;
		}

		foreach (Pulser pulser in Pulsers.Values)
		{
			if (pulser.Mode == Stopped)
			{
				continue;
			}

			pulser.Phase += pulser.RatePerSecond * seconds;
			if (pulser.Phase <= 1f)
			{
				continue;
			}

			if (pulser.Mode == Finishing)
			{
				pulser.Phase = 0f;
				pulser.Mode = Stopped;
				continue;
			}

			do
			{
				pulser.Phase -= 1f;
			}
			while (pulser.Phase > 1f);
		}
	}

	/// <summary>The phase and running state of one pulser, for tests; null when the name is unknown.</summary>
	public static Engine_UiSource.HudPulser? Of(uint hash) =>
		Pulsers.TryGetValue(hash, out Pulser? pulser)
			? new Engine_UiSource.HudPulser(pulser.Phase, pulser.Mode != Stopped)
			: null;

	/// <summary>Forgets every pulser: a new HUD file.</summary>
	public static void Clear()
	{
		Pulsers.Clear();
		_advancedUsec = 0ul;
	}
}
