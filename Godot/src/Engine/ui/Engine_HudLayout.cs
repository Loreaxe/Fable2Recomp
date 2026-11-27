using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// Where the rebuilt HUD's pieces go, and which art each one wears. A piece is a NAMED record —
/// movable by editing one entry — whose art resolves by id through an override table before it
/// reaches the banks.
///
/// A piece hangs from a fraction of the screen and is then offset in reference units, and only the
/// OFFSET is scaled: that is what keeps a corner piece in its corner at an aspect the game never
/// shipped for. The guest has exactly two layouts, 4:3 and 16:9, carried as attributes beside each
/// element (<see cref="Engine_UiWidescreen"/>), and takes the closer of the two for anything else.
/// </summary>
public sealed partial class Engine_HudLayout
{
    /// <summary>Where the edited arrangement lives — the editor writes it, every boot reads it.</summary>
    public const string LayoutPath = "user://hud/layout.json";

    /// <summary>
    /// The arrangement BENEATH the player's file — shipped, then the enabled mods' layout in load
    /// order — written every boot for the player's layout tool and for modders. Its
    /// <see cref="SnapshotLayerKey"/> is <see cref="SnapshotLayer"/>.
    /// </summary>
    public const string SnapshotPath = "user://hud/layout.snapshot.json";

    /// <summary>
    /// Where a title's HUD files live: its arrangement, its snapshot and its piece-art overrides.
    /// Fable 2 keeps the unscoped `user://hud` its players' saved arrangements are already in;
    /// every later title gets its own folder beneath it, by the title's folder name — the same
    /// per-title split as `assets/&lt;Folder&gt;` and `cache/&lt;Folder&gt;` — so one title's
    /// snapshot never overwrites another's.
    /// </summary>
    public static string FolderFor(Refii.Core.Engine_Title title) =>
        title == Refii.Core.Engine_Title.Fable2 ? "user://hud" : $"user://hud/{title.Folder}";

    /// <summary>A title's player arrangement (<see cref="LayoutPath"/> for Fable 2).</summary>
    public static string LayoutPathFor(Refii.Core.Engine_Title title) => FolderFor(title) + "/layout.json";

    /// <summary>A title's snapshot of the arrangement beneath the player's (<see cref="SnapshotPath"/> for Fable 2).</summary>
    public static string SnapshotPathFor(Refii.Core.Engine_Title title) => FolderFor(title) + "/layout.snapshot.json";

    /// <summary>The running title's player arrangement — what the editor and the player's tool write.</summary>
    public static string ActiveLayoutPath => LayoutPathFor(Refii.Core.Engine_Title.Active);

    /// <summary>The running title's snapshot.</summary>
    public static string ActiveSnapshotPath => SnapshotPathFor(Refii.Core.Engine_Title.Active);

    /// <summary>The snapshot's top-level field naming which arrangement it holds.</summary>
    public const string SnapshotLayerKey = "layer";

    /// <summary>
    /// <see cref="SnapshotLayerKey"/>'s value for a snapshot written beneath the player's file. A
    /// snapshot without it was written over the player's file, by a build before this one.
    /// </summary>
    public const string SnapshotLayer = "shipped+mods";

    /// <summary>
    /// The design height every offset and size is authored against — the CANVAS height, which must
    /// equal the design height. The camera draws 720 canvas units across the window height whatever
    /// the pixel count, so this only rescales pieces when the visible extent grows past the design
    /// (a narrower-than-16:9 window revealing more height).
    /// </summary>
    public const float ReferenceHeight = 720f;

    /// <summary>A fraction of the screen a piece is attached to: (0,0) top-left, (1,1) bottom-right.</summary>
    public readonly record struct Anchor(float X, float Y)
    {
        public static readonly Anchor TopLeft = new(0f, 0f);
        public static readonly Anchor TopCentre = new(0.5f, 0f);
        public static readonly Anchor TopRight = new(1f, 0f);
        public static readonly Anchor Centre = new(0.5f, 0.5f);
        public static readonly Anchor BottomLeft = new(0f, 1f);
        public static readonly Anchor BottomCentre = new(0.5f, 1f);
        public static readonly Anchor BottomRight = new(1f, 1f);
    }

    /// <summary>How a piece's value drives what is drawn.</summary>
    public enum Drive
    {
        /// <summary>Static art. No value; it is either shown or it is not.</summary>
        None,

        /// <summary>A bar. The meter scales it along X, from its left edge.</summary>
        Bar,

        /// <summary>A text field, taking the guest's published string.</summary>
        Text,

        /// <summary>
        /// A pulser: the guest loops the element's authored timeline (`sub_822C21E0`,
        /// `time = phase × 1000`) and publishes the phase on registry 4; the piece scrubs its
        /// tracks at that tick.
        /// </summary>
        Pulse,
    }

    /// <summary>One placed piece of the HUD.</summary>
    /// <param name="Id">
    /// The piece's stable name, what a layout edit and a mod override address it by. Not the `.bgf`
    /// element name: the art behind a piece can be replaced.
    /// </param>
    /// <param name="Art">
    /// The FALLBACK art, as an element name in `hud.bgf`; <see cref="Engine_HudArt"/> consults the
    /// override table first.
    /// </param>
    /// <param name="Anchor">Which fraction of the screen the piece hangs from.</param>
    /// <param name="Offset">
    /// Where it sits relative to that anchor, in reference units, +X right and +Y DOWN; the flip
    /// into the canvas's +Y-up space happens once, in <see cref="Place"/>.
    /// </param>
    /// <param name="Value">The meter or text name that drives it, or empty for static art.</param>
    /// <param name="Driven">What that value does to it.</param>
    /// <param name="ElementOrdinal">
    /// The `hud.bgf` element this piece was derived from, or -1 for a hand-authored piece. It is
    /// the piece's STATE gate: the guest's own active bit on that element says whether the thing
    /// exists right now (the wheel is closed, the ABXY prompt is not offered, the pulser is dark).
    /// </param>
    /// <param name="Placed">
    /// Whether this piece has a place on screen yet. The file states what a piece IS exactly, but
    /// not where most of them sit — two dozen are authored at their component's origin because a
    /// slide, an animation or the guest supplies the position later, and snapping those lands them
    /// all at dead centre. An unplaced piece is real, carries its value and appears in the snapshot
    /// and the editor; it is simply not drawn until somebody says where it goes.
    /// </param>
    public readonly record struct Piece(
        string Id,
        string Art,
        Anchor Anchor,
        Vector2 Offset,
        string Value = "",
        Drive Driven = Drive.None,
        int ElementOrdinal = -1,
        bool Placed = true,
        IReadOnlySet<string>? Strip = null,
        float Depth = 0f,
        bool LiveText = false,
        Basis Ancestor = default);

    /// <summary>The quest/info box, `Layer/TopBox` in `hud.bgf`.</summary>
    public const string QuestBoxName = "TopBox";

    /// <summary>
    /// One widget cut WHOLE (a `group.` piece): the component <paramref name="Name"/> directly
    /// under <paramref name="Parent"/> — null takes Fable 2's two root containers, `Layer` and
    /// `UpperElements` — with <paramref name="Label"/> in its id. A fixed <paramref name="Anchor"/>
    /// wins; else <paramref name="AnchorFromContents"/> snaps from where the subtree's art sits
    /// (the mean of its drawn elements), else from the component's own authored position.
    /// </summary>
    /// <remarks>
    /// The contents rule is for a file whose widgets are authored at the canvas origin with their
    /// art placed beneath (Fable 3's `hud.bgf`: `TopBoxRefined`, `Subtitles`, `dpad` all sit at or
    /// near 0,0), where snapping from the root's own position puts every widget on the centre
    /// anchor and nothing reaches the edges of a wider display.
    /// <para><paramref name="AnyNode"/> cuts an element of any type by that name, not only a
    /// component: Fable 3's box screens hold their frame art (`BlackBox`, `TopLine`, `BottomLine`) in
    /// a plain node (`BedMenu.Medium2`, `StatsBox.Large2`), cut under its named parent.</para>
    /// </remarks>
    public readonly record struct GroupSubtree(
        string Name, string Label, Anchor? Anchor, float Depth,
        bool AnchorFromContents = false, string? Parent = null, bool AnyNode = false);

    /// <summary>
    /// The active title's HUD rules (<see cref="Refii.Core.Engine_TitleGame.HudLayout"/>), which
    /// the seat rules below answer through. Asked per piece per frame, so resolved once per
    /// active title rather than through the title registry on every call.
    /// </summary>
    private static Engine_HudTitleLayout Rules
    {
        get
        {
            Refii.Core.Engine_Title title = Refii.Core.Engine_Title.Active;
            if (_rules is { } known && known.Title == title)
            {
                return known.Layout;
            }

            var resolved = new TitleRules(title, Refii.Core.Engine_TitleGame.For(title).HudLayout);
            _rules = resolved;
            return resolved.Layout;
        }
    }

    private sealed record TitleRules(Refii.Core.Engine_Title Title, Engine_HudTitleLayout Layout);

    private static TitleRules? _rules;

    /// <summary>
    /// The document a caller that states no groups is cut from (<see cref="FromBindings"/>,
    /// <see cref="Shipped"/>): Fable 2's `hud.bgf`, as that title states it. The game's Fable 2 HUD,
    /// the Studio's HUD preview and the meter lab state none, whichever title is active.
    /// </summary>
    private static Engine_HudTitleLayout Unstated =>
        Refii.Core.Engine_TitleGame.For(Refii.Core.Engine_Title.Fable2).HudLayout;

    /// <summary>Whose piece this is on a screen both players share (<see cref="Engine_HudTitleLayout.OwnerOf"/>).</summary>
    public static Engine_UiSeats.Seat OwnerOf(Piece piece) => Rules.OwnerOf(piece);

    /// <summary>A screen's piece each half draws from its own player's values on split screen.</summary>
    public static bool EachPlayerOnSplit(Piece piece) => Rules.EachPlayerOnSplit(piece);

    /// <summary>A piece of the d-pad.</summary>
    public static bool IsDpad(Piece piece) => Rules.IsDpad(piece);

    /// <summary>The expression wheel, which stands in its owner's half on split screen.</summary>
    public static bool IsExpressionWheel(Piece piece) => Rules.IsExpressionWheel(piece);

    /// <summary>A screen's piece both halves show from the one shared feed on split screen.</summary>
    public static bool SharedOnSplit(Piece piece) => Rules.SharedOnSplit(piece);

    /// <summary>A piece as a seated half-view places it (<see cref="Engine_HudTitleLayout.SeatedPiece"/>).</summary>
    public static Piece SeatedPiece(Piece piece) => Rules.SeatedPiece(piece);

    /// <summary>The timing meter, which belongs to whichever player armed it.</summary>
    public static bool IsExpressionMeter(Piece piece) => Rules.IsExpressionMeter(piece);

    /// <summary>Whether a seated view clips to its half (<see cref="Engine_HudTitleLayout.KeepsToSeat"/>).</summary>
    public static bool KeepsToSeat => Rules.KeepsToSeat;

    private readonly List<Piece> _pieces = new();

    public IReadOnlyList<Piece> Pieces => _pieces;

    /// <summary>
    /// The id of the piece that states player two's version of player one's piece
    /// <paramref name="id"/>, or null — the active title's rule
    /// (<see cref="Engine_HudTitleLayout.PlayerTwoSourceId"/>). A view drawn for player two draws
    /// player one's piece and takes its state and values from this one, which is not drawn there.
    /// </summary>
    public static string? PlayerTwoSourceId(string id) => Rules.PlayerTwoSourceId(id);

    /// <summary>The id of player one's piece that player two's source <paramref name="id"/> keys, or null.</summary>
    public static string? PlayerOneIdOf(string id) => Rules.PlayerOneIdOf(id);

    /// <summary>The piece <see cref="PlayerTwoSourceId"/> names, or null.</summary>
    public Piece? PlayerTwoSourceOf(Piece piece)
    {
        if (PlayerTwoSourceId(piece.Id) is not { } id)
        {
            return null;
        }

        foreach (Piece candidate in _pieces)
        {
            if (candidate.Id == id)
            {
                return candidate;
            }
        }

        return null;
    }

    public Engine_HudLayout Add(Piece piece)
    {
        _pieces.Add(piece);
        return this;
    }

    /// <summary>Drops every piece <paramref name="keep"/> refuses; how many went.</summary>
    public int KeepOnly(Predicate<Piece> keep) => _pieces.RemoveAll(piece => !keep(piece));

    /// <summary>
    /// Where a piece lands in the canvas's own space, for a given viewport. The canvas is
    /// centre-origin with +Y up; a layout is authored top-left with +Y down, and the flip is here.
    /// </summary>
    public static Vector2 Place(Piece piece, Vector2 viewport)
    {
        float scale = Scale(viewport);

        // The anchor first, in screen pixels from the top-left...
        float x = (piece.Anchor.X * viewport.X) + (piece.Offset.X * scale);
        float y = (piece.Anchor.Y * viewport.Y) + (piece.Offset.Y * scale);

        // ...then into the canvas: centre-origin, +Y up.
        return new Vector2(x - (viewport.X * 0.5f), (viewport.Y * 0.5f) - y);
    }

    /// <summary>
    /// The scale a piece is drawn at — viewport height over the design height. Unclamped, unlike
    /// the achievement toast's: the HUD is the game's own furniture and tracks the display.
    /// </summary>
    public static float Scale(Vector2 viewport) =>
        viewport.Y <= 0f
            ? 1f
            // ONE canvas unit per design unit, the scale every other UI layer draws at: the
            // extent is already in canvas units (`CanvasExtent` divides by the camera's
            // px-per-unit), so the design height it is measured against is too. Capped at 1:
            // narrower than 4:3 the camera pins the WIDTH, and the extra canvas height is
            // slack, not a larger design box.
            : Mathf.Min(viewport.Y * Engine_UiSceneBuilder.PixelsPerUnit / ReferenceHeight, 1f);

    /// <summary>The canvas box the design is authored in — the one the canvas camera fits.</summary>
    public const float DesignWidth = 1280f;
    public const float DesignHeight = 720f;

    /// <summary>
    /// How much of the canvas is VISIBLE at a given window aspect, in canvas units — what anchors
    /// anchor to, and not the design box. `Engine_UiSceneBuilder.FitCanvasCamera` fits the whole
    /// 1280x720 box rather than cropping: wider than 16:9 pins the height and reveals extra WIDTH,
    /// narrower pins the width and reveals extra HEIGHT, so on an ultrawide the right edge is well
    /// past x=640.
    /// </summary>
    public static Vector2 CanvasExtent(Vector2 window)
    {
        if (window.X <= 0f || window.Y <= 0f)
        {
            return new Vector2(DesignWidth, DesignHeight);
        }

        // The same switch and the same scale the camera uses — a disagreement insets every
        // edge piece by the difference. Height is pinned down to the authored 4:3 box (960x720),
        // width below that, and both at the console's 1.00843 px per unit.
        float aspect = window.X / window.Y;
        Vector2 extent = aspect >= Engine_UiSceneBuilder.MinimumWidth / DesignHeight
            ? new Vector2(DesignHeight * aspect, DesignHeight)
            : new Vector2(Engine_UiSceneBuilder.MinimumWidth,
                Engine_UiSceneBuilder.MinimumWidth / aspect);
        return extent / Engine_UiSceneBuilder.PixelsPerUnit;
    }

    /// <summary>
    /// Derives a whole layout from `hud.bgf`'s OWN bindings — the file binds 52 meters and 18 text
    /// fields and states, per element, which value it answers to and where the artist put it.
    /// The ANCHOR is what this adds: each element's authored canvas position becomes a screen
    /// fraction, that fraction snaps to the nearest edge or centre, and the remainder is the
    /// offset. The result is the starting arrangement and is meant to be edited.
    /// </summary>
    /// <param name="groups">
    /// The widgets the title cuts whole. Null is the unstated document (<see cref="Unstated"/>,
    /// Fable 2's `hud.bgf`): its group table, its shipped arrangement
    /// (<see cref="Engine_HudTitleLayout.Arrangement"/>) and, where it adds them, its own-reader
    /// widgets (<see cref="AddWidgets"/>). A title that states its groups states its whole
    /// arrangement: no placement or widget of another document is added.
    /// </param>
    public static Engine_HudLayout FromBindings(
        Engine_UiScreen screen, Engine_UiBindings bindings, Engine_UiWidescreen? wide = null,
        IReadOnlyList<GroupSubtree>? groups = null)
    {
        var layout = new Engine_HudLayout();
        Engine_HudTitleLayout? unstated = groups is null ? Unstated : null;
        IReadOnlyDictionary<string, (Anchor Anchor, Vector2 Offset)> arrangement =
            unstated?.Arrangement ?? NoArrangement;

        // THE WIDE LAYOUT DECIDES THE ANCHORS. Every position in `hud.bgf` is the 4:3 one; the
        // 16:9 arrangement rides beside it as the parent's `widescreenXPos/YPos/WidescreenXWidth`
        // (`hud/widescreenadapter.lua`), and a piece anchored from the 4:3 position lands a
        // third of the way in from the edge it belongs on — the dpad at 280 px for its authored
        // -360 where the wide layout says -512. Substituted into the ancestor walk here, once,
        // for every piece the layout derives.
        _wide = null;
        if (wide is { Count: > 0 })
        {
            _wide = new Dictionary<int, Engine_UiWidescreen.Override>();
            foreach (Engine_UiWidescreen.Override over in wide.Overrides)
            {
                _wide[over.ParentOrdinal] = over;
            }
        }

        layout.Wide = _wide;

        // Which bound elements are ANCESTORS of another bound element.
        var bound = new HashSet<int>();
        foreach (Engine_UiBindings.Binding b in bindings.All)
        {
            bound.Add(b.ElementOrdinal);
        }

        // Containment, and what each container holds. A piece's art is its element and everything
        // under it, so a bound group holding another bound piece would draw that piece twice —
        // once live, once as the authored placeholder inside the group's clone. The group is kept
        // and the DUPLICATE removed: each container carries the names of the bound elements
        // beneath it, and the clone drops exactly those subtrees.
        var contains = new Dictionary<int, HashSet<string>>();
        foreach (Engine_UiBindings.Binding b in bindings.All)
        {
            if (b.ElementOrdinal < 0 || b.ElementOrdinal >= screen.Nodes.Length)
            {
                continue;
            }

            string name = screen.Nodes[b.ElementOrdinal].Name;
            for (UiNode? up = screen.Nodes[b.ElementOrdinal].Parent; up is not null; up = up.Parent)
            {
                if (!bound.Contains(up.Ordinal))
                {
                    continue;
                }

                if (!contains.TryGetValue(up.Ordinal, out HashSet<string>? held))
                {
                    held = new HashSet<string>(StringComparer.Ordinal);
                    contains[up.Ordinal] = held;
                }

                held.Add(name);
            }
        }

        // One anchor per SIBLING GROUP, decided before any piece is made. Anchoring per piece
        // splits any widget that straddles `Nearest`'s 0.25/0.75 thresholds — no threshold
        // survives that — and the file states the grouping: siblings under one parent are one
        // widget. Each member then keeps its own offset, which carries the authored arrangement.
        //
        // From the members' MEAN, not the parent's own position: a parent at the origin with all
        // the placement on its children is ordinary here, and would anchor a corner widget to the
        // middle of the screen.
        var groupSum = new Dictionary<int, Vector2>();
        var groupCount = new Dictionary<int, int>();
        foreach (Engine_UiBindings.Binding binding in bindings.All)
        {
            if (binding.ElementOrdinal < 0 || binding.ElementOrdinal >= screen.Nodes.Length ||
                Owned(screen, binding.ElementOrdinal))
            {
                continue;
            }

            UiNode at = screen.Nodes[binding.ElementOrdinal];
            int group = at.Parent?.Ordinal ?? -1;
            Vector2 where = CanvasPositionWide(at);

            groupSum[group] = groupSum.TryGetValue(group, out Vector2 sum) ? sum + where : where;
            groupCount[group] = groupCount.TryGetValue(group, out int seen) ? seen + 1 : 1;
        }

        var groupAnchor = new Dictionary<int, Anchor>();
        foreach ((int group, Vector2 sum) in groupSum)
        {
            groupAnchor[group] = SnapAnchor(sum / groupCount[group]);
        }

        // Every meter-bound ordinal, gathered before anything is classified, because a fill
        // belongs to the NEAREST meter binding above it rather than to any binding above it.
        var meterBound = new HashSet<int>();
        foreach (Engine_UiBindings.Binding binding in bindings.All)
        {
            if (binding.Kind == Engine_UiBindings.Kind.Meter)
            {
                meterBound.Add(binding.ElementOrdinal);
            }
        }

        foreach (Engine_UiBindings.Binding binding in bindings.All)
        {
            Drive driven = binding.Kind switch
            {
                // NOT EVERY METER IS A BAR. `TopBoxY`, `CornerY` and `SubtitlesX` are LAYOUT
                // meters: they move whole components rather than filling anything, and the
                // rule for telling them apart is OWNERSHIP, not a name list —
                // `Engine_UiLiveValues` has carried it for a while and records what ignoring
                // it costs ("driving whatever quad happens to sit under them is how a meter at
                // 50% drew the heart icon at half size"). This file never got the rule, which
                // is a second implementation of one concern.
                //
                // It matters most now that the health readout is cut whole: `TopBoxY` is bound
                // to `HealthAndMana`, the PARENT of the health bar, and its tracks move
                // `Health`'s `position.y` 0 -> -136. Driven as a bar it drags the entire group
                // — orb, heart, fill and cap — off the top-left of the screen.
                Engine_UiBindings.Kind.Meter =>
                    OwnsFill(screen, binding.ElementOrdinal, meterBound) ? Drive.Bar : Drive.None,
                Engine_UiBindings.Kind.Text => Drive.Text,
                Engine_UiBindings.Kind.Pulser => Drive.Pulse,

                // Slide toggles drive an element's STATE rather than a value and a rebuilt piece
                // has no slides, so they are carried as static art rather than dropped.
                _ => Drive.None,
            };

            if (binding.ElementOrdinal < 0 || binding.ElementOrdinal >= screen.Nodes.Length)
            {
                continue;
            }

            UiNode node = screen.Nodes[binding.ElementOrdinal];

            // A binding inside a widget Godot already draws is not a HUD piece:
            // Engine_UiSpellAssign draws the assignment carousel whole and only while the wheel
            // is open.
            if (Owned(screen, binding.ElementOrdinal))
            {
                continue;
            }

            Vector2 canvas = CanvasPositionWide(node);
            Anchor anchor = groupAnchor.TryGetValue(node.Parent?.Ordinal ?? -1, out Anchor shared)
                ? shared
                : SnapAnchor(canvas);
            Vector2 offset = OffsetFrom(anchor, canvas);

            string id =
                $"{binding.Kind.ToString().ToLowerInvariant()}.{binding.Name}.{binding.ElementName}";

            // Placed by default; the one exclusion is containment. Where a group and a child are
            // both bound the CHILD wins — it is the part the guest drives — and the group's clone
            // drops that subtree.
            contains.TryGetValue(binding.ElementOrdinal, out HashSet<string>? strip);
            const bool placed = true;
            if (arrangement.TryGetValue(id, out (Anchor Anchor, Vector2 Offset) put))
            {
                anchor = put.Anchor;
                offset = put.Offset;
            }

            layout.Add(new Piece(
                Id: id,
                Art: binding.ElementName,
                Anchor: anchor,
                Offset: offset,
                Value: binding.Name,
                Driven: driven,
                ElementOrdinal: binding.ElementOrdinal,
                Strip: strip,
                Ancestor: AncestorBasisOf(screen, binding.ElementOrdinal),

                // A container sits BEHIND what it holds. Every piece is placed at z 0, and canvas
                // +Z faces the camera, so a container steps back by how much it holds — more
                // bound elements beneath it means higher in the tree and further back.
                Depth: strip is null ? 0f : -0.05f * strip.Count,
                Placed: placed));
        }

        // Group pieces — widgets cut WHOLE, with no binding of their own, following the guest's
        // live state for every element beneath them through the clone binding; their nested meters
        // and pulsers scrub their own element's tracks (Engine_HudView).
        //
        //   `Layer/TopBox` — the quest/info box. Its rows (title, body, button row) are placed by
        //   Engine_UiTopBoxLayout from the text Godot draws; the guest's own fit (`sub_823FC8F0`,
        //   TopBoxX/TopBoxY via `sub_823FE638`) places nothing on a headless element and measures
        //   in its own font. Its faders are the guest's stated opacity.
        foreach ((string name, string label, Anchor? fixedAnchor, float depth, bool fromContents, string? parent, bool anyNode)
            in groups ?? unstated!.Groups)
        {
            for (int i = 0; i < screen.Nodes.Length; ++i)
            {
                UiNode node = screen.Nodes[i];
                // `UpperElements` COUNTS AS A ROOT CONTAINER. Every group here used to sit
                // directly under `Layer`, so the check named it alone; the health readout sits
                // one level further down, under `Layer/UpperElements`, which is the same kind of
                // bare container and not a widget of its own. Restricting to `Layer` silently
                // matched nothing for it — a group that never forms leaves the per-binding
                // pieces standing, which looks like the layout working.
                if ((!anyNode && node.Type != BgfElementType.Component) || node.Name != name ||
                    (parent is null
                        ? node.Parent?.Name is not ("Layer" or "UpperElements")
                        : node.Parent?.Name != parent))
                {
                    continue;
                }

                Vector2 canvas = CanvasPositionWide(node);
                Anchor anchor = fixedAnchor ??
                    (fromContents && ContentsCentre(node) is { } contents ? SnapAnchor(contents) : SnapAnchor(canvas));
                string id = $"group.{label}.{name}";
                Vector2 offset = OffsetFrom(anchor, canvas);
                if (arrangement.TryGetValue(id, out (Anchor Anchor, Vector2 Offset) put))
                {
                    anchor = put.Anchor;
                    offset = put.Offset;
                }

                layout.Add(new Piece(
                    Id: id,
                    Art: name,
                    Anchor: anchor,
                    Offset: offset,
                    Value: string.Empty,
                    Driven: Drive.None,
                    ElementOrdinal: node.Ordinal,
                    Strip: null,
                    Ancestor: AncestorBasisOf(screen, node.Ordinal),
                    Depth: depth,
                    Placed: true,
                    LiveText: true));
                break;
            }
        }

        // An arrangement entry that matches nothing is a renamed binding, and would otherwise be
        // silent — the piece just would not appear, which looks like the guest not lighting it.
        var stranded = new List<string>();
        foreach (string id in arrangement.Keys)
        {
            if (!layout._pieces.Exists(p => p.Id == id))
            {
                stranded.Add(id);
            }
        }

        if (stranded.Count != 0)
        {
            GD.Print($"[hud-layout] the shipped arrangement names {stranded.Count} piece(s) " +
                $"hud.bgf does not bind: {string.Join(", ", stranded)}");
        }

        AbsorbIntoGroups(screen, layout._pieces);
        ReportNesting(screen, layout._pieces);
        if (unstated is { AddsWidgets: true })
        {
            layout.AddWidgets();
        }

        return layout;
    }

    /// <summary>No shipped placements: a title that states its own groups.</summary>
    private static readonly Dictionary<string, (Anchor Anchor, Vector2 Offset)> NoArrangement = new();

    /// <summary>
    /// The mean canvas position (wide layout applied) of the elements under <paramref name="root"/>
    /// that draw something — a quad or a text — or null when none does.
    /// </summary>
    private static Vector2? ContentsCentre(UiNode root)
    {
        Vector2 sum = Vector2.Zero;
        int count = 0;
        var pending = new Stack<UiNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            UiNode at = pending.Pop();
            if (at.Quad is not null || at.Text is not null)
            {
                sum += CanvasPositionWide(at);
                ++count;
            }

            foreach (UiNode child in at.Children)
            {
                pending.Push(child);
            }
        }

        return count == 0 ? null : sum / count;
    }

    /// <summary>
    /// The widgets drawn by their own readers (<see cref="Engine_HudWidgets"/>) as pieces at their
    /// homes, so a layout entry, the snapshot, the player's tool and the Studio address them as
    /// they address a cut piece. Never built by <see cref="Engine_HudView"/>: their readers draw them.
    /// </summary>
    public Engine_HudLayout AddWidgets()
    {
        foreach (Engine_HudWidgets.Widget widget in Engine_HudWidgets.All)
        {
            if (!_pieces.Exists(p => p.Id == widget.Id))
            {
                _pieces.Add(new Piece(widget.Id, widget.Name, widget.Home, Vector2.Zero));
            }
        }

        return this;
    }

    /// <summary>A piece by id, or null.</summary>
    public Piece? Find(string id)
    {
        foreach (Piece piece in _pieces)
        {
            if (piece.Id == id)
            {
                return piece;
            }
        }

        return null;
    }

    /// <summary>
    /// Widgets inside `hud.bgf` that a Godot-owned view draws whole, by element name — the title's
    /// (Fable 2: both players' copies of the will assigner, which share one controller as the
    /// guest's do).
    /// </summary>
    private static System.Collections.Generic.IReadOnlyCollection<string> OwnedSubtrees =>
        Refii.Core.Engine_TitleGame.Active.HudOwnedSubtrees;

    /// <summary>
    /// Whether a meter binding owns a fill of its own — the test that separates a BAR from a
    /// LAYOUT meter, ported from <c>Engine_UiLiveValues.FindFill</c> rather than reinvented.
    ///
    /// A fill is a `BarMiddle` quad whose NEAREST meter-bound ancestor is this element. The
    /// nearness is the whole point: `TopBoxY` is bound to `HealthAndMana`, the grandparent of
    /// the health bar, and a plain descendant search from there cheerfully claims the health
    /// bar's own fill — two meters writing one node, last one wins.
    /// </summary>
    private static bool OwnsFill(Engine_UiScreen screen, int root, HashSet<int> meterBound)
    {
        for (int ordinal = 0; ordinal < screen.Nodes.Length; ++ordinal)
        {
            if (!string.Equals(screen.Nodes[ordinal].Name, Engine_UiBindings.FillName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (NearestMeter(screen, ordinal, meterBound) == root)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The nearest meter-bound element at or above <paramref name="ordinal"/>, or -1.</summary>
    private static int NearestMeter(Engine_UiScreen screen, int ordinal, HashSet<int> meterBound)
    {
        // Bounded rather than trusted to terminate: a parent chain is data from a file, and a
        // cycle would hang the build rather than draw something wrong.
        UiNode? at = ordinal >= 0 && ordinal < screen.Nodes.Length ? screen.Nodes[ordinal] : null;
        for (int steps = 0; at is not null && steps <= screen.Nodes.Length; at = at.Parent, ++steps)
        {
            if (meterBound.Contains(at.Ordinal))
            {
                return at.Ordinal;
            }
        }

        return -1;
    }

    /// <summary>Whether an element sits inside — or is — a subtree another view owns.</summary>
    private static bool Owned(Engine_UiScreen screen, int ordinal)
    {
        if (ordinal < 0 || ordinal >= screen.Nodes.Length)
        {
            return false;
        }

        for (UiNode? at = screen.Nodes[ordinal]; at is not null; at = at.Parent)
        {
            if (System.Linq.Enumerable.Contains(OwnedSubtrees, at.Name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reports any two PLACED pieces where one element sits inside the other: nesting is invisible
    /// in the id and its symptom, a widget drawn twice slightly apart, reads as a layout problem.
    /// </summary>
    private static void ReportNesting(Engine_UiScreen screen, List<Piece> pieces)
    {
        var clashes = new List<string>();
        foreach (Piece piece in pieces)
        {
            if (!piece.Placed || piece.ElementOrdinal < 0)
            {
                continue;
            }

            foreach (Piece other in pieces)
            {
                if (!other.Placed || other.ElementOrdinal < 0 ||
                    other.ElementOrdinal == piece.ElementOrdinal)
                {
                    continue;
                }

                // Only one direction is reported, so a clash names each pair once.
                if (!IsAncestor(screen, other.ElementOrdinal, piece.ElementOrdinal))
                {
                    continue;
                }

                // Nesting is not a clash when the container stripped it: a group keeps its own art
                // and drops the subtrees of the children drawn live on top of it.
                if (other.Strip?.Contains(screen.Nodes[piece.ElementOrdinal].Name) == true)
                {
                    continue;
                }

                clashes.Add($"{other.Id} contains {piece.Id}");
            }
        }

        if (clashes.Count != 0)
        {
            GD.PushWarning($"[hud-layout] {clashes.Count} placed piece(s) NEST and will draw " +
                $"twice: {string.Join("; ", clashes)}");
        }
    }

    /// <summary>
    /// A group owns its subtree: it is placed whole and follows the guest's slides and opacity for
    /// every element beneath it through the clone binding, so a piece derived for one of its
    /// children is a second COPY — and one that never sees its parent's state. Dropped here.
    /// </summary>
    private static void AbsorbIntoGroups(Engine_UiScreen screen, List<Piece> pieces)
    {
        var absorbed = new List<string>();
        foreach (Piece group in pieces.ToArray())
        {
            if (!group.Id.StartsWith("group.", StringComparison.Ordinal) || group.ElementOrdinal < 0)
            {
                continue;
            }

            pieces.RemoveAll(piece =>
            {
                // THE GROUP'S OWN ELEMENT COUNTS, not just its descendants. `IsAncestor` starts
                // at the parent, so a binding on the group ROOT was never absorbed: the health
                // readout's root carries `SlideToggleName "TopBox"`, so
                // `slidetoggle.TopBox.PlayerHealth` and `group.PlayerHealth.PlayerHealth` are
                // the SAME element placed twice, at two different anchors — one snapped to the
                // top-left, one to top-centre with a -512 offset. They coincide at one window
                // width and drift apart at every other.
                // IDENTITY BY ID, NOT BY REFERENCE. `Piece` is a record STRUCT, so
                // `ReferenceEquals(piece, group)` boxes both operands and is ALWAYS FALSE —
                // the compiler says so (CA2013) and it was ignored. While the only test below
                // was `IsAncestor`, that latent bug could not bite: nothing is its own
                // ancestor. Adding the same-element test made it fatal, and every group
                // matched itself and was removed — the whole HUD vanished, 96 pieces down to
                // 84. Ids are unique here, so they are the identity.
                bool inside = piece.Id != group.Id && piece.ElementOrdinal >= 0 &&
                    (piece.ElementOrdinal == group.ElementOrdinal ||
                     IsAncestor(screen, group.ElementOrdinal, piece.ElementOrdinal));
                if (inside)
                {
                    absorbed.Add($"{piece.Id} into {group.Id}");
                }

                return inside;
            });
        }

        if (absorbed.Count != 0)
        {
            GD.Print($"[hud-layout] {absorbed.Count} piece(s) absorbed by the group that owns " +
                $"their subtree: {string.Join("; ", absorbed)}");
        }
    }

    /// <summary>
    /// The basis the ancestors ABOVE a piece's element compose onto it — scale (`HealthAndMana`
    /// is authored at 0.6) AND rotation (`SpellCharging` at -1.5708); the clone keeps only the
    /// element's own basis, so the layout carries the rest.
    /// </summary>
    public static Basis AncestorBasisOf(Engine_UiScreen screen, int element)
    {
        var basis = Basis.Identity;
        if (element < 0 || element >= screen.Nodes.Length)
        {
            return basis;
        }

        for (UiNode? up = screen.Nodes[element].Parent; up is not null; up = up.Parent)
        {
            basis = LocalBasis(up, zeroScaleIsOne: true) * basis;
        }

        return basis;
    }

    /// <summary>
    /// One element's rotation and scale as `Engine_UiSceneBuilder.BuildTransform` composes them,
    /// with the wide layout's x-scale where it states one.
    /// </summary>
    private static Basis LocalBasis(UiNode node, bool zeroScaleIsOne)
    {
        float sx = node.ScaleX;
        if (_wide is not null && _wide.TryGetValue(node.Ordinal, out Engine_UiWidescreen.Override over) &&
            over.ScaleX is { } wideX)
        {
            sx = wideX;
        }

        float sy = node.ScaleY;
        if (zeroScaleIsOne)
        {
            sx = sx == 0f ? 1f : sx;
            sy = sy == 0f ? 1f : sy;
        }

        var basis = Basis.Identity;
        if (node.RotationX != 0f || node.RotationY != 0f || node.RotationZ != 0f)
        {
            basis = Basis.FromEuler(new Vector3(-node.RotationX, -node.RotationY, node.RotationZ));
        }

        return basis.Scaled(new Vector3(sx, sy, 1f));
    }

    /// <summary>The wide layout's overrides by parent ordinal, for the build in progress and the layout it made.</summary>
    [System.ThreadStatic]
    private static Dictionary<int, Engine_UiWidescreen.Override>? _wide;

    public IReadOnlyDictionary<int, Engine_UiWidescreen.Override>? Wide { get; private set; }

    /// <summary>
    /// <see cref="Engine_UiElementResolve.CanvasPosition"/> with each ancestor's position and
    /// x-scale taken from the wide layout where it states one — absolute values, per attribute,
    /// exactly as `widescreenadapter.lua` writes them onto the parent. Each ancestor's rotation
    /// applies too (the spell-charge column is rot.z -1.5708).
    /// </summary>
    private static Vector2 CanvasPositionWide(UiNode node)
    {
        Vector2 at = Local(node);
        for (UiNode? up = node.Parent; up is not null; up = up.Parent)
        {
            Vector3 turned = LocalBasis(up, zeroScaleIsOne: false) * new Vector3(at.X, at.Y, 0f);
            Vector2 local = Local(up);
            at = new Vector2(local.X + turned.X, local.Y + turned.Y);
        }

        return at;

        static Vector2 Local(UiNode n)
        {
            var local = new Vector2(n.PositionX, n.PositionY);
            if (_wide is not null && _wide.TryGetValue(n.Ordinal, out Engine_UiWidescreen.Override over))
            {
                local = new Vector2(over.X ?? local.X, over.Y ?? local.Y);
            }

            return local;
        }
    }

    /// <summary>Whether <paramref name="ancestor"/> is at or above <paramref name="element"/>.</summary>
    private static bool IsAncestor(Engine_UiScreen screen, int ancestor, int element)
    {
        if (element < 0 || element >= screen.Nodes.Length)
        {
            return false;
        }

        for (UiNode? at = screen.Nodes[element].Parent; at is not null; at = at.Parent)
        {
            if (at.Ordinal == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Where a canvas position sits as a screen fraction, top-left origin.</summary>
    private static Vector2 Fraction(Vector2 canvas) => new(
        (canvas.X + (DesignWidth * 0.5f)) / DesignWidth,
        ((DesignHeight * 0.5f) - canvas.Y) / DesignHeight);

    /// <summary>The nearest of the three anchors on one axis — 0, 0.5 or 1: an edge, or the middle.</summary>
    private static float Nearest(float fraction) =>
        fraction < 0.25f ? 0f : fraction > 0.75f ? 1f : 0.5f;

    /// <summary>The anchor a position on its own would choose.</summary>
    private static Anchor SnapAnchor(Vector2 canvas)
    {
        Vector2 f = Fraction(canvas);
        return new Anchor(Nearest(f.X), Nearest(f.Y));
    }

    /// <summary>
    /// Where a canvas position sits relative to a GIVEN anchor, in reference units — separate from
    /// choosing the anchor, because a widget's pieces share one anchor and keep their own places.
    /// </summary>
    private static Vector2 OffsetFrom(Anchor anchor, Vector2 canvas)
    {
        Vector2 f = Fraction(canvas);
        float scale = ReferenceHeight / DesignHeight;
        return new Vector2(
            (f.X - anchor.X) * DesignWidth * scale,
            (f.Y - anchor.Y) * DesignHeight * scale);
    }

    /// <summary>
    /// The layout a view holds before it is given one: the unstated document's
    /// (<see cref="Unstated"/>, <see cref="Engine_HudTitleLayout.Shipped"/>).
    /// </summary>
    public static Engine_HudLayout Shipped() => Unstated.Shipped();

    /// <summary>One line naming what a layout holds, for the run.</summary>
    public string Describe(Vector2 viewport) =>
        $"[hud-layout] {_pieces.Count} piece(s) at {viewport.X:F0}x{viewport.Y:F0} " +
        $"(scale {Scale(viewport):F2}, aspect {(viewport.Y <= 0f ? 0f : viewport.X / viewport.Y):F2})";

    /// <summary>
    /// Moves one piece and PLACES it — positioning a piece is the statement that it belongs on
    /// screen, so there is no second switch to throw. False when no piece has the id.
    /// </summary>
    public bool Reposition(string id, Anchor anchor, Vector2 offset)
    {
        for (int i = 0; i < _pieces.Count; ++i)
        {
            if (_pieces[i].Id == id)
            {
                _pieces[i] = _pieces[i] with
                {
                    Anchor = anchor, Offset = offset, Placed = true,
                };
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Applies a saved arrangement over this layout, by piece id. The file MOVES pieces, it does
    /// not define them: what a piece is — art, value, drive — is derived from `hud.bgf`'s bindings
    /// every boot. Read by <see cref="Engine_HudModLayout.ParsePlayerFile"/>, the one parser of the
    /// entry shape a mod's `layout` block shares. Returns how many pieces moved; 0 with no file is
    /// ordinary, not an error.
    /// </summary>
    public int ApplyOverrides(string path)
    {
        if (!FileAccess.FileExists(path))
        {
            return 0;
        }

        using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file is null)
        {
            return 0;
        }

        Dictionary<string, Engine_HudModLayout.Move> moves = Engine_HudModLayout.ParsePlayerFile(file.GetAsText());
        if (moves.Count == 0)
        {
            GD.PushWarning($"[hud-layout] '{path}' places no piece (a 'pieces' object of " +
                "{ anchor: [x, y], offset: [x, y] } entries); ignored");
            return 0;
        }

        return ApplyMoves(moves, $"'{path}'");
    }

    /// <summary>
    /// Every enabled mod's `ui/hud.json` `layout` block, composed in load order
    /// (<see cref="Engine_HudModLayout.Compose"/>) and applied. Called BEFORE
    /// <see cref="ApplyOverrides"/>: shipped &lt; mods &lt; the player's file. The pieces two mods
    /// both moved are in <see cref="ModLayoutConflicts"/>.
    /// </summary>
    public int ApplyModLayout()
    {
        Engine_HudModLayout.Composed composed = Engine_HudModLayout.Compose(
            Refii.Mods.ModEntries.ReadEnabledText(Engine_HudModPieces.ModHudPath));
        ModLayoutConflicts = composed.Conflicts;
        foreach (Engine_HudModLayout.Conflict conflict in composed.Conflicts)
        {
            GD.Print($"[hud-layout] '{conflict.Piece}': mod '{conflict.Winner}' moves it over mod '{conflict.Loser}'");
        }

        return composed.Moves.Count == 0 ? 0 : ApplyMoves(composed.Moves, "the mods' ui/hud.json layout");
    }

    /// <summary>The pieces two mods' `layout` blocks both moved, from the last <see cref="ApplyModLayout"/>.</summary>
    public IReadOnlyList<Engine_HudModLayout.Conflict> ModLayoutConflicts { get; private set; } =
        Array.Empty<Engine_HudModLayout.Conflict>();

    /// <summary>
    /// Moves every named piece. An id matching no piece is reported, since that is what a renamed
    /// binding looks like. Returns how many pieces moved.
    /// </summary>
    public int ApplyMoves(IReadOnlyDictionary<string, Engine_HudModLayout.Move> moves, string source)
    {
        int moved = 0;
        var unknown = new List<string>();
        foreach ((string id, Engine_HudModLayout.Move move) in moves)
        {
            if (Reposition(id, new Anchor(move.AnchorX, move.AnchorY), new Vector2(move.OffsetX, move.OffsetY)))
            {
                ++moved;
            }
            else
            {
                unknown.Add(id);
            }
        }

        if (unknown.Count != 0)
        {
            GD.Print($"[hud-layout] {source} names {unknown.Count} piece(s) this build " +
                $"does not have: {string.Join(", ", unknown)}");
        }

        return moved;
    }

    /// <summary>
    /// Writes the whole arrangement, every piece, so the file survives a rederive that changes
    /// which pieces exist. The art/value/driven fields are informational —
    /// <see cref="ApplyOverrides"/> never reads them — and are there for the editor and modders.
    /// </summary>
    /// <param name="layer">Written as <see cref="SnapshotLayerKey"/> when given: which arrangement the file holds.</param>
    public bool Save(string path, string? layer = null)
    {
        var pieces = new Godot.Collections.Dictionary();
        foreach (Piece piece in _pieces)
        {
            pieces[piece.Id] = new Godot.Collections.Dictionary
            {
                ["anchor"] = new Godot.Collections.Array { piece.Anchor.X, piece.Anchor.Y },
                ["offset"] = new Godot.Collections.Array { piece.Offset.X, piece.Offset.Y },
                ["art"] = piece.Art,
                ["value"] = piece.Value,
                ["driven"] = piece.Driven.ToString(),

                // Informational, like the three above: an anchor and an offset in this file are
                // what place a piece, so there is nothing to read back.
                ["placed"] = piece.Placed,
            };
        }

        var root = new Godot.Collections.Dictionary { ["pieces"] = pieces };
        if (layer is not null)
        {
            root[SnapshotLayerKey] = layer;
        }

        using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file is null)
        {
            GD.PushWarning($"[hud-layout] cannot write '{path}': {FileAccess.GetOpenError()}");
            return false;
        }

        file.StoreString(Json.Stringify(root, "\t"));
        GC.KeepAlive(root);
        return true;
    }
}
