namespace Refii.Tests.Camera;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// While the screen is split the window draws no 3D: its pass ran under the halves and its view-0 effects took the
/// view-0 globals (ambient occlusion, exposure, depth) from player one's half.
/// </summary>
[TestSuite]
public class SplitWindowPassTests
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
	public void TheWindowDrawsNo3DWhileSplit()
	{
		AssertThat(Refii.Game.GameWorld.WindowDraws3D(sceneRendering: true, splitDrawn: false)).IsTrue();
		AssertThat(Refii.Game.GameWorld.WindowDraws3D(sceneRendering: true, splitDrawn: true)).IsFalse();
		AssertThat(Refii.Game.GameWorld.WindowDraws3D(sceneRendering: false, splitDrawn: false)).IsFalse();
		AssertThat(Refii.Game.GameWorld.WindowDraws3D(sceneRendering: false, splitDrawn: true)).IsFalse();
	}

	[TestCase]
	public void TwoIndependentCamerasShareNoPerViewEffect()
	{
		// A split camera's effects hold their view's slot only while the halves are drawn, the window's only while not.
		AssertThat(Refii.Core.Engine_Views.MayClaimViewSlot(splitCamera: true, splitDrawn: true)).IsTrue();
		AssertThat(Refii.Core.Engine_Views.MayClaimViewSlot(splitCamera: false, splitDrawn: true)).IsFalse();
		AssertThat(Refii.Core.Engine_Views.MayClaimViewSlot(splitCamera: false, splitDrawn: false)).IsTrue();
		AssertThat(Refii.Core.Engine_Views.MayClaimViewSlot(splitCamera: true, splitDrawn: false)).IsFalse();

		if (RepoRoot() is not { } root)
		{
			return;
		}

		string split = File.ReadAllText(Path.Combine(root, "Godot", "src", "game", "GameSplitView.cs"));
		// Each split camera builds a compositor of its own, marked as a split camera's; none is the window's.
		AssertThat(split.Contains("Compositor = Refii.Lighting.Engine_LevelLighting.BuildCompositor(player, splitCamera: true),", StringComparison.Ordinal)).IsTrue();
		AssertThat(split.Contains("Compositor = first.Compositor", StringComparison.Ordinal)).IsFalse();
		AssertThat(split.Contains("Compositor = _window.Compositor", StringComparison.Ordinal)).IsFalse();
		// The window camera keeps its compositor on the camera (taking it off left a garbage first frame at a merge).
		AssertThat(split.Contains(".Compositor = null", StringComparison.Ordinal)).IsFalse();
		string lighting = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "Lighting", "Engine_LevelLighting.cs"));
		AssertThat(lighting.Contains("public static Compositor? BuildCompositor(int view = 0, bool splitCamera = false)", StringComparison.Ordinal)).IsTrue();
		// Every per-view effect reports its slot and asks the rule before it claims.
		foreach (string file in new[]
				 {
					 Path.Combine(root, "Godot", "src", "Engine", "Lighting", "Engine_ScreenSpaceAoEffect.cs"),
					 Path.Combine(root, "Godot", "src", "Engine", "fx", "Engine_FxDepthCaptureEffect.cs"),
					 Path.Combine(root, "Godot", "src", "Engine", "Lighting", "Engine_PostFxEffect.cs"),
				 })
		{
			string text = File.ReadAllText(file);
			AssertThat(text.Contains("public bool HoldsViewSlot =>", StringComparison.Ordinal))
				.OverrideFailureMessage(file + " does not say whether it holds its view's slot").IsTrue();
			AssertThat(text.Contains("Refii.Core.Engine_Views.MayClaim(SplitCamera)", StringComparison.Ordinal))
				.OverrideFailureMessage(file + " claims its view's slot without asking whose camera it is").IsTrue();
		}
	}

	[TestCase]
	public void TheWorldsPerFrameWriteAsksTheSplit()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string world = File.ReadAllText(Path.Combine(root, "Godot", "src", "game", "GameWorld.cs"));
		AssertThat(world.Contains("bool disable3D = !WindowDraws3D(", StringComparison.Ordinal)).IsTrue();
		AssertThat(world.Contains("GameSplitView.Active);", StringComparison.Ordinal)).IsTrue();
		AssertThat(world.Contains("bool disable3D = !ShouldEnableSceneRendering(", StringComparison.Ordinal)).IsFalse();
	}
}
