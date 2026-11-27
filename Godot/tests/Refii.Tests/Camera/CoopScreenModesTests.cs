namespace Refii.Tests.Camera;

using System;
using System.IO;
using System.Linq;
using GdUnit4;
using Refii.Camera;
using static GdUnit4.Assertions;

/// <summary>
/// Camera stage C2e, Fable 2: the three local co-op screens wired end to end. The setting and its row, the
/// leash per mode in the core camera chunk (free in split and hybrid, the shipped leash on a shared screen),
/// the leash's own measure stated for hybrid on both titles' surfaces, the split stated to the guest from the
/// screen logic, and the views kept open across a split and a rejoin. Read from the source; the run is the
/// game lane's (NOTES.md).
/// </summary>
[TestSuite]
public class CoopScreenModesTests
{
	private static string? RepoRoot()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null; i++)
		{
			if (File.Exists(Path.Combine(at, "Fable2", "versions", "Fable2TU1", "refii_config.toml")))
			{
				return at;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	[TestCase]
	public void TheSettingAndTheRowNameEveryScreen()
	{
		var definitions = Refii.Launcher.Core.RuntimeCVarSet.For(Refii.Core.Engine_Title.Fable2);
		var definition = definitions.Single(d => d.Name == Engine_CameraScreens.ModeKey);
		AssertThat(definition.DefaultValue).IsEqual("split");
		foreach (string name in Engine_CameraScreens.Names)
		{
			AssertThat(definition.AllowedValues!.Contains(name + "|", StringComparison.Ordinal)).OverrideFailureMessage(name).IsTrue();
		}

		AssertThat(definitions.Any(d => d.Name == Engine_CameraScreens.LegacySplitKey)).IsFalse();

		// The row's labels (a Godot node the test host does not load: read from its file), one per row, in
		// every language.
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string row = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "ui", "Engine_UiCoopModeRow.cs"));
		string[] labels =
		{
			"REFII_F2_COOP_MODE_SHARED", "REFII_F2_COOP_MODE_SIDE", "REFII_F2_COOP_MODE_STACKED",
			"REFII_F2_COOP_MODE_HYBRID_SIDE", "REFII_F2_COOP_MODE_HYBRID_STACKED",
		};
		AssertThat(labels.Length).IsEqual(Engine_CameraScreens.Rows);
		AssertThat(row.Contains(string.Join(",\n\t\t", labels.Select(l => "\"" + l + "\"")), StringComparison.Ordinal))
			.OverrideFailureMessage("the row's labels are not in Engine_CameraScreens.Row order").IsTrue();
		foreach (string label in labels)
		{
			foreach (string culture in new[] { "en-uk", "fr-fr", "de-de", "ja-jp", "zh-cht", "sk-sk" })
			{
				AssertThat(Refii.Text.Engine_OwnWords.Find(label, culture)).OverrideFailureMessage(label + " " + culture).IsNotNull();
			}
		}
	}

	[TestCase]
	public void TheLeashIsFreeInSplitAndHybridAndMeasuredForHybrid()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string lua = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "host", "CoopHeroLua.cs"));
		AssertThat(lua.Contains("Refii._camera_read(4, 0) == 3", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("return C.OwnCameras() or C.HybridScreen()", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("if free and not C.HybridScreen() then", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("Refii._coop_tether_stated(C.SharedLeashMetres or C.LeashMetres, math.sqrt(dx * dx + dy * dy + dz * dz))", StringComparison.Ordinal)).IsTrue();
		// The distance hybrid measures against is the shared camera's, held while split (SAC scales the zoom's
		// MaxCoopDistance by its hero only while it frames one hero), taken where the statement lands; the shipped
		// SetMaxCoopDistance stays a tail call (CoopLuaSaveSafeTests).
		AssertThat(lua.Contains("elseif not C.OwnCameras() then\n\t\t\t\t\t\tC.SharedLeashMetres = distance", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua.Contains("if local_two and C.TetherTick ~= nil and C.IsHeroOne(hero) then", StringComparison.Ordinal)).IsTrue();

		// Both titles state it through the one shared statement, on the keys the shell reads.
		foreach (string natives in new[]
		{
			Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"),
			Path.Combine(root, "Fable3", "src", "hooks", "src", "mod_lua_hooks.cpp"),
		})
		{
			string text = File.ReadAllText(natives);
			AssertThat(text.Contains("{\"_coop_tether_stated\", &NativeCoopTetherStated, 0", StringComparison.Ordinal)).OverrideFailureMessage(natives).IsTrue();
			AssertThat(text.Contains("refii_bridge_coop_tether_stated(", StringComparison.Ordinal)).OverrideFailureMessage(natives).IsTrue();
		}

		string told = File.ReadAllText(Path.Combine(root, "src", "runtime", "src", "core", "told_statements.cpp"));
		AssertThat(told.Contains("g_stated_values[22].store(s);\n    g_stated_values[21].store(d);\n    g_stated_values[23].fetch_add(1u);", StringComparison.Ordinal)).IsTrue();
		AssertThat((uint)Refii.Host.HostBridge.StatedValue.CoopTetherDistance).IsEqual(21u);
		AssertThat((uint)Refii.Host.HostBridge.StatedValue.CoopTetherSeparation).IsEqual(22u);
		AssertThat((uint)Refii.Host.HostBridge.StatedValue.CoopTetherSerial).IsEqual(23u);
	}

	[TestCase]
	public void TheGuestIsToldTheSplitAndTheViewsStayOpen()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string session = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "host", "CoopSession.cs"));
		AssertThat(session.Contains("int want = Refii.Game.GameSplitView.GuestSplit ? 1 : 0;", StringComparison.Ordinal)).IsTrue();

		string split = File.ReadAllText(Path.Combine(root, "Godot", "src", "game", "GameSplitView.cs"));
		AssertThat(split.Contains("Refii.Core.Engine_Views.SetDrawn(on);", StringComparison.Ordinal)).IsTrue();
		// The views are built once and freed only when local co-op ends: Close is called from the not-local
		// branch and the tree's exit alone.
		AssertThat(split.Split("Close();").Length - 1).IsEqual(2);

		// Every system that keeps something per view builds it while the views are open, not while drawn.
		foreach (string file in new[]
		{
			Path.Combine("Engine", "fx", "Engine_FxAdditiveLayer.cs"),
			Path.Combine("Engine", "fx", "Engine_FxDisplacement.cs"),
			Path.Combine("Engine", "entities", "Engine_EntityHighlightLayer.cs"),
			Path.Combine("Engine", "level", "water", "Engine_WaterReflection.cs"),
		})
		{
			string text = File.ReadAllText(Path.Combine(root, "Godot", "src", file));
			AssertThat(text.Contains("Refii.Core.Engine_Views.Open", StringComparison.Ordinal)).OverrideFailureMessage(file).IsTrue();
		}

		// Hybrid's own swap raises camera_screen_changed with reason 1, on both titles.
		string f2 = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp"));
		AssertThat(f2.Contains("e.reason = mode == g_camera_screen_raised && mode == cam::kScreenHybrid ? 1 : 0;", StringComparison.Ordinal)).IsTrue();
		string f3 = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "mod_lua_hooks.cpp"));
		AssertThat(f3.Contains("mode == g_camera_screen_raised && mode == cam::kScreenHybrid ? 1 : 0", StringComparison.Ordinal)).IsTrue();
	}
}
