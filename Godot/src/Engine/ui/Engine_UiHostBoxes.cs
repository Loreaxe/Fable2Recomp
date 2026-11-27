using System;

namespace Refii.Ui;

/// <summary>
/// Questions and notices the HOST raises in the game's own boxes — in game the query box the
/// guest's questions are drawn with, owned by <see cref="Engine_UiQuerySurface"/> (a notice stands
/// its first choice alone); out of game the front end's own dialog
/// (<see cref="Engine_FrontEndWarning"/>, stood by GameUi) — for a flow that is ours rather than the
/// guest's, such as unlearning a mod's ability or a mod's message box (<see cref="Engine_UiModBoxes"/>).
/// Both draw the same `paperbox` paper, sized to the text and scrolled past the window
/// (<see cref="Engine_UiPaperBox"/>). The answer runs host code; nothing is posted to the guest.
/// </summary>
/// <remarks>
/// One of each on screen at a time, the rest QUEUED in order: a question raised while another is
/// up is asked when that one is answered, and a notice told under a notice follows it. Nothing
/// is answered -1 on another's account; -1 is only a withdrawal. An answer is the button's
/// index: 0 for A (the question's accept, the notice's dismiss), 1 for B, 2 for X (a question
/// asked with a third caption: the guest's three-button system box, Engine_UiSystemBoxes).
/// </remarks>
public static class Engine_UiHostBoxes
{
	/// <summary>The question up, or null.</summary>
	public static string? Question { get; private set; }

	/// <summary>The question's A caption, or null for the box's authored one.</summary>
	public static string? AcceptCaption { get; private set; }

	/// <summary>The question's B caption, or null for the box's authored one.</summary>
	public static string? CancelCaption { get; private set; }

	/// <summary>The question's X caption (answer 2), or null for a question of two answers.</summary>
	public static string? ThirdCaption { get; private set; }

	/// <summary>
	/// The question's page title when it is asked ON A PAGE (<see cref="AskPage"/>) — a body of
	/// unbounded length, drawn in the host page book's scroll region rather than the dialog's
	/// fixed text box, with Continue and Back as its rows. Null for a question in the dialog.
	/// </summary>
	public static string? PageTitle { get; private set; }

	/// <summary>The notice up, or null.</summary>
	public static string? Notice { get; private set; }

	private static Action<int>? _onQuestion;
	private static Action<int>? _onNotice;

	/// <summary>Who asked the question up, so its asker can take it back; empty for none.</summary>
	private static string _key = string.Empty;

	private readonly record struct PendingQuestion(
		string Body, string? Title, Action<int>? Answered, string? Accept, string? Cancel, string Key,
		string? Third = null);

	private readonly record struct PendingNotice(string Body, Action<int>? Dismissed);

	private static readonly System.Collections.Generic.List<PendingQuestion> _questions = new();

	private static readonly System.Collections.Generic.List<PendingNotice> _notices = new();

	private static void Show(PendingQuestion next)
	{
		Question = next.Body;
		PageTitle = next.Title;
		AcceptCaption = next.Accept;
		CancelCaption = next.Cancel;
		ThirdCaption = next.Third;
		_onQuestion = next.Answered;
		_key = next.Key;
		Godot.GD.Print($"[host-box] asked: {next.Body}");
	}

	private static void Show(PendingNotice next)
	{
		Notice = next.Body;
		_onNotice = next.Dismissed;
		Godot.GD.Print($"[host-box] told: {next.Body}");
	}

	/// <summary>
	/// Asks <paramref name="body"/> as a yes/no question; <paramref name="onYes"/> runs on A and
	/// <paramref name="onNo"/>, when given, on B. A <paramref name="key"/> lets the asker take the
	/// question back with <see cref="Withdraw"/> when it is answered somewhere else.
	/// </summary>
	public static void Ask(string body, Action onYes, Action? onNo = null, string key = "") =>
		Ask(body, answer =>
		{
			if (answer == 0)
			{
				onYes();
			}
			else if (answer == 1)
			{
				onNo?.Invoke();
			}
		}, null, null, key);

	/// <summary>
	/// Asks <paramref name="body"/> with the two captions given (null keeps the box's own);
	/// <paramref name="answered"/> gets 0 for A, 1 for B, -1 when it was withdrawn. Asked behind a question already up, it waits its turn.
	/// </summary>
	public static void Ask(string body, Action<int>? answered, string? accept, string? cancel, string key = "") =>
		Ask(new PendingQuestion(body, null, answered, accept, cancel, key));

	/// <summary>
	/// Asks <paramref name="body"/> with three answers: A (0), B (1) and X (2), each with its caption.
	/// </summary>
	public static void Ask(string body, Action<int>? answered, string accept, string cancel, string third, string key) =>
		Ask(new PendingQuestion(body, null, answered, accept, cancel, key, third));

	private static void Ask(PendingQuestion question)
	{
		if (!Refii.Core.Engine_TitleGame.Active.PresentsHostBoxes)
		{
			Godot.GD.Print($"[host-box] no presenter on this title: answered as withdrawn: {question.Body}");
			question.Answered?.Invoke(-1);
			return;
		}

		if (Question is not null)
		{
			_questions.Add(question);
			Godot.GD.Print($"[host-box] queued behind the question up: {question.Body}");
			return;
		}

		Show(question);
	}

	/// <summary>
	/// Asks <paramref name="body"/> on a PAGE: <paramref name="title"/> as the page's heading and
	/// the body in its scroll region, Continue (A, <paramref name="onYes"/>) and Back (B,
	/// <paramref name="onNo"/>) as the rows beside it. For a body whose length is not ours to
	/// bound — a list. The same question channel as <see cref="Ask"/>: one at a time, answered
	/// through <see cref="Answer"/>, withdrawn by <paramref name="key"/>.
	/// </summary>
	public static void AskPage(string title, string body, Action onYes, Action? onNo = null, string key = "") =>
		Ask(new PendingQuestion(body, title, answer =>
		{
			if (answer == 0)
			{
				onYes();
			}
			else if (answer == 1)
			{
				onNo?.Invoke();
			}
		}, null, null, key));

	/// <summary>Whether the question up was asked under <paramref name="key"/>.</summary>
	public static bool Asking(string key) => Question is not null && key.Length != 0 && _key == key;

	/// <summary>Closes the question asked under <paramref name="key"/> without running either answer.</summary>
	public static void Withdraw(string key)
	{
		if (key.Length != 0)
		{
			_questions.RemoveAll(q => q.Key == key);
		}

		if (!Asking(key))
		{
			return;
		}

		Question = null;
		PageTitle = null;
		AcceptCaption = null;
		CancelCaption = null;
		ThirdCaption = null;
		_onQuestion = null;
		_key = string.Empty;
		Godot.GD.Print($"[host-box] withdrawn: {key}");
		Next();
	}

	/// <summary>The next queued question, when none is up.</summary>
	private static void Next()
	{
		if (Question is null && _questions.Count != 0)
		{
			PendingQuestion next = _questions[0];
			_questions.RemoveAt(0);
			Show(next);
		}

		if (Notice is null && _notices.Count != 0)
		{
			PendingNotice next = _notices[0];
			_notices.RemoveAt(0);
			Show(next);
		}
	}

	/// <summary>Shows <paramref name="body"/> until it is dismissed.</summary>
	public static void Tell(string body) => Tell(body, null);

	/// <summary>
	/// Shows <paramref name="body"/> until it is dismissed; <paramref name="dismissed"/> gets 0 on
	/// the dismiss. Told under a notice already up, it waits its turn.
	/// </summary>
	public static void Tell(string body, Action<int>? dismissed)
	{
		if (!Refii.Core.Engine_TitleGame.Active.PresentsHostBoxes)
		{
			Engine_UiNotices.Post(0u, Engine_UiModBoxes.Resolve(0), string.Empty, body, 0f, done: _ => dismissed?.Invoke(0));
			return;
		}

		if (Notice is not null)
		{
			_notices.Add(new PendingNotice(body, dismissed));
			Godot.GD.Print($"[host-box] queued behind the notice up: {body}");
			return;
		}

		Show(new PendingNotice(body, dismissed));
	}

	/// <summary>The question's answer: A (<paramref name="yes"/>) or B. Closes it either way.</summary>
	public static void Answer(bool yes) => Answer(yes ? 0 : 1);

	/// <summary>
	/// The question's answer by index: 0 A, 1 B, 2 X (only while <see cref="ThirdCaption"/> is set;
	/// otherwise refused). Closes it.
	/// </summary>
	public static void Answer(int answer)
	{
		if (Question is null || answer is < 0 or > 2 || answer == 2 && ThirdCaption is null)
		{
			return;
		}

		Action<int>? answered = _onQuestion;
		Question = null;
		PageTitle = null;
		AcceptCaption = null;
		CancelCaption = null;
		ThirdCaption = null;
		_onQuestion = null;
		_key = string.Empty;
		answered?.Invoke(answer);
		Next();
	}

	public static void Dismiss()
	{
		Action<int>? dismissed = _onNotice;
		Notice = null;
		_onNotice = null;
		dismissed?.Invoke(0);
		Next();
	}
}
