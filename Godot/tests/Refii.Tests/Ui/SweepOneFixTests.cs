namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Titles.Fable2;

/// <summary>
/// The pure rules behind the first test sweep's fixes: the discard text's refund and the pause
/// statement the drawn world holds on. Pure model code.
/// </summary>
[TestSuite]
public class SweepOneFixTests
{
	[TestCase]
	public void TheDiscardRefundIsHalfTheHeldLevelsCostTowardZero()
	{
		// `srawi r10,r3,1` + `addze` (TU1 0x826C38C8 / 0x826C38D4): a signed halving toward zero.
		AssertThat(Engine_UiAbilityPage.DiscardRefund(16200)).IsEqual(8100);
		AssertThat(Engine_UiAbilityPage.DiscardRefund(64801)).IsEqual(32400);
		AssertThat(Engine_UiAbilityPage.DiscardRefund(-3)).IsEqual(-1);
		AssertThat(Engine_UiAbilityPage.DiscardRefund(0)).IsEqual(0);
	}

	[TestCase]
	public void AScreenUpStatesAPause()
	{
		AssertThat(Engine_StatedPause.StatesPause(0u)).IsTrue();
		AssertThat(Engine_StatedPause.StatesPause(14u)).IsTrue();
		AssertThat(Engine_StatedPause.StatesPause(31u)).IsFalse();
		AssertThat(Engine_StatedPause.StatesPause(uint.MaxValue)).IsFalse();
	}

	[TestCase]
	public void AFlowStepWithNoWorldTickStatesAPauseWithNoScreenUp()
	{
		// The lite pause box: no GUI screen (31), the world held by the guest's flow step.
		AssertThat(Engine_StatedPause.StatesPause(31u, worldHeld: 1u)).IsTrue();
		AssertThat(Engine_StatedPause.StatesPause(31u, worldHeld: 0u)).IsFalse();
		AssertThat(Engine_StatedPause.StatesPause(0u, worldHeld: 0u)).IsTrue();
	}

	[TestCase]
	public void AWorldThatRanUnderAPauseStatesNone()
	{
		// The player's own pause in an online session: the book is up (0), the guest's world ran (2).
		AssertThat(Engine_StatedPause.StatesPause(0u, worldHeld: 2u)).IsFalse();
		AssertThat(Engine_StatedPause.StatesPause(14u, worldHeld: 2u)).IsFalse();
		AssertThat(Engine_StatedPause.StatesPause(31u, worldHeld: 2u)).IsFalse();
	}
}
