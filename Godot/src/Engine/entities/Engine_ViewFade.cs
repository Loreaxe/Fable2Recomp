using System;
using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Entities;

/// <summary>
/// A camera's fade in its own view: on split screen an entity player two's camera fades (or does not) differently
/// from player one's is drawn in each half at that half's camera's fade.
/// </summary>
/// <remarks>
/// <para>WHAT EACH CAMERA STATES. A camera fades what stands near it through GraphicAppearance.SetCameraAlpha
/// (camerabase.lua, both titles), which writes the renderable's camera alpha (Fable 2 +116, Fable 3 +0x80):
/// one value per entity. In local co-op player two's camera manager updates after player one's on every screen,
/// shared or split, so both cameras wrote that one field and every view drew it — an object at player two's
/// camera was see-through in player one's half and in the shared picture. The core camera chunks
/// (CoopHeroLua.cs, Fable3CoreLua.cs) now keep player two's fades out of the field and state them as player two's
/// own book (<see cref="HostBridge.PartnerCameraAlphas"/>). The field is player one's camera's: the shared picture
/// and player one's half draw it, as before.</para>
/// <para>PLAYER TWO'S HALF. The opacity there is the entity's gameplay alpha times player two's camera alpha (1 for
/// an entity not in the book). Where it differs from player one's half by a step of the blend, the entity is drawn
/// once per view: the body in player one's view alone (<see cref="Engine_ViewHides.BodyLayers"/>), and a
/// <see cref="NodeName"/> child — the body's mesh, skin, materials and instance parameters
/// (<see cref="Engine_ViewTwin.CopyLook"/>), no shadow — in player two's alone, each on the layers
/// <see cref="Refii.Models.Engine_ClassicViews.OnlyIn"/> gives so it is lit as the body. A rig whose hidden bones
/// already stand twins (<see cref="Engine_ViewTwin.Narrows"/>) keeps them: each twin, and the body in the one view
/// it still draws, takes that view's fade (<see cref="TransparencyIn"/>).</para>
/// <para>One view (shared, hybrid while together, single player): player two's camera is not drawn, nothing here
/// acts, every node is as <see cref="Engine_EntityAppearance.Apply"/> left it.</para>
/// </remarks>
public static class Engine_ViewFade
{
	public const string NodeName = "ViewFade";

	/// <summary>Two fades closer than this blend alike: the title's blend factor is opacity × 255, truncated.</summary>
	public const float Step = 1f / 255f;

	/// <summary>The fade each view takes, for a body that takes a different one per view.</summary>
	private static readonly Dictionary<MeshInstance3D, (float First, float Second, bool Blended0, bool Blended1)> PerView =
		new(ReferenceEqualityComparer.Instance);

	/// <summary>The per-view twins this class stands, by body.</summary>
	private static readonly Dictionary<MeshInstance3D, Twin> Twins = new(ReferenceEqualityComparer.Instance);

	/// <summary>Per body drawn per half: the walks in a row its halves have been alike (<see cref="Hold"/>).</summary>
	private static readonly Dictionary<MeshInstance3D, int> AlikeWalks = new(ReferenceEqualityComparer.Instance);

	/// <summary>Entities that started / stopped being drawn per half since the session began (the churn the debug
	/// channel's <c>camera</c> op reads as two samples' difference).</summary>
	public static long Entered { get; private set; }

	public static long Left { get; private set; }

	/// <summary>Bodies whose own Transparency this class set for the one view they still draw.</summary>
	private static readonly HashSet<MeshInstance3D> Overridden = new(ReferenceEqualityComparer.Instance);

	private static readonly Dictionary<uint, float> Book = new();
	private static readonly List<MeshInstance3D> Stale = new();
	private static bool _split;

	/// <summary>Entities drawn per view in the last walk; reported on the debug channel.</summary>
	public static int PerViewCount => PerView.Count;

	/// <summary>The entries of player two's camera's book at the last walk.</summary>
	public static int BookCount => Book.Count;

	private sealed class Twin
	{
		public MeshInstance3D Node = null!;
		public ulong MaterialsKey;
		public readonly List<StringName> Parameters = new();
	}

	/// <summary>
	/// The two halves' opacities for an entity whose stated look is <paramref name="look"/> (its camera alpha player
	/// one's camera's) and whose camera alpha from player two's camera is <paramref name="second"/> (null when player
	/// two's camera has not faded it): player one's, player two's. Pure.
	/// </summary>
	public static (float First, float Second) Opacities(in HostBridge.EntityAppearance look, float? second) =>
		(Math.Clamp(look.Opacity, 0f, 1f), Math.Clamp(look.Alpha * (second ?? 1f), 0f, 1f));

	/// <summary>Whether the two halves draw an entity differently enough to need a drawing each. Pure.</summary>
	public static bool Differs(float first, float second) => MathF.Abs(first - second) >= Step;

	/// <summary>An entity starts a drawing per half when its halves differ by this much (two steps of the blend).</summary>
	public const float EnterStep = 2f / 255f;

	/// <summary>
	/// Walks an entity's halves must stay alike (under <see cref="Step"/>) before it is drawn once again: a camera's
	/// fade ramps by its distance and by 0.05 a frame back up (camerabase.lua), so at the edge of its radius the stated
	/// alpha crosses 1 back and forth, and each crossing would free the half's drawing and build it again.
	/// </summary>
	public const int LeaveWalks = 15;

	/// <summary>
	/// Whether an entity is drawn per half this walk, and the run of alike walks to carry: it starts at
	/// <see cref="EnterStep"/>, and once started stays until its halves have been alike for <see cref="LeaveWalks"/>
	/// walks in a row. Pure.
	/// </summary>
	public static (bool PerView, int Alike) Hold(bool perView, int alike, float first, float second)
	{
		float apart = MathF.Abs(first - second);
		if (!perView)
		{
			return (apart >= EnterStep, 0);
		}

		if (apart >= Step)
		{
			return (true, 0);
		}

		int run = alike + 1;
		return run >= LeaveWalks ? (false, 0) : (true, run);
	}

	/// <summary>A drawn body's Transparency for an opacity: blended below 0.999 (Engine_EntityAppearance). Pure.</summary>
	public static float TransparencyFor(float opacity) => opacity < 0.999f ? 1f - opacity : 0f;

	/// <summary>
	/// Main thread, once per entity walk before its entities: whether two views are drawn, and player two's
	/// camera's book. Drops every per-view drawing when the screen has one view.
	/// </summary>
	public static void BeginWalk()
	{
		_split = Refii.Core.Engine_Views.Split;
		if (!_split)
		{
			_reportedAny = false;
			Book.Clear();
			if (PerView.Count > 0 || Twins.Count > 0 || Overridden.Count > 0)
			{
				DropAll();
			}

			return;
		}

		HostBridge.PartnerCameraAlphas(Book);
		Purge();
		bool any = PerView.Count > 0;
		if (any != _reportedAny)
		{
			_reportedAny = any;
			GD.Print(any
				? $"[view-fade] each half at its own camera's fade: {PerView.Count} entity(s) drawn per view, player two's camera fades {Book.Count}; newest {_lastNew}"
				: "[view-fade] both halves draw every entity alike again");
		}
	}

	private static bool _reportedAny;
	private static string _lastNew = string.Empty;

	/// <summary>
	/// Main thread, per entity, after <see cref="Engine_EntityAppearance.Apply"/>: draws <paramref name="node"/> at
	/// each half's camera's fade while two views are drawn. Free when the screen has one view.
	/// </summary>
	public static void Sync(MeshInstance3D node, uint address, in HostBridge.EntityAppearance look, bool principal)
	{
		if (!_split)
		{
			return;
		}

		HostBridge.EntityAppearance visual = look.VisualOnly;
		float? second = Book.TryGetValue(address, out float alpha) ? alpha : null;
		(float first, float two) = Opacities(visual, second);
		bool drawnTwo = visual.Drawable && (two > 1e-4f || (principal && visual.Alpha >= 1e-4f));
		bool was = PerView.ContainsKey(node);
		(bool perView, int alike) = node.Visible ? Hold(was, AlikeWalks.GetValueOrDefault(node), first, two) : (false, 0);
		if (!perView)
		{
			if (was)
			{
				++Left;
			}

			AlikeWalks.Remove(node);
			Release(node);
			return;
		}

		if (!was)
		{
			++Entered;
		}

		AlikeWalks[node] = alike;

		if (!PerView.ContainsKey(node))
		{
			_lastNew = $"{address:X8} '{node.Mesh?.ResourceName}' {(node.Skin is null ? "static" : "rigged")} " +
				$"opacity {first:0.00} | {two:0.00}{(second is null ? "" : " (player two's camera)")}";
		}

		PerView[node] = (TransparencyFor(first), drawnTwo ? TransparencyFor(two) : 1f,
			TransparencyFor(first) > 0f && first >= Step, drawnTwo && TransparencyFor(two) > 0f && two >= Step);

		if (Engine_ViewTwin.Narrows(node, out bool firstTwin, out bool secondTwin))
		{
			// A rig's hidden bones own its views: their twins take each view's fade (CopyLook), and the body takes
			// the fade of the one view it still draws.
			DropTwin(node);
			if (firstTwin && !secondTwin)
			{
				SetBody(node, 1);
			}
			else
			{
				RestoreBody(node);
			}

			return;
		}

		RestoreBody(node);
		if (!Twins.TryGetValue(node, out Twin? twin) || !GodotObject.IsInstanceValid(twin.Node))
		{
			twin = Make(node);
			Twins[node] = twin;
		}

		Engine_ViewTwin.CopyLook(node, twin.Node, 1, ref twin.MaterialsKey, twin.Parameters);
		if (twin.Node.Visible != drawnTwo)
		{
			twin.Node.Visible = drawnTwo;
		}

		uint layers = Engine_ViewHides.TwinLayers(1, node.Layers);
		if (twin.Node.Layers != layers)
		{
			twin.Node.Layers = layers;
		}

		RenderingServer.InstanceSetLayerMask(node.GetInstance(), Engine_ViewHides.BodyLayers(false, true, node.Layers));
	}

	/// <summary>
	/// Whether the entity at <paramref name="address"/> needs a node of its own this walk: player two's camera fades
	/// it while two views are drawn, so its halves may differ (a batch or a married twin draws every view alike).
	/// </summary>
	public static bool WantsOwnNode(uint address) => _split && Book.ContainsKey(address);

	/// <summary>
	/// The Transparency view <paramref name="view"/> draws <paramref name="body"/> at: its half's camera's fade while
	/// the two differ, else the body's own.
	/// </summary>
	public static float TransparencyIn(MeshInstance3D body, int view) =>
		PerView.TryGetValue(body, out var fade) ? (view == 0 ? fade.First : fade.Second) : body.Transparency;

	/// <summary>
	/// The overlay view <paramref name="view"/>'s drawing of <paramref name="body"/> carries: the body's, with the
	/// kept depth (entity_fade_depth) where that view's fade blends.
	/// </summary>
	public static Material? OverlayIn(MeshInstance3D body, int view)
	{
		if (!PerView.TryGetValue(body, out var fade))
		{
			return body.MaterialOverlay;
		}

		return Engine_EntityAppearance.OverlayFor(body, view == 0 ? fade.Blended0 : fade.Blended1);
	}

	private static Twin Make(MeshInstance3D body)
	{
		var node = new MeshInstance3D
		{
			Name = NodeName,
			Mesh = body.Mesh,
			Skin = body.Skin,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		body.AddChild(node);
		if (body.Skin is not null && body.GetNodeOrNull(body.Skeleton) is Skeleton3D skeleton)
		{
			node.Skeleton = node.GetPathTo(skeleton);
		}

		Refii.Core.Engine_InstanceParams.Watch(body);
		return new Twin { Node = node };
	}

	/// <summary>The body draws in its one remaining view at that view's fade.</summary>
	private static void SetBody(MeshInstance3D body, int view)
	{
		float transparency = TransparencyIn(body, view);
		if (body.Transparency != transparency)
		{
			body.Transparency = transparency;
		}

		Material? overlay = OverlayIn(body, view);
		if (body.MaterialOverlay != overlay)
		{
			body.MaterialOverlay = overlay;
		}

		Overridden.Add(body);
	}

	/// <summary>The body back to the look Engine_EntityAppearance gave it (player one's camera's fade).</summary>
	private static void RestoreBody(MeshInstance3D body)
	{
		if (Overridden.Remove(body) && GodotObject.IsInstanceValid(body))
		{
			Engine_EntityAppearance.Reapply(body);
		}
	}

	private static void DropTwin(MeshInstance3D body)
	{
		if (!Twins.Remove(body, out Twin? twin))
		{
			return;
		}

		if (GodotObject.IsInstanceValid(twin.Node))
		{
			twin.Node.QueueFree();
		}

		if (GodotObject.IsInstanceValid(body) && !Engine_ViewTwin.Narrows(body, out _, out _))
		{
			RenderingServer.InstanceSetLayerMask(body.GetInstance(), body.Layers);
		}
	}

	/// <summary>Draws <paramref name="body"/> in every view at its own look again.</summary>
	public static void Release(MeshInstance3D body)
	{
		if (PerView.Count == 0 && Twins.Count == 0 && Overridden.Count == 0)
		{
			return;
		}

		PerView.Remove(body);
		DropTwin(body);
		RestoreBody(body);
	}

	private static void DropAll()
	{
		Stale.Clear();
		Stale.AddRange(PerView.Keys);
		Stale.AddRange(Twins.Keys);
		Stale.AddRange(Overridden);
		foreach (MeshInstance3D body in Stale)
		{
			Release(body);
		}

		PerView.Clear();
		AlikeWalks.Clear();
		Stale.Clear();
	}

	/// <summary>Forgets bodies that were freed (their twins went with them).</summary>
	private static void Purge()
	{
		Stale.Clear();
		foreach (MeshInstance3D body in PerView.Keys)
		{
			if (!GodotObject.IsInstanceValid(body))
			{
				Stale.Add(body);
			}
		}

		foreach (MeshInstance3D body in Twins.Keys)
		{
			if (!GodotObject.IsInstanceValid(body))
			{
				Stale.Add(body);
			}
		}

		foreach (MeshInstance3D body in Stale)
		{
			PerView.Remove(body);
			Twins.Remove(body);
			Overridden.Remove(body);
			AlikeWalks.Remove(body);
		}

		Stale.Clear();
	}
}
