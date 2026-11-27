using System.Collections.Generic;
using Godot;

namespace Refii.Entities;

/// <summary>
/// Puts a highlighted entity's silhouette on <see cref="Engine_EntityHighlightLayer"/> under a
/// slot, and keeps the stated word the slot stands for. The bleed and the composite are the post
/// chain's.
/// </summary>
/// <remarks>
/// The colour is taken, not derived: `sub_822A3BF8` reads the player's target from component 159,
/// categorises it and ramps toward the category colour, already graded by `flt_8331947C[cat]` and
/// the fade; the finished bytes reach the host at the setter `sub_825738F0`.
///
/// The off state is a colour: `sub_82429BB0` clears a highlight by writing {0,0,0,255}, and
/// `GuestEntity.Highlighted` compares the bytes, as `sub_82572C18` itself does against
/// `dword_8331B158` (0x82572d30..0x82572d88).
///
/// There is no hull: the guest redraws the object's own geometry into the highlight buffer
/// (pass 5), so the proxy is the same mesh, skeleton and skin on a layer only the highlight
/// camera sees. It carries a SLOT rather than the colour, so a colour change during the ramp —
/// the setter is called on every frame of the fade-in — is a table write, never a material one.
/// </remarks>
public static class Engine_EntityHighlight
{
    /// <summary>A highlighted entity: its node, the stated word, the slot its proxies draw, and the
    /// guest entity it stands for (0 for a title's own stand-in).</summary>
    public readonly record struct Lit(Node3D Node, uint Word, int Slot, uint Entity = 0u);

    /// <summary>
    /// Whether view <paramref name="view"/> (0 the window or player one's, 1 player two's) draws a
    /// highlight targeted by <paramref name="targetedBy"/> (bit 0 player one, bit 1 player two).
    /// The guest raises ONE highlight per target for whichever player targets it
    /// (lhECTargeted_SetTargetedByPlayer): split, each view keeps its own player's; on one screen,
    /// or for a highlight no player's targeting states (0), every view draws it.
    /// </summary>
    public static bool DrawnInView(int view, bool split, int targetedBy) =>
        !split || (targetedBy & 3) == 0 || view is < 0 or > 1 || (targetedBy & (1 << view)) != 0;

    /// <summary>Marks the proxies we add, so they are never mistaken for the entity's own.</summary>
    private static StringName? _madeMeta;
    private static StringName MadeMeta => _madeMeta ??= new StringName("refii_highlight_silhouette");

    /// <summary>The slot a mesh's proxy currently draws; 0 when it has none.</summary>
    private static StringName? _slotMeta;
    private static StringName SlotMeta => _slotMeta ??= new StringName("refii_highlight_slot");

    /// <summary>The instance id of the Skeleton3D a proxy was bound to; 0 for none.</summary>
    private static StringName? _bonesMeta;
    private static StringName BonesMeta => _bonesMeta ??= new StringName("refii_highlight_bones");

    private const string ShaderPath = "res://src/Engine/entities/entity_highlight_silhouette.gdshader";

    /// <summary>Entities the guest is highlighting. Reset by the walk that feeds this.</summary>
    public static int HighlightedThisFrame;

    /// <summary>Proxies created, shown or hidden; a run total.</summary>
    public static long PassChanges;

    /// <summary>
    /// Lit proxies re-bound because the body's mesh, skin or skeleton changed under an unchanged
    /// slot (a re-rig); a run total, on the debug channel.
    /// </summary>
    public static long Rebinds;

    /// <summary>
    /// Every distinct highlight word the walk has seen. `000000FF` is the OFF colour; anything
    /// else is a live highlight. Without it "nothing is highlighted" reads the same whether the
    /// guest never set a colour or we are reading the wrong bytes.
    /// </summary>
    public static readonly HashSet<uint> SeenWords = new();

    /// <summary>Highlighted entities by node instance id. The id is the key because a freed node
    /// leaves a wrapper whose every member throws.</summary>
    private static readonly Dictionary<ulong, Lit> Lits = new();

    /// <summary>The wrappers of <see cref="Lits"/>, by reference: membership without an engine call.</summary>
    private static readonly HashSet<Node> LitNodes = new(ReferenceEqualityComparer.Instance);

    private static readonly ShaderMaterial?[] SlotMaterials = new ShaderMaterial?[Engine_EntityHighlightLayer.Slots + 1];

    private static readonly Dictionary<(int Slot, ulong TextureId), ShaderMaterial> AlphaMaterials = new();

    private const string AlphaScissorParam = "alpha_scissor_on";
    private const string AlbedoTexParam = "albedo_tex";
    private const string AlphaScissorThresholdParam = "alpha_scissor_threshold";

    public static void BeginWalk() => HighlightedThisFrame = 0;

    /// <summary>Forgets which subtrees are lit. For a level change, where the nodes go away.</summary>
    public static void Forget()
    {
        Lits.Clear();
        LitNodes.Clear();
        AlphaMaterials.Clear();
    }

    /// <summary>
    /// The live highlighted entities. Entries whose node has been freed are dropped here, which
    /// frees their slot.
    /// </summary>
    public static IEnumerable<Lit> Active()
    {
        List<ulong>? dead = null;
        foreach (KeyValuePair<ulong, Lit> pair in Lits)
        {
            if (GodotObject.InstanceFromId(pair.Key) is Node3D node && GodotObject.IsInstanceValid(node) &&
                node.IsInsideTree())
            {
                yield return pair.Value;
            }
            else
            {
                (dead ??= new List<ulong>()).Add(pair.Key);
            }
        }

        if (dead is not null)
        {
            foreach (ulong id in dead)
            {
                LitNodes.Remove(Lits[id].Node);
                Lits.Remove(id);
            }
        }
    }

    /// <summary>
    /// Puts the silhouette on every mesh under <paramref name="node"/>, or takes it off.
    /// Idempotent: the slot is stored per mesh and a proxy is only touched when it changes.
    /// </summary>
    public static void Apply(Node node, bool highlighted, Color colour, uint word = 0u, uint entity = 0u)
    {
        if (SeenWords.Count < 32)
        {
            SeenWords.Add(word);
        }

        // Nothing wanted and nothing applied: the common case, and no engine crossing. The
        // instance id is one, and this runs for every entity of every walk.
        if (!highlighted && (LitNodes.Count == 0 || !LitNodes.Contains(node)))
        {
            return;
        }

        ulong id = node.GetInstanceId();
        bool lit = Lits.TryGetValue(id, out Lit current);

        if (!highlighted || node is not Node3D body || !Engine_EntityHighlightLayer.Present)
        {
            // Nothing wanted and nothing applied: the common case, which must stay a dictionary
            // lookup.
            if (lit)
            {
                Walk(node, 0);
                Lits.Remove(id);
            }

            LitNodes.Remove(node);

            return;
        }

        ++HighlightedThisFrame;

        int slot = lit ? current.Slot : FreeSlot();
        if (slot == 0)
        {
            return;
        }

        Lits[id] = new Lit(body, word, slot, entity);
        LitNodes.Add(node);

        // Walked on every statement, not only the first: a mesh change re-rigs the body and the
        // new meshes have no proxy until this finds them.
        Walk(node, slot);
    }

    private static int FreeSlot()
    {
        for (int slot = 1; slot <= Engine_EntityHighlightLayer.Slots; ++slot)
        {
            bool taken = false;
            foreach (Lit l in Lits.Values)
            {
                if (l.Slot == slot)
                {
                    taken = true;
                    break;
                }
            }

            if (!taken)
            {
                return slot;
            }
        }

        return 0;
    }

    private static void Walk(Node node, int slot)
    {
        // `GetChildren` returns a copy in C#, so adding a proxy while this walks is safe.
        foreach (Node child in node.GetChildren())
        {
            Walk(child, slot);
        }

        // A cast-only mesh (Engine_ShadowProxy) is never drawn, so it has no outline of its own.
        if (node is not MeshInstance3D mesh || mesh.HasMeta(MadeMeta) || mesh.Mesh is null ||
            mesh.CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly)
        {
            return;
        }

        int have = mesh.HasMeta(SlotMeta) ? (int)mesh.GetMeta(SlotMeta) : 0;
        if (have == slot)
        {
            // The guest draws the outline in the model's own draw (`sub_82572C18` posts it with
            // the model command, 0x82572d28), so the proxy must stay on what the body draws. A
            // re-rig frees the body's Skeleton3D, builds another under the same name and hands
            // the body a new Skin; the proxy bound at Create would keep the old pair.
            if (slot != 0 && Existing(mesh) is { } bound && BindingStale(bound, mesh))
            {
                ++Rebinds;
                Fit(mesh, slot);
            }

            return;
        }

        mesh.SetMeta(SlotMeta, slot);
        ++PassChanges;
        Fit(mesh, slot);
    }

    private static void Fit(MeshInstance3D mesh, int slot)
    {
        MeshInstance3D? proxy = Existing(mesh);
        if (slot == 0)
        {
            if (proxy is not null)
            {
                proxy.Visible = false;
            }

            return;
        }

        proxy ??= Create(mesh);
        if (proxy is null)
        {
            return;
        }

        // What the body draws now: a dressed body swaps meshes when its outfit changes
        // (Engine_BodyVariants), and a stale copy would outline a garment it no longer wears.
        if (!ReferenceEquals(proxy.Mesh, mesh.Mesh))
        {
            proxy.Mesh = mesh.Mesh;
        }

        Bind(proxy, mesh);

        int surfaceCount = mesh.Mesh?.GetSurfaceCount() ?? 0;
        bool hasCutout = false;
        for (int s = 0; s < surfaceCount; ++s)
        {
            Material? source = mesh.GetSurfaceOverrideMaterial(s) ?? mesh.Mesh?.SurfaceGetMaterial(s);
            if (TryGetAlphaCutout(source, out _, out _))
            {
                hasCutout = true;
                break;
            }
        }

        if (!hasCutout)
        {
            for (int s = 0; s < surfaceCount; ++s)
            {
                proxy.SetSurfaceOverrideMaterial(s, null);
            }
            proxy.MaterialOverride = SlotMaterial(slot);
        }
        else
        {
            proxy.MaterialOverride = null;
            for (int s = 0; s < surfaceCount; ++s)
            {
                Material? source = mesh.GetSurfaceOverrideMaterial(s) ?? mesh.Mesh?.SurfaceGetMaterial(s);
                if (TryGetAlphaCutout(source, out Texture2D? albedo, out float threshold) && albedo is not null)
                {
                    proxy.SetSurfaceOverrideMaterial(s, AlphaSlotMaterial(slot, albedo, threshold));
                }
                else
                {
                    proxy.SetSurfaceOverrideMaterial(s, SlotMaterial(slot));
                }
            }
        }

        proxy.Visible = true;
    }

    public static float ClampCutoutThreshold(float srcThreshold) =>
        srcThreshold > 0.9f ? 0.5f : srcThreshold;

    public static bool TryGetAlphaCutout(
        Material? material, out Texture2D? albedo, out float threshold)
    {
        albedo = null;
        threshold = 0.5f;
        if (material is not ShaderMaterial sm)
        {
            return false;
        }

        if (sm.GetShaderParameter(AlphaScissorParam) is { VariantType: Variant.Type.Bool } sc && sc.AsBool())
        {
            albedo = sm.GetShaderParameter(AlbedoTexParam).As<Texture2D>();
            if (albedo is not null)
            {
                if (sm.GetShaderParameter(AlphaScissorThresholdParam) is { VariantType: Variant.Type.Float } th)
                {
                    threshold = ClampCutoutThreshold(th.AsSingle());
                }
                return true;
            }
        }

        return false;
    }

    private static ShaderMaterial AlphaSlotMaterial(int slot, Texture2D albedo, float threshold)
    {
        ulong texId = albedo.GetInstanceId();
        if (AlphaMaterials.TryGetValue((slot, texId), out ShaderMaterial? cached))
        {
            return cached;
        }

        var material = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
        material.SetShaderParameter("slot", (float)slot);
        material.SetShaderParameter(AlbedoTexParam, albedo);
        material.SetShaderParameter(AlphaScissorParam, true);
        material.SetShaderParameter(AlphaScissorThresholdParam, threshold);
        AlphaMaterials[(slot, texId)] = material;
        return material;
    }

    private static MeshInstance3D? Existing(MeshInstance3D mesh)
    {
        foreach (Node child in mesh.GetChildren())
        {
            if (child is MeshInstance3D proxy && proxy.HasMeta(MadeMeta))
            {
                return proxy;
            }
        }

        return null;
    }

    private static MeshInstance3D? Create(MeshInstance3D mesh)
    {
        var proxy = new MeshInstance3D
        {
            Name = "Highlight",
            Mesh = mesh.Mesh,

            // On the highlight layer and nowhere else: the main camera has this bit cleared.
            Layers = Engine_EntityHighlightLayer.LayerMask,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

        proxy.SetMeta(MadeMeta, true);

        // A child of the body: it dies with the body on a re-rig, inherits its transform, and is
        // hidden with it, so a garment `Engine_HiddenClusters` has taken away stops drawing too.
        mesh.AddChild(proxy);
        return proxy;
    }

    /// <summary>
    /// The body's skeleton and skin on the proxy, or the silhouette stands in the bind pose. The
    /// path is resolved from the proxy, which is in the tree; a prop has none. Setting the path
    /// re-resolves it even when it reads the same (`MeshInstance3D::set_skeleton_path`), which
    /// is what binds a new skeleton that took the old one's name.
    /// </summary>
    private static void Bind(MeshInstance3D proxy, MeshInstance3D mesh)
    {
        if (BodyBones(mesh) is { } bones)
        {
            proxy.Skeleton = proxy.GetPathTo(bones);
            proxy.Skin = mesh.Skin;
            proxy.SetMeta(BonesMeta, (long)bones.GetInstanceId());
        }
        else if (!proxy.Skeleton.IsEmpty || proxy.Skin is not null)
        {
            proxy.Skeleton = new NodePath();
            proxy.Skin = null;
            proxy.SetMeta(BonesMeta, 0L);
        }
    }

    private static Skeleton3D? BodyBones(MeshInstance3D mesh) =>
        !mesh.Skeleton.IsEmpty && mesh.GetNodeOrNull(mesh.Skeleton) is Skeleton3D bones ? bones : null;

    private static bool BindingStale(MeshInstance3D proxy, MeshInstance3D mesh) =>
        BindingStale(
            proxy.Mesh?.GetInstanceId() ?? 0ul, mesh.Mesh?.GetInstanceId() ?? 0ul,
            proxy.Skin?.GetInstanceId() ?? 0ul, mesh.Skin?.GetInstanceId() ?? 0ul,
            proxy.HasMeta(BonesMeta) ? (ulong)proxy.GetMeta(BonesMeta).AsInt64() : 0ul,
            BodyBones(mesh)?.GetInstanceId() ?? 0ul);

    /// <summary>
    /// Whether a proxy bound to (<paramref name="boundMesh"/>, <paramref name="boundSkin"/>,
    /// <paramref name="boundBones"/>) no longer draws what the body draws. Instance ids, 0 for none.
    /// </summary>
    public static bool BindingStale(ulong boundMesh, ulong bodyMesh, ulong boundSkin, ulong bodySkin,
        ulong boundBones, ulong bodyBones) =>
        boundMesh != bodyMesh || boundSkin != bodySkin || boundBones != bodyBones;

    /// <summary>One shared material per slot; every proxy of an entity draws the same one.</summary>
    private static ShaderMaterial SlotMaterial(int slot)
    {
        if (SlotMaterials[slot] is { } made)
        {
            return made;
        }

        var material = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
        material.SetShaderParameter("slot", (float)slot);
        SlotMaterials[slot] = material;
        return material;
    }
}
