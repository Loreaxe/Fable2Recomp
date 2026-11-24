namespace Refii.Titles.Fable3;

using System.Collections.Generic;

/// <summary>
/// Fable 3's element statements (<see cref="Fable3Elements"/>) joined to one presentation's
/// document by ordinal: the per-element slide, stated opacity, text and timeline position the
/// shared HUD view and its resolve read. Pure — no Godot type — so the join is held by tests.
/// </summary>
/// <remarks>
/// THE THREE SPELLINGS A STATEMENT ARRIVES UNDER (gui_element_hooks.cpp):
/// <list type="bullet">
/// <item>`Scene` — the presentation's root element, `sub_82819DB8(pres)`: hud.bgf's by sub_825C1D28
/// (1 shown), sub_825C1CB0 (2 hidden) and the cutscene rules sub_8249A030 (3); InteractionMode.bgf's
/// by the interaction mode's tick sub_825C3A78 (0x825C3D10 / 0x825C3D5C / 0x825C3D8C / 0x825C3EC8).
/// It is the document's root.</item>
/// <item>A dotted path, as the resolver sub_82819E40 composed it. The resolver starts at the
/// presentation's `Layer` when the parent is 0 (0x82819E80), so a path is either spelled from
/// `Layer` (`Layer.TopBoxRefined.TopBox`) or from beneath it (`dpad.dpad.dpadmenu`).</item>
/// <item>`Attribute:value` — an element filed by the registry builder sub_82B9A108 under the
/// attribute's value (`SlideToggleName:ItemReceived`). The registry writes every element filed
/// under the name with the one value (the HUD update, 0x82259978), so it lands on every element
/// the document binds to that name.</item>
/// </list>
///
/// A COMPONENT THE GUEST NEVER WROTE IS ON ITS FIRST SLIDE. The Lua `getCurrentSlide`
/// (sub_82C25268) reads `pres->vt[9]()->vt[8](elem)`: the presentation's vt[9] is `pres + 0x238`
/// (0x82193994, vtable 0x82007700 on the HUD presentation), whose vt[1] returns the element itself
/// (0x82BA70C8) and whose vt[8] reads the element's byte +0x2B (`lbz r3,0x2B(r3)` at 0x8228C214);
/// goToSlide (vt[2], 0x82268BE0) accepts 1 ≤ slide &lt; the count at +0x2A, the master being 0.
/// Read in the r7 run: the `DPadUp` toggle element (0x47E6D9EC, never written — no statement) holds
/// +0x28..+0x2B = 0251 03 01, slide 1 of master + `off` + `on`. Every owner in `hud.bgf` authors
/// slide 1 as its resting state (`Off` / `hide` / `default`, a container's one `Slide1`).
/// </remarks>
public sealed class Fable3HudStatements
{
	/// <summary>One element's last statement: any channel may be unstated.</summary>
	/// <param name="Position">The element's local position (sub_82818580), in the .bgf's units.</param>
	/// <param name="Texture">The texture path set on it (sub_82818EF0).</param>
	/// <param name="Kind">
	/// A registry writer's statement (<see cref="Fable3HudRegistry"/>): it lands on every element the
	/// document binds under <paramref name="NameHash"/> in this kind's registry — or, with
	/// <paramref name="EveryName"/>, on every element bound in it — rather than on a path.
	/// </param>
	/// <param name="Source">
	/// What a stated <paramref name="Opacity"/> is made of, with its stamps: null where no later slide
	/// entry can retire it (a registry writer's value).
	/// </param>
	public readonly record struct Statement(
		string Path, int? State, float? Opacity, string? Text, float? Time, ulong Stamp,
		(float X, float Y, float Z)? Position = null, string? Texture = null,
		Refii.Ui.Engine_UiBindings.Kind? Kind = null, uint NameHash = 0u, bool EveryName = false,
		uint TextureHash = 0u, (float R, float G, float B, float A)? Colour = null, OpacitySource? Source = null);

	/// <summary>
	/// A stated drawn opacity's two parts: the opacity attribute's write (SetOpacity, the visibility
	/// toggle — both through the store, `@Ui.Opacity` / `@Ui.Visible`) and the active flag asked off
	/// (`@Ui.Active`), each with the stamp of the statement that made it.
	/// </summary>
	/// <remarks>
	/// The guest's GoTo applies the entered slide's entries IN the call (TU3 `sub_82267158`: master
	/// 0x82267224, slide 0x82267250 -> `sub_821CA6D8` 0x821CA960, each command through the store
	/// 0x821CA7A4, each entry's active bit through flags 0x821CA760): a told slide entered after a
	/// write overwrote it, and a write after the entry stands. BedMenu's Show halves `.Sex`
	/// (0x82B8DBF8 -> 0x82B8E878) and then enters BedMenu's slide (0x82B8DC0C), whose entry sets `.Sex`
	/// to 100: the 360 drew 100.
	/// </remarks>
	public readonly record struct OpacitySource(float? Write, ulong WriteStamp, bool ActiveOff, ulong ActiveStamp)
	{
		/// <summary>
		/// The opacity that still stands over the slides given the sequence of the latest told entry
		/// that wrote the element's opacity and its active bit: null where the entries rule.
		/// </summary>
		public float? After(ulong opacityEntered, ulong activeEntered)
		{
			if (ActiveOff && ActiveStamp > activeEntered)
			{
				return 0f;
			}

			return Write is { } write && WriteStamp > opacityEntered ? write : null;
		}
	}

	/// <summary>The width of a transform row: position x, y, z then scale x, y, z.</summary>
	public const int TransformWidth = 6;

	/// <summary>The name the root element is stated under.</summary>
	public const string RootPath = "Scene";

	/// <summary>The segment the resolver starts a parentless path at.</summary>
	public const string LayerSegment = "Layer";

	private readonly Dictionary<string, int> _byPath = new(System.StringComparer.Ordinal);
	private readonly System.Func<string, string, IReadOnlyList<int>> _bound;
	private readonly System.Func<Refii.Ui.Engine_UiBindings.Kind, uint?, IReadOnlyList<int>>? _named;
	private readonly int _root;
	private readonly int[] _slideOwners;
	private readonly int _count;
	private readonly List<Statement> _ordered = new();

	/// <param name="paths">Per ordinal, the element's dotted path from beneath the root; the root's is empty.</param>
	/// <param name="bound">The ordinals bound to (attribute, value) — the document's registry bindings.</param>
	/// <param name="slideOwners">Every ordinal that owns numbered slides.</param>
	/// <param name="named">
	/// The ordinals bound under a name hash in a kind's registry, every ordinal of the kind for a
	/// null hash — what a registry writer's statement lands on. Without it those statements join nothing.
	/// </param>
	public Fable3HudStatements(
		IReadOnlyList<string> paths, System.Func<string, string, IReadOnlyList<int>> bound, IReadOnlyList<int> slideOwners,
		System.Func<Refii.Ui.Engine_UiBindings.Kind, uint?, IReadOnlyList<int>>? named = null)
	{
		_count = paths.Count;
		_bound = bound;
		_named = named;
		_root = -1;
		for (int i = 0; i < paths.Count; ++i)
		{
			if (paths[i].Length == 0)
			{
				_root = _root < 0 ? i : _root;
				continue;
			}

			// The first element at a path: the document walks parents first, and the resolver's
			// renderer lookup answers the first child of a name.
			_byPath.TryAdd(paths[i], i);
		}

		_slideOwners = new int[slideOwners.Count];
		for (int i = 0; i < slideOwners.Count; ++i)
		{
			_slideOwners[i] = slideOwners[i];
		}
	}

	/// <summary>Statements whose path joined no element in the last <see cref="Apply"/>.</summary>
	public int Unmapped { get; private set; }

	/// <summary>Statements that joined at least one element in the last <see cref="Apply"/>.</summary>
	public int Mapped { get; private set; }

	/// <summary>The paths that joined nothing in the last <see cref="Apply"/>, for the census.</summary>
	public List<string> UnmappedPaths { get; } = new();

	/// <summary>The ordinals a statement's path names; empty when it names none of this document's.</summary>
	public IReadOnlyList<int> Resolve(string path)
	{
		if (path == RootPath)
		{
			return _root >= 0 ? new[] { _root } : System.Array.Empty<int>();
		}

		// `#<ordinal>`: a headless element no resolver step or filing named, keyed by its index in
		// the .bgf element table (gui_element_hooks.cpp Named, core/headless_presentation.h
		// FileOrdinal) — the index this document is laid out by.
		if (path.Length > 1 && path[0] == '#')
		{
			return int.TryParse(path[1..], System.Globalization.NumberStyles.None,
					System.Globalization.CultureInfo.InvariantCulture, out int fileOrdinal) &&
				fileOrdinal >= 0 && fileOrdinal < _count
				? new[] { fileOrdinal }
				: System.Array.Empty<int>();
		}

		int colon = path.IndexOf(':');
		if (colon > 0 && path.IndexOf('.') is var dot && (dot < 0 || dot > colon))
		{
			return _bound(path[..colon], path[(colon + 1)..]);
		}

		if (_byPath.TryGetValue(path, out int ordinal) ||
			_byPath.TryGetValue(LayerSegment + "." + path, out ordinal))
		{
			return new[] { ordinal };
		}

		return System.Array.Empty<int>();
	}

	/// <summary>
	/// Rewrites the per-ordinal channels from the statements, oldest first so the later of two
	/// statements on one element wins each channel it states. Unstated: slide 1 for an owner
	/// (see the class note), 0 otherwise; opacity, text, time and transform null; texture 0.
	/// A texture is the hash the renderable keeps (FNV-1 of the lower-cased path,
	/// <see cref="Refii.Core.Engine_GameHash.ResourcePath"/> — sub_8312F3D8 at 0x82B429EC).
	/// <paramref name="slideStamps"/>, when given, takes per ordinal the stamp of the statement whose
	/// slide landed there (0 where none did): every slide write is an ENTRY in the guest — the funnel
	/// sub_82268BE0 rewrites the slide byte and zeroes the time (+0x2B 0x82268CE4, +0x30 0x82268D00)
	/// without comparing — so a playback re-enters on a later stamp of the same slide.
	/// </summary>
	public void Apply(
		IEnumerable<Statement> statements, int[] slides, float?[] opacity, string?[] texts, float?[] times,
		float?[]?[]? transforms = null, uint[]? textures = null, ulong[]? slideStamps = null,
		(float R, float G, float B, float A)?[]? colours = null, OpacitySource?[]? sources = null)
	{
		System.Array.Clear(slides, 0, slides.Length);
		if (sources is not null)
		{
			System.Array.Clear(sources, 0, sources.Length);
		}

		if (colours is not null)
		{
			System.Array.Clear(colours, 0, colours.Length);
		}

		System.Array.Clear(opacity, 0, opacity.Length);
		System.Array.Clear(texts, 0, texts.Length);
		System.Array.Clear(times, 0, times.Length);
		if (transforms is not null)
		{
			System.Array.Clear(transforms, 0, transforms.Length);
		}

		if (textures is not null)
		{
			System.Array.Clear(textures, 0, textures.Length);
		}

		if (slideStamps is not null)
		{
			System.Array.Clear(slideStamps, 0, slideStamps.Length);
		}

		foreach (int owner in _slideOwners)
		{
			if (owner >= 0 && owner < slides.Length)
			{
				slides[owner] = 1;
			}
		}

		_ordered.Clear();
		_ordered.AddRange(statements);
		_ordered.Sort((a, b) => a.Stamp.CompareTo(b.Stamp));

		Mapped = 0;
		Unmapped = 0;
		UnmappedPaths.Clear();
		foreach (Statement statement in _ordered)
		{
			if (statement.Kind is { } kind)
			{
				// A registry name this document binds nothing under is another document's: not a miss.
				IReadOnlyList<int> named = _named?.Invoke(kind, statement.EveryName ? null : statement.NameHash) ??
					System.Array.Empty<int>();
				if (named.Count != 0)
				{
					++Mapped;
					Land(statement, named, slides, opacity, texts, times, transforms, textures, slideStamps, colours, sources);
				}

				continue;
			}

			IReadOnlyList<int> ordinals = Resolve(statement.Path);
			if (ordinals.Count == 0)
			{
				++Unmapped;
				UnmappedPaths.Add(statement.Path);
				continue;
			}

			++Mapped;
			Land(statement, ordinals, slides, opacity, texts, times, transforms, textures, slideStamps, colours, sources);
		}
	}

	/// <summary>Writes each channel the statement states onto every ordinal it names.</summary>
	private void Land(
		Statement statement, IReadOnlyList<int> ordinals, int[] slides, float?[] opacity, string?[] texts, float?[] times,
		float?[]?[]? transforms, uint[]? textures, ulong[]? slideStamps, (float R, float G, float B, float A)?[]? colours,
		OpacitySource?[]? sources)
	{
		foreach (int ordinal in ordinals)
		{
			if (ordinal < 0 || ordinal >= _count)
			{
				continue;
			}

			if (statement.State is { } state && ordinal < slides.Length)
			{
				slides[ordinal] = state;
				if (slideStamps is not null && ordinal < slideStamps.Length)
				{
					slideStamps[ordinal] = statement.Stamp;
				}
			}

			if (statement.Opacity is { } stated && ordinal < opacity.Length)
			{
				opacity[ordinal] = stated;
				if (sources is not null && ordinal < sources.Length)
				{
					sources[ordinal] = statement.Source;
				}
			}

			if (statement.Text is { } text && ordinal < texts.Length)
			{
				texts[ordinal] = text;
			}

			if (statement.Time is { } time && ordinal < times.Length)
			{
				times[ordinal] = time;
			}

			if (statement.Position is { } position && transforms is not null && ordinal < transforms.Length)
			{
				float?[] row = transforms[ordinal] ??= new float?[TransformWidth];
				row[0] = position.X;
				row[1] = position.Y;
				row[2] = position.Z;
			}

			if (statement.Texture is { Length: > 0 } texture && textures is not null && ordinal < textures.Length)
			{
				textures[ordinal] = Refii.Core.Engine_GameHash.ResourcePath(texture);
			}

			// The renderable's diffuse as the colour setter stated it (sub_828190E8, `@Ui.Colour`), 0..1.
			if (statement.Colour is { } colour && colours is not null && ordinal < colours.Length)
			{
				colours[ordinal] = colour;
			}

			// A texture the guest stated as the renderable's own hash (the `ImageName` writer's +512).
			if (statement.TextureHash != 0u && textures is not null && ordinal < textures.Length)
			{
				textures[ordinal] = statement.TextureHash;
			}
		}
	}
}
