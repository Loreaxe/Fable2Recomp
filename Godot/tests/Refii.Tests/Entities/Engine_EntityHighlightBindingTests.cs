using GdUnit4;
using Refii.Entities;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The re-bind trigger of the outline proxy: a re-rig (GameEntities' mesh change or late rig)
/// gives the body a new Skeleton3D under the old name and a new Skin, and the proxy must follow.
/// Instance ids only — no Godot object is built.
/// </summary>
[TestSuite]
public sealed class Engine_EntityHighlightBindingTests
{
    private const ulong Mesh = 101ul;
    private const ulong Skin = 202ul;
    private const ulong Bones = 303ul;

    [TestCase]
    public void SameBindingIsNotStale()
    {
        AssertThat(Engine_EntityHighlight.BindingStale(Mesh, Mesh, Skin, Skin, Bones, Bones)).IsFalse();

        // A prop: no skin, no skeleton, on either side.
        AssertThat(Engine_EntityHighlight.BindingStale(Mesh, Mesh, 0ul, 0ul, 0ul, 0ul)).IsFalse();
    }

    [TestCase]
    public void ReRigIsStale()
    {
        // The new skeleton under the old name: same path, different node.
        AssertThat(Engine_EntityHighlight.BindingStale(Mesh, Mesh, Skin, Skin, Bones, 304ul)).IsTrue();

        // `node.Skin = skeleton.Skin` after the re-rig.
        AssertThat(Engine_EntityHighlight.BindingStale(Mesh, Mesh, Skin, 203ul, Bones, Bones)).IsTrue();

        // Both at once, as the re-rig does.
        AssertThat(Engine_EntityHighlight.BindingStale(Mesh, Mesh, Skin, 203ul, Bones, 304ul)).IsTrue();
    }

    [TestCase]
    public void MeshSwapUnderTheSameSlotIsStale()
    {
        AssertThat(Engine_EntityHighlight.BindingStale(Mesh, 102ul, Skin, Skin, Bones, Bones)).IsTrue();
    }

    [TestCase]
    public void RigGainedOrLostIsStale()
    {
        // A late rig: the proxy was bound with no skeleton.
        AssertThat(Engine_EntityHighlight.BindingStale(Mesh, Mesh, 0ul, Skin, 0ul, Bones)).IsTrue();

        // The skeleton freed, nothing in its place.
        AssertThat(Engine_EntityHighlight.BindingStale(Mesh, Mesh, Skin, Skin, Bones, 0ul)).IsTrue();
    }
}
