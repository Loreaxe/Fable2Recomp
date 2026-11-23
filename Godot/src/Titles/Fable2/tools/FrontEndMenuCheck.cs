using Godot;

using Refii.Host;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Launcher.Tools;

/// <summary>
/// Drives <see cref="Engine_FrontEndMenuView"/> from the keyboard, with no guest, to expose
/// layout and navigation feel: the slot table bows the column toward the highlight and fades
/// the two end slots out; the auto-repeat starts at 0.25s and accelerates to 0.14.
/// Subclasses <see cref="Engine_UiPresentationHost"/> for the banks, fonts and texture cache.
///
/// Keys:
/// <list type="bullet">
/// <item>Up / Down — navigate, with the guest's own repeat curve</item>
/// <item>Enter or A — activate; on "Subtitles" that expands the submenu, on "New Game" it
/// opens the gender card</item>
/// <item>Left / Right — while the gender card is up, choose the boy or the girl</item>
/// <item>Escape or B — collapse an expanded submenu, or cancel the gender card</item>
/// <item>1..4 — toggle a root entry's availability, so the list can be watched changing
/// under the highlight</item>
/// </list>
///
/// <c>REFII_MENU_SCREEN</c> names a presentation to stand up behind the menu (e.g.
/// <c>frontendmainmenu</c>), which is also where the row frame art and the boy/girl cards are
/// cloned from; unset, the view draws labels only and the card draws stand-ins.
///
/// <c>REFII_MENU_OPEN=card</c> opens the gender card straight away and
/// <c>REFII_MENU_SHOT=&lt;path&gt;</c> writes the viewport and quits. The path must be outside
/// the repository — a render of the game's own card art is derived data.
/// </summary>
public partial class FrontEndMenuCheck : Engine_UiPresentationHost
{
	private Engine_FrontEndMenu? _menu;
	private Engine_FrontEndMenuView? _view;
	private Engine_FrontEndCardChoice? _cards;
	private Engine_FrontEndSaveCards? _saveCards;
	private bool _started;

	public override void _Ready()
	{
		// The base mounts the banks and calls OnLoaded on success; on failure it just reports,
		// and the harness starts anyway since only the labels need game data.
		base._Ready();
		Start();
	}

	protected override void OnLoaded() => Start();

	private void Start()
	{
		if (_started)
		{
			return;
		}
		_started = true;

		string backdrop = System.Environment.GetEnvironmentVariable("REFII_MENU_SCREEN") ?? string.Empty;
		Node3D? template = null;
		if (backdrop.Length > 0 && ShowByName(backdrop) && Built is not null)
		{
			template = Engine_FrontEndMenuView.FindRowTemplate(Built);
			GD.Print(template is null
				? $"[menu-check] '{backdrop}' is up but carries no 'Buttons' component group — rows will be text only"
				: $"[menu-check] cloning row art from '{template.Name}' under {backdrop}");

			// The shipped rows go away, as GameUi.AdoptMenu hides this group after every
			// rebuild; otherwise the harness draws two menus.
			if (Engine_UiTheme.FindByName(Built, Engine_FrontEndMenuView.RowGroupName)
				is Node3D shipped)
			{
				shipped.Visible = false;
			}

			// Where the file puts the menu in depth: the view anchors at z 0, which decides
			// whether the rows draw over or under the gender card (its poses reach z -134).
			for (Node? n = template; n is Node3D s; n = n.GetParent())
			{
				GD.Print($"[menu-check]   menu chain {s.Name} z={s.Position.Z:F1} worldZ={s.GlobalPosition.Z:F1}");
			}
		}
		else
		{
			// No backdrop means no screen was built, and the camera comes with the screen.
			Engine_UiSceneBuilder.ActivateCamera(this);
		}

		// The document's shape: which subtrees are chrome, which are content, where the text
		// lives. Depth-limited.
		if (System.Environment.GetEnvironmentVariable("REFII_MENU_DUMP_DOC") is { } docName &&
			docName.Length > 0)
		{
			Engine_UiScreen? doc = DocumentFor(docName);
			if (doc is null)
			{
				GD.Print($"[menu-check] no document for '{docName}'");
			}
			else
			{
				// The animation table, proving the section parse against a real file.
				GD.Print($"[doc] {doc.Document.Animations.Length} animation track(s), " +
					$"section {doc.Document.AnimationSectionSize} bytes, counts " +
					string.Join(",", doc.Document.MemoryCounts));

				// The first track's target element attribute table, to identify the animation
				// id space by intersection.
				if (doc.Document.Animations.Length > 0)
				{
					BgfAnimation first = doc.Document.Animations[0];
					if (first.ElementOrdinal >= 0 && first.ElementOrdinal < doc.Nodes.Length)
					{
						var ids = new System.Collections.Generic.List<string>();
						foreach (BgfAttribute attr in doc.Nodes[first.ElementOrdinal]
							.Element.Attributes)
						{
							ids.Add($"0x{attr.Id:X}:{attr.Type}" +
								(attr.Id == first.AttributeId ? "<==MATCH" : ""));
						}

						GD.Print($"[doc]   target element attrs: {string.Join(" ", ids)}");
					}
				}
				int shown = 0;
				foreach (BgfAnimation animation in doc.Document.Animations)
				{
					if (++shown > 8)
					{
						break;
					}

					string element = animation.ElementOrdinal >= 0 &&
						animation.ElementOrdinal < doc.Nodes.Length
							? doc.Nodes[animation.ElementOrdinal].Name
							: $"#{animation.ElementOrdinal}";
					GD.Print($"[doc]   anim on '{element}' attr=0x{animation.AttributeId:X} " +
						$"keys={animation.Keys.Length} active={animation.ActiveOnLoad} " +
						$"dyn={animation.Dynamic}" +
						(animation.Keys.Length == 0
							? string.Empty
							: $" t[{animation.Keys[0].Time:0}..{animation.Keys[^1].Time:0}] " +
							  $"v[{animation.Keys[0].Value:0.##}..{animation.Keys[^1].Value:0.##}]"));
				}
				void Walk(UiNode node, string indent, int depth)
				{
					string extra = node.Quad is not null
						? $" QUAD tex='{node.Quad.TexturePath}'"
						: node.Text is not null ? $" TEXT '{node.Text.Tag}'" : string.Empty;
					GD.Print($"[doc] {indent}{node.Name} [{node.Type}] ord={node.Ordinal} " +
						$"pos=({node.PositionX:0.#},{node.PositionY:0.#},{node.PositionZ:0.#}) " +
						$"scale=({node.ScaleX:0.##},{node.ScaleY:0.##}){extra}");
					if (depth <= 0)
					{
						if (node.Children.Count > 0)
						{
							GD.Print($"[doc] {indent}  ... {node.Children.Count} child(ren)");
						}
						return;
					}

					foreach (UiNode child in node.Children)
					{
						Walk(child, indent + "  ", depth - 1);
					}
				}

				foreach (UiNode root in doc.Nodes)
				{
					if (root.Parent is null)
					{
						Walk(root, string.Empty, 14);
					}
				}
			}
		}

		// The row's own depths, for the label-behind-the-face defect: everything in a row is
		// alpha-blended with no depth write, so cover order is decided purely by Z, and the
		// view parks its label at z 0.
		if (template is not null &&
			System.Environment.GetEnvironmentVariable("REFII_MENU_DUMP_ROWS") == "1")
		{
			void Depths(Node node, string indent)
			{
				if (node is Node3D spatial)
				{
					string type = node.HasMeta("element_type")
						? (string)node.GetMeta("element_type")
						: node.GetType().Name;
					GD.Print($"[menu-check] row-depth {indent}{node.Name} [{type}] " +
						$"z={spatial.Position.Z:F2} pos=({spatial.Position.X:F1},{spatial.Position.Y:F1})");
				}

				foreach (Node child in node.GetChildren())
				{
					Depths(child, indent + "  ");
				}
			}

			Depths(template, string.Empty);
		}

		// REFII_MENU_PAUSE=1 constructs the view the way GameUi.AdoptPauseMenu does — the
		// pattern book's theme and template, the BuildHero model, the authored anchor and
		// cursor pose, the half-slab z lift — instead of the harness's own way (host theme,
		// template out of the built tree, z 0). The two paths should be equivalent.
		if (System.Environment.GetEnvironmentVariable("REFII_MENU_PAUSE") == "1" &&
			backdrop.Length > 0)
		{
			Engine_UiTheme? book = PatternBookFor(backdrop);
			Node3D? pattern = book?.ArtSourceNode(Engine_FrontEndMenuView.RowGroupName);
			Node3D? bookTemplate = pattern is null
				? null
				: Engine_FrontEndMenuView.RowTemplateIn(pattern);

			var anchor = Vector2.Zero;
			if (pattern is not null)
			{
				Vector3 origin = Engine_UiTheme.CanvasTransform(pattern).Origin;
				anchor = new Vector2(origin.X, origin.Y);
			}

			Transform3D? cursorPose = null;
			if (book?.ArtSourceNode("MenuHighlight") is { } highlightSource)
			{
				Transform3D authored = Engine_UiTheme.CanvasTransform(highlightSource);
				authored.Origin -= new Vector3(anchor.X, anchor.Y, 0f);
				cursorPose = authored;
			}

			_menu = Engine_PauseMenu.BuildHero();
			_view = new Engine_FrontEndMenuView
			{
				Name = "PauseMenu",
				ScreenId = 0,
				Theme = book,
				RowTemplate = bookTemplate,
				CursorTemplate = book?.Art("MenuHighlight"),
				CursorPose = cursorPose,
			};
			AddChild(_view);
			_view.Bind(_menu);
			// The book's own slab: rows z 0, cover +10, badge +11, Back bar +15 as authored.
			_view.Position = new Vector3(anchor.X, anchor.Y, 400f);

			// The pool's mask binding, for the owned view's clip.
			if (DocumentFor(backdrop) is { } maskDoc &&
				Engine_ExpandableMenuItems.FindGroup(maskDoc) is { } maskPool)
			{
				int mask = maskPool.Ordinal < maskDoc.MaskByOrdinal.Length
					? maskDoc.MaskByOrdinal[maskPool.Ordinal]
					: -1;
				if (mask >= 0 && mask < maskDoc.Nodes.Length)
				{
					UiNode m = maskDoc.Nodes[mask];
					float mx = 0f, my = 0f;
					for (UiNode? at = m; at is not null; at = at.Parent)
					{
						mx += at.PositionX;
						my += at.PositionY;
					}

					GD.Print($"[menu-check] pool mask: '{m.Name}' ord={mask} " +
						$"centre=({mx:0},{my:0}) size=({100f * m.ScaleX:0}x{100f * m.ScaleY:0})");
				}
				else
				{
					GD.Print("[menu-check] pool mask: NONE");
				}
			}

			GD.Print($"[menu-check] PAUSE MODE: book={(book is null ? "NULL" : "built")} " +
				$"template={(bookTemplate is null ? "NULL" : "cloned")} anchor=({anchor.X},{anchor.Y}) " +
				$"cursor={(cursorPose is null ? "fallback" : "authored")} " +
				$"fonts={(book?.Fonts is null ? "MISSING" : "bound")} " +
				$"strings={(book?.Strings is null ? "MISSING" : "bound")}");

			// REFII_MENU_BOOK=1: the owned pause book instead of the mirrored backdrop. No live
			// values here, so text elements keep their authored placeholders.
			if (System.Environment.GetEnvironmentVariable("REFII_MENU_BOOK") == "1")
			{
				if (Built is not null)
				{
					Built.Visible = false;
				}

				var pauseBook = new Engine_UiOwnedBook
				{
					Name = "PauseBook",
					Book = book,
					Document = DocumentFor(backdrop),
					Position = new Vector3(0f, 0f, 400f),
				};
				pauseBook.Build();
				AddChild(pauseBook);
			}
		}
		else
		{
			_menu = BuildMenu();
			_view = new Engine_FrontEndMenuView
			{
				Name = "FrontEndMenuView",
				Theme = Theme,
				RowTemplate = template,
			};
			AddChild(_view);
			_view.Bind(_menu);
		}

		BuildCardChoice();
		BuildSaveCards();

		GD.Print(Theme?.Describe() ?? "[menu-check] no theme — the host never loaded game data");
		GD.Print(_view.Describe());

		// REFII_MENU_OPEN=card puts the gender card up immediately. `card-boy`/`card-girl` move
		// the choice through the view's own navigation rather than a setter, so the capture
		// also exercises left and right.
		string open = System.Environment.GetEnvironmentVariable("REFII_MENU_OPEN") ?? string.Empty;
		if (open.StartsWith("card", System.StringComparison.Ordinal))
		{
			OpenCards();

			if (open == "card-boy")
			{
				_cards?.Navigate(-1);
			}
			else if (open == "card-girl")
			{
				_cards?.Navigate(1);
			}

			if (_cards is not null && open != "card")
			{
				GD.Print($"[menu-check] {_cards.Describe()}");
			}
		}
		else if (open == "achievements")
		{
			// The achievements page over a file-backed seam: `REFII_ACHIEVEMENTS_XDBF` is a title
			// XDBF (or a synthetic one), `REFII_ACHIEVEMENTS_STORE` the unlock file to read, and
			// `REFII_ACHIEVEMENTS_NAV=<n>` steps the cursor down n times before the capture.
			Refii.Ui.Engine_UiSource.Current = new FileAchievementSource(
				System.Environment.GetEnvironmentVariable("REFII_ACHIEVEMENTS_XDBF") ?? string.Empty);
			Refii.Achievements.Engine_Achievements.StorePathOverride =
				System.Environment.GetEnvironmentVariable("REFII_ACHIEVEMENTS_STORE");
			Refii.Achievements.Engine_Achievements.Drive();
			GD.Print($"[menu-check] achievements: {Refii.Achievements.Engine_Achievements.Report}");
			foreach (Refii.Achievements.AchievementDefinition definition in
				Refii.Achievements.Engine_Achievements.Definitions)
			{
				GD.Print($"[menu-check]   #{definition.Id} '{definition.Name}' {definition.Gamerscore}G " +
					$"image={definition.ImageId} unlocked={Refii.Achievements.Engine_Achievements.IsUnlocked(definition.Id)} " +
					$"icon={(Refii.Achievements.Engine_Achievements.Icon(definition.Id) is null ? "none" : "png")}");
			}

			GD.Print($"[menu-check] achievements: {Refii.Achievements.Engine_Achievements.UnlockedCount} of " +
				$"{Refii.Achievements.Engine_Achievements.TotalCount} unlocked");

			// With REFII_MENU_BOOK=1 over the logbook: the owned book takes the achievement page
			// for one row — `REFII_ACHIEVEMENTS_ROW=<id>`, or the folder itself when unset —
			// through the same three host channels the game feeds it.
			if (GetNodeOrNull<Engine_UiOwnedBook>("PauseBook") is { } logbook &&
				DocumentFor(backdrop) is { } logbookDoc)
			{
				string rowSpec = System.Environment.GetEnvironmentVariable("REFII_ACHIEVEMENTS_ROW") ?? string.Empty;
				Engine_UiExpandableMenu.Item row;
				if (uint.TryParse(rowSpec, out uint id))
				{
					row = new Engine_UiExpandableMenu.Item($"Achievement:{id}", $"Achievement {id}")
					{
						EventType = Engine_UiAchievementPage.EventType,
						IdLow = (int)(id & 0xFFFFu),
						IdHigh = (int)((id >> 16) & 0xFFFFu),
					};
				}
				else
				{
					row = new Engine_UiExpandableMenu.Item(Engine_UiAchievementPage.FolderRowName, "Achievements")
					{
						EventType = Engine_UiAchievementPage.EventType,
					};
				}

				logbook.HostValues = Engine_UiAchievementPage.Values(row);
				logbook.HostTextures = Engine_UiAchievementPage.Textures(row);
				logbook.HostActive = Engine_UiAchievementPage.Active(row);
				var allActive = new bool[logbookDoc.Nodes.Length];
				System.Array.Fill(allActive, true);
				logbook.SetLive(null, null, allActive, null, null);
				GD.Print($"[menu-check] achievement page for '{row.Name}': " +
					$"{logbook.HostValues.Count} value(s), {logbook.HostTextures.Count} texture(s), " +
					$"{logbook.HostActive.Count} switch(es)");
			}
		}
		else if (open == "saves")
		{
			// The fan, straight away. `REFII_MENU_SAVES=<n>` decides how many cards are on it.
			OpenSaveCards();

			// Stepped through the view's own navigation rather than set, so the capture also
			// exercises left and right.
			if (int.TryParse(System.Environment.GetEnvironmentVariable("REFII_MENU_SAVE_NAV"),
					out int right))
			{
				for (int i = 0; i < right; ++i)
				{
					// A whole second apart, so each call is one move, not a swallowed repeat.
					_saveCards?.Navigate(1, 1.0);
					_saveCards?.Navigate(0, 1.0);
				}

				GD.Print($"[menu-check] stepped right {right}x — {_saveCards?.Describe()}");
			}
		}

		// REFII_MENU_NAV=<n> steps the highlight down n times before the capture, so a still can
		// catch the column mid-slide (the capture waits two frames, ~33ms into a 150ms tween).
		// REFII_MENU_BACKDROP=0 keeps the presentation mounted as the art source and stops it
		// being the picture: with no guest nothing hides the pages the screen is not on.
		if (System.Environment.GetEnvironmentVariable("REFII_MENU_BACKDROP") == "0" &&
			Built is not null)
		{
			Built.Visible = false;
		}

		if (int.TryParse(System.Environment.GetEnvironmentVariable("REFII_MENU_NAV"), out int steps))
		{
			for (int i = 0; i < steps; ++i)
			{
				// A whole second per step, so each call is one move, not a swallowed repeat.
				_view.Navigate(1, 1.0);
			}

			GD.Print($"[menu-check] stepped down {steps}x — {_view.Describe()}");
		}

		// REFII_MENU_ACTIVATE=1 presses A after the steps, so a capture can catch an expanded
		// submenu — the only place the red tab face and the child indent are visible.
		if (System.Environment.GetEnvironmentVariable("REFII_MENU_ACTIVATE") == "1")
		{
			string? dispatched = _view.Activate();
			GD.Print($"[menu-check] activated — {(dispatched ?? "handled here")}\n{_view.Describe()}");
		}

		CaptureIfRequested();
	}

	/// <summary>
	/// Stands the gender card up beside the menu, sourced as the game sources it: clones out of
	/// the built `choosecard` group, so a stand-in means the presentation carries no art.
	/// </summary>
	private void BuildCardChoice()
	{
		Node3D? group = Built is null ? null : Engine_FrontEndCardChoice.FindGroup(Built);

		// The same factory the game uses; a harness that built the panel its own way would be
		// evidence about the harness.
		_cards = Engine_FrontEndCardChoice.FromPresentation(group);
		AddChild(_cards);
		GD.Print($"[menu-check] {_cards.Describe()}" +
			$"{(group is null ? " (no 'choosecard' group in the backdrop)" : string.Empty)}");

		// Each face and its depth: a card is two quads at one place (a portrait and a
		// `cardback`), and which one the player sees is decided by z.
		DumpFaces(_cards.BoyTemplate, "boy");
		DumpFaces(_cards.GirlTemplate, "girl");
	}

	private static void DumpFaces(Node? card, string which)
	{
		if (card is null)
		{
			return;
		}

		void Walk(Node node)
		{
			if (node is Node3D spatial)
			{
				// Local z says how the artist stacked the two faces; global z says which one the
				// camera sees. They disagree once anything up the chain turns the card over.
				// Opacity comes through the builder, not a StandardMaterial3D cast: a card face
				// is a shader-driven quad.
				string material = spatial is MeshInstance3D m
					? $"alpha={Engine_UiSceneBuilder.QuadOpacity(m):F2} " +
					  $"prio={m.MaterialOverride?.RenderPriority ?? 0} vis={m.Visible}"
					: string.Empty;

				GD.Print($"[menu-check]   {which}/{spatial.Name} z={spatial.Position.Z:F3} " +
					$"worldZ={spatial.GlobalPosition.Z:F3} " +
					$"rotY={Mathf.RadToDeg(spatial.Rotation.Y):F1}deg {material} " +
					$"{(spatial is MeshInstance3D mesh && mesh.HasMeta("texture_path") ? (string)mesh.GetMeta("texture_path") : string.Empty)}");
			}

			foreach (Node child in node.GetChildren())
			{
				Walk(child);
			}
		}

		Walk(card);
	}

	/// <summary>
	/// Stands the Load Game fan up beside the menu, through the same factory the game uses.
	/// </summary>
	private void BuildSaveCards()
	{
		Node3D? page = Built is null ? null : Engine_FrontEndSaveCards.FindPage(Built);

		_saveCards = Engine_FrontEndSaveCards.FromPresentation(page);
		_saveCards.Theme = Theme;
		AddChild(_saveCards);

		GD.Print($"[menu-check] {_saveCards.Describe()}" +
			$"{(page is null ? " (no 'loadgame' page in the backdrop)" : string.Empty)}");
	}

	/// <summary>
	/// Puts the fan up with <c>REFII_MENU_SAVES</c> stand-in saves, three by default. The saves
	/// are placeholders: the real count comes from a live `XamContentCreateEnumerator` on the
	/// guest's side. In the game "Load Game" dispatches to the guest, which enumerates and
	/// enters substate 5 before the fan goes up; here the keypress stands in for that.
	/// </summary>
	private void OpenSaveCards()
	{
		if (_saveCards is null)
		{
			return;
		}

		int count = int.TryParse(System.Environment.GetEnvironmentVariable("REFII_MENU_SAVES"),
			out int wanted) ? wanted : 3;

		var saves = new System.Collections.Generic.List<Engine_FrontEndSaveCards.Save>();
		for (int i = 0; i < count && i < Engine_FrontEndSaveCards.MaxCards; ++i)
		{
			saves.Add(new Engine_FrontEndSaveCards.Save($"Save {i + 1} — stand-in, no guest"));
		}

		// No guest states a first card here: the stand-ins start at card 1.
		_saveCards.SetSaves(saves, 1);
		_saveCards.Reset();
		_saveCards.Visible = true;

		if (_view is not null)
		{
			_view.Visible = false;
		}

		// The backdrop goes away while the fan is up: with no guest nothing hides the pages the
		// presentation is not on, and the cards (z -10 through -100) end up behind them. The
		// presentation stays mounted as the art source.
		if (Built is not null)
		{
			Built.Visible = false;
		}

		GD.Print($"[menu-check] opened the load fan — {_saveCards.Describe()}");
	}

	/// <summary>Takes the fan down and gives the rows back, as the game does.</summary>
	private void CloseSaveCards()
	{
		if (_saveCards is not null)
		{
			_saveCards.Visible = false;
		}

		if (_view is not null)
		{
			_view.Visible = true;
		}

		if (Built is not null)
		{
			Built.Visible = true;
		}
	}

	private void OpenCards()
	{
		if (_cards is null)
		{
			return;
		}

		_cards.Reset();
		_cards.Visible = true;

		// The rows down while the card is up, as the game does it.
		if (_view is not null)
		{
			_view.Visible = false;
		}

		// The backdrop goes away while the card is up: with no guest nothing hides the pages the
		// presentation is not on (the guest's globalactive does that in game), and the gender
		// card sits at z -4 and -134, behind them. It stays mounted as the art source.
		if (Built is not null)
		{
			Built.Visible = false;
		}

		GD.Print($"[menu-check] opened the gender card — {_cards.Describe()}");
	}

	/// <summary>Takes the card down and gives the rows back, as the game does.</summary>
	private void CloseCards()
	{
		if (_cards is not null)
		{
			_cards.Visible = false;
		}

		if (_view is not null)
		{
			_view.Visible = true;
		}

		if (Built is not null)
		{
			Built.Visible = true;
		}
	}

	/// <summary>
	/// Writes the viewport to <c>REFII_MENU_SHOT</c> and quits. The slot bow, card poses and
	/// tint cannot be asserted on from a log.
	/// </summary>
	private async void CaptureIfRequested()
	{
		string? target = System.Environment.GetEnvironmentVariable("REFII_MENU_SHOT");
		if (string.IsNullOrEmpty(target))
		{
			return;
		}

		// Two frames: one to flush the scene change, one to draw it.
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

		Image image = GetViewport().GetTexture().GetImage();
		Error err = image.SavePng(target!);
		GD.Print(err == Error.Ok ? $"[menu-check] wrote {target}" : $"[menu-check] screenshot failed: {err}");
		GetTree().Quit();
	}

	/// <summary>Stands in for the guest's sign-in state. Toggled with 2.</summary>
	private bool _signedIn;

	/// <summary>Stands in for storage; the model takes the save count from the host.</summary>
	private int _saveCount;

	/// <summary>Installed languages. "Language" spawns only above one — toggled with 4.</summary>
	private uint _languageCount = 1;

	/// <summary>
	/// The root menu, with availability applied through the model's own API. Starts signed out
	/// with no saves and one language, so "Load Game" and "Language" are absent — which
	/// exercises the model keeping the highlight on the entry rather than on the index.
	/// </summary>
	private Engine_FrontEndMenu BuildMenu()
	{
		Engine_FrontEndMenu menu = Fable2FrontEndRoot.BuildRoot();
		ApplyAvailability(menu);
		menu.Add(BuildCustomScreen());
		return menu;
	}

	/// <summary>
	/// A screen the original never had, exercising the custom-screen path:
	/// <list type="bullet">
	/// <item><c>HostOnly</c> makes <see cref="Engine_FrontEndMenu.Activate"/> return null, so
	/// the entry never reaches the guest's dispatcher.</item>
	/// <item>A submenu replaces the list rather than expanding under it, and B returns where
	/// the player was; "Subtitles" two rows up expands in place instead.</item>
	/// <item>A label not in <c>book.babel</c> falls through the lookup verbatim, and still gets
	/// the theme's fonts, colour and slot layout.</item>
	/// </list>
	/// </summary>
	private static Engine_UiExpandableMenu.Item BuildCustomScreen()
	{
		// Children, not a second menu: the shipped model nests through children and `IsOpen`
		// at any depth.
		var extras = new Engine_UiExpandableMenu.Item("Extras", "Extras") { HostOnly = true };
		foreach (string name in new[] { "Renderer", "Mods", "Multiplayer" })
		{
			var child = new Engine_UiExpandableMenu.Item(name, name) { Parent = extras };
			extras.Children.Add(child);
		}

		return extras;
	}

	/// <summary>
	/// Feeds the model synthetic spawn conditions rather than poking <c>Item.Available</c>:
	/// <c>ApplyAvailability</c> is the only entry that both rules on the guest's spawn conditions
	/// and rebuilds the visible list.
	/// </summary>
	private void ApplyAvailability(Engine_FrontEndMenu menu) =>
		Fable2FrontEndRoot.ApplyAvailability(menu, (int)_languageCount,
			// Limit 6, which is what `sub_82378618` stores over `dword_83319620` before the menu
			// can dispatch — not the 150 that sits in the image.
			new Engine_UiModel.SaveState(_signedIn, _saveCount, Fable2FrontEndRoot.DefaultSaveLimit));

	public override void _Process(double delta)
	{
		if (_menu is null || _view is null)
		{
			return;
		}

		// The card owns the input while it is up, as it does in the game.
		if (_cards is { Visible: true })
		{
			int sideways = 0;
			if (Input.IsKeyPressed(Key.Left))
			{
				sideways = -1;
			}
			else if (Input.IsKeyPressed(Key.Right))
			{
				sideways = 1;
			}

			if (_cards.Navigate(sideways))
			{
				GD.Print(_cards.Describe());
			}

			return;
		}

		// The fan owns the input while it is up. It takes a delta as well as a direction,
		// unlike the card: its left and right are relative and auto-repeat on the guest's
		// curve, where the card's two sides are absolute and edge-triggered.
		if (_saveCards is { Visible: true })
		{
			int sideways = 0;
			if (Input.IsKeyPressed(Key.Left))
			{
				sideways = -1;
			}
			else if (Input.IsKeyPressed(Key.Right))
			{
				sideways = 1;
			}

			if (_saveCards.Navigate(sideways, delta))
			{
				GD.Print(_saveCards.Describe());
			}

			return;
		}

		// Sampled every frame and handed to the model, which owns the repeat curve; discrete
		// key events would put the timing in the harness instead.
		int direction = 0;
		if (Input.IsKeyPressed(Key.Down))
		{
			direction = 1;
		}
		else if (Input.IsKeyPressed(Key.Up))
		{
			direction = -1;
		}

		if (_view.Navigate(direction, delta))
		{
			GD.Print(_view.Describe());
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (_menu is null || _view is null || @event is not InputEventKey { Pressed: true, Echo: false } key)
		{
			return;
		}

		// The card first, for the same reason _Process routes to it first.
		if (_cards is { Visible: true })
		{
			switch (key.Keycode)
			{
				case Key.Enter or Key.KpEnter or Key.A:
					if (_cards.Activate() is { } chosen)
					{
						CloseCards();
						GD.Print($"[menu-check] would dispatch '{chosen}' to the guest");
					}
					else
					{
						GD.Print("[menu-check] A with neither card up does nothing — the guest's " +
							"own A arm has no case for substate 2");
					}
					break;

				case Key.Escape or Key.B:
					CloseCards();
					GD.Print("[menu-check] cancelled the gender card — back to the menu");
					break;
			}

			return;
		}

		if (_saveCards is { Visible: true })
		{
			switch (key.Keycode)
			{
				case Key.Enter or Key.KpEnter or Key.A:
					int chosen = _saveCards.Activate();
					if (chosen < 0)
					{
						GD.Print("[menu-check] A on an empty fan does nothing — there is no save " +
							"to commit, and the guest would never have opened the carousel");
						break;
					}

					CloseSaveCards();
					GD.Print($"[menu-check] would post 'LoadSave:{chosen}' to the guest");
					break;

				case Key.Escape or Key.B:
					CloseSaveCards();
					GD.Print("[menu-check] cancelled the load fan — back to the menu");
					break;
			}

			return;
		}

		switch (key.Keycode)
		{
			case Key.Enter or Key.KpEnter or Key.A:
			{
				string? dispatched = _view.Activate();

				if (_view.TakeOpenedScreen() is { } screen)
				{
					GD.Print($"[menu-check] the entry opens screen '{screen}'");
					if (screen == Fable2FrontEndRoot.HeroCardScreen)
					{
						OpenCards();
					}

					break;
				}

				GD.Print(dispatched is null
					? $"[menu-check] handled here (expand, submenu or host-only) depth={_view.Depth}\n{_view.Describe()}"
					: $"[menu-check] would dispatch '{dispatched}' to the guest");
				break;
			}

			case Key.Escape or Key.B:
				GD.Print(_view.Back()
					? $"[menu-check] backed out to depth {_view.Depth}"
					: "[menu-check] nothing to back out of — the front end would leave the screen here");
				GD.Print(_view.Describe());
				break;

			case >= Key.Key1 and <= Key.Key4:
				Toggle((int)(key.Keycode - Key.Key1));
				break;
		}
	}

	/// <summary>
	/// Flips one root entry's availability, in the order <c>BuildRoot</c> spawns them. Two go
	/// through the guest's condition rather than the entry: "Load Game" needs a sign-in and a
	/// save, "Language" needs a second language.
	/// </summary>
	private void Toggle(int index)
	{
		if (_menu is null || _view is null)
		{
			return;
		}

		string name;
		switch (index)
		{
			case 0:
			case 2:
				name = index == 0 ? "New Game" : "Downloadable Content";
				if (_menu.Find(name) is not { } entry)
				{
					return;
				}
				entry.Available = !entry.Available;
				break;

			case 1:
				name = "Load Game";
				_signedIn = !_signedIn;
				_saveCount = _signedIn ? 1 : 0;
				break;

			case 3:
				name = "Language";
				_languageCount = _languageCount > 1 ? 1u : 2u;
				break;

			default:
				return;
		}

		ApplyAvailability(_menu);
		_view.Refresh();
		GD.Print($"[menu-check] toggled '{name}'\n{_view.Describe()}");
	}
}

/// <summary>The achievement seam read from files, for the harness: an XDBF and no guest.</summary>
public sealed class FileAchievementSource : Engine_UiSource
{
	private readonly byte[]? _xdbf;

	public FileAchievementSource(string xdbfPath)
	{
		_xdbf = xdbfPath.Length != 0 && System.IO.File.Exists(xdbfPath)
			? System.IO.File.ReadAllBytes(xdbfPath)
			: null;
	}

	public override byte[]? GetTitleXdbf() => _xdbf;
}
