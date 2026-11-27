namespace Refii.Ui;

/// <summary>
/// WHERE A LAID-OUT TEXT BOX'S FIRST LINE STANDS, from the presentation file. Pure — no Godot
/// type — so the tests hold it against the shipped files.
/// </summary>
/// <remarks>
/// <para>THE FIT. Both titles place a text box's lines in one function — Fable 2 TU1
/// `sub_823FC8F0`, Fable 3 `sub_82920610` — from the element the BoxName registry (index 5) files
/// under the box's name: its position summed up its parent chain (`sub_823FE7C0` → `sub_824253A0`,
/// Fable 3 `sub_82818760` at 0x82920684) and its pivot (`sub_82425980`, Fable 3 `sub_82818C30` at
/// 0x829206EC). Line 0 stands at <c>y = h · (0.5 − pivot.y) + pos.y</c> (TU1 0x823FCA08, Fable 3
/// 0x82920720), every later line one measured line lower, written onto each line's Text elements
/// through SetPosition (TU1 `sub_824251E0` at 0x823FCA84, Fable 3 `sub_82818580` at 0x829207DC).
/// <c>h</c> is the box's fitted height (Fable 3 box+16, set at 0x829205C0 just before the fit).
/// Fable 3 places a box whose byte +8 is set at the bare <c>pos</c> (0x829206F4..0x82920748).</para>
///
/// <para>THE CHAIN SUMS, as the getters walk it — the element first, then each parent (vt +88)
/// up to and including the root, at most 101 steps:</para>
/// <list type="bullet">
/// <item>POSITION (`sub_82818760` / TU1 `sub_824253A0`): an element with `position.x` adds its
/// (x, y); its `position.y` when it has one, else its x again — the reader keeps the x it read
/// when the y is missing (`sub_82818688`, 0x82818704..0x82818720).</item>
/// <item>PIVOT (`sub_82818C30` / TU1 `sub_82425980`): a running scale starts at (100, 100)
/// (unk_820FCCD0; TU1 0x82425A0C); an element with `pivot.x` adds its pivot divided by the
/// running scale (0x82818D1C / 0x82818D20), then an element with `scale.x` multiplies the running
/// scale by its own (`sub_82818918`, default 1, 0x82818D7C) — so the file's percentages come out
/// as fractions of the box.</item>
/// </list>
///
/// <para>FILE DATA. The getters read the live attribute store; it holds the file's values unless
/// a slide track or a script writes the same attributes on the chain. <see cref="Moved"/> names
/// the tracks that could move line 0, so a caller can tell the file is the whole answer — the
/// TopBox chains of both titles' hud.bgf have none (TextBoxFitTests).</para>
/// </remarks>
public static class Engine_UiTextBoxFit
{
	/// <summary>The attribute the BoxName registry files an element under.</summary>
	public const string BoxNameAttribute = "BoxName";

	/// <summary>The getters' walk limit (`v7 <= 100`, 0x82818874).</summary>
	public const int MaxChain = 101;

	/// <summary>The pivot getter's starting scale (unk_820FCCD0: 100, 100, 100).</summary>
	public const float PivotScale = 100f;

	private static readonly uint PositionX = Refii.Core.Engine_GameHash.AttributeId("position.x");
	private static readonly uint PositionY = Refii.Core.Engine_GameHash.AttributeId("position.y");
	private static readonly uint PivotX = Refii.Core.Engine_GameHash.AttributeId("pivot.x");
	private static readonly uint PivotY = Refii.Core.Engine_GameHash.AttributeId("pivot.y");
	private static readonly uint ScaleX = Refii.Core.Engine_GameHash.AttributeId("scale.x");
	private static readonly uint ScaleY = Refii.Core.Engine_GameHash.AttributeId("scale.y");

	/// <summary>
	/// The element the BoxName registry answers first for <paramref name="boxName"/>: the first
	/// in file order (the registry builder walks the tree parents first), or -1.
	/// </summary>
	public static int BoxElement(Engine_BgfDocument document, string boxName)
	{
		uint id = Refii.Core.Engine_GameHash.AttributeId(BoxNameAttribute);
		for (int i = 0; i < document.Elements.Length; ++i)
		{
			if (document.Elements[i].TryGetAttribute(id, out BgfAttribute attribute) &&
				attribute.Type == BgfValueType.String &&
				string.Equals(document.GetString(attribute.AsStringIndex()), boxName, System.StringComparison.Ordinal))
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>The position summed up the chain (`sub_82818760`).</summary>
	/// <param name="stated">
	/// A position a script states on an element in place of the file's (the widescreen adapter's
	/// `setAttribute`), per ordinal; null components keep the file's.
	/// </param>
	public static (float X, float Y) ChainPosition(
		Engine_BgfDocument document, int ordinal, System.Func<int, (float? X, float? Y)>? stated = null)
	{
		float x = 0f, y = 0f;
		int steps = 0;
		for (int at = ordinal; at >= 0 && at < document.Elements.Length && steps < MaxChain; ++steps)
		{
			BgfElement element = document.Elements[at];
			(float? X, float? Y) over = stated?.Invoke(at) ?? default;
			float? fileX = Read(element, PositionX);
			if (fileX is not null || over.X is not null)
			{
				float ex = over.X ?? fileX ?? 0f;
				x += ex;
				y += over.Y ?? Read(element, PositionY) ?? ex;
			}

			at = element.ParentOrdinal;
		}

		return (x, y);
	}

	/// <summary>
	/// THE PLACES A SLIDE WRITES, as <see cref="ChainPosition"/>'s per-ordinal statement: an element the
	/// <paramref name="slide"/> carries a `position.x` / `position.y` command for stands there (the slide's
	/// write replaces the file's own attribute, as entering the slide does), every other element keeps
	/// what <paramref name="under"/> states (or the file's). So a page that moves its rows on its slide is
	/// read where that page stands them, by the one chain rule. A null slide states nothing new.
	/// </summary>
	public static System.Func<int, (float? X, float? Y)> SlideStated(BgfSlide? slide, System.Func<int, (float? X, float? Y)>? under)
	{
		System.Func<int, (float? X, float? Y)> beneath = under ?? (_ => default);
		if (slide is null)
		{
			return beneath;
		}

		var moved = new System.Collections.Generic.Dictionary<int, (float? X, float? Y)>();
		foreach (BgfSlideEntry entry in slide.Entries)
		{
			float? x = null, y = null;
			foreach (BgfAttribute command in entry.Commands ?? System.Array.Empty<BgfAttribute>())
			{
				float? value = command.Type == BgfValueType.Float ? command.AsFloat()
					: command.Type is BgfValueType.Int1 or BgfValueType.Int2 ? command.AsInt()
					: null;
				if (command.Id == PositionX)
				{
					x = value;
				}
				else if (command.Id == PositionY)
				{
					y = value;
				}
			}

			if (x is not null || y is not null)
			{
				moved[entry.ElementOrdinal] = (x, y);
			}
		}

		return ordinal =>
		{
			(float? X, float? Y) below = beneath(ordinal);
			return moved.TryGetValue(ordinal, out (float? X, float? Y) on) ? (on.X ?? below.X, on.Y ?? below.Y) : below;
		};
	}

	/// <summary>The pivot summed up the chain over the running scale (`sub_82818C30`).</summary>
	public static (float X, float Y) ChainPivot(Engine_BgfDocument document, int ordinal)
	{
		float x = 0f, y = 0f;
		float scaleX = PivotScale, scaleY = PivotScale;
		int steps = 0;
		for (int at = ordinal; at >= 0 && at < document.Elements.Length && steps < MaxChain; ++steps)
		{
			BgfElement element = document.Elements[at];
			if (Read(element, PivotX) is { } pivotX)
			{
				x += pivotX / scaleX;
				y += (Read(element, PivotY) ?? pivotX) / scaleY;
			}

			if (Read(element, ScaleX) is { } sx)
			{
				scaleX *= sx;
				scaleY *= Read(element, ScaleY) ?? sx;
			}

			at = element.ParentOrdinal;
		}

		return (x, y);
	}

	/// <summary>
	/// Line 0's local `position.y` for the box filed under <paramref name="boxName"/> at fitted
	/// height <paramref name="height"/>; null when the document files no such box.
	/// </summary>
	public static float? FirstLineY(
		Engine_BgfDocument document, string boxName, float height, System.Func<int, (float? X, float? Y)>? stated = null)
	{
		int box = BoxElement(document, boxName);
		if (box < 0)
		{
			return null;
		}

		(float _, float y) = ChainPosition(document, box, stated);
		(float _, float pivotY) = ChainPivot(document, box);
		return (height * (0.5f - pivotY)) + y;
	}

	/// <summary>
	/// Whether the first line's height term vanishes — the chain's pivot.y is exactly one half —
	/// so line 0 stands at the box's summed position whatever the box measures.
	/// </summary>
	public static bool HeightFree(Engine_BgfDocument document, string boxName)
	{
		int box = BoxElement(document, boxName);
		return box >= 0 && ChainPivot(document, box).Y == 0.5f;
	}

	/// <summary>
	/// The slide tracks that can move line 0 off the file's value: a `position.y` track on the
	/// chain keyed anywhere but the file's value, any pivot track, or a `scale.y` track on an
	/// element below a chain element with a pivot.y (the getter divides a parent's pivot by the
	/// scales beneath it; an element's own scale applies to its parents only, 0x82818D7C after
	/// 0x82818D30). Empty: line 0 stands at the file's value whatever the slides play.
	/// </summary>
	public static System.Collections.Generic.List<(int Ordinal, uint Attribute)> Moved(Engine_BgfDocument document, string boxName)
	{
		var moved = new System.Collections.Generic.List<(int, uint)>();
		var chain = new System.Collections.Generic.List<int>();
		int steps = 0;
		for (int at = BoxElement(document, boxName); at >= 0 && at < document.Elements.Length && steps < MaxChain; ++steps)
		{
			chain.Add(at);
			at = document.Elements[at].ParentOrdinal;
		}

		foreach (BgfAnimation animation in document.Animations)
		{
			int link = chain.IndexOf(animation.ElementOrdinal);
			if (link < 0)
			{
				continue;
			}

			BgfElement element = document.Elements[animation.ElementOrdinal];
			bool moves = false;
			if (animation.AttributeId == PositionY)
			{
				float file = Read(element, PositionY) ?? Read(element, PositionX) ?? 0f;
				foreach (BgfAnimationKey key in animation.Keys)
				{
					moves |= key.Value != file;
				}
			}
			else if (animation.AttributeId == PivotX || animation.AttributeId == PivotY)
			{
				moves = true;
			}
			else if (animation.AttributeId == ScaleY)
			{
				for (int above = link + 1; above < chain.Count && !moves; ++above)
				{
					BgfElement parent = document.Elements[chain[above]];
					moves = Read(parent, PivotX) is { } px && (Read(parent, PivotY) ?? px) != 0f;
				}
			}

			if (moves)
			{
				moved.Add((animation.ElementOrdinal, animation.AttributeId));
			}
		}

		return moved;
	}

	/// <summary>
	/// THE BOX'S EXTENT RANGE, as an expandable box calibrates it (Fable 3 `CHUDExpandableBox`
	/// vf+12, `sub_8227AEB0`): the box element's `scale` multiplied up its chain — every element
	/// with `scale.x` gives (scale.x, scale.y) (`sub_82876768`, `sub_82876620`) — times 100 (the
	/// splat at 0x8227AF70), with the box's meter components at time 1000 (both meters at 100,
	/// 0x8227AF30 / 0x8227AF4C) for the maximum and at 0 for the minimum. The slides play as the
	/// document plays them (<see cref="Engine_UiSlidePlayback"/>), every component on its slide 1.
	/// </summary>
	/// <param name="meters">The ordinals the box's X and Y meter names bind (MeterName `&lt;box&gt;X` / `&lt;box&gt;Y`).</param>
	public static (float MinX, float MinY, float MaxX, float MaxY) Extents(
		Engine_UiScreen screen, int box, System.Collections.Generic.IReadOnlyCollection<int> meters)
	{
		(float maxX, float maxY) = ChainScaleAt(screen, box, meters, MeterEndTime);
		(float minX, float minY) = ChainScaleAt(screen, box, meters, 0);
		return (minX, minY, maxX, maxY);
	}

	/// <summary>A meter at 100 stands its component at `100 × 10` (sub_82975A58, flt_820E99E0).</summary>
	public const int MeterEndTime = 1000;

	private static (float X, float Y) ChainScaleAt(
		Engine_UiScreen screen, int box, System.Collections.Generic.IReadOnlyCollection<int> meters, int time)
	{
		Engine_BgfDocument document = screen.Document;
		var playback = new Engine_UiSlidePlayback(screen);
		var stated = new int[screen.Nodes.Length];
		foreach (BgfSlide slide in document.Slides)
		{
			if (!slide.IsMaster && slide.OwnerElement >= 0 && slide.OwnerElement < stated.Length)
			{
				stated[slide.OwnerElement] = 1;
			}
		}

		playback.Step(stated, null, 0L, null, now =>
		{
			foreach (int meter in meters)
			{
				playback.GoToTime(meter, time, now);
			}
		});
		float[] values = playback.Values;
		float x = 1f, y = 1f;
		int steps = 0;
		for (int at = box; at >= 0 && at < document.Elements.Length && steps < MaxChain; ++steps)
		{
			BgfElement element = document.Elements[at];
			if (Read(element, ScaleX) is { } fileX)
			{
				float sx = Played(values, at, Engine_UiSlidePlayback.Channel.ScaleX) ?? fileX;
				float sy = Played(values, at, Engine_UiSlidePlayback.Channel.ScaleY) ?? Read(element, ScaleY) ?? sx;
				x *= sx;
				y *= sy;
			}

			at = element.ParentOrdinal;
		}

		return (x * ExtentScale, y * ExtentScale);
	}

	/// <summary>The extent's scale to the box's units (flt_8206D228, splatted at 0x8227AF70).</summary>
	public const float ExtentScale = 100f;

	private static float? Played(float[] values, int ordinal, Engine_UiSlidePlayback.Channel channel)
	{
		int at = (ordinal * Engine_UiSlidePlayback.Channels) + (int)channel;
		return at < values.Length && !float.IsNaN(values[at]) ? values[at] : null;
	}

	private static float? Read(BgfElement element, uint id) =>
		!element.TryGetAttribute(id, out BgfAttribute attribute) ? null
		: attribute.Type == BgfValueType.Float ? attribute.AsFloat()
		: attribute.Type is BgfValueType.Int1 or BgfValueType.Int2 ? attribute.AsInt()
		: null;
}
