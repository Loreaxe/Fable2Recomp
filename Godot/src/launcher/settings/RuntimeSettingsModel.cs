using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace Refii.Launcher.Core;

public sealed partial class RuntimeSettingsModel
{
    public string ConfigPath { get; private set; } = string.Empty;
    public string SecondaryConfigPath { get; private set; } = string.Empty;
    public SortedDictionary<string, string> Values { get; private set; } = new(StringComparer.Ordinal);
    public string StatusMessage { get; private set; } = string.Empty;
    public string Filter { get; set; } = string.Empty;

    private SortedDictionary<string, string> _fable2StoredValues = new(StringComparer.Ordinal);
    private SortedDictionary<string, string> _fable3StoredValues = new(StringComparer.Ordinal);

    /// <summary>
    /// <see cref="Values"/> as the last load or save left them: what <see cref="Save"/> compares
    /// against, so only the settings the player changed since are written.
    /// </summary>
    private SortedDictionary<string, string> _loadedValues = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string>? _modsRootsOverride;

    private const string PanelSection = "Settings";
    private const string ShowAdvancedKey = "ShowAdvanced";
    private const string ShowDeveloperKey = "ShowDebugTab";
    private const string ActiveTabKey = "ActiveTab";

    /// <summary>
    /// Whether the tuning dials are listed. Off, the panel shows the decisions a player makes
    /// — the look, the display, the controls — and the remaster's dials, the developer switches
    /// and the runtime's own knobs stay out of sight; a non-empty filter searches everything.
    /// </summary>
    public bool ShowAdvanced
    {
        get => Ini.GetBool(ShowAdvancedKey, false, PanelSection);
        set
        {
            Ini.SetBool(ShowAdvancedKey, value, PanelSection);
            Ini.Save();
        }
    }

    /// <summary>
    /// Whether the <see cref="SettingsTab.Developer"/> (Debug &amp; Profiling) tab is shown in the tab bar.
    /// Defaults to <see langword="true"/> so profiling and diagnostic CVars (such as Tracy and RenderDoc) are
    /// immediately accessible.
    /// </summary>
    public bool ShowDeveloperSettings
    {
        get => Ini.GetBool(ShowDeveloperKey, true, PanelSection);
        set
        {
            Ini.SetBool(ShowDeveloperKey, value, PanelSection);
            Ini.Save();
        }
    }

    /// <summary>The last selected <see cref="SettingsTab"/> in the launcher settings panel.</summary>
    public SettingsTab ActiveTab
    {
        get => Enum.TryParse<SettingsTab>(Ini.GetString(ActiveTabKey, "Display", PanelSection), ignoreCase: true, out var tab)
            ? tab
            : SettingsTab.Display;
        set
        {
            Ini.SetString(ActiveTabKey, value.ToString(), PanelSection);
            Ini.Save();
        }
    }

    /// <summary>
    /// A setting that tunes rather than decides. The remaster's lighting and texture dials, the
    /// developer switches, the picture's internals, and every runtime cvar that is not a key
    /// binding. The two decisions a look is made of stay in the plain view.
    /// </summary>
    public static bool IsAdvanced(RuntimeCVarDefinition definition)
    {
        if (Refii.AndroidRuntime.AndroidLauncherSettings.Advanced(definition.Name) is bool phone) return phone;
        if (definition.Name is "lighting_model" or "materials_modernized" or "texture_upscaling_enabled"
            or "lighting_preset" or "hdr_output" or "bloom_scale" or GraphicsPresets.Key or DesktopGraphicsPresets.Key)
        {
            return false;
        }
        // fullscreen, monitor and vsync are runtime-side entries re-homed under Display; their
        // registry category ("UI/Window") would otherwise count them as advanced.
        if (RuntimeConfig.IsDisplayCVar(definition.Name))
        {
            return false;
        }
        if (definition.Category is RuntimeCVarsGodot.DeveloperCategory
            or RuntimeCVarsGodot.LightingCategory or RuntimeCVarsGodot.Category)
        {
            return true;
        }
        if (definition.Name is "renderdoc" or "renderdoc_path" or "gc_heaps" or "perf_overlay"
            or "gpu_profile" or "render_thread" or "android_render_thread" or "draw_latency_ticks" or "mesh_lod_pixels"
            or "prop_cull_min_pixels" or "prop_fade_fraction" or "texture_mipmap_bias"
            or "cull_back_faces" or "video_skip_after" or "debanding" or "occlusion_culling")
        {
            return true;
        }
        // The runtime's own registry, apart from what a player sets: the bindings, and the
        // RexNet settings, which are the multiplayer options (user, 2026-10-11).
        bool godotSide = RuntimeCVarsGodot.Definitions.Contains(definition);
        return !godotSide
            && !definition.Category.StartsWith("Input", StringComparison.Ordinal)
            && definition.Category != "RexNet";
    }

    /// <summary>
    /// The look the switches add up to: "modern_fable", "modern_filmic", "classic", or "custom" when mixed.
    /// </summary>
    public string CurrentLook()
    {
        string lighting = Values.TryGetValue("lighting_model", out string? model) ? model.Trim().Trim('"') : "modern";
        bool classicLighting = lighting.Equals("classic", StringComparison.OrdinalIgnoreCase)
            || lighting.Equals("fable", StringComparison.OrdinalIgnoreCase);
        bool modernized = !Values.TryGetValue("materials_modernized", out string? materials)
            || RuntimeConfig.NormalizeBoolValue(materials) == "true";
        if (!classicLighting && modernized)
        {
            string curve = Values.TryGetValue("modern_tone_curve", out string? tc)
                ? tc.Trim().Trim('"').ToLowerInvariant()
                : "fable";
            return curve switch
            {
                "" or "fable" => "modern_fable",
                "filmic" or "all" => "modern_filmic",
                _ => "custom",
            };
        }
        if (classicLighting && !modernized)
        {
            return "classic";
        }
        return "custom";
    }

    /// <summary>
    /// Sets the look as one decision: the lighting model, materials switch, and modern tone curve
    /// together, since "the original game" or a modern tonemapper choice is a single top-level preset.
    /// </summary>
    public void ApplyLook(string look)
    {
        bool classic = look == "classic";
        Values["lighting_model"] = classic ? "classic" : "modern";
        Values["materials_modernized"] = classic ? "false" : "true";
        if (!classic)
        {
            Values["modern_tone_curve"] = look == "modern_filmic" ? "all" : "fable";
        }
        Save();
    }

    public sealed record LightingPreset(
        double LightRange,
        double ShadowRange);

    public static readonly IReadOnlyDictionary<string, LightingPreset> Presets =
        new Dictionary<string, LightingPreset>(StringComparer.OrdinalIgnoreCase)
        {
            ["performance"] = new(45.0, 30.0),
            ["balanced"] = new(65.0, 50.0),
            ["quality"] = new(85.0, 70.0),
        };

    /// <summary>The lighting preset the current range values match, or "custom".</summary>
    public string CurrentLightingPreset()
    {
        double lightRange = ParseDouble(GetValueOrDefault("placed_light_range", "65.0"), "65.0");
        double shadowRange = ParseDouble(GetValueOrDefault("placed_shadow_range", "50.0"), "50.0");

        foreach ((string name, LightingPreset preset) in Presets)
        {
            if (Math.Abs(lightRange - preset.LightRange) < 1.0 &&
                Math.Abs(shadowRange - preset.ShadowRange) < 1.0)
            {
                return name;
            }
        }

        return "custom";
    }

    /// <summary>Applies a named preset to individual lighting and shadow range CVars.</summary>
    public void ApplyLightingPreset(string presetName)
    {
        if (Presets.TryGetValue(presetName, out LightingPreset? preset))
        {
            string lower = presetName.ToLowerInvariant();
            Values["lighting_preset"] = lower;
            Values["placed_light_range"] = preset.LightRange.ToString("0.0", CultureInfo.InvariantCulture);
            Values["placed_shadow_range"] = preset.ShadowRange.ToString("0.0", CultureInfo.InvariantCulture);
            Values["terrain_ao_resolution"] = lower switch
            {
                "performance" => "2048",
                "quality" => "8192",
                _ => "4096",
            };
            Values["static_shadow_resolution"] = lower == "quality" ? "4096" : "2048";
            if (DesktopGraphicsPresets.Offered)
            {
                Values[DesktopGraphicsPresets.Key] = CurrentDesktopPreset();
            }
            if (GraphicsPresets.Offered)
            {
                Values[GraphicsPresets.Key] = CurrentGraphicsPreset();
            }
            Save();
        }
    }

    /// <summary>Returns the desktop performance preset matching <see cref="Values"/>, or <c>"custom"</c>.</summary>
    public string CurrentDesktopPreset() => DesktopGraphicsPresets.Current(Values);

    /// <summary>Applies a named desktop performance preset (<c>low</c>, <c>balanced</c>, <c>high</c>, <c>ultra</c>) and saves.</summary>
    public bool ApplyDesktopPreset(string presetName)
    {
        if (!DesktopGraphicsPresets.Apply(presetName, Values))
        {
            return false;
        }

        Values["lighting_preset"] = CurrentLightingPreset();
        Save();
        return true;
    }

    /// <summary>Updates <c>performance_preset</c> when one of its member settings changes.</summary>
    public bool RefreshDesktopPreset(string changedSetting)
    {
        if (!DesktopGraphicsPresets.Offered || !DesktopGraphicsPresets.Members.Contains(changedSetting))
        {
            return false;
        }

        Values[DesktopGraphicsPresets.Key] = CurrentDesktopPreset();
        Save();
        return true;
    }

    /// <summary>Re-evaluates <c>performance_preset</c> after bulk changes (such as the lighting dialog).</summary>
    public void RefreshDesktopPreset()
    {
        if (!DesktopGraphicsPresets.Offered)
        {
            return;
        }

        string current = CurrentDesktopPreset();
        if (Values.TryGetValue(DesktopGraphicsPresets.Key, out string? stored) && stored == current)
        {
            return;
        }

        Values[DesktopGraphicsPresets.Key] = current;
        Save();
    }

    /// <summary>
    /// Composite window presentation mode combining <c>fullscreen</c> and <c>borderless</c>:
    /// <c>"windowed"</c>, <c>"borderless"</c>, or <c>"fullscreen"</c>.
    /// </summary>
    public string GetWindowMode()
    {
        bool fs = Values.TryGetValue("fullscreen", out string? f) && RuntimeConfig.NormalizeBoolValue(f) == "true";
        if (fs)
        {
            return "fullscreen";
        }

        bool bl = Values.TryGetValue("borderless", out string? b) && RuntimeConfig.NormalizeBoolValue(b) == "true";
        return bl ? "borderless" : "windowed";
    }

    /// <summary>Applies the composite window mode (<c>windowed</c>, <c>borderless</c>, or <c>fullscreen</c>) and saves.</summary>
    public void ApplyWindowMode(string mode)
    {
        switch (mode)
        {
            case "fullscreen":
                Values["fullscreen"] = "true";
                Values["borderless"] = "false";
                break;
            case "borderless":
                Values["fullscreen"] = "false";
                Values["borderless"] = "true";
                break;
            default:
                Values["fullscreen"] = "false";
                Values["borderless"] = "false";
                break;
        }

        Save();
    }

    private void ReadDesktopPreset()
    {
        if (DesktopGraphicsPresets.Offered)
        {
            Values[DesktopGraphicsPresets.Key] = CurrentDesktopPreset();
        }
    }

    private void ApplyDesktopDefaults()
    {
        if (DesktopGraphicsPresets.Offered)
        {
            DesktopGraphicsPresets.Apply(DesktopGraphicsPresets.DefaultPreset, Values);
        }
    }

    private bool _initialized;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        Reload();
    }

    /// <summary>
    /// Reads both <c>refii.toml</c> and <c>refiii.toml</c> into the unified settings model so one
    /// shared settings panel edits shared engine CVars across both games while keeping title-owned
    /// CVars and per-title roots isolated.
    /// </summary>
    public void Reload() =>
        ReloadFromPaths(
            Installer.GetRuntimeConfigPath(Refii.Core.Engine_Title.Fable2),
            Installer.GetRuntimeConfigPath(Refii.Core.Engine_Title.Fable3),
            _modsRootsOverride,
            ensureDefaults: true);

    /// <summary>
    /// Loads the unified settings model from explicit Fable 2 and Fable 3 TOML paths.
    /// </summary>
    public void ReloadFromPaths(
        string fable2ConfigPath,
        string fable3ConfigPath,
        IReadOnlyDictionary<string, string>? modsRootsByTitleKey = null,
        bool ensureDefaults = true)
    {
        StatusMessage = string.Empty;
        ConfigPath = fable2ConfigPath;
        SecondaryConfigPath = fable3ConfigPath;
        _modsRootsOverride = modsRootsByTitleKey;

        // Refresh unified setting definitions (including enabled mod input bindings across all titles).
        if (modsRootsByTitleKey is null)
        {
            RuntimeCVarSet.Refresh();
        }
        else
        {
            RuntimeCVarSet.Refresh(modsRootsByTitleKey);
        }

        // THE ACTIVE TITLE'S FILE ONLY: loading the panel prepares the file of the game the launcher
        // is on (a Fable 3 start never touches refii.toml); the other title's file is read as it
        // stands and prepared when that title is launched (LaunchController.Launch). The pass edits
        // only its own keys (RuntimeConfig.EnsureDefaultsForTitle).
        if (ensureDefaults)
        {
            bool fable3 = Refii.Core.Engine_Title.Active == Refii.Core.Engine_Title.Fable3;
            string path = fable3 ? SecondaryConfigPath : ConfigPath;
            if ((!fable3 || System.IO.File.Exists(path)) &&
                !RuntimeConfig.EnsureDefaultsForTitle(Refii.Core.Engine_Title.Active, path, out string error))
            {
                StatusMessage = "Failed to prepare " + System.IO.Path.GetFileName(path) + ": " + error;
            }
        }

        _fable2StoredValues = RuntimeConfig.ReadValues(ConfigPath);
        _fable3StoredValues = RuntimeConfig.ReadValues(SecondaryConfigPath);

        Values = new SortedDictionary<string, string>(_fable2StoredValues, StringComparer.Ordinal);

        // Overlay Fable 3-owned settings from refiii.toml, and fall back to refiii.toml for shared
        // settings when refii.toml had not stored them yet.
        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All)
        {
            if (definition.OwnerTitle == Refii.Core.Engine_Title.Fable3.Folder)
            {
                if (_fable3StoredValues.TryGetValue(definition.Name, out string? f3Val))
                {
                    Values[definition.Name] = f3Val;
                }
            }
            else if (definition.OwnerTitle is null &&
                     !IsPerTitleIsolatedRoot(definition.Name) &&
                     !RuntimeCVarsGodot.IsKeyBinding(definition.Name) &&
                     !Values.ContainsKey(definition.Name) &&
                     _fable3StoredValues.TryGetValue(definition.Name, out string? sharedFallback))
            {
                Values[definition.Name] = sharedFallback;
            }
        }

        Refii.Core.Engine_Switches.Invalidate();
        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All)
        {
            if (!Values.ContainsKey(definition.Name))
            {
                Values[definition.Name] = definition.DefaultValue;
            }
        }

        if (Values.TryGetValue("placed_light_range", out string? lrStr) &&
            double.TryParse(lrStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedLr) &&
            parsedLr <= 8.0)
        {
            Values["placed_light_range"] = (parsedLr <= 1.1 ? 40.0 : parsedLr >= 1.9 ? 80.0 : 60.0).ToString("0.0", CultureInfo.InvariantCulture);
        }

        Values["lighting_preset"] = CurrentLightingPreset();
        ReadDesktopPreset();
        ReadGraphicsPreset();  // the phone's, RuntimeSettingsModel.GraphicsPreset.cs
        _loadedValues = new SortedDictionary<string, string>(Values, StringComparer.Ordinal);
    }

    private static bool IsPerTitleIsolatedRoot(string key) =>
        key is "game_data_root" or "cache_root" or "user_data_root";

    /// <summary>
    /// Writes the settings the player CHANGED since the last load or save, each into the file of every
    /// title it applies to, as an in-place edit of that key's line (<see cref="RuntimeConfig.EditValuesForTitle"/>):
    /// no other line of either file changes, no key is dropped, nothing is reworded. A save with nothing
    /// changed writes nothing — the Play button, a rebuild and closing the drawer all save, so a start
    /// never rewrites a file. Where a setting goes: a shared one to both files; a title's own (its table
    /// alone holds it) to that title's file; the keyboard rows and the per-title roots the panel holds
    /// are <c>refii.toml</c>'s, so to that file only (Fable 3's keys share their names, not their
    /// meaning); a key no table defines, or one <see cref="RuntimeConfig.ShouldWriteCVar"/> declines, to
    /// neither (its line stays as it stands). A file that does not exist yet is written whole from its
    /// title's table (nothing in it to keep).
    /// </summary>
    public void Save()
    {
        if (string.IsNullOrEmpty(ConfigPath))
        {
            ConfigPath = Installer.GetRuntimeConfigPath(Refii.Core.Engine_Title.Fable2);
        }

        if (string.IsNullOrEmpty(SecondaryConfigPath))
        {
            SecondaryConfigPath = Installer.GetRuntimeConfigPath(Refii.Core.Engine_Title.Fable3);
        }

        (Dictionary<string, string> fable2Set, Dictionary<string, string> fable3Set) = Route(Changed(_loadedValues, Values));
        // A root the panel cleared keeps the file's (the panel shows an empty root as "default").
        foreach (string rootKey in new[] { "game_data_root", "cache_root" })
        {
            if (fable2Set.TryGetValue(rootKey, out string? root) && string.IsNullOrEmpty(root) &&
                _fable2StoredValues.TryGetValue(rootKey, out string? stored) && !string.IsNullOrEmpty(stored))
            {
                fable2Set.Remove(rootKey);
            }
        }
        if (fable2Set.Count == 0 && fable3Set.Count == 0)
        {
            StatusMessage = "No settings changed";
            return;
        }

        if (fable2Set.Count != 0 && !SaveFable2(fable2Set, out string error2))
        {
            StatusMessage = "Failed to save " + System.IO.Path.GetFileName(ConfigPath) + ": " + error2;
            return;
        }

        if (fable3Set.Count != 0 && !SaveFable3(fable3Set, out string error3))
        {
            StatusMessage = "Failed to save " + System.IO.Path.GetFileName(SecondaryConfigPath) + ": " + error3;
            return;
        }

        _fable2StoredValues = RuntimeConfig.ReadValues(ConfigPath);
        _fable3StoredValues = RuntimeConfig.ReadValues(SecondaryConfigPath);
        _loadedValues = new SortedDictionary<string, string>(Values, StringComparer.Ordinal);
        List<string> saved = fable2Set.Keys.Union(fable3Set.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        string files = string.Join(" & ", new[]
        {
            fable2Set.Count != 0 ? System.IO.Path.GetFileName(ConfigPath) : null,
            fable3Set.Count != 0 ? System.IO.Path.GetFileName(SecondaryConfigPath) : null,
        }.Where(name => name is not null));
        StatusMessage = (saved.Count <= 4 ? "Saved " + string.Join(", ", saved) : $"Saved {saved.Count} settings") +
                        " (" + files + ")";
        Refii.Core.Engine_Switches.Invalidate();
    }

    /// <summary>The keys <paramref name="now"/> holds with a value <paramref name="loaded"/> did not, and their values.</summary>
    internal static Dictionary<string, string> Changed(
        IReadOnlyDictionary<string, string> loaded, IReadOnlyDictionary<string, string> now)
    {
        var changed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in now)
        {
            if (!loaded.TryGetValue(key, out string? was) || was != value)
            {
                changed[key] = value;
            }
        }

        return changed;
    }

    /// <summary>
    /// Which file each changed setting goes to (see <see cref="Save"/>): Fable 2's table,
    /// <c>refii.toml</c>; Fable 3's table, <c>refiii.toml</c>, but for the keyboard rows and the roots.
    /// A setting in no table (the pad maps, the trace switches) is the launcher's own and goes to
    /// <c>refii.toml</c>, the file the panel reads its values from; only a setting that is Fable 3's
    /// alone stays out of it.
    /// </summary>
    internal static (Dictionary<string, string> Fable2, Dictionary<string, string> Fable3) Route(
        IReadOnlyDictionary<string, string> changed)
    {
        var fable2Table = new HashSet<string>(
            RuntimeCVarSet.For(Refii.Core.Engine_Title.Fable2).Select(d => d.Name), StringComparer.Ordinal);
        var fable2Names = new HashSet<string>(
            RuntimeCVarSet.For(Refii.Core.Engine_Title.Fable2).Where(RuntimeConfig.ShouldWriteCVar).Select(d => d.Name),
            StringComparer.Ordinal);
        var fable3Names = new HashSet<string>(
            RuntimeCVarSet.For(Refii.Core.Engine_Title.Fable3).Where(RuntimeConfig.ShouldWriteCVar).Select(d => d.Name),
            StringComparer.Ordinal);
        var fable3Table = new HashSet<string>(
            RuntimeCVarSet.For(Refii.Core.Engine_Title.Fable3).Select(d => d.Name), StringComparer.Ordinal);
        var fable2 = new Dictionary<string, string>(StringComparer.Ordinal);
        var fable3 = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in changed)
        {
            if (fable2Names.Contains(key) || (!fable2Table.Contains(key) && !fable3Table.Contains(key)))
            {
                fable2[key] = value;
            }

            if (fable3Names.Contains(key) && !IsPerTitleIsolatedRoot(key) && !RuntimeCVarsGodot.IsKeyBinding(key))
            {
                fable3[key] = value;
            }
        }

        return (fable2, fable3);
    }

    /// <summary><paramref name="set"/> into <c>refii.toml</c>: edited in place, or written whole when there is none yet.</summary>
    private bool SaveFable2(Dictionary<string, string> set, out string error)
    {
        if (System.IO.File.Exists(ConfigPath))
        {
            return RuntimeConfig.EditValuesForTitle(Refii.Core.Engine_Title.Fable2, ConfigPath, set,
                Array.Empty<string>(), out error);
        }

        var fable2Out = new SortedDictionary<string, string>(_fable2StoredValues, StringComparer.Ordinal);
        foreach ((string key, string val) in Values)
        {
            fable2Out[key] = val;
        }

        return RuntimeConfig.WriteValuesForTitle(Refii.Core.Engine_Title.Fable2, ConfigPath, fable2Out, out error);
    }

    /// <summary>
    /// <paramref name="set"/> into <c>refiii.toml</c>: edited in place, or written whole when there is
    /// none yet — with Fable 3's own roots, never Fable 2's, and Fable 3's keyboard rows at their defaults.
    /// </summary>
    private bool SaveFable3(Dictionary<string, string> set, out string error)
    {
        if (System.IO.File.Exists(SecondaryConfigPath))
        {
            return RuntimeConfig.EditValuesForTitle(Refii.Core.Engine_Title.Fable3, SecondaryConfigPath, set,
                Array.Empty<string>(), out error);
        }

        var fable3Out = new SortedDictionary<string, string>(_fable3StoredValues, StringComparer.Ordinal);
        RuntimeConfig.PopulateDefaultRoots(fable3Out, SecondaryConfigPath);
        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.For(Refii.Core.Engine_Title.Fable3))
        {
            if (IsPerTitleIsolatedRoot(definition.Name) || RuntimeCVarsGodot.IsKeyBinding(definition.Name))
            {
                continue;
            }

            if (Values.TryGetValue(definition.Name, out string? val))
            {
                fable3Out[definition.Name] = val;
            }
        }

        return RuntimeConfig.WriteValuesForTitle(Refii.Core.Engine_Title.Fable3, SecondaryConfigPath, fable3Out, out error);
    }

    public void ResetAllToDefaults()
    {
        string? f2Root = Values.TryGetValue("game_data_root", out string? r) ? r : null;
        string? f2Cache = Values.TryGetValue("cache_root", out string? c) ? c : null;

        Values.Clear();
        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All)
        {
            Values[definition.Name] = definition.DefaultValue;
        }

        if (!string.IsNullOrEmpty(f2Root))
        {
            Values["game_data_root"] = f2Root!;
        }
        if (!string.IsNullOrEmpty(f2Cache))
        {
            Values["cache_root"] = f2Cache!;
        }

        ApplyDesktopDefaults();
        ApplyPhoneDefaults();
        Values["lighting_preset"] = CurrentLightingPreset();
        Save();
    }

    /// <summary>Resets every setting belonging to <paramref name="tab"/> to its default (or Balanced preset member default) and saves.</summary>
    public void ResetTabToDefaults(SettingsTab tab)
    {
        IReadOnlyDictionary<string, string>? desktopDefaults = DesktopGraphicsPresets.Offered
            ? DesktopGraphicsPresets.Values(DesktopGraphicsPresets.DefaultPreset)
            : Refii.AndroidRuntime.AndroidSettingsLayout.TabDefaults(); // a phone's High preset and 30 FPS cap

        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All)
        {
            if (IsPerTitleIsolatedRoot(definition.Name))
            {
                continue;
            }

            if (!RuntimeConfig.ShouldShowCVar(definition))
            {
                continue;
            }

            SettingPresentation meta = DesktopSettingMetadata.Classify(definition);
            if (meta.Tab != tab)
            {
                continue;
            }

            if (desktopDefaults is not null && desktopDefaults.TryGetValue(definition.Name, out string? presetVal))
            {
                Values[definition.Name] = presetVal;
            }
            else
            {
                Values[definition.Name] = definition.DefaultValue;
            }
        }

        Values["lighting_preset"] = CurrentLightingPreset();
        ReadDesktopPreset();
        ReadGraphicsPreset();
        Save();
    }

    /// <summary>
    /// Builds the ordered Essential and Advanced section lists for <paramref name="tab"/>.
    /// </summary>
    public (List<(string Section, List<RuntimeCVarDefinition> Rows)> Essential,
            List<(string Section, List<RuntimeCVarDefinition> Rows)> Advanced) BuildTabSections(
        SettingsTab tab,
        bool includeContextualHidden = false)
    {
        var essentialOrder = new List<string>();
        var essentialMap = new Dictionary<string, List<(RuntimeCVarDefinition Def, int Order)>>(StringComparer.Ordinal);
        var advancedOrder = new List<string>();
        var advancedMap = new Dictionary<string, List<(RuntimeCVarDefinition Def, int Order)>>(StringComparer.Ordinal);

        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All)
        {
            if (!RuntimeConfig.ShouldShowCVar(definition))
            {
                continue;
            }

            SettingPresentation meta = DesktopSettingMetadata.Classify(definition);
            if (meta.Tab != tab || meta.HandledByHeroOrComposite || meta.HandledByLightingModal)
            {
                continue;
            }

            if (!includeContextualHidden && !DesktopSettingMetadata.IsContextuallyVisible(definition, Values))
            {
                continue;
            }

            bool isEssential = meta.Tier == SettingTier.Essential;
            Dictionary<string, List<(RuntimeCVarDefinition Def, int Order)>> targetMap =
                isEssential ? essentialMap : advancedMap;
            List<string> targetOrder = isEssential ? essentialOrder : advancedOrder;

            if (!targetMap.TryGetValue(meta.Section, out List<(RuntimeCVarDefinition Def, int Order)>? bucket))
            {
                bucket = new List<(RuntimeCVarDefinition Def, int Order)>();
                targetMap[meta.Section] = bucket;
                targetOrder.Add(meta.Section);
            }

            bucket.Add((definition, meta.Order));
        }

        var essential = essentialOrder
            .Select(sec => (sec, essentialMap[sec].OrderBy(x => x.Order).Select(x => x.Def).ToList()))
            .ToList();
        var advanced = advancedOrder
            .Select(sec => (sec, advancedMap[sec].OrderBy(x => x.Order).Select(x => x.Def).ToList()))
            .ToList();
        return (essential, advanced);
    }

    /// <summary>
    /// Searches across all visible tabs, sections, friendly labels, CVar keys, and descriptions.
    /// </summary>
    public List<(SettingsTab Tab, string Section, List<RuntimeCVarDefinition> Rows)> SearchAll(
        string query,
        bool includeDeveloper)
    {
        string needle = (query ?? string.Empty).Trim().ToLowerInvariant();
        if (needle.Length == 0)
        {
            return new List<(SettingsTab Tab, string Section, List<RuntimeCVarDefinition> Rows)>();
        }

        var bucketOrder = new List<(SettingsTab Tab, string Section)>();
        var buckets = new Dictionary<(SettingsTab Tab, string Section), List<(RuntimeCVarDefinition Def, int Order)>>();

        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All)
        {
            if (!RuntimeConfig.ShouldShowCVar(definition))
            {
                continue;
            }

            SettingPresentation meta = DesktopSettingMetadata.Classify(definition);
            if (meta.Tab == SettingsTab.Developer && !includeDeveloper &&
                !definition.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (meta.HandledByHeroOrComposite && definition.Name is DesktopGraphicsPresets.Key or GraphicsPresets.Key)
            {
                continue;
            }

            string haystack = string.Concat(
                definition.Name, " ",
                meta.Label, " ",
                FormatDisplayName(definition), " ",
                meta.Section, " ",
                DesktopSettingMetadata.TabFullName(meta.Tab), " ",
                definition.OwnerTitle ?? string.Empty, " ",
                meta.Summary ?? string.Empty, " ",
                definition.Description).ToLowerInvariant();

            if (!haystack.Contains(needle, StringComparison.Ordinal))
            {
                continue;
            }

            var key = (meta.Tab, meta.Section);
            if (!buckets.TryGetValue(key, out List<(RuntimeCVarDefinition Def, int Order)>? list))
            {
                list = new List<(RuntimeCVarDefinition Def, int Order)>();
                buckets[key] = list;
                bucketOrder.Add(key);
            }

            list.Add((definition, meta.Order));
        }

        return bucketOrder
            .OrderBy(k => (int)k.Tab)
            .Select(k => (k.Tab, k.Section, buckets[k].OrderBy(x => x.Order).Select(x => x.Def).ToList()))
            .ToList();
    }

    public List<(string Category, List<RuntimeCVarDefinition> Definitions)> BuildGroups()
    {
        string filter = Filter.ToLowerInvariant();
        var order = new List<string>();
        var byCategory = new Dictionary<string, List<RuntimeCVarDefinition>>(StringComparer.Ordinal);

        bool showAdvanced = ShowAdvanced || filter.Length != 0;
        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All)
        {
            if (!RuntimeConfig.ShouldShowCVar(definition) || !MatchesFilter(definition, filter))
            {
                continue;
            }

            // All advanced lighting settings live in the LightingSettingsDialog
            // accessed via the "Advanced..." button under Lighting Preset.
            if (definition.Category == RuntimeCVarsGodot.LightingCategory &&
                definition.Name is not ("lighting_preset" or "hdr_output" or "bloom_scale"))
            {
                continue;
            }

            if (!showAdvanced && IsAdvanced(definition))
            {
                continue;
            }

            string displayCategory = DisplayCategory(definition);
            if (!byCategory.TryGetValue(displayCategory, out List<RuntimeCVarDefinition>? bucket))
            {
                bucket = new List<RuntimeCVarDefinition>();
                byCategory[displayCategory] = bucket;
                order.Add(displayCategory);
            }

            bucket.Add(definition);
        }

        // The picture first, then the game's services, then whatever the runtime adds, and
        // the developer switches last. Definition order alone put Multiplayer second, because
        // the runtime's registry is listed before Godot's.
        return order
            .OrderBy(GroupRank)
            .Select(category => (category, byCategory[category]))
            .ToList();
    }

    private static int GroupRank(string category) => category switch
    {
        RuntimeConfig.DisplayCategory => 0,
        RuntimeCVarsGodot.LightingCategory => 1,
        RuntimeCVarsGodot.VideoCategory => 2,
        RuntimeCVarsGodot.ControlsCategory => 3,
        // A mod's Controls group ("Controls: <title>") sits with the Controls group, after it.
        _ when category.StartsWith(RuntimeCVarsGodot.ControlsCategory + ": ", StringComparison.Ordinal) => 3,
        RuntimeCVarsGodot.Category => 4,
        MultiplayerCategory => 5,
        RuntimeCVarsGodot.PresenceCategory => 6,
        RuntimeCVarsGodot.DeveloperCategory => 9,
        _ => 7,
    };

    /// <summary>The panel's name for the runtime's RexNet group: what it is to a player.</summary>
    private const string MultiplayerCategory = RuntimeCVarsGodot.MultiplayerCategory;

    private static string DisplayCategory(RuntimeCVarDefinition definition)
    {
        // The few window keys that outlived their category — see RuntimeConfig.IsDisplayCVar.
        // They are generated entries and cannot be re-categorised at the source, so they are
        // re-homed here instead.
        if (RuntimeConfig.IsDisplayCVar(definition.Name))
        {
            return RuntimeConfig.DisplayCategory;
        }
        if (definition.Category == "RexNet")
        {
            return MultiplayerCategory;
        }

        return definition.Category;
    }

    private static bool MatchesFilter(RuntimeCVarDefinition definition, string filter)
    {
        if (filter.Length == 0)
        {
            return true;
        }

        string haystack = (definition.Name + " " + FormatDisplayName(definition) + " "
                + definition.Category + " " + definition.Description)
            .ToLowerInvariant();
        return haystack.Contains(filter, StringComparison.Ordinal);
    }

    /// <summary>
    /// The label a setting is shown under: its config key as words, so `max_fps` reads
    /// "Max FPS". The key itself is unchanged and still what the file holds.
    /// </summary>
    public static string FormatDisplayName(RuntimeCVarDefinition definition)
    {
        if (AndroidSettingNames.Name(definition.Name) is { } phone) return phone;
        string[] words = definition.Name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (DisplayWords.TryGetValue(words[i], out string? spelled))
            {
                words[i] = spelled;
            }
            else if (i == 0)
            {
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
            }
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// A setting's description with every other setting it names spelled the way the panel
    /// labels it, so "Needs texture_orm_enabled" reads "Needs Texture ORM enabled".
    /// </summary>
    public static string FormatDescription(RuntimeCVarDefinition definition) =>
        System.Text.RegularExpressions.Regex.Replace(
            AndroidSettingNames.About(definition.Name) ?? definition.Description,
            @"`?\b[a-z0-9]+(?:_[a-z0-9]+)+\b`?",
            match =>
            {
                string key = match.Value.Trim('`');
                RuntimeCVarDefinition? named = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == key);
                return named is null ? match.Value : FormatDisplayName(named);
            });

    /// <summary>Words in setting keys that are acronyms or names rather than plain English.</summary>
    private static readonly Dictionary<string, string> DisplayWords = new(StringComparer.Ordinal)
    {
        ["aa"] = "AA",
        ["ao"] = "AO",
        ["cl"] = "CL",
        ["csv"] = "CSV",
        ["fps"] = "FPS",
        ["fsr"] = "FSR",
        ["fx"] = "FX",
        ["gc"] = "GC",
        ["gi"] = "GI",
        ["gpu"] = "GPU",
        ["hdr"] = "HDR",
        ["io"] = "IO",
        ["lod"] = "LOD",
        ["lua"] = "Lua",
        ["mb"] = "MB",
        ["ms"] = "ms",
        ["msaa"] = "MSAA",
        ["orm"] = "ORM",
        ["postfx"] = "Post-FX",
        ["renderdoc"] = "RenderDoc",
        ["rexcrt"] = "RexCRT",
        ["rexnet"] = "RexNet",
        ["taa"] = "TAA",
        ["vfs"] = "VFS",
        ["vsync"] = "VSync",
        ["xam"] = "XAM",
    };

    /// <summary>
    /// The resolutions offered for each aspect ratio. Ordinary desktop sizes rather than a
    /// computed sweep: a generated list produces oddities nobody's monitor has, and the point
    /// of the pair of dropdowns is that a player recognises their own resolution in it.
    /// </summary>
    private static readonly Dictionary<string, string[]> ResolutionsByAspect =
        new(StringComparer.Ordinal)
        {
            ["16:9"] = new[]
            {
                "1280x720", "1366x768", "1600x900", "1920x1080",
                "2560x1440", "3200x1800", "3840x2160",
            },
            ["16:10"] = new[]
            {
                "1280x800", "1440x900", "1680x1050", "1920x1200", "2560x1600", "3840x2400",
            },
            ["4:3"] = new[]
            {
                "1024x768", "1280x960", "1400x1050", "1600x1200", "2048x1536",
            },
            ["21:9"] = new[]
            {
                "2560x1080", "3440x1440", "3840x1600", "5120x2160",
            },
            ["32:9"] = new[]
            {
                "3840x1080", "5120x1440",
            },
        };

    /// <summary>
    /// The options a setting offers RIGHT NOW. Static <c>AllowedValues</c> cannot express a list
    /// that depends on another setting, and the resolution list depends on the chosen aspect —
    /// so that one pair is answered here and everything else falls through unchanged. A
    /// non-empty answer makes the setting a dropdown whatever its type.
    /// </summary>
    public static List<RuntimeCVarOption> OptionsFor(
        RuntimeCVarDefinition definition, IReadOnlyDictionary<string, string> values)
    {
        if (IsLockedRenderingDriver(definition))
        {
            return new List<RuntimeCVarOption> { new("vulkan", "Vulkan") };
        }

        if (definition.Name != "display_resolution")
        {
            return ParseAllowedValues(definition.AllowedValues);
        }

        string aspect = values.TryGetValue("display_aspect", out string? chosen) && chosen.Length != 0
            ? chosen
            : "16:9";

        string[] candidates = ResolutionsByAspect.TryGetValue(aspect, out string[]? sizes)
            ? sizes
            : ResolutionsByAspect["16:9"];

        var resolved = FitToMonitor(candidates, aspect, values)
            .Select(size => new RuntimeCVarOption(size, size))
            .ToList();
        if (DesktopGraphicsPresets.Offered)
        {
            resolved.Insert(0, new RuntimeCVarOption("native", "Match Window"));
        }

        return resolved;
    }

    /// <summary>
    /// Direct3D 12 exists only on Windows, so elsewhere the driver is Vulkan and the dropdown
    /// offers nothing else and cannot be changed.
    /// </summary>
    public static bool IsLockedRenderingDriver(RuntimeCVarDefinition definition) =>
        definition.Name == "rendering_driver" && !OperatingSystem.IsWindows();

    /// <summary>
    /// Narrows a list to what the chosen monitor can actually show, and adds that monitor's own
    /// resolution when the aspect matches.
    /// </summary>
    /// <remarks>
    /// GODOT CANNOT ENUMERATE MODES. `DisplayServer` reports a screen's CURRENT size, refresh
    /// rate and usable rect — there is no "every mode this output supports" call of the kind
    /// SDL has. So this cannot be a true mode list, and pretending otherwise would be worse
    /// than the fixed table: it would look authoritative and be invented.
    ///
    /// What it can honestly do is drop sizes that do not fit — offering 4K on a 1080p panel is
    /// the thing that makes a list feel machine-generated — and make sure the panel's own
    /// resolution is present, since that is the one entry a player is certain to look for.
    ///
    /// It never returns empty: a monitor smaller than every listed size still gets the smallest,
    /// because a dropdown with nothing in it cannot be corrected from the UI.
    /// </remarks>
    private static bool IsRunningUnderTestHost() =>
        AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase)
        || System.Environment.CommandLine.Contains("testhost", StringComparison.OrdinalIgnoreCase);

    private static List<string> FitToMonitor(
        string[] candidates, string aspect, IReadOnlyDictionary<string, string> values)
    {
        if (IsRunningUnderTestHost())
        {
            return new List<string>(candidates);
        }

        Vector2I screen;
        try
        {
            int monitor = 0;
            if (values.TryGetValue("monitor", out string? chosen) &&
                int.TryParse(chosen, out int index) && index >= 0 &&
                index < DisplayServer.GetScreenCount())
            {
                monitor = index;
            }

            screen = DisplayServer.ScreenGetSize(monitor);
        }
        catch (Exception)
        {
            // Headless, or a display server that will not answer: keep the whole list rather
            // than filtering against a size we do not have.
            return new List<string>(candidates);
        }

        if (screen.X <= 0 || screen.Y <= 0)
        {
            return new List<string>(candidates);
        }

        // ASK THE PLATFORM FIRST. `DisplayModes` enumerates what this output can actually do —
        // Win32 on Windows, the X server on Linux — and returns nothing when it cannot say
        // (Wayland, headless). Real modes beat the curated table outright: this monitor offers
        // 1280x1024 and 1152x864, which the table lacks, and does NOT offer 1366x768 or 4K,
        // which the table would have shown.
        IReadOnlyList<Vector2I> reported = DisplayModes.ForScreen(screen);
        if (reported.Count != 0)
        {
            var enumerated = reported
                .Where(size => MatchesAspect(size, aspect))
                .Select(size => $"{size.X}x{size.Y}")
                .ToList();

            // Only when the aspect has something in it. An aspect no mode matches falls through
            // to the curated list rather than showing an empty dropdown.
            if (enumerated.Count != 0)
            {
                enumerated.Reverse();
                return enumerated;
            }
        }

        var fitted = candidates
            .Where(size => Parse(size) is { } wh && wh.X <= screen.X && wh.Y <= screen.Y)
            .ToList();

        // The monitor's own size, when it belongs to the aspect being shown.
        string native = $"{screen.X}x{screen.Y}";
        if (!fitted.Contains(native, StringComparer.Ordinal) && MatchesAspect(screen, aspect))
        {
            fitted.Add(native);
            fitted.Sort((a, b) => (Parse(a)?.X ?? 0).CompareTo(Parse(b)?.X ?? 0));
        }

        return fitted.Count != 0 ? fitted : new List<string> { candidates[0] };
    }

    /// <summary>"1920x1080" to a size, or null when it is not one.</summary>
    private static Vector2I? Parse(string size)
    {
        int cross = size.IndexOf('x');
        return cross > 0 &&
            int.TryParse(size[..cross], out int width) &&
            int.TryParse(size[(cross + 1)..], out int height)
            ? new Vector2I(width, height)
            : null;
    }

    /// <summary>
    /// Whether a size is the given aspect, within a percent. Real panels are not exact — 1366x768
    /// is sold as 16:9 and is 16:8.99 — so an equality test would reject the very sizes players
    /// have.
    /// </summary>
    private static bool MatchesAspect(Vector2I size, string aspect)
    {
        int colon = aspect.IndexOf(':');
        if (colon <= 0 ||
            !int.TryParse(aspect[..colon], out int wide) ||
            !int.TryParse(aspect[(colon + 1)..], out int high) ||
            high == 0 || size.Y == 0)
        {
            return false;
        }

        double want = (double)wide / high;
        double have = (double)size.X / size.Y;
        return Math.Abs(have - want) / want <= 0.01;
    }

    /// <summary>
    /// An allowed-values list is comma separated. An entry is a bare value, shown as itself, or
    /// <c>value|Label</c>, which shows the label and writes the value — how an Int that names its
    /// modes becomes a readable dropdown. The separator is a pipe because values carry colons
    /// ("16:9").
    /// </summary>
    public static List<RuntimeCVarOption> ParseAllowedValues(string? allowedValues)
    {
        var options = new List<RuntimeCVarOption>();
        if (string.IsNullOrEmpty(allowedValues))
        {
            return options;
        }

        foreach (string token in allowedValues!.Split(','))
        {
            string trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            int bar = trimmed.IndexOf('|');
            options.Add(bar < 0
                ? new RuntimeCVarOption(trimmed, trimmed)
                : new RuntimeCVarOption(trimmed[..bar].Trim(), trimmed[(bar + 1)..].Trim()));
        }

        return options;
    }

    public string GetValueOrDefault(string key, string fallback) =>
        Values.TryGetValue(key, out string? value) && value.Length != 0 ? value : fallback;

    public static int ParseInt(string value, string fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : int.TryParse(fallback, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fallbackValue)
                ? fallbackValue
                : 0;

    public static double ParseDouble(string value, string fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : double.TryParse(fallback, NumberStyles.Float, CultureInfo.InvariantCulture, out double fallbackValue)
                ? fallbackValue
                : 0.0;
}
