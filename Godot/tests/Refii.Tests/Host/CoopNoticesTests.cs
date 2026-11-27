namespace Refii.Tests;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Host;
using Refii.Ui;
using Refii.Titles.Fable2;

[TestSuite]
public class CoopNoticesTests
{
	[TestCase]
	public void TestLocalJoinTellsJoinThenProgressionOncePerRun()
	{
		var notices = new Engine_UiCoopNotices();
		notices.LocalCoop(false, true, told: true);

		AssertThat(notices.Tick(0.0, string.Empty, inGame: true)).IsEqual(Engine_UiCoopNotices.LocalJoined);
		AssertThat(notices.Tick(1.0, string.Empty, inGame: true)).IsNull();
		AssertThat(notices.Tick(4.0, string.Empty, inGame: true)).IsEqual(Engine_UiCoopNotices.Progression);

		notices.LocalCoop(true, false, told: true);
		AssertThat(notices.Tick(5.0, string.Empty, inGame: true)).IsEqual(Engine_UiCoopNotices.LocalLeft);

		notices.LocalCoop(false, true, told: true);
		AssertThat(notices.Tick(5.0, string.Empty, inGame: true)).IsEqual(Engine_UiCoopNotices.LocalJoined);
		AssertThat(notices.Tick(5.0, string.Empty, inGame: true)).IsNull();
	}

	[TestCase]
	public void TestOnlineJoinWaitsForTheName()
	{
		var notices = new Engine_UiCoopNotices();
		notices.Session(CoopSession.Phase.Hosting, CoopSession.Phase.PeerJoined, CoopSession.Role.Host);

		AssertThat(notices.Tick(1.0, string.Empty, inGame: true)).IsNull();
		AssertThat(notices.Tick(1.0, "Sparrow", inGame: true)).IsEqual(Engine_UiCoopNotices.HostJoined("Sparrow"));
	}

	[TestCase]
	public void TestOnlineJoinFallsBackWithoutAName()
	{
		var notices = new Engine_UiCoopNotices();
		notices.Session(CoopSession.Phase.Solo, CoopSession.Phase.PeerJoined, CoopSession.Role.Client);

		AssertThat(notices.Tick(Engine_UiCoopNotices.NameWaitSeconds + 1.0, string.Empty, inGame: true))
			.IsEqual(Engine_UiCoopNotices.JoinerJoined(string.Empty));
	}

	[TestCase]
	public void TestALossIsTheGuestsStatementOnBothSides()
	{
		// The joiner's guest raises its own box (KICKED / DISCONNECTED / FAILED); the host's guest pushes
		// its own TopBox record. Neither side queues a notice of ours.
		foreach (CoopSession.Role side in new[] { CoopSession.Role.Client, CoopSession.Role.Host })
		{
			var notices = new Engine_UiCoopNotices();
			CoopSession.Phase before = side == CoopSession.Role.Client ? CoopSession.Phase.Solo : CoopSession.Phase.Hosting;
			notices.Session(before, CoopSession.Phase.PeerJoined, side);
			AssertThat(notices.Tick(0.0, "Sparrow", inGame: true)).IsNotNull();
			AssertThat(notices.Tick(Engine_UiCoopNotices.Seconds, "Sparrow", inGame: true))
				.IsEqual(Engine_UiCoopNotices.Progression);
			notices.Session(CoopSession.Phase.PeerJoined, CoopSession.Phase.PeerLost, CoopSession.Role.Solo);
			AssertThat(notices.Queued).IsEqual(0);
			AssertThat(notices.Tick(Engine_UiCoopNotices.Seconds, string.Empty, inGame: true)).IsNull();
		}
	}

	[TestCase]
	public void TestALossBeforeTheNameCancelsTheJoinNotice()
	{
		var host = new Engine_UiCoopNotices();
		host.Session(CoopSession.Phase.Hosting, CoopSession.Phase.PeerJoined, CoopSession.Role.Host);
		AssertThat(host.Tick(1.0, string.Empty, inGame: true)).IsNull();
		host.Session(CoopSession.Phase.PeerJoined, CoopSession.Phase.PeerLost, CoopSession.Role.Solo);
		AssertThat(host.Tick(Engine_UiCoopNotices.NameWaitSeconds + 1.0, "Sparrow", inGame: true)).IsNull();
		AssertThat(host.Queued).IsEqual(0);
	}

	[TestCase]
	public void TestTheHostsLossRecordIsTheGuestsDroppedClient()
	{
		// The record name the host's lost-connection handler pushes on TopBox channel 0 (TU1 0x8324A1B4
		// seeds dword_834977BC with Fnv1("DroppedClient"); the run's `[hud-topbox] front record 0x74B90A4C`).
		AssertThat(Refii.Core.Engine_GameHash.Fnv1("DroppedClient")).IsEqual(0x74B90A4Cu);

		// Our own notices are our keys, resolved by the shell; a join notice is said already, with the name in it.
		AssertThat(Engine_UiCoopNotices.IsTextKey(Engine_UiCoopNotices.Progression)).IsTrue();
		AssertThat(Engine_UiCoopNotices.IsTextKey(Engine_UiCoopNotices.HostJoined("Sparrow"))).IsFalse();
	}

	[TestCase]
	public void TestPressStartOncePerRunAndAgainForANewPad()
	{
		var notices = new Engine_UiCoopNotices();
		notices.Pads(new[] { 0 }, open: true);
		AssertThat(notices.Tick(0.0, string.Empty, inGame: true)).IsNull();

		notices.Pads(new[] { 0, 1 }, open: true);
		AssertThat(notices.Tick(0.0, string.Empty, inGame: true)).IsEqual(Engine_UiCoopNotices.PressStart);

		notices.Pads(new[] { 0, 1 }, open: true);
		AssertThat(notices.Tick(5.0, string.Empty, inGame: true)).IsNull();

		notices.Pads(new[] { 0, 1, 2 }, open: true);
		AssertThat(notices.Tick(5.0, string.Empty, inGame: true)).IsEqual(Engine_UiCoopNotices.PressStart);
	}
}
