namespace Refii.Tests.Launcher;

using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Launcher.Core;

/// <summary>
/// Verifies dual-TOML settings synchronization in <see cref="RuntimeSettingsModel"/>:
/// shared engine CVars synchronize across <c>refii.toml</c> and <c>refiii.toml</c>,
/// per-title roots remain strictly isolated, and title-owned CVars never leak across titles.
/// </summary>
[TestSuite]
public class UnifiedLauncherSettingsSyncTests
{
    private string _tempDir = string.Empty;
    private Engine_Title _savedActive = Engine_Title.Fable2;

    [BeforeTest]
    public void SetUp()
    {
        _savedActive = Engine_Title.Active;
        _tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_unified_settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [AfterTest]
    public void TearDown()
    {
        Engine_Title.Active = _savedActive;
        RuntimeCVarSet.Refresh();
        if (_tempDir.Length != 0 && Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    [TestCase]
    public void TestSharedSettingsSyncAcrossBothTomlFilesWhileTitleOwnedAndRootsStayIsolated()
    {
        string f2Path = Path.Combine(_tempDir, "refii.toml");
        string f3Path = Path.Combine(_tempDir, "refiii.toml");
        string f2GameRoot = Path.Combine(_tempDir, "assets", "Fable2").Replace('\\', '/');
        string f3GameRoot = Path.Combine(_tempDir, "assets", "Fable3").Replace('\\', '/');
        Directory.CreateDirectory(f2GameRoot);
        Directory.CreateDirectory(f3GameRoot);

        File.WriteAllText(f2Path,
            $"game_data_root = \"{f2GameRoot}\"\n" +
            "display_resolution = \"1920x1080\"\n" +
            "decorate_mode_enabled = false\n" +
            "padmap_p2_a = \"A\"\n" +
            "vfs_trace_vfs = false\n");

        File.WriteAllText(f3Path,
            $"game_data_root = \"{f3GameRoot}\"\n" +
            "display_resolution = \"1280x720\"\n");

        var model = new RuntimeSettingsModel();
        model.ReloadFromPaths(f2Path, f3Path, ensureDefaults: false);

        // Mutate shared settings (display, performance preset, keybinding) and Fable 2-owned settings:
        model.Values["display_resolution"] = "2560x1440";
        model.ApplyDesktopPreset(DesktopGraphicsPresets.High);
        model.Values["key_sprint"] = "Shift,V";
        model.Values["decorate_mode_enabled"] = "true";
        model.Values["padmap_p2_a"] = "B";
        model.Values["vfs_trace_vfs"] = "true";
        model.Save();

        SortedDictionary<string, string> f2Saved = RuntimeConfig.ReadValues(f2Path);
        SortedDictionary<string, string> f3Saved = RuntimeConfig.ReadValues(f3Path);

        // Shared settings synced to both files:
        AssertThat(f2Saved["display_resolution"]).IsEqual("2560x1440");
        AssertThat(f3Saved["display_resolution"]).IsEqual("2560x1440");
        AssertThat(f2Saved[DesktopGraphicsPresets.Key]).IsEqual(DesktopGraphicsPresets.High);
        AssertThat(f3Saved[DesktopGraphicsPresets.Key]).IsEqual(DesktopGraphicsPresets.High);
        // The panel's keyboard rows are Fable 2's: refiii.toml keeps its own (it never held one here).
        AssertThat(f2Saved["key_sprint"]).IsEqual("Shift,V");
        AssertThat(f3Saved.ContainsKey("key_sprint")).IsFalse();

        // Per-title roots remain strictly isolated:
        AssertThat(f2Saved["game_data_root"]).IsEqual(f2GameRoot);
        AssertThat(f3Saved["game_data_root"]).IsEqual(f3GameRoot);

        // Fable 2-owned settings saved in refii.toml, never leaked into refiii.toml:
        AssertThat(f2Saved["decorate_mode_enabled"]).IsEqual("true");
        AssertThat(f2Saved["padmap_p2_a"]).IsEqual("B");
        AssertThat(f2Saved["vfs_trace_vfs"]).IsEqual("true");

        AssertThat(f3Saved.ContainsKey("decorate_mode_enabled")).IsFalse();
        AssertThat(f3Saved.ContainsKey("padmap_p2_a")).IsFalse();
        AssertThat(f3Saved.ContainsKey("vfs_trace_vfs")).IsFalse();
    }

    [TestCase]
    public void TestASaveWithNothingChangedWritesNeitherFile()
    {
        // The Play button, a rebuild and closing the drawer all save (int7d D4: a Fable 3 start
        // rewrote refii.toml whole from Fable 2's table, 117 lines, Fable 3's keys dropped).
        string f2Path = Path.Combine(_tempDir, "refii.toml");
        string f3Path = Path.Combine(_tempDir, "refiii.toml");
        const string f2Text = "# the player's own comment\nmax_fps = 60\nkey_roll = \"Space\"\ncoop_split_screen = true\n";
        const string f3Text = "# Fable 3\nmax_fps = 60\nkey_roll = \"Space\"\n";
        File.WriteAllText(f2Path, f2Text);
        File.WriteAllText(f3Path, f3Text);
        DateTime f2Time = File.GetLastWriteTimeUtc(f2Path);
        DateTime f3Time = File.GetLastWriteTimeUtc(f3Path);

        Engine_Title.Active = Engine_Title.Fable3;
        var model = new RuntimeSettingsModel();
        model.ReloadFromPaths(f2Path, f3Path, ensureDefaults: false);
        model.Save();

        AssertThat(File.ReadAllText(f2Path)).IsEqual(f2Text);
        AssertThat(File.ReadAllText(f3Path)).IsEqual(f3Text);
        AssertThat(File.GetLastWriteTimeUtc(f2Path)).IsEqual(f2Time);
        AssertThat(File.GetLastWriteTimeUtc(f3Path)).IsEqual(f3Time);
    }

    [TestCase]
    public void TestAFable2LaunchWithNothingChangedWritesNothing()
    {
        // A plain Fable 2 launch: the launcher's start (the panel's load with the defaults pass), Play
        // (SaveIfDirty), the launch's own defaults pass (LaunchController.Launch ->
        // Installer.EnsureRuntimeConfigFile), and an in-game write of the values the file already holds
        // (a settings row restating its value). The file is first settled by one defaults pass, as the
        // first launch after an upgrade would; from then on a launch touches neither its bytes nor its time.
        string f2Path = Path.Combine(_tempDir, "refii.toml");
        string f3Path = Path.Combine(_tempDir, "refiii.toml");
        File.WriteAllText(f2Path,
            "# the player's own comment\nmax_fps = \"60\"\nkey_roll = \"Space\"\ncoop_split_screen = true\n" +
            "something_unknown = 7\n[section]\ninner = 1\n");
        File.WriteAllText(f3Path, "# Fable 3\nmax_fps = 60\n");

        Engine_Title.Active = Engine_Title.Fable2;
        AssertThat(RuntimeConfig.EnsureDefaultsForTitle(Engine_Title.Fable2, f2Path, out string settle)).IsTrue();
        AssertThat(settle).IsEmpty();
        string f2Text = File.ReadAllText(f2Path);
        string f3Text = File.ReadAllText(f3Path);
        DateTime f2Time = File.GetLastWriteTimeUtc(f2Path);
        DateTime f3Time = File.GetLastWriteTimeUtc(f3Path);

        var model = new RuntimeSettingsModel();
        model.ReloadFromPaths(f2Path, f3Path, ensureDefaults: true);
        model.Save();
        AssertThat(RuntimeConfig.EnsureDefaultsForTitle(Engine_Title.Fable2, f2Path, out string launch)).IsTrue();
        AssertThat(launch).IsEmpty();
        AssertThat(RuntimeConfig.EditValues(f2Path, new Dictionary<string, string> { ["max_fps"] = "60" },
            new[] { "not_in_the_file" }, out string game)).IsTrue();
        AssertThat(game).IsEmpty();

        AssertThat(File.ReadAllText(f2Path)).IsEqual(f2Text);
        AssertThat(File.ReadAllText(f3Path)).IsEqual(f3Text);
        AssertThat(File.GetLastWriteTimeUtc(f2Path)).IsEqual(f2Time);
        AssertThat(File.GetLastWriteTimeUtc(f3Path)).IsEqual(f3Time);
        // The settled file still holds every key no table writes, untouched.
        AssertThat(f2Text).Contains("key_roll = \"Space\"\n");
        AssertThat(f2Text).Contains("coop_split_screen = true\n");
        AssertThat(f2Text).Contains("something_unknown = 7\n");
        AssertThat(f2Text).Contains("[section]\ninner = 1\n");
        AssertThat(f2Text).StartsWith("# the player's own comment\nmax_fps = \"60\"\n");
    }

    [TestCase]
    public void TestAChangedSharedSettingEditsOnlyItsLineInEachFile()
    {
        string f2Path = Path.Combine(_tempDir, "refii.toml");
        string f3Path = Path.Combine(_tempDir, "refiii.toml");
        File.WriteAllText(f2Path, "# mine\nmax_fps = 60\nkey_roll = \"Space\"\nsomething_unknown = 7\n");
        File.WriteAllText(f3Path, "# Fable 3\nmax_fps = 60\nkey_roll = \"Space\"\n");

        Engine_Title.Active = Engine_Title.Fable3;
        var model = new RuntimeSettingsModel();
        model.ReloadFromPaths(f2Path, f3Path, ensureDefaults: false);
        model.Values["max_fps"] = "120";
        model.Save();

        AssertThat(File.ReadAllText(f2Path)).IsEqual("# mine\nmax_fps = 120\nkey_roll = \"Space\"\nsomething_unknown = 7\n");
        AssertThat(File.ReadAllText(f3Path)).IsEqual("# Fable 3\nmax_fps = 120\nkey_roll = \"Space\"\n");

        // Saved once, the same values again write nothing.
        File.WriteAllText(f2Path, "# touched\nmax_fps = 120\n");
        model.Save();
        AssertThat(File.ReadAllText(f2Path)).IsEqual("# touched\nmax_fps = 120\n");
    }

    [TestCase]
    public void TestATitlesOwnSettingEditsOnlyThatTitlesFile()
    {
        string f2Path = Path.Combine(_tempDir, "refii.toml");
        string f3Path = Path.Combine(_tempDir, "refiii.toml");
        const string f3Text = "# Fable 3\nmax_fps = 60\n";
        File.WriteAllText(f2Path, "decorate_mode_enabled = false\n");
        File.WriteAllText(f3Path, f3Text);

        var model = new RuntimeSettingsModel();
        model.ReloadFromPaths(f2Path, f3Path, ensureDefaults: false);
        model.Values["decorate_mode_enabled"] = "true";
        model.Save();

        AssertThat(File.ReadAllText(f2Path)).IsEqual("decorate_mode_enabled = true\n");
        AssertThat(File.ReadAllText(f3Path)).IsEqual(f3Text);
    }

    [TestCase]
    public void TestSavingWhenFable3TomlDoesNotExistYetCreatesItWithoutCopyingFable2Root()
    {
        string f2Path = Path.Combine(_tempDir, "refii.toml");
        string f3Path = Path.Combine(_tempDir, "refiii.toml");
        string customF2Root = Path.Combine(_tempDir, "custom_fable2_disc").Replace('\\', '/');
        Directory.CreateDirectory(customF2Root);

        File.WriteAllText(f2Path, $"game_data_root = \"{customF2Root}\"\nmax_fps = 60\n");

        var model = new RuntimeSettingsModel();
        model.ReloadFromPaths(f2Path, f3Path, ensureDefaults: false);
        model.Values["max_fps"] = "120";
        model.Save();

        AssertThat(File.Exists(f3Path)).IsTrue();
        SortedDictionary<string, string> f2Saved = RuntimeConfig.ReadValues(f2Path);
        SortedDictionary<string, string> f3Saved = RuntimeConfig.ReadValues(f3Path);

        AssertThat(f2Saved["max_fps"]).IsEqual("120");
        AssertThat(f3Saved["max_fps"]).IsEqual("120");
        AssertThat(f2Saved["game_data_root"]).IsEqual(customF2Root);
        AssertThat(f3Saved["game_data_root"]).IsNotEqual(customF2Root);
    }

    [TestCase]
    public void TestResetTabToDefaultsSyncsResetAcrossBothTitleConfigs()
    {
        string f2Path = Path.Combine(_tempDir, "refii.toml");
        string f3Path = Path.Combine(_tempDir, "refiii.toml");

        var model = new RuntimeSettingsModel();
        model.ReloadFromPaths(f2Path, f3Path, ensureDefaults: false);
        model.Values["max_fps"] = "45";
        model.Save();

        AssertThat(RuntimeConfig.ReadValues(f2Path)["max_fps"]).IsEqual("45");
        AssertThat(RuntimeConfig.ReadValues(f3Path)["max_fps"]).IsEqual("45");

        model.ResetTabToDefaults(SettingsTab.Display);

        AssertThat(RuntimeConfig.ReadValues(f2Path)["max_fps"]).IsEqual("0");
        AssertThat(RuntimeConfig.ReadValues(f3Path)["max_fps"]).IsEqual("0");
    }
}
