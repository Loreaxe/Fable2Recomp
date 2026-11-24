using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// THE SLIDES THE GUEST TELLS ITS PRESENTATIONS, by element: every explicit `GotoSlide`
/// (`sub_82BB5760`), play / pause (`sub_82BB5CB8`), attribute, `active`, text and texture write as
/// gui_element_hooks.cpp queues them (taken by <see cref="Take"/>; a headless presentation's keyed
/// to its file through <see cref="Headless"/>) —
/// native screen code, the GUI registries, Lua `goToSlide`, a file's Logic commands, the page
/// arrows — keyed by presentation address and element ordinal. A book reads them through
/// <see cref="Engine_UiSlidePlayback"/>, which enters the slide and plays or pauses it as the
/// guest's time interface does.
///
/// Statements carry one global sequence so the playback can tell a statement made after a
/// component came live from one the walk's own entry superseded. A presentation rebuilt at the
/// same address starts clean: a different root name hash, or — for a headless presentation —
/// a different serial (each create states a new one, gui_headless_presentation.cpp), so the
/// title made again after Quit to Title never reads its predecessor's slides, texts or stamps.
/// </summary>
public static class Engine_UiSlideStatements
{
	/// <summary>The last entry and play state told to one element.</summary>
	public readonly record struct Told(int Slide, ulong Sequence, bool? Paused, ulong PauseSequence);

	/// <summary>One attribute or `active` write told to an element, in statement order.</summary>
	public readonly record struct Write(int Ordinal, uint Attribute, uint Value, bool IsActive, ulong Sequence);

	private sealed class Held
	{
		public uint RootHash;

		/// <summary>The headless presentation's serial its statements carried; 0 for a built one.</summary>
		public uint Serial;
		public readonly Dictionary<int, Told> ByOrdinal = new();
		public readonly List<Write> Writes = new();
		public readonly Dictionary<int, (string Text, ulong Sequence)> Texts = new();
		public readonly Dictionary<int, (uint Hash, ulong Sequence)> Textures = new();
	}

	/// <summary>Bumped whenever any element is told a texture.</summary>
	public static ulong TextureGeneration { get; private set; }

	/// <summary>The textures told to a presentation's elements after <paramref name="since"/>.</summary>
	public static IEnumerable<(int Ordinal, uint Hash)> TexturesSince(uint presentation, uint rootHash, ulong since)
	{
		if (presentation == 0 || !_said.TryGetValue(presentation, out Held? held) || held.RootHash != rootHash)
		{
			yield break;
		}

		foreach ((int ordinal, (uint hash, ulong sequence)) in held.Textures)
		{
			if (sequence > since)
			{
				yield return (ordinal, hash);
			}
		}
	}

	private static readonly Dictionary<uint, Held> _said = new();

	/// <summary>Writes kept per presentation; older ones are dropped first.</summary>
	private const int WriteCap = 4096;

	/// <summary>Bumped by every statement.</summary>
	public static ulong Sequence { get; private set; }

	/// <summary>
	/// A sequence for a statement Godot makes itself (a slide it states from a call's arguments):
	/// later than everything told so far, in the same order the playback reads the guest's.
	/// </summary>
	public static ulong Stamp() => ++Sequence;

	/// <summary>
	/// What <paramref name="presentation"/> holds under <paramref name="rootHash"/>, started clean
	/// when it held another root or another headless presentation's serial. A built
	/// presentation's statements carry no serial (0), which neither starts it clean nor is one.
	/// </summary>
	private static Held HeldFor(uint presentation, uint rootHash, uint serial = 0u)
	{
		if (!_said.TryGetValue(presentation, out Held? held) || held.RootHash != rootHash ||
			(serial != 0u && held.Serial != 0u && held.Serial != serial))
		{
			held = new Held { RootHash = rootHash };
			_said[presentation] = held;
		}

		if (serial != 0u)
		{
			held.Serial = serial;
		}

		return held;
	}

	private static Dictionary<int, Told> For(uint presentation, uint rootHash) => HeldFor(presentation, rootHash).ByOrdinal;

	/// <summary>`@Gui.SetAttribute pres rootHash ordinal attrId value`: Lua, Logic or native code wrote the attribute.</summary>
	public static void Attribute(uint presentation, uint rootHash, int ordinal, uint attribute, uint value) =>
		Add(HeldFor(presentation, rootHash), new Write(ordinal, attribute, value, false, ++Sequence));

	/// <summary>`@Gui.Active pres rootHash ordinal on`: the element's `active` was set.</summary>
	public static void Active(uint presentation, uint rootHash, int ordinal, bool on) =>
		Add(HeldFor(presentation, rootHash), new Write(ordinal, 0u, on ? 1u : 0u, true, ++Sequence));

	private static void Add(Held held, Write write)
	{
		if (held.Writes.Count >= WriteCap)
		{
			held.Writes.RemoveRange(0, WriteCap / 4);
		}

		held.Writes.Add(write);
	}

	/// <summary>The writes told to a presentation after <paramref name="since"/>, in order.</summary>
	public static IEnumerable<Write> WritesSince(uint presentation, uint rootHash, ulong since)
	{
		if (presentation == 0 || !_said.TryGetValue(presentation, out Held? held) || held.RootHash != rootHash)
		{
			yield break;
		}

		List<Write> writes = held.Writes;
		int start = writes.Count;
		while (start > 0 && writes[start - 1].Sequence > since)
		{
			--start;
		}

		for (int i = start; i < writes.Count; ++i)
		{
			yield return writes[i];
		}
	}

	/// <summary>`@Gui.GotoSlide pres rootHash ordinal slide`: the element enters the slide at t = 0.</summary>
	public static void GotoSlide(uint presentation, uint rootHash, int ordinal, int slide)
	{
		Dictionary<int, Told> told = For(presentation, rootHash);
		told.TryGetValue(ordinal, out Told was);
		told[ordinal] = was with { Slide = slide, Sequence = ++Sequence };
	}

	/// <summary>`@Gui.Paused pres rootHash ordinal paused`: the element's slide plays or pauses where it stands.</summary>
	public static void Paused(uint presentation, uint rootHash, int ordinal, bool paused)
	{
		Dictionary<int, Told> told = For(presentation, rootHash);
		told.TryGetValue(ordinal, out Told was);
		told[ordinal] = was with { Paused = paused, PauseSequence = ++Sequence };
	}

	private static readonly uint[] TakeBuffer = new uint[6 * 2048];

	private static Engine_UiHeadlessElements? _headless;

	/// <summary>
	/// The resolver of headless presentations' element ids (<see cref="Engine_UiHeadlessElements"/>):
	/// the bridge's stated paths against the file read from the game's mount.
	/// </summary>
	public static Engine_UiHeadlessElements Headless
	{
		get => _headless ??= new Engine_UiHeadlessElements(
			Refii.Host.HostBridge.ToldHeadlessFile,
			Refii.Host.HostBridge.ToldHeadlessElement,
			ReadDocument,
			message => Godot.GD.Print(message));
		set => _headless = value;
	}

	/// <summary>The presentation's file through the game's mount (its mods' layers included).</summary>
	private static Engine_BgfDocument? ReadDocument(string file)
	{
		if (Refii.Assets.Engine_GameAssets.Instance is not { IsMounted: true } assets)
		{
			return null;
		}

		// The guest names the file on its device (`GAME:\data\art\gui\gameface\…`): the mount is the device's root.
		string slashed = file.Replace('\\', '/');
		int device = slashed.IndexOf(':');
		if (device > 0 && slashed.IndexOf('/') is var firstSlash && (firstSlash < 0 || device < firstSlash))
		{
			slashed = slashed[(device + 1)..].TrimStart('/');
		}

		string bare = slashed.StartsWith("data/", System.StringComparison.OrdinalIgnoreCase) ? slashed[5..] : slashed;
		byte[]? bytes = assets.ReadAllBytes(slashed) ?? assets.ReadAllBytes(bare);
		if (bytes is null)
		{
			return null;
		}

		try
		{
			return Engine_BgfDocument.Parse(bytes);
		}
		catch (System.Exception e) when (e is System.IO.InvalidDataException or System.ArgumentException or
			System.IndexOutOfRangeException or System.IO.EndOfStreamException)
		{
			return null;
		}
	}

	/// <summary>
	/// The key a statement is held under: a live element's as told, a headless element's resolved to
	/// the file's root name hash and ordinal (the timeline owner's for a slide entry or a pause).
	/// </summary>
	private static bool KeyOf(uint presentation, uint root, uint ordinalWord, bool timelineOwner,
		out uint rootHash, out int ordinal)
	{
		if (!Engine_UiHeadlessElements.IsHeadless(ordinalWord))
		{
			rootHash = root;
			ordinal = (int)ordinalWord;
			return true;
		}

		return Headless.TryResolve(presentation, root, ordinalWord, timelineOwner, out rootHash, out ordinal);
	}

	/// <summary>The serial a statement's root word carries for a headless element; 0 for a built one.</summary>
	private static uint SerialOf(uint root, uint ordinalWord) =>
		Engine_UiHeadlessElements.IsHeadless(ordinalWord) ? root : 0u;

	/// <summary>One taken statement record: kind, presentation, root (a headless one's serial), ordinal word, a, b.</summary>
	public static void Apply(uint kind, uint presentation, uint root, uint ordinalWord, uint a, uint b)
	{
		if (!KeyOf(presentation, root, ordinalWord, kind is 1u or 2u, out uint rootHash, out int ordinal))
		{
			return;
		}

		HeldFor(presentation, rootHash, SerialOf(root, ordinalWord));

		switch (kind)
		{
			case 1u: GotoSlide(presentation, rootHash, ordinal, (int)a); break;
			case 2u: Paused(presentation, rootHash, ordinal, a != 0); break;
			case 3u: Attribute(presentation, rootHash, ordinal, a, b); break;
			case 4u: Active(presentation, rootHash, ordinal, a != 0); break;
			case 5u:
				HeldFor(presentation, rootHash).Textures[ordinal] = (a, ++Sequence);
				++TextureGeneration;
				break;
		}
	}

	/// <summary>One taken text: presentation, root (a headless one's serial), ordinal word, the string.</summary>
	public static void ApplyText(uint presentation, uint root, uint ordinalWord, string text)
	{
		if (KeyOf(presentation, root, ordinalWord, false, out uint rootHash, out int ordinal))
		{
			HeldFor(presentation, rootHash, SerialOf(root, ordinalWord)).Texts[ordinal] = (text, ++Sequence);
		}
	}

	/// <summary>
	/// Takes everything the guest told since the last call (HostBridge.TakeToldStatements) — once a
	/// frame, before the books stand.
	/// </summary>
	public static void Take()
	{
		int records;
		do
		{
			records = Refii.Host.HostBridge.TakeToldStatements(TakeBuffer);
			for (int r = 0; r < records; ++r)
			{
				int at = r * 6;
				if (Engine_UiStatementRecord.Recording)
				{
					Engine_UiStatementRecord.Note('S',
						$"{TakeBuffer[at]} {Engine_UiStatementRecord.FileOf(TakeBuffer[at + 1], TakeBuffer[at + 2], TakeBuffer[at + 3])} " +
						$"{TakeBuffer[at + 3] & 0x7FFFFFFFu} {TakeBuffer[at + 4]:X} {TakeBuffer[at + 5]:X}");
				}

				Apply(TakeBuffer[at], TakeBuffer[at + 1], TakeBuffer[at + 2], TakeBuffer[at + 3],
					TakeBuffer[at + 4], TakeBuffer[at + 5]);
			}
		}
		while (records * 6 == TakeBuffer.Length);

		int texts;
		do
		{
			texts = Refii.Host.HostBridge.TakeToldTexts(TextWords, TextUnits);
			int at = 0;
			for (int r = 0; r < texts; ++r)
			{
				int length = (int)TextWords[(r * 4) + 3];
				var chars = new char[length];
				for (int c = 0; c < length; ++c)
				{
					chars[c] = (char)TextUnits[at + c];
				}

				at += length;
				if (Engine_UiStatementRecord.Recording)
				{
					Engine_UiStatementRecord.Note('T',
						$"{Engine_UiStatementRecord.FileOf(TextWords[r * 4], TextWords[(r * 4) + 1], TextWords[(r * 4) + 2])} " +
						$"{TextWords[(r * 4) + 2] & 0x7FFFFFFFu} '{new string(chars)}'");
				}

				ApplyText(TextWords[r * 4], TextWords[(r * 4) + 1], TextWords[(r * 4) + 2], new string(chars));
			}
		}
		while (texts * 4 == TextWords.Length);
	}

	private static readonly uint[] TextWords = new uint[4 * 256];

	private static readonly ushort[] TextUnits = new ushort[64 * 1024];

	/// <summary>The texts told to a presentation's elements after <paramref name="since"/>, last one per element.</summary>
	public static IEnumerable<(int Ordinal, string Text)> TextsSince(uint presentation, uint rootHash, ulong since)
	{
		if (presentation == 0 || !_said.TryGetValue(presentation, out Held? held) || held.RootHash != rootHash)
		{
			yield break;
		}

		foreach ((int ordinal, (string text, ulong sequence)) in held.Texts)
		{
			if (sequence > since)
			{
				yield return (ordinal, text);
			}
		}
	}

	/// <summary>The text the guest last wrote into an element after <paramref name="since"/>, or null.</summary>
	public static string? TextOf(uint presentation, uint rootHash, int ordinal, ulong since) =>
		presentation != 0 && _said.TryGetValue(presentation, out Held? held) && held.RootHash == rootHash &&
		held.Texts.TryGetValue(ordinal, out var told) && told.Sequence > since
			? told.Text
			: null;

	/// <summary>
	/// The non-empty text last told to any element of <paramref name="presentation"/>, or null —
	/// for a presentation whose one live text is the statement (the menu cycler's screen name).
	/// </summary>
	public static string? LatestTextOf(uint presentation)
	{
		if (presentation == 0 || !_said.TryGetValue(presentation, out Held? held))
		{
			return null;
		}

		string? latest = null;
		ulong at = 0;
		foreach ((string text, ulong sequence) in held.Texts.Values)
		{
			if (text.Length != 0 && sequence > at)
			{
				latest = text;
				at = sequence;
			}
		}

		return latest;
	}

	/// <summary>What a presentation has been told: entries, writes, texts, textures.</summary>
	public static string Summary(uint presentation, uint rootHash)
	{
		if (presentation == 0)
		{
			return "no presentation key";
		}

		if (!_said.TryGetValue(presentation, out Held? held))
		{
			return $"nothing told to 0x{presentation:X8}";
		}

		return held.RootHash != rootHash
			? $"0x{presentation:X8} was told under root 0x{held.RootHash:X8}, the book's root is 0x{rootHash:X8}"
			: $"0x{presentation:X8}: {held.ByOrdinal.Count} element(s) told a slide, {held.Writes.Count} write(s), " +
				$"{held.Texts.Count} text(s), {held.Textures.Count} texture(s); serial {held.Serial}, sequence {Sequence}";
	}

	/// <summary>The last `active` write told to an element after <paramref name="since"/>, or null.</summary>
	public static bool? ActiveOf(uint presentation, uint rootHash, int ordinal, ulong since)
	{
		if (presentation == 0 || !_said.TryGetValue(presentation, out Held? held) || held.RootHash != rootHash)
		{
			return null;
		}

		for (int i = held.Writes.Count - 1; i >= 0 && held.Writes[i].Sequence > since; --i)
		{
			if (held.Writes[i].IsActive && held.Writes[i].Ordinal == ordinal)
			{
				return held.Writes[i].Value != 0;
			}
		}

		return null;
	}

	/// <summary>What one element was last told, or null; never across a rebuilt presentation.</summary>
	public static Told? Of(uint presentation, uint rootHash, int ordinal) =>
		presentation != 0 && _said.TryGetValue(presentation, out Held? held) && held.RootHash == rootHash &&
		held.ByOrdinal.TryGetValue(ordinal, out Told told)
			? told
			: null;
}
