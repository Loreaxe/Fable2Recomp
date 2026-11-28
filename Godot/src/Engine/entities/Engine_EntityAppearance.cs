using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Entities;

/// <summary>
/// Draws an entity the way its renderable's appearance setters state it: faded, hidden,
/// tinted, rescaled.
/// </summary>
/// <remarks>
/// The look is <see cref="HostBridge.EntityAppearance"/>, taken at the guest's setters. The
/// original renders a partial alpha as a CONSTANT blend, never a dither or a dissolve texture
/// (`sub_822251D8`): effective alpha = material+0x44 × the stated opacity, and below 0.999
/// (`flt_83320A70`) SrcBlend = BLENDFACTOR, DestBlend = INVBLENDFACTOR, BLENDFACTOR = that alpha.
/// Our opaque entity passes write no ALPHA, so <see cref="GeometryInstance3D.Transparency"/> is the
/// same `src·a + dst·(1−a)`; a pass that writes its own ALPHA multiplies it by the fade it enters
/// with (the modern translucent pass, the classic glass and later light-group passes).
///
/// A CUTOUT KEEPS ITS CUT. On the faded opaque pass c26 stays (1, 1, 1, 1) (0x82225254..
/// 0x82225288, stored at 0x82225BA0) and ALPHAREF (0x82225AC4) and ALPHATESTENABLE (0x82225AEC..
/// 0x82225B68: its r5 +0x51) are set without reference to the opacity, so the test sees the
/// texture's own alpha and only the blend takes the opacity: hair and hats fade with the body and
/// are gone at 0, never dissolved and never dithered. Our cutouts discard in the shader and write
/// no ALPHA, so Godot's fade reaches them the same way.
///
/// THE FUR SHELLS FADE WITH THE BODY — A REMASTER CHOICE, NOT THE GUEST'S. The guest's shell
/// item carries the opacity (item+0x20, `sub_822A2940` from `sub_82219AB8` 0x82219FA8 `fmr f1,
/// f31`; disc 0x8221A0D8) but its pass never reads it: ALPHABLENDENABLE 1, ALPHATESTENABLE 1,
/// ALPHAREF 16, ALPHAFUNC GREATEREQUAL, ZWRITEENABLE 1 over the default SrcBlend ONE / DestBlend
/// ZERO (`Render_LodMeshDraw_INFERRED` 0x821F1A90..0x821F1CFC, disc `sub_821F1578` 0x821F1958..
/// 0x821F1BC4; defaults `Render_ApplyDefaultRenderStates_INFERRED`), so on the 360 a faded dog
/// keeps its whole coat over a blended body. Here the shells take the body's fade (<see
/// cref="FadeOf"/>) and its kept depth, the cut staying on the slice's own alpha; they go with
/// the colour pass (<see cref="IsSubmitted"/>) as the guest's do.
///
/// The shadow and depth passes (pass types 0, 1, 7) skip the alpha multiply, so a fading body
/// keeps its full shadow — which the cast-only proxy already does.
///
/// Not drawn at all — SetAsDrawable(0), or the product not above 1e-4 — the guest releases the
/// proxies: no body, no shadow. Hiding the node does both; the shadow skeletons mirror it.
///
/// DISABLEALPHADEPTHWRITES (the definition's, +192 via vf8 `sub_825720A8`): every replication
/// then posts model command 16 with alpha reference 255 (`sub_82572C18` 0x82572c78..0x82572c94),
/// which the twin keeps at +228 (0x822d9e7c) in place of its constructed 254 and binds as
/// ALPHAREF, GREATER, on its alpha-tested colour draws of a surface that is not a cutout only
/// (`sub_822251D8` 0x82225ac4) and its depth-only draws (`sub_822015B8` 0x822016fc). Nothing is
/// greater than 255, so those draws write no colour and no depth and a translucent surface shows
/// its blended draw alone. The node carries it as the instance uniform `entity_no_alpha_depth`
/// (standard_material_body), which discards the opaque pass of each surface keyed to the twin's
/// reference. No shipped definition sets it; the classic shaders do not read it yet.
/// </remarks>
public static class Engine_EntityAppearance
{
    /// <summary>What was last applied to each node whose look is not the constructor's.</summary>
    private static readonly Dictionary<MeshInstance3D, HostBridge.EntityAppearance> Applied =
        new(ReferenceEqualityComparer.Instance);

    private static readonly StringName TintParameter = "entity_tint";

    /// <summary>The twin's alpha reference at 255 (<see cref="HostBridge.EntityAppearance.NoAlphaDepthWrites"/>).</summary>
    private static readonly StringName NoAlphaDepthParameter = "entity_no_alpha_depth";

    /// <summary>
    /// The tints <see cref="Apply"/> last wrote on each node, modern then classic (null where it
    /// wrote none), for <see cref="MirrorToCloth"/>.
    /// </summary>
    /// <remarks>
    /// Held here rather than read back: `GetInstanceShaderParameter` is a value-returning server
    /// call, and with Godot's separate render thread it waits until that thread has finished the
    /// frame it is drawing. A clothed body mirrors its look on every change — each frame of a
    /// fade — so the read cost a whole render frame of overlap per fading entity.
    /// </remarks>
    private static readonly Dictionary<GeometryInstance3D, (Vector4 Modern, Vector4? Classic)> Tints =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>The 7e3 scene ceiling over the exposure at rest: 31.875 / 4.</summary>
    private const float SceneCeilingTint = 31.875f / 4f;

    /// <summary>Nodes drawing the saturation-mask overlay; read by the post-process to skip its pass.</summary>
    private static readonly HashSet<MeshInstance3D> Masked = new(ReferenceEqualityComparer.Instance);

    /// <summary>The overlay each node carries: 0 none, else <see cref="OverlayMask"/> | <see cref="OverlayDepth"/>.</summary>
    private static readonly Dictionary<MeshInstance3D, int> Overlays = new(ReferenceEqualityComparer.Instance);

    private const int OverlayMask = 1;
    private const int OverlayDepth = 2;
    private static readonly ShaderMaterial?[] OverlayMaterials = new ShaderMaterial?[4];

    /// <summary>How many entity nodes carry the saturation mask right now.</summary>
    public static int MaskedCount { get; private set; }

    private static bool _lastSaturationMaskActive;

    /// <summary>Opacity at or above which the guest draws without blending (`flt_83320A70`).</summary>
    private const float OpaqueFrom = 0.999f;

    /// <summary>
    /// Opacity below which no pixel shows: the title's BLENDFACTOR is `fctiwz(opacity x 255)`
    /// (`sub_822251D8` 0x822257CC..0x822257E0) and Godot's instance fade `uint(fade x 255)`
    /// (render_forward_clustered.cpp), both 0 under one step.
    /// </summary>
    private const float InvisibleBelow = 1f / 255f;

    /// <summary>Entities whose look is not the constructor's; reset by the walk that feeds this.</summary>
    public static int StatedThisWalk;

    /// <summary>Of those, how many are faded, and how many are not drawn.</summary>
    public static int FadedThisWalk;

    public static int HiddenThisWalk;

    public static void BeginWalk()
    {
        StatedThisWalk = 0;
        FadedThisWalk = 0;
        HiddenThisWalk = 0;
        SyncSaturationMaskState();
    }

    /// <summary>
    /// Attaches or removes <c>OverlayMask</c> on tracked <see cref="Masked"/> entity nodes whenever
    /// <see cref="Refii.Lighting.Engine_PostFxParams.IsSaturationMaskActive"/> changes (e.g. entering
    /// or leaving a theme that states <c>SaturationMasked</c> / <c>BrightnessMasked</c>, or dropping the load cover).
    /// </summary>
    public static void SyncSaturationMaskState()
    {
        bool active = Refii.Lighting.Engine_PostFxParams.IsSaturationMaskActive;
        if (active == _lastSaturationMaskActive)
        {
            return;
        }

        _lastSaturationMaskActive = active;
        if (Masked.Count == 0)
        {
            return;
        }

        List<MeshInstance3D>? stale = null;
        foreach (MeshInstance3D node in Masked)
        {
            if (!GodotObject.IsInstanceValid(node))
            {
                stale ??= new List<MeshInstance3D>();
                stale.Add(node);
                continue;
            }

            Overlays.TryGetValue(node, out int current);
            int updated = active ? (current | OverlayMask) : (current & ~OverlayMask);
            if (updated == current)
            {
                continue;
            }

            node.MaterialOverlay = OverlayMaterial(updated);
            if (updated == 0)
            {
                Overlays.Remove(node);
            }
            else
            {
                Overlays[node] = updated;
            }
        }

        if (stale is not null)
        {
            foreach (MeshInstance3D dead in stale)
            {
                Masked.Remove(dead);
                Overlays.Remove(dead);
            }

            MaskedCount = Masked.Count;
        }
    }

    /// <summary>Whether a look needs the node itself: a batch or a married twin cannot carry it.</summary>
    public static bool NeedsOwnNode(in HostBridge.EntityAppearance look) => !look.IsConstructed;

    /// <summary>
    /// Applies the look to <paramref name="node"/>. Idempotent and free when nothing changed; the
    /// common case — a constructed look on a node that never had another — touches no engine
    /// state. The caller has already taken the node out of any batch and any twin marriage.
    /// </summary>
    private static readonly StringName ClassicTintParameter = "ps_g_StandardMaterialTintColour";

    /// <summary>
    /// Returns the modern tint vector for <paramref name="node"/> from the C# appearance cache
    /// without querying <c>RenderingServer</c>. Returns <see cref="Vector4.One"/> when constructed.
    /// </summary>
    public static Vector4 GetModernTint(MeshInstance3D node)
    {
        if (Applied.TryGetValue(node, out HostBridge.EntityAppearance look))
        {
            return new Vector4(
                Mathf.Min(look.TintLanes.X, SceneCeilingTint),
                Mathf.Min(look.TintLanes.Y, SceneCeilingTint),
                Mathf.Min(look.TintLanes.Z, SceneCeilingTint),
                1f);
        }

        return Vector4.One;
    }

    /// <summary>
    /// Whether <paramref name="node"/>'s colour pass is built (<see
    /// cref="HostBridge.EntityAppearance.Submitted"/>), from the appearance cache; true when
    /// constructed. The fur shells draw by it.
    /// </summary>
    public static bool IsSubmitted(MeshInstance3D node) =>
        !Applied.TryGetValue(node, out HostBridge.EntityAppearance look) || look.Submitted;

    /// <summary>
    /// The fade a drawn <paramref name="node"/>'s body was given, from the appearance cache: its
    /// Transparency, and whether it carries the kept depth (the depth overlay). (0, false) when
    /// constructed. The fur shells take both (the remaster's choice, see the remarks).
    /// </summary>
    public static (float Transparency, bool Depth) FadeOf(MeshInstance3D node)
    {
        if (!Applied.TryGetValue(node, out HostBridge.EntityAppearance look))
        {
            return (0f, false);
        }

        bool depth = Overlays.TryGetValue(node, out int overlay) && (overlay & OverlayDepth) != 0;
        return (TransparencyFor(Mathf.Clamp(look.Opacity, 0f, 1f)), depth);
    }

    /// <summary>A drawn body's Transparency for a clamped opacity: blended below <see cref="OpaqueFrom"/>.</summary>
    private static float TransparencyFor(float opacity) => opacity < OpaqueFrom ? 1f - opacity : 0f;

    /// <summary>
    /// The body's fade and tint onto its cloth. The guest draws the cloth as part of the body, so
    /// it takes the same c49 tint and the same blend; here the cloth is a child node of its own
    /// and would otherwise stay untinted and opaque under a fading, tinted body.
    /// </summary>
    public static void MirrorToCloth(GeometryInstance3D node)
    {
        if (node.GetNodeOrNull(Refii.Physics.Engine_ClothBody.NodeName) is not GeometryInstance3D cloth)
        {
            return;
        }
        cloth.Transparency = node.Transparency;
        // A node never tinted holds no value, and Nil puts the cloth's back to the default too.
        bool tinted = Tints.TryGetValue(node, out (Vector4 Modern, Vector4? Classic) tint);
        ClothTint written = ClothTints.GetOrCreateValue(cloth);
        written.Modern = MirrorTint(cloth, TintParameter, tinted ? tint.Modern : null, written.Modern);
        if (Refii.Models.Engine_ClassicMaterial.Active)
        {
            written.Classic = MirrorTint(cloth, ClassicTintParameter, tinted ? tint.Classic : null, written.Classic);
        }
    }

    /// <summary>Which tints this class has written on each cloth, so an untinted cloth is not reset.</summary>
    private sealed class ClothTint
    {
        public bool Modern;
        public bool Classic;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GeometryInstance3D, ClothTint> ClothTints = new();

    /// <summary>
    /// One tint onto a cloth; returns whether the cloth now holds one of ours.
    /// </summary>
    /// <remarks>
    /// NEUTRAL WHITE, NEVER NIL. Writing <c>default(Variant)</c> (nil) into
    /// <c>set_instance_shader_parameter</c> forces Godot's separate render thread to sync
    /// synchronously back to the main thread for the shader's default lookup (measured up to
    /// 28 ms per stall in Bowerstone Market). Both modern (<c>entity_tint</c>) and classic
    /// (<c>ps_g_StandardMaterialTintColour</c>) shaders initialize their tint uniforms to
    /// <c>vec4(1.0)</c>, so writing <see cref="Vector4.One"/> resets the cloth with zero sync.
    /// </remarks>
    private static bool MirrorTint(GeometryInstance3D cloth, StringName parameter, Vector4? tint, bool held)
    {
        if (tint is { } value)
        {
            cloth.SetInstanceShaderParameter(parameter, value);
            return true;
        }

        if (held)
        {
            cloth.SetInstanceShaderParameter(parameter, Vector4.One);
        }
        return false;
    }

    public static void Apply(MeshInstance3D node, in HostBridge.EntityAppearance look, bool principal)
    {
        HostBridge.EntityAppearance visual = look.VisualOnly;
        if (Applied.Count == 0 && visual.IsConstructed)
        {
            return;
        }

        bool had = Applied.TryGetValue(node, out HostBridge.EntityAppearance was);
        if (!had && visual.IsConstructed)
        {
            return;
        }

        bool drawn = visual.Drawn(principal);
        if (!visual.IsConstructed)
        {
            ++StatedThisWalk;
            if (!drawn)
            {
                ++HiddenThisWalk;
            }
            else if (visual.Opacity < OpaqueFrom)
            {
                ++FadedThisWalk;
            }
        }

        if (had && was == visual)
        {
            return;
        }

        if (!GodotObject.IsInstanceValid(node))
        {
            Applied.Remove(node);
            Tints.Remove(node);
            return;
        }

        float opacity = Mathf.Clamp(visual.Opacity, 0f, 1f);
        node.Transparency = drawn ? TransparencyFor(opacity) : 0f;
        node.Visible = drawn;

        // The tint's lanes in the guest's memory order are the shader's x, y, z (`sub_82573570`
        // packs its bytes 2, 1, 0, 3 into +80, +84, +88, +92, and `sub_82220CA8` stores +80..
        // to c49 in order), and `.w` is read by no shader. A corpse is {103,103,103,255}: grey.
        if (!had || was.TintLanes != visual.TintLanes)
        {
            // THE MODERN TINT IS CAPPED AT THE XBOX'S SCENE CEILING. The title's scene target is
            // 7e3 (`"1280x720 2xMSAA Horizontal Split (PIXEL_FORMAT_A2B10G10R10F_EDRAM)"`), whose
            // largest value is 31.875, and its materials write lit × tint × exposure (c20.w,
            // 4 at rest). qo240's ghosts state (0, 1000, 100000)/255 — blue 392 — and read as a
            // faint blue only because the target saturates; our FP16 scene does not, and the
            // ghost became a blinding glow. Capping the lane at 31.875 / 4 keeps every tint the
            // title can actually show and touches only the over-range ones (qo240, qo900 ghosts,
            // qo860/qo170/qo890 at 9..12). Classic clamps the export itself instead.
            var modern = new Vector4(
                Mathf.Min(visual.TintLanes.X, SceneCeilingTint),
                Mathf.Min(visual.TintLanes.Y, SceneCeilingTint),
                Mathf.Min(visual.TintLanes.Z, SceneCeilingTint), 1f);
            Refii.Core.Engine_InstanceParams.Set(node, TintParameter, modern);
            // The classic shaders read the same tint as the title's PS c49 (the instance's
            // +240, which this appearance setter fills).
            Vector4? classic = null;
            if (Refii.Models.Engine_ClassicMaterial.Active)
            {
                classic = new Vector4(visual.TintLanes.X, visual.TintLanes.Y, visual.TintLanes.Z, 1f);
                Refii.Core.Engine_InstanceParams.Set(node, ClassicTintParameter, classic.Value);
            }
            else if (Tints.TryGetValue(node, out (Vector4 Modern, Vector4? Classic) held))
            {
                // Not written this time, so the node still holds the earlier one.
                classic = held.Classic;
            }
            Tints[node] = (modern, classic);
        }

        // Written only when it differs from what the node holds: a node never stated holds false.
        if (visual.NoAlphaDepthWrites != (had && was.NoAlphaDepthWrites))
        {
            Refii.Core.Engine_InstanceParams.Set(node, NoAlphaDepthParameter, visual.NoAlphaDepthWrites);
        }

        if (visual.IsConstructed)
        {
            Applied.Remove(node);
            Tints.Remove(node);
        }
        else
        {
            Applied[node] = visual;
        }

        MirrorToCloth(node);

        // THE OVERLAY: SetSaturationMask (+196) marks the node's pixels for the post-process,
        // and a faded node writes the depth its blend tests against (entity_fade_depth). One
        // material carries whichever the look needs.
        bool masked = drawn && visual.SaturationMask;
        if (masked != Masked.Contains(node))
        {
            if (masked)
            {
                Masked.Add(node);
            }
            else
            {
                Masked.Remove(node);
            }

            MaskedCount = Masked.Count;
        }

        // NO DEPTH AT NO OPACITY (a principal under camera alpha 0: first person, a point-of-view
        // shot, a full pull-in). The depth exists so only the nearest layer of a blended body
        // shows; with no layer showing it would only stamp the whole body's depth, unculled and
        // untested, where the title alpha-tests its cutouts and culls every standard draw CCW
        // (`sub_8221BD40`) — from a camera inside the head, over every transparent draw behind it.
        SyncSaturationMaskState();
        bool blended = node.Transparency > 0f && opacity >= InvisibleBelow;
        bool maskOverlay = masked && _lastSaturationMaskActive;
        int overlay = (maskOverlay ? OverlayMask : 0) | (blended ? OverlayDepth : 0);
        Overlays.TryGetValue(node, out int carried);
        if (overlay != carried)
        {
            node.MaterialOverlay = OverlayMaterial(overlay);
            if (overlay == 0)
            {
                Overlays.Remove(node);
            }
            else
            {
                Overlays[node] = overlay;
            }
        }
    }

    /// <summary>
    /// The placement with the stated SCALE (+108, SetScale) folded in: uniform, about the entity's
    /// origin — the proxy builds its world matrix as each rotation row times the scale, then the
    /// translation (`sub_8220B740`, 0x8220b7fc..0x8220b828). Not interpolated: the block's
    /// previous scale is written once and never read.
    /// </summary>
    public static Transform3D Scaled(Transform3D placed, in HostBridge.EntityAppearance look)
    {
        float scale = look.Scale;
        if (scale == 1f || !float.IsFinite(scale) || scale <= 0f)
        {
            return placed;
        }

        placed.Basis = placed.Basis.Scaled(new Vector3(scale, scale, scale));
        return placed;
    }

    /// <summary>The node is going away.</summary>
    /// <summary>
    /// The overlay a drawing of <paramref name="node"/> carries at a fade of its own (Engine_ViewFade: one split
    /// half's camera's): the node's saturation mask as it stands, with the kept depth where <paramref name="blended"/>.
    /// </summary>
    internal static ShaderMaterial? OverlayFor(MeshInstance3D node, bool blended)
    {
        Overlays.TryGetValue(node, out int carried);
        return OverlayMaterial((carried & OverlayMask) | (blended ? OverlayDepth : 0));
    }

    /// <summary>
    /// Puts back the fade and overlay <see cref="Apply"/> last gave <paramref name="node"/> (Engine_ViewFade set
    /// another one view's for a while).
    /// </summary>
    internal static void Reapply(MeshInstance3D node)
    {
        float transparency = 0f;
        if (Applied.TryGetValue(node, out HostBridge.EntityAppearance look) && node.Visible)
        {
            transparency = TransparencyFor(Mathf.Clamp(look.Opacity, 0f, 1f));
        }

        if (node.Transparency != transparency)
        {
            node.Transparency = transparency;
        }

        Overlays.TryGetValue(node, out int carried);
        ShaderMaterial? overlay = OverlayMaterial(carried);
        if (node.MaterialOverlay != overlay)
        {
            node.MaterialOverlay = overlay;
        }
    }

    /// <summary>
    /// The overlay for a combination. The depth writer draws ahead of every other transparent
    /// surface (render priority -128), so the faded body's own surfaces then pass only where they
    /// are nearest; the mask alone draws with the rest.
    /// </summary>
    private static ShaderMaterial? OverlayMaterial(int overlay)
    {
        if (overlay == 0)
        {
            return null;
        }

        return OverlayMaterials[overlay] ??= new ShaderMaterial
        {
            Shader = GD.Load<Shader>(overlay switch
            {
                OverlayMask => "res://src/Engine/entities/entity_saturation_mask.gdshader",
                OverlayDepth => "res://src/Engine/entities/entity_fade_depth.gdshader",
                _ => "res://src/Engine/entities/entity_fade_depth_mask.gdshader",
            }),
            RenderPriority = (overlay & OverlayDepth) != 0 ? (int)Material.RenderPriorityMin : 0,
        };
    }

    public static void Forget(MeshInstance3D node)
    {
        Applied.Remove(node);
        Tints.Remove(node);
        Overlays.Remove(node);
        if (Masked.Remove(node))
        {
            MaskedCount = Masked.Count;
        }
    }

    /// <summary>Every node is going away (a level change).</summary>
    public static void ForgetAll()
    {
        Applied.Clear();
        Tints.Clear();
        Masked.Clear();
        Overlays.Clear();
        MaskedCount = 0;
        _lastSaturationMaskActive = false;
    }
}
