namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Ui;

/// <summary>
/// The Gameface elements Fable 3's C++ writes, by DOCUMENT and dotted path, as its hooks state
/// them. The screens' Godot views read their rows from here; the guest's own presentation never
/// renders.
/// </summary>
/// <remarks>
/// <para>KEYED BY DOCUMENT, NOT BY PRESENTATION. Every .bgf is loaded through one function,
/// sub_825C7A90(slot, name), and ui_stated_hooks.cpp states each load as
/// `@Presentation.Load(pres, file)`: a statement's presentation is turned into its document
/// (`hud`, `interactionmode`, `options`, …) the moment it lands, and a view asks for its
/// document — never for an address it would have to wait for. A reload of a document forgets
/// what its previous presentation was told. The path is the file's own (the resolver's spelling,
/// joined to the document's ordinals by <see cref="Fable3HudStatements"/>).</para>
///
/// <para>ONLY STATED. Every value here is a writer's argument as its caller passed it (`@Ui.*`,
/// the surface statements of <see cref="Fable3UiStatements"/>); no setter's after-the-fact report
/// (`@Element.*`) has a route.</para>
/// </remarks>
public static class Fable3Elements
{
	/// <summary>One element's last stated values.</summary>
	public sealed class Element
	{
		/// <summary>The slide its writer passed (`@Ui.State`, a surface statement), null until one did.</summary>
		public int? State { get; set; }

		/// <summary>An outright opacity a writer's call stated (`@Ui.Opacity`, a surface statement), null until one did.</summary>
		public float? Opacity { get; set; }

		/// <summary>The visibility toggle's request (sub_82819AE0's bool), null until asked or after an outright opacity.</summary>
		public bool? Visible { get; set; }

		/// <summary>The text a writer passed — a literal, a tag hash or a tag name — or null when none stated one.</summary>
		public StatedText? Stated { get; set; }

		/// <summary>
		/// The slide a writer passed at its entry (`@Ui.State`: the path setters, the interaction
		/// mode tick's root, the interaction bars' show), null until one did.
		/// </summary>
		public int? StatedState { get; set; }

		/// <summary>
		/// The stamp of the latest `@Ui.State`: each one is an entry of the slide (the guest's funnel
		/// restarts it even when it is the same), so a playback re-enters on a newer stamp.
		/// </summary>
		public ulong StatedStateStamp { get; set; }

		/// <summary>
		/// How many `@Ui.State` entries the path took since its document loaded: the entry the scroll
		/// gate's `ui-reached:` names (ui_reader_hooks.cpp counts the same posts).
		/// </summary>
		public int Entries { get; set; }

		/// <summary>The active flag as its setter was asked (`@Ui.Active`, sub_828196E0), null until it was.</summary>
		public bool? StatedActive { get; set; }

		/// <summary>The texture path the setter was called with (`@Ui.Texture`), null until one was.</summary>
		public string? StatedTexture { get; set; }

		/// <summary>An outright opacity a surface's own writer passed (the interaction button's 100 / 50), null until one did.</summary>
		public float? StatedOpacity { get; set; }

		/// <summary>
		/// The opacity the stated channels alone draw the element at: 0 asked inactive (flag 0x2000
		/// off skips the whole subtree) or hidden by the visibility toggle (sub_82819AE0), else the
		/// stated outright opacity, else null for the authored — a show restores what a hide divided
		/// away (0x82819B44..0x82819B64).
		/// </summary>
		public float? StatedDrawnOpacity => StatedActive == false || Visible == false ? 0f : StatedOpacity;

		/// <summary>The stamp of the latest opacity write (`@Ui.Opacity`, `@Ui.Visible`, a surface's outright opacity), 0 for none.</summary>
		public ulong OpacityStamp { get; set; }

		/// <summary>The stamp of the latest `@Ui.Active`, 0 for none.</summary>
		public ulong ActiveStamp { get; set; }

		/// <summary>
		/// <see cref="StatedDrawnOpacity"/>'s parts with their stamps, for a playback to retire what a
		/// later told slide entry wrote over (<see cref="Fable3HudStatements.OpacitySource"/>).
		/// </summary>
		public Fable3HudStatements.OpacitySource OpacitySource =>
			new(Visible == false ? 0f : StatedOpacity, OpacityStamp, StatedActive == false, ActiveStamp);

		/// <summary>Stamps an opacity write made by the statement now landing (its stamp is the next one).</summary>
		public void MarkOpacity() => OpacityStamp = Generation + 1;

		/// <summary>The stated text resolved (a literal, or the tag's text in the title's book), or null.</summary>
		public string? StatedTextValue => Stated is { } stated ? Resolve(stated) : null;

		/// <summary>A meter's timeline position as its writer passed it (`@Ui.Time`: fraction × 1000).</summary>
		public float? Time { get; set; }

		/// <summary>
		/// The element's local `position.x/.y/.z` as a stated call passed them (`@Ui.Position`: the
		/// credits roll's SetPosition sites), the .bgf's own units, null until one did.
		/// </summary>
		public (float X, float Y, float Z)? Position { get; set; }

		/// <summary>The texture path stated for the element (`@Ui.Texture`, a surface statement), null until one was.</summary>
		public string? Texture { get; set; }

		/// <summary>
		/// The renderable's diffuse as the colour setter was called with it (`@Ui.Colour`,
		/// sub_828190E8): r, g, b, a in 0..1; null until one was — the file's colour stands.
		/// </summary>
		public (float R, float G, float B, float A)? Colour { get; set; }

		public uint Address { get; set; }

		/// <summary>The <see cref="Generation"/> of the element's latest statement: which of two statements is the later.</summary>
		public ulong Stamp { get; set; }

		/// <summary>
		/// Hidden, as the visibility toggle leaves it: a hide divides the opacity by 1024 and a show
		/// multiplies one below 0.09765625 (flt_832A18DC) by 1024 (sub_82819AE0, 0x82819B38..0x82819B64),
		/// so asked-hidden is hidden, asked-shown is hidden only over an outright 0, and an
		/// element never toggled is hidden at or under the threshold.
		/// </summary>
		public bool Hidden => Visible switch
		{
			false => true,
			true => Opacity is { } o && o * 1024f <= HiddenOpacity,
			null => Opacity is { } o && o <= HiddenOpacity,
		};

		/// <summary>The document and path this element is held under.</summary>
		internal (string Document, string Path)? Key { get; set; }
	}

	/// <summary>A text as a writer stated it: exactly one of the three is set.</summary>
	public readonly record struct StatedText(string? Literal, uint? Tag, string? TagName, string Via);

	/// <summary>The visibility toggle's threshold, flt_832A18DC (sub_82819AE0 at 0x82819B38).</summary>
	public const float HiddenOpacity = 0.09765625f;

	/// <summary>hud.bgf's document (sub_825C18C0 loads "HUD.bgf").</summary>
	public const string HudDocument = "hud";

	/// <summary>InteractionMode.bgf's document (sub_825C42C0 loads "InteractionMode.bgf").</summary>
	public const string InteractionDocument = "interactionmode";

	private static readonly Dictionary<(string Document, string Path), Element> ByPath = new();
	private static readonly Dictionary<uint, Element> ByAddress = new();
	private static readonly Dictionary<uint, string> Documents = new();
	private static readonly HashSet<string> Reported = new();
	private const int MaxReported = 600;

	/// <summary>Where the census writes (GD.Print in the game; a test's own sink).</summary>
	public static System.Action<string> Log { get; set; } = message => GD.Print(message);

	/// <summary>A tag hash's text in the title's book (Fable3Strings), or null.</summary>
	public static System.Func<uint, string?> TagText { get; set; } = DefaultTagText;

	/// <summary>A tag name's text in the title's book, or null.</summary>
	public static System.Func<string, string?> TagNameText { get; set; } = DefaultTagNameText;

	/// <summary>Bumped on every statement, so a view redraws only when something changed.</summary>
	public static ulong Generation { get; private set; }

	/// <summary>
	/// The next stamp on the one clock every Fable 3 UI statement is ordered by — the element
	/// writes here and the registry writers' (<see cref="Fable3HudRegistry"/>) — so a document
	/// folding both applies them in the order the guest made them.
	/// </summary>
	internal static ulong NextStamp() => ++Generation;

	/// <summary>
	/// THE HUD ROOT'S STATE as its writers state it (`@Hud.Root`, screen_stated_hooks.cpp): HIDE
	/// sub_825C1CB0 2 (every box's Show), SHOW sub_825C1D28 1 (the boxes' hides, the HUD tick's
	/// enable, the cutscene rules leaving bit 27), the cutscene rules sub_8249A030 3 (bit 27 kept).
	/// Null until one spoke. Not the interaction mode's tick sub_825C3A78 — that writes
	/// InteractionMode.bgf's root (IM+0), not hud.bgf's.
	/// </summary>
	public static int? HudRootState { get; private set; }

	/// <summary>The HUD is hidden: its root on state 2 (hud.bgf's `GameToPauseMenu` slide fades every widget).</summary>
	public static bool HudHidden => HudRootState == 2;

	/// <summary>The document under each root segment that most recently stated an element there.</summary>
	private static readonly Dictionary<string, string> LatestByRoot = new(System.StringComparer.Ordinal);

	/// <summary>A presentation's document: its load's file stem, lower-cased; `#address` before a load named it; empty for 0.</summary>
	public static string DocumentOf(uint presentation) =>
		presentation == 0u ? string.Empty
		: Documents.TryGetValue(presentation, out string? document) ? document
		: "#" + presentation.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>
	/// The document a statement's presentation argument names: `=hud` (the native names the
	/// document once it saw the load — ui_stated_hooks.cpp's PresentationKey), else the address in
	/// hex through the loads this model heard.
	/// </summary>
	public static string DocumentOfKey(string key) =>
		key.StartsWith('=') ? key[1..] : DocumentOf(Parse(key));

	/// <summary>The document a load's file names: `HUD.bgf` and `…\gameface\Options.bgf` are `hud` and `options`.</summary>
	public static string DocumentName(string file)
	{
		string name = file.Replace('\\', '/');
		int slash = name.LastIndexOf('/');
		name = slash >= 0 ? name[(slash + 1)..] : name;
		int dot = name.LastIndexOf('.');
		return (dot > 0 ? name[..dot] : name).ToLowerInvariant();
	}

	/// <summary>The document that last wrote under <paramref name="root"/>, or null.</summary>
	public static string? LatestDocumentOf(string root) =>
		LatestByRoot.TryGetValue(root, out string? document) ? document : null;

	/// <summary>The element at a path in the root's latest document, or null.</summary>
	public static Element? Latest(string path)
	{
		int dot = path.IndexOf('.');
		return LatestDocumentOf(dot < 0 ? path : path[..dot]) is { } document ? Get(document, path) : null;
	}

	/// <summary>The element at a path in a document, or null when nothing was stated for it.</summary>
	public static Element? Get(string document, string path) =>
		ByPath.TryGetValue((document, path), out Element? element) ? element : null;

	/// <summary>Every stated element with a path, across documents.</summary>
	public static IEnumerable<KeyValuePair<(string Document, string Path), Element>> All => ByPath;

	/// <summary>Every stated element of a document.</summary>
	public static IEnumerable<KeyValuePair<(string Document, string Path), Element>> Of(string document)
	{
		foreach (KeyValuePair<(string Document, string Path), Element> entry in ByPath)
		{
			if (entry.Key.Document == document)
			{
				yield return entry;
			}
		}
	}

	/// <summary>Forgets everything (a new session; the tests).</summary>
	public static void Clear()
	{
		ByPath.Clear();
		ByAddress.Clear();
		Documents.Clear();
		LatestByRoot.Clear();
		Reported.Clear();
		HudRootState = null;
		++Generation;
	}

	/// <summary>Routes the element statements into the model.</summary>
	private static readonly Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>> Routes = BuildRoutes();

	/// <summary>Routes this model's statements from the natives router.</summary>
	public static void Register(Engine_UiGuiNatives router)
	{
		foreach (KeyValuePair<string, System.Action<Engine_UiModel.GuiNativeCall>> route in Routes)
		{
			System.Action<Engine_UiModel.GuiNativeCall> handler = route.Value;
			router.Register(route.Key, call => handler(call));
		}
	}

	/// <summary>Takes one statement as the router would (the tests' entry); false when it is not this model's.</summary>
	public static bool Take(Engine_UiModel.GuiNativeCall call)
	{
		if (!Routes.TryGetValue(call.Name, out System.Action<Engine_UiModel.GuiNativeCall>? handler))
		{
			return false;
		}

		handler(call);
		return true;
	}

	private static Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>> BuildRoutes()
	{
		var routes = new Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>>(System.StringComparer.Ordinal);
		void Add(string name, System.Action<Engine_UiModel.GuiNativeCall> handler) => routes[name] = handler;

		Add("@Presentation.Load", call => Load(Parse(call.Argument(0).Text), call.Argument(1).Text));

		// The writers' values at entry: what is drawn.
		Add("@Ui.Text", call => Apply(call, (element, value) => SetStated(element, new StatedText(value.Text, null, null, Via(call)))));
		Add("@Ui.TextTag", call => Apply(call, (element, value) => SetStated(element, new StatedText(null, Parse(value.Text), null, Via(call)))));
		Add("@Ui.TextName", call => Apply(call, (element, value) => SetStated(element, new StatedText(null, null, value.Text, Via(call)))));
		// A position the guest's own call states (the credits roll's SetPosition sites,
		// headless_commit_hooks.cpp): (pres, elem, path, x, y, z, via).
		Add("@Ui.Position", call => Apply(call, (element, value) =>
			element.Position = (value.Number, call.Argument(4).Number, call.Argument(5).Number)));
		Add("@Ui.Visible", call => Apply(call, (element, value) =>
		{
			element.Visible = value.Boolean;
			element.MarkOpacity();
		}));
		Add("@Ui.State", call => Apply(call, (element, value) =>
		{
			Slide(element, (int)value.Number);
			++element.Entries;
		}));
		// An outright opacity a writer's own call passes (`@Ui.Opacity`, ui_stated_hooks.cpp: the
		// multiplier roots' shown 100 / hidden 0, sub_82950AF0 at 0x82950B40; the layer fade
		// sub_82B14A50 at 0x82B14B28): it overrides the visibility toggle, as SetOpacity does.
		Add("@Ui.Opacity", call => Apply(call, (element, value) =>
		{
			element.StatedOpacity = value.Number;
			element.Opacity = value.Number;
			element.Visible = null;
			element.MarkOpacity();
		}));
		Add("@Ui.Active", call => Apply(call, (element, value) =>
		{
			element.StatedActive = value.Boolean;
			element.ActiveStamp = Generation + 1;
		}));
		// (pres, elem, path, r, g, b, a, via), 0..255 (ui_stated_hooks.cpp, sub_828190E8).
		Add("@Ui.Colour", call => Apply(call, (element, red) => element.Colour = (
			red.Number / 255f, call.Argument(4).Number / 255f, call.Argument(5).Number / 255f, call.Argument(6).Number / 255f)));
		// A meter by path, its fraction × 1000 at the call (sub_8281AD48, ui_stated_hooks.cpp).
		Add("@Ui.Time", call => Apply(call, (element, value) => element.Time = value.Number));
		Add("@Ui.Texture", call => Apply(call, (element, value) =>
		{
			element.StatedTexture = value.Text;
			element.Texture = value.Text;
		}));

		return routes;
	}

	/// <summary>`@Presentation.Load(pres, file)`: the address is that document's now, and the document starts clean.</summary>
	public static void Load(uint presentation, string file)
	{
		if (presentation == 0u || file.Length == 0)
		{
			return;
		}

		string document = DocumentName(file);
		Documents[presentation] = document;
		Fable3LuaContracts.Forget(document);
		var drop = new List<(string, string)>();
		foreach (KeyValuePair<(string Document, string Path), Element> entry in ByPath)
		{
			if (entry.Key.Document == document)
			{
				drop.Add(entry.Key);
				if (entry.Value.Address != 0u)
				{
					ByAddress.Remove(entry.Value.Address);
				}
			}
		}

		foreach ((string, string) key in drop)
		{
			ByPath.Remove(key);
		}

		NextStamp();
		Log($"[fable3-elements] {presentation:X8} loads '{file}' as document '{document}'" +
			(drop.Count == 0 ? string.Empty : $" ({drop.Count} element(s) of its previous load forgotten)"));
	}

	/// <summary>`@Hud.Root(state, via)`: the root's state, landed on hud.bgf's root element too.</summary>
	public static void StateHudRoot(int state, string via)
	{
		HudRootState = state;
		StateSlide(HudDocument, Fable3HudStatements.RootPath, state);
		Log($"[fable3-elements] HUD root state = {state} ({(state == 2 ? "hidden" : state == 3 ? "ambient cutscene" : "shown")}) by sub_{via}");
	}

	/// <summary>
	/// A surface statement's write onto a document's path (<see cref="Fable3UiStatements"/>): the
	/// element made when the path was never stated, stamped like every other statement.
	/// </summary>
	public static Element State(string document, string path, System.Action<Element> set)
	{
		Element element = ElementAt(document, path, 0u);
		set(element);
		element.Stamp = NextStamp();
		return element;
	}

	/// <summary>
	/// A surface statement's SLIDE onto a document's path: an entry of the slide under its own stamp
	/// (the guest's funnel sub_82268BE0 enters a slide on every write, a repeat included).
	/// </summary>
	public static Element StateSlide(string document, string path, int slide) =>
		State(document, path, element => Slide(element, slide));

	private static void Slide(Element element, int slide)
	{
		element.StatedState = slide;
		element.State = slide;
		element.StatedStateStamp = Generation + 1;
	}

	private static Element ElementAt(string document, string path, uint address)
	{
		if (!ByPath.TryGetValue((document, path), out Element? element))
		{
			element = new Element { Address = address, Key = (document, path) };
			ByPath[(document, path)] = element;
		}

		int dot = path.IndexOf('.');
		LatestByRoot[dot < 0 ? path : path[..dot]] = document;
		return element;
	}

	private static string Via(Engine_UiModel.GuiNativeCall call) => call.Argument(4).Text;

	private static void SetStated(Element element, StatedText stated) => element.Stated = stated;

	private static string? Resolve(StatedText stated) =>
		stated.Literal ?? (stated.Tag is { } tag ? TagText(tag) : stated.TagName is { } name ? TagNameText(name) : null);

	private static string? DefaultTagText(uint tag) =>
		Fable3Strings.Current is { } strings && strings.TryGetString(tag, out string text) ? text : null;

	private static string? DefaultTagNameText(string name) =>
		name.Length == 0 ? string.Empty : Engine_UiGuiText.Resolve(name, Fable3Strings.Current);

	/// <summary>The guest wraps lines with U+000A and the book's strings carry their own breaks: compared without whitespace.</summary>
	internal static string Normalise(string text)
	{
		var builder = new System.Text.StringBuilder(text.Length);
		foreach (char c in text)
		{
			if (!char.IsWhiteSpace(c))
			{
				builder.Append(c);
			}
		}

		return builder.ToString();
	}

	private static void Apply(Engine_UiModel.GuiNativeCall call, System.Action<Element, Engine_UiModel.GuiNativeArgument> set)
	{
		uint address = Parse(call.Argument(1).Text);
		string path = call.Argument(2).Text;
		Engine_UiModel.GuiNativeArgument value = call.Argument(3);
		string document = DocumentOfKey(call.Argument(0).Text);

		// One element under every name it was stated by: the address first (a registry key and the
		// resolver's later path are the same element), else the path.
		Element? element = null;
		if (address != 0u && ByAddress.TryGetValue(address, out Element? known))
		{
			element = known;
		}

		if (element is null && path.Length > 0 && ByPath.TryGetValue((document, path), out Element? named))
		{
			element = named;
		}

		if (element is null)
		{
			element = new Element { Address = address };
			if (address != 0u)
			{
				ByAddress[address] = element;
			}
		}

		if (path.Length > 0)
		{
			ByPath[(document, path)] = element;
			element.Key ??= (document, path);
			int dot = path.IndexOf('.');
			LatestByRoot[dot < 0 ? path : path[..dot]] = document;
		}

		set(element, value);
		element.Stamp = NextStamp();

		if (Reported.Count < MaxReported && Reported.Add(document + ":" + (path.Length > 0 ? path : address.ToString("X8"))))
		{
			string shown = value.Kind == Engine_UiModel.GuiNativeArgumentKind.String ? $"'{value.Text}'" : value.Text;
			Log($"[fable3-elements] {document} {(path.Length > 0 ? path : "<unnamed " + address.ToString("X8") + ">")} {call.Name[(call.Name.IndexOf('.') + 1)..]} = {shown}");
		}
	}

	internal static uint Parse(string hex) =>
		uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint value) ? value : 0u;
}
