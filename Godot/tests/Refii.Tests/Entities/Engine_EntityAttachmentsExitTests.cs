using GdUnit4;
using Refii.Entities;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Each mount source names its own composed exit, so a run log tells them apart.</summary>
[TestSuite]
public sealed class Engine_EntityAttachmentsExitTests
{
    [TestCase]
    public void EveryMountSourceHasItsOwnExit()
    {
        AssertThat(Engine_EntityAttachments.ComposedExit(Engine_EntityAttachments.MountGmdBone))
            .IsEqual(Engine_EntityAttachments.ExitComposedGmdBone);
        AssertThat(Engine_EntityAttachments.ComposedExit(Engine_EntityAttachments.MountGmdRoot))
            .IsEqual(Engine_EntityAttachments.ExitComposedGmdRoot);
        AssertThat(Engine_EntityAttachments.ComposedExit(Engine_EntityAttachments.MountGmdFocal))
            .IsEqual(Engine_EntityAttachments.ExitComposedGmdFocal);
        AssertThat(Engine_EntityAttachments.ComposedExit(Engine_EntityAttachments.MountLevelFx))
            .IsEqual(Engine_EntityAttachments.ExitComposedLevelFx);
        AssertThat(Engine_EntityAttachments.ComposedExit(Engine_EntityAttachments.MountFocalName))
            .IsEqual(Engine_EntityAttachments.ExitComposedFocalName);
        AssertThat(Engine_EntityAttachments.ComposedExit(Engine_EntityAttachments.MountIdentity))
            .IsEqual(Engine_EntityAttachments.ExitComposedIdentity);

        // Never resolved reads as the guest's own last fallback, the identity.
        AssertThat(Engine_EntityAttachments.ComposedExit(string.Empty))
            .IsEqual(Engine_EntityAttachments.ExitComposedIdentity);
    }
}
