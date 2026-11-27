using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using Refii.Assets;
using Refii.Core;
using Refii.Game;
using Refii.Lighting;
using Refii.Textures;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// Unit and feature tests verifying D3D12/DXIL UI shader derivative invariants,
/// fast boot prebuild marker round-trips, and managed heap census coverage.
/// </summary>
[TestSuite]
public sealed class Dx12AndMemoryOptimizationTests
{
	private static string FindRepoRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			if (Directory.Exists(Path.Combine(dir, "src", "Engine", "ui")))
			{
				return dir;
			}
			string candidate = Path.Combine(dir, "Godot");
			if (Directory.Exists(Path.Combine(candidate, "src", "Engine", "ui")))
			{
				return candidate;
			}
			dir = Path.GetDirectoryName(dir);
		}
		throw new DirectoryNotFoundException("Could not locate Godot/src/Engine/ui from test base directory.");
	}

	/// <summary>
	/// Verifies <c>ui_text.gdshader</c>, <c>ui_quad.gdshader</c>, and <c>ui_world_marker.gdshader</c>
	/// sample textures cleanly and emit the hardware <c>ALPHA = 0.0</c> blend identity instead of
	/// <c>discard;</c> in <c>depth_draw_never</c> UI passes.
	/// </summary>
	[TestCase]
	public void UiShadersSampleAllImplicitDerivativesBeforeAnyDiscard()
	{
		string godotRoot = FindRepoRoot();
		foreach (string shaderFile in new[] { "ui_text.gdshader", "ui_quad.gdshader", "ui_world_marker.gdshader" })
		{
			string fullPath = Path.Combine(godotRoot, "src", "Engine", "ui", shaderFile);
			AssertThat(File.Exists(fullPath)).IsTrue();
			string source = File.ReadAllText(fullPath);
			int fragIdx = source.IndexOf("void fragment()", StringComparison.Ordinal);
			AssertThat(fragIdx).IsGreaterEqual(0);
			string fragBody = StripShaderComments(source[fragIdx..]);

			AssertThat(System.Text.RegularExpressions.Regex.IsMatch(fragBody, @"\bdiscard\s*;"))
				.OverrideFailureMessage($"{shaderFile} should omit discard; under depth_draw_never")
				.IsFalse();

			int lastTextureSample = Math.Max(
				fragBody.LastIndexOf("texture(", StringComparison.Ordinal),
				Math.Max(
					fragBody.LastIndexOf("textureLod(", StringComparison.Ordinal),
					fragBody.LastIndexOf("textureGrad(", StringComparison.Ordinal)));
			AssertThat(lastTextureSample).IsGreater(0);
		}
	}

	/// <summary>
	/// Prevents regressions of the Intel Arc D3D12 GPU hang (`LiveKernelEvent 141` / `0x887a0005`)
	/// caused by evaluating screen-space derivatives (`fwidth(distance)`) on a sampled `.g` SDF
	/// texel or issuing redundant `fwidth(shipped)` calls in `ui_text.gdshader`.
	/// </summary>
	[TestCase]
	public void UiTextShaderAvoidsDependentTextureDerivativesAndRedundantFwidth()
	{
		string godotRoot = FindRepoRoot();
		string fullPath = Path.Combine(godotRoot, "src", "Engine", "ui", "ui_text.gdshader");
		AssertThat(File.Exists(fullPath)).IsTrue();
		string source = File.ReadAllText(fullPath);
		int fragIdx = source.IndexOf("void fragment()", StringComparison.Ordinal);
		AssertThat(fragIdx).IsGreaterEqual(0);
		string fragBody = string.Join("\n", System.Linq.Enumerable.Select(
			source[fragIdx..].Split('\n'),
			static line =>
			{
				int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
				return commentIdx >= 0 ? line[..commentIdx] : line;
			}));

		AssertThat(fragBody.Contains("fwidth(distance)", StringComparison.Ordinal)).IsFalse();
		AssertThat(fragBody.Contains("dFdx(distance)", StringComparison.Ordinal)).IsFalse();
		AssertThat(fragBody.Contains("dFdy(distance)", StringComparison.Ordinal)).IsFalse();
		AssertThat(fragBody.Contains("fwidth(texel", StringComparison.Ordinal)).IsFalse();
		AssertThat(fragBody.Contains("fwidth(shipped", StringComparison.Ordinal)).IsFalse();
		AssertThat(fragBody.Contains("dFdx(UV)", StringComparison.Ordinal)).IsTrue();
		AssertThat(fragBody.Contains("dFdy(UV)", StringComparison.Ordinal)).IsTrue();
		AssertThat(fragBody.Contains("clamp(0.25 / max(magnified", StringComparison.Ordinal)).IsTrue();
	}

	/// <summary>
	/// Verifies that `Engine_TexPrebuild.WriteFastMarker` and `TryReadFastMarker` round-trip
	/// when the fingerprint and mounted file count match, and invalidate when the mounted file count changes.
	/// </summary>
	[TestCase]
	public void FastPrebuildMarkerRoundTripsAndInvalidatesOnFileCountChange()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), "refii-test-marker-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempDir);
		string markerPath = Path.Combine(tempDir, "orm_prebuild.txt");
		try
		{
			Directory.CreateDirectory(Engine_TexCache.CacheRoot);
			Engine_TexPrebuild.WriteFastMarker(markerPath, 140141, built: 1949, total: 1949, failed: 0);

			bool hit = Engine_TexPrebuild.TryReadFastMarker(
				markerPath, 140141, out int built, out int total, out int failed);
			AssertThat(hit).IsTrue();
			AssertThat(built).IsEqual(1949);
			AssertThat(total).IsEqual(1949);
			AssertThat(failed).IsEqual(0);

			bool missOnDifferentCount = Engine_TexPrebuild.TryReadFastMarker(
				markerPath, 140142, out _, out _, out _);
			AssertThat(missOnDifferentCount).IsFalse();
		}
		finally
		{
			if (Directory.Exists(tempDir))
			{
				Directory.Delete(tempDir, recursive: true);
			}
		}
	}

	/// <summary>
	/// Verifies that the BNK filesystem and UI bitmap font cache census helpers
	/// execute safely without unmanaged Godot interop when unmounted.
	/// </summary>
	[TestCase]
	public void HeapCensusHelpersAreSafeWhenUnmounted()
	{
		(int files, int archives, long bytes) = Engine_GameAssets.GetHeapCensus();
		AssertThat(files).IsGreaterEqual(0);
		AssertThat(archives).IsGreaterEqual(0);
		AssertThat(bytes).IsGreaterEqual(0L);
		AssertThat(Engine_BitmapFontTexture.CachedFontCount).IsGreaterEqual(0);
		AssertThat(Engine_BitmapFontTexture.CachedFontBytes).IsGreaterEqual(0L);
	}

	/// <summary>
	/// Verifies that background worker calls to <see cref="Engine_ServerSyncs.RunGpuUpload{T}"/>
	/// never execute off the main thread (even after waiting) and complete cleanly on the main
	/// thread when <see cref="Engine_ServerSyncs.PumpWorkerGpuUploads"/> runs.
	/// </summary>
	[TestCase]
	public void WorkerGpuUploadsExecuteExclusivelyOnMainThreadAndRespectLoadCoverBudget()
	{
		Engine_ServerSyncs.ResetMainThreadForTest();
		try
		{
			Engine_ServerSyncs.MarkMainThread();
			int mainThreadId = Environment.CurrentManagedThreadId;
			Engine_ServerSyncs.BeginMainFrameUploadBudget();

			// Queue two 40 MB uploads from a worker thread: under normal play (48 MB cap), the first
			// pump drains 1 item (40 MB < 48 MB) and pauses before the second; with LoadCoverActive (96 MB cap),
			// the pump drains the second item immediately in the same frame.
			var executedThreads = new System.Collections.Concurrent.ConcurrentQueue<int>();
			var workerTask = System.Threading.Tasks.Task.Run(() =>
			{
				int a = Engine_ServerSyncs.RunGpuUpload(40L * 1024L * 1024L, () =>
				{
					executedThreads.Enqueue(Environment.CurrentManagedThreadId);
					return 11;
				});
				int b = Engine_ServerSyncs.RunGpuUpload(40L * 1024L * 1024L, () =>
				{
					executedThreads.Enqueue(Environment.CurrentManagedThreadId);
					return 31;
				});
				return a + b;
			});

			// Wait briefly for the worker to enqueue its first upload, then verify it has not self-executed.
			System.Threading.Thread.Sleep(30);
			AssertThat(workerTask.IsCompleted).IsFalse();

			Engine_ServerSyncs.PumpWorkerGpuUploads(10.0);
			System.Threading.Thread.Sleep(20);
			// Second 40 MB upload arrives; with LoadCoverActive == false, _frameUploadBytes is 40 MB (< 48 MB),
			// so a second pump drains it and brings total to 80 MB (>= 48 MB), capping further normal-play drains.
			Engine_ServerSyncs.LoadCoverActive = true;
			Engine_ServerSyncs.PumpWorkerGpuUploads(10.0);

			int sum = workerTask.GetAwaiter().GetResult();
			AssertThat(sum).IsEqual(42);
			AssertThat(executedThreads.Count).IsEqual(2);
			foreach (int tid in executedThreads)
			{
				AssertThat(tid).IsEqual(mainThreadId);
			}
		}
		finally
		{
			Engine_ServerSyncs.ResetMainThreadForTest();
		}
	}

	/// <summary>
	/// Guards against regressions that reintroduce C++ ResourceLoader mutex inversions on entity
	/// worker threads or lock-order inversions in Engine_ShadowProxy.MergedFor.
	/// </summary>
	[TestCase]
	public void EntityLoaderAndShadowProxyAvoidResourceLoaderAndLockOrderInversions()
	{
		string godotRoot = FindRepoRoot();

		string gameEntitiesSource = File.ReadAllText(Path.Combine(godotRoot, "src", "game", "GameEntities.cs"));
		AssertThat(gameEntitiesSource.Contains("ResourceLoader.Load<Mesh>(", StringComparison.Ordinal)).IsFalse();

		string shadowProxySource = File.ReadAllText(Path.Combine(godotRoot, "src", "Engine", "entities", "Engine_ShadowProxy.cs"));
		int mergedForIdx = shadowProxySource.IndexOf("private static ArrayMesh? MergedFor(", StringComparison.Ordinal);
		int takeMergeIdx = shadowProxySource.IndexOf("public static (double WaitMs, double BuildMs) TakeMergeTimes()", StringComparison.Ordinal);
		AssertThat(mergedForIdx).IsGreater(0);
		AssertThat(takeMergeIdx).IsGreater(mergedForIdx);
		string mergedForBody = shadowProxySource[mergedForIdx..takeMergeIdx];
		int buildCallIdx = mergedForBody.IndexOf("Build(source, body, hidden, skinned, always)", StringComparison.Ordinal);
		int firstLockIdx = mergedForBody.IndexOf("lock (perMask)", StringComparison.Ordinal);
		int secondLockIdx = mergedForBody.LastIndexOf("lock (perMask)", StringComparison.Ordinal);
		AssertThat(firstLockIdx).IsGreater(0);
		AssertThat(buildCallIdx).IsGreater(firstLockIdx);
		AssertThat(secondLockIdx).IsGreater(buildCallIdx);
	}

	/// <summary>
	/// Guards against the Intel Arc D3D12 title-screen GPU TDR regression (`0x887a0005`):
	/// UI text and world-marker shaders must declare explicit `hint_default_*` sampler fallbacks,
	/// gate `UV2` and `ui_text_outline_cover` behind uniform booleans for Fable 2 meshes without UV2,
	/// and keep `ui_text_outline.gdshaderinc` free of dual-sampler branches or `textureSize` inside loops.
	/// </summary>
	[TestCase]
	public void UiTextAndOutlineShadersMaintainD3D12SamplerAndUniformInvariants()
	{
		string godotRoot = FindRepoRoot();
		string uiDir = Path.Combine(godotRoot, "src", "Engine", "ui");

		string uiText = File.ReadAllText(Path.Combine(uiDir, "ui_text.gdshader"));
		AssertThat(uiText.Contains("uniform sampler2D atlas : filter_linear_mipmap, repeat_disable, hint_default_transparent;", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiText.Contains("uniform sampler2D distance_field", StringComparison.Ordinal)).IsFalse();
		AssertThat(uiText.Contains("uniform bool has_uv2_modes = false;", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiText.Contains("uniform bool outline = false;", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiText.Contains("if (has_uv2_modes)", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiText.Contains("if (outline && outlined)", StringComparison.Ordinal)).IsTrue();

		string uiWorldMarker = File.ReadAllText(Path.Combine(uiDir, "ui_world_marker.gdshader"));
		AssertThat(uiWorldMarker.Contains("uniform sampler2D tex : source_color, filter_linear_mipmap, repeat_disable, hint_default_transparent;", StringComparison.Ordinal)).IsTrue();

		string uiQuad = File.ReadAllText(Path.Combine(uiDir, "ui_quad.gdshader"));
		AssertThat(uiQuad.Contains("render_mode unshaded, cull_disabled, depth_draw_never, blend_mix;", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiQuad.Contains("textureGrad(diffuse_tex, uv, uv_dx, uv_dy)", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiQuad.Contains("textureGrad(detail_tex, detail_uv, detail_dx, detail_dy)", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiQuad.Contains("textureGrad(keycap_tex, UV, raw_uv_dx, raw_uv_dy)", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiQuad.Contains("textureLod(", StringComparison.Ordinal)).IsFalse();
		int quadBranchIdx = uiQuad.IndexOf("if (has_keycap && (glyph_player_two ? ui_keyboard_mode_two : ui_keyboard_mode))", StringComparison.Ordinal);
		AssertThat(quadBranchIdx).IsGreater(0);
		AssertThat(uiQuad.IndexOf("dFdx(uv)", StringComparison.Ordinal)).IsLess(quadBranchIdx);
		AssertThat(uiQuad.IndexOf("dFdy(detail_uv)", StringComparison.Ordinal)).IsLess(quadBranchIdx);
		AssertThat(uiQuad.Contains("bool should_discard = clipped;", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiQuad.Contains("should_discard = (final_alpha <= 0.001);", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiQuad.Contains("if (!should_discard)", StringComparison.Ordinal)).IsTrue();
		AssertThat(System.Text.RegularExpressions.Regex.IsMatch(StripShaderComments(uiQuad), @"\bdiscard\s*;")).IsFalse();

		string uiOutlineInc = File.ReadAllText(Path.Combine(uiDir, "ui_text_outline.gdshaderinc"));
		AssertThat(uiOutlineInc.Contains("float ui_text_outline_cover(sampler2D glyph_atlas,", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiOutlineInc.Contains("textureSize(", StringComparison.Ordinal)).IsFalse();
		int fromFieldIdx = uiOutlineInc.IndexOf("if (from_field)", StringComparison.Ordinal);
		int firstForIdx = uiOutlineInc.IndexOf("for (int y = -1;", StringComparison.Ordinal);
		AssertThat(fromFieldIdx).IsGreater(0);
		AssertThat(firstForIdx).IsGreater(fromFieldIdx);

		// Verify project.godot's configured application icon exists so startup never logs a missing icon error.
		string projectGodot = File.ReadAllText(Path.Combine(godotRoot, "project.godot"));
		AssertThat(projectGodot.Contains("config/icon=\"res://assets/launcher/fable.png\"", StringComparison.Ordinal)).IsTrue();
		AssertThat(File.Exists(Path.Combine(godotRoot, "assets", "launcher", "fable.png"))).IsTrue();
	}

	/// <summary>
	/// Verifies pure storage-layout matching used by <see cref="Engine_TextureUpload"/> to update
	/// textures in-place on D3D12 without live <c>RS::texture_replace</c> descriptor churn, and
	/// verifies load-time UI book prebuild restricts eager builds to core screens.
	/// </summary>
	[TestCase]
	public void TextureUploadStorageLayoutAndCoreBookPrebuildInvariants()
	{
		AssertThat(Engine_TextureUpload.MatchesStorageLayout(
			512, 512, Godot.Image.Format.Dxt5, true,
			512, 512, Godot.Image.Format.Dxt5, true)).IsTrue();
		AssertThat(Engine_TextureUpload.MatchesStorageLayout(
			512, 512, Godot.Image.Format.Dxt5, true,
			256, 512, Godot.Image.Format.Dxt5, true)).IsFalse();
		AssertThat(Engine_TextureUpload.MatchesStorageLayout(
			512, 512, Godot.Image.Format.Dxt5, true,
			512, 512, Godot.Image.Format.Rgba8, true)).IsFalse();
		AssertThat(Engine_TextureUpload.MatchesStorageLayout(
			512, 512, Godot.Image.Format.Dxt5, true,
			512, 512, Godot.Image.Format.Dxt5, false)).IsFalse();

		AssertThat(Engine_UiBookPrebuild.Wants("pause")).IsTrue();
		AssertThat(Engine_UiBookPrebuild.Wants("items")).IsTrue();
		AssertThat(Engine_UiBookPrebuild.Wants("abilities")).IsFalse();
		AssertThat(Engine_UiBookPrebuild.Wants("quests")).IsFalse();
		AssertThat(Engine_UiBookPrebuild.Wants("logbook")).IsFalse();
	}

	/// <summary>
	/// Verifies that <see cref="Engine_RdHealth"/> latches cleanly on the first invalid RID / failure,
	/// increments suppressed counts without re-tripping on subsequent calls, and rate-limits repeating logs.
	/// </summary>
	[TestCase]
	public void RdHealthCircuitBreakerAndLogThrottleWorkCleanly()
	{
		Engine_RdHealth.Reset();
		try
		{
			AssertThat(Engine_RdHealth.IsDeviceLost).IsFalse();
			AssertThat(Engine_RdHealth.EvaluateResourceHealth(true, "test.ok", out bool firstOk)).IsTrue();
			AssertThat(firstOk).IsFalse();
			AssertThat(Engine_RdHealth.IsDeviceLost).IsFalse();

			AssertThat(Engine_RdHealth.EvaluateResourceHealth(false, "test.fail1", out bool firstFail)).IsFalse();
			AssertThat(firstFail).IsTrue();
			AssertThat(Engine_RdHealth.IsDeviceLost).IsTrue();
			AssertThat(Engine_RdHealth.SuppressedCount).IsEqual(0);

			AssertThat(Engine_RdHealth.EvaluateResourceHealth(false, "test.fail2", out bool secondFail)).IsFalse();
			AssertThat(secondFail).IsFalse();
			AssertThat(Engine_RdHealth.SuppressedCount).IsEqual(1);

			AssertThat(Engine_RdHealth.CheckRdValid(default, "test.defaultRid")).IsFalse();
			AssertThat(Engine_RdHealth.SuppressedCount).IsEqual(2);

			AssertThat(Engine_RdHealth.ShouldLogThrottled("k", 1000, 100, out int sup0)).IsTrue();
			AssertThat(sup0).IsEqual(0);
			AssertThat(Engine_RdHealth.ShouldLogThrottled("k", 1000, 500, out _)).IsFalse();
			AssertThat(Engine_RdHealth.ShouldLogThrottled("k", 1000, 900, out _)).IsFalse();
			AssertThat(Engine_RdHealth.ShouldLogThrottled("k", 1000, 1100, out int sup1)).IsTrue();
			AssertThat(sup1).IsEqual(2);
		}
		finally
		{
			Engine_RdHealth.Reset();
		}
	}

	/// <summary>
	/// Verifies that <see cref="Engine_UiFrontEndPrebuild"/> deduplicates normalised texture paths and
	/// spreads document load, individual texture decode/upload, pattern book build, and owned-book build
	/// across separate frames so startup videos never receive a synchronous single-frame burst.
	/// </summary>
	[TestCase]
	public void FrontEndPrebuildSpreadsWorkAcrossSeparateFramesAndDeduplicatesTextures()
	{
		var bgScreen = new Engine_UiScreen
		{
			Name = "scrollingbackground",
			Nodes = new[]
			{
				new UiNode { Quad = new UiQuad { TexturePath = @"art\gui\bg.tex" } },
			},
		};
		var menuScreen = new Engine_UiScreen
		{
			Name = "frontendmainmenu",
			Nodes = new[]
			{
				new UiNode { Quad = new UiQuad { TexturePath = "art/gui/bg.tex" } },
				new UiNode { Quad = new UiQuad { TexturePath = @"art\gui\button.tex" } },
				new UiNode { Quad = new UiQuad { TexturePath = @"art\gui\frame.tex" } },
			},
		};

		var events = new List<string>();
		var warmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var prebuild = new Engine_UiFrontEndPrebuild();

		Engine_UiScreen? LoadDoc(string name)
		{
			events.Add("doc:" + name);
			return name == "scrollingbackground" ? bgScreen : menuScreen;
		}

		bool WarmTex(string path)
		{
			events.Add("tex:" + path);
			return warmed.Add(path);
		}

		void BuildBook(string name) => events.Add("pattern:" + name);
		bool BuildOwned(string name)
		{
			events.Add("owned:" + name);
			return true;
		}

		// Frame 1: scrollingbackground doc only
		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.LoadMainMenuDocument);
		// Frame 2: frontendmainmenu doc only
		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.WarmTextures);
		AssertThat(prebuild.TotalQueuedTextures).IsEqual(3); // bg.tex deduplicated across slash styles

		// Frames 3..5: exactly ONE uncached texture per frame
		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.NextTextureIndex).IsEqual(1);
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.WarmTextures);

		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.NextTextureIndex).IsEqual(2);
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.WarmTextures);

		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.NextTextureIndex).IsEqual(3);
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.BuildBackgroundPatternBook);

		// Frames 6..8: pattern books and owned book each on separate frames
		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.BuildMainMenuPatternBook);

		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.PrebuildMainMenuOwnedBook);

		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.Complete);
		AssertThat(events.Count).IsEqual(8);
	}

	/// <summary>
	/// Verifies classic spot-shadow isolation from Modern mode, UI visibility layer symmetry across
	/// quads/text/fade chains, and shared Fable 2 / Fable 3 DRY canvas camera construction.
	/// </summary>
	[TestCase]
	public void SpotShadowsAndUiSceneBuilderEnforceClassicGuardAndLayerSymmetry()
	{
		string godotRoot = FindRepoRoot();

		string spotShadows = File.ReadAllText(Path.Combine(godotRoot, "src", "Engine", "material", "Engine_ClassicSpotShadows.cs"));
		AssertThat(spotShadows.Contains("if (_initialized || !Engine_ClassicMaterial.Active", StringComparison.Ordinal)).IsTrue();
		AssertThat(spotShadows.Contains("if (!Engine_ClassicMaterial.Active", StringComparison.Ordinal)).IsTrue();
		AssertThat(spotShadows.Contains("idleViewport.Disable3D = true;", StringComparison.Ordinal)).IsTrue();

		string builder = File.ReadAllText(Path.Combine(godotRoot, "src", "Engine", "ui", "Engine_UiSceneBuilder.cs"));
		AssertThat(builder.Contains("public static void SetVisibilityLayer(MeshInstance3D mesh, float alpha)", StringComparison.Ordinal)).IsTrue();
		AssertThat(builder.Contains("SetVisibilityLayer(node, _inheritedOpacity);", StringComparison.Ordinal)).IsTrue();
		AssertThat(builder.Contains("SetVisibilityLayer(label, colour.A);", StringComparison.Ordinal)).IsTrue();
		AssertThat(builder.Contains("SetVisibilityLayer(mesh, accumulated);", StringComparison.Ordinal)).IsTrue();
		AssertThat(builder.Contains("SetVisibilityLayer(mesh, opacity);", StringComparison.Ordinal)).IsTrue();
		AssertThat(builder.Contains("public static Camera3D CreateCanvasCamera(", StringComparison.Ordinal)).IsTrue();

		string fable3Canvas = File.ReadAllText(Path.Combine(godotRoot, "src", "Titles", "Fable3", "Fable3Canvas.cs"));
		string fable3Icons = File.ReadAllText(Path.Combine(godotRoot, "src", "Titles", "Fable3", "Fable3WorldIcons.cs"));
		AssertThat(fable3Canvas.Contains("Engine_UiSceneBuilder.CreateCanvasCamera(", StringComparison.Ordinal)).IsTrue();
		AssertThat(fable3Icons.Contains("Engine_UiSceneBuilder.CreateCanvasCamera(", StringComparison.Ordinal)).IsTrue();
	}

	/// <summary>
	/// Verifies root 3D scalers/MSAA/SSAA/TAA remain disabled while no 3D world is active (boot videos and
	/// front-end menus) and restore when a 3D world is mounted, without capping active window FPS.
	/// </summary>
	[TestCase]
	public void GameWindowSettingsDisablesRoot3DEffectsWithoutActiveWorld()
	{
		var settings = new Refii.Game.GameWindowSettings
		{
			Size = new Godot.Vector2I(1280, 720),
			RenderScalePercent = 75,
			Msaa = 4,
			Scaler = "fsr2",
			FsrQuality = "quality",
			ScreenSpaceAa = "fxaa",
			Taa = true,
			MeshLodPixels = 2.0f,
			MaxFps = 0,
			Vsync = false,
		};

		var idle = settings.ResolveRoot3DQuality(new Godot.Vector2I(1920, 1080), activeWorld: false);
		AssertThat(idle.ScalingMode).IsEqual(Godot.Viewport.Scaling3DModeEnum.Bilinear);
		AssertThat(idle.ScalingScale).IsEqual(1.0f);
		AssertThat(idle.MsaaMode).IsEqual(Godot.Viewport.Msaa.Disabled);
		AssertThat(idle.SsaaMode).IsEqual(Godot.Viewport.ScreenSpaceAAEnum.Disabled);
		AssertThat(idle.UseTaa).IsFalse();

		var active = settings.ResolveRoot3DQuality(new Godot.Vector2I(1920, 1080), activeWorld: true);
		AssertThat(active.ScalingMode).IsEqual(Godot.Viewport.Scaling3DModeEnum.Fsr2);
		AssertThat(active.MsaaMode).IsEqual(Godot.Viewport.Msaa.Msaa4X);
		AssertThat(active.SsaaMode).IsEqual(Godot.Viewport.ScreenSpaceAAEnum.Fxaa);
		AssertThat(active.UseTaa).IsFalse(); // FSR2 replaces TAA

		AssertThat(Refii.Game.GameWindow.ResolveVsyncMode(settings, isD3d12: true))
			.IsEqual(Godot.DisplayServer.VSyncMode.Disabled);
	}

	/// <summary>
	/// Verifies that 3D scene rendering runs incrementally behind the loading cover as soon as an
	/// active world mounts (preventing a cold-start render stall at worldReady) while root 3D quality
	/// upscaling/AA, DepthCapture, and PostFX stay gated during streaming, stagger across settle
	/// frames, and remain active once the cover drops; and that multi-pass compute batching stays
	/// disabled on D3D12.
	/// </summary>
	[TestCase]
	public void LoadCoverSceneAndEffectsRemainGatedUntilStreamingFinishes()
	{
		AssertThat(Refii.Fx.Engine_FxDepthCaptureEffect.RequiredSettleFrames).IsEqual(3);
		AssertThat(Refii.Lighting.Engine_PostFxEffect.RequiredSettleFrames).IsEqual(5);
		AssertThat(Refii.Lighting.Engine_PostFxEffect.ShouldBatchComputeLists(isD3D12Driver: true)).IsFalse();
		AssertThat(Refii.Lighting.Engine_PostFxEffect.ShouldBatchComputeLists(isD3D12Driver: false)).IsTrue();

		// No active world -> everything stays off.
		AssertThat(Refii.Game.GameWorld.ShouldEnableSceneRendering(
			hasActiveWorld: false, coverHeld: true, readyForWarmupAndSettle: true)).IsFalse();
		AssertThat(Refii.Game.GameWorld.ShouldEnableWorldQualityAndEffects(
			hasActiveWorld: false, coverHeld: true, readyForWarmupAndSettle: true)).IsFalse();
		AssertThat(Refii.Game.GameWorld.ShouldRunRenderEffects(
			hasActiveWorld: false, coverHeld: true, readyForWarmupAndSettle: true, settleFrames: 10, requiredSettleFrames: 1)).IsFalse();

		// Mounted region, cover held, streaming still in progress -> 3D renders incrementally behind cover,
		// while quality upscalers and compositor effects stay off.
		AssertThat(Refii.Game.GameWorld.ShouldEnableSceneRendering(
			hasActiveWorld: true, coverHeld: true, readyForWarmupAndSettle: false)).IsTrue();
		AssertThat(Refii.Game.GameWorld.ShouldEnableWorldQualityAndEffects(
			hasActiveWorld: true, coverHeld: true, readyForWarmupAndSettle: false)).IsFalse();
		AssertThat(Refii.Game.GameWorld.ShouldRunRenderEffects(
			hasActiveWorld: true, coverHeld: true, readyForWarmupAndSettle: false, settleFrames: 10, requiredSettleFrames: Refii.Fx.Engine_FxDepthCaptureEffect.RequiredSettleFrames)).IsFalse();
		AssertThat(Refii.Game.GameWorld.ShouldRunRenderEffects(
			hasActiveWorld: true, coverHeld: true, readyForWarmupAndSettle: false, settleFrames: 10, requiredSettleFrames: Refii.Lighting.Engine_PostFxEffect.RequiredSettleFrames)).IsFalse();

		// Streaming & worker uploads finished -> quality turns on immediately at settle,
		// DepthCapture at frame 3, PostFX at frame 5.
		AssertThat(Refii.Game.GameWorld.ShouldEnableWorldQualityAndEffects(
			hasActiveWorld: true, coverHeld: true, readyForWarmupAndSettle: true)).IsTrue();
		AssertThat(Refii.Game.GameWorld.ShouldRunRenderEffects(
			hasActiveWorld: true, coverHeld: true, readyForWarmupAndSettle: true, settleFrames: 2, requiredSettleFrames: Refii.Fx.Engine_FxDepthCaptureEffect.RequiredSettleFrames)).IsFalse();
		AssertThat(Refii.Game.GameWorld.ShouldRunRenderEffects(
			hasActiveWorld: true, coverHeld: true, readyForWarmupAndSettle: true, settleFrames: 3, requiredSettleFrames: Refii.Fx.Engine_FxDepthCaptureEffect.RequiredSettleFrames)).IsTrue();
		AssertThat(Refii.Game.GameWorld.ShouldRunRenderEffects(
			hasActiveWorld: true, coverHeld: true, readyForWarmupAndSettle: true, settleFrames: 4, requiredSettleFrames: Refii.Lighting.Engine_PostFxEffect.RequiredSettleFrames)).IsFalse();
		AssertThat(Refii.Game.GameWorld.ShouldRunRenderEffects(
			hasActiveWorld: true, coverHeld: true, readyForWarmupAndSettle: true, settleFrames: 5, requiredSettleFrames: Refii.Lighting.Engine_PostFxEffect.RequiredSettleFrames)).IsTrue();

		// Live gameplay (cover dropped) -> always active when world is mounted.
		AssertThat(Refii.Game.GameWorld.ShouldEnableSceneRendering(
			hasActiveWorld: true, coverHeld: false, readyForWarmupAndSettle: false)).IsTrue();
		AssertThat(Refii.Game.GameWorld.ShouldEnableWorldQualityAndEffects(
			hasActiveWorld: true, coverHeld: false, readyForWarmupAndSettle: false)).IsTrue();
		AssertThat(Refii.Game.GameWorld.ShouldRunRenderEffects(
			hasActiveWorld: true, coverHeld: false, readyForWarmupAndSettle: false, settleFrames: 0, requiredSettleFrames: Refii.Lighting.Engine_PostFxEffect.RequiredSettleFrames)).IsTrue();
	}

	/// <summary>
	/// Verifies `--render-thread separate` detection, D3D12 synchronous RD verification gating,
	/// in-place storage layout parity checks, and tighter D3D12 per-frame staging upload caps.
	/// </summary>
	[TestCase]
	public void D3D12SeparateRenderThreadAvoidsSynchronousRdChecksAndCapsStaging()
	{
		AssertThat(Engine_TextureUpload.HasSeparateRenderThreadArgs(new[] { "--rendering-driver", "d3d12", "--render-thread", "separate" })).IsTrue();
		AssertThat(Engine_TextureUpload.HasSeparateRenderThreadArgs(new[] { "--rendering-driver", "d3d12" })).IsFalse();

		// Godot strips `--render-thread separate` before `OS.GetCmdlineArgs()`; raw process argv or
		// launcher env marker must still resolve separate-render-thread mode accurately.
		AssertThat(Engine_TextureUpload.ResolveIsSeparateRenderThread(
			envMarker: null,
			processArgs: new[] { "refii.exe", "--render-thread", "separate", "--rendering-driver", "d3d12" },
			godotCmdlineArgs: System.Array.Empty<string>(),
			projectThreadModel: 1)).IsTrue();
		AssertThat(Engine_TextureUpload.ResolveIsSeparateRenderThread(
			envMarker: "1",
			processArgs: System.Array.Empty<string>(),
			godotCmdlineArgs: System.Array.Empty<string>(),
			projectThreadModel: 1)).IsTrue();
		AssertThat(Engine_TextureUpload.ResolveIsSeparateRenderThread(
			envMarker: null,
			processArgs: new[] { "refii.exe", "--rendering-driver", "d3d12" },
			godotCmdlineArgs: System.Array.Empty<string>(),
			projectThreadModel: 1)).IsFalse();

		// In-place `texture.Update` skips synchronous RD backing verification even on single-thread mode.
		AssertThat(Engine_TextureUpload.ShouldVerifyRdBackingOnSetImage(updatedInPlace: true, verifyOnCallerThread: true)).IsFalse();
		AssertThat(Engine_TextureUpload.ShouldVerifyRdBackingOnSetImage(updatedInPlace: false, verifyOnCallerThread: true)).IsTrue();
		AssertThat(Engine_TextureUpload.ShouldVerifyRdBackingOnSetImage(updatedInPlace: false, verifyOnCallerThread: false)).IsFalse();

		// D3D12 verifies RD backing synchronously only on single-threaded mode or already on the render thread.
		AssertThat(Engine_TextureUpload.ShouldVerifyRdBackingSynchronously(isD3D12: true, isSeparateRenderThread: false, isOnRenderThread: false)).IsTrue();
		AssertThat(Engine_TextureUpload.ShouldVerifyRdBackingSynchronously(isD3D12: true, isSeparateRenderThread: true, isOnRenderThread: false)).IsFalse();
		AssertThat(Engine_TextureUpload.ShouldVerifyRdBackingSynchronously(isD3D12: true, isSeparateRenderThread: true, isOnRenderThread: true)).IsTrue();
		AssertThat(Engine_TextureUpload.ShouldVerifyRdBackingSynchronously(isD3D12: false, isSeparateRenderThread: false, isOnRenderThread: true)).IsFalse();

		// Storage layout parity for RS::texture_2d_update vs RS::texture_replace.
		AssertThat(Engine_TextureUpload.MatchesStorageLayout(
			64, 64, Godot.Image.Format.Rgba8, true,
			64, 64, Godot.Image.Format.Rgba8, true)).IsTrue();
		AssertThat(Engine_TextureUpload.MatchesStorageLayout(
			64, 64, Godot.Image.Format.Rgba8, true,
			64, 64, Godot.Image.Format.Rgba8, false)).IsFalse();
		AssertThat(Engine_TextureUpload.MatchesStorageLayout(
			64, 64, Godot.Image.Format.Rgba8, true,
			128, 64, Godot.Image.Format.Rgba8, true)).IsFalse();

		// D3D12 enforces tighter staging caps than Vulkan during load cover and play.
		AssertThat(Engine_ServerSyncs.EffectiveMaxUploadBytesPerFrame(loadCoverActive: true, isD3D12: true))
			.IsLess(Engine_ServerSyncs.EffectiveMaxUploadBytesPerFrame(loadCoverActive: true, isD3D12: false));
		AssertThat(Engine_ServerSyncs.EffectiveMaxUploadBytesPerFrame(loadCoverActive: false, isD3D12: true))
			.IsLess(Engine_ServerSyncs.EffectiveMaxUploadBytesPerFrame(loadCoverActive: false, isD3D12: false));
	}

	/// <summary>
	/// Verifies device-lost latching stays idempotent and requests render-loop termination outside test hosts.
	/// </summary>
	[TestCase]
	public void RdHealthHaltsRenderLoopOutsideTestHostAndTracksDeviceLoss()
	{
		Engine_RdHealth.Reset();
		try
		{
			AssertThat(Engine_RdHealth.ShouldHaltRenderLoopOnDeviceLost(isTestHost: true)).IsFalse();
			AssertThat(Engine_RdHealth.ShouldHaltRenderLoopOnDeviceLost(isTestHost: false)).IsTrue();

			AssertThat(Engine_RdHealth.TripDeviceLost("TestContext", "0x887a0005")).IsTrue();
			AssertThat(Engine_RdHealth.IsDeviceLost).IsTrue();
			AssertThat(Engine_RdHealth.TripDeviceLost("TestContext", "second")).IsFalse();
			AssertThat(Engine_RdHealth.SuppressedCount).IsEqual(1);
		}
		finally
		{
			Engine_RdHealth.Reset();
		}
	}

	private static string StripShaderComments(string source)
	{
		string withoutBlock = System.Text.RegularExpressions.Regex.Replace(
			source, @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
		return string.Join("\n", System.Linq.Enumerable.Select(
			withoutBlock.Split('\n'),
			static line =>
			{
				int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
				return commentIdx >= 0 ? line[..commentIdx] : line;
			}));
	}

	private static string ExtractFragmentBody(string strippedSource)
	{
		int fragIdx = strippedSource.IndexOf("void fragment()", StringComparison.Ordinal);
		AssertThat(fragIdx).IsGreaterEqual(0);
		int lightIdx = strippedSource.IndexOf("void light()", fragIdx, StringComparison.Ordinal);
		return lightIdx > fragIdx ? strippedSource[fragIdx..lightIdx] : strippedSource[fragIdx..];
	}

	/// <summary>
	/// Prevents regressions of D3D12 / Intel Arc DXIL GPU hangs caused by evaluating implicit
	/// derivatives (<c>texture()</c>, <c>dFdx()</c>, <c>dFdy()</c>, <c>fwidth()</c>, or
	/// <c>fable_normal_side()</c>) after a per-pixel <c>discard;</c> in prop/material shaders.
	/// </summary>
	[TestCase]
	public void PropAndMaterialShadersPrecomputeDerivativesBeforeDiscard()
	{
		string godotRoot = FindRepoRoot();
		string matDir = Path.Combine(godotRoot, "src", "Engine", "material");

		foreach (string fileName in new[] { "standard_material_body.gdshaderinc", "standard_material_phong.gdshader" })
		{
			string code = StripShaderComments(File.ReadAllText(Path.Combine(matDir, fileName)));
			string frag = ExtractFragmentBody(code);

			int firstDiscard = frag.IndexOf("discard;", StringComparison.Ordinal);
			AssertThat(firstDiscard).IsGreater(0);

			int dxIdx = frag.IndexOf("dFdx(UV)", StringComparison.Ordinal);
			int dyIdx = frag.IndexOf("dFdy(UV)", StringComparison.Ordinal);
			int crossIdx = frag.IndexOf("cross(dFdx(VERTEX), dFdy(VERTEX))", StringComparison.Ordinal);
			AssertThat(dxIdx).IsGreaterEqual(0);
			AssertThat(dyIdx).IsGreaterEqual(0);
			AssertThat(crossIdx).IsGreaterEqual(0);
			AssertThat(dxIdx).IsLess(firstDiscard);
			AssertThat(dyIdx).IsLess(firstDiscard);
			AssertThat(crossIdx).IsLess(firstDiscard);

			AssertThat(frag.Contains("fable_normal_side_precomputed(", StringComparison.Ordinal)).IsTrue();
			AssertThat(System.Text.RegularExpressions.Regex.IsMatch(frag, @"\bfable_normal_side\(")).IsFalse();
			AssertThat(frag.Contains("textureGrad(albedo_tex,", StringComparison.Ordinal)).IsTrue();
			AssertThat(frag.Contains("textureGrad(normal_tex,", StringComparison.Ordinal)).IsTrue();
			AssertThat(frag.Contains("textureGrad(specular_tex,", StringComparison.Ordinal)).IsTrue();
			AssertThat(frag.Contains("textureGrad(emission_tex,", StringComparison.Ordinal)).IsTrue();

			string postDiscard = frag[firstDiscard..];
			AssertThat(System.Text.RegularExpressions.Regex.IsMatch(postDiscard, @"\btexture\(")).IsFalse();
			AssertThat(System.Text.RegularExpressions.Regex.IsMatch(postDiscard, @"\bdFd[xy](Fine)?\(")).IsFalse();
			AssertThat(System.Text.RegularExpressions.Regex.IsMatch(postDiscard, @"\bfwidth\(")).IsFalse();
		}

		string bodyCode = StripShaderComments(File.ReadAllText(Path.Combine(matDir, "standard_material_body.gdshaderinc")));
		AssertThat(bodyCode.Contains("textureGrad(orm_tex,", StringComparison.Ordinal)).IsTrue();
		AssertThat(bodyCode.Contains("textureLod(envmap_tex,", StringComparison.Ordinal)).IsTrue();

		string lightingCode = StripShaderComments(File.ReadAllText(Path.Combine(matDir, "fable_lighting.gdshaderinc")));
		AssertThat(lightingCode.Contains("textureLod(fable_prop_lightmaps,", StringComparison.Ordinal)).IsTrue();
		AssertThat(lightingCode.Contains("fable_normal_side_precomputed(", StringComparison.Ordinal)).IsTrue();
		int bakeIdx = lightingCode.IndexOf("fable_bake_amb3(", StringComparison.Ordinal);
		int bakeEnd = lightingCode.IndexOf('}', bakeIdx);
		AssertThat(System.Text.RegularExpressions.Regex.IsMatch(lightingCode[bakeIdx..bakeEnd], @"\btexture\(")).IsFalse();
	}

	/// <summary>
	/// Ensures terrain splatting never runs implicit-LOD <c>texture()</c> inside per-pixel divergent
	/// material loops and uses explicit <c>textureGrad</c>/<c>textureLod</c> throughout.
	/// </summary>
	[TestCase]
	public void TerrainSplatUsesExplicitDerivativesAcrossSplatLoop()
	{
		string godotRoot = FindRepoRoot();
		string splatPath = Path.Combine(godotRoot, "src", "Engine", "level", "terrain", "terrain_splat_body.gdshaderinc");
		string code = StripShaderComments(File.ReadAllText(splatPath));

		int sampleIdx = code.IndexOf("void sample_layer(", StringComparison.Ordinal);
		int fragIdx = code.IndexOf("void fragment()", StringComparison.Ordinal);
		AssertThat(sampleIdx).IsGreater(0);
		AssertThat(fragIdx).IsGreater(sampleIdx);

		string sampleBody = code[sampleIdx..fragIdx];
		string fragBody = code[fragIdx..];

		AssertThat(System.Text.RegularExpressions.Regex.Matches(sampleBody, @"\btexture\(").Count).IsEqual(0);
		AssertThat(System.Text.RegularExpressions.Regex.Matches(fragBody, @"\btexture\(").Count).IsEqual(0);
		AssertThat(System.Text.RegularExpressions.Regex.Matches(sampleBody, @"\btextureGrad\(").Count).IsGreaterEqual(7);
		AssertThat(fragBody.Contains("textureGrad(page_albedo,", StringComparison.Ordinal)).IsTrue();
		AssertThat(fragBody.Contains("textureLod(material_weight,", StringComparison.Ordinal)).IsTrue();
		AssertThat(fragBody.Contains("fable_normal_side_precomputed(", StringComparison.Ordinal)).IsTrue();
	}

	/// <summary>
	/// Verifies water, environment, FX, medallion, and mobile composite shaders never issue
	/// implicit-derivative calls after <c>discard;</c> or inside divergent branches.
	/// </summary>
	[TestCase]
	public void WaterEnvironmentAndFxShadersObeyD3D12ControlFlowRules()
	{
		string godotRoot = FindRepoRoot();

		string[] zeroDiscardBlendedAndSolidFiles =
		{
			Path.Combine("src", "Engine", "entities", "shadow_caster_solid.gdshader"),
			Path.Combine("src", "Engine", "fx", "fx_particle.gdshader"),
			Path.Combine("src", "Engine", "fx", "fx_particle_add.gdshader"),
			Path.Combine("src", "Engine", "fx", "fx_particle_addone.gdshader"),
			Path.Combine("src", "Engine", "fx", "fx_particle_darken.gdshader"),
			Path.Combine("src", "Engine", "fx", "fx_particle_disp.gdshader"),
			Path.Combine("src", "Engine", "fx", "fx_particle_mixalpha.gdshader"),
			Path.Combine("src", "Engine", "fx", "fx_particle_sub.gdshader"),
			Path.Combine("src", "Engine", "fx", "fx_mesh_disp.gdshader"),
			Path.Combine("src", "Engine", "fx", "fx_displacement_apply.gdshader"),
			Path.Combine("src", "Engine", "level", "environment", "clouds.gdshader"),
			Path.Combine("src", "Engine", "level", "environment", "weather.gdshader"),
			Path.Combine("src", "Engine", "level", "environment", "dynamic", "dynamic_surface_impact.gdshader"),
			Path.Combine("src", "Engine", "level", "environment", "dynamic", "dynamic_snow_tracks.gdshader"),
			Path.Combine("src", "Engine", "ui", "ui_quad.gdshader"),
			Path.Combine("src", "Engine", "ui", "ui_text.gdshader"),
			Path.Combine("src", "Engine", "ui", "ui_world_marker.gdshader"),
			Path.Combine("src", "Engine", "material", "preparation_medallion.gdshader"),
			Path.Combine("src", "android", "mobile_highlight_composite.gdshader"),
		};

		foreach (string relPath in zeroDiscardBlendedAndSolidFiles)
		{
			string code = StripShaderComments(File.ReadAllText(Path.Combine(godotRoot, relPath)));
			string frag = ExtractFragmentBody(code);
			AssertThat(System.Text.RegularExpressions.Regex.IsMatch(frag, @"\bdiscard\s*;"))
				.OverrideFailureMessage($"{relPath} should emit hardware blend identity instead of discard;")
				.IsFalse();
		}

		string waterCode = StripShaderComments(File.ReadAllText(
			Path.Combine(godotRoot, "src", "Engine", "level", "water", "water.gdshader")));
		AssertThat(waterCode.Contains("local_xz_dx", StringComparison.Ordinal)).IsTrue();
		AssertThat(waterCode.Contains("textureGrad(normal_map,", StringComparison.Ordinal)).IsTrue();
		AssertThat(waterCode.Contains("/ ((inv_proj[2].w * d) + inv_proj[3].w)", StringComparison.Ordinal)).IsFalse();
		foreach (string sampler in new[] { "depth_tex", "reflection_tex", "reflection_tex_second", "screen_tex", "water_mask", "terrain_height_tex" })
		{
			AssertThat(System.Text.RegularExpressions.Regex.IsMatch(waterCode, $@"\btexture\({sampler}\b")).IsFalse();
		}

		foreach (string relPath in new[]
		         {
			         Path.Combine("src", "Engine", "entities", "entity_highlight_silhouette.gdshader"),
			         Path.Combine("src", "android", "mobile_highlight_composite.gdshader"),
		         })
		{
			string code = StripShaderComments(File.ReadAllText(Path.Combine(godotRoot, relPath)));
			AssertThat(code.Contains("/ view.w", StringComparison.Ordinal))
				.OverrideFailureMessage($"{relPath} divides by unguarded view.w")
				.IsFalse();
			AssertThat(code.Contains("abs(view.w) > 1e-6", StringComparison.Ordinal)).IsTrue();
		}

		(string RelPath, string Sampler)[] lodOnlySamplers =
		{
			(Path.Combine("src", "Engine", "level", "environment", "sky_dome.gdshaderinc"), "in_scatter_lut"),
			(Path.Combine("src", "Engine", "level", "environment", "fable_atmosphere.gdshaderinc"), "atmos_inscatter"),
			(Path.Combine("src", "Engine", "level", "environment", "dynamic", "dynamic_surface_weather.gdshaderinc"), "dynamic_track_map"),
			(Path.Combine("src", "Engine", "level", "environment", "dynamic", "dynamic_cloud_receiver.gdshaderinc"), "dynamic_cloud_mask"),
			(Path.Combine("src", "Engine", "fx", "fx_soft.gdshaderinc"), "fx_depth_tex"),
			(Path.Combine("src", "Engine", "core", "views.gdshaderinc"), "fx_scene_depth_second"),
			(Path.Combine("src", "Engine", "core", "views.gdshaderinc"), "fx_scene_depth"),
		};
		foreach ((string relPath, string sampler) in lodOnlySamplers)
		{
			string code = StripShaderComments(File.ReadAllText(Path.Combine(godotRoot, relPath)));
			AssertThat(System.Text.RegularExpressions.Regex.IsMatch(code, $@"\btexture\({sampler}\b"))
				.OverrideFailureMessage($"{relPath} calls implicit texture({sampler}) instead of textureLod.")
				.IsFalse();
			AssertThat(code.Contains($"textureLod({sampler},", StringComparison.Ordinal))
				.OverrideFailureMessage($"{relPath} missing textureLod({sampler}, ...).")
				.IsTrue();
		}

		foreach (string relPath in new[]
		         {
			         Path.Combine("src", "Engine", "level", "environment", "dynamic", "dynamic_sky.gdshader"),
			         Path.Combine("src", "Engine", "level", "environment", "dynamic", "dynamic_cloud_layers.gdshaderinc"),
			         Path.Combine("src", "Engine", "level", "environment", "dynamic", "dynamic_cloud_density.gdshaderinc"),
		         })
		{
			string code = StripShaderComments(File.ReadAllText(Path.Combine(godotRoot, relPath)));
			AssertThat(System.Text.RegularExpressions.Regex.IsMatch(code, @"\btexture\("))
				.OverrideFailureMessage($"{relPath} calls implicit texture() instead of textureLod.")
				.IsFalse();
		}

		string guestFx = StripShaderComments(File.ReadAllText(
			Path.Combine(godotRoot, "src", "Engine", "fx", "fx_guest_particle_pixel.gdshaderinc")));
		AssertThat(guestFx.Contains("textureGrad(", StringComparison.Ordinal)).IsTrue();
		AssertThat(System.Text.RegularExpressions.Regex.IsMatch(guestFx, @"\btexture\(")).IsFalse();
	}

	/// <summary>
	/// Ensures every local (non-global) <c>sampler2D</c> and <c>sampler2DArray</c> uniform across
	/// <c>Godot/src</c> declares an explicit default or built-in hint so unbound D3D12 slots never bind null SRVs.
	/// </summary>
	[TestCase]
	public void AllLocal2DSamplersDeclareExplicitDefaultOrBuiltinHints()
	{
		string srcRoot = Path.Combine(FindRepoRoot(), "src");
		string[] files = Directory.GetFiles(srcRoot, "*.gdshader*", SearchOption.AllDirectories);
		var offending = new List<string>();
		var localSamplerRegex = new System.Text.RegularExpressions.Regex(
			@"^\s*uniform\s+sampler2D(?:Array)?\b",
			System.Text.RegularExpressions.RegexOptions.Compiled);
		var validHintRegex = new System.Text.RegularExpressions.Regex(
			@"hint_default_|hint_normal|hint_screen_texture|hint_depth_texture|hint_roughness",
			System.Text.RegularExpressions.RegexOptions.Compiled);

		foreach (string file in files)
		{
			if (!file.EndsWith(".gdshader", StringComparison.OrdinalIgnoreCase) &&
			    !file.EndsWith(".gdshaderinc", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			string[] lines = File.ReadAllLines(file);
			for (int i = 0; i < lines.Length; i++)
			{
				if (localSamplerRegex.IsMatch(lines[i]) && !validHintRegex.IsMatch(lines[i]))
				{
					offending.Add($"{Path.GetRelativePath(srcRoot, file)}:{i + 1}: {lines[i].Trim()}");
				}
			}
		}

		AssertThat(offending)
			.OverrideFailureMessage(offending.Count > 0 ? string.Join("\n", offending) : "none")
			.IsEmpty();
	}

	/// <summary>
	/// Verifies global depth/exposure effects register valid 1x1 <c>ImageTexture</c> fallbacks
	/// rather than uninitialized <c>Texture2Drd</c> instances, and guards deferred RID publication.
	/// </summary>
	[TestCase]
	public void GlobalEffectResourcesUseValidImageTextureFallbacksUntilRdTargetIsReady()
	{
		static string ExtractMethodBody(string source, string signature)
		{
			int start = source.IndexOf(signature, StringComparison.Ordinal);
			int open = source.IndexOf('{', start);
			int depth = 0;
			for (int i = open; i < source.Length; i++)
			{
				if (source[i] == '{') depth++;
				else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
			}
			return source[open..];
		}

		string godotRoot = FindRepoRoot();

		string depthCapture = File.ReadAllText(Path.Combine(godotRoot, "src", "Engine", "fx", "Engine_FxDepthCaptureEffect.cs"));
		string depthRegBody = ExtractMethodBody(depthCapture, "public static void RegisterGlobals()");
		AssertThat(depthRegBody.Contains("GetOrCreateFallbackDepthTexture()", StringComparison.Ordinal)).IsTrue();
		AssertThat(depthRegBody.Contains("new Texture2Drd()", StringComparison.Ordinal)).IsFalse();
		AssertThat(depthCapture.Contains("!rid.IsValid", StringComparison.Ordinal)).IsTrue();

		string postFx = File.ReadAllText(Path.Combine(godotRoot, "src", "Engine", "Lighting", "Engine_PostFxEffect.cs"));
		string postRegBody = ExtractMethodBody(postFx, "public static void RegisterGlobals()");
		AssertThat(postRegBody.Contains("GetOrCreateFallbackExposureTexture()", StringComparison.Ordinal)).IsTrue();
		AssertThat(postRegBody.Contains("new Texture2Drd()", StringComparison.Ordinal)).IsFalse();
		AssertThat(postFx.Contains("!exposureRid.IsValid", StringComparison.Ordinal)).IsTrue();
	}

	/// <summary>
	/// Verifies <c>GameWorld.ParseDx12Skip3d</c> token parsing for subsystem bisection.
	/// </summary>
	[TestCase]
	public void Dx12Skip3dParserHandlesIndividualCombinedAndAllTokens()
	{
		AssertThat(Refii.Game.GameWorld.ParseDx12Skip3d(null)).IsEqual(Refii.Game.GameWorld.Dx12Skip3dMask.None);
		AssertThat(Refii.Game.GameWorld.ParseDx12Skip3d("   ")).IsEqual(Refii.Game.GameWorld.Dx12Skip3dMask.None);
		AssertThat(Refii.Game.GameWorld.ParseDx12Skip3d("unknown_flag")).IsEqual(Refii.Game.GameWorld.Dx12Skip3dMask.None);

		AssertThat(Refii.Game.GameWorld.ParseDx12Skip3d("terrain")).IsEqual(Refii.Game.GameWorld.Dx12Skip3dMask.Terrain);
		AssertThat(Refii.Game.GameWorld.ParseDx12Skip3d("PROPS")).IsEqual(Refii.Game.GameWorld.Dx12Skip3dMask.Props);
		AssertThat(Refii.Game.GameWorld.ParseDx12Skip3d("entities")).IsEqual(Refii.Game.GameWorld.Dx12Skip3dMask.Entities);
		AssertThat(Refii.Game.GameWorld.ParseDx12Skip3d("fx")).IsEqual(Refii.Game.GameWorld.Dx12Skip3dMask.Fx);
		AssertThat(Refii.Game.GameWorld.ParseDx12Skip3d("environment")).IsEqual(Refii.Game.GameWorld.Dx12Skip3dMask.Environment);
		AssertThat(Refii.Game.GameWorld.ParseDx12Skip3d("water")).IsEqual(Refii.Game.GameWorld.Dx12Skip3dMask.Water);
		AssertThat(Refii.Game.GameWorld.ParseDx12Skip3d("reflections")).IsEqual(Refii.Game.GameWorld.Dx12Skip3dMask.Reflections);

		AssertThat(Refii.Game.GameWorld.ParseDx12Skip3d("terrain, water ; fx | sky reflection"))
			.IsEqual(
				Refii.Game.GameWorld.Dx12Skip3dMask.Terrain |
				Refii.Game.GameWorld.Dx12Skip3dMask.Water |
				Refii.Game.GameWorld.Dx12Skip3dMask.Fx |
				Refii.Game.GameWorld.Dx12Skip3dMask.Environment |
				Refii.Game.GameWorld.Dx12Skip3dMask.Reflections);

		AssertThat(Refii.Game.GameWorld.ParseDx12Skip3d("all")).IsEqual(Refii.Game.GameWorld.Dx12Skip3dMask.All);
	}

	/// <summary>
	/// Enforces D3D12/DXIL shader invariants: every fragment shader has at most one tail <c>discard;</c>
	/// per compile-time branch with zero instructions, texture taps, or derivatives after <c>discard;</c>,
	/// and terrain screen-space cliff derivatives execute unconditionally outside diagnostic branches.
	/// </summary>
	[TestCase]
	public void ShadersEnforceSingleTailDiscardAndUniformDerivatives()
	{
		static string StripComments(string source)
		{
			string noBlock = System.Text.RegularExpressions.Regex.Replace(
				source,
				@"/\*.*?\*/",
				" ",
				System.Text.RegularExpressions.RegexOptions.Singleline);
			return System.Text.RegularExpressions.Regex.Replace(noBlock, @"//[^\r\n]*", string.Empty);
		}

		string godotRoot = FindRepoRoot();
		string srcRoot = Path.Combine(godotRoot, "src");

		var files = new List<string>(Directory.GetFiles(srcRoot, "*.gdshader", SearchOption.AllDirectories));
		files.AddRange(Directory.GetFiles(srcRoot, "*.gdshaderinc", SearchOption.AllDirectories));

		foreach (string filePath in files)
		{
			string normalized = filePath.Replace('\\', '/');
			string stripped = StripComments(File.ReadAllText(filePath));
			var matches = System.Text.RegularExpressions.Regex.Matches(stripped, @"\bdiscard\s*;");
			int allowedDiscards = 1;
			AssertThat(matches.Count)
				.OverrideFailureMessage($"Expected <= {allowedDiscards} discard; in {normalized}, found {matches.Count}")
				.IsLessEqual(allowedDiscards);

			if (matches.Count == 0)
			{
				continue;
			}

			int fragIdx = stripped.IndexOf("void fragment()", StringComparison.Ordinal);
			string fragmentScope = fragIdx >= 0 ? stripped[fragIdx..] : stripped;
			var nextStageMatch = System.Text.RegularExpressions.Regex.Match(
				fragmentScope,
				@"\bvoid\s+(?:light|vertex)\s*\(");
			if (nextStageMatch.Success)
			{
				fragmentScope = fragmentScope[..nextStageMatch.Index];
			}

			AssertTailOnlyAfterDiscard(normalized, fragmentScope);
		}

		// Lit depth_draw_never blended spatial shaders must not emit mid-PSMain discard; instructions.
		string guestPixel = StripComments(File.ReadAllText(
			Path.Combine(srcRoot, "Engine", "fx", "fx_guest_particle_pixel.gdshaderinc")));
		AssertThat(System.Text.RegularExpressions.Regex.Matches(guestPixel, @"\bdiscard\s*;").Count).IsEqual(0);
		AssertThat(guestPixel.Contains("any(greaterThan(ALBEDO, vec3(0.0)))", StringComparison.Ordinal)).IsTrue();

		string stdBodyFile = StripComments(File.ReadAllText(
			Path.Combine(srcRoot, "Engine", "material", "standard_material_body.gdshaderinc")));
		AssertThat(stdBodyFile.Contains("!defined(FABLE_SOLID) && !defined(FABLE_TRANSLUCENT)", StringComparison.Ordinal)).IsTrue();
		AssertThat(stdBodyFile.Contains("has_surface_response", StringComparison.Ordinal)).IsTrue();

		string furShell = StripComments(File.ReadAllText(
			Path.Combine(srcRoot, "Engine", "material", "fur_shell.gdshaderinc")));
		AssertThat(System.Text.RegularExpressions.Regex.Matches(furShell, @"\bdiscard\s*;").Count).IsEqual(1);
		AssertThat(furShell.Contains("any(greaterThan(ALBEDO, vec3(0.0)))", StringComparison.Ordinal)).IsTrue();

		string weatherCode = StripComments(File.ReadAllText(
			Path.Combine(srcRoot, "Engine", "level", "environment", "weather.gdshader")));
		AssertThat(weatherCode.Contains("if (!should_discard)", StringComparison.Ordinal)).IsTrue();

		string impactCode = StripComments(File.ReadAllText(
			Path.Combine(srcRoot, "Engine", "level", "environment", "dynamic", "dynamic_surface_impact.gdshader")));
		AssertThat(impactCode.Contains("if (!should_discard)", StringComparison.Ordinal)).IsTrue();

		string classicParticles = File.ReadAllText(
			Path.Combine(srcRoot, "Engine", "material", "Engine_ClassicParticles.cs"));
		int dispStart = classicParticles.IndexOf("private const string DisplacementDefines =", StringComparison.Ordinal);
		AssertThat(dispStart).IsGreaterEqual(0);
		int dispEnd = classicParticles.IndexOf("}\";", dispStart, StringComparison.Ordinal);
		AssertThat(dispEnd).IsGreater(dispStart);
		string dispBlock = classicParticles[dispStart..dispEnd];
		AssertThat(dispBlock.Contains("textureLod(", StringComparison.Ordinal)).IsTrue();
		AssertThat(System.Text.RegularExpressions.Regex.IsMatch(dispBlock, @"\btexture\(")).IsFalse();

		string terrainSplat = StripComments(File.ReadAllText(Path.Combine(srcRoot, "Engine", "level", "terrain", "terrain_splat_body.gdshaderinc")));
		int diagIdx = terrainSplat.IndexOf("fable_shader_cost_diagnostic", StringComparison.Ordinal);
		int dfdxFineIdx = terrainSplat.LastIndexOf("dFdxFine(", StringComparison.Ordinal);
		int dfdyFineIdx = terrainSplat.LastIndexOf("dFdyFine(", StringComparison.Ordinal);
		AssertThat(diagIdx).IsGreater(0);
		AssertThat(dfdxFineIdx).IsGreater(0);
		AssertThat(dfdyFineIdx).IsGreater(0);
		AssertThat(dfdxFineIdx).IsLess(diagIdx);
		AssertThat(dfdyFineIdx).IsLess(diagIdx);

		// Guard against Intel Arc OpVectorExtractDynamic, indexed Bayer tables, local indexed roof arrays,
		// and unguarded projective / w divides in fragment/precip shaders.
		string dynamicPrecip = StripComments(File.ReadAllText(Path.Combine(srcRoot, "Engine", "level", "environment", "dynamic", "dynamic_precip.gdshaderinc")));
		AssertThat(dynamicPrecip.Contains(")[i % 4]", StringComparison.Ordinal)).IsFalse();
		AssertThat(dynamicPrecip.Contains("/ scene.w", StringComparison.Ordinal)).IsFalse();

		string fableRoofs = StripComments(File.ReadAllText(Path.Combine(srcRoot, "Engine", "level", "environment", "fable_roofs.gdshaderinc")));
		AssertThat(fableRoofs.Contains("vec2 c[4]", StringComparison.Ordinal)).IsFalse();

		string stdBody = StripComments(File.ReadAllText(Path.Combine(srcRoot, "Engine", "material", "standard_material_body.gdshaderinc")));
		AssertThat(stdBody.Contains("bayer[", StringComparison.Ordinal)).IsFalse();

		string shadowCaster = StripComments(File.ReadAllText(Path.Combine(srcRoot, "Engine", "entities", "shadow_caster.gdshader")));
		AssertThat(shadowCaster.Contains("bayer[", StringComparison.Ordinal)).IsFalse();

		string fxSoft = StripComments(File.ReadAllText(Path.Combine(srcRoot, "Engine", "fx", "fx_soft.gdshaderinc")));
		AssertThat(fxSoft.Contains("/= scene_view.w", StringComparison.Ordinal)).IsFalse();

		string fxAddOne = StripComments(File.ReadAllText(Path.Combine(srcRoot, "Engine", "fx", "fx_particle_addone.gdshader")));
		AssertThat(fxAddOne.Contains("/= scene_view.w", StringComparison.Ordinal)).IsFalse();

		static void AssertTailOnlyAfterDiscard(string label, string scope)
		{
			var match = System.Text.RegularExpressions.Regex.Match(scope, @"\bdiscard\s*;");
			if (!match.Success)
			{
				return;
			}

			string tail = scope[(match.Index + match.Length)..];
			// Strip preprocessor directives and closing braces/whitespace; nothing executable may remain.
			string tailNoPreproc = System.Text.RegularExpressions.Regex.Replace(tail, @"^\s*#[^\r\n]*", string.Empty, System.Text.RegularExpressions.RegexOptions.Multiline);
			string tailRemainder = tailNoPreproc.Replace("}", string.Empty, StringComparison.Ordinal).Trim();
			AssertThat(tailRemainder)
				.OverrideFailureMessage($"Executable code found after discard; in {label}: {tailRemainder}")
				.IsEqual(string.Empty);
		}
	}

	/// <summary>
	/// Prevents reintroducing the <c>--render-thread separate</c> / D3D12 image-mutation race where
	/// CPU <c>Image.SetData(...)</c> mutates an image while the render thread reads it for <c>ImageTexture.Update(...)</c>.
	/// </summary>
	[TestCase]
	public void DynamicImageTexturesNeverMutateInPlaceWithSetDataBeforeUpdate()
	{
		string srcRoot = Path.Combine(FindRepoRoot(), "src");
		foreach (string csPath in Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
		{
			string raw = File.ReadAllText(csPath);
			string noBlock = System.Text.RegularExpressions.Regex.Replace(
				raw,
				@"/\*.*?\*/",
				" ",
				System.Text.RegularExpressions.RegexOptions.Singleline);
			string codeOnly = System.Text.RegularExpressions.Regex.Replace(noBlock, @"//[^\r\n]*", string.Empty);
			bool hasSetData = codeOnly.Contains(".SetData(", StringComparison.Ordinal);
			bool hasUpdate = codeOnly.Contains(".Update(", StringComparison.Ordinal);
			AssertThat(hasSetData && hasUpdate)
				.OverrideFailureMessage($"In-place Image.SetData + ImageTexture.Update race candidate in {csPath}")
				.IsFalse();
		}
	}

	/// <summary>
	/// Verifies <c>Engine_RdRetire.FlushAll(null)</c> safely clears retired RIDs on test hosts without a device.
	/// </summary>
	[TestCase]
	public void RdRetireFlushAllClearsPendingWithoutDevice()
	{
		Engine_RdRetire.FlushAll(null);
		AssertThat(Engine_RdRetire.PendingCount).IsEqual(0);
		Engine_RdRetire.Retire(default);
		Engine_RdRetire.FlushAll(null);
		AssertThat(Engine_RdRetire.PendingCount).IsEqual(0);
	}

	/// <summary>
	/// Verifies <see cref="Refii.Models.Engine_ShaderWarmup.ShouldWarmShader"/> skips
	/// dynamic sky/weather shaders whenever dynamic weather is disabled, while preserving core shaders.
	/// </summary>
	[TestCase]
	public void ShaderWarmupSkipsDynamicWeatherShadersWhenDisabled()
	{
		string[] dynamicShaders =
		[
			"res://src/Engine/level/environment/dynamic/dynamic_sky.gdshader",
			"res://src/Engine/level/environment/dynamic/dynamic_rain.gdshader",
			"res://src/Engine/level/environment/dynamic/dynamic_snow.gdshader",
			"res://src/Engine/level/environment/dynamic/dynamic_snow_tracks.gdshader",
			"res://src/Engine/level/environment/dynamic/dynamic_cloud_shadow_density.gdshader",
			"res://src/Engine/level/environment/dynamic/dynamic_surface_impact.gdshader",
		];

		foreach (string path in dynamicShaders)
		{
			AssertThat(Refii.Models.Engine_ShaderWarmup.IsDynamicWeatherShader(path)).IsTrue();
			AssertThat(Refii.Models.Engine_ShaderWarmup.ShouldWarmShader(path, dynamicWeatherEnabled: false)).IsFalse();
			AssertThat(Refii.Models.Engine_ShaderWarmup.ShouldWarmShader(path, dynamicWeatherEnabled: true)).IsTrue();
		}

		string standard = "res://src/Engine/material/standard_material.gdshader";
		AssertThat(Refii.Models.Engine_ShaderWarmup.IsDynamicWeatherShader(standard)).IsFalse();
		AssertThat(Refii.Models.Engine_ShaderWarmup.ShouldWarmShader(standard, dynamicWeatherEnabled: false)).IsTrue();
	}

	/// <summary>
	/// Verifies <see cref="GameWorld.ParseDx12Skip3d"/> recognizes granular D3D12 isolation tokens
	/// (<c>ssao</c>, <c>sunshadows</c>, <c>lights</c>, <c>depthprepass</c>, <c>hud3d</c>, <c>sky</c>,
	/// <c>clouds</c>, <c>reflections</c>, <c>all</c>).
	/// </summary>
	[TestCase]
	public void ParseDx12Skip3dHandlesGranularPassIsolationTokens()
	{
		AssertThat(GameWorld.ParseDx12Skip3d(null)).IsEqual(GameWorld.Dx12Skip3dMask.None);
		AssertThat(GameWorld.ParseDx12Skip3d("  ")).IsEqual(GameWorld.Dx12Skip3dMask.None);

		GameWorld.Dx12Skip3dMask stepA = GameWorld.ParseDx12Skip3d("ssao,sunshadows,depthprepass,reflections,lights,hud3d");
		AssertThat((stepA & GameWorld.Dx12Skip3dMask.Ssao) != 0).IsTrue();
		AssertThat((stepA & GameWorld.Dx12Skip3dMask.SunShadows) != 0).IsTrue();
		AssertThat((stepA & GameWorld.Dx12Skip3dMask.DepthPrepass) != 0).IsTrue();
		AssertThat((stepA & GameWorld.Dx12Skip3dMask.Reflections) != 0).IsTrue();
		AssertThat((stepA & GameWorld.Dx12Skip3dMask.Lights) != 0).IsTrue();
		AssertThat((stepA & GameWorld.Dx12Skip3dMask.Hud3d) != 0).IsTrue();
		AssertThat((stepA & GameWorld.Dx12Skip3dMask.Terrain) != 0).IsFalse();

		GameWorld.Dx12Skip3dMask skyCloudMask = GameWorld.ParseDx12Skip3d("skydome,clouds");
		AssertThat((skyCloudMask & GameWorld.Dx12Skip3dMask.SkyDome) != 0).IsTrue();
		AssertThat((skyCloudMask & GameWorld.Dx12Skip3dMask.Clouds) != 0).IsTrue();
		AssertThat((skyCloudMask & GameWorld.Dx12Skip3dMask.Environment) != 0).IsFalse();

		AssertThat(GameWorld.ParseDx12Skip3d("all")).IsEqual(GameWorld.Dx12Skip3dMask.All);
	}

	/// <summary>
	/// Verifies <see cref="Engine_RdHealth.ShouldAcceptSurfaceIndex"/> and
	/// <see cref="Engine_RdHealth.CheckArrayMeshSurfaceAdded"/> guard negative/out-of-bounds surface indices
	/// and latch device loss cleanly after RD surface creation failures.
	/// </summary>
	[TestCase]
	public void RdHealthGuardsArrayMeshSurfaceBoundsAndTripsOnFailedSurfaceAdd()
	{
		Engine_RdHealth.Reset();
		try
		{
			AssertThat(Engine_RdHealth.ShouldAcceptSurfaceIndex(-1, 0)).IsFalse();
			AssertThat(Engine_RdHealth.ShouldAcceptSurfaceIndex(0, 0)).IsFalse();
			AssertThat(Engine_RdHealth.ShouldAcceptSurfaceIndex(1, 1)).IsFalse();
			AssertThat(Engine_RdHealth.ShouldAcceptSurfaceIndex(0, 1)).IsTrue();

			bool handlerCalled = false;
			Engine_RdHealth.RegisterDeviceLostHandler(() => handlerCalled = true);
			AssertThat(Engine_RdHealth.CheckArrayMeshSurfaceAdded(null, 0, "Test.NullMesh")).IsFalse();
			AssertThat(Engine_RdHealth.IsDeviceLost).IsTrue();
			AssertThat(handlerCalled).IsTrue();
			AssertThat(Engine_RdHealth.DeviceLostReason!.Contains("Test.NullMesh", StringComparison.Ordinal)).IsTrue();
		}
		finally
		{
			Engine_RdHealth.Reset();
		}
	}

	/// <summary>
	/// Verifies <see cref="GameWindowSettings.ResolvePositionalShadowAtlasSize"/> frees unused
	/// 4096x4096 positional shadow atlas VRAM when <c>placed_light_shadows = false</c> and budget is 0.
	/// </summary>
	[TestCase]
	public void PositionalShadowAtlasZeroesWhenPlacedLightShadowsDisabledAndBudgetZero()
	{
		AssertThat(GameWindowSettings.ResolvePositionalShadowAtlasSize(placedLightShadows: false, shadowBudget: 0)).IsEqual(0);
		AssertThat(GameWindowSettings.ResolvePositionalShadowAtlasSize(placedLightShadows: true, shadowBudget: 0)).IsEqual(4096);
		AssertThat(GameWindowSettings.ResolvePositionalShadowAtlasSize(placedLightShadows: false, shadowBudget: 4)).IsEqual(4096);

		var lowCfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["placed_light_shadows"] = "false",
			["display_resolution"] = "1920x1080",
		};
		GameWindowSettings parsed = GameWindowSettings.FromValues(lowCfg, "refii.toml");
		AssertThat(parsed.PlacedLightShadows).IsFalse();
	}

	/// <summary>
	/// Verifies <see cref="Engine_LevelAmbientOcclusion.ShouldRebakeForResolution"/> accepts higher-resolution
	/// cached terrain AO when a lower resolution is configured unless rebuild is explicitly forced,
	/// and verifies <see cref="GameWorld.ShouldTriggerLohHighWatermark"/>.
	/// </summary>
	[TestCase]
	public void AoResolutionCacheGuardAndLohHighWatermarkBehaveDeterministically()
	{
		AssertThat(Engine_LevelAmbientOcclusion.ShouldRebakeForResolution(4096, 2048)).IsFalse();
		AssertThat(Engine_LevelAmbientOcclusion.ShouldRebakeForResolution(2048, 2048)).IsFalse();
		AssertThat(Engine_LevelAmbientOcclusion.ShouldRebakeForResolution(2048, 4096)).IsTrue();
		AssertThat(Engine_LevelAmbientOcclusion.ShouldRebakeForResolution(4096, 2048, forceRebuild: true)).IsTrue();

		const long oneGb = 1024L * 1024L * 1024L;
		AssertThat(GameWorld.ShouldTriggerLohHighWatermark(oneGb, oneGb / 2, worldBuildHeld: false, deviceLost: false)).IsFalse();
		AssertThat(GameWorld.ShouldTriggerLohHighWatermark(oneGb * 2, oneGb / 2, worldBuildHeld: true, deviceLost: false)).IsFalse();
		AssertThat(GameWorld.ShouldTriggerLohHighWatermark(oneGb * 2, oneGb / 2, worldBuildHeld: false, deviceLost: true)).IsFalse();
		AssertThat(GameWorld.ShouldTriggerLohHighWatermark(oneGb * 2, oneGb / 2, worldBuildHeld: false, deviceLost: false)).IsTrue();
	}

	/// <summary>
	/// Verifies <see cref="GameChildProcess.BuildRendererSettingsContent"/> honors optional D3D12
	/// diagnostic env overrides for soft shadow quality and descriptor heap limits.
	/// </summary>
	[TestCase]
	public void BuildRendererSettingsContentAppliesD3D12DiagnosticOverrides()
	{
		var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["lighting_model"] = "modern",
		};
		var env = new Dictionary<string, string?>(StringComparer.Ordinal)
		{
			["REFII_D3D12_SOFT_SHADOW_QUALITY"] = "0",
			["REFII_D3D12_MAX_RESOURCE_DESCRIPTORS"] = "131072",
			["REFII_D3D12_MAX_RESOURCE_DESCRIPTORS_PER_FRAME"] = "65536",
		};

		string content = GameChildProcess.BuildRendererSettingsContent(config, envOverrides: env);
		AssertThat(content.Contains("rendering_device/d3d12/max_resource_descriptors=131072", StringComparison.Ordinal)).IsTrue();
		AssertThat(content.Contains("rendering_device/d3d12/max_resource_descriptors_per_frame=65536", StringComparison.Ordinal)).IsTrue();
		AssertThat(content.Contains("lights_and_shadows/directional_shadow/soft_shadow_filter_quality=0", StringComparison.Ordinal)).IsTrue();
	}

	/// <summary>
	/// Verifies DRY gating of <see cref="Engine_ArrayMeshes.ShouldStripSurfaceLodsForD3d12"/>,
	/// <see cref="Refii.Lighting.Engine_PostFxParams.RequiresSaturationMask"/>,
	/// <see cref="Refii.Lighting.Engine_DepthPrepassMarker.ShouldEnableMarker"/>,
	/// <see cref="GameWorld.ParseDx12Skip3d"/> <c>hero</c> token, shared <c>NextPass</c> copying in
	/// <c>Engine_MaterialCopy</c>, and post-TDR <see cref="Engine_RdHealth"/> early-outs.
	/// </summary>
	[TestCase]
	public void DryDx12WorkaroundStencilGatingAndRdHealthGuardsHold()
	{
		// Upstream Godot #120857: strip runtime surface LODs on D3D12 by default unless overridden.
		AssertThat(Engine_ArrayMeshes.ShouldStripSurfaceLodsForD3d12(isD3d12: true)).IsTrue();
		AssertThat(Engine_ArrayMeshes.ShouldStripSurfaceLodsForD3d12(isD3d12: true, keepLodsEnv: "1")).IsFalse();
		AssertThat(Engine_ArrayMeshes.ShouldStripSurfaceLodsForD3d12(isD3d12: false)).IsFalse();

		// Zero-visual-change driver-agnostic saturation mask gating matching Xbox 360 0x821e5e60.
		AssertThat(Refii.Lighting.Engine_PostFxParams.RequiresSaturationMask(0.0f, 0.0f)).IsFalse();
		AssertThat(Refii.Lighting.Engine_PostFxParams.RequiresSaturationMask(0.005f, -0.009f)).IsFalse();
		AssertThat(Refii.Lighting.Engine_PostFxParams.RequiresSaturationMask(0.25f, 0.0f, chainActive: false)).IsFalse();
		AssertThat(Refii.Lighting.Engine_PostFxParams.RequiresSaturationMask(0.25f, 0.0f, chainActive: true, coverHeld: true)).IsFalse();
		AssertThat(Refii.Lighting.Engine_PostFxParams.RequiresSaturationMask(0.25f, 0.0f, chainActive: true, coverHeld: false)).IsTrue();
		AssertThat(Refii.Lighting.Engine_PostFxParams.RequiresSaturationMask(0.0f, -0.15f, chainActive: true, coverHeld: false)).IsTrue();

		// Zero-visual-change driver-agnostic depth pre-pass marker gating (Classic-only when native pre-pass is off).
		AssertThat(Refii.Lighting.Engine_DepthPrepassMarker.ShouldEnableMarker(enabled: true, nativeDepthPrepassEnabled: true)).IsFalse();
		AssertThat(Refii.Lighting.Engine_DepthPrepassMarker.ShouldEnableMarker(enabled: false, nativeDepthPrepassEnabled: false)).IsFalse();
		AssertThat(Refii.Lighting.Engine_DepthPrepassMarker.ShouldEnableMarker(enabled: true, nativeDepthPrepassEnabled: false)).IsTrue();

		// Hero skip token in GameWorld.ParseDx12Skip3d.
		GameWorld.Dx12Skip3dMask heroMask = GameWorld.ParseDx12Skip3d("hero");
		AssertThat(heroMask).IsEqual(GameWorld.Dx12Skip3dMask.Hero);
		AssertThat((GameWorld.Dx12Skip3dMask.All & GameWorld.Dx12Skip3dMask.Hero) != 0).IsTrue();

		// Engine_MaterialCopy deep-clones ShaderMaterial NextPass; RD health guards cover FX/PostFX.
		string srcRoot = Path.Combine(FindRepoRoot(), "src");
		string matCopy = File.ReadAllText(Path.Combine(srcRoot, "Engine", "material", "Engine_MaterialCopy.cs"));
		AssertThat(matCopy.Contains("source.NextPass is ShaderMaterial nextPass", StringComparison.Ordinal)).IsTrue();

		string fxDepth = File.ReadAllText(Path.Combine(srcRoot, "Engine", "fx", "Engine_FxDepthCaptureEffect.cs"));
		AssertThat(fxDepth.Contains("Engine_FxDepthCaptureEffect.EnsureTarget", StringComparison.Ordinal)).IsTrue();

		string satMask = File.ReadAllText(Path.Combine(srcRoot, "Engine", "Lighting", "Engine_PostFxSaturationMask.cs"));
		AssertThat(satMask.Contains("Engine_PostFxSaturationMask.Resize", StringComparison.Ordinal)).IsTrue();

		string postFx = File.ReadAllText(Path.Combine(srcRoot, "Engine", "Lighting", "Engine_PostFxEffect.cs"));
		AssertThat(postFx.Contains("Engine_PostFxEffect.EnsureTargets.quarter", StringComparison.Ordinal)).IsTrue();

		string fxDriver = File.ReadAllText(Path.Combine(srcRoot, "Engine", "fx", "Engine_FxDriver.cs"));
		AssertThat(fxDriver.Contains("Engine_RdHealth.IsDeviceLost", StringComparison.Ordinal)).IsTrue();

		string fableFx = File.ReadAllText(Path.Combine(srcRoot, "Engine", "fx", "FableFx.cs"));
		AssertThat(fableFx.Contains("Engine_RdHealth.IsDeviceLost", StringComparison.Ordinal)).IsTrue();
	}

	/// <summary>
	/// Verifies <c>DualParaboloid</c> is retired across C# code, shadow-caster proxy selection uses
	/// zero-discard solid and 1-tap cutout shaders, surface shaders strip Stage 2 behind
	/// <c>!IN_SHADOW_PASS</c>, and degenerate water edge-blend diagnostics log at most once per tuple.
	/// </summary>
	[TestCase]
	public void DualParaboloidRetiredShadowPassStrippedAndWaterLogDeduplicated()
	{
		string srcRoot = Path.Combine(FindRepoRoot(), "src");

		// Zero DualParaboloid references in runtime C# code.
		foreach (string csPath in Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
		{
			string csCode = File.ReadAllText(csPath);
			AssertThat(csCode.Contains("DualParaboloid", StringComparison.Ordinal))
				.OverrideFailureMessage($"{csPath} still references DualParaboloid")
				.IsFalse();
		}

		// Shadow proxy solid vs cutout caster policies.
		AssertThat(Refii.Entities.Engine_ShadowProxy.UsesSolidProxyCaster(skinned: true, always: false)).IsTrue();
		AssertThat(Refii.Entities.Engine_ShadowProxy.UsesSolidProxyCaster(skinned: false, always: true)).IsTrue();
		AssertThat(Refii.Entities.Engine_ShadowProxy.UsesSolidProxyCaster(skinned: false, always: false)).IsFalse();

		AssertThat(Refii.Entities.Engine_ShadowProxy.CanUseDedicatedCutoutCaster(
			skinned: true, always: false, "res://src/Engine/material/standard_material.gdshader")).IsTrue();
		AssertThat(Refii.Entities.Engine_ShadowProxy.CanUseDedicatedCutoutCaster(
			skinned: true, always: false, "res://src/Engine/material/standard_material_phong.gdshader")).IsTrue();
		AssertThat(Refii.Entities.Engine_ShadowProxy.CanUseDedicatedCutoutCaster(
			skinned: true, always: false, "res://src/Engine/material/standard_material_wind.gdshader")).IsFalse();
		AssertThat(Refii.Entities.Engine_ShadowProxy.CanUseDedicatedCutoutCaster(
			skinned: false, always: false, "res://src/Engine/material/standard_material.gdshader")).IsFalse();

		string cutoutShader = StripShaderComments(File.ReadAllText(
			Path.Combine(srcRoot, "Engine", "entities", "shadow_caster_cutout.gdshader")));
		AssertThat(cutoutShader.Contains("textureLod(albedo_tex,", StringComparison.Ordinal)).IsTrue();
		AssertThat(System.Text.RegularExpressions.Regex.IsMatch(cutoutShader, @"\bdFd[xy]\(")).IsFalse();
		AssertThat(System.Text.RegularExpressions.Regex.Matches(cutoutShader, @"\bdiscard\s*;").Count).IsEqual(1);

		// Surface shaders must never gate derivatives on runtime UBO IN_SHADOW_PASS, and under
		// Forward+ (`RENDERER_FORWARD_PLUS`) zero PASS_MODE_COLOR shaders may emit `discard;`.
		foreach (string relPath in new[]
		         {
			         Path.Combine("Engine", "material", "standard_material_body.gdshaderinc"),
			         Path.Combine("Engine", "material", "standard_material_phong.gdshader"),
			         Path.Combine("Engine", "material", "families", "tree_canopy.gdshader"),
			         Path.Combine("Engine", "material", "fur_shell.gdshaderinc"),
		         })
		{
			string code = StripShaderComments(File.ReadAllText(Path.Combine(srcRoot, relPath)));
			AssertThat(code.Contains("IN_SHADOW_PASS", StringComparison.Ordinal))
				.OverrideFailureMessage($"{relPath} must not gate fragment work or derivatives on IN_SHADOW_PASS")
				.IsFalse();
		}

		string godotRoot = FindRepoRoot();
		string? ReadRes(string resPath)
		{
			string full = Path.Combine(godotRoot, resPath["res://".Length..].Replace('/', Path.DirectorySeparatorChar));
			return File.Exists(full) ? File.ReadAllText(full).Replace("\r\n", "\n") : null;
		}

		var depthOnlyAllowedDiscards = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"res://src/Engine/entities/shadow_caster.gdshader",
			"res://src/Engine/entities/shadow_caster_cutout.gdshader",
			"res://src/Engine/Lighting/depth_prepass_marker.gdshader",
		};

		var expectedForwardPlusDepthPrepassAlpha = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"res://src/Engine/material/standard_material.gdshader",
			"res://src/Engine/material/standard_material_wind.gdshader",
			"res://src/Engine/material/standard_material_tree.gdshader",
			"res://src/Engine/material/fur_shell.gdshader",
			"res://src/Engine/material/standard_material_phong.gdshader",
			"res://src/Engine/material/families/tree_canopy.gdshader",
			"res://src/Engine/entities/entity_highlight_silhouette.gdshader",
		};

		foreach (string shaderFile in Directory.GetFiles(srcRoot, "*.gdshader", SearchOption.AllDirectories))
		{
			string relToGodot = Path.GetRelativePath(godotRoot, shaderFile).Replace('\\', '/');
			string resPath = "res://" + relToGodot;
			string rawCode = File.ReadAllText(shaderFile).Replace("\r\n", "\n");
			string preprocessed = Refii.Models.Engine_ShaderCeilingSource.Preprocess(
				rawCode,
				resPath,
				ReadRes,
				Refii.Models.Engine_ShaderCeilingSource.RendererForwardPlus);

			bool hasDiscard = System.Text.RegularExpressions.Regex.IsMatch(preprocessed, @"\bdiscard\s*;");
			bool expectedDiscard = depthOnlyAllowedDiscards.Contains(resPath);
			AssertThat(hasDiscard)
				.OverrideFailureMessage($"{resPath} unexpected discard state under Forward+ (got {hasDiscard}, expected {expectedDiscard})")
				.IsEqual(expectedDiscard);

			if (expectedForwardPlusDepthPrepassAlpha.Contains(resPath))
			{
				AssertThat(preprocessed.Contains("depth_prepass_alpha", StringComparison.Ordinal))
					.OverrideFailureMessage($"{resPath} must enable depth_prepass_alpha under Forward+")
					.IsTrue();
			}
			else if (resPath is "res://src/Engine/material/standard_material_culled.gdshader"
			         or "res://src/Engine/material/standard_material_translucent.gdshader")
			{
				AssertThat(preprocessed.Contains("depth_prepass_alpha", StringComparison.Ordinal))
					.OverrideFailureMessage($"{resPath} must not enable depth_prepass_alpha under Forward+")
					.IsFalse();
			}
		}

		AssertThat(Refii.Models.Engine_MdlMaterial.CutoutRenderPriority).IsLess(0);

		// Degenerate water edge blend log deduplication.
		Refii.Level.Engine_WaterTheme.ResetLoggedDegenerateEdgeBlendsForTests();
		AssertThat(Refii.Level.Engine_WaterTheme.ShouldLogDegenerateEdgeBlend(0f, 0f, 0f)).IsTrue();
		AssertThat(Refii.Level.Engine_WaterTheme.ShouldLogDegenerateEdgeBlend(0f, 0f, 0f)).IsFalse();
		AssertThat(Refii.Level.Engine_WaterTheme.ShouldLogDegenerateEdgeBlend(1f, 1f, 0f)).IsTrue();
		Refii.Level.Engine_WaterTheme.ResetLoggedDegenerateEdgeBlendsForTests();
		AssertThat(Refii.Level.Engine_WaterTheme.ShouldLogDegenerateEdgeBlend(0f, 0f, 0f)).IsTrue();
	}

	[TestCase]
	public void ShadowProxyFollowerReachAndSpecialisedCutoutPolicy()
	{
		// Stationary rigid props use static ShadowReach culling; skinned characters and moving
		// attached followers (held/sheathed weapons) travel with their holder and stay excluded.
		AssertThat(Refii.Entities.Engine_ShadowProxy.UsesStaticShadowReach(skinned: false, follows: false)).IsTrue();
		AssertThat(Refii.Entities.Engine_ShadowProxy.UsesStaticShadowReach(skinned: false, follows: true)).IsFalse();
		AssertThat(Refii.Entities.Engine_ShadowProxy.UsesStaticShadowReach(skinned: true, follows: true)).IsFalse();
		AssertThat(Refii.Entities.Engine_ShadowProxy.UsesStaticShadowReach(skinned: true, follows: false)).IsFalse();

		string shadowProxySource = File.ReadAllText(
			Path.Combine(FindRepoRoot(), "src", "Engine", "entities", "Engine_ShadowProxy.cs"));
		int cutoutCallIdx = shadowProxySource.IndexOf("CanUseDedicatedCutoutCaster(", StringComparison.Ordinal);
		// Skip the method declaration itself and inspect the call site inside Build().
		int buildCallIdx = shadowProxySource.IndexOf("CanUseDedicatedCutoutCaster(", cutoutCallIdx + 1, StringComparison.Ordinal);
		AssertThat(buildCallIdx).IsGreater(0);
		string callSlice = shadowProxySource.Substring(buildCallIdx, Math.Min(260, shadowProxySource.Length - buildCallIdx));
		AssertThat(callSlice.Contains("Engine_ShaderSpecialisation.PathOf(", StringComparison.Ordinal)).IsTrue();
		AssertThat(callSlice.Contains("Engine_ShaderVariant.FullPathOf(", StringComparison.Ordinal)).IsTrue();
	}

	/// <summary>
	/// Verifies RD viewport texture invalidation across A -> B -> A Scaling3DScale transitions,
	/// relaxed D3D12/Vulkan upload budgets, and wake-on-enqueue chained worker servicing.
	/// </summary>
	[TestCase]
	public void LevelSwap_RdResolutionAndUploadPacing_EliminateStaleRidAndChainedWorkerStalls()
	{
		var halfSize = new Godot.Vector2I(665, 416);
		AssertThat(Engine_RdHealth.ShouldReuseResolvedRd(
			101ul, halfSize, 1ul, 101ul, halfSize, 1ul, 202ul, rdTextureValid: true)).IsTrue();

		// Same Viewport texture RID & returned 665x416 size after an intermediate 1280x800 swap,
		// but publication generation incremented -> must re-query TextureGetRdTexture.
		AssertThat(Engine_RdHealth.ShouldReuseResolvedRd(
			101ul, halfSize, 3ul, 101ul, halfSize, 1ul, 202ul, rdTextureValid: true)).IsFalse();

		// Live device-texture check failing -> must re-query immediately.
		AssertThat(Engine_RdHealth.ShouldReuseResolvedRd(
			101ul, halfSize, 1ul, 101ul, halfSize, 1ul, 202ul, rdTextureValid: false)).IsFalse();

		// Relaxed post-TDR upload caps: D3D12 64 MB / 24 MB, Vulkan 96 MB / 32 MB.
		AssertThat(Engine_ServerSyncs.EffectiveMaxUploadBytesPerFrame(loadCoverActive: true, isD3D12: true))
			.IsEqual(64L * 1024L * 1024L);
		AssertThat(Engine_ServerSyncs.EffectiveMaxUploadBytesPerFrame(loadCoverActive: false, isD3D12: true))
			.IsEqual(24L * 1024L * 1024L);
		AssertThat(Engine_ServerSyncs.EffectiveMaxUploadBytesPerFrame(loadCoverActive: true, isD3D12: false))
			.IsEqual(96L * 1024L * 1024L);
		AssertThat(Engine_ServerSyncs.EffectiveMaxUploadBytesPerFrame(loadCoverActive: false, isD3D12: false))
			.IsEqual(32L * 1024L * 1024L);

		// Chained worker uploads wake and complete inside a single load-cover pump window.
		Engine_ServerSyncs.ResetMainThreadForTest();
		try
		{
			Engine_ServerSyncs.MarkMainThread();
			Engine_ServerSyncs.LoadCoverActive = true;
			int committed = 0;
			var workerTask = System.Threading.Tasks.Task.Run(() =>
			{
				for (int i = 0; i < 3; i++)
				{
					System.Threading.Thread.Sleep(2);
					Engine_ServerSyncs.RunGpuUpload(4096L, () =>
					{
						System.Threading.Interlocked.Increment(ref committed);
					});
				}
			});

			long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 2;
			while (!workerTask.IsCompleted && System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
			{
				Engine_ServerSyncs.PumpWorkerGpuUploads(25.0, waitForWorkers: true);
			}

			AssertThat(workerTask.Wait(1000)).IsTrue();
			AssertThat(committed).IsEqual(3);
		}
		finally
		{
			Engine_ServerSyncs.ResetMainThreadForTest();
		}
	}

	/// <summary>
	/// Verifies bounded cross-region LRU texture eviction, wind-direction material keying,
	/// desktop prefetch concurrency scaling, and unfocused load-cover FPS retention.
	/// </summary>
	[TestCase]
	public void CrossRegionWorkingSet_And_ConcurrencyPolicies_RespectLimits()
	{
		var entries = new List<(string Key, long Bytes, long LastAccess)>
		{
			("T:old_hub", 400, 10),
			("T:recent_shared", 350, 30),
			("O:mid_orm", 300, 20),
		};

		AssertThat(Engine_TexLibrary.SelectLruKeysToEvict(entries, 1200).Count).IsEqual(0);
		List<string> evicted = Engine_TexLibrary.SelectLruKeysToEvict(entries, 600);
		AssertThat(evicted.Count).IsEqual(2);
		AssertThat(evicted[0]).IsEqual("T:old_hub");
		AssertThat(evicted[1]).IsEqual("O:mid_orm");
		AssertThat(Engine_TexLibrary.SelectLruKeysToEvict(entries, 0).Count).IsEqual(3);

		Godot.Vector2 prevWind = Refii.Models.Engine_MdlMaterial.WindDirection;
		try
		{
			var mat = new Refii.Models.MdlMaterial
			{
				Diffuse = "art/test_tree.tex",
			};
			var swaying = new Refii.Models.MdlInfo { MovementMode = 1, WindAmplitude = 0.05f };
			var rigid = new Refii.Models.MdlInfo { MovementMode = 0, WindAmplitude = 0f };

			Refii.Models.Engine_MdlMaterial.WindDirection = new Godot.Vector2(1f, 0f);
			string swayEast = Refii.Models.Engine_MdlMaterial.BuildSharedKey(mat, swaying, false);
			string rigidEast = Refii.Models.Engine_MdlMaterial.BuildSharedKey(mat, rigid, false);

			Refii.Models.Engine_MdlMaterial.WindDirection = new Godot.Vector2(0f, 1f);
			string swayNorth = Refii.Models.Engine_MdlMaterial.BuildSharedKey(mat, swaying, false);
			string rigidNorth = Refii.Models.Engine_MdlMaterial.BuildSharedKey(mat, rigid, false);

			AssertThat(swayEast).IsNotEqual(swayNorth);
			AssertThat(rigidEast).IsEqual(rigidNorth);
		}
		finally
		{
			Refii.Models.Engine_MdlMaterial.WindDirection = prevWind;
		}

		AssertThat(Refii.Level.Engine_PropStreamer.ResolvePrefetchWorkerCount(16, isAndroid: true, 100)).IsEqual(2);
		AssertThat(Refii.Level.Engine_PropStreamer.ResolvePrefetchWorkerCount(16, isAndroid: false, 100)).IsEqual(6);
		AssertThat(Refii.Level.Engine_PropStreamer.ResolvePrefetchWorkerCount(8, isAndroid: false, 100)).IsEqual(4);
		AssertThat(Refii.Level.Engine_PropStreamer.ResolvePrefetchWorkerCount(16, isAndroid: false, 3)).IsEqual(3);
		AssertThat(Refii.Level.Engine_PropStreamer.ResolvePrefetchWorkerCount(16, isAndroid: false, 0)).IsEqual(0);
		AssertThat(Engine_LevelCaches.ResolveDefaultRegionCacheBudgetMb(isD3d12: true)).IsEqual(256);
		AssertThat(Engine_LevelCaches.ResolveDefaultRegionCacheBudgetMb(isD3d12: false)).IsEqual(512);

		AssertThat(GameWindow.ResolveEffectiveMaxFps(120, focused: false, loadCoverHeld: true)).IsEqual(120);
		AssertThat(GameWindow.ResolveEffectiveMaxFps(0, focused: false, loadCoverHeld: true)).IsEqual(0);
		AssertThat(GameWindow.ResolveEffectiveMaxFps(120, focused: false, loadCoverHeld: false)).IsEqual(30);
	}

	/// <summary>
	/// Verifies <see cref="GameWorld.AreGuestEntitiesReadyForMount"/> ignores stale <c>worldReady</c>
	/// during blocking State 3 loads and waits for <c>WorldIsActive</c> (ABI &gt;= 50), and that
	/// <see cref="Engine_TexLibrary.RunKeyedSingleFlight{T}"/> deduplicates concurrent ORM solves.
	/// </summary>
	[TestCase]
	public void LevelSwap_MountGateAndSingleFlightOrm_PreventPrematureReleaseAndDuplicateWork()
	{
		// Exact BWSMarket fast-travel regression: blocking State 3 keeps stale worldReady=true while World+0xA8=false.
		AssertThat(GameWorld.AreGuestEntitiesReadyForMount(
			worldActive: false, worldReady: true, worldActiveSupported: true,
			hasFullLoadInFlight: true, inFlightState: 3, waitFrames: 10, waitLimit: 1800)).IsFalse();
		AssertThat(GameWorld.AreGuestEntitiesReadyForMount(
			worldActive: false, worldReady: true, worldActiveSupported: true,
			hasFullLoadInFlight: false, inFlightState: 0, waitFrames: 10, waitLimit: 1800)).IsFalse();
		AssertThat(GameWorld.AreGuestEntitiesReadyForMount(
			worldActive: true, worldReady: false, worldActiveSupported: true,
			hasFullLoadInFlight: true, inFlightState: 5, waitFrames: 10, waitLimit: 1800)).IsTrue();
		// Legacy ABI fallback & timeout safety ceiling.
		AssertThat(GameWorld.AreGuestEntitiesReadyForMount(
			worldActive: false, worldReady: true, worldActiveSupported: false,
			hasFullLoadInFlight: false, inFlightState: 0, waitFrames: 10, waitLimit: 1800)).IsTrue();
		AssertThat(GameWorld.AreGuestEntitiesReadyForMount(
			worldActive: false, worldReady: false, worldActiveSupported: true,
			hasFullLoadInFlight: true, inFlightState: 3, waitFrames: 1800, waitLimit: 1800)).IsTrue();

		// Concurrent crowd ORM deduplication via single-flight gate.
		const int workers = 8;
		var gate = new object();
		var flights = new Dictionary<string, object>(StringComparer.Ordinal);
		using var barrier = new System.Threading.Barrier(workers);
		int executions = 0;
		var results = new string[workers];
		var leaderFlags = new bool[workers];
		var tasks = new System.Threading.Tasks.Task[workers];
		for (int i = 0; i < workers; i++)
		{
			int idx = i;
			tasks[i] = System.Threading.Tasks.Task.Run(() =>
			{
				barrier.SignalAndWait();
				results[idx] = Engine_TexLibrary.RunKeyedSingleFlight(
					gate,
					flights,
					"orm-key-alpha",
					() =>
					{
						int call = System.Threading.Interlocked.Increment(ref executions);
						System.Threading.Thread.Sleep(25);
						return $"packed-{call}";
					},
					out bool wasLeader);
				leaderFlags[idx] = wasLeader;
			});
		}

		System.Threading.Tasks.Task.WaitAll(tasks);
		AssertThat(executions).IsEqual(1);
		int leaderCount = 0;
		for (int i = 0; i < workers; i++)
		{
			if (leaderFlags[i]) leaderCount++;
			AssertThat(results[i]).IsEqual("packed-1");
		}
		AssertThat(leaderCount).IsEqual(1);
		AssertThat(flights.Count).IsEqual(0);
	}

	/// <summary>
	/// Verifies gameplay lean <see cref="Refii.Entities.Engine_EntityInspect.Build"/> matches
	/// editor mode on <see cref="Refii.Entities.EntityInspectData.HasFor"/> without reading Babel strings.
	/// </summary>
	[TestCase]
	public void EntityInspect_LeanGameplayMode_PreservesHasForWithoutBabelReads()
	{
		const uint chestEntity = 0x11110001u;
		const uint chestComp = 0x11110002u;
		const uint invComp = 0x11110003u;
		const uint readableEntity = 0x22220001u;
		const uint readableComp = 0x22220002u;
		const uint sceneryEntity = 0x33330001u;

		var db = new Refii.Gdb.GdbPatchDatabase();
		var chest = new Refii.Gdb.GdbPatchEntry(chestEntity, 0x01);
		chest.Properties.Add(new Refii.Gdb.GdbPatchProperty(
			Refii.Entities.Engine_EntityContents.ChestComponent, Refii.Gdb.GdbFieldType.Reference, chestComp));
		chest.Properties.Add(new Refii.Gdb.GdbPatchProperty(
			Refii.Entities.Engine_EntityContents.InventoryComponentA, Refii.Gdb.GdbFieldType.Reference, invComp));
		db.Entries.Add(chest);
		db.Entries.Add(new Refii.Gdb.GdbPatchEntry(chestComp, 0x02));
		db.Entries.Add(new Refii.Gdb.GdbPatchEntry(invComp, 0x02));

		var sign = new Refii.Gdb.GdbPatchEntry(readableEntity, 0x01);
		sign.Properties.Add(new Refii.Gdb.GdbPatchProperty(
			Refii.Entities.Engine_EntityReadables.ReadableComponent, Refii.Gdb.GdbFieldType.Reference, readableComp));
		db.Entries.Add(sign);
		var signComp = new Refii.Gdb.GdbPatchEntry(readableComp, 0x02);
		signComp.Properties.Add(new Refii.Gdb.GdbPatchProperty(
			Refii.Entities.Engine_EntityReadables.TextTag, Refii.Gdb.GdbFieldType.ResourceReference, 0x44440001u));
		db.Entries.Add(signComp);
		db.Entries.Add(new Refii.Gdb.GdbPatchEntry(sceneryEntity, 0x01));

		byte[] gdbBytes = Refii.Gdb.GdbWriter.Serialize(db);
		byte[] saveBytes = System.Text.Encoding.UTF8.GetBytes("""
			<?xml version="1.0" encoding="utf-8"?>
			<Save version="2">
			  <Level name="TestArea" version="1">
			    <id_10000001 name="DefaultLayer" load="AlwaysOn">
			      <Entity name="Chest_01">0x11110001</Entity>
			      <Entity name="Sign_01">0x22220001</Entity>
			      <Entity name="Rock_01">0x33330001</Entity>
			    </id_10000001>
			  </Level>
			</Save>
			""");

		var level = new Refii.Level.LevelEntry
		{
			World = "albion",
			Name = "TestArea",
			LevelPath = "albion/testarea",
			MainEhfPath = "levels/albion/testarea/testarea.ehf",
		};

		var requestedPaths = new List<string>();
		byte[]? Read(string path)
		{
			requestedPaths.Add(path);
			if (path.EndsWith("testarea.gdb", StringComparison.OrdinalIgnoreCase)) return gdbBytes;
			if (path.EndsWith("testarea.save", StringComparison.OrdinalIgnoreCase)) return saveBytes;
			return null;
		}

		Refii.Entities.EntityInspectData? lean = Refii.Entities.Engine_EntityInspect.Build(
			level, Read, fullMetadata: false);
		AssertThat(requestedPaths.Exists(static p => p.Contains("babel", StringComparison.OrdinalIgnoreCase))).IsFalse();
		AssertThat(lean).IsNotNull();
		AssertThat(lean!.FullMetadata).IsFalse();
		AssertThat(lean.HasFor(chestEntity)).IsTrue();
		AssertThat(lean.HasFor(readableEntity)).IsTrue();
		AssertThat(lean.HasFor(sceneryEntity)).IsFalse();

		Refii.Entities.EntityInspectData? full = Refii.Entities.Engine_EntityInspect.Build(
			level, Read, fullMetadata: true);
		AssertThat(full).IsNotNull();
		AssertThat(full!.FullMetadata).IsTrue();
		AssertThat(full.HasFor(chestEntity)).IsEqual(lean.HasFor(chestEntity));
		AssertThat(full.HasFor(readableEntity)).IsEqual(lean.HasFor(readableEntity));
		AssertThat(full.HasFor(sceneryEntity)).IsEqual(lean.HasFor(sceneryEntity));
	}

	/// <summary>
	/// Extracts a contiguous slice of source text between two anchor markers for focused
	/// invariant checks.
	/// </summary>
	private static string ExtractSourceSlice(string source, string startMarker, string endMarker)
	{
		int start = source.IndexOf(startMarker, StringComparison.Ordinal);
		AssertThat(start >= 0).IsTrue();
		int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
		AssertThat(end > start).IsTrue();
		return source.Substring(start, end - start);
	}

	/// <summary>
	/// Verifies the Round 2 main-thread render-sync eliminations: cloth untints write neutral
	/// <c>Vector4.One</c> instead of <c>default(Variant)</c>, <see cref="Refii.Entities.Engine_ViewTwin.Create"/>
	/// watches the source body's instance parameters, and both half-res viewport layers pre-warm
	/// <c>_layerRd</c> inside <c>Resize()</c>.
	/// </summary>
	[TestCase]
	public void EntityTintTwinAndLayerPrewarm_AvoidMainThreadRenderSyncStalls()
	{
		string godotRoot = FindRepoRoot();

		string appearanceSource = File.ReadAllText(Path.Combine(godotRoot, "src", "Engine", "entities", "Engine_EntityAppearance.cs"));
		string mirrorTintSlice = ExtractSourceSlice(appearanceSource, "private static bool MirrorTint(", "public static void Apply(");
		AssertThat(mirrorTintSlice.Contains("cloth.SetInstanceShaderParameter(parameter, Vector4.One);", StringComparison.Ordinal)).IsTrue();
		AssertThat(mirrorTintSlice.Contains("default(Variant)", StringComparison.Ordinal)).IsFalse();

		string twinSource = File.ReadAllText(Path.Combine(godotRoot, "src", "Engine", "entities", "Engine_ViewTwin.cs"));
		string twinCreateSlice = ExtractSourceSlice(twinSource, "public static Engine_ViewTwin? Create(", "public static bool[] Subtree(");
		AssertThat(twinCreateSlice.Contains("Engine_InstanceParams.Watch(body);", StringComparison.Ordinal)).IsTrue();

		string highlightSource = File.ReadAllText(Path.Combine(godotRoot, "src", "Engine", "entities", "Engine_EntityHighlightLayer.cs"));
		string highlightResizeSlice = ExtractSourceSlice(highlightSource, "private void Resize()", "public override void _Process(");
		AssertThat(highlightResizeSlice.Contains("_layerRd = ViewportTexRid();", StringComparison.Ordinal)).IsTrue();
		AssertThat(highlightResizeSlice.Contains("_publishedTex[_view] = _layerRd;", StringComparison.Ordinal)).IsTrue();

		string additiveSource = File.ReadAllText(Path.Combine(godotRoot, "src", "Engine", "fx", "Engine_FxAdditiveLayer.cs"));
		string additiveResizeSlice = ExtractSourceSlice(additiveSource, "private void Resize()", "public override void _Process(");
		AssertThat(additiveResizeSlice.Contains("_layerRd = ViewportTexRid();", StringComparison.Ordinal)).IsTrue();
		AssertThat(additiveResizeSlice.Contains("_publishedTex[_view] = _layerRd;", StringComparison.Ordinal)).IsTrue();
	}

	/// <summary>
	/// Ensures legacy/upscaled vanilla assets (<c>!FABLE_IS_PBR</c>) preserve their authored
	/// <c>specular_tex</c> colour and exponent while still reading cavity AO and parallax height
	/// from synthesized ORM textures, and keeps ORM-to-Phong reconstruction gated strictly on
	/// <c>FABLE_IS_PBR</c>.
	/// </summary>
	[TestCase]
	public void LegacyVanillaSpecularIsPreservedWithSynthesizedOrm()
	{
		string godotRoot = FindRepoRoot();
		string raw = File.ReadAllText(Path.Combine(godotRoot, "src", "Engine", "material", "standard_material_body.gdshaderinc"));
		string body = StripShaderComments(raw);

		AssertThat(body.Contains("FABLE_HAS_ORM && (has_specular || FABLE_IS_PBR)", StringComparison.Ordinal)).IsFalse();

		string specSlice = ExtractSourceSlice(body, "v_specular = spec_colour;", "#endif");
		AssertThat(specSlice.Contains("v_specular = spec_colour;", StringComparison.Ordinal)).IsTrue();
		AssertThat(specSlice.Contains("v_specular_power = spec_power;", StringComparison.Ordinal)).IsTrue();
		AssertThat(specSlice.Contains("if (FABLE_HAS_ORM && FABLE_IS_PBR)", StringComparison.Ordinal)).IsTrue();
		AssertThat(specSlice.Contains("v_specular = mix(vec3(0.04), alb.rgb, METALLIC);", StringComparison.Ordinal)).IsTrue();

		// Preserve synthesized ORM upscaling features (cavity AO + parallax height) on legacy assets.
		AssertThat(body.Contains("ao = orm.r;", StringComparison.Ordinal)).IsTrue();
		AssertThat(body.Contains("if (FABLE_HAS_ORM && has_height && parallax_scale > 0.0)", StringComparison.Ordinal)).IsTrue();
	}
}
