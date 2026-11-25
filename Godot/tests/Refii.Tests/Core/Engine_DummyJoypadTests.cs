namespace Refii.Tests.Core;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;

/// <summary>
/// The debug channel's dummy controller (Engine_DummyJoypad): its place on the joypad roster, the
/// guest pad its first press takes, its refusals, what it states itself as, and that nothing of it
/// is there while it is disconnected. Pure model code.
/// </summary>
[TestSuite]
public class Engine_DummyJoypadTests
{
	private static readonly int[] Free = { -1, -1, -1, -1 };
	private static readonly int[] OneReal = { 0 };

	[BeforeTest]
	public void Unplugged() => Engine_DummyJoypad.Disconnect();

	[AfterTest]
	public void LeftUnplugged() => Engine_DummyJoypad.Disconnect();

	[TestCase]
	public void WithNoDummyTheRosterAndThePadsAreTheRealJoypadsAlone()
	{
		AssertThat(Engine_DummyJoypad.Connected).IsFalse();
		AssertThat(Engine_DummyJoypad.Seat).IsEqual(-1);
		AssertThat(Engine_DummyJoypad.Is(Engine_DummyJoypad.Device)).IsFalse();
		AssertThat(Engine_DummyJoypad.Roster(new[] { 0, 3 })).IsEqual(new[] { 0, 3 });
		AssertThat(Engine_DummyJoypad.Roster(Array.Empty<int>())).IsEmpty();
		AssertThat(Engine_DummyJoypad.PadOnPress(Free)).IsEqual(-1);

		// Any joypad, the dummy's id included, takes the lowest free pad.
		AssertThat(Engine_DummyJoypad.PadForFirstPress(0, Free)).IsEqual(0);
		AssertThat(Engine_DummyJoypad.PadForFirstPress(Engine_DummyJoypad.Device, Free)).IsEqual(0);
		AssertThat(Engine_DummyJoypad.PadForFirstPress(2, new[] { 5, -1, 7, -1 })).IsEqual(1);
		AssertThat(Engine_DummyJoypad.PadForFirstPress(2, new[] { 5, 6, 7, 8 })).IsEqual(-1);
	}

	[TestCase]
	public void ConnectedItJoinsTheRosterAndItsFirstPressTakesItsSeatNotTheLowestFreePad()
	{
		int changes = 0;
		Action counted = () => ++changes;
		Engine_DummyJoypad.Changed += counted;
		try
		{
			AssertThat(Engine_DummyJoypad.Connect(Engine_DummyJoypad.DefaultSeat, ControllerFamily.Xbox, OneReal, Free)).IsNull();
		}
		finally
		{
			Engine_DummyJoypad.Changed -= counted;
		}

		AssertThat(changes).IsEqual(1);
		AssertThat(Engine_DummyJoypad.Connected).IsTrue();
		AssertThat(Engine_DummyJoypad.Seat).IsEqual(1);
		AssertThat(Engine_DummyJoypad.Is(Engine_DummyJoypad.Device)).IsTrue();
		AssertThat(Engine_DummyJoypad.Is(0)).IsFalse();
		AssertThat(Engine_DummyJoypad.Roster(OneReal)).IsEqual(new[] { 0, Engine_DummyJoypad.Device });
		AssertThat(Engine_DummyJoypad.Roster(Array.Empty<int>())).IsEqual(new[] { Engine_DummyJoypad.Device });

		// Pad 0 is free (player one is the scripted pad), and the dummy still takes pad 1.
		AssertThat(Engine_DummyJoypad.PadOnPress(Free)).IsEqual(1);
		AssertThat(Engine_DummyJoypad.PadForFirstPress(Engine_DummyJoypad.Device, Free)).IsEqual(1);
		// A real joypad's rule is unchanged beside it.
		AssertThat(Engine_DummyJoypad.PadForFirstPress(0, Free)).IsEqual(0);
	}

	[TestCase]
	public void APressReachesThePadItHoldsAndNoneOnceAnotherJoypadTookItsSeat()
	{
		AssertThat(Engine_DummyJoypad.Connect(2, ControllerFamily.Xbox, OneReal, Free)).IsNull();
		AssertThat(Engine_DummyJoypad.PadOnPress(new[] { 0, -1, Engine_DummyJoypad.Device, -1 })).IsEqual(2);
		AssertThat(Engine_DummyJoypad.PadOnPress(new[] { 0, -1, 4, -1 })).IsEqual(-1);
		AssertThat(Engine_DummyJoypad.PadForFirstPress(Engine_DummyJoypad.Device, new[] { 0, -1, 4, -1 })).IsEqual(-1);
	}

	[TestCase]
	public void ARefusedConnectChangesNothing()
	{
		int changes = 0;
		Action counted = () => ++changes;
		Engine_DummyJoypad.Changed += counted;
		try
		{
			AssertThat(Engine_DummyJoypad.Connect(4, ControllerFamily.Xbox, OneReal, Free)).IsNotNull();
			AssertThat(Engine_DummyJoypad.Connect(-1, ControllerFamily.Xbox, OneReal, Free)).IsNotNull();
			AssertThat(Engine_DummyJoypad.Connect(1, ControllerFamily.Xbox, new[] { 0, Engine_DummyJoypad.Device }, Free))
				.IsNotNull();
			AssertThat(Engine_DummyJoypad.Connect(1, ControllerFamily.Xbox, OneReal, new[] { -1, 0, -1, -1 })).IsNotNull();
			AssertThat(Engine_DummyJoypad.Connected).IsFalse();
			AssertThat(changes).IsEqual(0);

			AssertThat(Engine_DummyJoypad.Connect(1, ControllerFamily.Nintendo, OneReal, Free)).IsNull();
			AssertThat(Engine_DummyJoypad.Connect(2, ControllerFamily.Xbox, OneReal, Free)).IsNotNull();
			AssertThat(Engine_DummyJoypad.Seat).IsEqual(1);
			AssertThat(Engine_DummyJoypad.Family).IsEqual(ControllerFamily.Nintendo);
			AssertThat(changes).IsEqual(1);
		}
		finally
		{
			Engine_DummyJoypad.Changed -= counted;
		}
	}

	[TestCase]
	public void DisconnectedItIsGoneFromTheRosterAndSaysSoOnce()
	{
		AssertThat(Engine_DummyJoypad.Disconnect()).IsFalse();
		AssertThat(Engine_DummyJoypad.Connect(1, ControllerFamily.Xbox, OneReal, Free)).IsNull();
		Engine_DummyJoypad.StartVibration(0.25f, 0.5f);
		int changes = 0;
		Action counted = () => ++changes;
		Engine_DummyJoypad.Changed += counted;
		try
		{
			AssertThat(Engine_DummyJoypad.Disconnect()).IsTrue();
			AssertThat(Engine_DummyJoypad.Disconnect()).IsFalse();
		}
		finally
		{
			Engine_DummyJoypad.Changed -= counted;
		}

		AssertThat(changes).IsEqual(1);
		AssertThat(Engine_DummyJoypad.Connected).IsFalse();
		AssertThat(Engine_DummyJoypad.Seat).IsEqual(-1);
		AssertThat(Engine_DummyJoypad.Rumble.HasValue).IsFalse();
		AssertThat(Engine_DummyJoypad.Is(Engine_DummyJoypad.Device)).IsFalse();
		AssertThat(Engine_DummyJoypad.Roster(OneReal)).IsEqual(OneReal);
		AssertThat(Engine_DummyJoypad.PadOnPress(new[] { 0, Engine_DummyJoypad.Device, -1, -1 })).IsEqual(-1);
	}

	[TestCase]
	public void ItStatesItselfAsAControllerOfTheFamilyAskedFor()
	{
		foreach (ControllerFamily family in new[]
			{
				ControllerFamily.Xbox, ControllerFamily.PlayStation, ControllerFamily.PlayStation5, ControllerFamily.Nintendo,
			})
		{
			string name = Engine_DummyJoypad.NameOf(family);
			string guid = Engine_DummyJoypad.GuidOf(family);
			(int vendor, int product) = Engine_DummyJoypad.IdsOf(family);
			AssertThat(guid.Length).IsEqual(32);
			AssertThat(Engine_ControllerFamily.GuidIds(guid)).IsEqual(((int, int)?)(vendor, product));
			// By the platform's ids, by the GUID's, and by the name alone.
			AssertThat(Engine_ControllerFamily.Classify(name, guid, vendor, product)).IsEqual(family);
			AssertThat(Engine_ControllerFamily.Classify(null, guid)).IsEqual(family);
			AssertThat(Engine_ControllerFamily.ByName(name)).IsEqual(family);
		}

		AssertThat(Engine_DummyJoypad.Connect(1, ControllerFamily.PlayStation5, OneReal, Free)).IsNull();
		AssertThat(Engine_DummyJoypad.Family).IsEqual(ControllerFamily.PlayStation5);
		AssertThat(Engine_DummyJoypad.Name).IsEqual(Engine_DummyJoypad.NameOf(ControllerFamily.PlayStation5));
		AssertThat(Engine_DummyJoypad.Ids).IsEqual(Engine_DummyJoypad.IdsOf(ControllerFamily.PlayStation5));
		Engine_DummyJoypad.Disconnect();
		// A family nothing identifies is drawn as the Xbox pad, and the dummy states that one.
		AssertThat(Engine_DummyJoypad.Connect(1, ControllerFamily.Unknown, OneReal, Free)).IsNull();
		AssertThat(Engine_DummyJoypad.Family).IsEqual(ControllerFamily.Xbox);
	}

	[TestCase]
	public void TheFamilyArgumentNamesAFamilyOrIsRefused()
	{
		AssertThat(Engine_DummyJoypad.ParseFamily(null)).IsEqual((ControllerFamily?)ControllerFamily.Xbox);
		AssertThat(Engine_DummyJoypad.ParseFamily(" Xbox ")).IsEqual((ControllerFamily?)ControllerFamily.Xbox);
		AssertThat(Engine_DummyJoypad.ParseFamily("playstation")).IsEqual((ControllerFamily?)ControllerFamily.PlayStation);
		AssertThat(Engine_DummyJoypad.ParseFamily("PlayStation5")).IsEqual((ControllerFamily?)ControllerFamily.PlayStation5);
		AssertThat(Engine_DummyJoypad.ParseFamily("nintendo")).IsEqual((ControllerFamily?)ControllerFamily.Nintendo);
		AssertThat(Engine_DummyJoypad.ParseFamily("sega").HasValue).IsFalse();
	}

	[TestCase]
	public void ItsMotorsKeepWhatTheGuestLastStated()
	{
		AssertThat(Engine_DummyJoypad.Connect(1, ControllerFamily.Xbox, OneReal, Free)).IsNull();
		AssertThat(Engine_DummyJoypad.Rumble.HasValue).IsFalse();
		Engine_DummyJoypad.StartVibration(0.5f, 1f);
		AssertThat(Engine_DummyJoypad.Rumble).IsEqual(((float, float)?)(0.5f, 1f));
		Engine_DummyJoypad.StopVibration();
		AssertThat(Engine_DummyJoypad.Rumble.HasValue).IsFalse();
	}
}
