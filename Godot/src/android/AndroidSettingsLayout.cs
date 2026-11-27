using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Refii.Launcher.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// Where the launcher's tabbed settings (DesktopSettingMetadata) show each setting on a phone. The desktop
/// layout puts a row in a tab, a section and a tier (the main list, or the tab's Advanced section); on a
/// phone the main lists hold what a player sets (<see cref="AndroidLauncherSettings.PlayerFacing"/>) and
/// everything else waits under Advanced, as the phone's launcher did before the tabs. The Keys and Debug
/// tabs keep the shared tiers. A desktop is
/// unchanged: <see cref="Present"/> returns the shared layout as it is.
/// </summary>
/// <remarks>
/// The rows the phone has a player set that the desktop layout puts under Advanced move beside their
/// neighbours (<see cref="PhonePlaces"/>). The phone's own tuning switches gather in one Advanced section,
/// <see cref="PhoneTuning"/>, rather than among the desktop's display rows; the three the shared layout
/// leaves to the desktop's lighting dialog, which does not list them, come back to the tab with it. A row
/// the phone has a name for (<see cref="AndroidSettingNames"/>) shows that name and its description. A
/// slider whose range a phone's values leave (render scale from 30%, Distant detail to 4) is widened to
/// take them, so the launcher shows and keeps what a preset or Options wrote.
/// </remarks>
public static class AndroidSettingsLayout
{
    /// <summary>The Advanced section the phone's own tuning switches are listed in.</summary>
    public const string PhoneTuning = "Phone Tuning";

    /// <summary>The player-facing rows the desktop layout puts under Advanced, by where a phone shows them:
    /// tab, section (one the desktop layout already has) and order, just after the row they go with.</summary>
    private static readonly Dictionary<string, (SettingsTab Tab, string Section, int Order)> PhonePlaces = new(StringComparer.Ordinal)
    {
        // After the frame limit (max_fps, 60).
        [AndroidFpsCounter.Key] = (SettingsTab.Display, "Frame Rate & Sync", 61),
        // After the lamps' live shadows (placed_light_shadows, 50).
        [AndroidBakedShadows.Key] = (SettingsTab.Graphics, "Lighting & Shadows", 51),
        // After skipping cutscenes (video_skip, 20).
        [AndroidStartup.SkipVideosKey] = (SettingsTab.Gameplay, "Features & Media", 21),
        // After vibration on or off (controller_vibration, 110).
        [AndroidQuickSettings.VibrationStrengthKey] = (SettingsTab.Controls, "Controller & Prompts", 111),
        // After the local co-op screen (coop_screen_mode, 110).
        [Refii.Game.GameSplitView.OrientationKey] = (SettingsTab.Gameplay, "Co-op & Online Multiplayer", 111),
    };

    /// <summary>
    /// The phone's presentation of <paramref name="definition"/>: <paramref name="shared"/> on a desktop
    /// (<see cref="GraphicsPresets.Offered"/> false), else its phone name, place, tier and range.
    /// </summary>
    /// <param name="definition">The setting.</param>
    /// <param name="shared">The shared (desktop) layout's presentation of it.</param>
    public static SettingPresentation Present(RuntimeCVarDefinition definition, SettingPresentation shared)
    {
        if (!GraphicsPresets.Offered)
        {
            return shared;
        }

        string name = definition.Name;
        SettingPresentation phone = shared;
        if (AndroidSettingNames.Name(name) is { } label)
        {
            // The shared summary describes the desktop's setting ("FSR ...", "HDR monitors"); the phone's own
            // description takes its place in the tooltip.
            phone = phone with { Label = label, Summary = AndroidSettingNames.About(name) is null ? phone.Summary : null };
        }

        if (PhonePlaces.TryGetValue(name, out var place))
        {
            phone = phone with { Tab = place.Tab, Section = place.Section, Order = place.Order, HandledByLightingModal = false };
        }
        else if (RuntimeCVarsAndroid.AllKeys.Contains(name) && phone.Tab != SettingsTab.Developer)
        {
            phone = phone with { Section = PhoneTuning, HandledByLightingModal = false };
        }

        // The Debug and Keys tabs keep their own tiers (Keys lists every binding at once); the preset is the
        // card above the tabs' lists.
        if (phone.Tab is not (SettingsTab.Developer or SettingsTab.Keybindings) && !phone.HandledByHeroOrComposite)
        {
            phone = phone with
            {
                Tier = AndroidLauncherSettings.PlayerFacing.Contains(name) ? SettingTier.Essential : SettingTier.Advanced,
            };
        }

        if (phone.Range is { } range && PhoneRange(name, range, definition.Type == RuntimeCVarType.Int) is { } wider)
        {
            phone = phone with { Range = wider };
        }

        return phone;
    }

    /// <summary>
    /// Whether a phone leaves <paramref name="tab"/> out of the tab bar: Keys (keyboard bindings a touch
    /// screen has no use for) and Debug (desktop profiling and capture tools). Their settings keep their
    /// values; a desktop shows both.
    /// </summary>
    public static bool HidesTab(SettingsTab tab) =>
        GraphicsPresets.Offered && tab is SettingsTab.Keybindings or SettingsTab.Developer;

    /// <summary>The value a phone's row resets to: High's for a setting the presets decide, the phone's
    /// 30 FPS cap for the frame limit; null on a desktop or for any other setting (its own default).</summary>
    /// <param name="name">The setting's key.</param>
    public static string? Baseline(string name) => !GraphicsPresets.Offered ? null
        : name == "max_fps" ? GraphicsPresets.AndroidFrameCap
        : GraphicsPresets.HighValues.TryGetValue(name, out string? high) ? high
        : null;

    /// <summary>The values a phone's tab reset writes in place of the settings' own defaults (<see cref="Baseline"/>
    /// for every setting it covers); null on a desktop.</summary>
    public static IReadOnlyDictionary<string, string>? TabDefaults()
    {
        if (!GraphicsPresets.Offered)
        {
            return null;
        }

        var defaults = new Dictionary<string, string>(GraphicsPresets.HighValues, StringComparer.Ordinal)
        {
            ["max_fps"] = GraphicsPresets.AndroidFrameCap,
        };
        return defaults;
    }

    /// <summary>A short line under the preset's dropdown saying what <paramref name="preset"/> gives, as
    /// docs/android.md's table does.</summary>
    public static string Summary(string preset) => GraphicsPresets.Canonical(preset) switch
    {
        GraphicsPresets.Low => "40% resolution, no sun shadow, the nearest four lamps, far trees as pictures, a quarter of the undergrowth and the modern lighting, tuned for phones: the most frame rate.",
        GraphicsPresets.Medium => "45% resolution, no sun shadow, far trees as pictures, flat-lit hedges, more lamps and detail than Low.",
        GraphicsPresets.High => "50% resolution, the sun's shadow where the area casts one, 16 lamps, far trees as pictures and three quarters of the undergrowth.",
        GraphicsPresets.Ultra => "67% resolution, longer sun shadows, more model detail and distance, tree pictures only far away, 32 lamps: for the fastest phones.",
        _ => "A setting the preset decides was changed by hand.",
    };

    /// <summary>
    /// <paramref name="range"/> widened to take every value a phone's presets or Options rows write for
    /// <paramref name="name"/>, with a step that lands on each; null when it already does.
    /// </summary>
    /// <param name="name">The setting's key.</param>
    /// <param name="range">The desktop slider's range.</param>
    /// <param name="isInt">Whether the setting is an integer, whose finest step is 1.</param>
    internal static NumericRange? PhoneRange(string name, NumericRange range, bool isInt)
    {
        List<double> values = PhoneValues(name).ToList();
        if (values.Count == 0)
        {
            return null;
        }

        double min = Math.Min(range.Min, values.Min());
        double max = Math.Max(range.Max, values.Max());
        bool OnGrid(double value, double step)
        {
            double steps = (value - min) / step;
            return Math.Abs(steps - Math.Round(steps)) < 1e-6;
        }

        double finest = isInt ? 1 : 0.01;
        double chosen = new[] { range.Step, 5, 1, 0.5, 0.25, 0.1, 0.05, 0.01 }
            .Where(step => step <= range.Step && step >= finest)
            .FirstOrDefault(step => values.All(value => OnGrid(value, step)) && OnGrid(range.Min, step), finest);
        if (min == range.Min && max == range.Max && chosen == range.Step)
        {
            return null;
        }

        return range with { Min = min, Max = max, Step = chosen };
    }

    /// <summary>Every number the phone's presets and the in-game Options rows write for <paramref name="name"/>.</summary>
    private static IEnumerable<double> PhoneValues(string name)
    {
        var texts = new List<string>();
        foreach (string preset in AndroidQuickSettings.Presets)
        {
            if (GraphicsPresets.Values(preset) is { } values && values.TryGetValue(name, out string? value))
            {
                texts.Add(value);
            }
        }

        foreach (AndroidGraphicsOption option in AndroidGraphicsOptions.All)
        {
            if (option.Key == name)
            {
                texts.AddRange(option.Steps);
            }

            foreach ((string key, string[] linked) in option.Linked)
            {
                if (key == name)
                {
                    texts.AddRange(linked);
                }
            }
        }

        foreach (string text in texts)
        {
            if (double.TryParse(text.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                yield return number;
            }
        }
    }
}
