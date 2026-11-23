using System.Collections.Generic;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Game;
using Refii.Ui;
using Refii.Titles.Fable2;
using static GdUnit4.Assertions;
using Screen = Refii.Ui.Engine_UiModel.FrontEndScreen;

namespace Refii.Tests;

/// <summary>Verifies sole-owner input routing and manual production-menu lifecycles without native GUI objects.</summary>
[TestSuite]
public sealed class AndroidInteractiveFrontendTests
{
    /// <summary>Focus loss mutes physical devices without stopping production edge-release sampling.</summary>
    [TestCase]
    public void ApplicationAndWindowFocusBothGateDevices()
    {
        AssertThat(GameControllerTransport.DevicesHaveFocus(true, true)).IsTrue();
        AssertThat(GameControllerTransport.DevicesHaveFocus(false, true)).IsFalse();
        AssertThat(GameControllerTransport.DevicesHaveFocus(true, false)).IsFalse();
        AssertThat(GameControllerTransport.DevicesHaveFocus(false, false)).IsFalse();
    }

    /// <summary>A supplied owner receives exactly one complete packet and prevents a second native writer.</summary>
    [TestCase]
    public void OwnerSinkExcludesNativeFallback()
    {
        var mailbox = new SinglePlayerControllerMailbox();
        int nativeCalls = 0;
        var expected = new GameControllerPacket(0, true, 0x1001, 20, 30, -32767, 123, 456, -789);
        GameControllerTransport.Publish(expected, packet => mailbox.Publish(packet), _ => ++nativeCalls);
        AssertThat(nativeCalls).IsEqual(0);
        AssertThat(mailbox.Select(42)).IsEqual(expected);
        AssertThat(mailbox.Owner!.Step).IsEqual(42L);
        AssertThat(mailbox.Owner.Publication.Sequence).IsEqual(1L);
    }

    /// <summary>Without an owner adapter, the existing desktop bridge transport remains the sole receiver.</summary>
    [TestCase]
    public void DesktopFallbackStillReceivesExactlyOnePacket()
    {
        var expected = new GameControllerPacket(2, false, 0, 0, 0, 0, 0, 0, 0);
        var packets = new List<GameControllerPacket>();
        GameControllerTransport.Publish(expected, null, packets.Add);
        AssertThat(packets.Count).IsEqual(1);
        AssertThat(packets[0]).IsEqual(expected);
    }

    /// <summary>Player-two samples cannot replace player one; releases are observed on the next owner step.</summary>
    [TestCase]
    public void SinglePlayerMailboxPreservesReleaseAndPacketIdentity()
    {
        var mailbox = new SinglePlayerControllerMailbox();
        AssertThat(mailbox.Latest.Packet.Connected).IsFalse();
        var held = new GameControllerPacket(0, true, 0x1000, 255, 0, 32000, 0, 0, 0);
        AssertThat(mailbox.Publish(held)).IsTrue();
        AssertThat(mailbox.Select(1)).IsEqual(held);
        AssertThat(mailbox.Publish(held with { Pad = 1, Buttons = 0x10 })).IsFalse();
        AssertThat(mailbox.Latest.Sequence).IsEqual(1L);
        var release = held with { Buttons = 0, LeftTrigger = 0, LeftX = 0 };
        AssertThat(mailbox.Publish(release)).IsTrue();
        AssertThat(mailbox.Owner!.Publication.Packet).IsEqual(held);
        AssertThat(mailbox.Select(2)).IsEqual(release);
        AssertThat(mailbox.Owner!.Publication.Sequence).IsEqual(2L);
        AssertThat(SinglePlayerControllerMailbox.AgeMilliseconds(mailbox.Latest) >= 0).IsTrue();
    }

    /// <summary>The title becomes visible when armed, without relying on its valid zero state or advancing it.</summary>
    [TestCase]
    public void RealFrontendVisibilityFollowsNativeReadiness()
    {
        AssertThat(InteractiveFrontEndPolicy.ShouldReveal(true, Screen.Title, true, false, false)).IsFalse();
        AssertThat(InteractiveFrontEndPolicy.ShouldReveal(true, Screen.Title, true, true, false)).IsTrue();
        AssertThat(InteractiveFrontEndPolicy.ShouldReveal(true, Screen.MainMenu, true, false, false)).IsTrue();
        AssertThat(InteractiveFrontEndPolicy.ShouldReveal(false, Screen.MainMenu, true, true, false)).IsFalse();
        AssertThat(InteractiveFrontEndPolicy.ShouldReveal(true, Screen.MainMenu, false, true, false)).IsFalse();
        AssertThat(InteractiveFrontEndPolicy.ShouldReveal(true, Screen.None, false, false, false)).IsFalse();
        AssertThat(InteractiveFrontEndPolicy.ShouldReveal(false, Screen.None, false, false, true)).IsTrue();
    }

    /// <summary>Revealed UI stays through the title's hand-over to the main menu, and goes with the front end's GUI.</summary>
    [TestCase]
    public void RevealedFrontendStaysUntilItsGuiIsGone()
    {
        AssertThat(InteractiveFrontEndPolicy.StaysRevealed(true, true, false)).IsTrue();
        AssertThat(InteractiveFrontEndPolicy.StaysRevealed(true, false, true)).IsTrue();
        AssertThat(InteractiveFrontEndPolicy.StaysRevealed(true, false, false)).IsFalse();
        AssertThat(InteractiveFrontEndPolicy.StaysRevealed(false, true, false)).IsFalse();
    }

    /// <summary>Boot, GUI and title readiness remain distinct until the real native menu becomes interactive.</summary>
    [TestCase]
    public void FrontendStatusCannotReportAnUnobservedTitle()
    {
        AssertThat(InteractiveFrontEndPolicy.Status(false, Screen.Title, true, true))
            .IsEqual("waiting for native frontend");
        AssertThat(InteractiveFrontEndPolicy.Status(false, Screen.MainMenu, true, true))
            .IsEqual("waiting for native frontend");
        AssertThat(InteractiveFrontEndPolicy.Status(true, Screen.None, false, false))
            .IsEqual("waiting for native frontend");
        AssertThat(InteractiveFrontEndPolicy.Status(true, Screen.Title, false, true))
            .IsEqual("waiting for native frontend GUI");
        AssertThat(InteractiveFrontEndPolicy.Status(true, Screen.Title, true, false))
            .IsEqual("waiting for native title input readiness");
        AssertThat(InteractiveFrontEndPolicy.Status(true, Screen.Title, true, true))
            .IsEqual("title — press controller A");
        AssertThat(InteractiveFrontEndPolicy.Status(true, Screen.MainMenu, true, false))
            .IsEqual("main menu — select Load Game with the controller");
    }

    /// <summary>Availability and navigation select the real named Load Game command only on explicit activation.</summary>
    [TestCase]
    public void ProductionMenuWaitsForManualNavigationAndActivation()
    {
        var menu = new Engine_FrontEndMenu();
        menu.Add(new Engine_UiExpandableMenu.Item("New Game", "new")
            { Opens = Fable2FrontEndRoot.HeroCardScreen });
        menu.Add(new Engine_UiExpandableMenu.Item("Load Game", "load"));
        Fable2FrontEndRoot.ApplyAvailability(menu, 1, new Engine_UiModel.SaveState(true, -1, 6));
        AssertThat(menu.Visible.Count).IsEqual(1);
        Fable2FrontEndRoot.ApplyAvailability(menu, 1, new Engine_UiModel.SaveState(true, 1, 6));
        AssertThat(menu.Visible.Count).IsEqual(2);
        AssertThat(menu.Highlighted!.Name).IsEqual("New Game");
        // Idle polling cannot choose a save or turn the initial New Game highlight into a dispatch.
        for (int i = 0; i < 60; ++i) menu.Navigate(0, 1.0 / 30);
        AssertThat(menu.Highlighted!.Name).IsEqual("New Game");
        AssertThat(menu.Activate()).IsNull();
        AssertThat(menu.Navigate(1, 1.0 / 30)).IsEqual(1);
        AssertThat(menu.Highlighted!.Name).IsEqual("Load Game");
        AssertThat(menu.Activate()).IsEqual("Load Game");
        AssertThat(menu.Back()).IsFalse();
        Fable2FrontEndRoot.ApplyAvailability(menu, 1, new Engine_UiModel.SaveState(true, 0, 6));
        AssertThat(menu.Visible.Count).IsEqual(1);
        AssertThat(menu.Highlighted!.Name).IsEqual("New Game");
    }
}
