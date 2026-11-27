namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The guest's system message box as the native side states it (RefiiHostGetSystemMessageBox):
/// id, user, flags, focus, count, title, text, captions, split by 0x1F.
/// </summary>
[TestSuite]
public class SystemBoxesTests
{
	private static string Record(params string[] fields) => string.Join(Engine_UiSystemBoxes.Separator, fields);

	[TestCase]
	public void TheSignInWarningReadsAsTheGuestStatedIt()
	{
		Engine_UiSystemBoxes.Box? box = Engine_UiSystemBoxes.Parse(
			Record("3", "1", "2", "0", "3", "Sign In", "Line one\nLine two", "Sign In", "Continue", "Cancel"));
		AssertThat(box is null).IsFalse();
		AssertThat(box!.Id).IsEqual(3u);
		AssertThat(box.User).IsEqual(1u);
		AssertThat(box.Flags).IsEqual(2u);
		AssertThat(box.Focus).IsEqual(0u);
		AssertThat(box.Title).IsEqual("Sign In");
		AssertThat(box.Text).IsEqual("Line one\nLine two");
		AssertThat(box.Buttons).ContainsExactly("Sign In", "Continue", "Cancel");
		AssertThat(Engine_UiSystemBoxes.Body(box)).IsEqual("Sign In\n\nLine one\nLine two");
	}

	[TestCase]
	public void ARecordThatDoesNotHoldTogetherIsNoBox()
	{
		AssertThat(Engine_UiSystemBoxes.Parse(null) is null).IsTrue();
		AssertThat(Engine_UiSystemBoxes.Parse(string.Empty) is null).IsTrue();
		// Two captions where the count says three.
		AssertThat(Engine_UiSystemBoxes.Parse(Record("3", "1", "2", "0", "3", "T", "X", "A", "B")) is null).IsTrue();
		// Id 0 is never a box.
		AssertThat(Engine_UiSystemBoxes.Parse(Record("0", "0", "0", "0", "1", "T", "X", "OK")) is null).IsTrue();
		AssertThat(Engine_UiSystemBoxes.Parse(Record("x", "0", "0", "0", "1", "T", "X", "OK")) is null).IsTrue();
	}

	[TestCase]
	public void AnEmptyTitleOrTextLeavesTheOtherAlone()
	{
		Engine_UiSystemBoxes.Box box = Engine_UiSystemBoxes.Parse(Record("1", "0", "0", "0", "1", string.Empty, "Body", "OK"))!;
		AssertThat(Engine_UiSystemBoxes.Body(box)).IsEqual("Body");
		box = Engine_UiSystemBoxes.Parse(Record("2", "0", "0", "0", "1", "Title", string.Empty, "OK"))!;
		AssertThat(Engine_UiSystemBoxes.Body(box)).IsEqual("Title");
	}
}
