namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;
using Refii.Text;

/// <summary>
/// THE ONE TEXT LOOKUP for a mod's player-visible strings (<see cref="ModText"/>): <c>{KEY}</c> through the
/// running book, the mods' tables in the culture in force, their en-uk rows, the engine's words, else as
/// written; the launcher's read of one mod's tables in the install's culture; the validator's warning;
/// and the Lua / Godot / native surfaces that reach it. No Godot runtime object is made here.
/// </summary>
[TestSuite]
public class ModTextTests
{
	private static ModText.Tables Tables(params (string Path, string Csv)[] files) => ModText.Tables.FromFiles(files);

	private static readonly ModText.Tables Sample = Tables(
		("language/en-uk/text/book.babel.csv", "key,value\nMY_NAME,My Mod\nMY_GOLD,+%1 gold\nMY_ONLY_ENGLISH,English only\n"),
		("files/language/hu-hu/text/book.babel.csv", "key,value\nMY_NAME,Saját mod\nMY_GOLD,+%1 arany\n"),
		("language/hu-hu/text/other.babel.csv", "key,value\nMY_NAME,not a book\n"));

	[TestCase]
	public void APlainStringIsUnchangedAndAnUnknownKeyIsDrawnAsWritten()
	{
		Func<string, string?> lookup = key => key == "KNOWN" ? "Known" : null;
		AssertThat(ModText.Resolve("Size", lookup)).IsEqual("Size");
		AssertThat(ModText.Resolve("Damage {0:0} per hit", lookup)).IsEqual("Damage {0:0} per hit");
		AssertThat(ModText.Resolve("{KNOWN} x10 and {MISSING}", lookup)).IsEqual("Known x10 and {MISSING}");
		AssertThat(ModText.Resolve(null, lookup)).IsEqual(string.Empty);

		// A script's bare key is looked up whole; anything else resolves only its braces.
		AssertThat(ModText.Lookup("KNOWN", lookup)).IsEqual("Known");
		AssertThat(ModText.Lookup("MISSING", lookup)).IsEqual("MISSING");
		AssertThat(ModText.Lookup("KNOWN thing", lookup)).IsEqual("KNOWN thing");
		AssertThat(string.Join(",", ModText.Keys("{A} {B} {A} {not-a-key}"))).IsEqual("A,B");
		AssertThat(ModText.Format("%2 of %1, %3", new[] { "10", "3" })).IsEqual("3 of 10, %3");
	}

	[TestCase]
	public void AKeyGoesBookThenCultureThenEnglishThenEngineThenRaw()
	{
		Func<string, string?> book = key => key == "BOOK_TAG" || key == "MY_NAME" ? "from the book" : null;

		// The book (the game's tags and the merged rows) answers first.
		AssertThat(ModText.Find("MY_NAME", book, Sample, "hu-hu")).IsEqual("from the book");
		AssertThat(ModText.Find("BOOK_TAG", book, Sample, "hu-hu")).IsEqual("from the book");

		// Then the culture in force, then en-uk — a partly translated mod still says every line.
		AssertThat(ModText.Find("MY_NAME", null, Sample, "hu-hu")).IsEqual("Saját mod");
		AssertThat(ModText.Find("MY_ONLY_ENGLISH", null, Sample, "hu-hu")).IsEqual("English only");
		AssertThat(ModText.Find("MY_NAME", null, Sample, "cs-cz")).IsEqual("My Mod");
		AssertThat(ModText.Find("MY_NAME", null, Sample, "en-uk")).IsEqual("My Mod");

		// The engine's own words, in the culture, else English; nothing else answers null (drawn as written).
		AssertThat(ModText.Find(ModText.InputKeyRow, null, Sample, "hu-hu")).IsEqual("%1 (billentyű)");
		AssertThat(ModText.Find(ModText.InputPadRow, null, ModText.Tables.Empty, "xx-yy")).IsEqual("%1 (pad)");
		AssertThat(ModText.Find("NOBODY", book, Sample, "hu-hu")).IsNull();

		// A text: every key through the chain, the rest as written.
		AssertThat(ModText.Resolve("{MY_NAME}: {MY_GOLD} {NOBODY}", key => ModText.Find(key, null, Sample, "hu-hu")))
			.IsEqual("Saját mod: +%1 arany {NOBODY}");
		AssertThat(ModText.Format(ModText.Lookup("MY_GOLD", key => ModText.Find(key, null, Sample, "hu-hu")), new[] { "1 000" }))
			.IsEqual("+1 000 arany");

		// Only a book's tables count, with or without files/ and .csv.
		AssertThat(string.Join(",", Sample.Cultures.OrderBy(c => c, StringComparer.Ordinal))).IsEqual("en-uk,hu-hu");
		AssertThat(ModText.CultureOfBook("files/language/zh-cht/text/book.babel.csv")).IsEqual("zh-cht");
		AssertThat(ModText.CultureOfBook("language/en-uk/text/book.babel")).IsEqual("en-uk");
		AssertThat(ModText.CultureOfBook("globals/globals.gdb.csv")).IsNull();
	}

	[TestCase]
	public void TheMountsTablesAreTheGamesAndALaterModWinsAKey()
	{
		var low = new List<BabelOverlayRow> { new() { Key = "SHARED", Value = "low" }, new() { Key = "LOW_ONLY", Value = "low only" } };
		var high = new List<BabelOverlayRow> { new() { Key = "SHARED", Value = "high" } };
		ModText.Tables merged = ModText.Tables.From(new (string, IEnumerable<BabelOverlayRow>)[]
		{
			("language/en-uk/text/book.babel", low),
			("language/en-uk/text/book.babel", high),
		});
		AssertThat(merged.Find("en-uk", "SHARED")).IsEqual("high");
		AssertThat(merged.Find("de-de", "LOW_ONLY")).IsEqual("low only");
		AssertThat(ModText.Tables.From(Array.Empty<(string, IEnumerable<BabelOverlayRow>)>()).IsEmpty).IsTrue();
	}

	[TestCase]
	public void EveryEngineWordIsInEveryDiscCultureWithItsMarker()
	{
		string[] cultures = { "en-uk", "fr-fr", "de-de", "it-it", "es-es", "ru-ru", "pl-pl", "cs-cz", "hu-hu", "sk-sk", "ja-jp", "ko-kr", "zh-cht" };
		string english = string.Join(",", ModText.EngineWords["en-uk"].Keys.OrderBy(k => k, StringComparer.Ordinal));
		foreach (string culture in cultures)
		{
			AssertThat(ModText.EngineWords.ContainsKey(culture)).OverrideFailureMessage(culture).IsTrue();
			Dictionary<string, string> words = ModText.EngineWords[culture];
			AssertThat(string.Join(",", words.Keys.OrderBy(k => k, StringComparer.Ordinal))).OverrideFailureMessage(culture).IsEqual(english);
			AssertThat(words[ModText.InputKeyRow].Contains("%1", StringComparison.Ordinal)).OverrideFailureMessage(culture).IsTrue();
			AssertThat(words[ModText.InputPadRow].Contains("%1", StringComparison.Ordinal)).OverrideFailureMessage(culture).IsTrue();
			AssertThat(words.Values.All(w => w.Trim().Length != 0)).OverrideFailureMessage(culture).IsTrue();
		}

		// The toggle's words are the game's own Options-page tags, so its book answers first.
		AssertThat(ModText.OnTag).IsEqual("GUI_CONTROLS_ON");
		AssertThat(ModText.OffTag).IsEqual("GUI_CONTROLS_OFF");
	}

	/// <summary>
	/// The launcher mounts nothing: a mod's <c>mod.toml</c> name is read from the mod's own tables in the
	/// culture the INSTALL starts in (a disc that ships only es-es), then en-uk, then as written.
	/// </summary>
	[TestCase]
	public void TheLauncherReadsTheModsOwnTablesInTheInstallsCulture()
	{
		string root = Path.Combine(Refii.Tests.TestTemp.Root, "refii_modtext_" + Guid.NewGuid().ToString("N"));
		string game = Path.Combine(root, "game");
		string mod = Path.Combine(root, "my_mod");
		Engine_GameCulture.ResetForTests();
		try
		{
			string language = Path.Combine(game, "data", "language");
			Directory.CreateDirectory(Path.Combine(language, "es-es", "text"));
			File.WriteAllBytes(Path.Combine(language, "cultures.txt"), Encoding.BigEndianUnicode.GetPreamble()
				.Concat(Encoding.BigEndianUnicode.GetBytes(
					"en-uk\tGUI_SCREEN_OPTIONS_LANGUAGE_EN_UK\tnull\r\nes\tGUI_SCREEN_OPTIONS_LANGUAGE_ES\ten-uk\r\n" +
					"en-us\tGUI_SCREEN_OPTIONS_LANGUAGE_EN_US\ten-uk\r\nes-es\tGUI_SCREEN_OPTIONS_LANGUAGE_ES_ES\tes")).ToArray());
			Engine_GameCulture.UseInstall(game);
			AssertThat(ModText.InstallCulture()).IsEqual("es-es");

			Directory.CreateDirectory(Path.Combine(mod, "language", "en-uk", "text"));
			Directory.CreateDirectory(Path.Combine(mod, "language", "es-es", "text"));
			File.WriteAllText(Path.Combine(mod, "language", "en-uk", "text", "book.babel.csv"),
				"key,value\nMY_MOD_NAME,My Mod\nMY_MOD_DESCRIPTION,\"Gold, renown and more.\"\n");
			File.WriteAllText(Path.Combine(mod, "language", "es-es", "text", "book.babel.csv"), "key,value\nMY_MOD_NAME,Mi mod\n");

			var install = new ModInstall { Name = "my_mod", Root = mod, Metadata = new ModMetadata { Title = "{MY_MOD_NAME}" } };
			AssertThat(ModText.TitleOf(install)).IsEqual("Mi mod");
			AssertThat(ModText.ForMod(install, "{MY_MOD_DESCRIPTION}")).IsEqual("Gold, renown and more.");
			AssertThat(ModText.ForMod(install, "{NOT_THERE}")).IsEqual("{NOT_THERE}");
			AssertThat(ModText.ForMod(install, "A plain description.")).IsEqual("A plain description.");
			AssertThat(ModText.TitleOf(new ModInstall { Name = "bare", Root = mod })).IsEqual("bare");
		}
		finally
		{
			Engine_GameCulture.ResetForTests();
			if (Directory.Exists(root))
			{
				Directory.Delete(root, true);
			}
		}
	}

	[TestCase]
	public void TheValidatorWarnsAKeyWithNoEnglishRow()
	{
		var files = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			[ModValidate.EnglishTable] = "key,value\nMY_NAME,My Mod\nMY_SIZE,Size\n",
			["mod.toml"] = "name = \"{MY_NAME}\"\ndescription = \"{MY_DESCRIPTION}\"\n",
			["settings.ui.json"] = "{ \"size\": { \"label\": \"{MY_SIZE}\", \"description\": \"Plain.\", \"type\": \"choice\", " +
				"\"options\": [ { \"value\": 0, \"label\": \"{MY_SMALL}\" } ] } }",
			["input.json"] = "{ \"go\": { \"label\": \"{MY_GO}\", \"keys\": [\"G\"] } }",
		};
		List<ModIssue> issues = ModValidate.TextKeys(files);
		AssertThat(issues.All(i => !i.Error)).IsTrue();
		AssertThat(string.Join(";", issues.Select(i => i.Path + ":" + i.Pointer).OrderBy(s => s, StringComparer.Ordinal)))
			.IsEqual("input.json:go.label;mod.toml:description;settings.ui.json:size.options[0].label");
	}

	/// <summary>Every <c>{KEY}</c> a bundled mod names in settings.ui.json, input.json or mod.toml has its en-uk row.</summary>
	[TestCase]
	public void EveryBundledModsKeysHaveEnglishRows()
	{
		string? repo = GodotModScripts.RepoRoot();
		if (repo == null)
		{
			return;
		}

		int named = 0;
		foreach (string folder in Directory.GetDirectories(Path.Combine(repo, "Fable2", "mods-src")))
		{
			var files = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (string name in new[] { ModValidate.EnglishTable, "mod.toml", "settings.ui.json", "input.json" })
			{
				string path = Path.Combine(folder, name);
				if (File.Exists(path))
				{
					files[name] = File.ReadAllText(path);
				}
			}

			List<ModIssue> issues = ModValidate.TextKeys(files);
			AssertThat(issues.Count).OverrideFailureMessage($"{folder}: {string.Join("; ", issues)}").IsEqual(0);

			// The player reads no bare English in these files: every label, description and name is a key.
			ModMetadata meta = ModWorkspace.ParseModToml(files["mod.toml"]);
			AssertThat(ModText.Keys(meta.Title).Count == 1 && ModText.Keys(meta.Description).Count == 1)
				.OverrideFailureMessage($"{folder}: mod.toml's name and description are text keys").IsTrue();
			named += 2;
		}

		AssertThat(named).IsEqual(26);
	}

	/// <summary>
	/// THE LOOKUP ON EVERY SURFACE: Lua's Refii.text (the preamble over the native Refii._text, in the game VM
	/// and the front end), Godot's RefiiHost.text, the native refii_mod_text over the bridge's
	/// refii_bridge_mod_text and the host's RefiiHostSetModTextLookup — one parity row and one meaning.
	/// </summary>
	[TestCase]
	public void LuaGodotAndNativeReachTheSameLookup()
	{
		AssertThat(ModApi.Twins.Any(t => t.Lua == "text" && t.Godot == "text")).IsTrue();
		AssertThat(ModApi.LuaHelpers.Any(h => h.Signature.StartsWith("Refii.text(", StringComparison.Ordinal) && h.Meaning.StartsWith(ModApi.TextMeaning, StringComparison.Ordinal))).IsTrue();
		AssertThat(ModApi.GodotHelpers.Any(h => h.Signature.StartsWith("text(", StringComparison.Ordinal) && h.Meaning.StartsWith(ModApi.TextMeaning, StringComparison.Ordinal))).IsTrue();
		AssertThat(ModApi.Helpers.Any(h => h.Signature == "int32_t refii_mod_text(text, out, capacity)")).IsTrue();
		AssertThat(typeof(RefiiHost).GetMethods().Count(m => m.Name == "text" && m.ReturnType == typeof(string))).IsEqual(2);

		string preamble = ModLua.RuntimePreamble(Array.Empty<(string, float)>());
		AssertThat(preamble.Contains("function Refii.text(text, a, b, c, d, e, f, g, h)", StringComparison.Ordinal)).IsTrue();
		AssertThat(preamble.Contains("if Refii._text ~= nil then s = Refii._text(s) or s end", StringComparison.Ordinal)).IsTrue();
		AssertThat(ModLua.FrontendNatives.Contains("Refii._text = __refii_text\n", StringComparison.Ordinal)).IsTrue();

		string? repo = GodotModScripts.RepoRoot();
		if (repo == null)
		{
			return;
		}

		string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { repo }.Concat(parts).ToArray()));
		AssertThat(Read("Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp").Contains("{\"_text\", &NativeText, 0}", StringComparison.Ordinal)).IsTrue();
		AssertThat(Read("Fable2", "src", "vfs", "src", "hooks", "mod_frontend_lua.cpp").Contains("{\"__refii_text\", &NativeText}", StringComparison.Ordinal)).IsTrue();
		AssertThat((Read("Fable2", "src", "vfs", "include", "core", "refii_mod.h") + Read("src", "runtime", "include", "core", "refii_mod_requests.h"))
			.Contains("extern \"C\" int32_t refii_mod_text(const char* text, char* out, uint32_t capacity);", StringComparison.Ordinal)).IsTrue();
		AssertThat(Read("src", "runtime", "include", "core", "payload_bridge.h")
			.Contains("int32_t refii_bridge_mod_text(const char* text, size_t length, char* out, uint32_t capacity);", StringComparison.Ordinal)).IsTrue();
		AssertThat(Read("src", "hostcore", "src", "host_exports.cpp")
			.Contains("REFII_HOST_ABI_EXPORT void RefiiHostSetModTextLookup(refii::mods::HostModTextFn lookup)", StringComparison.Ordinal)).IsTrue();
		AssertThat(Read("Godot", "src", "host", "HostBridge.cs")
			.Contains("private static extern void RefiiHostSetModTextLookup(IntPtr lookup);", StringComparison.Ordinal)).IsTrue();

		// Installed wherever the guest's asset reader is, desktop and Android.
		AssertThat(Read("Godot", "src", "Engine", "Engine_GameAssets.cs").Contains("HostBridge.SetModTextLookup(text => Refii.Mods.ModText.Text(text));", StringComparison.Ordinal)).IsTrue();
		AssertThat(Read("Godot", "src", "android", "AndroidNativeSession.cs").Contains("HostBridge.SetModTextLookup(text => Refii.Mods.ModText.Text(text));", StringComparison.Ordinal)).IsTrue();

		// The notice channel and the boxes take a key as they are.
		string boxes = Read("Godot", "src", "Engine", "ui", "Engine_UiModBoxes.cs");
		AssertThat(boxes.Contains("Engine_UiNotices.Post(id, shownTo, Refii.Mods.ModText.Game(title), Refii.Mods.ModText.Game(text), seconds,", StringComparison.Ordinal)).IsTrue();
		AssertThat(boxes.Contains("Engine_UiModRequest.Body(Refii.Mods.ModText.Game(title), Refii.Mods.ModText.Game(text))", StringComparison.Ordinal)).IsTrue();
	}
}
