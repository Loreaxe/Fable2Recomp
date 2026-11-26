namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// Fable 2's reads and levers beyond the shell's UI seam (<see cref="Engine_UiSource"/>): the front
/// end's own dialog, the spell wheel and blade, the `g_Menu:` chunks, the `GUI` predicate answers and
/// the list fold. Every read has an absent answer and every lever returns what the bridge returns.
/// </summary>
public abstract class Fable2UiSource : Engine_UiSource
{
	/// <summary>
	/// The session's source as Fable 2 reads it: <see cref="Engine_UiSource.Current"/> when it is a
	/// Fable 2 source (the live guest, <see cref="HostUiSource"/>), else one answering every read
	/// absent — what the shell's own sources answered.
	/// </summary>
	public static Fable2UiSource Active => Current as Fable2UiSource ?? Absent;

	private static readonly Fable2UiSource Absent = new AbsentSource();

	private sealed class AbsentSource : Fable2UiSource
	{
	}

	// ---- THE FRONT END ----------------------------------------------------------------

	/// <summary>
	/// The front end's own `dialog` box, raised by the guest (`sub_826E40E0`): whether its owner
	/// takes the answer yet, and whether the title raised it (the title takes A from any pad, the main
	/// menu from the pad the title took A from). What it says is the owner's statement
	/// (Engine_FrontEndStated.Dialog).
	/// </summary>
	public readonly record struct FrontEndDialogState(bool Accepting, bool Title);

	/// <summary>The front-end dialog that is up, or null.</summary>
	public virtual FrontEndDialogState? GetFrontEndDialog() => null;

	// ---- THE SPELL WHEEL AND BLADE ----------------------------------------------------

	/// <summary>The spell-assignment blade's model.</summary>
	public virtual Engine_UiModel.SpellAssignState GetSpellAssign() => default;

	/// <summary>The quick wheel's guest-held cursor — see <see cref="Engine_UiModel.SpellWheelState"/>.</summary>
	public virtual Engine_UiModel.SpellWheelState GetSpellWheel(int player) => default;

	/// <summary>The abilities book's blade widget, while that screen is up and the blade is open.</summary>
	public virtual Engine_UiModel.SpellWheelState GetSpellBlade() => default;

	// ---- THE UI CUT --------------------------------------------------------------------

	/// <summary>The `g_Menu:` mutations the game's C++ has made since the last drain, with their staged attributes. See docs/ui_native_cut.md.</summary>
	public virtual System.Collections.Generic.List<Engine_UiModel.MenuChunk> TakeMenuChunks() =>
		new();

	// ---- THE LEVERS --------------------------------------------------------------------

	/// <summary>Answers one `GUI` predicate instead of the guest. Only for a native whose setters this host owns; otherwise the answer is a guess.</summary>
	public virtual void SetPredicate(string name, bool value)
	{
	}

	/// <summary>Answers a float-returning predicate.</summary>
	public virtual void SetPredicate(string name, float value)
	{
	}

	/// <summary>
	/// Stops the guest running a native this host owns outright. Suppress only audited natives —
	/// some carry side effects (display box suspends gameplay, the minigame tally opens a screen).
	/// </summary>
	public virtual void SuppressNative(string name)
	{
	}

	/// <summary>States the fold depth of the list Godot shows for a screen (0 at the root, which
	/// clears) — see <c>HostBridge.SetListFoldDepth</c>.</summary>
	public virtual void StateListFoldDepth(uint screen, uint depth)
	{
	}
}
