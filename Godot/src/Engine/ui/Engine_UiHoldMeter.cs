namespace Refii.Ui;

/// <summary>
/// A HOLD-TO-ACCEPT METER ON GODOT'S CLOCK: it fills while its button is held and drains at the same
/// rate when it is let go, clamped to [0, 1], and accepts on the step its fill crosses full — once
/// per crossing, so a press held past full accepts once and a second accept needs the fill to drop
/// and climb again. The title states how long a full hold takes; the meter knows no device, so a pad
/// button, a key and a mouse held on the prompt are one hold.
/// </summary>
public sealed class Engine_UiHoldMeter
{
	public Engine_UiHoldMeter(double seconds) => Seconds = seconds;

	/// <summary>How long a hold from empty to full takes.</summary>
	public double Seconds { get; }

	/// <summary>The fill, 0..1.</summary>
	public float Progress { get; private set; }

	/// <summary>Moves the fill by <paramref name="delta"/> seconds held or released; true on the step it reaches full.</summary>
	public bool Step(double delta, bool held)
	{
		float before = Progress;
		float step = Seconds <= 0.0 ? 1f : (float)(delta / Seconds);
		Progress = System.Math.Clamp(Progress + (held ? step : -step), 0f, 1f);
		return Progress >= 1f && before < 1f;
	}

	/// <summary>Empties the meter (a new box).</summary>
	public void Reset() => Progress = 0f;
}
