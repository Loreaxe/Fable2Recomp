using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// SCREEN 28'S PAGES AS THEIR FILLS WROTE THEM (<see cref="Engine_UiPageFills"/>): page 1
/// `sub_82998FB0` and page 2 `sub_8299B068` at the open, page 1's tick `sub_8299A728` over them —
/// every text as the guest composed it, a row the fill hid blanked (the template blanks by value), the
/// star slide `sub_8299AF88` gave. <see cref="StatedValues"/> and <see cref="StatedSlides"/>.
///
/// The world map's Properties rows are not this screen: no guest page states a property the sign is not
/// open on, so their page is the owned record (<see cref="Engine_UiPropertySource.FillPage"/>).
/// </summary>
public static class Engine_UiRealEstatePage
{
	private const uint ScreenId = 28;

	/// <summary>Both pages' texts as the fills wrote them, page 1's tick over page 1; a row a fill hid
	/// is blank. Empty before the screen's pages have been filled.</summary>
	public static IReadOnlyDictionary<string, string> StatedValues()
	{
		var values = new Dictionary<string, string>(32);
		foreach (string scope in new[] { "page1", "page2", Engine_UiPageFills.Tick })
		{
			if (Engine_UiPageFills.Of(ScreenId, scope) is not { } fill)
			{
				continue;
			}

			foreach ((string path, string text) in fill.Texts)
			{
				values[path] = text;
			}

			foreach ((string path, bool shown) in fill.Shown)
			{
				if (!shown)
				{
					values[path] = string.Empty;
				}
				else if (!fill.Texts.ContainsKey(path) && values.TryGetValue(path, out string? hidden) && hidden.Length == 0)
				{
					values.Remove(path);
				}
			}
		}

		return values;
	}

	/// <summary>The slides the fills gave (the star strip, `sub_8299AF88`).</summary>
	public static IReadOnlyDictionary<string, int> StatedSlides()
	{
		var slides = new Dictionary<string, int>(2);
		foreach (string scope in new[] { "page1", "page2", Engine_UiPageFills.Tick })
		{
			if (Engine_UiPageFills.Of(ScreenId, scope) is { } fill)
			{
				foreach ((string path, int slide) in fill.Slides)
				{
					slides[path] = slide;
				}
			}
		}

		return slides.Count == 0 ? Engine_UiPageProviders.NoSlides : slides;
	}
}
