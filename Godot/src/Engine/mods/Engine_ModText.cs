using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Refii.Text;

namespace Refii.Mods;

/// <summary>
/// THE ONE LOOKUP FOR A MOD'S PLAYER-VISIBLE TEXT. A string a mod states — a <c>settings.ui.json</c>
/// label, description or option, an <c>input.json</c> label, <c>mod.toml</c>'s name and description, a
/// notice or a box — names a text key as <c>{KEY}</c>, the form a stated menu row's label already takes
/// (<c>"{QUICKSLOTS_SLOT_1}: {OBJECT_HEALTH_POTION_NAME}"</c>). Each key is looked up in order:
/// <list type="number">
/// <item>the running book (<see cref="Refii.Core.Engine_TitleGame.Strings"/>) — the game's own tags and the
/// mods' rows the mount merged into it, in the culture in force;</item>
/// <item>the mods' own <c>language/&lt;culture&gt;/text/book.babel.csv</c> in that culture;</item>
/// <item>their en-uk table — a partly translated mod still says every line;</item>
/// <item>the engine's own words (<see cref="EngineWords"/>), in that culture, then en-uk;</item>
/// </list>
/// and a key nothing knows is drawn AS WRITTEN, braces and all — a plain string with no braces, which is
/// every third-party mod written before keys, is unchanged. The game reads the enabled mods' tables from
/// the mount (<see cref="UseMounted"/>); the launcher, which mounts nothing, reads one mod's own tables
/// (<see cref="ForMod(string, string?)"/>) in the install's culture.
/// </summary>
/// <remarks>
/// Lua's <c>Refii.text</c> and Godot's <c>RefiiHost.text</c> are <see cref="Text"/>: there a BARE key is
/// looked up whole too, and <c>%1</c>, <c>%2</c> … take the call's arguments. Lua reaches it through the
/// payload's <c>refii_mod_text</c> (the host's registered lookup, called on the guest thread), so the
/// lookup reads only immutable snapshots. Pure but for <see cref="Find(string)"/>'s book and
/// <see cref="InstallCulture"/>; the rules are held by tests.
/// </remarks>
public static class ModText
{
	/// <summary>The culture every mod authors in and every lookup falls back to.</summary>
	public const string Fallback = Engine_GameCulture.Fallback;

	/// <summary>The game's own words for a toggle's two captions (the Options page's).</summary>
	public const string OnTag = "GUI_CONTROLS_ON";

	public const string OffTag = "GUI_CONTROLS_OFF";

	/// <summary>A mod action's key row and pad row on the Controls page: <c>%1</c> is the action's label.</summary>
	public const string InputKeyRow = "REFII_MOD_INPUT_KEY_ROW";

	public const string InputPadRow = "REFII_MOD_INPUT_PAD_ROW";

	private static readonly Regex KeyPattern = new(@"\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

	private static readonly Regex BareKey = new(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled);

	// ---- the rules, pure ------------------------------------------------------------------------

	/// <summary>
	/// <paramref name="text"/> with each <c>{KEY}</c> replaced by what <paramref name="lookup"/> answers; a key
	/// it does not answer stays as written. Null is empty.
	/// </summary>
	public static string Resolve(string? text, Func<string, string?> lookup)
	{
		if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0)
		{
			return text ?? string.Empty;
		}

		return KeyPattern.Replace(text, m => lookup(m.Groups[1].Value) is { } found ? found : m.Value);
	}

	/// <summary>A script's lookup: a bare key (<c>A-Z a-z 0-9 _</c> only) looked up whole, else <see cref="Resolve"/>.</summary>
	public static string Lookup(string? text, Func<string, string?> lookup)
	{
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}

		return BareKey.IsMatch(text) ? lookup(text) ?? text : Resolve(text, lookup);
	}

	/// <summary>The <c>{KEY}</c>s a text names, in order, each once.</summary>
	public static IReadOnlyList<string> Keys(string? text)
	{
		var keys = new List<string>();
		if (string.IsNullOrEmpty(text))
		{
			return keys;
		}

		foreach (Match m in KeyPattern.Matches(text))
		{
			if (!keys.Contains(m.Groups[1].Value))
			{
				keys.Add(m.Groups[1].Value);
			}
		}

		return keys;
	}

	/// <summary><c>%1</c>, <c>%2</c> … replaced by <paramref name="args"/> (one-based; a marker with no argument stays).</summary>
	public static string Format(string text, IReadOnlyList<string>? args) =>
		args is null || args.Count == 0 ? text : Refii.Ui.Engine_UiGuiText.Substitute(text, args);

	/// <summary>
	/// One key through the chain: the book, the mods' tables in <paramref name="culture"/> then en-uk, the
	/// engine's words in <paramref name="culture"/> then en-uk, then our own words (<c>REFII_*</c>,
	/// Refii.Text.Engine_OwnWords: the core Lua's notices); null when none knows it.
	/// </summary>
	public static string? Find(string key, Func<string, string?>? book, Tables tables, string culture)
	{
		if (string.IsNullOrEmpty(key))
		{
			return null;
		}

		if (book?.Invoke(key) is { Length: > 0 } said)
		{
			return said;
		}

		return tables.Find(culture, key) ?? EngineWord(culture, key) ?? Refii.Text.Engine_OwnWords.Find(key, culture);
	}

	/// <summary>The engine's own word for <paramref name="key"/> in <paramref name="culture"/>, else en-uk's; null for none.</summary>
	public static string? EngineWord(string culture, string key)
	{
		string code = Engine_GameCulture.Normalize(culture) ?? Fallback;
		if (EngineWords.TryGetValue(code, out Dictionary<string, string>? words) && words.TryGetValue(key, out string? word))
		{
			return word;
		}

		return EngineWords[Fallback].TryGetValue(key, out string? english) ? english : null;
	}

	/// <summary>
	/// THE ENGINE'S OWN WORDS on a mod's page, where the game's book has none: a Controls row's device. The
	/// toggle's On / Off are the game's (<see cref="OnTag"/>), here only for a book that lacks them. A mod's
	/// table may restate any of these keys; the book and the mods' tables are asked first.
	/// </summary>
	public static readonly IReadOnlyDictionary<string, Dictionary<string, string>> EngineWords = BuildEngineWords();

	private static Dictionary<string, Dictionary<string, string>> BuildEngineWords()
	{
		var words = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
		void Add(string culture, string on, string off, string key, string pad) => words[culture] = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			[OnTag] = on, [OffTag] = off, [InputKeyRow] = key, [InputPadRow] = pad,
		};

		Add("en-uk", "On", "Off", "%1 (key)", "%1 (pad)");
		Add("fr-fr", "Activé", "Désactivé", "%1 (touche)", "%1 (manette)");
		Add("de-de", "An", "Aus", "%1 (Taste)", "%1 (Pad)");
		Add("it-it", "Sì", "No", "%1 (tasto)", "%1 (pad)");
		Add("es-es", "Sí", "No", "%1 (tecla)", "%1 (mando)");
		Add("ru-ru", "Вкл.", "Выкл.", "%1 (клавиша)", "%1 (геймпад)");
		Add("pl-pl", "Wł.", "Wył.", "%1 (klawisz)", "%1 (pad)");
		Add("cs-cz", "Zapnuto", "Vypnuto", "%1 (klávesa)", "%1 (ovladač)");
		// The disc's sk-sk book is the cs-cz text, word for word.
		Add("sk-sk", "Zapnuto", "Vypnuto", "%1 (klávesa)", "%1 (ovladač)");
		Add("hu-hu", "Be", "Ki", "%1 (billentyű)", "%1 (kontroller)");
		Add("ja-jp", "オン", "オフ", "%1 (キー)", "%1 (パッド)");
		Add("ko-kr", "켜기", "끄기", "%1 (키)", "%1 (패드)");
		Add("zh-cht", "開", "關", "%1 (按鍵)", "%1 (手把)");
		return words;
	}

	/// <summary>
	/// Mods' text tables: culture → key → text. Several mods' tables merge in the order given, a later one
	/// winning a key, as the mount applies their rows.
	/// </summary>
	public sealed class Tables
	{
		private readonly Dictionary<string, Dictionary<string, string>> _byCulture;

		private Tables(Dictionary<string, Dictionary<string, string>> byCulture) => _byCulture = byCulture;

		public static readonly Tables Empty = new(new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal));

		/// <summary>The cultures some table states.</summary>
		public IReadOnlyCollection<string> Cultures => _byCulture.Keys;

		public bool IsEmpty => _byCulture.Count == 0;

		/// <summary><paramref name="key"/> in exactly <paramref name="culture"/>'s tables, or null.</summary>
		public string? Get(string culture, string key) =>
			Engine_GameCulture.Normalize(culture) is { } code &&
			_byCulture.TryGetValue(code, out Dictionary<string, string>? rows) && rows.TryGetValue(key, out string? text)
				? text
				: null;

		/// <summary><paramref name="key"/> in <paramref name="culture"/>, else en-uk; null for neither.</summary>
		public string? Find(string culture, string key) => Get(culture, key) ?? Get(Fallback, key);

		/// <summary>
		/// The tables among <paramref name="tables"/> that are a book's (<c>language/&lt;culture&gt;/text/book.babel</c>,
		/// with or without <c>data/</c> and <c>.csv</c>), each under its culture.
		/// </summary>
		public static Tables From(IEnumerable<(string Table, IEnumerable<BabelOverlayRow> Rows)> tables)
		{
			var byCulture = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
			foreach ((string table, IEnumerable<BabelOverlayRow> rows) in tables)
			{
				if (CultureOfBook(table) is not { } culture)
				{
					continue;
				}

				if (!byCulture.TryGetValue(culture, out Dictionary<string, string>? into))
				{
					into = new Dictionary<string, string>(StringComparer.Ordinal);
					byCulture[culture] = into;
				}

				foreach (BabelOverlayRow row in rows)
				{
					into[row.Key] = row.Value;
				}
			}

			return byCulture.Count == 0 ? Empty : new Tables(byCulture);
		}

		/// <summary>The same from table files (a mod-relative path and its CSV text).</summary>
		public static Tables FromFiles(IEnumerable<(string Path, string Text)> files)
		{
			var tables = new List<(string, IEnumerable<BabelOverlayRow>)>();
			foreach ((string path, string text) in files)
			{
				tables.Add((path, BabelOverlay.ParseCsv(text, new List<string>())));
			}

			return From(tables);
		}
	}

	/// <summary>The culture of a book table path (mod-relative, <c>/</c>-separated, <c>files/</c> stripped), or null for any other table.</summary>
	public static string? CultureOfBook(string table)
	{
		string path = table.Replace('\\', '/').TrimStart('/');
		if (path.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
		{
			path = path["files/".Length..];
		}

		if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
		{
			path = path[..^4];
		}

		return path.EndsWith("/text/book.babel", StringComparison.OrdinalIgnoreCase) ? BabelOverlay.CultureOf(path) : null;
	}

	// ---- the game: the mount's tables and the running book -----------------------------------------

	private static volatile Tables _mounted = Tables.Empty;

	/// <summary>The enabled mods' tables as the mount read them (<see cref="ModRowOverlays.Apply"/>).</summary>
	public static Tables Mounted => _mounted;

	/// <summary>Takes the mount's tables; every later lookup reads them.</summary>
	public static void UseMounted(Tables tables) => _mounted = tables ?? Tables.Empty;

	/// <summary>One key in the running game: <see cref="Find(string, Func{string, string?}?, Tables, string)"/> over the running book, the mount's tables and the culture in force.</summary>
	public static string? Find(string key) => Find(key, RunningBook, _mounted, Engine_GameCulture.Current);

	/// <summary>The running book's string for a tag: the tag as given, then with <c>TEXT_</c>, as the game's GUI resolves a tag (<see cref="Refii.Ui.Engine_UiGuiText.Resolve(string?, Refii.Text.Engine_BabelDatabase?, out bool)"/>).</summary>
	private static string? RunningBook(string key)
	{
		try
		{
			if (Refii.Core.Engine_TitleGame.Active.Strings is not { } book)
			{
				return null;
			}

			if (book.TryGetString(key, out string text) && text.Length != 0)
			{
				return text;
			}

			return !key.StartsWith(Refii.Ui.Engine_UiGuiText.TagPrefix, StringComparison.Ordinal) &&
				book.TryGetString(Refii.Ui.Engine_UiGuiText.TagPrefix + key, out string prefixed) && prefixed.Length != 0
					? prefixed
					: null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>A stated string as the game draws it: each <c>{KEY}</c> resolved (<see cref="Resolve"/>).</summary>
	public static string Game(string? text) => Resolve(text, Find);

	/// <summary>Lua's <c>Refii.text</c> and Godot's <c>RefiiHost.text</c>: a bare key or each <c>{KEY}</c>, then <c>%n</c> from <paramref name="args"/>.</summary>
	public static string Text(string? text, IReadOnlyList<string>? args = null) => Format(Lookup(text, Find), args);

	/// <summary>An engine word (<see cref="EngineWords"/>) in the culture in force, through the whole chain.</summary>
	public static string Word(string key) => Find(key) ?? key;

	// ---- one mod's own tables: the launcher, an install, a fingerprint ----------------------------

	private static readonly object Gate = new();

	private static readonly Dictionary<string, (long Stamp, Tables Tables)> _ofMod = new(StringComparer.OrdinalIgnoreCase);

	private static bool _installRead;

	/// <summary>
	/// The culture a lookup outside the running game uses: the one in force, after reading the installed
	/// game's <c>cultures.txt</c> once if nothing has (<see cref="Engine_GameCulture.UseInstall"/> — the
	/// launcher mounts nothing, so it is the install's own culture there).
	/// </summary>
	public static string InstallCulture()
	{
		lock (Gate)
		{
			if (!_installRead && Engine_GameCulture.Installed.Count == 0)
			{
				_installRead = true;
				try
				{
					Engine_GameCulture.UseInstall(InstalledGame());
				}
				catch (Exception)
				{
				}
			}
		}

		return Engine_GameCulture.Current;
	}

	/// <summary>The active title's installed data, found by convention (apart, so a test host never touches it).</summary>
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static string InstalledGame() => Refii.Core.Engine_GameData.Resolve();

	/// <summary>
	/// <paramref name="text"/> through ONE mod's own tables — a <c>.f2mod</c> package or a mod folder at
	/// <paramref name="source"/> — in the install's culture, en-uk, then the engine's words.
	/// </summary>
	public static string ForMod(string source, string? text)
	{
		if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0)
		{
			return text ?? string.Empty;
		}

		Tables tables = Of(source);
		string culture = InstallCulture();
		return Resolve(text, key => Find(key, null, tables, culture));
	}

	/// <summary><see cref="ForMod(string, string?)"/> for an install (its package, else its folder).</summary>
	public static string ForMod(ModInstall install, string? text) =>
		ForMod(!string.IsNullOrEmpty(install.PackagePath) ? install.PackagePath : install.Root, text);

	/// <summary>A mod's display title: its <c>mod.toml</c> name through its own tables, else its folder name.</summary>
	public static string TitleOf(ModInstall install) =>
		install.Metadata.Title.Length != 0 ? ForMod(install, install.Metadata.Title) : install.Name;

	/// <summary>One mod's own tables, read once per change of the package or folder.</summary>
	public static Tables Of(string source)
	{
		if (string.IsNullOrEmpty(source))
		{
			return Tables.Empty;
		}

		try
		{
			bool package = File.Exists(source);
			List<string> loose = package ? new List<string>() : LooseTables(source);
			long stamp = package
				? File.GetLastWriteTimeUtc(source).Ticks ^ new FileInfo(source).Length
				: Stamp(loose);
			lock (Gate)
			{
				if (_ofMod.TryGetValue(source, out (long Stamp, Tables Tables) cached) && cached.Stamp == stamp)
				{
					return cached.Tables;
				}
			}

			Tables read = package ? ReadPackage(source) : Tables.FromFiles(ReadLoose(source, loose));
			lock (Gate)
			{
				_ofMod[source] = (stamp, read);
			}

			return read;
		}
		catch (Exception)
		{
			return Tables.Empty;
		}
	}

	private static Tables ReadPackage(string path)
	{
		if (!F2ModPackage.TryOpen(path, out F2ModPackage? package, out _) || package is null)
		{
			return Tables.Empty;
		}

		var files = new List<(string, string)>();
		using (package)
		{
			foreach (F2ModEntry entry in package.Entries)
			{
				if (CultureOfBook(entry.Path) is not null)
				{
					files.Add((entry.Path, Encoding.UTF8.GetString(package.ReadEntryBytes(entry))));
				}
			}
		}

		return Tables.FromFiles(files);
	}

	/// <summary>A folder's book tables, under <c>language/</c> or <c>files/language/</c>.</summary>
	private static List<string> LooseTables(string folder)
	{
		var found = new List<string>();
		foreach (string root in new[] { Path.Combine(folder, "language"), Path.Combine(folder, "files", "language") })
		{
			if (Directory.Exists(root))
			{
				found.AddRange(Directory.EnumerateFiles(root, "*" + BabelOverlay.CsvSuffix, SearchOption.AllDirectories));
			}
		}

		found.Sort(StringComparer.Ordinal);
		return found;
	}

	private static long Stamp(List<string> files)
	{
		long stamp = files.Count;
		foreach (string file in files)
		{
			stamp = (stamp * 31) ^ File.GetLastWriteTimeUtc(file).Ticks;
		}

		return stamp;
	}

	private static IEnumerable<(string Path, string Text)> ReadLoose(string folder, List<string> files)
	{
		foreach (string file in files)
		{
			yield return (Path.GetRelativePath(folder, file).Replace('\\', '/'), File.ReadAllText(file));
		}
	}
}
