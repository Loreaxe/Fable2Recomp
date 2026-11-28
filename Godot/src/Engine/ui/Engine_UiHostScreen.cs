using Godot;

namespace Refii.Ui;

/// <summary>
/// A HOST-OWNED BOOK SCREEN, stood by Godot alone from its template record: the achievements
/// screen (1013) and any <c>screens</c> record a mod states with an id of
/// <see cref="Engine_UiBookSet.HostOnlyScreenId"/> and up, a <c>menu</c> and a
/// <c>presentation</c>. Opened by the <c>Screen:open:&lt;id&gt;</c> verb
/// (<see cref="Engine_UiHostScreens"/>); its page is filled by the record's provider — a mod's by
/// <c>data</c> (<see cref="Engine_UiPageProviders.Data"/>), the record's own <c>fields</c>.
/// </summary>
/// <remarks>
/// <para>Every other in-game surface is a guest presentation the host mirrors or replaces. This one
/// has no guest behind it, so it is built from the FILE-ONLY pattern book, needs no live sample,
/// and can be opened on any frame whether or not the guest ever showed that file.</para>
/// <para>With no sample there is no DRAW GATE: the book's gate reads the guest's per-element draw
/// bits and a null sample reads as "nothing draws", so this screen states an all-true gate and lets
/// the provider switch off what it does not want. It states NO SLIDES unless its provider names
/// some: a host slide builds the whole array with every other ordinal at 0, which resolves each
/// owner to its master baseline — `logbookscreen`'s master took the book to 0 of 677 nodes drawing.</para>
/// <para>A row's command runs through the one dispatcher (<see cref="Engine_UiCommands"/>) on the
/// host list; a mod's row raises <c>ModMenuRow</c> first and <c>Opens:</c> opens over this list.</para>
/// </remarks>
public sealed class Engine_UiHostScreen
{
	public Engine_UiHostScreen(uint screenId) => ScreenId = screenId;

	public uint ScreenId { get; }

	/// <summary>The host the book and the list are parented to and take their art from.</summary>
	public Engine_UiPresentationHost? Host { get; set; }

	/// <summary>The book set the screen's owned book is built and kept in.</summary>
	public Engine_UiBookSet? Books { get; set; }

	/// <summary>Draws locked rows on slate (the achievements list's look).</summary>
	public bool LockedRowsSlate { get; init; }

	/// <summary>Hides the row rating strip (a list with no ratings).</summary>
	public bool HideRatingStrip { get; init; }

	/// <summary>Whether the screen is standing.</summary>
	public bool IsOpen { get; private set; }

	/// <summary>The player whose screen it is (1 or 2), as the opener stated.</summary>
	public int Player { get; private set; }

	/// <summary>The row the cursor is on, for a caller that reports the list.</summary>
	public Engine_UiExpandableMenu.Item? Highlighted => _menu?.Highlighted;

	/// <summary>The view, for the shared list report.</summary>
	public Engine_FrontEndMenuView? View => _view;

	private Engine_UiScreenTemplate? Record => Engine_UiTemplates.Screen(ScreenId);

	/// <summary>
	/// Opens the screen for <paramref name="player"/>. False when its art is not read yet — a
	/// pattern book is built from the banks on first ask, so that is "come back next frame".
	/// </summary>
	public bool Open(int player = 0)
	{
		if (!Build())
		{
			return false;
		}

		Player = player;
		IsOpen = true;
		Engine_UiModMenus.RaiseHostScreen(ScreenId, player, open: true);
		return true;
	}

	/// <summary>Takes the screen down. The book and the list are kept, only hidden.</summary>
	public void Close()
	{
		if (IsOpen)
		{
			Engine_UiModMenus.RaiseHostScreen(ScreenId, Player, open: false);
		}

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
	/// Drops what was built, so the next open builds again from the record as it stands now — a
	/// remount that changed the record or its art.
	/// </summary>
	public void Forget()
	{
		Close();
		if (_view is not null && GodotObject.IsInstanceValid(_view))
		{
			_view.QueueFree();
		}

		_view = null;
		_book = null;
		_menu = null;
		_template = null;
		_drawn = null;
	}

	/// <summary>
	/// One frame of the standing screen: the list takes the input, the provider fills the page.
	/// True when the screen consumed the frame's input.
	/// </summary>
	/// <param name="baseZ">
	/// The slab the screen stands in front of — the caller's current in-game presentation depth.
	/// A whole <see cref="Engine_UiPresentationDepth.Step"/> in front of it: this is a surface
	/// standing over the entire screen behind it, not a book standing over a mirror.
	/// </param>
	public bool Drive(int direction, bool accept, bool back, double delta, float baseZ)
	{
		if (!IsOpen || !Build())
		{
			return false;
		}

		// THE SAME TWO PLANES EVERY IN-GAME SCREEN USES, off a slab of our own: the book at
		// `slab + BookOffset` and the rows at `slab + ListLift`, so `LeftFrame`, authored in front
		// of `Buttons` inside `ExpandableMenu`, covers them. The anchor's Z is not added, matching
		// the in-game path.
		float slab = baseZ + Engine_UiPresentationDepth.Step;
		_book!.Position = new Vector3(0f, 0f, slab + Engine_UiPresentationDepth.BookOffset);
		_book.Visible = true;

		_view!.Position = new Vector3(
			_anchor.X, _anchor.Y, slab + (Record?.ListLift ?? Engine_UiPresentationDepth.BookOffset));
		_view.Visible = true;

		// A sourced list (the title's XDBF, a mod's rows) can land after the screen is first
		// opened. `RefreshSources` compares the set and does nothing when it has not moved.
		if (_template!.RefreshSources(_menu!))
		{
			_view.Refresh();
		}

		_view.Navigate(direction, delta);

		if (back)
		{
			// A fold, then a mod menu opened over the list, then the screen itself.
			if (!_view.Back() && !Engine_UiModMenus.Back(_view))
			{
				Close();
			}

			return true;
		}

		Fill();

		if (accept)
		{
			Engine_UiExpandableMenu.Item? chosen = _view.Menu?.Highlighted;
			string? action = _view.Activate();
			string menu = Record?.Menu ?? string.Empty;
			if (!Engine_UiModMenus.Activated(_view, menu, chosen, action, Engine_UiCommandList.HostList) &&
				action is { Length: > 0 })
			{
				Engine_UiCommands.Run(action, new Engine_UiCommandContext(
					Engine_UiCommandList.HostList, menu, chosen?.Name ?? string.Empty, Player,
					Engine_UiTemplates.MenuOwner(menu, chosen?.Name ?? string.Empty)) { View = _view });
			}

			_view.Refresh();
		}

		return true;
	}

	/// <summary>Feeds the book the provider's channels over an all-true draw gate.</summary>
	private void Fill()
	{
		Engine_UiExpandableMenu.Item? row = _menu?.Highlighted;
		_book!.HostValues = Engine_UiDetailPages.Values(
			ScreenId, row, default, default, EmptyQuests, default, default, _book.Book?.Strings);
		_book.HostTextures = Engine_UiDetailPages.Textures(ScreenId, row);
		_book.HostActive = Engine_UiDetailPages.Active(ScreenId, row);

		// The provider's own slides only. The book's leaf/folder page rule (`hostRows`) is for a
		// host row inside a guest-driven book; this screen draws the whole book itself and its
		// pages always stand. Naming `Foreground.Book` builds the whole slide array, every other
		// element at 0 — logbookscreen's master — and took the achievements book to 0/677 drawn.
		System.Collections.Generic.IReadOnlyDictionary<string, int> slides = Engine_UiDetailPages.Slides(ScreenId, row, hostRows: false);
		_book.HostSlides = slides.Count == 0 ? null : slides;

		// THE DRAW GATE THE GUEST WOULD HAVE STATED, sized to the document once; the provider's
		// switches are applied over it inside the book.
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

		if (Host is null || Books is null || Record is not { } record ||
			record.Presentation.Length == 0 || record.Menu.Length == 0)
		{
			return false;
		}

		string presentation = record.Presentation;
		_book ??= Books.ClaimPrebuilt(ScreenId, presentation) ?? Books.Ensure(ScreenId, presentation);
		if (_book is null)
		{
			return false;
		}

		if (_drawn is null)
		{
			if (Host.DocumentFor(presentation) is not { } document)
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

		Engine_UiTheme? art = Host.PatternBookFor(presentation);
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

		_template = Engine_UiTemplates.Menu(record.Menu);
		_menu = _template?.Build();
		if (_menu is null)
		{
			GD.PushWarning($"[host-screen] screen {ScreenId} names menu '{record.Menu}', which no ui/templates.json defines");
			return false;
		}

		_view = new Engine_FrontEndMenuView
		{
			Name = $"HostScreen{ScreenId}",
			ScreenId = ScreenId,
			Theme = art,
			RowTemplate = Engine_FrontEndMenuView.RowTemplateIn(pool),
			CursorTemplate = art.Art("MenuHighlight"),
			CursorPose = cursorPose,
			Anchor = _anchor,
			LockedRowsSlate = LockedRowsSlate,
			HideRatingStrip = HideRatingStrip,
			Visible = false,
		};

		Host.MenuLayer.AddChild(_view);
		_view.Bind(_menu);
		_view.ClipRect = Engine_UiButtonPool.ListClip(Host.DocumentFor(presentation));

		GD.Print($"[host-screen] screen {ScreenId} ('{record.Name}') is up — {_menu.Visible.Count} row(s) of " +
			$"'{record.Menu}' on '{presentation}', provider '{record.Provider}', row art " +
			(_view.RowTemplate is null ? "NOT sourced (text-only rows)" : "cloned from the pattern book"));
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
