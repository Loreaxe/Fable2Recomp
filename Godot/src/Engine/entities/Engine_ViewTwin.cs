using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Entities;

/// <summary>
/// A body drawn one way in one split view: a second MeshInstance3D on the body's own skeleton, with
/// its own Skin whose binds collapse the subtrees of the bones that view hides, drawn in that view
/// alone (<see cref="Engine_ViewHides"/>).
/// </summary>
/// <remarks>
/// <para>WHY A SECOND INSTANCE. Both split views render the one World3D in one frame from one set of
/// instances, so a bone pose — which every mesh on the skeleton reads — cannot differ between them.
/// What can differ per view is an instance's render layers against each view camera's cull mask
/// (Engine_ClassicViews keeps the split masks). So a view that must not see a subtree draws a twin,
/// and the body is drawn only in a view that hides nothing of its own (<see cref="DrawBody"/>), each
/// on <see cref="Refii.Models.Engine_ClassicViews.OnlyIn"/> layers that light it as the body's own
/// layers would. Both views hiding something is two twins and the body in neither.</para>
/// <para>THE BODY'S NODE LAYERS ARE NOT TOUCHED. Its <c>Layers</c> still state its zone and caster bits,
/// which the shadow proxy, the probe lighting and the classic casters read; only the server instance's
/// mask is narrowed (re-stated every update by the owner, since any write of <c>Layers</c> puts the
/// node's value back), and restored by <c>DrawBody(body, false, false)</c>.</para>
/// <para>THE COLLAPSE IS IN THE BIND. The rig's skin bind is <c>diag(s)·rest⁻¹</c> with the bone's morph
/// scale (Engine_EntitySkeleton.SetBoneScales); the twin's is that bind scaled by
/// <see cref="Shrunk"/> plus its subtree root's rest origin in the bone's rest frame
/// (<see cref="CollapsedBind"/>), so every collapsed vertex lands at the root joint as the bone
/// carries it — the root's own vertices exactly at its joint, as a pose scale on the root would put
/// them. A bone under two hidden roots collapses to the outer one (<see cref="Collapse"/>). The twin's
/// skin is copied again whenever the rig's changes. The skeleton is never written, so the shadow
/// proxy (its own skeleton) and every other mesh on the rig are unchanged.</para>
/// <para>THE LOOK IS THE BODY'S, AT ITS VIEW'S FADE. Mesh, materials, ranges and the instance shader
/// parameters are copied from the body on every update, and the fade is the one the twin's view draws the body at
/// (<see cref="Engine_ViewFade.TransparencyIn"/>: each split half its own camera's); shadows stay the proxy's (the
/// twin casts none).
/// Visibility follows the body: the twin is a child of the body's skeleton, under the body.</para>
/// </remarks>
public sealed class Engine_ViewTwin
{
	public const string NodeName = "ViewTwin";

	/// <summary>The collapse scale, the pose scale the single-view hiders use.</summary>
	public const float Shrunk = 0.001f;

	private readonly MeshInstance3D _body;
	private readonly MeshInstance3D _twin;
	private readonly Skin _source;
	private readonly Skin _skin;
	private readonly int[] _roots;
	private readonly bool[] _collapsed;
	private readonly Vector3[] _anchor;
	private bool _bindsDirty = true;
	private ulong _materialsKey;
	private readonly List<StringName> _parameters = new();

	/// <summary>Twins standing.</summary>
	public static int Active { get; private set; }

	private Engine_ViewTwin(MeshInstance3D body, MeshInstance3D twin, Skin source, Skin skin, int[] roots, bool[] collapsed, Vector3[] anchor)
	{
		_body = body;
		_twin = twin;
		_source = source;
		_skin = skin;
		_roots = roots;
		_collapsed = collapsed;
		_anchor = anchor;
		_source.Changed += OnSourceChanged;
		++Active;
	}

	/// <summary>
	/// A twin of <paramref name="body"/> on <paramref name="skeleton"/> with each bone of
	/// <paramref name="roots"/> and every bone under it collapsed to that root's joint; null when the
	/// body has no skin or no root is a bone of the skeleton.
	/// </summary>
	public static Engine_ViewTwin? Create(MeshInstance3D body, Skeleton3D skeleton, IReadOnlyList<int> roots)
	{
		int bones = skeleton.GetBoneCount();
		if (body.Skin is not { } source || bones == 0)
		{
			return null;
		}

		var parents = new int[bones];
		for (int b = 0; b < bones; b++)
		{
			parents[b] = skeleton.GetBoneParent(b);
		}

		int[] rootOf = Collapse(parents, roots);
		var collapsed = new bool[bones];
		var anchor = new Vector3[bones];
		bool any = false;
		for (int b = 0; b < bones; b++)
		{
			if (rootOf[b] >= 0)
			{
				collapsed[b] = any = true;
				anchor[b] = skeleton.GetBoneGlobalRest(b).AffineInverse() * skeleton.GetBoneGlobalRest(rootOf[b]).Origin;
			}
		}

		if (!any)
		{
			return null;
		}

		var skin = new Skin();
		skin.SetBindCount(source.GetBindCount());
		for (int i = 0; i < source.GetBindCount(); i++)
		{
			skin.SetBindBone(i, source.GetBindBone(i));
			StringName name = source.GetBindName(i);
			if (!name.IsEmpty)
			{
				skin.SetBindName(i, name);
			}
		}

		var twin = new MeshInstance3D
		{
			Name = NodeName,
			Mesh = body.Mesh,
			Skin = skin,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		skeleton.AddChild(twin);
		twin.Skeleton = twin.GetPathTo(skeleton);
		Refii.Core.Engine_InstanceParams.Watch(body);
		var made = new Engine_ViewTwin(body, twin, source, skin, Sorted(roots), collapsed, anchor);
		made.SyncBinds();
		return made;
	}

	/// <summary><paramref name="root"/> and every bone under it, by the parent table <paramref name="parents"/>.</summary>
	public static bool[] Subtree(IReadOnlyList<int> parents, int root)
	{
		var inside = new bool[parents.Count];
		for (int b = 0; b < parents.Count; b++)
		{
			for (int p = b, guard = 0; p >= 0 && p < parents.Count && guard <= parents.Count; p = parents[p], guard++)
			{
				if (p == root)
				{
					inside[b] = true;
					break;
				}
			}
		}

		return inside;
	}

	/// <summary>
	/// For each bone of the parent table <paramref name="parents"/>, the outermost of
	/// <paramref name="roots"/> it is or is under (the joint it collapses to), else -1.
	/// </summary>
	public static int[] Collapse(IReadOnlyList<int> parents, IReadOnlyList<int> roots)
	{
		var rootOf = new int[parents.Count];
		var isRoot = new bool[parents.Count];
		foreach (int root in roots)
		{
			if (root >= 0 && root < parents.Count)
			{
				isRoot[root] = true;
			}
		}

		for (int b = 0; b < parents.Count; b++)
		{
			rootOf[b] = -1;
			for (int p = b, guard = 0; p >= 0 && p < parents.Count && guard <= parents.Count; p = parents[p], guard++)
			{
				if (isRoot[p])
				{
					rootOf[b] = p;
				}
			}
		}

		return rootOf;
	}

	/// <summary>
	/// A collapsed bind: every vertex the bone carries to <paramref name="anchor"/> (the subtree root's
	/// rest origin in this bone's rest frame) give or take <see cref="Shrunk"/> of where
	/// <paramref name="bind"/> put it. Composes with the morph scale the bind already carries.
	/// </summary>
	public static Transform3D CollapsedBind(Transform3D bind, Vector3 anchor) =>
		new(Basis.FromScale(new Vector3(Shrunk, Shrunk, Shrunk)) * bind.Basis, bind.Origin * Shrunk + anchor);

	/// <summary>Whether this twin collapses exactly <paramref name="sortedRoots"/> (ascending, none repeated: a <see cref="Engine_ViewHides.Plan"/> view).</summary>
	public bool Collapses(ReadOnlySpan<int> sortedRoots) => sortedRoots.SequenceEqual(_roots);

	/// <summary>Keeps the twin the body's look and binds, drawn in <paramref name="view"/> alone.</summary>
	public void Update(int view)
	{
		if (!GodotObject.IsInstanceValid(_body) || !GodotObject.IsInstanceValid(_twin))
		{
			return;
		}

		if (_bindsDirty || _skin.GetBindCount() != _source.GetBindCount())
		{
			SyncBinds();
		}

		CopyLook(_body, _twin, view, ref _materialsKey, _parameters);
		uint twinLayers = Engine_ViewHides.TwinLayers(view, _body.Layers);
		if (_twin.Layers != twinLayers)
		{
			_twin.Layers = twinLayers;
		}
	}

	/// <summary>
	/// Draws <paramref name="body"/> in the views without a twin: view 0 has one when
	/// <paramref name="firstTwin"/>, view 1 when <paramref name="secondTwin"/>; neither puts it back in
	/// every view as its layers state (<see cref="Engine_ViewHides.BodyLayers"/>).
	/// </summary>
	public static void DrawBody(MeshInstance3D body, bool firstTwin, bool secondTwin)
	{
		if (firstTwin || secondTwin)
		{
			Narrowed[body] = (firstTwin, secondTwin);
		}
		else
		{
			Narrowed.Remove(body);
		}

		if (GodotObject.IsInstanceValid(body))
		{
			RenderingServer.InstanceSetLayerMask(body.GetInstance(), Engine_ViewHides.BodyLayers(firstTwin, secondTwin, body.Layers));
		}
	}

	/// <summary>The bodies <see cref="DrawBody"/> keeps out of a view, and which views have their twin.</summary>
	private static readonly Dictionary<MeshInstance3D, (bool First, bool Second)> Narrowed =
		new(ReferenceEqualityComparer.Instance);

	/// <summary>
	/// Whether <paramref name="body"/> stands a twin in either view (a rig's hidden bones), and which: its
	/// views are this class's, so a per-view fade reaches it through the twins (<see cref="Engine_ViewFade"/>).
	/// </summary>
	public static bool Narrows(MeshInstance3D body, out bool firstTwin, out bool secondTwin)
	{
		if (Narrowed.TryGetValue(body, out (bool First, bool Second) views) && GodotObject.IsInstanceValid(body))
		{
			(firstTwin, secondTwin) = views;
			return true;
		}

		firstTwin = secondTwin = false;
		return false;
	}

	/// <summary>The twin gone; the body's views are its owner's to restore (<see cref="DrawBody"/>).</summary>
	public void Release()
	{
		_source.Changed -= OnSourceChanged;
		if (GodotObject.IsInstanceValid(_twin))
		{
			_twin.QueueFree();
		}

		--Active;
	}

	private static int[] Sorted(IReadOnlyList<int> roots)
	{
		var sorted = new SortedSet<int>(roots);
		return [.. sorted];
	}

	private void OnSourceChanged() => _bindsDirty = true;

	private void SyncBinds()
	{
		_bindsDirty = false;
		int count = _source.GetBindCount();
		if (_skin.GetBindCount() != count)
		{
			_skin.SetBindCount(count);
			for (int i = 0; i < count; i++)
			{
				_skin.SetBindBone(i, _source.GetBindBone(i));
			}
		}

		for (int i = 0; i < count; i++)
		{
			Transform3D bind = _source.GetBindPose(i);
			int bone = _source.GetBindBone(i);
			_skin.SetBindPose(i, bone >= 0 && bone < _collapsed.Length && _collapsed[bone] ? CollapsedBind(bind, _anchor[bone]) : bind);
		}
	}

	/// <summary>
	/// Keeps <paramref name="twin"/> <paramref name="body"/>'s look as view <paramref name="view"/> draws it: mesh,
	/// materials, ranges and instance parameters copied; the fade the body takes in that view
	/// (<see cref="Engine_ViewFade.TransparencyIn"/>) with the overlay it needs. <paramref name="materialsKey"/> and
	/// <paramref name="parameters"/> are the caller's, naming the instance parameters once per material set.
	/// </summary>
	internal static void CopyLook(MeshInstance3D body, MeshInstance3D twin, int view, ref ulong materialsKey,
		List<StringName> parameters)
	{
		if (!ReferenceEquals(twin.Mesh, body.Mesh))
		{
			twin.Mesh = body.Mesh;
		}

		ulong key = 1469598103934665603ul;
		if (twin.MaterialOverride != body.MaterialOverride)
		{
			twin.MaterialOverride = body.MaterialOverride;
		}

		Material? overlay = Engine_ViewFade.OverlayIn(body, view);
		if (twin.MaterialOverlay != overlay)
		{
			twin.MaterialOverlay = overlay;
		}

		key = Fold(key, body.Mesh);
		key = Fold(key, body.MaterialOverride);
		key = Fold(key, body.MaterialOverlay);
		int surfaces = body.Mesh?.GetSurfaceCount() ?? 0;
		for (int s = 0; s < surfaces; s++)
		{
			Material? material = body.GetSurfaceOverrideMaterial(s);
			if (twin.GetSurfaceOverrideMaterial(s) != material)
			{
				twin.SetSurfaceOverrideMaterial(s, material);
			}

			key = Fold(key, material);
		}

		float transparency = Engine_ViewFade.TransparencyIn(body, view);
		if (twin.Transparency != transparency)
		{
			twin.Transparency = transparency;
		}

		if (twin.GIMode != body.GIMode)
		{
			twin.GIMode = body.GIMode;
		}

		if (twin.CustomAabb != body.CustomAabb)
		{
			twin.CustomAabb = body.CustomAabb;
		}

		if (twin.ExtraCullMargin != body.ExtraCullMargin)
		{
			twin.ExtraCullMargin = body.ExtraCullMargin;
		}

		if (twin.LodBias != body.LodBias)
		{
			twin.LodBias = body.LodBias;
		}

		if (twin.IgnoreOcclusionCulling != body.IgnoreOcclusionCulling)
		{
			twin.IgnoreOcclusionCulling = body.IgnoreOcclusionCulling;
		}

		if (twin.SortingOffset != body.SortingOffset)
		{
			twin.SortingOffset = body.SortingOffset;
		}

		if (twin.VisibilityRangeBegin != body.VisibilityRangeBegin || twin.VisibilityRangeEnd != body.VisibilityRangeEnd ||
			twin.VisibilityRangeBeginMargin != body.VisibilityRangeBeginMargin ||
			twin.VisibilityRangeEndMargin != body.VisibilityRangeEndMargin ||
			twin.VisibilityRangeFadeMode != body.VisibilityRangeFadeMode)
		{
			twin.VisibilityRangeBegin = body.VisibilityRangeBegin;
			twin.VisibilityRangeEnd = body.VisibilityRangeEnd;
			twin.VisibilityRangeBeginMargin = body.VisibilityRangeBeginMargin;
			twin.VisibilityRangeEndMargin = body.VisibilityRangeEndMargin;
			twin.VisibilityRangeFadeMode = body.VisibilityRangeFadeMode;
		}

		// The instance parameters the body's materials declare: named once per material set, then
		// copied every update (a fade or a tint moves them between frames).
		// The instance parameters the body's materials declare, named from the shaders' source (never the server's
		// list: Engine_InstanceUniformNames). On a new material set every one is copied, a value not held asked of
		// the server once (a returned value, so it waits); after that only the held ones, which are what moves.
		bool fresh = key != materialsKey;
		if (fresh)
		{
			materialsKey = key;
			parameters.Clear();
			NamesScratch.Clear();
			Refii.Core.Engine_InstanceUniformNames.OfNode(body, NamesScratch);
			parameters.AddRange(NamesScratch);
		}

		foreach (StringName name in parameters)
		{
			if (!fresh && !Refii.Core.Engine_InstanceParams.Holds(body, name))
			{
				continue;
			}

			Variant value = Refii.Core.Engine_InstanceParams.Get(body, name);
			if (value.VariantType != Variant.Type.Nil)
			{
				twin.SetInstanceShaderParameter(name, value);
			}
		}
	}

	private static readonly HashSet<StringName> NamesScratch = new();

	private static ulong Fold(ulong key, GodotObject? item) =>
		(key ^ (item is null ? 0ul : item.GetInstanceId())) * 1099511628211ul;
}
