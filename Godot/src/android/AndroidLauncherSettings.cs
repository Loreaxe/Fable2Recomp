using System;
using System.IO;
using System.Text.RegularExpressions;
using Refii.Launcher.Core;

namespace Refii.AndroidRuntime;

/// <summary>Resolves desktop automatic paths before the shared settings panel writes Android's runtime configuration.</summary>
public static class AndroidLauncherSettings
{
    /// <summary>The launcher settings a phone ignores: the window's size, mode, monitor and vsync, the
    /// rendering driver, and the anti-aliasing and upscalers the phone's renderer turns off
    /// (AndroidGameHost: MSAA and TAA off, bilinear scaling at render_scale_percent).</summary>
    private static readonly System.Collections.Generic.HashSet<string> DesktopOnly = new(StringComparer.Ordinal)
    {
        "fullscreen", "monitor", "borderless", "display_aspect", "display_resolution", "vsync_mode",
        "rendering_driver", "msaa", "taa", "render_scaler", "fsr_quality", "fsr_sharpness",
        DesktopGraphicsPresets.Key, DesktopGraphicsPresets.RevisionKey,
    };

    /// <summary>Whether the launcher's settings list shows <paramref name="name"/>: on a phone, hides
    /// desktop-only window/upscaler rows; on desktop, hides every phone-only row in <see cref="RuntimeCVarsAndroid.AllKeys"/>.</summary>
    public static bool Shows(string name) =>
        GraphicsPresets.Offered ? !DesktopOnly.Contains(name) : !RuntimeCVarsAndroid.AllKeys.Contains(name);

    /// <summary>What a phone player sets in the launcher without opening Advanced: the graphics preset
    /// and the frame cap with the two settings beside them, the camera and vibration, and online and
    /// split-screen co-op. The rest of the list (the tuning dials the preset sets, the phone's own
    /// experiments, keyboard and mouse bindings) stays under each tab's Advanced section
    /// (<see cref="AndroidSettingsLayout"/>); the game's Options panel has the graphics settings with an
    /// explanation each.</summary>
    internal static readonly System.Collections.Generic.HashSet<string> PlayerFacing = new(StringComparer.Ordinal)
    {
        GraphicsPresets.Key, "max_fps", "fps_counter", "render_scale_percent", AndroidBakedShadows.Key, AndroidStartup.SkipVideosKey,
        "camera_invert_x", "camera_invert_y", "controller_vibration", AndroidQuickSettings.VibrationStrengthKey,
        "rexnet", "rexnet_display_name", Refii.Game.GameSplitView.SwitchKey, Refii.Game.GameSplitView.OrientationKey,
    };

    /// <summary>On a phone, whether <paramref name="name"/> waits for Advanced (anything a player does not
    /// set); null elsewhere, where the panel's own rule decides.</summary>
    public static bool? Advanced(string name) => GraphicsPresets.Offered ? !PlayerFacing.Contains(name) : null;

    /// <summary>Whether the launcher offers the Look choice: not on a phone, which always draws the modern
    /// lighting, and where Modern would only switch on the desktop's modernised materials.</summary>
    public static bool ShowsLook => !GraphicsPresets.Offered;

    /// <summary>Preserves launch options while making automatic native paths writable and consistent with the selected installation.</summary>
    /// <param name="runtimeRoot">Physical private runtime directory supplied by the Android host.</param>
    /// <param name="gameRoot">Selected prepared game directory, or null before an installation exists.</param>
    public static void Prepare(string runtimeRoot, string? gameRoot)
    {
        string root = AndroidRuntimeOptions.PhysicalPath(runtimeRoot, "runtime root");
        string config = Path.Combine(root, Refii.Core.Engine_Title.Active.ConfigFileName);
        var values = RuntimeConfig.ReadValues(config);
        var before = new System.Collections.Generic.Dictionary<string, string>(values, StringComparer.Ordinal);
        bool changed = false;
        // Native automatic logging uses the executable folder, which is /system/bin
        // for Android's app_process64. Resolve that default before TOML overrides argv.
        if (!values.TryGetValue("log_file", out string? log) || string.IsNullOrWhiteSpace(log))
        {
            values["log_file"] = Path.Combine(root, "logs", "refii.log");
            changed = true;
        }
        changed |= ApplyPresetOnce(values);
        changed |= GraphicsPresets.Upgrade(values);
        if (gameRoot != null)
        {
            string game = AndroidRuntimeOptions.PhysicalPath(gameRoot, "game root");
            PrepareUpdateBank(game);
            if (!values.TryGetValue("game_data_root", out string? previous) || string.IsNullOrWhiteSpace(previous) ||
                SamePath(previous, Path.Combine(root, "assets")) || (OwnedGamePath(previous, root) && !SamePath(previous, game)))
            {
                values["game_data_root"] = game;
                changed = true;
            }
        }
        if (changed && !RuntimeConfig.WriteValues(config, values, out string error))
            throw new IOException("Cannot prepare Android launcher paths: " + error);
        ShareWithOtherTitles(config, before, values);
    }

    /// <summary>Settings a title's file keeps for itself even where the setting has no owning title: its
    /// game, update, cache and save folders (the launcher's settings keep the same ones apart).</summary>
    private static readonly System.Collections.Generic.HashSet<string> PerTitleKeys = new(StringComparer.Ordinal)
    {
        "game_data_root", "update_data_root", "cache_root", "user_data_root",
    };

    /// <summary>Keys RuntimeConfig writes to record that a one-shot migration ran; copied to a title's file that lacks them.</summary>
    private static readonly string[] MigrationMarkers = { "placed_light_shadows_world" };

    /// <summary>
    /// Copies what changed in one title's runtime configuration to the other titles' beside it, for the
    /// settings the titles share, as the launcher's unified settings do.
    /// </summary>
    /// <remarks>
    /// WHY. The launcher's settings read refii.toml (Fable 2's) first for every shared setting and write
    /// it to both titles' files (RuntimeSettingsModel.Save). The phone also writes the active title's file
    /// alone: the first preset and its upgrades here, and Options in the game (AndroidQuickSettings). With
    /// Fable 3 active those changes never reached refii.toml, so the launcher showed Fable 2's values and
    /// its next save wrote them over Fable 3's choices; a phone that started on Fable 3 had a refii.toml of
    /// desktop defaults (the launcher creates it) that replaced its phone preset and frame cap. So each
    /// change is copied as it is made: a setting <paramref name="after"/> holds differently from
    /// <paramref name="before"/>, with no owning title and read by the other title, and not one of
    /// <see cref="PerTitleKeys"/>. A missing file is created with all the shared settings, and a file still
    /// on an earlier revision of a named preset takes the current one (<see cref="GraphicsPresets.Upgrade"/>).
    /// A file that cannot be written is left as it was: the change itself already stands.
    /// </remarks>
    /// <param name="config">The file just written: a title's configuration (refii.toml, refiii.toml); a file of
    /// any other name has no siblings and nothing is copied.</param>
    /// <param name="before">Its values before the change.</param>
    /// <param name="after">Its values as written.</param>
    /// <returns>Whether every other title's file is up to date.</returns>
    public static bool ShareWithOtherTitles(string config, System.Collections.Generic.IReadOnlyDictionary<string, string> before,
        System.Collections.Generic.IReadOnlyDictionary<string, string> after)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(config)) ?? string.Empty;
        string fileName = Path.GetFileName(config);
        // Only a title's own file has siblings: a file of another name (a test's) is left alone.
        if (!System.Linq.Enumerable.Any(Refii.Core.Engine_Title.All,
                title => string.Equals(title.ConfigFileName, fileName, StringComparison.OrdinalIgnoreCase))) return true;
        var definitions = new System.Collections.Generic.Dictionary<string, RuntimeCVarDefinition>(StringComparer.Ordinal);
        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All) definitions.TryAdd(definition.Name, definition);
        bool ok = true;
        foreach (Refii.Core.Engine_Title title in Refii.Core.Engine_Title.All)
        {
            if (string.Equals(title.ConfigFileName, fileName, StringComparison.OrdinalIgnoreCase)) continue;
            // The touch editor owns this preference outside the launcher's CVar list.
            bool Shared(string key) => key == AndroidQuickSettings.HideTouchControlsKey ||
                (!PerTitleKeys.Contains(key) && definitions.TryGetValue(key, out var definition) &&
                 definition.OwnerTitle == null && definition.AppliesTo(title));
            string other = Path.Combine(directory, title.ConfigFileName);
            try
            {
                bool exists = File.Exists(other);
                var values = exists ? RuntimeConfig.ReadValues(other)
                    : new System.Collections.Generic.SortedDictionary<string, string>(StringComparer.Ordinal);
                bool changed = !exists;
                foreach ((string key, string value) in after)
                {
                    if (!Shared(key)) continue;
                    bool changedHere = !exists || !before.TryGetValue(key, out string? was) || was != value;
                    if (!changedHere || (values.TryGetValue(key, out string? there) && there == value)) continue;
                    values[key] = value;
                    changed = true;
                }
                // The record that RuntimeConfig's one-shot fixture-shadow migration has run, which is no
                // setting: without it the launcher's next read turns placed_light_shadows back on, and a
                // copied preset that leaves them off reads as Custom (ApplyPresetOnce).
                foreach (string marker in MigrationMarkers)
                {
                    if (after.TryGetValue(marker, out string? done) && !values.ContainsKey(marker))
                    {
                        values[marker] = done;
                        changed = true;
                    }
                }
                changed |= GraphicsPresets.Upgrade(values);
                if (changed && !RuntimeConfig.WriteValuesForTitle(title, other, values, out _)) ok = false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>Gives a configuration that never chose a graphics preset the one for this phone's GPU
    /// (<see cref="FirstPreset"/>) and its frame cap, or Low with that cap on a phone short of memory
    /// (<see cref="Refii.Game.AndroidMemoryRelease.LowRamPhone"/>).</summary>
    /// <remarks>Runs once: afterwards the key is present, and the player's own choices, the
    /// preset's settings and the cap included, stay exactly as they leave them.</remarks>
    /// <param name="values">The runtime configuration's values, changed in place.</param>
    /// <param name="adapter">The GPU's name; null asks the rendering server.</param>
    /// <returns>Whether the preset was applied.</returns>
    public static bool ApplyPresetOnce(System.Collections.Generic.IDictionary<string, string> values, string? adapter = null)
    {
        if (values.ContainsKey(GraphicsPresets.Key)) return false;
        (string preset, string cap) = FirstPreset(adapter ?? AdapterName());
        if (Refii.Core.Engine_Platform.IsAndroid && Refii.Game.AndroidMemoryRelease.LowRamPhone()) preset = GraphicsPresets.Low;
        if (!GraphicsPresets.Apply(preset, values)) return false;
        values["max_fps"] = cap;
        // The key that says RuntimeConfig's one-shot migration has run: without it the next read
        // turns placed_light_shadows back on, and the first preset reads as Custom with its
        // shadowless shaders rebuilt.
        values["placed_light_shadows_world"] = "false";
        return true;
    }

    /// <summary>
    /// The preset and frame cap a phone starts on, by its GPU: Low on an Adreno older than the
    /// 700s, else High, both at <see cref="GraphicsPresets.AndroidFrameCap"/>.
    /// </summary>
    /// <remarks>On the Galaxy S10e (Adreno 640) High ran 13 FPS at either cap, GPU-bound, while
    /// Low reached 22-25 at 30. A cap of 20 holds steadier there (every frame 50 ms), but it is the
    /// player's choice in Options, never the default. The 650 and 660 (Snapdragon 865, 888) have
    /// roughly half as much GPU again, untested. The S25+'s Adreno 830 runs High at 30 with room to
    /// spare, and the 700s and any other GPU start on High, as every phone did before.</remarks>
    /// <param name="adapter">The GPU's name as the rendering server gives it ("Adreno (TM) 640").</param>
    public static (string Preset, string Cap) FirstPreset(string adapter)
    {
        Match adreno = Regex.Match(adapter, @"Adreno\D{0,8}(\d{3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (adreno.Success && int.TryParse(adreno.Groups[1].Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int model) && model < 700)
            return (GraphicsPresets.Low, GraphicsPresets.AndroidFrameCap);
        return (GraphicsPresets.High, GraphicsPresets.AndroidFrameCap);
    }

    /// <summary>The GPU's name on a phone; empty elsewhere (tools and tests run without a GPU's server).</summary>
    private static string AdapterName()
    {
        if (!Refii.Core.Engine_Platform.IsAndroid) return string.Empty;
        try
        {
            return Godot.RenderingServer.GetVideoAdapterName() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Copies the earlier Android TU layout into upstream's update volume without replacing existing files.</summary>
    /// <param name="gameRoot">The prepared private game directory containing the extracted update bank.</param>
    public static void PrepareUpdateBank(string gameRoot)
    {
        foreach (string name in Refii.Core.Engine_Title.Active.TitleUpdateDataFiles)
        {
            string source = Path.Combine(gameRoot, TitleUpdate.DataRelativePath(name));
            string target = Path.Combine(Refii.Core.Engine_GameData.UpdateRootFor(gameRoot), TitleUpdate.DataRelativePath(name));
            if (!File.Exists(source) || File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
        }
    }

    /// <summary>Recognizes an installer's own prior game locator when activating a replacement installation.</summary>
    /// <param name="path">Previously recorded physical game directory.</param>
    /// <param name="runtimeRoot">Private runtime directory beside phone-installs.</param>
    /// <returns>Whether the path is exactly an owned installation's game child.</returns>
    private static bool OwnedGamePath(string path, string runtimeRoot)
    {
        string parent = AndroidRuntimeOptions.TitleDirectory(Path.GetDirectoryName(runtimeRoot) ?? runtimeRoot);
        string relative = Path.GetRelativePath(Path.Combine(parent, "phone-installs"), path).Replace('\\', '/');
        return Regex.IsMatch(relative, @"\Ainstall-[0-9a-f]{32}/game\z", RegexOptions.CultureInvariant);
    }

    /// <summary>Compares physical paths using the current host's filesystem case and separators.</summary>
    /// <param name="left">Recorded path.</param>
    /// <param name="right">Automatic private default.</param>
    /// <returns>Whether both paths name the same location.</returns>
    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
