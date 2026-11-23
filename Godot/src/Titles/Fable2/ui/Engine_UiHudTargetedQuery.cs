namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The targeted-citizen readout, as the guest states it.
/// </summary>
/// <remarks>
/// The guest's publisher (`sub_823FD410`) and its raise and lower (`sub_823FD8E8`, `sub_823FD9B0`)
/// write the readout through the registry writers, which gui_registry_hooks.cpp states as
/// `@Gui.Slide`: registry 9 holds the toggles (`TargetedQuery`, `TargetedQueryButton`, 2 on and 1
/// off, 0x823fd71c) and registry 10 the slide set (`TargetedQuery`, the opinion axis plus one,
/// 0x823fd760). The entity it publishes for is the publisher's own argument (`@Hud.Targeted`).
/// The HUD draws from those statements; this is the same answer for the code that asks.
/// </remarks>
public static class Engine_UiHudTargetedQuery
{
	private const int ToggleRegistry = 9;
	private const int SlideSetRegistry = 10;

	private static readonly uint Readout = Refii.Core.Engine_GameHash.Fnv1("TargetedQuery");
	private static readonly uint ReadoutButton = Refii.Core.Engine_GameHash.Fnv1("TargetedQueryButton");

	/// <summary>The entity the publisher last published for, 0 for none. `@Hud.Targeted`.</summary>
	public static uint Entity { get; set; }

	/// <summary>The readout is raised.</summary>
	public static bool Present { get; private set; }

	/// <summary>The readout's button prompt is on.</summary>
	public static bool Button { get; private set; }

	/// <summary>The slide set the guest last stated, the opinion axis plus one; 0 before any.</summary>
	public static int SlideSet { get; private set; }

	private static bool _wasUp;

	private static bool _faded;

	/// <summary>
	/// Takes the registry's current statements; once a frame, after the calls are drained.
	/// </summary>
	/// <remarks>
	/// THE READOUT'S TOGGLE AND FADE ARE STATED BY NAME HERE TOO, from the raise the guest states
	/// (`sub_823FD8E8` / `sub_823FD9B0`): fader index 1 in, 2 out fast. Without them the readout
	/// draws with no panel behind it. Which binding the registry statements alone do not reach is
	/// not established.
	/// </remarks>
	public static void Refresh(Engine_HudValues values)
	{
		Present = values.RegistrySlide(ToggleRegistry, Readout) == 2;
		Button = values.RegistrySlide(ToggleRegistry, ReadoutButton) == 2;
		SlideSet = values.RegistrySlide(SlideSetRegistry, Readout) ?? 0;

		values.StateVisible(Readout, Present);
		if (Present != _wasUp || !_faded)
		{
			values.StateFade(Readout, Present ? 1 : 2);
			_wasUp = Present;
			_faded = true;
		}
	}

}
