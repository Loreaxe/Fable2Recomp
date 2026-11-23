using Godot;

namespace Refii.Ui;

/// <summary>
/// Every owned book — one per screen — and the two ways they are stood up.
///
/// A book is a screen's chrome rebuilt from its template out of the FILE-ONLY pattern book, in
/// place of the mirrored tree; the guest still states every value. One construction serves all,
/// so a screen becoming owned is a template entry and nothing else.
///
/// Two drivers: in-game books are found in the presentation registry, while the front end never
/// appears there and is stood up by name from its screen object's fields.
/// </summary>
public sealed class Engine_UiBookSet
{
	/// <summary>The host the books are parented to and take their art from.</summary>
	public Engine_UiPresentationHost? Host { get; set; }

	/// <summary>The guest's GUI registries as stated — handed to every book this set makes
	/// (<see cref="Engine_UiOwnedBook.RegistryText"/>, <see cref="Engine_UiOwnedBook.RegistrySlide"/>).</summary>
	public System.Func<Engine_HudValues?>? Registry { get; set; }

	/// <summary>
	/// A book's behaviour fired its presentation event under the book's own playback — the
	/// presentation's file name and the event (Engine_UiBehaviourEvents). The title states where it goes.
	/// </summary>
	public System.Action<string, Engine_UiBehaviourEvents.Fired>? BehaviourFired { get; set; }

	/// <summary>One screen's book, or null when it has none yet.</summary>
	public Engine_UiOwnedBook? Book(uint screenId) =>
		_books.TryGetValue(screenId, out Engine_UiOwnedBook? book) &&
		GodotObject.IsInstanceValid(book)
			? book
			: null;

	/// <summary>Registers a book <see cref="Ensure"/> built for a caller that stands it itself (Engine_UiScreenStand).</summary>
	public void Keep(uint screenId, Engine_UiOwnedBook book) => _books[screenId] = book;

	/// <summary>Forgets every book — their owner frees them.</summary>
	public void ForgetAll()
	{
		_books.Clear();
		_made.Clear();
	}

	/// <summary>Forgets which book the front end is standing — the guest has left it.</summary>
	public void ReleaseFrontEnd() => FrontEndBookId = 0;

	/// <summary>
	/// Builds one screen's owned book from its template, or null when the template names no
	/// chrome for it. Built once per screen and kept: its art is cloned from the FILE-ONLY
	/// pattern book, so it survives the rebuilds a guest slide change triggers on the live tree.
	/// </summary>
	public Engine_UiOwnedBook? Ensure(uint screenId, string presentation)
	{
		if (Host is null ||
			Engine_UiTemplates.Screen(screenId) is not { ChromeUnits.Count: > 0 } template)
		{
			return null;
		}

		// The art has to exist first: a presentation appears in the registry before its pattern
		// book is read, and building then yields a book with no units. Null here means "not
		// yet"; the caller asks again next frame.
		Engine_UiTheme? art = Host.PatternBookFor(presentation);
		Engine_UiScreen? document = Host.DocumentFor(presentation);
		if (art is null || document is null)
		{
			return null;
		}

		var book = new Engine_UiOwnedBook
		{
			Name = $"OwnedBook{screenId}",
			Template = template,
			Book = art,
			Document = document,
			Visible = false,
			ResolveTexture = hash =>
				Host.TextureNames is { } names &&
				names.TryResolve(hash, out string path)
					? Host.LoadTexture(path)
					: null,
			NameTexture = hash =>
				Host.TextureNames is { } names && names.TryResolve(hash, out string path) ? path : null,
		};
		Wire(book);
		book.BehaviourFired = fired => BehaviourFired?.Invoke(presentation, fired);

		// Suppression is applied at bind time, so it must be registered before Build().
		foreach (string owned in template.OwnedElsewhere)
		{
			book.Suppressed.Add(owned);
		}

		using (Refii.Core.Engine_FramePhases.Measure("ui.book.build"))
		{
			book.Build();
			book.DriveFrames();
		}

		Host.MenuLayer.AddChild(book);
		_made.Add((book, presentation));
		return book;
	}

	/// <summary>
	/// Builds ONE owned book that no screen has asked for yet, and returns whether it did —
	/// for a caller with a frame to spare (the loading screen), so the build lands there and
	/// not on the frame the player first opens the screen. Every template with chrome and an
	/// in-game presentation is a candidate; each is tried once.
	///
	/// The book is SET ASIDE, not registered: it joins <see cref="_books"/> only when the path
	/// that would have built it claims it (<see cref="ClaimPrebuilt"/>), so nothing about when
	/// a book is driven, hidden or stood changes — the registry's trailing hide pass never sees
	/// a book the player has not reached, and the pause book, which the pause view owns outside
	/// the registry, is never in it at all.
	/// </summary>
	public bool PrebuildOne()
	{
		if (Host is null)
		{
			return false;
		}

		foreach (Engine_UiScreenTemplate template in Engine_UiTemplates.Screens)
		{
			uint screenId = template.ScreenId;
			if (template.ChromeUnits.Count == 0 || template.Presentation.Length == 0 ||
				Refii.Core.Engine_TitleGame.Active.IsFrontEndPresentation(template.Presentation) ||
				_bookless.Contains(screenId) || !Engine_UiBookPrebuild.Wants(template.Name) || !_prebuildTried.Add(screenId) ||
				Book(screenId) is not null)
			{
				continue;
			}

			if (Ensure(screenId, template.Presentation) is { } made)
			{
				_prebuilt[screenId] = (made, template.Presentation);
				GD.Print($"[owned-book] '{template.Name}' prebuilt for screen {screenId} " +
					$"('{template.Presentation}') ahead of its first open");
			}

			return true;
		}

		return false;
	}

	/// <summary>
	/// Builds a FRONT-END screen's book before the guest asks for it, and keeps it hidden where
	/// <see cref="DriveFrontEnd"/> will find it. True when there is nothing left to do (built, or
	/// no chrome for it); false when its art is not readable yet, for the caller to ask again.
	/// </summary>
	public bool PrebuildFrontEnd(string presentation)
	{
		if (Host is null ||
			Engine_UiTemplates.ScreenFor(presentation) is not { ChromeUnits.Count: > 0 } template)
		{
			return true;
		}

		if (_books.TryGetValue(template.ScreenId, out Engine_UiOwnedBook? held) && GodotObject.IsInstanceValid(held))
		{
			return true;
		}

		if (Ensure(template.ScreenId, presentation) is not { } book)
		{
			return false;
		}

		_books[template.ScreenId] = book;
		return true;
	}

	/// <summary>
	/// The book <see cref="PrebuildOne"/> set aside for this screen and presentation, handed
	/// over once; null when there is none (or it was built from another file, or a remount
	/// retired it), and the caller builds as before.
	/// </summary>
	public Engine_UiOwnedBook? ClaimPrebuilt(uint screenId, string presentation)
	{
		if (!_prebuilt.Remove(screenId, out (Engine_UiOwnedBook Book, string Presentation) held))
		{
			return null;
		}

		return GodotObject.IsInstanceValid(held.Book) && !held.Book.IsQueuedForDeletion() &&
			string.Equals(held.Presentation, presentation, System.StringComparison.OrdinalIgnoreCase)
				? held.Book
				: null;
	}

	/// <summary>Screens <see cref="PrebuildOne"/> has attempted, whatever the outcome.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _prebuildTried = new();

	/// <summary>Books built ahead of their first open and not yet claimed — see <see cref="PrebuildOne"/>.</summary>
	private readonly System.Collections.Generic.Dictionary<uint, (Engine_UiOwnedBook Book, string Presentation)> _prebuilt = new();

	/// <summary>
	/// Frees every book built from one of <paramref name="presentations"/> — the host retired
	/// those pattern books on a new mount (<see cref="Engine_UiPresentationHost.PatternBooksRetired"/>).
	/// Whoever holds one finds it invalid and ensures it again from the new book, as on first
	/// sight. Returns how many went.
	/// </summary>
	public int Retire(System.Collections.Generic.ISet<string> presentations)
	{
		int freed = 0;
		for (int i = _made.Count - 1; i >= 0; i--)
		{
			(Engine_UiOwnedBook book, string presentation) = _made[i];
			if (!GodotObject.IsInstanceValid(book))
			{
				_made.RemoveAt(i);
				continue;
			}

			if (!presentations.Contains(presentation))
			{
				continue;
			}

			book.QueueFree();
			_made.RemoveAt(i);
			++freed;
		}

		var gone = new System.Collections.Generic.List<uint>();
		foreach ((uint id, Engine_UiOwnedBook book) in _books)
		{
			if (!GodotObject.IsInstanceValid(book) || book.IsQueuedForDeletion())
			{
				gone.Add(id);
			}
		}

		foreach (uint id in gone)
		{
			_books.Remove(id);
			if (id == FrontEndBookId)
			{
				FrontEndBookId = 0;
			}
		}

		return freed;
	}

	/// <summary>
	/// Stands a screen's owned book over its stated presentation's stand-in node, on its file, the
	/// host and the guest's statements.
	/// </summary>
	private void Drive(uint screenId, Engine_UiDrawnPresentation? drawn, uint address = 0)
	{
		// BY ID, WITHOUT A PRESENTATION: a rest-stand screen needs nothing from the guest's tree
		// but its name, which its template states.
		string presentation = drawn?.Name ?? Engine_UiTemplates.Screen(screenId)?.Presentation ?? string.Empty;

		if (_books.TryGetValue(screenId, out Engine_UiOwnedBook? book) &&
			!GodotObject.IsInstanceValid(book))
		{
			_books.Remove(screenId);
			book = null;
		}

		if (book is null && !_bookless.Contains(screenId))
		{
			// "No chrome" is permanent; "not ready yet" is not. Only a template naming no chrome
			// is a settled answer — a presentation can reach the registry before its pattern
			// book is read.
			if (Engine_UiTemplates.Screen(screenId) is not { ChromeUnits.Count: > 0 })
			{
				_bookless.Add(screenId);
			}
			else if (presentation.Length != 0 &&
				(ClaimPrebuilt(screenId, presentation) ?? Ensure(screenId, presentation)) is { } made)
			{
				_books[screenId] = made;
				book = made;
			}
		}

		// The mirror's visibility is restated every frame, never toggled: one presentation serves
		// several screens (`GiftGivingScreen.bgf` is ids 15, 25 and 27), so a tree left hidden
		// would be inherited dark by the next screen that has no book of its own.
		// THE GUEST'S TREE IS NEVER DRAWN. Godot owns every screen; a screen with no chrome yet
		// shows nothing rather than the guest's picture.
		if (drawn is not null && GodotObject.IsInstanceValid(drawn.Node))
		{
			drawn.Node.Visible = false;
		}

		if (book is null || !GodotObject.IsInstanceValid(book))
		{
			return;
		}

		// The mirror tree is freed on every guest slide change; a book keeps its last depth
		// across that frame rather than reading a disposed node — and keeps it with no tree at all.
		if (drawn is not null && GodotObject.IsInstanceValid(drawn.Node))
		{
			book.Position = new Vector3(0f, 0f, drawn.Node.Position.Z + (Engine_UiPresentationDepth.Step / 4f));
		}

		book.Visible = true;
		if (address != 0)
		{
			book.PresentationAddress = address;
		}

		using (Refii.Core.Engine_FramePhases.Measure("ui.book.live"))
		{
			Engine_UiDetailPages.FillOnDraw(screenId, book);

			// Every presentation is headless (gui_headless_presentation.cpp): there is no guest
			// sample, so the book stands on its file, the host and the guest's statements.
			book.SetRest();
		}
	}

	/// <summary>
	/// Stands the FRONT END's own owned book. The front end never appears in the presentation
	/// registry's kernel array — it reaches Godot through its screen-object fields, read by name —
	/// so it cannot be driven by <see cref="Drive(uint, Engine_UiDrawnPresentation, uint)"/>. Its
	/// presentation is headless: the book stands on its file, its slides, plays, attributes,
	/// switches, texts and textures arriving as the guest's statements to the front end's active
	/// screen presentation (Engine_UiSlideStatements).
	///
	/// Returns true when the book is standing.
	/// </summary>
	public bool DriveFrontEnd(string presentation)
	{
		if (Host is null ||
			Engine_UiTemplates.ScreenFor(presentation) is not { ChromeUnits.Count: > 0 } template)
		{
			return false;
		}

		uint screenId = template.ScreenId;
		if (_books.TryGetValue(screenId, out Engine_UiOwnedBook? book) && !GodotObject.IsInstanceValid(book))
		{
			_books.Remove(screenId);
			book = null;
		}

		if (book is null)
		{
			// The pattern book is read from the banks on first ask, so a null here means "come
			// back next frame", not "never".
			book = Ensure(screenId, presentation);
			if (book is null)
			{
				return false;
			}

			_books[screenId] = book;
		}

		bool switching = FrontEndBookId != screenId || !book.Visible;
		book.Position = Vector3.Zero;
		book.Visible = true;
		book.PresentationAddress = Refii.Host.HostBridge.LivePresentation(presentation);
		FrontEndBookId = screenId;

		if (switching || Refii.Core.Engine_GuestFrame.Changed || Refii.Core.Engine_GuestFrame.Frame == 0u)
		{
			book.SetRest();
		}

		// Take the other front-end book down: title and main menu are two presentations and only
		// one is up. Only front-end books, tested through the same helper that defines the front
		// end — `scrollingbackground` is host-only too and stays up, being the title's panorama.
		foreach ((uint id, Engine_UiOwnedBook other) in _books)
		{
			if (id != screenId && GodotObject.IsInstanceValid(other) &&
				Engine_UiTemplates.Screen(id) is { } sibling &&
				Refii.Core.Engine_TitleGame.Active.IsFrontEndPresentation(sibling.Presentation))
			{
				other.Visible = false;
			}
		}

		return true;
	}

	/// <summary>
	/// Which owned book the FRONT END is standing, or 0 for none — the registry loop hides every
	/// book it did not stand, and the front end's never appears there.
	/// </summary>
	public uint FrontEndBookId { get; private set; }

	/// <summary>Screens whose template names no chrome — asked once, then left alone.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _bookless = new();

	/// <summary>Which books stood this pass — scratch for <see cref="Drive"/>, reused per frame.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _standing = new();

	/// <summary>
	/// Stands every owned surface the guest currently has live.
	///
	/// Driven by the presentation registry, not the current screen id: some owned surfaces are
	/// never "current" (the spell wheel is id 2 and the manager never reports it) and several can
	/// be live at once. The screen id still wins where the manager states one, since three ids
	/// share `GiftGivingScreen`.
	/// </summary>
	public void Drive(
		System.Collections.Generic.IReadOnlyDictionary<uint, Engine_UiDrawnPresentation> drawn,
		bool revealed)
	{
		if (Host is null)
		{
			return;
		}

		// Reused, not reallocated: this runs every guest frame and the set never outlives the
		// call, so a fresh one is per-frame garbage whose collection pauses stall the sim thread.
		System.Collections.Generic.HashSet<uint> standing = _standing;
		standing.Clear();
		if (revealed)
		{
			Engine_UiModel.InGameScreens? screens = Engine_UiSource.Current.GetInGameScreens();
			System.Collections.Generic.IReadOnlyDictionary<uint, int>? owned = screens?.Owned;
			uint current = screens is { } state ? (uint)state.Current : uint.MaxValue;
			uint boxScreen = Engine_UiSource.Current.DisplayBoxScreen();

			foreach ((uint address, Engine_UiDrawnPresentation live) in drawn)
			{
				// The pause book is driven by the pause menu, which also owns its banner, its
				// markers and the root-only window it stands in.
				uint screenId = owned is not null && owned.TryGetValue(address, out int stated)
					? (uint)stated
					: Engine_UiTemplates.ScreenFor(live.Name) is { } byFile
						? byFile.ScreenId
						: uint.MaxValue;

				if (screenId is uint.MaxValue or 0)
				{
					continue;
				}

				// GODOT OWNS EVERY SCREEN. The guest's tree is never drawn — restated every
				// frame because the mirror tree is rebuilt on slide changes.
				if (GodotObject.IsInstanceValid(live.Node))
				{
					live.Node.Visible = false;
				}

				// Only the screen the player is on: the guest keeps the previous screen's
				// presentation in the registry after moving on, so standing a book for everything
				// drawn puts two books on screen at once. Host-only surfaces are exempt — their
				// ids sit past the guest's range and the manager never reports them as current.
				// A display box stands on its screen without ever being current.
				if (screenId < HostOnlyScreenId && screenId != current && screenId != boxScreen)
				{
					continue;
				}

				Drive(screenId, live, address);
				standing.Add(screenId);
			}

			// THE STATED SCREEN STANDS WITHOUT ITS PRESENTATION: a rest-stand screen the manager
			// names as current, or the display box's, is stood by its id whether or not the
			// guest built (or still holds) a presentation for it.
			foreach (uint stated in new[] { current, boxScreen })
			{
				if (stated is not (uint.MaxValue or 0) && stated < HostOnlyScreenId &&
					!standing.Contains(stated) &&
					Engine_UiTemplates.Screen(stated) is { RestFromFile: true })
				{
					Drive(stated, null);
					standing.Add(stated);
				}
			}
		}

		foreach ((uint id, Engine_UiOwnedBook book) in _books)
		{
			// The front end's book is not driven from the registry — see DriveFrontEnd — so
			// this loop has no standing entry for it and would take it straight back down.
			if (id != FrontEndBookId && !standing.Contains(id) && GodotObject.IsInstanceValid(book))
			{
				book.Visible = false;
			}
		}
	}

	/// <summary>
	/// Where the guest's screen ids end and ours begin — its own table stops at 29, so anything
	/// at or above this is a surface Godot names for itself and is never reported as current.
	/// </summary>
	public const uint HostOnlyScreenId = 1000;

	/// <summary>The title screen's host-only id — <see cref="Engine_UiTemplates"/> record 1011.</summary>
	public const uint TitleScreenId = 1011;

	/// <summary>Every book <see cref="Ensure"/> built and the presentation it was built from, for <see cref="Retire"/>.</summary>
	private readonly System.Collections.Generic.List<(Engine_UiOwnedBook Book, string Presentation)> _made = new();

	/// <summary>One owned book per screen id, built on first sight of that screen.</summary>
	private readonly System.Collections.Generic.Dictionary<uint, Engine_UiOwnedBook> _books =
		new();

	/// <summary>Hands a book the guest's registries — every book, the set's own and the pause root's.</summary>
	public void Wire(Engine_UiOwnedBook book)
	{
		book.RegistryText = hash => Registry?.Invoke()?.Text(new Engine_HudValues.Handle(string.Empty, hash));
		book.RegistrySlide = (registry, hash) => Registry?.Invoke()?.RegistrySlide(registry, hash);
		book.RegistryPlayed = (registry, hash) => Registry?.Invoke()?.RegistryPlayed(registry, hash) ?? false;
		book.RegistryGeneration = () => Registry?.Invoke()?.RegistryGeneration ?? 0ul;
		book.RegistryImage = hash => Registry?.Invoke()?.RegistryImage(hash);
		book.RegistryMeter = hash => Registry?.Invoke()?.Meter(new Engine_HudValues.Handle(string.Empty, hash));
	}
}
