using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE SHOP'S FRONT PAGE (screen 22), as its fill `sub_82991800` wrote it at the screen's open
/// (<see cref="Engine_UiPageFills"/>, scope "front"): under `Foreground.FrontPage.Contents.`, the
/// shop's name (or the trader's TOWN_CRIER / TRAVELING_TRADER), the anecdote, the `StarRating`
/// slide, the wealth line, the keeper's name, the `Group.OwnerKeeper` slide with the owner's title
/// and discount or the three opinions and the special reward, and the sale or shortage — each text
/// as the guest composed it, each group it hid as not shown.
/// </summary>
public static class Engine_UiShopFrontPage
{
	public const uint ScreenId = 22;

	public sealed class Statements
	{
		public readonly Dictionary<string, string> Values = new();
		public readonly Dictionary<string, bool> Active = new();
		public readonly Dictionary<string, int> Slides = new();
	}

	private static Engine_UiPageFills.Fill? _from;

	private static Statements? _said;

	/// <summary>The page's statements; null while the guest has filled none.</summary>
	public static Statements? State()
	{
		Engine_UiPageFills.Fill? fill = Engine_UiPageFills.Of(ScreenId, Engine_UiPageFills.Front);
		if (!ReferenceEquals(fill, _from))
		{
			_from = fill;
			_said = fill is null ? null : From(fill);
		}

		return _said;
	}

	/// <summary>The fill's writes as the page's three channels.</summary>
	public static Statements From(Engine_UiPageFills.Fill fill)
	{
		var said = new Statements();
		foreach ((string path, string text) in fill.Texts)
		{
			said.Values[path] = text;
		}

		foreach ((string path, bool shown) in fill.Shown)
		{
			said.Active[path] = shown;
		}

		foreach ((string path, int slide) in fill.Slides)
		{
			said.Slides[path] = slide;
		}

		return said;
	}
}
