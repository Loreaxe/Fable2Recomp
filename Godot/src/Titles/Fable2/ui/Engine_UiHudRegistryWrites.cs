namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The GUI registries' writers as the HUD's values take them (hooks/gui_registry_hooks.cpp): each
/// writer is stated at its entry from its arguments, so a name the registry holds no record for is
/// still drawn where the guest put it. The indices are the GUI's typed set (`sub_827F0F00`, each
/// handler's vtable slot 3 the attribute it binds): 1 MeterName, 2 SlideFaderName,
/// 3 SlideFader2Name, 6 TextName, 9 SlideToggleName, 10 SlideSetName, 12 ImageName.
/// </summary>
public static class Engine_UiHudRegistryWrites
{
	public const int MeterRegistry = 1;

	public const int TextRegistry = 6;

	/// <summary>The bindings a slide-playing registry's names drive, or null for another registry.</summary>
	public static Engine_UiBindings.Kind? SlideKind(int registry) => registry switch
	{
		2 => Engine_UiBindings.Kind.SlideFader,
		3 => Engine_UiBindings.Kind.SlideFader2,
		9 => Engine_UiBindings.Kind.SlideToggle,
		10 => Engine_UiBindings.Kind.SlideSet,
		_ => null,
	};

	/// <summary>
	/// One meter writer's statement, `@Gui.Meter registry hash op a b c`, in its own terms (binding
	/// +20 current, +24 rate in percent per second, +28 target, all clamped to 0..100):
	/// `set` (`sub_823FE210`) and `scrub` (`sub_823FE3D0`) snap to <paramref name="a"/>; `seek`
	/// (`sub_823FE2D0`) reaches <paramref name="a"/> over <paramref name="b"/> clock units; `run`
	/// (`sub_823FE068`) starts at <paramref name="a"/> and moves toward <paramref name="b"/> at
	/// <paramref name="c"/>; `freeze` (`sub_823FE4A0`) stops it where it stands. Only the meter
	/// registry's names are meters.
	/// </summary>
	/// <summary>
	/// Seconds per unit of the meter bindings' clock. The step moves a meter by rate × Δclock
	/// (`sub_82265D20` 0x82265e50) and a seek's rate is distance over its duration argument
	/// (`sub_823FE2D0` 0x823fe3a4); the health bar seeks over the INTEGER at its widget's +24
	/// (`sub_825E92C8` 0x825e93c0), which a run states as 500. Milliseconds.
	/// </summary>
	public const float ClockUnit = 0.001f;

	public static void StateMeter(Engine_HudValues values, int registry, uint hash, string op,
		float a, float b, float c)
	{
		if (registry != MeterRegistry)
		{
			return;
		}

		switch (op)
		{
			case "set":
			case "scrub":
				values.StateMeter(hash, a, 0f);
				break;
			case "seek":
				values.StateMeter(hash, a, b * ClockUnit);
				break;
			case "run":
				values.StateMeterRun(hash, a, b, c / ClockUnit);
				break;
			case "freeze":
				values.FreezeMeter(hash);
				break;
		}
	}

	/// <summary>
	/// Whether a TextName write is taken as the element's text. TopTitle and TopMain are not: the
	/// only writer of those names is the TopBox box fit, which restates the front record's
	/// strings after wrapping them to the GUEST's font metrics (`sub_823FC510` 0x823fc750
	/// `sub_823FEB28(reg[6], line, wrapped)`, the wrap `sub_823FCAD8`/`sub_823FCC20`; disc
	/// `sub_8257B4F0` 0x8257b730 `sub_8257C720`). A wrap break cannot be told from an authored
	/// one, and Godot wraps with its own font, so the TopBox's strings are stated once, from the
	/// record (Engine_UiHudTopBox), and laid out by Engine_UiTopBoxLayout.
	/// </summary>
	public static bool TakesText(uint hash) =>
		hash != TopTitleHash && hash != TopMainHash;

	private static readonly uint TopTitleHash = Refii.Core.Engine_GameHash.Fnv1("TopTitle");

	private static readonly uint TopMainHash = Refii.Core.Engine_GameHash.Fnv1("TopMain");

	/// <summary>A writer's float argument as the hook printed it (`%.9g`), 0 when absent.</summary>
	public static float Number(string? text) =>
		float.TryParse(text, System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : 0f;
}
