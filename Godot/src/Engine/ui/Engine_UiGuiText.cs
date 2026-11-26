namespace Refii.Ui;

/// <summary>
/// Resolving the text a `GUI.*` native was passed.
/// </summary>
/// <remarks>
/// A script passes a Babel TAG, not a sentence — the display string is the host's to look up.
/// Three steps, in order: the tag as given, the tag with a `TEXT_` prefix (scripts are
/// inconsistent, and the engine's own text path retries with it), then the literal. The literal
/// fallback is correct rather than a guard — authored non-tag strings reach the same path — so
/// a miss is not an error. <see cref="Engine_UiScreen"/> resolves `.fac` text the same way.
/// </remarks>
public static class Engine_UiGuiText
{
	/// <summary>The prefix scripts sometimes omit.</summary>
	public const string TagPrefix = "TEXT_";

	/// <summary>
	/// The display string for a tag, or the tag itself when nothing resolves.
	/// </summary>
	/// <param name="tag">What the script passed.</param>
	/// <param name="strings">The loaded Babel database, or null before it is up.</param>
	/// <param name="localised">True when a lookup actually hit — for reports, never for logic.</param>
	public static string Resolve(
		string? tag, Refii.Text.Engine_BabelDatabase? strings, out bool localised)
	{
		localised = false;
		if (string.IsNullOrEmpty(tag))
		{
			return string.Empty;
		}

		if (strings is null)
		{
			return Refii.Text.Engine_OwnWords.Say(Refii.AndroidRuntime.AndroidMenuWords.ForPhone(tag));
		}

		if (strings.TryGetString(tag, out string direct))
		{
			localised = true;
			return direct;
		}

		if (!tag.StartsWith(TagPrefix, System.StringComparison.Ordinal) &&
			strings.TryGetString(TagPrefix + tag, out string prefixed))
		{
			localised = true;
			return prefixed;
		}

		return Refii.Text.Engine_OwnWords.Say(Refii.AndroidRuntime.AndroidMenuWords.ForPhone(tag));
	}

	/// <summary>The display string, discarding whether it resolved.</summary>
	public static string Resolve(string? tag, Refii.Text.Engine_BabelDatabase? strings) =>
		Resolve(tag, strings, out _);

	/// <summary>
	/// Substitutes `%1`, `%2`, … in a resolved string with a call's remaining arguments.
	/// </summary>
	/// <remarks>
	/// Part of the contract — the guest has a dedicated reader at `0x82302270`. Markers are
	/// ONE-BASED; one with no matching argument is left in place rather than blanked, so a call
	/// passing too few arguments stays visible.
	/// </remarks>
	public static string Substitute(string text, System.Collections.Generic.IReadOnlyList<string> values)
	{
		if (string.IsNullOrEmpty(text) || values.Count == 0 || !text.Contains('%'))
		{
			return text;
		}

		var built = new System.Text.StringBuilder(text.Length + 16);
		for (int i = 0; i < text.Length; ++i)
		{
			if (text[i] != '%' || i + 1 >= text.Length || !char.IsDigit(text[i + 1]))
			{
				built.Append(text[i]);
				continue;
			}

			int digits = 0;
			int index = 0;
			while (i + 1 + digits < text.Length && char.IsDigit(text[i + 1 + digits]))
			{
				index = (index * 10) + (text[i + 1 + digits] - '0');
				++digits;
			}

			if (index >= 1 && index <= values.Count)
			{
				built.Append(values[index - 1]);
				i += digits;
			}
			else
			{
				built.Append(text[i]);
			}
		}

		return built.ToString();
	}
}
