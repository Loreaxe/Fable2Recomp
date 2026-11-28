using System;
using Godot;
using Refii.Fx;

namespace Refii.Level;

/// <summary>
/// A silver key, standing where its placement puts it.
///
/// IN THE GAME THE GUEST MOVES IT. The key's bob and spin are the guest's component CECHighlightedItem
/// (slot 14 `CECHighlightedItem_vf14` TU1 0x8217E920 / disc 0x8217E210: `lbz r11,0xC(r31) ; beq` then
/// `bl sub_8227E608` / disc `sub_8227E308`, 179 instructions, identical): it reads the definition's
/// `BobTime`, `DistanceFromGround`, `BobRange` and `RotationSpeed` (0x8227E660 / 0x8227E714 / 0x8227E738 /
/// 0x8227E7A8) and states the entity's position and rotation through its transform object, entity+0x7C
/// (`lwz r31,0x7C(r11)` 0x8227E630): `lwz r7,0x64(r3) ; … bctrl` (0x8227E768..0x8227E794) the bobbed
/// position over its stored `InitialPos` (comp+0x20), `lwz r11,0x68(r5) ; … bctrl` (0x8227E888..0x8227E8B8)
/// the turn. "BobTime" has no other reader in the image. So while a guest entity drives this node
/// (<see cref="Refii.Entities.Engine_StaticTwins.Drive"/>), the node stands exactly where the guest states
/// and adds nothing of its own.
///
/// With no guest (the editor) nothing states the motion; the node then turns and bobs by the constants
/// below — the editor's own picture, never the game's.
/// </summary>
[Tool]
public partial class SilverKeyNode : MeshInstance3D
{
    public const float BobPeriod = 3.0f;

    public const float BobAmplitude = 0.08f;

    public const float BobAltitude = 0.25f;

    public const float RotateDegSec = 45.0f;

    [Export]
    public Transform3D BaseTransform { get; set; } = Transform3D.Identity;

    /// <summary>A guest entity states where the key stands (Engine_StaticTwins.Drive): no motion of the node's own.</summary>
    public bool Stated { get; set; }

    private float _phase;
    private float _yaw;
    private bool _pivotSet;
    private Vector3 _pivot;

    public override void _Process(double delta)
    {
    	using var _framePhase = Refii.Core.Engine_FramePhases.Measure("silverkeynode");
        if (Stated)
        {
            if (!Transform.IsEqualApprox(BaseTransform))
            {
                Transform = BaseTransform;
            }

            return;
        }

        float dt = (float)delta;
        const float twoPi = Mathf.Pi * 2f;

        _phase += (twoPi / BobPeriod) * dt;
        if (_phase > twoPi)
        {
            _phase -= twoPi;
        }

        _yaw += Mathf.DegToRad(RotateDegSec) * dt;
        if (_yaw > twoPi)
        {
            _yaw -= twoPi;
        }

        // THE PIVOT IS FOR THE SPIN, and only for the spin. It used to reposition a hardcoded
        // "KeyFx" child onto the mesh's centre too; the effect now comes from
        // <see cref="Refii.Level.Engine_LevelFx"/> and is placed by the `OffsetFromDummy` the
        // key's own data states, so moving it from here would be the host overriding the game.
        if (!_pivotSet && Mesh is not null)
        {
            _pivotSet = true;
            _pivot = Mesh.GetAabb().GetCenter();
        }

        float bob = (BobAmplitude * MathF.Sin(_phase)) + BobAltitude;

        Transform3D spun = BaseTransform *
            new Transform3D(Basis.Identity, _pivot) *
            new Transform3D(new Basis(Vector3.Up, _yaw), Vector3.Zero) *
            new Transform3D(Basis.Identity, -_pivot);
        spun.Origin += Vector3.Up * bob;
        Transform = spun;
    }
}
