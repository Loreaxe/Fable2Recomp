namespace Refii.Tests.Camera;

using System;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// A co-op view that draws again (the halves at a split, the window at a merge or when player two leaves)
/// starts its temporal history over and continues the tone history of the view it replaces. Read from the
/// source (the effects are Godot runtime types); the run is the game lane's (NOTES.md).
/// </summary>
[TestSuite]
public class CoopViewHistoryTests
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

	private static string Read(string root, params string[] parts) => File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));

	private static string Body(string source, string head)
	{
		int at = source.IndexOf(head, StringComparison.Ordinal);
		AssertThat(at >= 0).OverrideFailureMessage(head).IsTrue();
		int end = source.IndexOf("\n\t}\n", at, StringComparison.Ordinal);
		return source.Substring(at, end - at);
	}

	private static void InOrder(string body, params string[] steps)
	{
		int last = -1;
		foreach (string step in steps)
		{
			int at = body.IndexOf(step, last + 1, StringComparison.Ordinal);
			AssertThat(at > last).OverrideFailureMessage(step).IsTrue();
			last = at;
		}
	}

	[TestCase]
	public void TheToneHistoryHandedOnStopsAtTheTrapsScalars()
	{
		AssertThat(Refii.Lighting.Engine_PostFxState.ToneBytes).IsEqual(Refii.Lighting.Engine_PostFxState.TrapPrevMean * sizeof(float));
		AssertThat(Refii.Lighting.Engine_PostFxState.ToneBytes).IsLess(Refii.Lighting.Engine_PostFxState.Bytes);
	}

	[TestCase]
	public void TheTemporalResetClearsOnlyTaasContextBeforeTheResolve()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string effect = Read(root, "Godot", "src", "Engine", "Lighting", "Engine_TemporalHistoryEffect.cs");
		AssertThat(effect.Contains("EffectCallbackType = EffectCallbackTypeEnum.PostTransparent;\n\t\tEnabled = false;", StringComparison.Ordinal)).IsTrue();
		InOrder(Body(effect, "public override void _RenderCallback"),
			"if (Interlocked.Exchange(ref _drop, 0) == 0)",
			"buffers.HasTexture(TaaContext, TaaHistory)",
			"buffers.ClearContext(TaaContext);");
		AssertThat(effect.Contains("private static readonly StringName TaaContext = \"taa\";", StringComparison.Ordinal)).IsTrue();
		// FSR 2: the view's own sharpness, set again, configures its buffers once; nothing it draws changes.
		AssertThat(effect.Contains("view.Scaling3DMode == Viewport.Scaling3DModeEnum.Fsr2)\n\t\t{\n\t\t\tRenderingServer.ViewportSetFsrSharpness(view.GetViewportRid(), view.FsrSharpness);", StringComparison.Ordinal)).IsTrue();

		string lighting = Read(root, "Godot", "src", "Engine", "Lighting", "Engine_LevelLighting.cs");
		AssertThat(lighting.Contains("effects.Add(new Engine_TemporalHistoryEffect());", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void OnlyTheCoopScreenResumesAView()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		// Single player is unchanged: nothing else asks for a drop or a hand-on.
		foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "Godot", "src"), "*.cs", SearchOption.AllDirectories))
		{
			string text = File.ReadAllText(file);
			if (Path.GetFileName(file) is "GameSplitView.cs" or "Engine_TemporalHistoryEffect.cs" or "Engine_PostFxEffect.cs")
			{
				continue;
			}

			AssertThat(text.Contains("Engine_TemporalHistoryEffect.Drop(", StringComparison.Ordinal)).OverrideFailureMessage(file).IsFalse();
			AssertThat(text.Contains("Engine_PostFxEffect.Continue(", StringComparison.Ordinal)).OverrideFailureMessage(file).IsFalse();
		}

		string split = Read(root, "Godot", "src", "game", "GameSplitView.cs");
		InOrder(Body(split, "private void Draw(bool on"),
			"GetViewport().Disable3D = on;",
			"Resume(_cameras[player], _window, _resized[player]);",
			"Resume(_window, _cameras[0], buffersFresh: false);");
		InOrder(Body(split, "private void Close()"),
			"if (Active)",
			"Resume(_window, _cameras[0], buffersFresh: false);",
			"Refii.Lighting.Engine_LevelLighting.ReleaseCompositor(_cameras[player], releaseStaticFallbacks: false);");
		InOrder(Body(split, "private static void Resume("),
			"Refii.Lighting.Engine_TemporalHistoryEffect.Drop(camera, buffersFresh);",
			"Refii.Lighting.Engine_PostFxEffect.Continue(from, camera);");
		AssertThat(split.Contains("_window = first;\n\t\tRefii.Lighting.Engine_TemporalHistoryEffect.Quiet();", StringComparison.Ordinal)).IsTrue();
		AssertThat(split.Contains("MatchWindow();", StringComparison.Ordinal)).IsTrue();

		string postFx = Read(root, "Godot", "src", "Engine", "Lighting", "Engine_PostFxEffect.cs");
		InOrder(postFx,
			"private void TakeStateOf(Engine_PostFxEffect source)",
			"source._firstFrame",
			"_rd.BufferCopy(source._stateBuffer, _stateBuffer, 0u, 0u, (uint)Engine_PostFxState.ToneBytes);",
			"_rd.BufferCopy(source._histogramBuffer, _histogramBuffer, 0u, 0u, (uint)HistogramBytes);",
			"_firstFrame = false;",
			"_historyResetSeen = source._historyResetSeen;");
	}
}
