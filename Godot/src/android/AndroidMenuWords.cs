using System;
using System.Collections.Generic;

namespace Refii.AndroidRuntime;

/// <summary>The host's own menu labels that speak of a desktop, as a phone says them.</summary>
/// <remarks>These rows are literal host labels with no string in the game's table: the front end's
/// way out and the pause menu's quit choices. On a phone there is no desktop to quit to; the rows
/// close the app, so they say so. Each title states its own (<see cref="Refii.Core.Engine_Title.PhoneHostLabels"/>).</remarks>
public static class AndroidMenuWords
{
    /// <summary>Fable 2's host rows (Fable2UiTemplates: the front end's and the pause menu's quit rows).</summary>
    public static readonly IReadOnlyDictionary<string, string> Fable2 = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["REFII_QUIT_TO_DESKTOP"] = "REFII_F2_QUIT_GAME",
        ["REFII_F2_SAVE_QUIT_DESKTOP"] = "REFII_F2_SAVE_QUIT_GAME",
        ["REFII_F2_QUIT_DESKTOP_NO_SAVE"] = "REFII_F2_QUIT_GAME_NO_SAVE",
    };

    /// <summary>A title with no such rows.</summary>
    public static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    /// <summary>The label key a phone shows for the unresolved host label key <paramref name="tag"/>; our own
    /// words (Refii.Text.Engine_OwnWords) say it in the language in force.</summary>
    public static string ForPhone(string tag) =>
        ForPhone(tag, Refii.Core.Engine_Platform.IsAndroid, Refii.Core.Engine_Title.Active);

    /// <summary>The same, with the platform and title given (the tests).</summary>
    internal static string ForPhone(string tag, bool phone, Refii.Core.Engine_Title title) =>
        phone && title.PhoneHostLabels.TryGetValue(tag, out string? said) ? said : tag;
}
