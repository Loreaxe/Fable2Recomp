using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests.Launcher;

/// <summary>
/// Verifies desktop vs. mobile platform filtering, 5-tab + Developer tab organization, progressive
/// disclosure, contextual visibility, window-mode composition, cross-tab search, and keybinding slot editing.
/// </summary>
[TestSuite]
public sealed class SettingsPanelTabsAndPlatformFilterTests
{
	[AfterTest]
	public void RestorePlatform() => GraphicsPresets.Offered = Refii.Core.Engine_Platform.IsAndroid;

	[TestCase]
	public void EveryExplicitPresentationNamesADefinedSetting()
	{
		// A presentation is looked up by the cvar's own name, so a key that names no cvar never
		// applies and the setting falls to the default classification (coop_split_orientation).
		var defined = RuntimeCVarSet.All.Select(d => d.Name)
			.Concat(RuntimeCVarsAndroid.AllKeys)
			.ToHashSet(StringComparer.Ordinal);
		var unmatched = DesktopSettingMetadata.ExplicitNames.Where(name => !defined.Contains(name)).ToList();
		AssertThat(unmatched.Count)
			.OverrideFailureMessage("presentations naming no setting: " + string.Join(", ", unmatched))
			.IsEqual(0);
	}

	[TestCase]
	public void DesktopHidesEveryMobileOnlyCVarAndExposesMatchWindowResolution()
	{
		GraphicsPresets.Offered = false;

		AssertThat(RuntimeCVarsAndroid.AllKeys.Count).IsGreaterEqual(35);
		foreach (string mobileKey in RuntimeCVarsAndroid.AllKeys)
		{
			AssertThat(AndroidLauncherSettings.Shows(mobileKey))
				.OverrideFailureMessage($"Mobile key '{mobileKey}' leaked onto desktop")
				.IsFalse();

			RuntimeCVarDefinition? def = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == mobileKey);
			if (def is not null)
			{
				AssertThat(RuntimeConfig.ShouldShowCVar(def))
					.OverrideFailureMessage($"ShouldShowCVar returned true on desktop for '{mobileKey}'")
					.IsFalse();
			}
		}

		RuntimeCVarDefinition resDef = RuntimeCVarSet.All.First(d => d.Name == "display_resolution");
		List<RuntimeCVarOption> options = RuntimeSettingsModel.OptionsFor(resDef, new Dictionary<string, string>());
		AssertThat(options.Count).IsGreater(1);
		AssertThat(options[0].Value).IsEqual("native");
		AssertThat(options[0].Label).IsEqual("Match Window");
	}

	[TestCase]
	public void EveryDesktopVisibleCVar_MapsCleanlyIntoTabsWithProgressiveDisclosure()
	{
		GraphicsPresets.Offered = false;
		var model = new RuntimeSettingsModel();
		model.ApplyDesktopPreset(DesktopGraphicsPresets.Balanced);

		// Display tab shows Window/Monitor, Frame Rate, Upscaling & AA; FSR sub-options visible when FSR active
		(var displayEssential, var displayAdvanced) = model.BuildTabSections(SettingsTab.Display);
		var displayEssentialNames = displayEssential.SelectMany(s => s.Rows).Select(d => d.Name).ToHashSet();
		AssertThat(displayEssentialNames).Contains("display_aspect");
		AssertThat(displayEssentialNames).Contains("display_resolution");
		AssertThat(displayEssentialNames).Contains("vsync_mode");
		AssertThat(displayEssentialNames).Contains("max_fps");
		AssertThat(displayEssentialNames).Contains("render_scaler");
		AssertThat(displayEssentialNames).Contains("fsr_quality");
		AssertThat(displayEssentialNames).NotContains("key_move_forward");

		// Switching upscaler to bilinear hides contextual FSR sub-rows
		model.Values["render_scaler"] = "bilinear";
		var bilinearDisplayNames = model.BuildTabSections(SettingsTab.Display).Essential
			.SelectMany(s => s.Rows).Select(d => d.Name).ToHashSet();
		AssertThat(bilinearDisplayNames).NotContains("fsr_quality");
		AssertThat(bilinearDisplayNames).NotContains("fsr_sharpness");

		// Display Advanced holds low-frequency technical switches
		var displayAdvNames = displayAdvanced.SelectMany(s => s.Rows).Select(d => d.Name).ToHashSet();
		AssertThat(displayAdvNames).Contains("rendering_driver");
		AssertThat(displayAdvNames).Contains("debanding");

		// Keybindings live exclusively in Keybindings tab, not Controls tab
		var controlsNames = model.BuildTabSections(SettingsTab.Controls).Essential
			.SelectMany(s => s.Rows).Select(d => d.Name).ToHashSet();
		AssertThat(controlsNames).Contains("mouse_sensitivity");
		AssertThat(controlsNames).NotContains("key_move_forward");

		var keybindNames = model.BuildTabSections(SettingsTab.Keybindings).Essential
			.SelectMany(s => s.Rows).Select(d => d.Name).ToHashSet();
		AssertThat(keybindNames).Contains("key_move_forward");
		AssertThat(keybindNames).Contains("key_attack");

		// Debug & Profiling tab exposes profiling/logging CVars immediately and tucks internals into Advanced
		AssertThat(DesktopSettingMetadata.TabTitle(SettingsTab.Developer)).IsEqual("Debug");
		AssertThat(DesktopSettingMetadata.TabFullName(SettingsTab.Developer)).IsEqual("Debug & Profiling");

		(var devEssential, var devAdvanced) = model.BuildTabSections(SettingsTab.Developer);
		var devNames = devEssential.SelectMany(s => s.Rows).Select(d => d.Name).ToHashSet();
		AssertThat(devNames).Contains("tracy");
		AssertThat(devNames).Contains("tracy_advanced");
		AssertThat(devNames).Contains("renderdoc");
		AssertThat(devNames).Contains("gpu_profile");
		AssertThat(devNames).Contains("log_level");
		AssertThat(displayEssentialNames).NotContains("tracy");

		var devAdvNames = devAdvanced.SelectMany(s => s.Rows).Select(d => d.Name).ToHashSet();
		AssertThat(devAdvNames).Contains("gc_heaps");
		AssertThat(devAdvNames).Contains("game_data_root");
	}

	[TestCase]
	public void WindowModeComposite_AndCrossTabSearch_AndKeybindingSlotHelper_WorkEndToEnd()
	{
		GraphicsPresets.Offered = false;
		var model = new RuntimeSettingsModel();

		model.ApplyWindowMode("borderless");
		AssertThat(model.GetWindowMode()).IsEqual("borderless");
		AssertThat(model.Values["fullscreen"]).IsEqual("false");
		AssertThat(model.Values["borderless"]).IsEqual("true");

		model.ApplyWindowMode("fullscreen");
		AssertThat(model.GetWindowMode()).IsEqual("fullscreen");
		AssertThat(model.Values["fullscreen"]).IsEqual("true");
		AssertThat(model.Values["borderless"]).IsEqual("false");

		model.ApplyWindowMode("windowed");
		AssertThat(model.GetWindowMode()).IsEqual("windowed");

		// Search finds essential, advanced, lighting-modal, and keybinding entries across tabs
		var shadowHits = model.SearchAll("shadowing", includeDeveloper: false)
			.SelectMany(g => g.Rows).Select(d => d.Name).ToHashSet();
		AssertThat(shadowHits).Contains("placed_shadow_limit");
		AssertThat(shadowHits).NotContains("omni_shadow_mode");

		// placed_shadow_limit is contextually hidden in the Graphics tab when placed_light_shadows is off
		model.Values["placed_light_shadows"] = "true";
		var withShadowsNames = model.BuildTabSections(SettingsTab.Graphics).Essential
			.SelectMany(s => s.Rows).Select(d => d.Name).ToHashSet();
		AssertThat(withShadowsNames).Contains("placed_shadow_limit");

		model.Values["placed_light_shadows"] = "false";
		var withoutShadowsNames = model.BuildTabSections(SettingsTab.Graphics).Essential
			.SelectMany(s => s.Rows).Select(d => d.Name).ToHashSet();
		AssertThat(withoutShadowsNames).NotContains("placed_shadow_limit");

		var sprintHits = model.SearchAll("sprint", includeDeveloper: false)
			.SelectMany(g => g.Rows).Select(d => d.Name).ToHashSet();
		AssertThat(sprintHits).Contains("key_sprint");

		// KeybindingSlotHelper updates, deduplicates, clears, and formats tokens cleanly
		AssertThat(KeybindingSlotHelper.FormatToken("MouseLeft")).IsEqual("Left Click");
		AssertThat(KeybindingSlotHelper.FormatToken("MouseRight")).IsEqual("Right Click");

		string reboundPrimary = KeybindingSlotHelper.UpdateSlot("MouseRight,T", "MouseRight,T", isModBinding: false, slotIndex: 0, "Mouse4");
		AssertThat(reboundPrimary).IsEqual("Mouse4,T");

		string clearedPrimary = KeybindingSlotHelper.UpdateSlot(reboundPrimary, "MouseRight,T", isModBinding: false, slotIndex: 0, null);
		AssertThat(clearedPrimary).IsEqual("T");

		string clearedAll = KeybindingSlotHelper.UpdateSlot(clearedPrimary, "MouseRight,T", isModBinding: false, slotIndex: 0, null);
		AssertThat(clearedAll).IsEqual("none");
	}
}
