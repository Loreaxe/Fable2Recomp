namespace Refii.Titles.Fable3;

using Refii.Ui;

/// <summary>
/// Fable 3's platform keyboard: the shared <see cref="Engine_UiPlatformKeyboard"/>, whose one asker
/// here is the interaction mode's rename of the dog or a weapon
/// (`GUI.RenameEntityWithInteractionMode`). The guest renames when the text is non-empty and new
/// (sub_82962720) and posts "ClosedSystemUI" otherwise (sub_82962E28) — both its own. While it is open
/// <see cref="Fable3Input"/> keeps the pad from the guest.
/// </summary>
public partial class Fable3Keyboard : Engine_UiPlatformKeyboard
{
}
