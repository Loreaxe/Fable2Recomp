namespace Refii.Titles.Fable3;

using Godot;
using Refii.Host;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// THE SHOP PAGE (interaction mode 1, CInteractionModeMenuShop), drawn by Godot beside the interaction
/// list in the PC arrangement, from what the guest states: the shop mode's state (its store
/// instructions, <see cref="Fable3CommerceStores"/>), the info panel's texts as the panel writer
/// sub_8295B980 / disc sub_82B2EA50 passes them to SetTextAt, and the lines' shown / hidden as it passes
/// them to its show-by-path sub_82877898. While it draws, the document rests the info box's shop
/// slide closed (<see cref="Fable3HudRegistry.DrawnPanel"/>). Its rules are <see cref="Fable3ShopPageRules"/>.
/// </summary>
/// <remarks>
/// <para>THE TEXTS (both builds the same paths; the CCharStrings are built at load from
/// `LowerElements.InfoBox` (sub_831E64A8) plus a suffix: sub_8320A888 `.ShopPage.Contents.Title`,
/// sub_8320A8E0 `.ShopPage.Contents.Line1`, and Line2..Line4, each written as its `.Text` and its
/// `.Detail` (dword_8359CBC4 / dword_8359CBC8)). The panel writes them only in the states past 0
/// (`lwz …,0xF8` 0x8295BA40, then SetPanel 2 at 0x8295BE7C / disc 0x82B2EF4C); in state 0 it writes the
/// keeper's card (SetPanel 5 at 0x8295BC20 / 0x82B2ECF0), `LowerElements.InfoBox.NPCInfo.Contents.Name`
/// and its traits, which the document keeps drawing as the NPC's card. The keeper's name heads this
/// page as its writer left it.</para>
/// <para>WHICH STATES. 1 browsing, 2 stealing or haggling and 3 just bought are the shop page's; 0 is
/// the keeper's card and 4 the weapon page (its own slide), which this page leaves to the document.</para>
/// <para>INPUT. The page shows; it takes nothing. The rows are the interaction list's (pointer and pad),
/// the buttons the document's InteractionButtons (A, B, X, Y as the guest offers them).</para>
/// </remarks>
public partial class Fable3ShopPage : Control
{
	// THE ARRANGEMENT, canvas units about the screen's centre, y up: the list's mirror on the right.
	public const float PanelX = 451f;
	public const float PanelTop = -150f;
	public const float PanelWidth = 360f;
	public const float LinePitch = 30f;
	public const float Inset = 14f;

	private const string BackingArt = "art/gui_memmount/hudassests/version4/middleelements/options/selectionbar_background.tex";

	private static (bool? Visible, string? Text) Element(string path)
	{
		Fable3Elements.Element? element = Fable3Elements.Get(Fable3Elements.InteractionDocument, path);
		return (element?.Visible, element?.StatedTextValue);
	}

	private UiFont? _font;
	private string _culture = string.Empty;
	private Texture2D? _backing;
	private ulong _drawn = ulong.MaxValue;

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
	}

	public override void _Process(double delta)
	{
		uint shop = Fable3CommerceStores.OpenShop;
		bool owned = HostBridge.IsInGame && Fable3ShopPageRules.Owns(shop, Fable3CommerceStores.Of(Fable3CommerceStores.Field.ShopState, shop));
		if (owned != Fable3ShopPageRules.Owned)
		{
			Fable3ShopPageRules.Owned = owned;
			GD.Print($"[fable3-shop] page {(owned ? "up" : "down")} (shop {shop:X8}, state " +
				$"{Fable3CommerceStores.Of(Fable3CommerceStores.Field.ShopState, shop)?.ToString() ?? "none"})");
			Fable3HudRegistry.DrawPanel();
			QueueRedraw();
		}

		ulong stamp = Fable3Elements.Generation ^ (Fable3CommerceStores.Generation << 24);
		if (Fable3ShopPageRules.Owned && stamp != _drawn)
		{
			_drawn = stamp;
			QueueRedraw();
		}
	}

	public override void _Draw()
	{
		if (!Fable3ShopPageRules.Owned)
		{
			return;
		}

		if (_culture != Engine_GameCulture.Current || _font is null)
		{
			_culture = Engine_GameCulture.Current;
			_font = Fable3Fonts.For(_culture).Get(UiFontBucket.Normal);
		}

		_backing ??= Fable3Art.Load(BackingArt);
		if (_font is null)
		{
			return;
		}

		Vector2 size = GetViewportRect().Size;
		float scale = size.Y / Fable3Canvas.CanvasHeight * Fable3Canvas.PixelsPerUnit;
		float line = _font.Font.LineHeight;
		float text = 32f * 0.62f * scale / line;
		System.Collections.Generic.List<Fable3ShopPageRules.PageLine> lines = Fable3ShopPageRules.LinesOf(Element);
		string keeper = Element(Fable3ShopPageRules.Keeper).Text ?? string.Empty;
		string title = Element(Fable3ShopPageRules.Title).Text ?? string.Empty;
		int rows = lines.Count + (keeper.Length != 0 ? 1 : 0) + (title.Length != 0 ? 1 : 0);
		if (rows == 0)
		{
			return;
		}

		var origin = new Vector2((size.X * 0.5f) + ((PanelX - (PanelWidth * 0.5f)) * scale), (size.Y * 0.5f) - (PanelTop * scale));
		var box = new Rect2(origin, new Vector2(PanelWidth * scale, ((rows * LinePitch) + Inset) * scale));
		if (_backing is null)
		{
			DrawRect(box, new Color(0f, 0f, 0f, 0.7f));
		}
		else
		{
			DrawTextureRect(_backing, box, false, new Color(1f, 1f, 1f, 0.85f));
		}

		// The texts are the guest's and carry its markup: the price's detail is the number with the
		// guest's own `<gold_img>` appended (TU3 sub_8295B980 0x8295BF64..0x8295BFA4, disc sub_82B2EA50
		// 0x82B2F034..0x82B2F074), drawn as the coin, never as its letters.
		Engine_UiTextContract contract = Engine_UiTextContract.Fable3;
		float y = origin.Y + (Inset * 0.5f * scale);
		void Row(string left, string right, float alpha, float size_)
		{
			float textScale = text * size_;
			Engine_UiCanvasText.DrawMarked(this, _font!, left, new Vector2(origin.X + (Inset * scale), y), textScale,
				new Color(1f, 1f, 1f, alpha), contract, Fable3Art.Load);
			if (right.Length != 0)
			{
				float width = Engine_UiCanvasText.MeasureMarked(_font!, right, contract) * textScale;
				Engine_UiCanvasText.DrawMarked(this, _font!, right,
					new Vector2(origin.X + box.Size.X - (Inset * scale) - width, y), textScale, new Color(1f, 0.85f, 0.45f, alpha),
					contract, Fable3Art.Load);
			}

			y += LinePitch * scale;
		}

		if (keeper.Length != 0)
		{
			Row(keeper, string.Empty, 0.7f, 14f / 16f);
		}

		if (title.Length != 0)
		{
			Row(title, string.Empty, 1f, 1f);
		}

		foreach (Fable3ShopPageRules.PageLine page in lines)
		{
			Row(page.Text, page.Detail, 0.9f, 14f / 16f);
		}
	}
}
