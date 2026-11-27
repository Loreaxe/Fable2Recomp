namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using System.Globalization;
using Refii.Ui;

/// <summary>
/// What Fable 3's HUD writers STATE, as hud_hooks.cpp and interaction_hooks.cpp post it: the slide
/// registries' names and slides, the script-placed elements, the d-pad's slide and slots and the
/// interaction info panel. <see cref="Fable3HudDocument"/> folds these over its document instead of
/// the element writes the writers make. Pure — no Godot type — so the fold is held by tests.
/// </summary>
/// <remarks>
/// <para>THE SLIDE REGISTRIES. A writer addresses a NAME (the FNV-1 of an element's `SlideToggleName`
/// / `SlideSetName` / `SlideFaderName` / `SlideFader2Name` value) and sets every element filed under
/// it: SlideSet and SlideFader2 to the slide given (sub_8218D8A0), SlideToggle to 2 on / 1 off
/// (sub_82921200), SlideFader to the slide given (sub_82B4FB28). `@Hud.Slide(attribute, hash,
/// slide)` is that write; the document lands it on every element its bindings put under the name.
/// The HUD reset (sub_825C1D98) rests every SlideFader element at 2 and every SlideFader2 element at
/// 1 (`@Hud.SlideReset`); a name an unfiling or the registry's destruction drops
/// (`@Hud.Unfiled`, `@Hud.Destroyed`) states nothing any more, so its elements rest on slide 1.</para>
/// <para>SCRIPT POSITIONS. `@Hud.Place(hash, x, y)` (sub_82B4FD30) moves every `ScriptPositioned`
/// element under the name to (x, y) in the .bgf's units, its own z kept; `@Hud.Unplace(hash)`
/// (sub_82B4FE18) puts them back where the file authors them.</para>
/// <para>THE D-PAD. `@Hud.Dpad.Slide(player, s)` (sub_825D8B30: `dpad` / `dpad2` + `.dpad.dpadmenu`
/// state s, 2 open, 1 closed) and `@Hud.Dpad.Slot(player, slot, texture, count, flash)`
/// (sub_825D8988): the slot's icon `group.&lt;dir&gt;.&lt;dir&gt;` takes the texture, its
/// `group.&lt;dir&gt;.Counter.Counter` the count in decimal (empty at 0), and slot 0's
/// `group.up.fx` opacity 100 while flashing, else 0. Slots 0..3 are up, left, down, right.</para>
/// <para>THE INFO PANEL. `@Interaction.Panel(id)`: the interaction mode's IM+64, the state of
/// `LowerElements.InfoBox` in `interactionmode.bgf` (the screen tick's write, 0x825C3B14).</para>
/// </remarks>
public static class Fable3HudRegistry
{
	/// <summary>The document each path statement belongs to, by its name in the banks.</summary>
	public const string HudDocument = "hud";
	public const string InteractionDocument = "interactionmode";

	/// <summary>The d-pad's slot directions, slot order (0x825D8498).</summary>
	public static readonly string[] DpadDirections = { "up", "left", "down", "right" };

	private static readonly Dictionary<(Engine_UiBindings.Kind Kind, uint Hash), Fable3HudStatements.Statement> Slides = new();
	private static readonly Dictionary<Engine_UiBindings.Kind, Fable3HudStatements.Statement> Resets = new();
	private static readonly Dictionary<uint, (float X, float Y)> PlacedNames = new();
	private static readonly Dictionary<(string Document, string Path, int Channel), Fable3HudStatements.Statement> Paths = new();
	private static readonly Dictionary<uint, Fable3HudStatements.Statement> Texts = new();
	private static readonly Dictionary<uint, Fable3HudStatements.Statement> Images = new();
	private static readonly Dictionary<uint, (uint Box, string Text)> LineTexts = new();
	private static readonly Dictionary<uint, (uint Hash, string Text)[]> BoxLineLists = new();

	private const int ChannelState = 0;
	private const int ChannelText = 1;
	private const int ChannelTexture = 2;
	private const int ChannelOpacity = 3;

	/// <summary>Every name the script placed, at its stated (x, y).</summary>
	public static IReadOnlyDictionary<uint, (float X, float Y)> Placed => PlacedNames;

	/// <summary>The slide-registry kind an attribute spells, or null for one this model does not take.</summary>
	public static Engine_UiBindings.Kind? KindOf(string attribute) => attribute switch
	{
		"SlideToggleName" => Engine_UiBindings.Kind.SlideToggle,
		"SlideSetName" => Engine_UiBindings.Kind.SlideSet,
		"SlideFaderName" => Engine_UiBindings.Kind.SlideFader,
		"SlideFader2Name" => Engine_UiBindings.Kind.SlideFader2,
		_ => null,
	};

	/// <summary>
	/// The attributes whose elements the registry writers state: an element statement filed under
	/// one of them (`Attribute:value`) is the writer's own write seen again, and is not folded.
	/// </summary>
	public static bool IsStatedAttribute(string attribute) =>
		KindOf(attribute) is not null || attribute is "TextName" or "MeterName" or ScriptPositionedAttribute;

	/// <summary>The attribute the script-placed elements are filed by (registry 13, vtable 0x820A4A80).</summary>
	public const string ScriptPositionedAttribute = "ScriptPositioned";

	/// <summary>Every statement for <paramref name="document"/>: the registry slides (any document's names) and its own paths.</summary>
	public static IEnumerable<Fable3HudStatements.Statement> StatementsFor(string document)
	{
		foreach (Fable3HudStatements.Statement reset in Resets.Values)
		{
			yield return reset;
		}

		foreach (Fable3HudStatements.Statement slide in Slides.Values)
		{
			yield return slide;
		}

		foreach (Fable3HudStatements.Statement text in Texts.Values)
		{
			yield return text;
		}

		foreach (Fable3HudStatements.Statement image in Images.Values)
		{
			yield return image;
		}

		foreach (KeyValuePair<(string Document, string Path, int Channel), Fable3HudStatements.Statement> entry in Paths)
		{
			if (entry.Key.Document == document)
			{
				yield return entry.Value;
			}
		}
	}

	public static void Register(Engine_UiGuiNatives router)
	{
		router.Register("@Hud.Slide", call =>
			Slide(call.Argument(0).Text, Parse(call.Argument(1).Text), (int)call.Argument(2).Number));
		router.Register("@Hud.SlideReset", call => SlideReset(call.Argument(0).Text, (int)call.Argument(1).Number));
		router.Register("@Hud.Unfiled", call => Unfiled(call.Argument(0).Text, Parse(call.Argument(1).Text)));
		router.Register("@Hud.Destroyed", call => Destroyed(call.Argument(0).Text));
		router.Register("@Hud.Place", call =>
			Place(Parse(call.Argument(0).Text), call.Argument(1).Number, call.Argument(2).Number));
		router.Register("@Hud.Unplace", call => Unplace(Parse(call.Argument(0).Text)));
		router.Register("@Hud.Dpad.Slide", call => DpadSlide((int)call.Argument(0).Number, (int)call.Argument(1).Number));
		router.Register("@Hud.Dpad.Slot", call => DpadSlot(
			(int)call.Argument(0).Number, (int)call.Argument(1).Number, call.Argument(2).Text,
			(int)call.Argument(3).Number, call.Argument(4).Boolean));
		router.Register("@Interaction.Panel", call => InteractionPanel((int)call.Argument(0).Number));
	}

	/// <summary>A registry writer set every element under the name to <paramref name="slide"/>.</summary>
	public static void Slide(string attribute, uint hash, int slide)
	{
		if (KindOf(attribute) is not { } kind)
		{
			return;
		}

		Slides[(kind, hash)] = new Fable3HudStatements.Statement(
			string.Empty, slide, null, null, null, Fable3Elements.NextStamp(), Kind: kind, NameHash: hash);
	}

	/// <summary>Every element of the attribute's registry rests on <paramref name="slide"/>.</summary>
	public static void SlideReset(string attribute, int slide)
	{
		if (KindOf(attribute) is not { } kind)
		{
			return;
		}

		DropKind(kind);
		Resets[kind] = new Fable3HudStatements.Statement(
			string.Empty, slide, null, null, null, Fable3Elements.NextStamp(), Kind: kind, EveryName: true);
	}

	/// <summary>The registry dropped the name: its elements rest on their first slide again.</summary>
	public static void Unfiled(string attribute, uint hash)
	{
		if (attribute == ScriptPositionedAttribute)
		{
			PlacedNames.Remove(hash);
		}
		else if (KindOf(attribute) is { } kind)
		{
			Slides.Remove((kind, hash));
		}

		Fable3Elements.NextStamp();
	}

	/// <summary>The registry is gone, and every name it held with it.</summary>
	public static void Destroyed(string attribute)
	{
		if (attribute == ScriptPositionedAttribute)
		{
			PlacedNames.Clear();
		}
		else if (KindOf(attribute) is { } kind)
		{
			DropKind(kind);
			Resets.Remove(kind);
		}

		Fable3Elements.NextStamp();
	}

	public static void Place(uint hash, float x, float y)
	{
		PlacedNames[hash] = (x, y);
		Fable3Elements.NextStamp();
	}

	public static void Unplace(uint hash)
	{
		PlacedNames.Remove(hash);
		Fable3Elements.NextStamp();
	}

	/// <summary>The root a player's d-pad is written under (sub_825D8B30: dword_83558D1C / D20).</summary>
	public static string DpadRoot(int player) => player == 0 ? "dpad" : "dpad2";

	public static void DpadSlide(int player, int slide) =>
		SetPath(HudDocument, DpadRoot(player) + ".dpad.dpadmenu", ChannelState, state: slide);

	public static void DpadSlot(int player, int slot, string texture, int count, bool flash)
	{
		if (slot < 0 || slot >= DpadDirections.Length)
		{
			return;
		}

		string group = DpadRoot(player) + ".dpad.dpadmenu.group." + DpadDirections[slot];
		SetPath(HudDocument, group + "." + DpadDirections[slot], ChannelTexture, texture: texture);
		SetPath(HudDocument, group + ".Counter.Counter", ChannelText,
			text: count > 0 ? count.ToString(CultureInfo.InvariantCulture) : string.Empty);
		if (slot == 0)
		{
			SetPath(HudDocument, group + ".fx", ChannelOpacity, opacity: flash ? 100f : 0f);
		}
	}

	public static void InteractionPanel(int panel)
	{
		StatedPanel = panel;
		DrawPanel();
	}

	/// <summary>The info panel's id as the guest last stated it (`@Interaction.Panel`), 1 before any.</summary>
	public static int StatedPanel { get; private set; } = 1;

	/// <summary>
	/// The info box's slide the document draws: the stated panel, except the shop page (2) while
	/// <see cref="Fable3ShopPage"/> draws it itself — the document then rests it closed (1).
	/// </summary>
	public static int DrawnPanel(int stated, bool shopPageOwned) =>
		stated == Fable3ShopPageRules.ShopPagePanel && shopPageOwned ? 1 : stated;

	/// <summary>Writes the drawn panel to the document; again whenever the shop page's ownership changes.</summary>
	public static void DrawPanel() =>
		SetPath(InteractionDocument, "LowerElements.InfoBox", ChannelState, state: DrawnPanel(StatedPanel, Fable3ShopPageRules.Owned));

	/// <summary>
	/// A text the guest wrote under a text NAME through a call that carries it — the epilogue's
	/// title and description (Epilogue_Show's r4 / r5 under `*dword_8355AC24` / `*dword_8355AC28`) —
	/// landed on every element the document binds under the name, as the text registry would.
	/// </summary>
	public static void StateText(uint nameHash, string text) =>
		Texts[nameHash] = new Fable3HudStatements.Statement(
			string.Empty, null, null, text, null, Fable3Elements.NextStamp(), Kind: Engine_UiBindings.Kind.Text, NameHash: nameHash);

	/// <summary>
	/// A texture the guest set by its hash on every element filed under an `ImageName` (registry
	/// 12) — the epilogue's screenshot (`@Epilogue.Image`, end_screen_hooks.cpp).
	/// </summary>
	public static void StateImage(uint nameHash, uint textureHash) =>
		Images[nameHash] = new Fable3HudStatements.Statement(
			string.Empty, null, null, null, null, Fable3Elements.NextStamp(), Kind: Engine_UiBindings.Kind.Image,
			NameHash: nameHash, TextureHash: textureHash);

	/// <summary>
	/// A text box line as sub_82920BC8's caller passed it (`@Hud.Lines`): the line's TextName shows
	/// it when the text registry's own wrapped write (hud_feed.cpp) said nothing — an unfiled box
	/// writes nothing (no R7 record, 0x82920BE4).
	/// </summary>
	public static void StateLine(uint box, uint lineHash, string text)
	{
		LineTexts[lineHash] = (box, text);
		Fable3Elements.NextStamp();
	}

	/// <summary>
	/// A text box's whole line vector as its caller passed sub_82920BC8 (`@Hud.Lines`), in order —
	/// what the box's fit stacks (Fable3TextBoxes). An empty vector leaves the box's last one:
	/// sub_82920278 returns before the fit (0x829202B4).
	/// </summary>
	public static void StateBoxLines(uint box, (uint Hash, string Text)[] lines)
	{
		if (lines.Length != 0)
		{
			BoxLineLists[box] = lines;
		}
	}

	/// <summary>The box's last stated line vector, or null before its caller stated one.</summary>
	public static IReadOnlyList<(uint Hash, string Text)>? BoxLines(uint box) =>
		BoxLineLists.TryGetValue(box, out (uint Hash, string Text)[]? lines) ? lines : null;

	/// <summary>The stated line under a text name hash, or null.</summary>
	public static string? Line(uint lineHash) => LineTexts.TryGetValue(lineHash, out (uint Box, string Text) line) ? line.Text : null;

	/// <summary>Forgets every statement (a new session).</summary>
	public static void Clear()
	{
		StatedPanel = 1;
		Texts.Clear();
		Images.Clear();
		LineTexts.Clear();
		BoxLineLists.Clear();
		Slides.Clear();
		Resets.Clear();
		PlacedNames.Clear();
		Paths.Clear();
	}

	private static void SetPath(
		string document, string path, int channel, int? state = null, string? text = null, string? texture = null,
		float? opacity = null)
	{
		Paths[(document, path, channel)] = new Fable3HudStatements.Statement(
			path, state, opacity, text, null, Fable3Elements.NextStamp(), Texture: texture);
	}

	private static void DropKind(Engine_UiBindings.Kind kind)
	{
		var drop = new List<(Engine_UiBindings.Kind, uint)>();
		foreach ((Engine_UiBindings.Kind Kind, uint Hash) key in Slides.Keys)
		{
			if (key.Kind == kind)
			{
				drop.Add(key);
			}
		}

		foreach ((Engine_UiBindings.Kind, uint) key in drop)
		{
			Slides.Remove(key);
		}
	}

	private static uint Parse(string hex) =>
		uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value) ? value : 0u;
}
