namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Ui;

/// <summary>
/// One Fable 3 presentation on the SHARED HUD view: its `.bgf` read in place by the presentation
/// host, laid out by <see cref="Engine_HudLayout"/> with the widgets the title cuts whole, drawn by
/// <see cref="Engine_HudView"/> with its authored art, and driven by the element statements made
/// under its document (<see cref="Fable3Elements"/>, joined by
/// <see cref="Fable3HudStatements"/>), the registry values the bridge publishes (hud_feed.cpp,
/// through <see cref="Engine_HudValues"/>) and the registry writers' slides, places, d-pad and
/// panel (<see cref="Fable3HudRegistry"/>). An element filed under a name those writers state is
/// taken from them, never from the element write they make. <see cref="Fable3Hud"/> stands one
/// per presentation.
/// </summary>
/// <remarks>
/// WHAT FABLE 2 SAMPLES, FABLE 3 STATES. Fable 2's feed reads every element's live slide, active
/// bit, opacity, text, texture and transform off the guest's presentation each frame; Fable 3's
/// element hooks post each write as it happens (gui_element_hooks.cpp). Folded per ordinal they are
/// the same channels, plus the timeline position Fable 2 never had (`sub_8281A5D8`, a meter's
/// `fraction × 1000`), which scrubs the group's meters through <see cref="Engine_HudView.LiveTimes"/>.
/// The position is the element's local `position.x/.y/.z` (sub_82818580), the column the view's
/// stated transform takes; the texture is the path the renderable hashes (sub_82818EF0).
/// </remarks>
public sealed partial class Fable3HudDocument
{
	/// <summary>
	/// FABLE 3'S TOPBOX, from the 360 fit (the same scheme as Fable 2's, Engine_UiHudTopBox): both
	/// lines take the plain wrap (publisher `sub_8217D880`, `li r25,1` 0x8217d8c0, `stb` 0x8217db7c /
	/// 0x8217dbec) at the `BoxName 'TopBox'` entry's max.x, measured once with the meters at 100
	/// (`sub_8227CC70`, max stored 0x8227cd70/74): 100 × hud.bgf `Bar` scale.x 1.105 (TopBoxX track
	/// end) × `Bar` 7.8 — no widescreen factor on this chain. Height 100 × scale.y 0.38 → 1.1, and the
	/// fit adds 16 before stating TopBoxY (0x829205b8).
	/// </summary>
	public const float TopBoxWrapWidth = 100f * 1.105f * 7.8f;

	public static readonly Vector2 TopBoxHeightRange = new(100f * 0.38f, 100f * 1.1f);

	public const float TopBoxHeightPad = 16f;

	/// <param name="document">
	/// The presentation's name in the banks (`hud`, `interactionmode`) — also the document its
	/// statements are keyed under (<see cref="Fable3Elements.DocumentOf"/>), so the view reads them
	/// whether or not a presentation for it was ever built.
	/// </param>
	/// <param name="groups">The widgets cut whole.</param>
	/// <param name="layouts">Whether this document's arrangement is the title's HUD layout file (snapshot written, player file applied).</param>
	/// <param name="groupsOnly">
	/// Whether only the widgets cut whole are drawn: no piece for the document's own bindings
	/// (a box screen's frame art, <see cref="Fable3ScreenFrames"/>, whose rows Godot's list draws).
	/// </param>
	public Fable3HudDocument(
		string document, IReadOnlyList<Engine_HudLayout.GroupSubtree> groups, bool layouts, bool groupsOnly = false)
	{
		Document = document;
		Groups = groups;
		_layouts = layouts;
		_groupsOnly = groupsOnly;
	}

	/// <remarks>
	/// EVERY DOCUMENT IS DRAWN FROM ITS CALLS ALONE: each element's slide, text, texture and opacity
	/// as its writer was called with them (`@Ui.State`, `@Ui.Text` / `TextTag` / `TextName`,
	/// `@Ui.Texture`, `@Ui.Visible`, the surface statements' own values — the interaction button's
	/// 100 / 50), never the setters' after-the-fact reports (`@Element.*`: the slide funnel's
	/// writes, the stored opacity, the timeline) nor anything read back off a headless element.
	/// Meters stand at the registry values their writers state; the registry slides and the
	/// interaction panel are <see cref="Fable3HudRegistry"/>'s. A writer the static census missed shows
	/// in a run as `[fable3-unstated] doc path channel via` (gui_element_hooks.cpp).
	/// </remarks>
	public string Document { get; }

	public IReadOnlyList<Engine_HudLayout.GroupSubtree> Groups { get; }

	/// <summary>The view, once the document is read.</summary>
	public Engine_HudView? View { get; private set; }

	/// <summary>
	/// PLAYER TWO'S HALF of the HUD (`hud.bgf` only) while each couch player has their own view
	/// (<see cref="Refii.Core.Engine_Views.Split"/>: split, and hybrid while split): the same pieces, art,
	/// layout and element feed as <see cref="View"/>, seated <see cref="Engine_UiSeats.Seat.PlayerTwo"/>, so
	/// the shared view draws player one's widgets keyed to player two's copies (<see cref="Fable3HudLayout"/>)
	/// and the TopBox wraps inside the half (Engine_UiTopBoxLayout.WrapWidthIn). Built the first time the
	/// screen splits; suppressed while it is whole. <see cref="View"/> takes player one's half meanwhile.
	/// </summary>
	public Engine_HudView? ViewTwo { get; private set; }

	private Node? _parent;
	private Fable3Presentations? _host;

	private readonly bool _layouts;
	private readonly bool _groupsOnly;
	private Engine_UiScreen? _screen;
	private Engine_UiBindings? _bindings;
	private Fable3HudStatements? _statements;
	private Engine_UiElementResolve? _resolve;
	private Engine_UiWidescreen? _wide;

	/// <summary>Texture hashes the view asked for and could not load, each named once.</summary>
	private readonly HashSet<uint> _missed = new();

	private int[] _slides = System.Array.Empty<int>();
	private float?[] _opacity = System.Array.Empty<float?>();

	/// <summary>Per ordinal: what its stated opacity is made of, for the told entries to retire (Fable3HudStatements.OpacitySource).</summary>
	private Fable3HudStatements.OpacitySource?[] _opacitySources = System.Array.Empty<Fable3HudStatements.OpacitySource?>();
	private string?[] _texts = System.Array.Empty<string?>();
	private float?[] _times = System.Array.Empty<float?>();
	private float?[]?[] _transforms = System.Array.Empty<float?[]?>();
	private uint[] _textures = System.Array.Empty<uint>();
	private (float R, float G, float B, float A)?[] _colours = System.Array.Empty<(float, float, float, float)?>();
	private bool[] _active = System.Array.Empty<bool>();

	/// <summary>
	/// The document's slides PLAYED by Godot (the shared <see cref="Engine_UiSlidePlayback"/>,
	/// as Fable 2's HUD stand plays its file): each stated slide entered, its tracks run over real
	/// time, the meters' components at their registry time. Null before <see cref="Stand"/>.
	/// </summary>
	private Engine_UiSlidePlayback? _playback;

	/// <summary>Per ordinal, the stamp of the slide write that landed there (an entry; 0 none).</summary>
	private ulong[] _slideStamps = System.Array.Empty<ulong>();

	/// <summary>This feed's meter components and their registry time (`pct × 10`), for the playback's GoToTime.</summary>
	private readonly List<(int Ordinal, int Time)> _meterTimes = new();

	private bool _played;

	private static readonly System.Diagnostics.Stopwatch PlayClock = System.Diagnostics.Stopwatch.StartNew();

	/// <summary>Per ordinal: the meter name bound there, for the time an unstated meter stands at.</summary>
	private string?[] _meters = System.Array.Empty<string?>();

	/// <summary>Per ordinal: the text name bound there, for a text the registry states and no element write carried.</summary>
	private string?[] _textNames = System.Array.Empty<string?>();

	/// <summary>The ordinals filed under each `ScriptPositioned` name (FNV-1 of the value), for <see cref="Fable3HudRegistry.Placed"/>.</summary>
	private readonly Dictionary<uint, List<int>> _placed = new();

	/// <summary>Per registry kind, every ordinal the document binds in it (a reset's reach).</summary>
	private readonly Dictionary<Engine_UiBindings.Kind, IReadOnlyList<int>> _everyOfKind = new();

	private readonly List<Fable3HudStatements.Statement> _gathered = new();
	private ulong _fed = ulong.MaxValue;
	private readonly Fable3MiniGameBarClones _barClones = new();
	private int _reportedUnmapped = -1;

	/// <summary>Builds the view under <paramref name="parent"/> from the host's banks; false when the document is not there.</summary>
	public bool Stand(Fable3Presentations host, Node parent)
	{
		_screen = host.DocumentFor(Document);
		if (_screen is null)
		{
			GD.PushWarning($"[fable3-hud] '{Document}' is not in the banks.");
			return false;
		}

		_bindings = Engine_UiBindings.Read(_screen.Document);
		StateBoxExtents();
		_wide = Engine_UiWidescreen.Read(_screen.Document);
		var layout = Engine_HudLayout.FromBindings(_screen, _bindings, _wide, Groups);
		if (_groupsOnly)
		{
			layout.KeepOnly(piece => piece.Id.StartsWith("group.", System.StringComparison.Ordinal));
		}

		View = new Engine_HudView
		{
			Name = "Hud_" + Document,
			Theme = host.Theme,
			Screen = _screen,
			Art = new Engine_HudArt { Shipped = host.PatternBookFor(Document) },
			ResolveTexture = hash => ResolveTexture(host, hash),
			Layout = layout,
			StatesTimes = true,
			TopBoxWrapWidth = TopBoxWrapWidth,
			TopBoxHeightRange = TopBoxHeightRange,
			TopBoxHeightPad = TopBoxHeightPad,
			// Fable 3's own box art: the TopBox's `Bar` [1519] (BoxName 'TopBox', the `hud` atlas) under
			// the TopBoxX / TopBoxY meters [1518] / [1517] with its `Group` [1522] highlight and glow
			// quads, the Subtitles and Corner boxes' nine-slices under their X / Y meters — drawn at the
			// size the meters state, never Fable 2's fitted rounded panel.
			AuthoredTextBoxPanels = true,
			// A mod's HUD pieces and meters are drawn once, on the HUD's own view.
			HostsMods = Document == Fable3Elements.HudDocument,
			Glints = GlintChannel,
		};
		View.Values.Strings = host.Strings ?? Fable3Strings.Current;
		parent.AddChild(View);
		_parent = parent;
		_host = host;

		// The title's arrangement files (Engine_HudLayout.FolderFor): shipped < mods < the player's,
		// the snapshot written beneath the player's file for the player's tool and modders.
		Refii.Core.Engine_Title title = Refii.Core.Engine_Title.Fable3;
		string folder = Engine_HudLayout.FolderFor(title);
		DirAccess.MakeDirRecursiveAbsolute(folder);
		int modArranged = layout.ApplyModLayout();
		if (_layouts)
		{
			layout.Save(Engine_HudLayout.SnapshotPathFor(title), Engine_HudLayout.SnapshotLayer);
		}

		int arranged = layout.ApplyOverrides(Engine_HudLayout.LayoutPathFor(title));
		var ids = new List<string>();
		foreach (Engine_HudLayout.Piece piece in layout.Pieces)
		{
			ids.Add(piece.Id);
		}

		int replaced = View.Art.LoadOverrides(folder, ids);

		int count = _screen.Nodes.Length;
		_slides = new int[count];
		_opacity = new float?[count];
		_opacitySources = new Fable3HudStatements.OpacitySource?[count];
		_texts = new string?[count];
		_times = new float?[count];
		_transforms = new float?[]?[count];
		_textures = new uint[count];
		_colours = new (float, float, float, float)?[count];
		_active = new bool[count];
		_meters = new string?[count];
		_textNames = new string?[count];
		foreach (Engine_UiBindings.Binding binding in _bindings.All)
		{
			if (binding.ElementOrdinal < 0 || binding.ElementOrdinal >= count)
			{
				continue;
			}

			if (binding.Kind == Engine_UiBindings.Kind.Meter)
			{
				_meters[binding.ElementOrdinal] ??= binding.Name;
			}
			else if (binding.Kind == Engine_UiBindings.Kind.Text)
			{
				_textNames[binding.ElementOrdinal] ??= binding.Name;
			}
		}

		_playback = new Engine_UiSlidePlayback(_screen);
		_slideStamps = new ulong[count];
		_played = false;
		_live = null;

		IndexScriptPositioned(_screen.Document);
		_statements = new Fable3HudStatements(PathsOf(_screen), Bound, SlideOwners(_screen.Document), Named);
		// The rest state from the slides alone: an ancestor's slide gates a child component over
		// the child's own self-activating entry (Engine_UiElementResolve.AncestorsWinAtRest).
		_resolve = new Engine_UiElementResolve(_screen) { AncestorsWinAtRest = true };
		View.Build();
		Live.Remove(this);
		Live.Add(this);
		GD.Print($"[fable3-hud] '{Document}' {count} element(s), {_bindings.Describe()}; " +
			$"{layout.Pieces.Count} piece(s), {Groups.Count} cut whole; {replaced} piece(s) replaced from {folder}" +
			(modArranged == 0 ? string.Empty : $"; {modArranged} moved by mods") +
			(arranged == 0 ? string.Empty : $"; {arranged} moved by {Engine_HudLayout.LayoutPathFor(title)}"));
		return true;
	}

	/// <summary>The documents standing, for the debug channel (`f3ui`).</summary>
	public static readonly List<Fable3HudDocument> Live = new();

	/// <summary>
	/// Where this document's file authors the element at <paramref name="path"/> on the canvas — its
	/// position summed up the chain (sub_82818760), the widescreen adapters' writes taken as the frame
	/// takes them (<see cref="WidescreenStated"/>) — and, for a text, its horizontal alignment (the
	/// .fac's: 0 left, 1 centred, 2 right). With <paramref name="slideOwner"/> and <paramref name="slide"/>, as
	/// that owner's slide stands it (its position writes taken, <see cref="Engine_UiTextBoxFit.SlideStated"/>;
	/// the slide numbered from 1 past the master, as the guest enters it). Null until the document stands,
	/// or for a path it does not hold.
	/// </summary>
	public (float X, float Y, int HorzAlign)? Authored(string path, string? slideOwner = null, int slide = 0)
	{
		if (_screen is null || _statements is null || _statements.Resolve(path) is not { Count: > 0 } found)
		{
			return null;
		}

		int ordinal = found[0];
		System.Func<int, (float? X, float? Y)> stated = WidescreenStated;
		if (slideOwner is not null && slide > 0 && _statements.Resolve(slideOwner) is { Count: > 0 } owner)
		{
			stated = Engine_UiTextBoxFit.SlideStated(Engine_UiElementResolve.SlideOf(_screen.Document, owner[0], slide), WidescreenStated);
		}

		(float x, float y) = Engine_UiTextBoxFit.ChainPosition(_screen.Document, ordinal, stated);
		int align = ordinal >= 0 && ordinal < _screen.Nodes.Length ? _screen.Nodes[ordinal].Text?.HorzAlign ?? 0 : 0;
		return (x, y, align);
	}

	/// <summary>
	/// For the debug channel: every element whose path contains <paramref name="match"/> — what the
	/// guest stated for it (<see cref="Fable3Elements"/>), what the feed folded into the view's
	/// channels, and what the resolve made of it (drawn, own and accumulated opacity, why dark) — and
	/// the view's pieces under the same match.
	/// </summary>
	public Dictionary<string, object?> Describe(string match, int limit)
	{
		var rows = new List<Dictionary<string, object?>>();
		if (_screen is not null && _resolve is not null)
		{
			string[] paths = PathsOf(_screen);
			for (int i = 0; i < paths.Length && rows.Count < limit; ++i)
			{
				if (match.Length != 0 && !paths[i].Contains(match, System.StringComparison.Ordinal))
				{
					continue;
				}

				bool drawn = i < _resolve.Drawn.Length && _resolve.Drawn[i];
				rows.Add(new Dictionary<string, object?>
				{
					["ordinal"] = i,
					["path"] = paths[i],
					["slide"] = i < _slides.Length ? _slides[i] : null,
					["fedOpacity"] = i < _opacity.Length ? _opacity[i] : null,
					["text"] = i < _texts.Length ? _texts[i] : null,
					["time"] = i < _times.Length ? _times[i] : null,
					["drawn"] = drawn,
					["own"] = i < _resolve.Own.Length ? _resolve.Own[i] : null,
					["opacity"] = i < _resolve.Opacity.Length ? _resolve.Opacity[i] : null,
					["active"] = i < _active.Length && _active[i],
					["dark"] = drawn ? null : _resolve.WhyDark(i),
				});
			}
		}

		var stated = new List<Dictionary<string, object?>>();
		foreach (KeyValuePair<(string Document, string Path), Fable3Elements.Element> entry in Fable3Elements.Of(Document))
		{
			if (stated.Count >= limit || (match.Length != 0 && !entry.Key.Path.Contains(match, System.StringComparison.Ordinal)))
			{
				continue;
			}

			Fable3Elements.Element e = entry.Value;
			stated.Add(new Dictionary<string, object?>
			{
				["path"] = entry.Key.Path,
				["address"] = e.Address.ToString("X8"),
				["state"] = e.State,
				["opacity"] = e.Opacity,
				["visible"] = e.Visible,
				["time"] = e.Time,
				["position"] = e.Position is { } at ? new[] { at.X, at.Y, at.Z } : null,
				["statedState"] = e.StatedState,
				["statedOpacity"] = e.StatedDrawnOpacity,
				["statedText"] = e.StatedTextValue,
				["statedTexture"] = e.StatedTexture,
				["stamp"] = e.Stamp,
			});
		}

		return new Dictionary<string, object?>
		{
			["document"] = Document,
			["elements"] = rows,
			["stated"] = stated,
			["pieces"] = View?.Describe(match),
		};
	}

	/// <summary>The ordinals bound to `Attribute:value`, by the attribute's spelling in the file.</summary>
	private IReadOnlyList<int> Bound(string attribute, string value) =>
		_bindings is null ? System.Array.Empty<int>() : Bound(_bindings, attribute, value);

	/// <summary>The ordinals <paramref name="bindings"/> binds to `Attribute:value`, by the attribute's spelling in the file.</summary>
	internal static IReadOnlyList<int> Bound(Engine_UiBindings bindings, string attribute, string value)
	{
		foreach ((Engine_UiBindings.Kind kind, string spelled) in Engine_UiBindings.Attributes)
		{
			if (spelled == attribute)
			{
				return bindings.Elements(kind, value);
			}
		}

		return System.Array.Empty<int>();
	}

	/// <summary>
	/// One frame: the window, and on a statement or the guest's frame (an unstated meter stands at
	/// the registry value it published, which moves without an element write) the folded channels.
	/// </summary>
	public void Frame(Vector2 window)
	{
		if (View is null || _statements is null || _resolve is null)
		{
			return;
		}

		View.Window = window;
		SeatPlayers(window);
		PlayStatedParticles();

		// The mini-game bar's cloned segments (minigamebarcontrol.lua `clone`), only once its script ran here.
		if (Fable3LuaContracts.BarOf(Document) is { Elements.Count: > 0 } bar)
		{
			_barClones.Drive(View, bar, _screen, _resolve, _statements.Resolve);
		}

		if (Fable3Elements.Generation == _fed && !Refii.Core.Engine_GuestFrame.Changed && !OverridesChanged)
		{
			return;
		}

		_fed = Fable3Elements.Generation;
		Feed();
	}

	private void Feed()
	{
		_gathered.Clear();
		_inactivePaths.Clear();
		foreach (KeyValuePair<(string Document, string Path), Fable3Elements.Element> entry in Fable3Elements.Of(Document))
		{
			// An element filed under a name the registry writers state is written by them: their
			// statement (Fable3HudRegistry, the text and meter values below) is the one taken. Where
			// a laid-out box's line stands is Godot's (StateStacked).
			Fable3Elements.Element stated = entry.Value;
			if (IsRegistryStated(entry.Key.Path))
			{
				continue;
			}

			if (stated.StatedActive == false)
			{
				_inactivePaths.Add(entry.Key.Path);
			}

			// The slide under its own stamp: an entry is re-entered only by a newer slide write.
			if (stated.StatedState is { } slide)
			{
				_gathered.Add(new Fable3HudStatements.Statement(
					entry.Key.Path, slide, null, null, null, stated.StatedStateStamp));
			}

			if (stated.StatedDrawnOpacity is not null || stated.Stated is not null || stated.StatedTexture is not null ||
				stated.Colour is not null || stated.Time is not null || stated.Position is not null)
			{
				_gathered.Add(new Fable3HudStatements.Statement(
					entry.Key.Path, null, stated.StatedDrawnOpacity, stated.StatedTextValue, stated.Time,
					stated.Stamp, stated.Position, stated.StatedTexture, Colour: stated.Colour,
					Source: stated.StatedDrawnOpacity is null ? null : stated.OpacitySource));
			}
		}

		_gathered.AddRange(Fable3HudRegistry.StatementsFor(Document));
		_gathered.AddRange(Fable3LuaContracts.StatementsFor(Document, SlideNamed));
		_gathered.AddRange(Fable3ScreenFrames.StatementsFor(Document));
		_statements!.Apply(_gathered, _slides, _opacity, _texts, _times, _transforms, _textures,
			_playback is null ? null : _slideStamps, _colours, _opacitySources);
		ApplyModOverrides();
		_meterTimes.Clear();
		StatePlaced();
		StateWidescreen();
		StateLuaTransforms();
		StateStacked();
		if (_statements.Unmapped != _reportedUnmapped)
		{
			_reportedUnmapped = _statements.Unmapped;
			GD.Print($"[fable3-hud] {_statements.Mapped} statement(s) joined to '{Document}', {_statements.Unmapped} not" +
				(_statements.Unmapped == 0 ? string.Empty : $": {string.Join(", ", _statements.UnmappedPaths)}"));
		}

		Engine_HudValues values = View!.Values;
		for (int i = 0; i < _slides.Length; ++i)
		{
			// A text the registry states (hud_feed.cpp, registry 6) where no element write carried
			// one; else the line its text box's caller passed (sub_82920BC8, `@Hud.Lines`) — the
			// registry's write is that line wrapped, made only for a box the TextBox registry filed.
			if (_texts[i] is null && _textNames[i] is { } textName)
			{
				string? written = values.Text(Engine_HudValues.Handle.For(textName));
				string? line = Fable3HudRegistry.Line(Refii.Core.Engine_GameHash.Fnv1(textName));
				// The registry's write wins; an EMPTY one does not over a non-empty stated line (the
				// Sanctuary's counters drew empty bars with the registry at '' and the line stated —
				// round 7q). A cleared subtitle states '' as its line and keeps its slide's hide.
				_texts[i] = string.IsNullOrEmpty(written) && !string.IsNullOrEmpty(line) ? line : written ?? line;
				CheckLine(textName, written, line);
			}

			// A meter's timeline: the stated position, else the registry's value (`pct × 10`, the
			// guest's own scale, sub_8281AD48 at 0x8281ADA4), else its start — a paused meter the
			// guest has not positioned stands at 0, not at its last key.
			if (_times[i] is null && _meters[i] is { } meter)
			{
				float? percent = values.Meter(Engine_HudValues.Handle.For(meter));
				_times[i] = percent is { } p ? Mathf.Clamp(p, 0f, 100f) * 10f : 0f;
				if (percent is not null)
				{
					_meterTimes.Add((i, (int)_times[i]!.Value));
				}
			}
		}

		if (_playback is not null)
		{
			Play(_playback);
			RetireOverwrittenOpacity(_playback);
			StateReached(_playback);
		}

		_resolve!.Apply(_slides, null, _opacity);
		StateLiveEdges();
		for (int i = 0; i < _active.Length; ++i)
		{
			// Drawn, by its slide and its parents', and not faded out — Fable 2's one answer.
			_active[i] = _resolve.Drawn[i] && _resolve.Opacity[i] > 0.01f;
		}

		View.Resolve = _resolve;
		View.LiveSlides = _slides;
		View.ActiveInputs = null;
		View.StatedOpacity = _opacity;
		View.LiveTexts = _texts;
		View.LiveTimes = _times;
		View.LiveTransform = _transforms;
		View.LiveTextures = _textures;
		View.LiveColours = _colours;
		View.ElementActive = _active;
		ShareFeed();
	}

	/// <summary>
	/// Each couch player's HUD in their own half while the views are split: player one's view in player
	/// one's seat, player two's (built once) in theirs; the whole window otherwise, player two's held hidden.
	/// Only `hud.bgf` is seated — the interaction mode, the end screens and the box frames are the menus
	/// of the one screen, drawn whole as the original draws them.
	/// </summary>
	private void SeatPlayers(Vector2 window)
	{
		if (View is null || Document != Fable3Elements.HudDocument)
		{
			return;
		}

		bool split = Refii.Core.Engine_Views.Split;
		View.Seat = split ? Engine_UiSeats.Seat.PlayerOne : Engine_UiSeats.Seat.Whole;
		if (split && ViewTwo is null)
		{
			BuildViewTwo();
		}

		if (ViewTwo is null || !GodotObject.IsInstanceValid(ViewTwo))
		{
			return;
		}

		ViewTwo.Window = window;
		ViewTwo.Suppressed = !split;
		ViewTwo.Visible = split;
		Node.ProcessModeEnum mode = split ? Node.ProcessModeEnum.Inherit : Node.ProcessModeEnum.Disabled;
		if (ViewTwo.ProcessMode != mode)
		{
			ViewTwo.ProcessMode = mode;
		}
	}

	/// <summary>
	/// Fable 3's GUI glints (Engine_UiParticles): every `ParticleEffect` element the view's pieces hold —
	/// the box frames' glass glints, the d-pad's, the epilogue's, the weapon panel's — drawn from the
	/// document's own playback. Fable 3's particle system takes the element's opacity as its live
	/// alpha (command 15, `sub_831C15E8`).
	/// </summary>
	private const Engine_UiParticles.OpacityChannel GlintChannel = Engine_UiParticles.OpacityChannel.Alpha;

	private readonly List<Fable3UiParticles.Shot> _shots = new();

	/// <summary>
	/// The one-shots the guest stated on this document, each on the view that DRAWS its element: a
	/// player-two element (the guild seal tick names `FollowerCollection2`, its `"2"` suffix from the
	/// tick's player byte, TU3 0x8261FC54..0x8261FC64 / disc 0x825F82DC..0x825F82EC) on player two's half
	/// while the views are split — that view's widgets are keyed to player two's elements — and on the
	/// whole screen's view otherwise. A point (the position form) is the whole canvas's.
	/// </summary>
	private void PlayStatedParticles()
	{
		_shots.Clear();
		Fable3UiParticles.Drain(Document, _shots);
		foreach (Fable3UiParticles.Shot shot in _shots)
		{
			bool played = false;
			if (shot.Point is { } point)
			{
				played = View?.GlintDriver is { } whole &&
					whole.BurstAt(new Vector3(point.X, point.Y, point.Z), shot.Effect, shot.Scale);
			}
			else if (_statements is not null)
			{
				foreach (int ordinal in _statements.Resolve(shot.Path))
				{
					foreach (Engine_HudView? view in new[] { View, ViewTwo })
					{
						if (view is not null && GodotObject.IsInstanceValid(view) && view.IsVisibleInTree() &&
							view.GlintDriver is { } glints && glints.Burst(ordinal, shot.Effect, shot.Scale))
						{
							played = true;
							break;
						}
					}
				}
			}

			if (!played)
			{
				Fable3UiParticles.Unplaced(shot);
			}
		}
	}

	private void BuildViewTwo()
	{
		if (View is null || _parent is null || _host is null || !GodotObject.IsInstanceValid(_parent))
		{
			return;
		}

		ViewTwo = new Engine_HudView
		{
			Name = "Hud_" + Document + "_PlayerTwo",
			Theme = View.Theme,
			Screen = View.Screen,
			Art = View.Art,
			ResolveTexture = View.ResolveTexture,
			Layout = View.Layout,
			StatesTimes = true,
			TopBoxWrapWidth = TopBoxWrapWidth,
			TopBoxHeightRange = TopBoxHeightRange,
			TopBoxHeightPad = TopBoxHeightPad,
			AuthoredTextBoxPanels = true,
			// The mods' pieces are drawn once, on player one's view.
			HostsMods = false,
			Glints = GlintChannel,
			// Player one's registry values; the view's own statements (its TopBox height) over them.
			Values = new Engine_HudValues { Under = View.Values },
			ReadsValues = false,
			Seat = Engine_UiSeats.Seat.PlayerTwo,
			Suppressed = true,
			Visible = false,
			ProcessMode = Node.ProcessModeEnum.Disabled,
		};
		ViewTwo.Values.Strings = View.Values.Strings;
		_parent.AddChild(ViewTwo);
		ViewTwo.Build();
		ShareFeed();
		GD.Print($"[fable3-hud] '{Document}' player two's half built: player one's widgets keyed to player two's");
	}

	/// <summary>Player two's half takes the element feed player one's view was just given: one document, one read, two views.</summary>
	private void ShareFeed()
	{
		if (View is null || ViewTwo is null || !GodotObject.IsInstanceValid(ViewTwo))
		{
			return;
		}

		ViewTwo.Resolve = View.Resolve;
		ViewTwo.LiveSlides = View.LiveSlides;
		ViewTwo.ActiveInputs = View.ActiveInputs;
		ViewTwo.StatedOpacity = View.StatedOpacity;
		ViewTwo.LiveTexts = View.LiveTexts;
		ViewTwo.LiveTimes = View.LiveTimes;
		ViewTwo.LiveTransform = View.LiveTransform;
		ViewTwo.LiveTextures = View.LiveTextures;
		ViewTwo.LiveColours = View.LiveColours;
		ViewTwo.ElementActive = View.ElementActive;
	}

	/// <summary>
	/// A stated opacity a later told slide entry wrote over is the slides' again, as the guest's GoTo
	/// left it (Fable3HudStatements.OpacitySource).
	/// </summary>
	private void RetireOverwrittenOpacity(Engine_UiSlidePlayback playback)
	{
		for (int i = 0; i < _opacity.Length && i < _opacitySources.Length; ++i)
		{
			if (_opacity[i] is not null && _opacitySources[i] is { } source)
			{
				_opacity[i] = source.After(
					playback.ToldEntryWrote(i, Engine_UiSlidePlayback.Channel.Opacity), playback.ToldEntryActive(i));
			}
		}
	}

	/// <summary>
	/// One step of a stated-only document's playback: every stated slide is an entry (a newer stamp
	/// of the same slide enters it again — the guest's funnel restarts it), an authored-paused slide
	/// waits where the meter's GoToTime (sub_8281A5D8 -> `vt[9]()->vt[11]` at 0x8281A634, the meters'
	/// `fraction × 1000`) puts it, the rest run over real time; the tracks' values go to the resolve
	/// (positions, scales, opacity) and the slide each component plays (a finished go-next slide
	/// moves on) replaces the stated one.
	/// </summary>
	private void Play(Engine_UiSlidePlayback playback)
	{
		long now = PlayClock.ElapsedMilliseconds;
		int[] playing = playback.Step(
			_slides, _played ? _resolve!.Drawn : null, now, null,
			at =>
			{
				foreach ((int ordinal, int time) in _meterTimes)
				{
					playback.GoToTime(ordinal, time, now);
				}
			},
			ordinal => ordinal >= 0 && ordinal < _slideStamps.Length && _slideStamps[ordinal] != 0
				? new Engine_UiSlideStatements.Told(_slides[ordinal], _slideStamps[ordinal], null, 0)
				: null,
			Fable3Elements.Generation);
		System.Array.Copy(playing, _slides, System.Math.Min(playing.Length, _slides.Length));
		_resolve!.Animate(playback.Values, playback.Closed);
		_played = true;
	}

	/// <summary>
	/// THE BOXES' EXTENTS (CHUDExpandableBox vf+12, TU3 sub_8227AEB0): each BoxName box of the
	/// document calibrates the range it is drawn between — its scale up the chain at its meters'
	/// end and start (<see cref="Engine_UiTextBoxFit.Extents"/>). Godot draws that box, so Godot
	/// states the range as the document stands (`ui-box:`, ui_reader_hooks.cpp), and the guest's
	/// text layout wraps and pages by it.
	/// </summary>
	private void StateBoxExtents()
	{
		uint boxName = Refii.Core.Engine_GameHash.AttributeId(Engine_UiTextBoxFit.BoxNameAttribute);
		Engine_BgfDocument document = _screen!.Document;
		for (int i = 0; i < document.Elements.Length; ++i)
		{
			if (!document.Elements[i].TryGetAttribute(boxName, out BgfAttribute attribute) ||
				attribute.Type != BgfValueType.String ||
				document.GetString(attribute.AsStringIndex()) is not { Length: > 0 } name)
			{
				continue;
			}

			var meters = new List<int>(_bindings!.Elements(Engine_UiBindings.Kind.Meter, name + "X"));
			meters.AddRange(_bindings.Elements(Engine_UiBindings.Kind.Meter, name + "Y"));
			(float minX, float minY, float maxX, float maxY) = Engine_UiTextBoxFit.Extents(_screen, i, meters);
			System.FormattableString line = $"ui-box:{Document}|{i}|{minX:R}|{minY:R}|{maxX:R}|{maxY:R}";
			Refii.Host.HostBridge.PostFrontEndAction(line.ToString(System.Globalization.CultureInfo.InvariantCulture));
			GD.Print($"[fable3-hud] box '{name}' [{i}] of '{Document}': extents min ({minX:R}, {minY:R}) max ({maxX:R}, {maxY:R}), " +
				$"meters [{string.Join(", ", meters)}] — stated");
		}
	}

	/// <summary>
	/// Per path, the element and its `@Ui.State` entry count already stated as past its owner's Master
	/// end — a load makes the path's element anew and its count starts again.
	/// </summary>
	private readonly Dictionary<string, (Fable3Elements.Element Element, int Entries)> _reached = new(System.StringComparer.Ordinal);

	/// <summary>
	/// THE SCROLL GATE'S CLOCK (TU3 sub_82878358): before each scroll step the guest asks whether the
	/// list component's clock passed its Master end since its path setter last entered a slide (the
	/// interaction list's 200 ms). This playback is that clock: a path-stated owner whose clock stands
	/// at or past its Master end is stated once per entry (`ui-reached:`, ui_reader_hooks.cpp).
	/// </summary>
	private void StateReached(Engine_UiSlidePlayback playback)
	{
		foreach (KeyValuePair<(string Document, string Path), Fable3Elements.Element> entry in Fable3Elements.Of(Document))
		{
			int entries = entry.Value.Entries;
			if (entries == 0 ||
				(_reached.TryGetValue(entry.Key.Path, out (Fable3Elements.Element Element, int Entries) told) &&
					ReferenceEquals(told.Element, entry.Value) && told.Entries == entries))
			{
				continue;
			}

			IReadOnlyList<int> ordinals = _statements!.Resolve(entry.Key.Path);
			if (ordinals.Count == 0 || playback.OwnerClock(ordinals[0]) is not { } clock || clock.Time < clock.MasterEnd)
			{
				continue;
			}

			_reached[entry.Key.Path] = (entry.Value, entries);
			System.FormattableString line = $"ui-reached:{Document}|{entry.Key.Path}|{entries}";
			Refii.Host.HostBridge.PostFrontEndAction(line.ToString(System.Globalization.CultureInfo.InvariantCulture));
		}
	}

	/// <summary>The paths whose last `@Ui.Active` is off, gathered by each <see cref="Feed"/>.</summary>
	private readonly List<string> _inactivePaths = new();

	/// <summary>Per element, whether it was live at the last feed; null before the first.</summary>
	private bool[]? _live;

	/// <summary>The edges stated since the document was made, for the log's cap.</summary>
	private int _edges;

	/// <summary>
	/// THE RE-VISIBILITY EDGE (the presentation Update's walk, TU3 sub_821C9CF8 0x821C9E4C..0x821C9E90):
	/// a timeline owner that becomes visible — drawn by its slides and its parents', its window open,
	/// no stated active bit off on it or above it; opacity never counts — is sent to slide 1 by the
	/// guest's own GoTo(owner, 1, 0). This playback is that walk: each component that comes live is
	/// stated once (`ui-live:`, ui_reader_hooks.cpp), and the host's handle takes slide 1.
	/// </summary>
	private void StateLiveEdges()
	{
		Engine_BgfDocument document = _screen!.Document;
		int count = System.Math.Min(_resolve!.Drawn.Length, document.Elements.Length);
		var off = new bool[count];
		foreach (string path in _inactivePaths)
		{
			foreach (int ordinal in _statements!.Resolve(path))
			{
				if (ordinal >= 0 && ordinal < count)
				{
					off[ordinal] = true;
				}
			}
		}

		var live = new bool[count];
		for (int i = 0; i < count; ++i)
		{
			int parent = document.Elements[i].ParentOrdinal;
			bool above = parent < 0 || parent >= i || live[parent];
			live[i] = above && _resolve.Drawn[i] && !off[i];
		}

		bool[]? was = _live;
		_live = live;
		if (was is null || was.Length != count)
		{
			return;
		}

		for (int i = 0; i < count; ++i)
		{
			if (live[i] && !was[i] && document.Elements[i].IsComponent)
			{
				System.FormattableString line = $"ui-live:{Document}|{i}";
				Refii.Host.HostBridge.PostFrontEndAction(line.ToString(System.Globalization.CultureInfo.InvariantCulture));
				if (++_edges <= 64)
				{
					GD.Print($"[fable3-hud] '{Document}' [{i}] came live — stated ({_edges})");
				}
			}
		}
	}

	/// <summary>Text names whose stated line and registry write were compared and differed, logged once each.</summary>
	private readonly HashSet<string> _lineChecks = new(System.StringComparer.Ordinal);

	/// <summary>The cross-check of a stated line against the registry's wrapped write of it.</summary>
	private void CheckLine(string textName, string? written, string? line)
	{
		if (written is null || line is null ||
			Fable3Elements.Normalise(written) == Fable3Elements.Normalise(line) || _lineChecks.Count > 200)
		{
			return;
		}

		if (_lineChecks.Add(textName + "\u0001" + line))
		{
			GD.Print($"[fable3-statecheck] DIFF line '{Document}' {textName}: stated '{line}', registry wrote '{written}'");
		}
	}

	/// <summary>
	/// Whether a statement's path is `Attribute:value` for an attribute the registry writers state
	/// (<see cref="Fable3HudRegistry.IsStatedAttribute"/>).
	/// </summary>
	internal static bool IsRegistryStated(string path)
	{
		int colon = path.IndexOf(':');
		if (colon <= 0)
		{
			return false;
		}

		int dot = path.IndexOf('.');
		return (dot < 0 || dot > colon) && Fable3HudRegistry.IsStatedAttribute(path[..colon]);
	}

	/// <summary>
	/// The ordinals bound under <paramref name="hash"/> in the kind's registry, or every ordinal the
	/// document binds in it for a null hash.
	/// </summary>
	private IReadOnlyList<int> Named(Engine_UiBindings.Kind kind, uint? hash)
	{
		if (_bindings is null)
		{
			return System.Array.Empty<int>();
		}

		if (hash is { } name)
		{
			return _bindings.Elements(kind, name);
		}

		if (!_everyOfKind.TryGetValue(kind, out IReadOnlyList<int>? every))
		{
			var ordinals = new List<int>();
			foreach (Engine_UiBindings.Binding binding in _bindings.All)
			{
				if (binding.Kind == kind)
				{
					ordinals.Add(binding.ElementOrdinal);
				}
			}

			_everyOfKind[kind] = every = ordinals;
		}

		return every;
	}

	/// <summary>Files every element's `ScriptPositioned` value under its FNV-1, as the registry filer does.</summary>
	private void IndexScriptPositioned(Engine_BgfDocument document)
	{
		_placed.Clear();
		uint id = Refii.Core.Engine_GameHash.AttributeId(Fable3HudRegistry.ScriptPositionedAttribute);
		for (int ordinal = 0; ordinal < document.Elements.Length; ordinal++)
		{
			if (!document.Elements[ordinal].TryGetAttribute(id, out BgfAttribute attribute) ||
				attribute.Type != BgfValueType.String)
			{
				continue;
			}

			string name = document.GetString(attribute.AsStringIndex());
			if (name.Length == 0)
			{
				continue;
			}

			uint hash = Refii.Core.Engine_GameHash.Fnv1(name);
			if (!_placed.TryGetValue(hash, out List<int>? ordinals))
			{
				_placed[hash] = ordinals = new List<int>();
			}

			ordinals.Add(ordinal);
		}
	}

	/// <summary>Every script-placed element at the (x, y) its name was placed at (sub_82B4FD30), z its own.</summary>
	private void StatePlaced()
	{
		foreach (KeyValuePair<uint, (float X, float Y)> placed in Fable3HudRegistry.Placed)
		{
			if (!_placed.TryGetValue(placed.Key, out List<int>? ordinals))
			{
				continue;
			}

			foreach (int ordinal in ordinals)
			{
				if (ordinal < 0 || ordinal >= _transforms.Length)
				{
					continue;
				}

				float?[] row = _transforms[ordinal] ??= new float?[Fable3HudStatements.TransformWidth];
				row[0] = placed.Value.X;
				row[1] = placed.Value.Y;
			}
		}
	}

	/// <summary>
	/// THE WIDESCREEN ADAPTER'S WRITE IS A STATEMENT. `widescreenadapter.lua` (onInitializeReal →
	/// setWidescreenMode) reads its own `widescreenXPos` / `widescreenYPos` / `WidescreenXWidth` and
	/// `setAttribute`s them onto its PARENT's `position.x` / `.y` / `scale.x` when `GUI.State.
	/// IsWidescreen` — a Lua write, not through sub_82818580, so no hook carries it. The layout
	/// takes it for a cut piece's root (Engine_HudLayout's wide position); an adapter on an element
	/// INSIDE a piece (`ReceivedItems.ReceivedItemAlert`, -136; `CornerBox…Group`, -135) moved
	/// nothing, which left the parked received-item alert's gift on screen. Stated here wherever the
	/// guest has not written that channel itself.
	/// </summary>
	/// <summary>
	/// The shipped Lua's transform writes (Fable3LuaContracts.Transforms: the mini-game bar's pip and
	/// segment templates), written over the folded rows as the script's setAttribute was — after the
	/// C++'s writes of the same frame.
	/// </summary>
	private void StateLuaTransforms()
	{
		foreach ((string path, int channel, float value) in Fable3LuaContracts.Transforms(Document))
		{
			foreach (int ordinal in _statements!.Resolve(path))
			{
				if (ordinal >= 0 && ordinal < _transforms.Length)
				{
					float?[] row = _transforms[ordinal] ??= new float?[Fable3HudStatements.TransformWidth];
					row[channel] = value;
				}
			}
		}
	}

	/// <summary>
	/// A component's slide by NAME, as Gameface's goToSlide(element, name) resolves it: the index
	/// relative to the owner's own run, its master 0 (BgfSlide). Null when the path or name is not
	/// in this document.
	/// </summary>
	private int? SlideNamed(string path, string name)
	{
		if (_screen is null || _statements is null || _statements.Resolve(path) is not { Count: > 0 } owners)
		{
			return null;
		}

		int owner = owners[0];
		int relative = 0;
		foreach (BgfSlide slide in _screen.Document.Slides)
		{
			if (slide.OwnerElement != owner)
			{
				continue;
			}

			if (slide.Name == name)
			{
				return relative;
			}

			++relative;
		}

		return null;
	}

	/// <summary>Per line element ordinal, where the last stacking of its box placed it (a line keeps its place until its box is laid again, as the fit's write does).</summary>
	private readonly Dictionary<int, (float X, float Y)> _stacked = new();

	private readonly List<Engine_UiTextBoxStack.Measured> _measured = new();

	/// <summary>Measured blocks by (text, point size): the walk restates its lines every frame.</summary>
	private readonly Dictionary<(string Text, int Size), Engine_UiTextBoxStack.Measured> _measures = new();

	/// <summary>Per box, the places last stacked, so the report is built only on a change.</summary>
	private readonly Dictionary<string, (float X, float Y)[]> _lastPlaced = new(System.StringComparer.Ordinal);

	/// <summary>Per box, the line vector its last fit laid (a new statement is a new fit).</summary>
	private readonly Dictionary<string, object> _fitLines = new(System.StringComparer.Ordinal);

	/// <summary>Per box, its X meter as its fits see it (Fable3TextBoxes.MeterLag).</summary>
	private readonly Dictionary<string, Fable3TextBoxes.MeterLag> _meterLags = new(System.StringComparer.Ordinal);

	/// <summary>Per box, the position tracks its X meter's slide plays on the chain, read once.</summary>
	private readonly Dictionary<string, Dictionary<int, (BgfAnimation? X, BgfAnimation? Y)>> _meterTracks = new(System.StringComparer.Ordinal);

	/// <summary>The box being laid: its X meter's lag and tracks, read by <see cref="StackStated"/>.</summary>
	private Fable3TextBoxes.MeterLag? _layingLag;
	private Dictionary<int, (BgfAnimation? X, BgfAnimation? Y)>? _layingTracks;

	/// <summary>
	/// THE LAID-OUT BOXES' LINES ARE GODOT'S (Fable3TextBoxes: the corner counters, the subtitles).
	/// Each box's lines are the vector its caller last stated (`@Hud.Lines`, sub_82920BC8), in
	/// order; each line is measured as Godot draws it — the text registry's wrapped write of it,
	/// else the stated line, in the line element's own font size through the title's text
	/// contract (Engine_UiTextMesh.Measure) — and the fit's contract stacks them
	/// (Engine_UiTextBoxStack: line 0 at the BoxName chain's FirstLineY for the fitted height,
	/// each next line one measured block lower). The chain reads the widescreen adapters' writes
	/// (the Corner's `Group` [422] x −135), as the guest's getter does on the 360's widescreen
	/// set. The rows are the line elements' local position, the channel the view's binding
	/// writes; the fit's own `@Element.Position` on them is dropped (Fable3Elements).
	/// </summary>
	private void StateStacked()
	{
		if (_screen is null || _bindings is null || View?.Theme?.Fonts is not { } fonts)
		{
			return;
		}

		Engine_HudValues values = View.Values;

		// A subtitle fit refits the corner without a meter of its own (sub_825E7BA0).
		bool subtitlesFit = Fable3HudRegistry.BoxLines(Refii.Core.Engine_GameHash.Fnv1(Fable3TextBoxes.Subtitles.Name)) is { } said &&
			!(_fitLines.TryGetValue(Fable3TextBoxes.Subtitles.Name, out object? subtitlesLaid) && ReferenceEquals(subtitlesLaid, said));
		foreach (Engine_UiTextBoxStack.Box box in Fable3TextBoxes.Stacked)
		{
			if (Fable3HudRegistry.BoxLines(Refii.Core.Engine_GameHash.Fnv1(box.Name)) is not { Count: > 0 } lines)
			{
				continue;
			}

			bool fit = !(_fitLines.TryGetValue(box.Name, out object? laid) && ReferenceEquals(laid, lines));
			_measured.Clear();
			foreach ((uint hash, string stated) in lines)
			{
				IReadOnlyList<int> ordinals = Named(Engine_UiBindings.Kind.Text, hash);
				UiText? authored = ordinals.Count > 0 && ordinals[0] < _screen.Nodes.Length ? _screen.Nodes[ordinals[0]].Text : null;
				string painted = values.Text(new Engine_HudValues.Handle(string.Empty, hash)) ?? stated;
				if (authored is null || fonts.For(authored.Size) is not { } font)
				{
					_measured.Add(new Engine_UiTextBoxStack.Measured(0f, 0f));
					continue;
				}

				if (!_measures.TryGetValue((painted, authored.Size), out Engine_UiTextBoxStack.Measured measured))
				{
					var text = new UiText
					{
						Display = painted,
						Size = authored.Size,
						HorzAlign = authored.HorzAlign,
						VertAlign = authored.VertAlign,
						WrapMode = authored.WrapMode,
						BoxWidth = authored.BoxWidth,
						BoxHeight = authored.BoxHeight,
						Truncate = authored.Truncate,
					};
					(int _, float width, float height) = Engine_UiTextMesh.Measure(text, font.Font.Font, font.Scale, fonts.Contract);
					measured = new Engine_UiTextBoxStack.Measured(width, height);
					if (_measures.Count > 256)
					{
						_measures.Clear();
					}

					_measures[(painted, authored.Size)] = measured;
				}

				_measured.Add(measured);
			}

			// THE FIT'S METERS ARE GODOT'S: stated from the size Godot fitted, as sub_82921110 states the
			// guest's (0x829205C8), so the box art matches the lines; the lines stand on the X meter's
			// position tracks one fit late (Fable3TextBoxes.MeterLag).
			(float fitWidth, float fitHeight) = Engine_UiTextBoxStack.Fitted(box, _measured);
			(string meterX, string meterY) = Fable3TextBoxes.MetersOf(box);
			if (!_meterLags.TryGetValue(box.Name, out Fable3TextBoxes.MeterLag? lag))
			{
				_meterLags[box.Name] = lag = new Fable3TextBoxes.MeterLag();
			}

			if (!_meterTracks.TryGetValue(box.Name, out Dictionary<int, (BgfAnimation? X, BgfAnimation? Y)>? tracks))
			{
				IReadOnlyList<int> meters = _bindings.Elements(Engine_UiBindings.Kind.Meter, meterX);
				_meterTracks[box.Name] = tracks = meters.Count > 0
					? Fable3TextBoxes.PositionTracks(_screen.Document, meters[0])
					: new Dictionary<int, (BgfAnimation? X, BgfAnimation? Y)>();
			}

			if (fit)
			{
				_fitLines[box.Name] = lines;
				float? percentX = Fable3TextBoxes.MeterPercent(box.Width, fitWidth);
				float? percentY = Fable3TextBoxes.MeterPercent(box.Height, fitHeight);
				lag.Laid(percentX);
				if (percentX is { } x)
				{
					values.OverrideMeter(Engine_HudValues.Handle.For(meterX), x);
				}

				if (percentY is { } y)
				{
					values.OverrideMeter(Engine_HudValues.Handle.For(meterY), y);
				}
			}
			else if (subtitlesFit && box.Name == Fable3TextBoxes.Corner.Name)
			{
				lag.Refit();
			}

			_layingLag = lag;
			_layingTracks = tracks;
			(float X, float Y)[]? stackedRows = Engine_UiTextBoxStack.Place(_screen.Document, box, _measured, StackStated);
			_layingLag = null;
			_layingTracks = null;
			if (stackedRows is not { } placed)
			{
				continue;
			}

			for (int i = 0; i < lines.Count; ++i)
			{
				foreach (int ordinal in Named(Engine_UiBindings.Kind.Text, lines[i].Hash))
				{
					_stacked[ordinal] = placed[i];
				}
			}

			if (_lastPlaced.TryGetValue(box.Name, out (float X, float Y)[]? before) &&
				Same(before, placed))
			{
				continue;
			}

			_lastPlaced[box.Name] = placed;
			var report = new System.Text.StringBuilder();
			(float fittedWidth, float fittedHeight) = Engine_UiTextBoxStack.Fitted(box, _measured);
			report.Append(System.FormattableString.Invariant($"{lines.Count} line(s), box {fittedWidth:0.#}x{fittedHeight:0.#}"));
			for (int i = 0; i < lines.Count; ++i)
			{
				report.Append(System.FormattableString.Invariant(
					$"; {lines[i].Hash:X8} w {_measured[i].Width:0.#} h {_measured[i].Height:0.#} -> ({placed[i].X:0.#}, {placed[i].Y:0.#}) on {Named(Engine_UiBindings.Kind.Text, lines[i].Hash).Count} element(s)"));
			}

			GD.Print($"[fable3-stack] {Document} {box.Name}: {report}");
		}

		foreach (KeyValuePair<int, (float X, float Y)> line in _stacked)
		{
			if (line.Key >= 0 && line.Key < _transforms.Length)
			{
				float?[] row = _transforms[line.Key] ??= new float?[Fable3HudStatements.TransformWidth];
				row[0] = line.Value.X;
				row[1] = line.Value.Y;
				row[2] = Engine_UiTextBoxStack.LineZ;
			}
		}
	}

	private static bool Same((float X, float Y)[] a, (float X, float Y)[] b)
	{
		if (a.Length != b.Length)
		{
			return false;
		}

		for (int i = 0; i < a.Length; ++i)
		{
			if (a[i] != b[i])
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// What the fit's chain getter reads on a chain element: the widescreen adapters' write, else the
	/// laid box's X meter track at the meter its fit sees (<see cref="_layingLag"/>), else the file.
	/// </summary>
	private (float? X, float? Y) StackStated(int ordinal)
	{
		(float? X, float? Y) wide = WidescreenStated(ordinal);
		if (wide.X is not null || wide.Y is not null)
		{
			return wide;
		}

		if (_layingLag?.AtFit is { } percent && _layingTracks is not null &&
			_layingTracks.TryGetValue(ordinal, out (BgfAnimation? X, BgfAnimation? Y) track))
		{
			return (Fable3TextBoxes.At(track.X, percent), Fable3TextBoxes.At(track.Y, percent));
		}

		return (null, null);
	}

	/// <summary>The widescreen adapters' position writes, per moved ordinal, as the fit's chain getter reads them.</summary>
	private (float? X, float? Y) WidescreenStated(int ordinal)
	{
		if (_wide is not null)
		{
			foreach (Engine_UiWidescreen.Override over in _wide.Overrides)
			{
				if (over.ParentOrdinal == ordinal)
				{
					return (over.X, over.Y);
				}
			}
		}

		return (null, null);
	}

	private void StateWidescreen()
	{
		if (_wide is null)
		{
			return;
		}

		foreach (Engine_UiWidescreen.Override over in _wide.Overrides)
		{
			int ordinal = over.ParentOrdinal;
			if (ordinal < 0 || ordinal >= _transforms.Length)
			{
				continue;
			}

			float?[] row = _transforms[ordinal] ??= new float?[Fable3HudStatements.TransformWidth];
			row[0] ??= over.X;
			row[1] ??= over.Y;
			row[3] ??= over.ScaleX;
		}
	}

	/// <summary>A texture the view asks for by hash, through the host's name table; a miss is named once.</summary>
	private Texture2D? ResolveTexture(Fable3Presentations host, uint hash)
	{
		// A mod's HUD icon (hud_icon) is a host texture's key, not a bank name.
		if (Engine_UiHostTextures.TryGet(hash) is { } stated)
		{
			return stated;
		}

		if (host.TextureNames is { } names && names.TryResolve(hash, out string path))
		{
			if (host.LoadTexture(path) is { } texture)
			{
				return texture;
			}

			if (_missed.Add(hash))
			{
				GD.Print($"[fable3-hud] '{Document}' texture {hash:X8} '{path}' is named but did not load");
			}

			return null;
		}

		if (_missed.Add(hash))
		{
			GD.Print($"[fable3-hud] '{Document}' texture {hash:X8} is not in the banks' name table");
		}

		return null;
	}

	/// <summary>Per ordinal, the element's dotted path from beneath the root (the root's is empty).</summary>
	internal static string[] PathsOf(Engine_UiScreen screen)
	{
		var paths = new string[screen.Nodes.Length];
		for (int i = 0; i < screen.Nodes.Length; ++i)
		{
			UiNode node = screen.Nodes[i];
			paths[i] = node.Parent is { } parent && parent.Ordinal >= 0 && parent.Ordinal < i && paths[parent.Ordinal] is { } above
				? (above.Length == 0 ? node.Name : above + "." + node.Name)
				: string.Empty;
		}

		return paths;
	}

	/// <summary>Every element that owns numbered slides.</summary>
	internal static List<int> SlideOwners(Engine_BgfDocument document)
	{
		var owners = new HashSet<int>();
		foreach (BgfSlide slide in document.Slides)
		{
			if (!slide.IsMaster)
			{
				owners.Add(slide.OwnerElement);
			}
		}

		return new List<int>(owners);
	}
}
