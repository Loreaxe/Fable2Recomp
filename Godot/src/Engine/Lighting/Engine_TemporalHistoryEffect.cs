using System.Threading;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// Starts a view's temporal history over on the frame its picture stops continuing its own last drawn
/// frame: a co-op view that draws again after not drawing (the halves at a split, the window at a merge or
/// when player two leaves). Asked by <see cref="Refii.Game.GameSplitView"/> only, so a single-player camera
/// never drops anything; the effect is disabled except on the frame it has work.
/// </summary>
/// <remarks>
/// <para>TAA. Godot keeps the history in the render buffers' <c>taa</c> context: RendererRD::TAA::process
/// creates <c>history</c>, <c>temp</c> and <c>prev_velocity</c> when <c>history</c> is missing and, on that
/// frame, takes the frame's own picture as the history without resolving against anything. The effect runs
/// after the transparent pass, before the resolve (render_forward_clustered.cpp: the POST_TRANSPARENT
/// callbacks, then <c>_render_3d_upscaling</c>), and clears that one context
/// (<see cref="RenderSceneBuffersRD.ClearContext"/>). Nothing else in the buffers moves: SDFGI, the
/// volumetric fog and our shafts keep their own history.</para>
/// <para>FSR 2. Its history is the FSR 2 context in the buffers' own data, which no API reaches
/// (<c>params.reset_accumulation = false; // FIXME: The engine does not provide a way to reset the
/// accumulation.</c>). The one stock way to start it over is to configure the view's render buffers again,
/// which <see cref="RenderingServer.ViewportSetFsrSharpness"/> does unconditionally
/// (renderer_viewport.cpp: <c>viewport->fsr_sharpness = p_sharpness; _configure_3d_render_buffers</c>), set
/// to the value the view already has. That frees every buffer of the view, SDFGI and fog included, so an
/// SDFGI user sees that view's GI settle again after a merge.</para>
/// <para>Godot source read: the 4.8-beta tree on disk (the 4.7.1 sources are not); the calls used are bound
/// in GodotSharp 4.7.1.</para>
/// </remarks>
public partial class Engine_TemporalHistoryEffect : CompositorEffect
{
	private static readonly StringName TaaContext = "taa";
	private static readonly StringName TaaHistory = "history";

	/// <summary>The effects enabled for a drop, until <see cref="Quiet"/> sees it taken (main thread).</summary>
	private static readonly System.Collections.Generic.List<Engine_TemporalHistoryEffect> Armed = new();

	private int _drop;

	public Engine_TemporalHistoryEffect()
	{
		EffectCallbackType = EffectCallbackTypeEnum.PostTransparent;
		Enabled = false;
	}

	public override void _RenderCallback(int effectCallbackType, RenderData renderData)
	{
		if (Interlocked.Exchange(ref _drop, 0) == 0)
		{
			return;
		}

		if (renderData.GetRenderSceneBuffers() is RenderSceneBuffersRD buffers &&
			buffers.HasTexture(TaaContext, TaaHistory))
		{
			buffers.ClearContext(TaaContext);
		}
	}

	/// <summary>
	/// Main thread: <paramref name="camera"/>'s view draws a picture this frame that does not continue its
	/// last drawn one. TAA's history is cleared at this frame's resolve; under FSR 2 the view's buffers are
	/// configured again, unless <paramref name="buffersFresh"/> (the view was resized this frame, which
	/// configured them already).
	/// </summary>
	public static void Drop(Camera3D? camera, bool buffersFresh = false)
	{
		if (camera is null || !GodotObject.IsInstanceValid(camera))
		{
			return;
		}

		foreach (Engine_TemporalHistoryEffect history in Of(camera))
		{
			Interlocked.Exchange(ref history._drop, 1);
			if (!history.Enabled)
			{
				history.Enabled = true;
				Armed.Add(history);
			}
		}

		if (!buffersFresh && camera.GetViewport() is { } view &&
			view.Scaling3DMode == Viewport.Scaling3DModeEnum.Fsr2)
		{
			RenderingServer.ViewportSetFsrSharpness(view.GetViewportRid(), view.FsrSharpness);
		}
	}

	/// <summary>Main thread, each co-op frame: disables every effect again once its drop was taken.</summary>
	public static void Quiet()
	{
		for (int i = Armed.Count - 1; i >= 0; --i)
		{
			Engine_TemporalHistoryEffect history = Armed[i];
			if (!GodotObject.IsInstanceValid(history))
			{
				Armed.RemoveAt(i);
			}
			else if (Volatile.Read(ref history._drop) == 0)
			{
				history.Enabled = false;
				Armed.RemoveAt(i);
			}
		}
	}

	private static System.Collections.Generic.IEnumerable<Engine_TemporalHistoryEffect> Of(Camera3D camera)
	{
		if (camera.Compositor is not { } compositor || !GodotObject.IsInstanceValid(compositor))
		{
			yield break;
		}

		foreach (CompositorEffect effect in compositor.CompositorEffects)
		{
			if (effect is Engine_TemporalHistoryEffect history)
			{
				yield return history;
			}
		}
	}
}
