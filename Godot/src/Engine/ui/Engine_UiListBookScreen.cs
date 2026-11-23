using Godot;

namespace Refii.Ui;

/// <summary>
/// A host list book: a list on the left, the highlighted row's detail on the page, and A
/// performing that row's action. The multiplayer book (friends, requests, invitations, games) and
/// the Active Mods book are two of these over their own lists.
/// </summary>
/// <remarks>
/// The same shape as <see cref="Engine_UiHostScreen"/> — a host-only screen id, the
/// logbook's furniture, an owned book and an owned list — with one difference that matters: an
/// achievement is a fact and this screen's rows are COMMANDS, so A dispatches through
/// <see cref="Action"/> rather than being swallowed.
/// </remarks>
public sealed class Engine_UiListBookScreen(uint screenId, string menuName, string viewName)
{
	/// <summary>The `.bgf` the screen borrows — the logbook's own furniture.</summary>
	public const string Presentation = "logbookscreen";

	/// <summary>The host the book and the list are parented to and take their art from.</summary>
	public Engine_UiPresentationHost? Host { get; set; }

	/// <summary>The book set the screen's owned book is built and kept in.</summary>
	public Engine_UiBookSet? Books { get; set; }

	/// <summary>Whether the screen is standing.</summary>
	public bool IsOpen { get; private set; }

	/// <summary>The row the cursor is on, for a caller that reports the list.</summary>
	public Engine_UiExpandableMenu.Item? Highlighted => _menu?.Highlighted;

	/// <summary>
	/// Where a row's command goes. The screen knows what was pressed; it does not know what a
	/// friend request or an invitation MEANS, and the dispatch that does already exists.
	/// </summary>
	public System.Action<string>? Action { get; set; }

	/// <summary>The view, for the shared list report.</summary>
	public Engine_FrontEndMenuView? View => _view;

	/// <summary>The book, whose right-hand page the mouse scrolls.</summary>
	public Engine_UiOwnedBook? Book => _book;

	/// <summary>
	/// Opens the screen. False when its art is not read yet — a pattern book is built from the
	/// banks on first ask, so that is "come back next frame", not "never".
	/// </summary>
	public bool Open()
	{
		if (!Build())
		{
			return false;
		}

		IsOpen = true;
		return true;
	}

	/// <summary>Takes the screen down. The book and the list are kept, only hidden.</summary>
	public void Close()
	{
		IsOpen = false;
		if (_view is not null && GodotObject.IsInstanceValid(_view))
		{
			_view.Visible = false;
		}

		if (_book is not null && GodotObject.IsInstanceValid(_book))
		{
			_book.Visible = false;
		}
	}

	/// <summary>
	/// One frame of the standing screen: the list takes the input, the provider fills the page.
	/// True when the screen consumed the frame's input.
	/// </summary>
	/// <param name="baseZ">
	/// The slab the screen stands in front of — the caller's current in-game presentation depth.
	/// A whole <see cref="Engine_UiPresentationDepth.Step"/> in front of it, because this is not
	/// a book standing over a mirror but a surface standing over the entire screen behind it.
	/// </param>
	/// <remarks>
	/// THE RIGHT STICK IS THE PAGE'S. The highlighted row's page is the book's scroll region
	/// (screen template <c>ScrollContent</c>), which the owned book scrolls with the right stick
	/// and the wheel as the logbook's pages do; the list is walked with the d-pad and left stick.
	/// </remarks>
	public bool Drive(
		int direction, bool accept, bool back, bool refresh, double delta, float baseZ)
	{
		if (!IsOpen || !Build())
		{
			return false;
		}

		// THE SAME TWO PLANES EVERY IN-GAME SCREEN USES, off a slab of our own.
		//
		// `GameUi` puts an owned book at `slab + BookOffset` and its row list at
		// `slab + ListLift`, and the default `ListLift` IS `BookOffset` — so the rows land on
		// the book's own root plane, and `LeftFrame`, authored in front of `Buttons` inside
		// `ExpandableMenu`, covers them. Standing the book at the slab and then lifting the
		// rows another quarter-step put every tab in front of the cover instead.
		//
		// The anchor's Z is deliberately NOT added, matching the in-game path; only the pause
		// list applies it, and that correction is being proved there first.
		float slab = baseZ + Engine_UiPresentationDepth.Step;
		_book!.Position = new Vector3(0f, 0f, slab + Engine_UiPresentationDepth.BookOffset);
		_book.Visible = true;

		_view!.Position = new Vector3(
			_anchor.X, _anchor.Y,
			slab +
			(Engine_UiTemplates.Screen(screenId)?.ListLift ??
				Engine_UiPresentationDepth.BookOffset));
		_view.Visible = true;

		// Y RE-READS. The sources hold their answer for a quarter second so a per-frame
		// re-source is not a per-frame P/Invoke; an explicit refresh drops that.
		if (refresh)
		{
			Engine_UiNetworkSource.Invalidate();
		}

		// ONCE A FRAME: the list arrives with the title's XDBF, which can land after the screen
		// is first opened, and a friend coming online has to appear under an already-open fold.
		// `RefreshSources` compares the finished set and does nothing when it has not moved.
		if (_template!.RefreshSources(_menu!))
		{
			_view.Refresh();
		}

		_view.Navigate(direction, delta);

		if (back)
		{
			// The one B rule every list keeps: a fold, then a mod menu opened over the list,
			// then the screen itself. The list holds group headers, so a fold can be open.
			if (!_view.Back() && !Engine_UiModMenus.Back(_view))
			{
				Close();
			}

			return true;
		}

		Fill();

		if (accept)
		{
			// A ROW IS A COMMAND HERE. The list holds folds too — a group header — so the fold
			// is offered first and only a leaf dispatches; `Activate` answers null for a fold,
			// which is exactly the "nothing to dispatch" case.
			// A FOLD ANSWERS NULL — `Activate` opens or shuts it and there is nothing to
			// dispatch. Only a leaf carries a command, and acting on one can change the list it
			// is in, so the sources are dropped and re-read.
			if (_menu!.Activate() is { Length: > 0 } command)
			{
				Action?.Invoke(command);
				Engine_UiNetworkSource.Invalidate();
			}

			_view!.Refresh();
		}

		return true;
	}

	/// <summary>Feeds the book the provider's four channels over an all-true draw gate.</summary>
	private void Fill()
	{
		Engine_UiExpandableMenu.Item? row = _menu?.Highlighted;
		_book!.HostValues = Engine_UiDetailPages.Values(
			screenId, row,
			default, default, EmptyQuests, default, default, default,
			_book.Book?.Strings);
		_book.HostTextures =
			Engine_UiDetailPages.Textures(screenId, row);
		_book.HostActive =
			Engine_UiDetailPages.Active(screenId, row);

		// NO SLIDES — see Engine_UiMultiplayerPage. Naming even one builds the whole array with
		// every other ordinal at 0, which resolves each owner to `logbookscreen`'s master
		// baseline and takes the book to 0 of 677 nodes drawing.
		_book.HostSlides = null;

		// THE DRAW GATE THE GUEST WOULD HAVE STATED. Sized to the document once and reused; the
		// provider's switches are applied over it inside the book.
		_book.SetLive(null, null, _drawn, null, null);
	}

	/// <summary>Builds the book and the list once. False while the art is still being read.</summary>
	private bool Build()
	{
		if (_book is not null && !GodotObject.IsInstanceValid(_book))
		{
			_book = null;
		}

		if (_view is not null && !GodotObject.IsInstanceValid(_view))
		{
			_view = null;
		}

		if (Host is null || Books is null)
		{
			return false;
		}

		_book ??= Books.ClaimPrebuilt(screenId, Presentation) ?? Books.Ensure(screenId, Presentation);
		if (_book is null)
		{
			return false;
		}

		if (_drawn is null)
		{
			if (Host.DocumentFor(Presentation) is not { } document)
			{
				return false;
			}

			_drawn = new bool[document.Nodes.Length];
			System.Array.Fill(_drawn, true);
		}

		if (_view is not null)
		{
			return true;
		}

		Engine_UiTheme? art = Host.PatternBookFor(Presentation);
		Node3D? pool = art is null ? null : Engine_UiButtonPool.FindButtonPool(art);
		if (art is null || pool is null)
		{
			return false;
		}

		// The anchor is the SCREEN's own composed spot, the same read the pause list makes: the
		// slot table is authored in the `Buttons` group's space and each screen parks that group
		// where it wants.
		_anchor = Engine_UiTheme.CanvasTransform(pool).Origin;

		Transform3D? cursorPose = null;
		if (art.ArtSourceNode("MenuHighlight") is { } highlight)
		{
			Transform3D authored = Engine_UiTheme.CanvasTransform(highlight);
			authored.Origin -= _anchor;
			cursorPose = authored;
		}

		_template = Engine_UiTemplates.Menu(menuName);
		_menu = _template?.Build();
		if (_menu is null)
		{
			return false;
		}

		_view = new Engine_FrontEndMenuView
		{
			Name = viewName,
			ScreenId = screenId,
			Theme = art,
			RowTemplate = Engine_FrontEndMenuView.RowTemplateIn(pool),
			CursorTemplate = art.Art("MenuHighlight"),
			CursorPose = cursorPose,
			Anchor = _anchor,
			Ratings = Engine_UiRowRating.Resolve(Host.DocumentFor(Presentation)),
			Visible = false,
		};

		Host.MenuLayer.AddChild(_view);
		_view.Bind(_menu);
		_view.ClipRect = Engine_UiButtonPool.ListClip(Host.DocumentFor(Presentation));

		GD.Print($"[list-book] {viewName} is up — {_menu.Visible.Count} row(s), " +
			$"row art {(_view.RowTemplate is null ? "NOT sourced (text-only rows)" : "cloned from the pattern book")}, " +
			$"anchor ({_anchor.X}, {_anchor.Y}, z {_anchor.Z})");
		return true;
	}

	private Engine_FrontEndMenu? _menu;

	private Engine_UiMenuTemplate? _template;

	private Engine_FrontEndMenuView? _view;

	private Engine_UiOwnedBook? _book;

	private Vector3 _anchor = Engine_FrontEndMenuView.CanvasAnchor;

	private bool[]? _drawn;

	private static readonly System.Collections.Generic.List<Refii.Host.HostBridge.CompletedQuest>
		EmptyQuests = new();
}
