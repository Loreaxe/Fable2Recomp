namespace Refii.Titles.Fable3;

/// <summary>
/// The shop page's rules and paths (<see cref="Fable3ShopPage"/> draws by them; pure, no Godot type):
/// which states are the shop page's, which info-box slide that is, the texts' paths as the panel
/// writer builds them, and which lines show.
/// </summary>
public static class Fable3ShopPageRules
{
	/// <summary>The info box's slide for the shop page (SetPanel 2, 0x8295BE7C / disc 0x82B2EF4C).</summary>
	public const int ShopPagePanel = 2;

	public const string Root = "LowerElements.InfoBox";
	public const string Keeper = Root + ".NPCInfo.Contents.Name";
	public const string Title = Root + ".ShopPage.Contents.Title";
	public const int Lines = 4;

	/// <summary>Line n's (1-based) element: its `.Text` and `.Detail` are written beneath it.</summary>
	public static string Line(int n) => Root + ".ShopPage.Contents.Line" + n;

	/// <summary>Whether the page draws the shop's info box now, as its states say.</summary>
	public static bool Owned { get; set; }

	/// <summary>The page's rule: a shop mode is up and in a state whose page is the shop page.</summary>
	public static bool Owns(uint openShop, int? state) => openShop != 0u && state is 1 or 2 or 3;

	/// <summary>A line as its writer left it: shown unless hidden, its text and its detail.</summary>
	public readonly record struct PageLine(string Text, string Detail);

	/// <summary>The lines to draw: each line the writer did not hide and that holds a text or a detail.</summary>
	public static System.Collections.Generic.List<PageLine> LinesOf(System.Func<string, (bool? Visible, string? Text)> element)
	{
		var lines = new System.Collections.Generic.List<PageLine>();
		for (int n = 1; n <= Lines; ++n)
		{
			(bool? lineShown, _) = element(Line(n));
			(bool? textShown, string? text) = element(Line(n) + ".Text");
			(bool? detailShown, string? detail) = element(Line(n) + ".Detail");
			string shownText = lineShown == false || textShown == false ? string.Empty : text ?? string.Empty;
			string shownDetail = lineShown == false || detailShown == false ? string.Empty : detail ?? string.Empty;
			if (shownText.Length != 0 || shownDetail.Length != 0)
			{
				lines.Add(new PageLine(shownText, shownDetail));
			}
		}

		return lines;
	}
}
