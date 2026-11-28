namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;
using Press = Refii.Titles.Fable2.Engine_CoopSavePicker.Press;
using Picker = Refii.Titles.Fable2.Engine_CoopSavePicker.Picker;

/// <summary>
/// Player two's co-op save picker past the guest's six (<see cref="Engine_CoopSavePicker"/>): the
/// guest keeps its cursor and A on saves 0..5 (its setter's `min(count, 6)` bound), this holds the
/// cursor and commits by index from save 6, both step over the save player one plays, and an
/// unloadable save is not committed.
/// </summary>
[TestSuite]
public class CoopSavePickerTests
{
	private const uint AllValid = 0xFFu;

	[TestCase]
	public void OnTheGuestsSixNothingIsWithheldButTheStepOffItsEnd()
	{
		AssertThat(Engine_CoopSavePicker.Withheld(new Picker(8, 0, -1, AllValid, null))).IsEqual(Press.None);
		AssertThat(Engine_CoopSavePicker.Withheld(new Picker(8, 4, -1, AllValid, null))).IsEqual(Press.None);
		AssertThat(Engine_CoopSavePicker.Withheld(new Picker(8, 5, -1, AllValid, null))).IsEqual(Press.Right);

		// Six saves or fewer: the guest's own refusal at its end stands.
		AssertThat(Engine_CoopSavePicker.Withheld(new Picker(6, 5, -1, AllValid, null))).IsEqual(Press.None);

		// Its own loop steps over player one's save: from 4 over 5 lands on 6, past its six.
		AssertThat(Engine_CoopSavePicker.Withheld(new Picker(8, 4, 5, AllValid, null))).IsEqual(Press.Right);
	}

	[TestCase]
	public void TheGuestKeepsItsOwnPressesOnItsSix()
	{
		var picker = new Picker(8, 2, -1, AllValid, null);
		foreach (Press press in new[] { Press.Left, Press.Right, Press.Accept })
		{
			Engine_CoopSavePicker.Outcome outcome = Engine_CoopSavePicker.Apply(picker, press);
			AssertThat(outcome.Taken).IsFalse();
			AssertThat(outcome.Commit).IsEqual(-1);
			AssertThat(outcome.GuestCursor).IsEqual(-1);
		}
	}

	[TestCase]
	public void RightOffTheSixthTakesTheCursorAndLeftHandsItBack()
	{
		Engine_CoopSavePicker.Outcome right = Engine_CoopSavePicker.Apply(new Picker(8, 5, -1, AllValid, null), Press.Right);
		AssertThat(right.Taken).IsTrue();
		AssertThat(right.Owned).IsEqual(6);
		AssertThat(right.GuestCursor).IsEqual(-1);

		Engine_CoopSavePicker.Outcome further = Engine_CoopSavePicker.Apply(new Picker(8, 5, -1, AllValid, 6), Press.Right);
		AssertThat(further.Owned).IsEqual(7);

		// At the last save a right goes nowhere, and still never reaches the guest.
		Engine_CoopSavePicker.Outcome end = Engine_CoopSavePicker.Apply(new Picker(8, 5, -1, AllValid, 7), Press.Right);
		AssertThat(end.Taken).IsTrue();
		AssertThat(end.Owned).IsEqual(7);

		// Back onto the guest's six: the guest's cursor is already there, so nothing is sent.
		Engine_CoopSavePicker.Outcome left = Engine_CoopSavePicker.Apply(new Picker(8, 5, -1, AllValid, 6), Press.Left);
		AssertThat(left.Taken).IsTrue();
		AssertThat(left.Owned is null).IsTrue();
		AssertThat(left.GuestCursor).IsEqual(-1);
	}

	[TestCase]
	public void WhileHeldLeftRightAndAAreWithheld()
	{
		AssertThat(Engine_CoopSavePicker.Withheld(new Picker(8, 5, -1, AllValid, 7)))
			.IsEqual(Press.Left | Press.Right | Press.Accept);
	}

	[TestCase]
	public void PageTurnsLandOnThePagesFirstOrLastSave()
	{
		// Five to a page: from save 2, next lands on save 5 — on the guest's six, so its own cursor
		// is sent there.
		Engine_CoopSavePicker.Outcome next = Engine_CoopSavePicker.Apply(new Picker(8, 2, -1, AllValid, null), Press.PageNext);
		AssertThat(next.Taken).IsTrue();
		AssertThat(next.Owned is null).IsTrue();
		AssertThat(next.GuestCursor).IsEqual(5);

		// Back from save 7 lands on save 4, the last of page one.
		Engine_CoopSavePicker.Outcome back = Engine_CoopSavePicker.Apply(new Picker(8, 5, -1, AllValid, 7), Press.PagePrevious);
		AssertThat(back.Owned is null).IsTrue();
		AssertThat(back.GuestCursor).IsEqual(4);

		// No page past the last.
		Engine_CoopSavePicker.Outcome none = Engine_CoopSavePicker.Apply(new Picker(8, 5, -1, AllValid, 7), Press.PageNext);
		AssertThat(none.Owned).IsEqual(7);
		AssertThat(none.GuestCursor).IsEqual(-1);

		// A turn landing on player one's save moves on past it.
		Engine_CoopSavePicker.Outcome skipped = Engine_CoopSavePicker.Apply(new Picker(10, 1, 5, AllValid, null), Press.PageNext);
		AssertThat(skipped.Owned).IsEqual(6);
	}

	[TestCase]
	public void AcceptPastTheSixCommitsByIndexOnlyALoadableSave()
	{
		Engine_CoopSavePicker.Outcome a = Engine_CoopSavePicker.Apply(new Picker(8, 5, -1, AllValid, 7), Press.Accept);
		AssertThat(a.Taken).IsTrue();
		AssertThat(a.Commit).IsEqual(7);

		uint sevenBroken = AllValid & ~(1u << 7);
		Engine_CoopSavePicker.Outcome refused = Engine_CoopSavePicker.Apply(new Picker(8, 5, -1, sevenBroken, 7), Press.Accept);
		AssertThat(refused.Taken).IsTrue();
		AssertThat(refused.Commit).IsEqual(-1);
	}

	[TestCase]
	public void StepsLoopPastPlayerOnesSave()
	{
		AssertThat(Engine_CoopSavePicker.Step(6, 1, 9, 7)).IsEqual(8);
		AssertThat(Engine_CoopSavePicker.Step(8, -1, 9, 7)).IsEqual(6);
		AssertThat(Engine_CoopSavePicker.Step(8, 1, 9, -1)).IsEqual(-1);
		AssertThat(Engine_CoopSavePicker.Step(0, -1, 9, -1)).IsEqual(-1);
		AssertThat(Engine_CoopSavePicker.Presses(0, 3, 8, 1)).IsEqual(2);
		AssertThat(Engine_CoopSavePicker.Presses(4, 0, 8, -1)).IsEqual(-4);
		AssertThat(Engine_CoopSavePicker.Presses(0, 1, 8, 1)).IsEqual(0);
	}

	[TestCase]
	public void ThePointerTakesTheLaterSavesAndLeavesTheGuestsSixToItsPresses()
	{
		var picker = new Picker(8, 2, -1, AllValid, null);
		AssertThat(Engine_CoopSavePicker.Point(picker, 4, true).Taken).IsFalse();

		Engine_CoopSavePicker.Outcome later = Engine_CoopSavePicker.Point(picker, 7, true);
		AssertThat(later.Owned).IsEqual(7);
		AssertThat(later.Commit).IsEqual(7);

		// Held past the six, a click back on the guest's six commits there through the pump.
		Engine_CoopSavePicker.Outcome back = Engine_CoopSavePicker.Point(new Picker(8, 5, -1, AllValid, 7), 3, true);
		AssertThat(back.Taken).IsTrue();
		AssertThat(back.Owned is null).IsTrue();
		AssertThat(back.Commit).IsEqual(3);

		// Player one's save is not reached.
		Engine_CoopSavePicker.Outcome own = Engine_CoopSavePicker.Point(new Picker(8, 2, 6, AllValid, null), 6, true);
		AssertThat(own.Taken).IsTrue();
		AssertThat(own.Commit).IsEqual(-1);
		AssertThat(own.Owned is null).IsTrue();
	}

	[TestCase]
	public void PadEdgesFireOncePerPressAndTheStickFoldsAtTheGuestsThreshold()
	{
		var edges = new Engine_CoopSavePicker.PadEdges();
		AssertThat(edges.Feed(false, true, false, false, false, 0, 0, 0)).IsEqual(Press.Right);
		AssertThat(edges.Feed(false, true, false, false, false, 0, 0, 0)).IsEqual(Press.None);
		AssertThat(edges.Feed(false, false, true, false, true, 0, 0, 0)).IsEqual(Press.Accept | Press.PageNext);

		// RB and RT are one page button: RT while RB is held is no new press.
		AssertThat(edges.Feed(false, false, false, false, true, 0, 200, 0)).IsEqual(Press.None);
		AssertThat(edges.Feed(false, false, false, false, false, 0, 0, 0)).IsEqual(Press.None);
		AssertThat(edges.Feed(false, false, false, false, false, 0, 200, 0)).IsEqual(Press.PageNext);
		AssertThat(edges.Feed(false, false, false, false, false, 0, 0, 0)).IsEqual(Press.None);

		// 0.70 is not yet a press; 0.75 is; holding above the release threshold is not a new one.
		AssertThat(edges.Feed(false, false, false, false, false, 0, 0, -22937)).IsEqual(Press.None);
		AssertThat(edges.Feed(false, false, false, false, false, 0, 0, -24576)).IsEqual(Press.Left);
		AssertThat(edges.Feed(false, false, false, false, false, 0, 0, -22000)).IsEqual(Press.None);
		AssertThat(edges.Feed(false, false, false, false, false, 0, 0, 0)).IsEqual(Press.None);
		AssertThat(edges.Feed(false, false, false, false, false, 0, 0, 24576)).IsEqual(Press.Right);
	}

	/// <summary>
	/// One press as the whole picker answers it: this side's outcome, and where it leaves the press
	/// to the guest, the guest's own answer — its loop steps over player one's save, its setter keeps
	/// <c>0 &lt;= N &lt; min(count, 6)</c>, its A commits its cursor when the header parsed.
	/// </summary>
	private static (int Guest, int? Owned, int Commit) Answer(int saves, int own, uint valid, int guest, int? owned, Press press)
	{
		var picker = new Picker(saves, guest, own, valid, owned);
		Engine_CoopSavePicker.Outcome outcome = Engine_CoopSavePicker.Apply(picker, press);
		if (outcome.Taken)
		{
			return (outcome.GuestCursor >= 0 ? outcome.GuestCursor : guest, outcome.Owned, outcome.Commit);
		}

		if (press is Press.Left or Press.Right)
		{
			int to = Engine_CoopSavePicker.Step(guest, press == Press.Right ? 1 : -1, saves, own);
			return (to >= 0 && to < System.Math.Min(saves, Engine_CoopSavePicker.GuestCards) ? to : guest, owned, -1);
		}

		return (guest, owned, press == Press.Accept && picker.IsValid(guest) ? guest : -1);
	}

	/// <summary>Every save the cursor stands on walking one way from <paramref name="guest"/> until it stops.</summary>
	private static System.Collections.Generic.List<int> Walk(int saves, int own, int guest, int? owned, Press press)
	{
		var seen = new System.Collections.Generic.List<int> { owned ?? guest };
		for (int i = 0; i < saves + 2; ++i)
		{
			(guest, owned, _) = Answer(saves, own, 0xFFu, guest, owned, press);
			if ((owned ?? guest) == seen[^1])
			{
				break;
			}

			seen.Add(owned ?? guest);
		}

		return seen;
	}

	/// <summary>
	/// SEVEN SAVES WITH PLAYER ONE'S LEFT OUT (local co-op: the host's listing omits the save player
	/// one plays, so the opener states seven and steps over none). The run of 2026-11-28 18:07: the
	/// guest kept its cursor on save 1 from the open before, save 0 is the newest — the save player
	/// two had just quit — and saves 5 and 6 stand on page 2. Every save is reached with the D-pad or
	/// the stick (one press type), the page buttons and the pointer, and chosen with A or a click.
	/// </summary>
	[TestCase]
	public void SevenSavesWithPlayerOnesLeftOutAreEveryOneReachedAndChosen()
	{
		const int saves = 7;
		const uint valid = 0x7Fu;

		// Left from the kept cursor reaches save 0; right from there every save in turn, the
		// seventh held here past the guest's six; left walks all the way back.
		AssertThat(Walk(saves, -1, 1, null, Press.Left)).ContainsExactly(1, 0);
		AssertThat(Walk(saves, -1, 0, null, Press.Right)).ContainsExactly(0, 1, 2, 3, 4, 5, 6);
		AssertThat(Walk(saves, -1, 5, 6, Press.Left)).ContainsExactly(6, 5, 4, 3, 2, 1, 0);

		// A chooses the save under the cursor, the guest's on its six and this side's past them.
		for (int save = 0; save < Engine_CoopSavePicker.GuestCards; ++save)
		{
			AssertThat(Answer(saves, -1, valid, save, null, Press.Accept).Commit).IsEqual(save);
		}

		AssertThat(Answer(saves, -1, valid, 5, 6, Press.Accept).Commit).IsEqual(6);

		// Five to a page: page 2 holds saves 5 and 6, on cards 3 and 4.
		AssertThat(Engine_FrontEndSavePages.PageCount(saves)).IsEqual(2);
		AssertThat(Engine_FrontEndSavePages.FirstOn(1)).IsEqual(5);
		AssertThat(Engine_FrontEndSavePages.CountOn(saves, 1)).IsEqual(2);
		AssertThat(Engine_FrontEndSavePages.BaseSlot(2)).IsEqual(3);

		// The page buttons: from save 1 to save 5, nothing past page 2, back to save 4.
		(int guest, int? owned, _) = Answer(saves, -1, valid, 1, null, Press.PageNext);
		AssertThat(guest).IsEqual(5);
		AssertThat(owned is null).IsTrue();
		(guest, owned, _) = Answer(saves, -1, valid, 5, null, Press.PageNext);
		AssertThat(guest).IsEqual(5);
		AssertThat(owned is null).IsTrue();
		(guest, owned, _) = Answer(saves, -1, valid, 5, 6, Press.PagePrevious);
		AssertThat(guest).IsEqual(4);
		AssertThat(owned is null).IsTrue();

		// The pointer: on the guest's six its own presses walk there, past them this takes it; a click chooses.
		for (int save = 0; save < Engine_CoopSavePicker.GuestCards; ++save)
		{
			AssertThat(Engine_CoopSavePicker.Point(new Picker(saves, 1, -1, valid, null), save, true).Taken).IsFalse();
			AssertThat(Engine_CoopSavePicker.Presses(1, save, saves, -1)).IsEqual(save - 1);
		}

		Engine_CoopSavePicker.Outcome seventh = Engine_CoopSavePicker.Point(new Picker(saves, 1, -1, valid, null), 6, true);
		AssertThat(seventh.Owned).IsEqual(6);
		AssertThat(seventh.Commit).IsEqual(6);
	}

	/// <summary>
	/// Eight saves with player one's kept in the listing and stepped over: seven reached either way,
	/// wherever player one's stands — before the guest's edge, or on its sixth.
	/// </summary>
	[TestCase]
	public void PlayerOnesSaveSteppedOverLeavesSevenReached()
	{
		AssertThat(Walk(8, 2, 0, null, Press.Right)).ContainsExactly(0, 1, 3, 4, 5, 6, 7);
		AssertThat(Walk(8, 2, 5, 7, Press.Left)).ContainsExactly(7, 6, 5, 4, 3, 1, 0);
		AssertThat(Walk(8, 5, 0, null, Press.Right)).ContainsExactly(0, 1, 2, 3, 4, 6, 7);
		AssertThat(Walk(8, 5, 4, 7, Press.Left)).ContainsExactly(7, 6, 4, 3, 2, 1, 0);
	}
}
