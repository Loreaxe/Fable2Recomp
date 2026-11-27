using System.Collections.Generic;
using Godot;


namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The sixteen pooled world icons — quest markers, trader titles, region labels — drawn by
/// Godot from the guest's own per-frame statements.
///
/// The one part of the HUD the guest still arranges: an icon's place is the projection of a
/// world position, computed by `sub_824251E0` and written onto the element every frame. The
/// guest states position, content and which slots are lit; Godot owns only the drawing.
///
/// Not a subtree clone — an icon is one textured quad and one title, so this draws exactly that
/// and applies the stated position with the file's own pivot term, instead of running the
/// general live-transform pipeline.
///
/// Joined by ordinal: when the told statements' element count disagrees with the file's, they are
/// declined rather than misread.
/// </summary>
public sealed partial class Engine_HudWorldIcons : Node3D
{
	/// <summary>The shipped look — glyphs for the titles.</summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>Stated texture hash to a loaded texture; null when the hash resolves to nothing.</summary>
	public System.Func<uint, Texture2D?>? ResolveTexture { get; set; }

	/// <summary>
	/// The HUD's pattern book, for the slots whose art is a live widget rather than one quad:
	/// the hold icon's ring and button are cloned from it whole.
	/// </summary>
	public Engine_UiTheme? Art { get; set; }

	/// <summary>The guest's published HUD values — the hold icon's meter is one of them.</summary>
	public Engine_HudValues? Values { get; set; }

	/// <summary>
	/// Whose button glyphs the prompts draw (Engine_UiKeyGlyphs.Surface): 2 for player two's icons,
	/// which are theirs alone; 1, the shared set, for player one's.
	/// </summary>
	public int GlyphPlayer { get; set; } = 1;

	/// <summary>One pooled slot, resolved from the document once.</summary>
	private sealed class Slot
	{
		public required string Name { get; init; }
		public required int Ordinal { get; init; }

		/// <summary>The first quad-bearing descendant — the icon image itself.</summary>
		public int QuadOrdinal { get; init; } = -1;

		public UiQuad? Quad { get; init; }

		/// <summary>The quad's authored offset and scale within the component.</summary>
		public Vector2 QuadOffset { get; init; }

		public Vector2 QuadScale { get; init; } = Vector2.One;

		/// <summary>The `Contents.Title` text, when the slot has one.</summary>
		public int TitleOrdinal { get; init; } = -1;

		public UiText? Title { get; init; }

		public Vector2 TitleOffset { get; init; }

		/// <summary>
		/// The `Contents.Opinion` text — the amount line. Request types 36/37 write the value
		/// here and the name on `Title`; authored 8 units below the title.
		/// </summary>
		public int OpinionOrdinal { get; init; } = -1;

		/// <summary>The `Contents` child — the element the guest actually shows and hides.</summary>
		public int LitOrdinal { get; init; }

		public UiText? Opinion { get; init; }

		public Vector2 OpinionOffset { get; init; }

		/// <summary>The component's own pivot and scale — the file's terms in the origin.</summary>
		public Vector2 Pivot { get; init; }

		public Vector2 Scale { get; init; } = Vector2.One;

		/// <summary>
		/// The meter inside the slot, when it has one — `FillWorldIcon`'s `HoldMeter`, bound to
		/// the meter of the same name — and the child of `Contents` holding it, which is cloned
		/// whole in place of the single quad.
		/// </summary>
		public int MeterOrdinal { get; init; } = -1;

		public Engine_HudValues.Handle Meter { get; init; }

		public int WidgetOrdinal { get; init; } = -1;

		public Vector2 WidgetOffset { get; init; }

		// Live state.
		public Node3D? Node { get; set; }
		public MeshInstance3D? Icon { get; set; }
		public MeshInstance3D? Label { get; set; }
		public string Shown { get; set; } = string.Empty;
		public MeshInstance3D? OpinionLabel { get; set; }
		public string ShownOpinion { get; set; } = string.Empty;
		public uint ShownTexture { get; set; }
		public bool Lit { get; set; }

		/// <summary>The cloned widget and what its meter scrubs; null when drawn as one quad.</summary>
		public Engine_UiCloneBinding? Binding { get; set; }

		public List<(Node3D Node, BgfAnimation Animation, Engine_UiTimeline.Target Target)> Tracks { get; } = new();

		public List<(int Slot, Engine_UiTimeline.Window Window)> Windows { get; } = new();

		/// <summary>The button's pressed look, which the `FillWorldIconButton` toggle raises.</summary>
		public Node3D? Pressed { get; set; }
		public Vector2 LastOrigin { get; set; } = new(float.NaN, float.NaN);
		public float LastPieceScale { get; set; } = float.NaN;
		public float LastScrubT { get; set; } = float.NaN;
		public bool? LastScrubPressed { get; set; }
	}

	private readonly List<Slot> _slots = new();
	private Engine_UiScreen? _screen;
	private int _documentElements;
	private string _reported = string.Empty;

	/// <summary>How many slots the guest currently lights.</summary>
	public int Lit { get; private set; }

	/// <summary>
	/// A hold prompt this layer draws whatever the guest's one hold slot shows: player two's own
	/// (HostBridge.GetPlayerTwoHoldPrompt), its title; null for none. It lights the hold icon
	/// (`FillWorldIcon`, pool index 16) — the copy whose component the guest has active.
	/// </summary>
	public string? StatedFill { get; set; }

	/// <summary>
	/// Titles this layer draws over the guest's, by pool index: on split screen, the text this
	/// half's player would be shown where the guest's one prompt for a thing is the other
	/// player's (HostBridge.GetPromptTitles). Null for none.
	/// </summary>
	public IReadOnlyDictionary<int, string>? StatedTitles { get; set; }

	/// <summary>
	/// Resolves the pooled slots from the parsed document. The file may carry the pool twice —
	/// the 4:3 and 16:9 layouts — and every copy is kept: the guest's own active bit says which
	/// copy draws, so choosing here would be a host-side guess.
	/// </summary>
	public void Bind(Engine_UiScreen screen)
	{
		using Engine_UiKeyGlyphs.SurfaceScope glyphs = Engine_UiKeyGlyphs.Surface(GlyphPlayer);

		// A rebind (a new `hud.bgf`, or new art) drops the slots built from the old one.
		foreach (Slot slot in _slots)
		{
			if (slot.Node is not null && GodotObject.IsInstanceValid(slot.Node))
			{
				slot.Node.QueueFree();
			}
		}

		Lit = 0;
		_slots.Clear();
		_screen = screen;
		_documentElements = screen.Nodes.Length;
		Engine_UiBindings bindings = Engine_UiBindings.Read(screen.Document);

		foreach (UiNode node in screen.Nodes)
		{
			if (node.Type != Refii.Ui.BgfElementType.Component || !node.HasAuthoredName ||
				!(IsWorldIconName(node.Name) || IsSingleIconName(node.Name)))
			{
				continue;
			}

			// The guest lights `Contents`, never the root: `sub_82825618` (0x828256d4) and
			// `sub_82825328` call `sub_82284210(Contents, pres, visible)` on the child, and the
			// component root stays authored-active for the life of the pool.
			int contents = node.Ordinal;
			foreach (UiNode child in node.Children)
			{
				if (child.Name == "Contents")
				{
					contents = child.Ordinal;
					break;
				}
			}

			(UiNode? quadNode, Vector2 quadOffset) = FirstQuad(node, Vector2.Zero);
			(UiNode? titleNode, Vector2 titleOffset) = FirstText(node, Vector2.Zero);
			(UiNode? opinionNode, Vector2 opinionOffset) = NamedText(node, Vector2.Zero, "Opinion");
			(int meter, string meterName, UiNode? widget, Vector2 widgetOffset) =
				MeterWidget(screen, bindings, node, contents);

			_slots.Add(new Slot
			{
				Name = node.Name,
				Ordinal = node.Ordinal,
				LitOrdinal = contents,
				QuadOrdinal = quadNode?.Ordinal ?? -1,
				Quad = quadNode?.Quad,
				QuadOffset = quadOffset,
				QuadScale = quadNode is null
					? Vector2.One
					: new Vector2(quadNode.ScaleX, quadNode.ScaleY),
				TitleOrdinal = titleNode?.Ordinal ?? -1,
				Title = titleNode?.Text,
				TitleOffset = titleOffset,
				OpinionOrdinal = opinionNode?.Ordinal ?? -1,
				Opinion = opinionNode?.Text,
				OpinionOffset = opinionOffset,
				Pivot = new Vector2(node.PivotX, node.PivotY),
				Scale = new Vector2(node.ScaleX, node.ScaleY),
				MeterOrdinal = meter,
				Meter = Engine_HudValues.Handle.For(meterName),
				WidgetOrdinal = widget?.Ordinal ?? -1,
				WidgetOffset = widgetOffset,
			});
		}

		GD.Print($"[world-icons] {_slots.Count} pooled slot(s) resolved from the file" +
			(_slots.Count == 0 ? " — NOTHING TO DRAW" : string.Empty));
	}

	/// <summary>
	/// The three icons the guest draws OUTSIDE the pool of sixteen: registries 8, 9 and 10 —
	/// the hold-to-fill icon, and the hero's and henchman's off-screen markers. Same request
	/// path, same per-frame position write, one element each instead of a pool.
	/// </summary>
	private static bool IsSingleIconName(string name) =>
		name is "FillWorldIcon" or "HeroOffScreenWorldIcon" or "HenchmanOffScreenWorldIcon";

	/// <summary>`WorldIcon` followed by digits and nothing else — not `FillWorldIconButton`.</summary>
	private static bool IsWorldIconName(string name)
	{
		const string prefix = "WorldIcon";
		if (!name.StartsWith(prefix, System.StringComparison.Ordinal) ||
			name.Length == prefix.Length)
		{
			return false;
		}

		for (int i = prefix.Length; i < name.Length; ++i)
		{
			if (!char.IsAsciiDigit(name[i]))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// The meter bound inside a slot and the child of <paramref name="contents"/> that holds it,
	/// with that child's offset from the slot — (-1, "", null) for a slot with no meter.
	/// </summary>
	private static (int, string, UiNode?, Vector2) MeterWidget(
		Engine_UiScreen screen, Engine_UiBindings bindings, UiNode slot, int contents)
	{
		foreach (Engine_UiBindings.Binding binding in bindings.All)
		{
			if (binding.Kind != Engine_UiBindings.Kind.Meter ||
				!Engine_UiTimeline.InSubtree(screen, binding.ElementOrdinal, contents))
			{
				continue;
			}

			UiNode? widget = screen.Nodes[binding.ElementOrdinal];
			while (widget?.Parent is { } parent && parent.Ordinal != contents)
			{
				widget = parent;
			}

			if (widget is null || widget.Ordinal == contents)
			{
				continue;
			}

			var offset = Vector2.Zero;
			for (UiNode? up = widget; up is not null && up.Ordinal != slot.Ordinal; up = up.Parent)
			{
				offset += new Vector2(up.PositionX, up.PositionY);
			}

			return (binding.ElementOrdinal, binding.Name, widget, offset);
		}

		return (-1, string.Empty, null, Vector2.Zero);
	}

	private static (UiNode?, Vector2) FirstQuad(UiNode node, Vector2 at)
	{
		foreach (UiNode child in node.Children)
		{
			Vector2 offset = at + new Vector2(child.PositionX, child.PositionY);
			if (child.Quad is not null)
			{
				return (child, offset);
			}

			(UiNode? inner, Vector2 innerAt) = FirstQuad(child, offset);
			if (inner is not null)
			{
				return (inner, innerAt);
			}
		}

		return (null, at);
	}

	/// <summary>
	/// The slot's `Title` text and its accumulated offset. By NAME: every world icon authors two
	/// `Text` elements with `Opinion` first in document order, so the first one is the wrong one.
	///
	/// <code>
	/// Contents [Node]
	///   Icon [Node] …
	///   Opinion [Text] ord=183 pos=(0,0,0)
	///   Title   [Text] ord=184 pos=(0,8,0)
	/// </code>
	///
	/// Falls back to the first `Text` when a slot has no `Title`.
	/// </summary>
	private static (UiNode?, Vector2) FirstText(UiNode node, Vector2 at) =>
		NamedText(node, at, "Title") is (not null, _) and var named ? named : AnyText(node, at);

	private static (UiNode?, Vector2) NamedText(UiNode node, Vector2 at, string name)
	{
		foreach (UiNode child in node.Children)
		{
			Vector2 offset = at + new Vector2(child.PositionX, child.PositionY);
			if (child.Type == Refii.Ui.BgfElementType.Text &&
				string.Equals(child.Name, name, System.StringComparison.Ordinal))
			{
				return (child, offset);
			}

			(UiNode? inner, Vector2 innerAt) = NamedText(child, offset, name);
			if (inner is not null)
			{
				return (inner, innerAt);
			}
		}

		return (null, at);
	}

	private static (UiNode?, Vector2) AnyText(UiNode node, Vector2 at)
	{
		foreach (UiNode child in node.Children)
		{
			Vector2 offset = at + new Vector2(child.PositionX, child.PositionY);
			if (child.Type == Refii.Ui.BgfElementType.Text)
			{
				return (child, offset);
			}

			(UiNode? inner, Vector2 innerAt) = AnyText(child, offset);
			if (inner is not null)
			{
				return (inner, innerAt);
			}
		}

		return (null, at);
	}

	/// <summary>Unlights every slot — the feed is gone (the guest left the game).</summary>
	public void Clear()
	{
		Lit = 0;
		foreach (Slot slot in _slots)
		{
			slot.Lit = false;
			if (slot.Node is not null)
			{
				slot.Node.Visible = false;
			}
		}
	}

	/// <summary>
	/// Takes the slow-changing statements: which slots are lit, their titles, their art — from the
	/// HUD's stand (Engine_UiHudStand): <paramref name="drawn"/> the resolved draw gate, which carries
	/// the guest's told `active` on each slot's `Contents` (the pool's claim `sub_82825618` and
	/// update `sub_82824D60` show and hide it), <paramref name="texts"/> the told texts and
	/// <paramref name="textures"/> the told textures, per file ordinal. Declined on a count mismatch —
	/// the arrays join to the file by ordinal.
	/// </summary>
	public void Sample(IReadOnlyList<bool> drawn, IReadOnlyList<string?> texts, uint[]? textures)
	{
		using Engine_UiKeyGlyphs.SurfaceScope glyphs = Engine_UiKeyGlyphs.Surface(GlyphPlayer);

		if (drawn.Count != _documentElements || texts.Count != _documentElements)
		{
			Clear();
			return;
		}

		Lit = 0;
		bool fillStated = false;
		foreach (Slot slot in _slots)
		{
			bool statedFill = StatedFill is not null && !fillStated && PoolIndexOf(slot.Name) == 16 &&
				slot.Ordinal < drawn.Count && drawn[slot.Ordinal];
			fillStated |= statedFill;
			slot.Lit = statedFill || (slot.LitOrdinal < drawn.Count && drawn[slot.LitOrdinal]);
			if (!slot.Lit)
			{
				if (slot.Node is not null)
				{
					slot.Node.Visible = false;
				}
				continue;
			}

			++Lit;
			slot.Node ??= Build(slot);
			slot.Node.Visible = true;

			// The guest's stated art, per sample: quest icon, trader icon and region marker all
			// ride the same pooled quad and only the hash says which. The file's own hash is
			// the fallback, so an unstated slot keeps the authored image.
			uint stated = textures is not null && slot.QuadOrdinal >= 0 &&
				slot.QuadOrdinal < textures.Length && textures[slot.QuadOrdinal] != 0
					? textures[slot.QuadOrdinal]
					: slot.Quad?.TextureHash ?? 0;
			if (stated != slot.ShownTexture && slot.Icon is not null &&
				ResolveTexture?.Invoke(stated) is { } texture &&
				slot.Icon.MaterialOverride is StandardMaterial3D material)
			{
				// Advanced only on success, so an unresolvable hash is asked for again.
				slot.ShownTexture = stated;
				material.AlbedoTexture = texture;
			}

			// The title, through the same glyph path as every owned surface. Live text only:
			// the authored string is the artist's "Text" placeholder, never a fallback — an
			// element still holding it was never written (Engine_HudView.LiveTextToPaint).
			string title = statedFill
				? StatedFill!
				: StatedTitles is { } titles && titles.TryGetValue(PoolIndexOf(slot.Name), out string? own)
					? own
				: slot.TitleOrdinal >= 0 && slot.TitleOrdinal < texts.Count
					? Engine_HudView.LiveTextToPaint(
						string.Empty, texts[slot.TitleOrdinal], slot.Title?.Display, null)
					: string.Empty;
			// The `.fac` record's colour — the prompt title is authored (255,255,48).
			var colour = slot.Title is { } record
				? new Color(record.R, record.G, record.B, 1f)
				: new Color(1f, 224f / 255f, 128f / 255f, 1f);
			if (title != slot.Shown && slot.Label is not null && Theme is not null)
			{
				slot.Shown = title;
				Theme.Paint(
					slot.Label, title,
					slot.Title?.Size is > 0 and var size ? size : 18, colour,
					horizontalAlign: 1, verticalAlign: 1,
					boxWidth: 0f, boxHeight: 0f, wrapMode: 0);
			}

			// The amount line, live only, exactly like the title.
			string opinion = slot.OpinionOrdinal >= 0 && slot.OpinionOrdinal < texts.Count
				? texts[slot.OpinionOrdinal] ?? string.Empty
				: string.Empty;
			if (opinion != slot.ShownOpinion && slot.OpinionLabel is not null && Theme is not null)
			{
				slot.ShownOpinion = opinion;
				Theme.Paint(
					slot.OpinionLabel, opinion,
					slot.Opinion?.Size is > 0 and var opinionSize ? opinionSize : 18, colour,
					horizontalAlign: 1, verticalAlign: 1,
					boxWidth: 0f, boxHeight: 0f, wrapMode: 0);
			}
		}

		Report();
	}

	/// <summary>
	/// Follows the guest's per-frame positions. A slot with no stated row keeps the file's
	/// authored spot (offscreen storage for a pooled element); the active bit gates drawing.
	/// </summary>
	public void Track(float?[]?[]? transform)
	{
		if (transform is null || transform.Length != _documentElements)
		{
			return;
		}

		foreach (Slot slot in _slots)
		{
			if (!slot.Lit || slot.Node is null ||
				slot.Ordinal >= transform.Length || transform[slot.Ordinal] is not { } row)
			{
				continue;
			}

			float x = row.Length > 0 && row[0] is { } sx ? sx : 0f;
			float y = row.Length > 1 && row[1] is { } sy ? sy : 0f;

			// `origin = position - basis * pivot`, with the component's authored scale standing
			// in for the basis: these elements are never rotated.
			Vector2 origin = new Vector2(x, y) - (PieceScale * slot.Scale * slot.Pivot);
			if (float.IsFinite(origin.X) && float.IsFinite(origin.Y))
			{
				if (!slot.LastOrigin.IsEqualApprox(origin))
				{
					slot.LastOrigin = origin;
					slot.Node.Position = new Vector3(origin.X, origin.Y, 0f);
				}
				if (!Mathf.IsEqualApprox(slot.LastPieceScale, PieceScale))
				{
					slot.LastPieceScale = PieceScale;
					slot.Node.Scale = new Vector3(PieceScale, PieceScale, 1f);
				}
			}
		}
	}

	/// <summary>
	/// The pool index a slot answers to: `WorldIcon{n}` is n-1, the three singles are 16/17/18
	/// — the order of the guest's slot records at 0x8349BC80 (`sub_82825618`).
	/// </summary>
	private static int PoolIndexOf(string name) => name switch
	{
		"FillWorldIcon" => 16,
		"HeroOffScreenWorldIcon" => 17,
		"HenchmanOffScreenWorldIcon" => 18,
		_ => name.StartsWith("WorldIcon", System.StringComparison.Ordinal) &&
			int.TryParse(name.Substring("WorldIcon".Length), out int n)
				? n - 1
				: -1,
	};

	/// <summary>
	/// A mod's size for this widget (<c>ui/hud.json</c> <c>scale</c>, <see cref="Engine_HudWidgets"/>):
	/// each mark is drawn this much larger about its own point; 1 is the game's.
	/// </summary>
	public float PieceScale { get; set; } = 1f;

	/// <summary>
	/// Places the lit slots at canvas points projected by GODOT from the guest's world anchors —
	/// the pooled object's `+112` anchor plus its `+144` lift, the same inputs the guest projects
	/// through its own cameras. The guest's stated position would follow the guest's camera, not
	/// the one Godot draws the world with.
	/// </summary>
	/// <param name="hideUntracked">
	/// Split screen: each half's layer draws only the slots its player sees, so a lit slot with no
	/// position here is another player's and hides; whole-screen, an untracked slot keeps its own.
	/// </param>
	public void TrackProjected(IReadOnlyDictionary<int, Vector2> canvasBySlot, bool hideUntracked = false)
	{
		foreach (Slot slot in _slots)
		{
			if (!slot.Lit || slot.Node is null)
			{
				continue;
			}

			int index = PoolIndexOf(slot.Name);
			if (index < 0 || !canvasBySlot.TryGetValue(index, out Vector2 at))
			{
				if (hideUntracked && slot.Node.Visible)
				{
					slot.Node.Visible = false;
				}

				continue;
			}

			if (hideUntracked && !slot.Node.Visible)
			{
				slot.Node.Visible = true;
			}

			Vector2 origin = at - (PieceScale * slot.Scale * slot.Pivot);
			if (float.IsFinite(origin.X) && float.IsFinite(origin.Y))
			{
				if (!slot.LastOrigin.IsEqualApprox(origin))
				{
					slot.LastOrigin = origin;
					slot.Node.Position = new Vector3(origin.X, origin.Y, 0f);
				}
				if (!Mathf.IsEqualApprox(slot.LastPieceScale, PieceScale))
				{
					slot.LastPieceScale = PieceScale;
					slot.Node.Scale = new Vector3(PieceScale, PieceScale, 1f);
				}
			}
		}
	}

	/// <summary>
	/// The hold widget's size and lift over its title — OUR layout, not the file's. As authored
	/// (`Icon` at y 25, `HoldMeter` 0.55, `Title` at y 8) the ring's outer edge reaches y 4.4,
	/// under the title's middle, and the pair reads large beside an 18-point title. The guest
	/// states only the icon's position (`sub_82824D60` → `sub_824251E0`, position.x/y/z) under
	/// an orthographic UI camera, so nothing in the guest sizes it.
	/// </summary>
	private const float WidgetScale = 0.85f;

	/// <summary>Canvas units: the scaled ring's bottom (25 − 20.6 × 0.85 + 12 ≈ 19.5) clears the title's top (≈ 17).</summary>
	private const float WidgetLift = 12f;

	/// <summary>
	/// Clones a slot's meter widget whole — the hold icon's ring of nine timed half-discs and
	/// its button — and binds what its meter's timeline scrubs. False leaves the slot to the
	/// single-quad path, and says why once.
	/// </summary>
	private bool BuildWidget(Slot slot, Node3D node)
	{
		if (slot.MeterOrdinal < 0 || slot.WidgetOrdinal < 0 || _screen is not { } screen)
		{
			return false;
		}

		UiNode widget = screen.Nodes[slot.WidgetOrdinal];
		if (Art?.ArtForOrdinal(widget.Name, widget.Ordinal, keepAuthoredBasis: true) is not { } art)
		{
			GD.Print($"[world-icons] '{slot.Name}' has meter '{slot.Meter.Name}' but the HUD book " +
				$"holds no '{widget.Name}' (element {widget.Ordinal}) to clone — drawn as one quad");
			return false;
		}

		art.Transform = new Transform3D(
			art.Transform.Basis.Scaled(new Vector3(WidgetScale, WidgetScale, 1f)),
			new Vector3(slot.WidgetOffset.X, slot.WidgetOffset.Y + WidgetLift, 0f));
		node.AddChild(art);

		var binding = new Engine_UiCloneBinding { ResolveTexture = ResolveTexture };
		binding.Bind(art, screen);
		slot.Binding = binding;

		foreach ((BgfAnimation animation, Engine_UiTimeline.Target target) in
			Engine_UiTimeline.Tracks(screen, slot.MeterOrdinal))
		{
			if (binding.NodeFor(animation.ElementOrdinal) is { } bound)
			{
				slot.Tracks.Add((bound, animation, target));
			}
		}

		BgfSlide? playing = Engine_UiTimeline.PlayingSlide(screen.Document, slot.MeterOrdinal);
		foreach (Engine_UiTimeline.Window window in
			Engine_UiTimeline.Windows(screen, slot.MeterOrdinal, playing))
		{
			int bound = binding.SlotFor(window.Ordinal);
			if (bound >= 0)
			{
				slot.Windows.Add((bound, window));
			}
		}

		// The button's pressed look: `Button`'s `on` slide lights `selected`, `off` darkens it.
		// Within it the `button` slide — the one it opens on — shows the pressed glyph and hides
		// the `eye`, which only `FillWorldIconSetEyeIsOpen` asks for.
		slot.Pressed = Engine_UiTheme.FindByName(art, "selected") as Node3D;
		if (slot.Pressed is not null && Engine_UiTheme.FindByName(slot.Pressed, "eye") is Node3D eye)
		{
			eye.Visible = false;
		}

		Scrub(slot, 0f, pressed: false);
		GD.Print($"[world-icons] '{slot.Name}' draws its '{widget.Name}' widget whole: " +
			$"{slot.Tracks.Count} track(s) and {slot.Windows.Count} time window(s) on meter " +
			$"'{slot.Meter.Name}'");
		return true;
	}

	/// <summary>
	/// Scrubs the lit widgets by their meters, every frame. <paramref name="pressed"/> is the
	/// hold button: `sub_823DD318` raises `FillWorldIconButton` on the press and lowers it on
	/// the release, a toggle the host cannot read (registry 9), so it is stated from the pad.
	/// </summary>
	/// <param name="meter">
	/// Player two's own hold, 0..100, for their half's layer; null reads the guest's published meter.
	/// </param>
	public void Drive(bool pressed, float? meter = null)
	{
		foreach (Slot slot in _slots)
		{
			if (!slot.Lit || slot.Binding is null)
			{
				continue;
			}

			// `sub_821C5BB0` publishes `fraction x 100` on meter registry 1 whenever it moves;
			// unpublished is the ring at rest.
			float value = meter ?? Values?.Meter(slot.Meter) ?? 0f;
			Scrub(slot, Mathf.Clamp(value, 0f, 100f) * 10f, pressed);
		}
	}

	private static void Scrub(Slot slot, float t, bool pressed)
	{
		if (Mathf.IsEqualApprox(slot.LastScrubT, t) && slot.LastScrubPressed == pressed)
		{
			return;
		}
		slot.LastScrubT = t;
		slot.LastScrubPressed = pressed;

		foreach ((Node3D node, BgfAnimation animation, Engine_UiTimeline.Target target) in slot.Tracks)
		{
			if (GodotObject.IsInstanceValid(node))
			{
				Engine_UiTimeline.Write(node, target, animation.Evaluate(t));
			}
		}

		foreach ((int bound, Engine_UiTimeline.Window window) in slot.Windows)
		{
			slot.Binding?.Gate(bound, window.IsOpen(t));
		}

		if (slot.Pressed is not null)
		{
			slot.Pressed.Visible = pressed;
		}
	}

	/// <summary>One slot's scene: a textured quad and a title label at their authored offsets.</summary>
	private Node3D Build(Slot slot)
	{
		var node = new Node3D { Name = slot.Name };
		AddChild(node);

		if (BuildWidget(slot, node))
		{
			// The widget carries the slot's art; the single-quad path below is for icons.
		}
		else if (slot.Quad is { } quad)
		{
			var icon = new MeshInstance3D
			{
				Name = "Icon",
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				Position = new Vector3(slot.QuadOffset.X, slot.QuadOffset.Y, 0f),
				Mesh = new QuadMesh
				{
					// QuadBaseSize: a unit quad is 100 canvas units before scale.
					Size = new Vector2(100f * slot.QuadScale.X, 100f * slot.QuadScale.Y),
				},
				MaterialOverride = new StandardMaterial3D
				{
					Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
					ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
					CullMode = BaseMaterial3D.CullModeEnum.Disabled,
					AlbedoTexture = ResolveTexture?.Invoke(quad.TextureHash),
				},
			};

			// The authored atlas rectangle, or the whole texture when degenerate.
			if (!quad.IsDegenerate && icon.Mesh is QuadMesh mesh)
			{
				// QuadMesh has no UV rect; the atlas cell rides the material's UV1 transform.
				if (icon.MaterialOverride is StandardMaterial3D material)
				{
					material.Uv1Scale = new Vector3(
						quad.UMax - quad.UMin, quad.VMax - quad.VMin, 1f);
					material.Uv1Offset = new Vector3(quad.UMin, quad.VMin, 0f);
				}
			}

			node.AddChild(icon);
			slot.Icon = icon;
			slot.ShownTexture = quad.TextureHash;
		}

		var label = new MeshInstance3D
		{
			Name = "Title",
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			// A step in front of the icon: equal-depth alpha has no defined winner.
			Position = new Vector3(slot.TitleOffset.X, slot.TitleOffset.Y, 1f),
		};
		node.AddChild(label);
		slot.Label = label;

		if (slot.OpinionOrdinal >= 0)
		{
			var opinion = new MeshInstance3D
			{
				Name = "Opinion",
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				Position = new Vector3(slot.OpinionOffset.X, slot.OpinionOffset.Y, 1f),
			};
			node.AddChild(opinion);
			slot.OpinionLabel = opinion;
		}

		return node;
	}

	/// <summary>Names what is lit, when the set changes.</summary>
	private void Report()
	{
		var lit = new List<string>();
		foreach (Slot slot in _slots)
		{
			if (slot.Lit)
			{
				lit.Add($"{slot.Name}" +
					(slot.Shown.Length == 0 ? string.Empty : $"='{slot.Shown}'"));
			}
		}

		string described = lit.Count == 0 ? "none lit" : string.Join("  ", lit);
		if (described == _reported)
		{
			return;
		}

		_reported = described;
		GD.Print($"[world-icons] {Lit}/{_slots.Count} lit: {described}");
	}
}
