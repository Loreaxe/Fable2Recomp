namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// A MOD'S MENU ON FABLE 3 (<see cref="Engine_UiModMenus"/>: <c>Refii.open_menu</c>, Godot's <c>open_menu</c>,
/// <c>refii_mod_open_menu</c>, a menu from a mod's <c>ui/templates.json</c>). The shared list view runs it,
/// undrawn — the template's build, folds, sub-menus, row commands, the mod_menu_row event and Back are the
/// engine's, as on Fable 2 — and Fable 3 draws its rows as its own boxes draw. The pad is held from the game
/// while it stands (<see cref="Fable3Input"/>); a box a row raises draws over it.
/// </summary>
public partial class Fable3Boxes
{
	private Engine_FrontEndMenuView? _modMenu;
	private string _modMenuRoot = string.Empty;
	private uint _menuWasDown;

	/// <summary>The player the menu was opened for (Engine_UiModMenus.Standalone.Player): the menu takes that player's pad.</summary>
	private int _modMenuPlayer = Fable3ActingPlayer.One;

	/// <summary>The buttons down on the pad of the player the menu stands for.</summary>
	private uint ModMenuPad() => Refii.Game.GameInput.Seen(Fable3ActingPlayer.PadOf(_modMenuPlayer)).Buttons;

	/// <summary>A mod's menu stands and has the pad.</summary>
	public static bool ModMenuUp { get; private set; }

	/// <summary>Once a frame: a requested menu opened, the open one driven. True while it has the pad.</summary>
	private bool DriveModMenu(double delta, bool input)
	{
		if (Engine_UiModMenus.TakeStandalone() is { } request)
		{
			OpenModMenu(request);
		}

		if (_modMenu is null)
		{
			ModMenuUp = false;
			return false;
		}

		if (!IsInstanceValid(_modMenu) || !HostBridge.IsGuestUp)
		{
			CloseModMenu();
			return false;
		}

		ModMenuUp = true;
		Engine_UiModMenus.Focus(_modMenu, _modMenuRoot, Engine_UiCommandList.HostList);
		uint down = ModMenuPad();
		uint pressed = down & ~_menuWasDown;
		_menuWasDown = down;
		if (!input)
		{
			return true;
		}

		int direction = (down & Refii.Game.GameInput.DpadDown) != 0 ? 1 : (down & Refii.Game.GameInput.DpadUp) != 0 ? -1 : 0;
		if (_modMenu.Navigate(direction, delta))
		{
			QueueRedraw();
		}

		if ((pressed & Refii.Game.GameInput.ButtonB) != 0)
		{
			if (!_modMenu.Back() && !Engine_UiModMenus.Back(_modMenu))
			{
				CloseModMenu();
			}

			Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
			QueueRedraw();
			return true;
		}

		if ((pressed & Refii.Game.GameInput.ButtonA) != 0)
		{
			Engine_UiExpandableMenu.Item? chosen = _modMenu.Menu?.Highlighted;
			string? action = _modMenu.Activate();
			Engine_UiModMenus.Activated(_modMenu, _modMenuRoot, chosen, action, Engine_UiCommandList.HostList, rootIsModMenu: true);
			if (_modMenu is not null && IsInstanceValid(_modMenu))
			{
				// An entry naming a screen latches it for a front end; there is none under this list.
				_modMenu.TakeOpenedScreen();
				_modMenu.Refresh();
			}

			Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
			QueueRedraw();
		}

		return true;
	}

	/// <summary>
	/// A MOD'S SCREEN ON FABLE 3 (<c>open_screen</c> / <c>Screen:open:&lt;id&gt;</c>, a ui/templates.json
	/// <c>screens</c> record, id 1000 and up): Fable 2 stands it as a page of its book from the record's
	/// presentation; Fable 3 has no book, so the record's menu stands as a mod's menu does here, in Fable 3's
	/// box look — its rows, sub-menus, commands and Back the same. The presentation's page art is Fable 2's.
	/// </summary>
	private Engine_UiCommandResult OpenHostScreen(uint screenId, int player, Engine_UiCommandContext context)
	{
		if (Engine_UiTemplates.Screen(screenId) is not { } record || Engine_UiTemplates.Menu(record.Menu) is null)
		{
			return Engine_UiCommandResult.Refused($"screen {screenId} names no menu Fable 3 can stand");
		}

		OpenModMenu(new Engine_UiModMenus.Standalone(record.Menu, player));
		return _modMenu is not null ? Engine_UiCommandResult.Done : Engine_UiCommandResult.Refused($"screen {screenId}: its menu did not open");
	}

	private void OpenModMenu(Engine_UiModMenus.Standalone request)
	{
		if (Engine_UiTemplates.Menu(request.Menu) is not { } template)
		{
			GD.PushWarning($"[fable3-boxes] open_menu: no menu template '{request.Menu}'");
			return;
		}

		CloseModMenu();
		// The shared view with no book: text-only rows, never shown — Fable 3 draws the model below.
		Engine_FrontEndMenuView view = Engine_FrontEndMenuView.Dressed("HostModMenu", null, bookLook: false, null, null);
		view.Visible = false;
		AddChild(view);
		view.Bind(template.Build());
		_modMenu = view;
		_modMenuRoot = request.Menu;
		_modMenuPlayer = request.Player == Fable3ActingPlayer.Two ? Fable3ActingPlayer.Two : Fable3ActingPlayer.One;
		_menuWasDown = ModMenuPad();
		Engine_UiModMenus.StandaloneOpened(view, request.Menu, request.Player);
		QueueRedraw();
	}

	private void CloseModMenu()
	{
		if (_modMenu is null)
		{
			return;
		}

		if (IsInstanceValid(_modMenu))
		{
			_modMenu.QueueFree();
		}

		_modMenu = null;
		ModMenuUp = false;
		Engine_UiModMenus.StandaloneClosed(_modMenuRoot);
		_modMenuRoot = string.Empty;
		QueueRedraw();
	}

	/// <summary>The menu's rows in Fable 3's box look: the scrolled window of its visible rows, indented by depth.</summary>
	private void DrawModMenu(Vector2 size, float scale, Engine_BabelDatabase? strings)
	{
		if (_modMenu?.Menu is not { } menu || _font is null)
		{
			return;
		}

		IReadOnlyList<Engine_UiExpandableMenu.Item> rows = menu.Visible;
		// The window is 1-based and inclusive (the formatter's display indices); the list is 0-based.
		(int first, int last) = menu.Model.Window();
		first -= 1;
		last -= 1;
		first = Mathf.Clamp(first, 0, rows.Count);
		last = Mathf.Clamp(last, first - 1, rows.Count - 1);
		int count = last - first + 1;
		float width = Mathf.Min(BoxWidth * scale, size.X - (2f * Padding * scale));
		float inner = width - (2f * Padding * scale);
		float lineHeight = _font.Font.LineHeight * scale;
		float rowHeight = lineHeight + (6f * scale);
		// The open menu's own template title (a tag or literal), else its name.
		string shownMenu = _modMenu.OpenedMenuName ?? _modMenuRoot;
		string title = Engine_UiTemplates.Menu(shownMenu) is { Title.Length: > 0 } shown ? shown.Title : shownMenu;
		float height = lineHeight + (Mathf.Max(count, 1) * rowHeight) + lineHeight + (3f * Padding * scale);
		var rect = new Rect2(new Vector2((size.X - width) * 0.5f, (size.Y - height) * 0.5f), new Vector2(width, height));
		DrawRect(rect, Panel);

		Vector2 pen = rect.Position + new Vector2(Padding * scale, Padding * scale);
		Engine_UiCanvasText.DrawLine(this, _font, Text(title, strings), pen, scale, TitleColour);
		pen.Y += lineHeight + (Padding * 0.5f * scale);
		Engine_UiExpandableMenu.Item? highlighted = menu.Highlighted;
		for (int i = first; i <= last; ++i)
		{
			Engine_UiExpandableMenu.Item item = rows[i];
			if (ReferenceEquals(item, highlighted))
			{
				DrawRect(new Rect2(pen - new Vector2(6f * scale, 2f * scale), new Vector2(inner + (12f * scale), rowHeight)), new Color(1f, 1f, 1f, 0.15f));
			}

			float indent = Mathf.Max(0, item.Depth - 1) * 18f * scale;
			string label = item.StatedLabel ?? Text(item.TextTag, strings);
			if (item.Children.Count > 0)
			{
				label += item.IsOpen ? "  -" : "  +";
			}

			Engine_UiCanvasText.DrawLine(this, _font, label, pen + new Vector2(indent, 0f), scale, item.IsGhosted ? Disabled : Colors.White);
			pen.Y += rowHeight;
		}

		string back = Label(null, "GUI_DIALOG_BACK", strings);
		float backWidth = Fable3Prompts.Measure(_font, back, scale);
		Fable3Prompts.Draw(this, _font, "B", back,
			new Vector2(rect.End.X - (Padding * scale) - backWidth, rect.End.Y - (Padding * scale) - lineHeight), scale, Colors.White);
	}

	/// <summary>A row or title tag in Fable 3's text, else the shared view's own reading (a mod's {TAG} literal, or as written).</summary>
	private string Text(string tag, Engine_BabelDatabase? strings)
	{
		string resolved = Engine_UiGuiText.Resolve(tag, strings);
		return resolved != tag || _modMenu is null ? resolved : _modMenu.Resolve(tag);
	}
}
