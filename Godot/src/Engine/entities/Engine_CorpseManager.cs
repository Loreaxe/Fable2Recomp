using System;
using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Physics;

namespace Refii.Entities;

/// <summary>
/// Retains and steps host-side fallen corpse nodes after the guest frees their original
/// <c>CEntity</c> components, leaving nodes in-place in the scene tree so <see cref="Engine_ShadowProxy"/>
/// bindings stay intact, finishing active bone scatter, freezing settled cloth, and smoothly
/// fading out upon timeout or cap eviction.
/// </summary>
public sealed class Engine_CorpseManager
{
    private sealed class RetainedCorpse
    {
        public required MeshInstance3D Node;
        public required Engine_EntitySkeleton? Rig;
        public Engine_DisarticulatedRig? Shatter;
        public required HostBridge.EntityAppearance HeldLook;
        public float Age;
        public float FadeElapsed;
        public bool FadingOut;
        public bool ClothFrozen;
        public bool ShadowDropped;
    }

    private readonly List<RetainedCorpse> _corpses = new();
    private Engine_CorpsePolicy.CorpseSlotSummary[] _summaryScratch = Array.Empty<Engine_CorpsePolicy.CorpseSlotSummary>();

    /// <summary>Number of retained corpses currently managed on the host.</summary>
    public int Count => _corpses.Count;

    /// <summary>
    /// Adopts a qualifying retired corpse node in-place without reparenting so attached skeletons,
    /// cloth, and shadow proxies remain undisturbed.
    /// </summary>
    public void Adopt(
        MeshInstance3D node,
        Engine_EntitySkeleton? rig,
        Engine_DisarticulatedRig? shatter,
        HostBridge.EntityAppearance heldLook,
        float initialAge)
    {
        if (!GodotObject.IsInstanceValid(node))
        {
            return;
        }

        // The retired entity leaves the walk, and the walk is what takes an outline off
        // (GameEntities' Engine_EntityHighlight.Apply per entity): off now, or the kill frame's
        // target outline stays on the body for its whole retention.
        Engine_EntityHighlight.Apply(node, false, default);

        HostBridge.EntityAppearance clean = heldLook.VisualOnly;
        Vector4 tint = clean.TintLanes;
        if (!tint.IsFinite() || (tint.X == 0f && tint.Y == 0f && tint.Z == 0f && tint.W == 0f))
        {
            tint = Vector4.One;
        }

        float scale = float.IsFinite(clean.Scale) && clean.Scale > 0.05f ? clean.Scale : 1f;
        HostBridge.EntityAppearance opaqueLook = clean with
        {
            Alpha = 1f,
            CameraAlpha = 1f,
            Scale = scale,
            TintLanes = tint,
            Flags = clean.Flags | HostBridge.EntityAppearance.DrawableFlag,
        };

        Engine_EntityAppearance.Apply(node, opaqueLook, principal: false);
        node.Visible = true;

        if (shatter is not null)
        {
            SyncDisarticulatedBounds(node, shatter, node.GlobalTransform, force: true);
        }
        else if (rig is not null && GodotObject.IsInstanceValid(rig.Node))
        {
            ApplyCustomBounds(node, rig.Bounds(false).Grow(0.55f));
            Engine_ShadowSkeletons.SetEveryFrame(rig.Node, false);
            Engine_ShadowSkeletons.Placed(node);
        }

        _corpses.Add(new RetainedCorpse
        {
            Node = node,
            Rig = rig,
            Shatter = shatter,
            HeldLook = opaqueLook,
            Age = MathF.Max(0f, initialAge),
        });
    }

    /// <summary>
    /// Advances retained corpses, finishes any unsettled bone scatter, freezes settled cloth and
    /// shadows, enforces cap eviction, and fades expired corpses out smoothly over
    /// <see cref="Engine_CorpsePolicy.FadeDurationSeconds"/>.
    /// </summary>
    public void Step(float dt, Vector3 cameraPos, Engine_DisarticulatedRig.TrySampleGroundContact sampleGround)
    {
        if (_corpses.Count == 0)
        {
            return;
        }

        float stepDt = float.IsFinite(dt) && dt > 0f ? Math.Clamp(dt, 0f, 0.1f) : 0f;
        float lifetime = Engine_CorpsePolicy.LifetimeSeconds;
        int maxCorpses = Engine_CorpsePolicy.EffectiveMaxCount;

        int activeCount = 0;
        for (int i = _corpses.Count - 1; i >= 0; --i)
        {
            RetainedCorpse corpse = _corpses[i];
            if (!GodotObject.IsInstanceValid(corpse.Node))
            {
                ReleaseCorpse(corpse, queueFreeNode: false);
                _corpses.RemoveAt(i);
                continue;
            }

            corpse.Age += stepDt;

            if (corpse.Shatter is { } shatter)
            {
                if (corpse.Rig is { } rig && GodotObject.IsInstanceValid(rig.Node))
                {
                    Transform3D corpseWorld = corpse.Node.GlobalTransform;
                    shatter.Step(stepDt, corpseWorld, sampleGround, default, 0);
                    rig.ApplyModelSpacePoses(shatter.ModelPoses);
                    bool settledNow = shatter.AllAsleep || shatter.Elapsed >= 10.0f;
                    SyncDisarticulatedBounds(
                        corpse.Node,
                        shatter,
                        corpseWorld,
                        force: settledNow,
                        hysteresis: settledNow ? 0.25f : 0.50f);
                    if (settledNow)
                    {
                        Engine_ShadowSkeletons.SetEveryFrame(rig.Node, false);
                        Engine_ShadowSkeletons.Placed(corpse.Node);
                        corpse.Shatter = null;
                    }
                }
                else
                {
                    corpse.Shatter = null;
                }
            }

            if (!corpse.ClothFrozen && corpse.Shatter is null && corpse.Age >= 1.0f)
            {
                Engine_ClothBody.Of(corpse.Node)?.Freeze();
                corpse.ClothFrozen = true;
            }

            if (!corpse.FadingOut && corpse.Age >= lifetime)
            {
                corpse.FadingOut = true;
            }

            if (!corpse.FadingOut)
            {
                ++activeCount;
            }
        }

        // Evict excess non-fading corpses cleanly (oldest distant first).
        if (activeCount > maxCorpses && _corpses.Count > 0)
        {
            if (_summaryScratch.Length < _corpses.Count)
            {
                _summaryScratch = new Engine_CorpsePolicy.CorpseSlotSummary[_corpses.Count];
            }

            while (activeCount > maxCorpses)
            {
                int n = _corpses.Count;
                for (int i = 0; i < n; ++i)
                {
                    RetainedCorpse c = _corpses[i];
                    Vector3 pos = c.Node.IsInsideTree() ? c.Node.GlobalPosition : c.Node.Position;
                    _summaryScratch[i] = new Engine_CorpsePolicy.CorpseSlotSummary(pos, c.Age, c.FadingOut);
                }

                int evictIdx = Engine_CorpsePolicy.SelectEvictionIndex(_summaryScratch.AsSpan(0, n), cameraPos);
                if (evictIdx < 0)
                {
                    break;
                }

                _corpses[evictIdx].FadingOut = true;
                --activeCount;
            }
        }

        // Advance fade-outs and release finished corpses.
        for (int i = _corpses.Count - 1; i >= 0; --i)
        {
            RetainedCorpse corpse = _corpses[i];
            if (!corpse.FadingOut)
            {
                continue;
            }

            corpse.FadeElapsed += stepDt;
            float progress = corpse.FadeElapsed / Engine_CorpsePolicy.FadeDurationSeconds;
            if (progress >= 1.0f)
            {
                ReleaseCorpse(corpse, queueFreeNode: true);
                _corpses.RemoveAt(i);
                continue;
            }

            float alpha = Math.Clamp(1.0f - progress, 0.0f, 1.0f);
            HostBridge.EntityAppearance fadingLook = corpse.HeldLook with { Alpha = alpha };
            Engine_EntityAppearance.Apply(corpse.Node, fadingLook, principal: false);

            if (!corpse.ShadowDropped && alpha < Engine_CorpsePolicy.ShadowDropAlpha)
            {
                DropShadow(corpse);
            }
        }
    }

    /// <summary>Immediately releases and frees every retained corpse (called on level unload / teardown).</summary>
    public void Clear()
    {
        for (int i = _corpses.Count - 1; i >= 0; --i)
        {
            ReleaseCorpse(_corpses[i], queueFreeNode: true);
        }

        _corpses.Clear();
    }

    /// <summary>
    /// Updates <paramref name="node"/> and its shadow proxy's <c>CustomAabb</c> when a disarticulated
    /// bone pile grows outside its previously stated hysteresis envelope (or on forced init/settle),
    /// preventing Godot from frustum-culling scattered bones still visible on screen.
    /// </summary>
    internal static void SyncDisarticulatedBounds(
        MeshInstance3D node,
        Engine_DisarticulatedRig shatter,
        Transform3D entityWorld,
        bool force = false,
        float hysteresis = 0.50f)
    {
        if (!GodotObject.IsInstanceValid(node))
        {
            return;
        }

        if (shatter.TryUpdateStatedBounds(entityWorld, force, out Aabb stated, hysteresis))
        {
            ApplyCustomBounds(node, stated);
        }
    }

    private static void ApplyCustomBounds(MeshInstance3D node, Aabb bounds)
    {
        if (!bounds.Position.IsFinite() || !bounds.Size.IsFinite() || bounds.Size.LengthSquared() < 1e-6f)
        {
            return;
        }

        node.CustomAabb = bounds;
        if (Engine_ShadowProxy.ProxyOf(node) is { } proxy && GodotObject.IsInstanceValid(proxy))
        {
            proxy.CustomAabb = bounds;
        }
    }

    private static void DropShadow(RetainedCorpse corpse)
    {
        corpse.ShadowDropped = true;
        Engine_ShadowSkeletons.Forget(corpse.Node);
        if (Engine_ShadowProxy.ProxyOf(corpse.Node) is { } proxy && GodotObject.IsInstanceValid(proxy))
        {
            proxy.Visible = false;
        }
        corpse.Node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
    }

    private static void ReleaseCorpse(RetainedCorpse corpse, bool queueFreeNode)
    {
        Engine_ShadowSkeletons.Forget(corpse.Node);
        if (GodotObject.IsInstanceValid(corpse.Node))
        {
            Engine_EntityAppearance.Forget(corpse.Node);
            if (queueFreeNode)
            {
                corpse.Node.QueueFree();
            }
        }
    }
}
