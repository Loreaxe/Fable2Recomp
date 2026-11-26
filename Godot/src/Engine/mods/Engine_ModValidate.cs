using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Refii.Mods;

/// <summary>
/// One problem in one file of a mod. <see cref="Pointer"/> is the place inside the document —
/// <c>pieces[2].value</c>, <c>souls_combat.swing_cost</c>, <c>requires (line 4)</c> — and empty for
/// the file as a whole. An ERROR is content the loader refuses, drops or reads as something other
/// than what it says; a WARNING is content the loader ignores or corrects on its own.
/// </summary>
public sealed record ModIssue(string Path, string Pointer, bool Error, string Message)
{
	/// <summary><c>path: pointer: message</c>, the pointer left out when the issue is the file's.</summary>
	public override string ToString() =>
		Pointer.Length == 0 ? $"{Path}: {Message}" : $"{Path}: {Pointer}: {Message}";
}

/// <summary>
/// THE SCHEMA CHECK for every content kind a mod ships, run before the game loads it (once per
/// mount) and when the Mod Studio writes it (a write with an error is refused). Each kind is
/// checked against what ITS LOADER reads — the loader is the schema, not a document: required
/// fields, types the loader accepts, ranges, the enum values it knows, and unknown keys (which
/// the loaders ignore silently, so they are warnings).
/// </summary>
/// <remarks>
/// <para>Pure .NET over <see cref="System.Text.Json"/>, so tests run it without a Godot type in the
/// host. The loaders read most kinds with Godot's <c>Json.ParseString</c>, whose numbers are all
/// floats: a whole number written <c>5.0</c> is an integer here too, and a field the loader reads
/// with <c>AsString</c>/<c>AsDouble</c> accepts what those convert, with a warning where the
/// conversion is surprising. A field the loader reads only when its Variant type matches is
/// STRICT: anything else is dropped, which is an error.</para>
/// <para>Not checked here, because only the engine can: key names in <c>input.json</c>
/// (<c>OS.FindKeycodeFromString</c>), menu source names (registered at runtime in
/// <c>Engine_UiMenuSources</c>), whether a texture or effect name exists, and whether a
/// <c>mod:</c> value is ever published.</para>
/// </remarks>
public static partial class ModValidate
{
	/// <summary><c>spells/&lt;id&gt;.json</c>, the one kind matched by folder rather than by path.</summary>
	public const string SpellsKind = "spells/*.json";

	/// <summary><c>fx/effects/&lt;name&gt;.json</c>, a mod effect (Engine_FxEffectJson), matched by folder like the spells.</summary>
	public const string FxEffectsKind = "fx/effects/*.json";

	/// <summary><c>&lt;database&gt;.gdb.csv</c>, database rows (GdbCsvOverlay), matched by suffix wherever they sit.</summary>
	public const string GdbRowsKind = "*.gdb.csv";

	/// <summary>
	/// The kind of a package path, by the path its loader reads (<c>files/</c> stripped, any case);
	/// empty for a path no schema covers.
	/// </summary>
	public static string KindOf(string path)
	{
		string normalized = Normalize(path);
		switch (normalized)
		{
			case ModSettings.ModSettingsPath:
			case ModSettingsUi.ModSettingsUiPath:
			case ModInput.ModInputPath:
			case Refii.Fx.Engine_FxRecipes.RecipesPath:
			case Refii.Ui.Engine_HudModPieces.ModHudPath:
			case Refii.Ui.Engine_UiTemplates.ModTemplatesPath:
			case Engine_ModUiFiles.SpellWheelPath:
			case Engine_ModUiFiles.AbilitiesPath:
			case ModWorkspace.ModMetadataFilename:
			case Refii.Hero.Engine_ModMorphs.ManifestPath:
			case ModMeshOverlays.TargetsPath:
			case ModMeshOverlays.ModelsPath:
				return normalized;
		}

		if (Refii.Fx.Engine_FxModEffects.IsEffectFile(normalized, out _))
		{
			return FxEffectsKind;
		}

		if (normalized.EndsWith(Refii.Gdb.GdbCsvOverlay.CsvSuffix, StringComparison.Ordinal))
		{
			return GdbRowsKind;
		}

		return normalized.StartsWith(ModSpells.Folder, StringComparison.Ordinal) &&
			normalized.EndsWith(".json", StringComparison.Ordinal) &&
			normalized.Length > ModSpells.Folder.Length + ".json".Length &&
			normalized.IndexOf('/', ModSpells.Folder.Length) < 0
				? SpellsKind
				: string.Empty;
	}

	/// <summary>A package path as the package and the loaders match it: forward slashes, lower case, no <c>files/</c>.</summary>
	public static string Normalize(string path)
	{
		string normalized = path.Replace('\\', '/').Trim('/').ToLowerInvariant();
		return normalized.StartsWith("files/", StringComparison.Ordinal) ? normalized["files/".Length..] : normalized;
	}

	/// <summary>Every issue of one file; empty when the file is clean or its path is no checked kind.</summary>
	public static IReadOnlyList<ModIssue> Validate(string path, string text)
	{
		string kind = KindOf(path);
		if (kind.Length == 0)
		{
			return Array.Empty<ModIssue>();
		}

		var doc = new Doc(Normalize(path));
		bool bom = text.Length != 0 && text[0] == '﻿';
		if (kind == ModWorkspace.ModMetadataFilename)
		{
			if (bom)
			{
				doc.Error(string.Empty, "the file starts with a byte-order mark, which becomes part of the first key; " +
					"save it as UTF-8 without a BOM");
				text = text[1..];
			}

			CheckManifest(doc, text);
			return doc.Issues;
		}

		// *.gdb.csv: the rows' FORM — columns, type tokens, numbers — is what can be checked without
		// the database (this check is pure .NET and runs in the test host). Whether an entry or
		// property exists, and whether a type matches, is decided at mount against the shipped
		// records, and a refused row is reported in the resolved index's rows section.
		if (kind == GdbRowsKind)
		{
			Refii.Gdb.GdbCsvResult rows = Refii.Gdb.GdbCsvOverlay.Parse(text, null);
			foreach (string error in rows.Errors)
			{
				int colon = error.IndexOf(':');
				doc.Error(colon > 0 ? error[..colon] : string.Empty, colon > 0 ? error[(colon + 1)..].Trim() : error);
			}

			return doc.Issues;
		}

		if (bom)
		{
			doc.Warn(string.Empty, "the file starts with a UTF-8 byte-order mark; save it without one " +
				"(Godot's JSON reader is not guaranteed to skip it)");
			text = text[1..];
		}

		// morphs.json: the registry's reader IS the schema (Engine_ModMorphs.Parse).
		if (kind == Refii.Hero.Engine_ModMorphs.ManifestPath)
		{
			var morphIssues = new List<Refii.Hero.ModMorphIssue>();
			Refii.Hero.Engine_ModMorphs.Parse(string.Empty, text, morphIssues);
			foreach (Refii.Hero.ModMorphIssue issue in morphIssues)
			{
				if (issue.Error)
				{
					doc.Error(issue.Pointer, issue.Message);
				}
				else
				{
					doc.Warn(issue.Pointer, issue.Message);
				}
			}

			return doc.Issues;
		}

		// morph_targets.json: ModMeshOverlays.ParseTargets is its reader.
		if (kind == ModMeshOverlays.TargetsPath)
		{
			var targetErrors = new List<string>();
			ModMeshOverlays.ParseTargets(text, targetErrors);
			foreach (string error in targetErrors)
			{
				doc.Error(string.Empty, error);
			}

			return doc.Issues;
		}

		// models.json: ModMeshOverlays.ParseModels is its reader.
		if (kind == ModMeshOverlays.ModelsPath)
		{
			var modelErrors = new List<string>();
			ModMeshOverlays.ParseModels(text, modelErrors);
			foreach (string error in modelErrors)
			{
				doc.Error(string.Empty, error);
			}

			return doc.Issues;
		}

		// fx/effects/*.json: the reader IS the schema (Engine_FxEffectJson.Parse reports every issue
		// with its place), so the check is that reader's verdict.
		if (kind == FxEffectsKind)
		{
			var issues = new List<Refii.Fx.Engine_FxEffectJson.Issue>();
			Refii.Fx.Engine_FxEffectJson.Parse(text, issues);
			foreach (Refii.Fx.Engine_FxEffectJson.Issue issue in issues)
			{
				if (issue.Error)
				{
					doc.Error(issue.Pointer, issue.Message);
				}
				else
				{
					doc.Warn(issue.Pointer, issue.Message);
				}
			}

			return doc.Issues;
		}

		// spells/*.json is read with System.Text.Json allowing comments and trailing commas
		// (ModSpells.TryParse); every other kind with Godot's JSON reader, which allows neither.
		bool lenient = kind == SpellsKind;
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(text, new JsonDocumentOptions
			{
				AllowTrailingCommas = lenient,
				CommentHandling = lenient ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
			});
		}
		catch (JsonException exception)
		{
			doc.Error(string.Empty, NotJson(exception, lenient));
			return doc.Issues;
		}

		using (document)
		{
			JsonElement root = document.RootElement;
			switch (kind)
			{
				case ModSettings.ModSettingsPath: CheckSettings(doc, root); break;
				case ModSettingsUi.ModSettingsUiPath: CheckSettingsUi(doc, root); break;
				case ModInput.ModInputPath: CheckInput(doc, root); break;
				case SpellsKind: CheckSpell(doc, root); break;
				case Refii.Fx.Engine_FxRecipes.RecipesPath: CheckRecipes(doc, root); break;
				case Refii.Ui.Engine_HudModPieces.ModHudPath: CheckHud(doc, root); break;
				case Refii.Ui.Engine_UiTemplates.ModTemplatesPath: CheckTemplates(doc, root); break;
				case Engine_ModUiFiles.SpellWheelPath: CheckSpellWheel(doc, root); break;
				case Engine_ModUiFiles.AbilitiesPath: CheckAbilities(doc, root); break;
			}
		}

		return doc.Issues;
	}

	private static string NotJson(JsonException exception, bool lenient)
	{
		string message = exception.Message;
		foreach (string cut in new[] { " Change the reader options.", " Path:", " LineNumber:" })
		{
			int at = message.IndexOf(cut, StringComparison.Ordinal);
			if (at > 0)
			{
				message = message[..at];
			}
		}

		string where = exception.LineNumber is { } line
			? $" at line {line + 1}, column {(exception.BytePositionInLine ?? 0) + 1}"
			: string.Empty;
		return $"not valid JSON{where}: {message.Trim()}" +
			(lenient ? string.Empty : " (Godot's JSON reader takes no comments and no trailing commas)");
	}

	// ---------------------------------------------------------------------------------------
	// settings.json — ModSettings.Load / Take
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// An object of numbers keyed <c>&lt;mod&gt;.&lt;name&gt;</c>, one level of nesting folded with a
	/// dot. A value is a number, true/false (1/0) or a number in a string; anything else, or a
	/// second level of nesting, is ignored by the loader.
	/// </summary>
	private static void CheckSettings(Doc doc, JsonElement root)
	{
		if (!doc.Object(root, string.Empty,
			"must be a JSON object of settings: { \"<mod>\": { \"<name>\": 1.5 } } or { \"<mod>.<name>\": 1.5 }; the file is ignored"))
		{
			return;
		}

		var folded = new HashSet<string>(StringComparer.Ordinal);
		foreach (JsonProperty entry in root.EnumerateObject())
		{
			string at = At(string.Empty, entry.Name);
			if (entry.Value.ValueKind == JsonValueKind.Object)
			{
				if (!entry.Value.EnumerateObject().Any())
				{
					doc.Warn(at, "states no settings");
				}

				foreach (JsonProperty inner in entry.Value.EnumerateObject())
				{
					SettingValue(doc, At(at, inner.Name), entry.Name + "." + inner.Name, inner.Value, folded);
				}
			}
			else
			{
				SettingValue(doc, at, entry.Name, entry.Value, folded);
			}
		}
	}

	private static void SettingValue(Doc doc, string at, string key, JsonElement value, HashSet<string> folded)
	{
		if (key.Trim().Trim('.').Length == 0)
		{
			doc.Warn(at, "a setting with no name");
		}

		if (!folded.Add(key))
		{
			doc.Warn(at, $"'{key}' is stated twice (a nested and a dotted key fold to the same name); the last one is read");
		}

		switch (value.ValueKind)
		{
			case JsonValueKind.Number:
			case JsonValueKind.True:
			case JsonValueKind.False:
				return;
			case JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _):
				doc.Warn(at, "a number written as text; write it without quotes");
				return;
			case JsonValueKind.Object:
				doc.Error(at, "settings nest one level only (<mod>.<name>); this one is ignored");
				return;
			default:
				doc.Error(at, $"must be a number, not {Describe(value)}; the setting is ignored");
				return;
		}
	}

	// ---------------------------------------------------------------------------------------
	// settings.ui.json — ModSettingsUi.Build / Describe
	// ---------------------------------------------------------------------------------------

	private static readonly string[] SettingsUiRowKeys = { "label", "description", "type", "min", "max", "step", "options" };

	private static readonly string[] SettingsUiTypes = { "slider", "toggle", "choice" };

	private static readonly string[] SettingsUiOptionKeys = { "value", "label" };

	/// <summary>
	/// An object keyed by setting name (without or with the <c>&lt;mod&gt;.</c> prefix), each an object
	/// of <c>label</c>/<c>description</c> (strings), <c>type</c> (slider or toggle) and
	/// <c>min</c>/<c>max</c>/<c>step</c> (numbers). Every field is read only at its own type.
	/// </summary>
	private static void CheckSettingsUi(Doc doc, JsonElement root)
	{
		if (!doc.Object(root, string.Empty, "must be a JSON object of rows keyed by setting name; the file is ignored"))
		{
			return;
		}

		doc.DuplicateKeys(root, string.Empty);
		foreach (JsonProperty entry in root.EnumerateObject())
		{
			string at = At(string.Empty, entry.Name);
			if (!doc.Object(entry.Value, at,
				"must be an object { \"label\", \"type\", \"min\", \"max\", \"step\", \"description\" }; the row is built from the value alone"))
			{
				continue;
			}

			JsonElement row = entry.Value;
			doc.Keys(row, at, SettingsUiRowKeys);
			doc.Text(row, "label", at, strict: true);
			doc.Text(row, "description", at, strict: true);
			string type = (doc.Text(row, "type", at, strict: true) ?? string.Empty).Trim().ToLowerInvariant();
			if (type.Length != 0 && Array.IndexOf(SettingsUiTypes, type) < 0)
			{
				doc.Error(At(at, "type"), $"'{type}' is not a row type: slider, toggle or choice (it is drawn as a slider)");
			}

			if (type == "choice")
			{
				ChoiceOptions(doc, row, at);
				if (Has(row, "min") || Has(row, "max") || Has(row, "step"))
				{
					doc.Warn(at, "a choice steps through its options; min, max and step are ignored");
				}

				continue;
			}

			if (Has(row, "options"))
			{
				doc.Warn(At(at, "options"), "read only for \"type\": \"choice\"; ignored");
			}

			double? min = doc.Number(row, "min", at, strict: true);
			double? max = doc.Number(row, "max", at, strict: true);
			double? step = doc.Number(row, "step", at, strict: true);
			if (type == "toggle" && (Has(row, "min") || Has(row, "max") || Has(row, "step")))
			{
				doc.Warn(at, "a toggle steps 0 and 1; min, max and step are ignored");
			}

			if (min is { } low && max is { } high && high <= low)
			{
				doc.Warn(At(at, "max"), $"must be above min ({Format(low)}); the page widens the range itself");
			}

			if (step is <= 0)
			{
				doc.Warn(At(at, "step"), "must be above 0; the page steps a twentieth of the range instead");
			}
		}
	}

	/// <summary>
	/// ModSettingsUi.ChoiceOptions: a list of <c>{ "value": number, "label": text }</c>. An option
	/// with no number is skipped; a choice with none is drawn as a slider.
	/// </summary>
	private static void ChoiceOptions(Doc doc, JsonElement row, string at)
	{
		if (doc.Array(row, "options", at) is not { } options)
		{
			doc.Error(at, "a choice needs \"options\": [{ \"value\": 0, \"label\": \"…\" }, …]; without them the row is a slider");
			return;
		}

		int i = 0;
		int kept = 0;
		var values = new HashSet<double>();
		foreach (JsonElement option in options.EnumerateArray())
		{
			string where = At(At(at, "options"), i++);
			if (!doc.Object(option, where, "must be an object { \"value\", \"label\" }; skipped"))
			{
				continue;
			}

			doc.Keys(option, where, SettingsUiOptionKeys);
			if (!option.TryGetProperty("value", out JsonElement value) ||
				value.ValueKind is not (JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
			{
				doc.Error(At(where, "value"), "must be a number (the setting's value for this option); the option is skipped");
				continue;
			}

			double number = value.ValueKind == JsonValueKind.Number ? value.GetDouble() : value.ValueKind == JsonValueKind.True ? 1 : 0;
			if (!values.Add(number))
			{
				doc.Warn(At(where, "value"), $"a second option with the value {Format(number)}; the first is shown for it");
			}

			if (doc.Text(option, "label", where, strict: true) is null)
			{
				doc.Warn(where, "no \"label\"; the option is captioned by its number");
			}

			kept++;
		}

		if (kept == 0)
		{
			doc.Error(At(at, "options"), "no usable option; the row is a slider");
		}
	}

	// ---------------------------------------------------------------------------------------
	// input.json — ModInput.Load / Parse / TryButton / TryTrigger
	// ---------------------------------------------------------------------------------------

	private static readonly string[] InputKeys = { "label", "keys", "buttons", "axis", "chord", "touch", "wheel", "wheel_icon" };


	/// <summary>The pad modifiers a <c>"chord"</c> may name (ModInputClaims.PadModifiers); anything else is a key name, the engine's to check.</summary>
	private static readonly string[] ChordModifiers = { "LT", "RT", "LB", "RB" };

	/// <summary>
	/// ModInput.TryButton's Xbox aliases and TryTrigger's trigger names. Mirrored, not called:
	/// they are a switch over Godot's <c>JoyButton</c>/<c>JoyAxis</c>, and a Godot type cannot be
	/// touched here.
	/// </summary>
	private static readonly string[] PadAliases =
	{
		"a", "b", "x", "y", "lb", "rb", "l3", "r3", "start", "back", "select", "lt", "rt", "triggerleft", "triggerright",
	};

	/// <summary>Godot 4.7's <c>JoyButton</c> names (Invalid excluded), which <c>Enum.TryParse</c> accepts in any case.</summary>
	private static readonly string[] JoyButtonNames =
	{
		"A", "B", "X", "Y", "Back", "Guide", "Start", "LeftStick", "RightStick", "LeftShoulder", "RightShoulder",
		"DpadUp", "DpadDown", "DpadLeft", "DpadRight", "Misc1", "Misc2", "Misc3", "Misc4", "Misc5", "Misc6",
		"Paddle1", "Paddle2", "Paddle3", "Paddle4", "Touchpad", "SdlMax", "Max",
	};

	/// <summary>Godot 4.7's <c>JoyAxis</c> names (Invalid excluded).</summary>
	private static readonly string[] JoyAxisNames =
	{
		"LeftX", "LeftY", "RightX", "RightY", "TriggerLeft", "TriggerRight", "SdlMax", "Max",
	};

	/// <summary>
	/// An object keyed by action name, each a binding object: <c>label</c> (string), <c>keys</c> and
	/// <c>buttons</c> (a string or a list of strings), <c>axis</c> (a string, optionally signed + or -).
	/// An action that binds nothing is ignored. Key names are the engine's to check.
	/// </summary>
	private static void CheckInput(Doc doc, JsonElement root)
	{
		if (!doc.Object(root, string.Empty, "must be a JSON object of actions keyed by name; the file is ignored"))
		{
			return;
		}

		doc.DuplicateKeys(root, string.Empty);
		foreach (JsonProperty entry in root.EnumerateObject())
		{
			string at = At(string.Empty, entry.Name);
			if (entry.Name.Trim().Length == 0)
			{
				doc.Error(at, "an action needs a name; ignored");
				continue;
			}

			if (!doc.Object(entry.Value, at,
				"must be a binding object { \"keys\", \"buttons\", \"axis\", \"label\" }; the action is ignored"))
			{
				continue;
			}

			JsonElement binding = entry.Value;
			doc.Keys(binding, at, InputKeys);
			doc.Text(binding, "label", at, strict: true);
			int bound = 0;
			List<string>? keys = doc.TextList(binding, "keys", at, single: true, strict: true);
			bound += keys?.Count(k => k.Trim().Length != 0) ?? 0;

			List<string>? buttons = doc.TextList(binding, "buttons", at, single: true, strict: true);
			if (buttons is not null)
			{
				bool single = binding.GetProperty("buttons").ValueKind == JsonValueKind.String;
				for (int i = 0; i < buttons.Count; i++)
				{
					string name = buttons[i].Trim();
					if (name.Length == 0)
					{
						continue;
					}

					string where = single ? At(at, "buttons") : At(At(at, "buttons"), i);
					if (IsPadName(name, PadAliases, JoyButtonNames, out bool numeric))
					{
						bound++;
						if (numeric)
						{
							doc.Warn(where, $"a button by number ({name}); name it (A, B, X, Y, LB, RB, LT, RT, Start, Back...)");
						}
					}
					else
					{
						doc.Error(where, $"'{name}' is not a pad button: A, B, X, Y, LB, RB, L3, R3, LT, RT, Start, Back, " +
							"or a Godot JoyButton name (DpadUp, Guide, Paddle1...); ignored");
					}
				}
			}

			string? axis = doc.Text(binding, "axis", at, strict: true);
			if (axis is not null && axis.Trim().Length != 0)
			{
				string name = axis.Trim();
				if (name[0] is '+' or '-')
				{
					name = name[1..];
				}

				if (IsPadName(name, Array.Empty<string>(), JoyAxisNames, out bool numeric))
				{
					bound++;
					if (numeric)
					{
						doc.Warn(At(at, "axis"), $"an axis by number ({name}); name it (LeftX, LeftY, RightX, RightY...)");
					}
				}
				else
				{
					doc.Error(At(at, "axis"), $"'{axis}' is not a pad axis: LeftX, LeftY, RightX, RightY, TriggerLeft or " +
						"TriggerRight, optionally signed (+LeftY, -LeftY); ignored");
				}
			}

			// The chord: a held LT / RT / LB / RB, or a key name; a chord on nothing is a mistake.
			string? chord = doc.Text(binding, "chord", at, strict: true);
			if (chord is not null && chord.Trim().Length != 0)
			{
				string name = chord.Trim();
				if (!ChordModifiers.Contains(name, StringComparer.OrdinalIgnoreCase) && name.Any(char.IsWhiteSpace))
				{
					doc.Error(At(at, "chord"), $"'{chord}' is neither LT, RT, LB, RB nor a key name; the chord is ignored");
				}
				else if (bound == 0)
				{
					doc.Warn(At(at, "chord"), "a chord on an action that binds nothing never fires");
				}
			}

			if (Has(binding, "wheel") && binding.GetProperty("wheel").ValueKind is not
				(System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False or System.Text.Json.JsonValueKind.String))
			{
				doc.Error(At(at, "wheel"), "not true, false or a string; the action stays off the controller's wheel");
			}

			else if (Has(binding, "wheel") && binding.GetProperty("wheel") is var wheel &&
				(wheel.ValueKind == System.Text.Json.JsonValueKind.True ||
				(wheel.ValueKind == System.Text.Json.JsonValueKind.String && wheel.GetString()!.Trim().Length != 0)))
			{
				bound++;
			}

			doc.Text(binding, "wheel_icon", at, strict: true);

			if (Has(binding, "touch") && CheckInputTouch(doc, binding.GetProperty("touch"), At(at, "touch")))
			{
				bound++;
			}

			if (bound == 0)
			{
				doc.Error(at, "binds no key, button or axis (nor a touch pill or wheel entry); the action is ignored");
			}
		}
	}

	/// <summary>An alias, an enum name in any case, or a number <c>Enum.TryParse</c> would take.</summary>
	private static bool IsPadName(string name, string[] aliases, string[] names, out bool numeric)
	{
		numeric = false;
		if (aliases.Contains(name, StringComparer.OrdinalIgnoreCase) || names.Contains(name, StringComparer.OrdinalIgnoreCase))
		{
			return true;
		}

		numeric = int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 0;
		return numeric;
	}

	// ---------------------------------------------------------------------------------------
	// spells/<id>.json — ModSpells.TryParse
	// ---------------------------------------------------------------------------------------

	private static readonly string[] SpellKeys =
	{
		"replaces", "name", "description", "icon", "card", "damage", "look", "on_cast", "on_hit",
	};

	/// <summary>
	/// <c>replaces</c> (required: a type name or 0..7, resolved by ModSpells.TryResolveType),
	/// <c>name</c>/<c>description</c>/<c>icon</c>/<c>card</c>/<c>on_cast</c>/<c>on_hit</c> (strings),
	/// <c>damage</c> (a number at least 0) and <c>look</c> (one fx recipe body, plus <c>seconds</c>).
	/// </summary>
	private static void CheckSpell(Doc doc, JsonElement root)
	{
		if (!doc.Object(root, string.Empty, "the file is not a JSON object; the spell is refused"))
		{
			return;
		}

		doc.Keys(root, string.Empty, SpellKeys);
		string types = string.Join(", ", ModSpells.TypeNames);
		if (!root.TryGetProperty("replaces", out JsonElement replaces))
		{
			doc.Error("replaces", $"\"replaces\" is required: one of {types} or 0..7; the spell is refused");
		}
		else
		{
			string text = replaces.ValueKind == JsonValueKind.String ? replaces.GetString() ?? string.Empty : replaces.GetRawText();
			if (!ModSpells.TryResolveType(text, out _))
			{
				doc.Error("replaces", $"'{text}' is not a spell type; the binary has exactly {types} (0..7); the spell is refused");
			}
		}

		foreach (string key in new[] { "name", "description", "icon", "card", "on_cast", "on_hit" })
		{
			doc.Text(root, key, string.Empty, strict: true);
		}

		if (root.TryGetProperty("damage", out JsonElement damage) &&
			(damage.ValueKind != JsonValueKind.Number || damage.GetDouble() < 0))
		{
			doc.Error("damage", "must be a number at least 0 (a multiplier on every hit of the type); the spell is refused");
		}

		if (root.TryGetProperty("look", out JsonElement look) &&
			doc.Object(look, "look", "must be an object: one fx recipe body; the spell is refused"))
		{
			CheckRecipe(doc, look, "look", spellLook: true);
		}
	}

	// ---------------------------------------------------------------------------------------
	// fx/recipes.json — Engine_FxRecipes.Parse / ParsePiece, Engine_FxRecipePieces.Build
	// ---------------------------------------------------------------------------------------

	private static readonly string[] RecipeFileKeys = { "recipes" };

	private static readonly string[] RecipeKeys =
	{
		"id", "when", "equals", "base", "tint", "intensity", "size", "attach", "anchor", "light", "pieces",
		"with", "withRadius", "everyNode", "entities",
	};

	private static readonly string[] SpellLookKeys = RecipeKeys.Append("seconds").ToArray();

	private static readonly string[] AttachKeys =
	{
		"effect", "scale", "count", "radius", "height", "turnsPerSecond", "intensity", "stack", "stackStep", "stagger",
		"tint", "radiusStep", "scaleStep", "fadeIn",
	};

	private static readonly string[] LightKeys = { "color", "energy", "range", "height", "flicker" };

	private static readonly string[] PieceKeys =
	{
		"kind", "texture", "color", "colorEnd", "height", "radius", "radiusTop", "rate", "speed", "size", "sizeEnd",
		"count", "period", "width", "mesh", "tilt", "intensity", "blend",
	};

	/// <summary>Engine_FxRecipePieces.Build's kinds; any other is dropped with a warning at build.</summary>
	private static readonly string[] PieceKinds = { "embers", "arcs", "ring", "blades" };

	/// <summary>Engine_FxRecipePieces.Billboard: "mix" covers, everything else adds.</summary>
	private static readonly string[] Blends = { "add", "mix" };

	/// <summary>
	/// An object with a <c>recipes</c> array. A recipe needs <c>id</c>, <c>when</c> (or <c>with</c>: an
	/// effect the mod plays per cast) and a non-empty <c>base</c>.
	/// </summary>
	private static void CheckRecipes(Doc doc, JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object ||
			!root.TryGetProperty("recipes", out JsonElement recipes) || recipes.ValueKind != JsonValueKind.Array)
		{
			doc.Error(string.Empty, "must be an object with a \"recipes\" array; the file is ignored");
			return;
		}

		doc.Keys(root, string.Empty, RecipeFileKeys);
		var ids = new HashSet<string>(StringComparer.Ordinal);
		int i = 0;
		foreach (JsonElement recipe in recipes.EnumerateArray())
		{
			string at = At("recipes", i++);
			if (!doc.Object(recipe, at, "must be an object; skipped"))
			{
				continue;
			}

			string? id = CheckRecipe(doc, recipe, at, spellLook: false);
			if (id is { Length: > 0 } && !ids.Add(id))
			{
				doc.Warn(At(at, "id"), $"a second recipe '{id}' in this file; the later one replaces the earlier");
			}
		}
	}

	/// <summary>One recipe body; returns its id. A spell's look has its id, when and equals filled in by the composer.</summary>
	private static string? CheckRecipe(Doc doc, JsonElement recipe, string at, bool spellLook)
	{
		doc.Keys(recipe, at, spellLook ? SpellLookKeys : RecipeKeys);
		string? id = null;
		if (spellLook)
		{
			foreach (string filled in new[] { "id", "when" })
			{
				if (Has(recipe, filled))
				{
					doc.Warn(At(at, filled), "filled in from the spell (its look key); ignored");
				}
			}

			double? seconds = doc.Number(recipe, "seconds", at, strict: true);
			if (seconds is < 0.1)
			{
				doc.Warn(At(at, "seconds"), "below 0.1; the look is held 0.1 s");
			}
		}
		else
		{
			id = doc.Text(recipe, "id", at, required: true);
			List<string> when = doc.TextOrList(recipe, "when", at) ?? new List<string>();
			if (when.Count == 0 && (doc.Text(recipe, "with", at) ?? string.Empty).Length == 0)
			{
				doc.Error(at, "needs \"when\" (the mod values that switch it on) or \"with\" (an effect the mod plays per cast); skipped");
			}
		}

		doc.Number(recipe, "equals", at, strict: true);
		doc.Number(recipe, "intensity", at, strict: true);
		doc.Number(recipe, "size", at, strict: true);
		doc.Number(recipe, "withRadius", at, strict: true, min: 0);
		doc.Flag(recipe, "everyNode", at);
		List<string>? baseNames = doc.TextList(recipe, "base", at);
		if (baseNames is null || baseNames.All(name => name.Length == 0))
		{
			doc.Error(At(at, "base"), "\"base\" must name at least one effect (the live effects the recipe dresses); skipped");
		}

		doc.TextList(recipe, "anchor", at);
		doc.TextList(recipe, "entities", at);
		doc.Vector(recipe, "tint", at, 3, 3, extraIgnored: true);

		if (doc.Array(recipe, "attach", at) is { } attach)
		{
			int i = 0;
			foreach (JsonElement item in attach.EnumerateArray())
			{
				string where = At(At(at, "attach"), i++);
				if (!doc.Object(item, where, "must be an object; skipped"))
				{
					continue;
				}

				doc.Keys(item, where, AttachKeys);
				doc.Text(item, "effect", where, required: true);
				foreach (string key in new[] { "scale", "radius", "height", "turnsPerSecond", "intensity", "stackStep", "stagger", "radiusStep", "scaleStep", "fadeIn" })
				{
					doc.Number(item, key, where, strict: true);
				}

				doc.Number(item, "count", where, strict: true, integer: true, min: 0);
				doc.Number(item, "stack", where, strict: true, integer: true, min: 1);
				doc.Vector(item, "tint", where, 3, 3, extraIgnored: true);
			}
		}

		if (Has(recipe, "light"))
		{
			JsonElement light = recipe.GetProperty("light");
			string where = At(at, "light");
			if (doc.Object(light, where, "must be an object { \"color\", \"energy\", \"range\", \"height\", \"flicker\" }; ignored"))
			{
				doc.Keys(light, where, LightKeys);
				doc.Vector(light, "color", where, 3, 3, extraIgnored: true);
				doc.Number(light, "energy", where, strict: true, min: 0);
				doc.Number(light, "range", where, strict: true, min: 0);
				doc.Number(light, "height", where, strict: true);
				doc.Number(light, "flicker", where, strict: true, min: 0);
			}
		}

		if (doc.Array(recipe, "pieces", at) is { } pieces)
		{
			int i = 0;
			foreach (JsonElement piece in pieces.EnumerateArray())
			{
				string where = At(At(at, "pieces"), i++);
				if (!doc.Object(piece, where, "must be an object; skipped"))
				{
					continue;
				}

				doc.Keys(piece, where, PieceKeys);
				string kind = doc.Text(piece, "kind", where, required: true) ?? string.Empty;
				if (kind.Length != 0 && Array.IndexOf(PieceKinds, kind) < 0)
				{
					doc.Error(At(where, "kind"), $"'{kind}' is not a piece kind: {string.Join(", ", PieceKinds)}; the piece is not built");
				}

				doc.Text(piece, "texture", where);
				doc.Text(piece, "mesh", where);
				string blend = doc.Text(piece, "blend", where) ?? string.Empty;
				if (blend.Length != 0 && Array.IndexOf(Blends, blend) < 0)
				{
					doc.Error(At(where, "blend"), $"'{blend}' is not a blend: add (lights the scene up) or mix (covers it); drawn as add");
				}

				doc.Vector(piece, "color", where, 3, 4, extraIgnored: true);
				doc.Vector(piece, "colorEnd", where, 3, 4, extraIgnored: true);
				foreach (string key in new[] { "height", "radius", "radiusTop", "rate", "speed", "size", "sizeEnd", "period", "width", "tilt", "intensity" })
				{
					doc.Number(piece, key, where, strict: true);
				}

				doc.Number(piece, "count", where, strict: true, integer: true, min: 0);
			}
		}

		return id;
	}

	// ---------------------------------------------------------------------------------------
	// ui/hud.json — Engine_HudModPieces.Parse / ParseClones / Source / MeterValue
	// ---------------------------------------------------------------------------------------

	private static readonly string[] HudKeys = { "hide", "pieces", "clones", "scale", "layout", "groups" };

	private static readonly string[] HudPieceKeys =
	{
		"id", "kind", "anchor", "offset", "size", "value", "max", "fill", "back", "texture", "follow", "followElement",
		"widthFrom", "widthPerUnit", "minWidth", "maxWidth", "chip", "border", "borderWidth", "corner", "textures", "index",
		"text", "format", "colour", "align", "region", "each", "tint", "group", "outline", "outlineWidth", "shadow", "shadowOffset",
		"touch",
	};

	/// <summary>The fields only a <c>text</c> piece reads.</summary>
	private static readonly string[] HudTextKeys = { "text", "format", "colour", "align", "outline", "outlineWidth", "shadow", "shadowOffset" };

	/// <summary>A <c>groups</c> record's fields (Engine_HudModGroups).</summary>
	private static readonly string[] HudGroupKeys = { "anchor", "pivot", "scale", "offset", "touchScale" };

	/// <summary>A layout entry's fields, as <c>user://hud/layout.json</c> carries them; the last four are informational.</summary>
	private static readonly string[] HudLayoutEntryKeys = { "anchor", "offset", "art", "value", "driven", "placed" };

	private static readonly string[] HudCloneKeys = { "id", "of", "offset", "hue", "hueElements", "icon", "values", "bars", "slides" };

	private static readonly string[] HudIconKeys = { "element", "textures" };

	private static readonly string[] HudPieceKinds = { "meter", "panel", "picture", "text" };

	/// <summary>
	/// <c>hide</c> (ids), <c>scale</c> (id → factor), <c>clones</c> (copies of a shipped piece: id
	/// and of required) and <c>pieces</c> (meter, panel or picture: id required). Values are a
	/// number or a source the HUD reads (Engine_UiValueSources): <c>mod:&lt;key&gt;</c>,
	/// <c>game:&lt;meter&gt;</c>, <c>player:&lt;key&gt;</c>, <c>setting:&lt;mod&gt;.&lt;name&gt;</c>,
	/// <c>hero:health</c>, <c>hero:health_max</c>; colours are HTML hex. <c>groups</c> (id → pivot,
	/// scale, offset) moves the pieces naming it. A <c>widget.</c> id must name one of Engine_HudWidgets.
	/// </summary>
	private static void CheckHud(Doc doc, JsonElement root)
	{
		if (!doc.Object(root, string.Empty, "must be a JSON object; the file is ignored"))
		{
			return;
		}

		doc.Keys(root, string.Empty, HudKeys);
		if (doc.TextList(root, "hide", string.Empty) is { } hidden)
		{
			for (int i = 0; i < hidden.Count; i++)
			{
				UnknownWidget(doc, At("hide", i), hidden[i]);
			}
		}

		foreach (string section in new[] { "layout", "scale" })
		{
			if (Has(root, section) && root.GetProperty(section).ValueKind == JsonValueKind.Object)
			{
				foreach (JsonProperty entry in root.GetProperty(section).EnumerateObject())
				{
					UnknownWidget(doc, At(section, entry.Name), entry.Name);
				}
			}
		}

		if (Has(root, "layout") &&
			doc.Object(root.GetProperty("layout"), "layout", "must be an object of shipped piece id to { \"anchor\", \"offset\" }; ignored"))
		{
			doc.DuplicateKeys(root.GetProperty("layout"), "layout");
			foreach (JsonProperty entry in root.GetProperty("layout").EnumerateObject())
			{
				HudLayoutEntry(doc, entry.Value, At("layout", entry.Name));
			}
		}

		if (Has(root, "scale") && doc.Object(root.GetProperty("scale"), "scale", "must be an object of piece id to factor; ignored"))
		{
			doc.DuplicateKeys(root.GetProperty("scale"), "scale");
			foreach (JsonProperty factor in root.GetProperty("scale").EnumerateObject())
			{
				string at = At("scale", factor.Name);
				if (doc.LooseNumber(factor.Value, at) is <= 0)
				{
					doc.Warn(at, "a factor of 0 or less draws the piece at no size");
				}
			}
		}

		if (doc.Array(root, "clones", string.Empty) is { } clones)
		{
			var ids = new HashSet<string>(StringComparer.Ordinal);
			int i = 0;
			foreach (JsonElement clone in clones.EnumerateArray())
			{
				string at = At("clones", i++);
				if (!doc.Object(clone, at, "must be an object; skipped"))
				{
					continue;
				}

				CheckHudClone(doc, clone, at, ids);
			}
		}

		var groups = new HashSet<string>(StringComparer.Ordinal);
		if (Has(root, Refii.Ui.Engine_HudModGroups.Section) &&
			doc.Object(root.GetProperty(Refii.Ui.Engine_HudModGroups.Section), Refii.Ui.Engine_HudModGroups.Section,
				"must be an object of group id to { \"anchor\", \"pivot\", \"scale\", \"offset\" }; ignored"))
		{
			doc.DuplicateKeys(root.GetProperty(Refii.Ui.Engine_HudModGroups.Section), Refii.Ui.Engine_HudModGroups.Section);
			foreach (JsonProperty entry in root.GetProperty(Refii.Ui.Engine_HudModGroups.Section).EnumerateObject())
			{
				groups.Add(entry.Name);
				CheckHudGroup(doc, entry.Value, At(Refii.Ui.Engine_HudModGroups.Section, entry.Name));
			}
		}

		if (doc.Array(root, "pieces", string.Empty) is { } pieces)
		{
			var ids = new HashSet<string>(StringComparer.Ordinal);
			int i = 0;
			foreach (JsonElement piece in pieces.EnumerateArray())
			{
				string at = At("pieces", i++);
				if (!doc.Object(piece, at, "must be an object; skipped"))
				{
					continue;
				}

				CheckHudPiece(doc, piece, at, ids, groups);
			}
		}
	}

	/// <summary>
	/// One <c>groups</c> record (Engine_HudModGroups): <c>anchor</c> and <c>pivot</c> two numbers
	/// each, <c>scale</c> a number or a value source, <c>offset</c> two of them, a source negated
	/// by a leading <c>-</c>.
	/// </summary>
	private static void CheckHudGroup(Doc doc, JsonElement group, string at)
	{
		if (!doc.Object(group, at, "must be an object { \"anchor\", \"pivot\", \"scale\", \"offset\" }; ignored"))
		{
			return;
		}

		doc.Keys(group, at, HudGroupKeys);
		doc.Vector(group, "anchor", at, 2, 2, extraIgnored: false);
		doc.Vector(group, "pivot", at, 2, 2, extraIgnored: false);
		if (Has(group, "scale"))
		{
			GroupValue(doc, At(at, "scale"), group.GetProperty("scale"));
			if (group.GetProperty("scale").ValueKind == JsonValueKind.Number && group.GetProperty("scale").GetDouble() <= 0)
			{
				doc.Warn(At(at, "scale"), "a scale of 0 or less is read as 1");
			}
		}

		if (doc.Number(group, "touchScale", at) is <= 0)
		{
			doc.Warn(At(at, "touchScale"), "a touchScale of 0 or less is read as 1");
		}

		if (Has(group, "offset"))
		{
			JsonElement offset = group.GetProperty("offset");
			if (offset.ValueKind != JsonValueKind.Array || offset.GetArrayLength() != 2)
			{
				doc.Error(At(at, "offset"), "must be [x, y], each a number or a value source; the group is not offset");
				return;
			}

			for (int i = 0; i < 2; i++)
			{
				GroupValue(doc, At(At(at, "offset"), i), offset[i]);
			}
		}

		if (!Has(group, "scale") && !Has(group, "offset"))
		{
			doc.Warn(at, "states neither \"scale\" nor \"offset\"; it moves nothing");
		}
	}

	/// <summary>A group's value: a number, or a value source with an optional leading <c>-</c>.</summary>
	private static void GroupValue(Doc doc, string at, JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.Number)
		{
			return;
		}

		if (value.ValueKind != JsonValueKind.String)
		{
			doc.Error(at, $"must be a number or a value source, not {Describe(value)}");
			return;
		}

		if (Refii.Ui.Engine_HudModGroups.Problem(value.GetString()) is { } problem)
		{
			doc.Error(at, problem);
		}
	}

	/// <summary>A <c>widget.</c> id that names no widget: the statement reaches nothing.</summary>
	private static void UnknownWidget(Doc doc, string at, string id)
	{
		if (Refii.Ui.Engine_HudWidgets.IsWidget(id) && Refii.Ui.Engine_HudWidgets.Find(id) is null)
		{
			doc.Error(at, $"'{id}' is not a widget: {string.Join(", ", Refii.Ui.Engine_HudWidgets.All.Select(w => w.Id))}; ignored");
		}
	}

	/// <summary>
	/// One <c>layout</c> entry, the shape of <c>user://hud/layout.json</c>'s
	/// (Engine_HudModLayout.ParseEntries): <c>anchor</c> and <c>offset</c>, each two numbers, both
	/// required. The editor's informational fields are allowed and ignored.
	/// </summary>
	private static void HudLayoutEntry(Doc doc, JsonElement entry, string at)
	{
		if (!doc.Object(entry, at, "must be an object { \"anchor\": [x, y], \"offset\": [x, y] }; ignored"))
		{
			return;
		}

		doc.Keys(entry, at, HudLayoutEntryKeys);
		foreach (string pair in new[] { "anchor", "offset" })
		{
			string where = At(at, pair);
			if (!entry.TryGetProperty(pair, out JsonElement value))
			{
				doc.Error(at, $"needs \"{pair}\" as well; an entry missing either is not applied");
				continue;
			}

			if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 2 ||
				value[0].ValueKind != JsonValueKind.Number || value[1].ValueKind != JsonValueKind.Number)
			{
				doc.Error(where, "must be two numbers [x, y]; the entry is not applied");
				continue;
			}

			if (value.GetArrayLength() > 2)
			{
				doc.Warn(where, "only the first two numbers are read");
			}

			if (pair == "anchor" && (value[0].GetDouble() is < 0 or > 1 || value[1].GetDouble() is < 0 or > 1))
			{
				doc.Warn(where, "an anchor is a fraction of the screen, 0..1 each way; this one hangs the piece off screen");
			}
		}
	}

	private static void CheckHudClone(Doc doc, JsonElement clone, string at, HashSet<string> ids)
	{
		doc.Keys(clone, at, HudCloneKeys);
		string? id = doc.Text(clone, "id", at, required: true);
		string? of = doc.Text(clone, "of", at, required: true);
		if (id is { Length: > 0 } && !ids.Add(id))
		{
			doc.Warn(At(at, "id"), $"a second clone '{id}' in this file; the later one replaces the earlier");
		}

		// A copy of a widget (Engine_HudWidgets) draws what the widget draws; it has no meters,
		// art or icon of its own to change.
		if (of is { Length: > 0 } && Refii.Ui.Engine_HudWidgets.IsWidget(of))
		{
			if (Refii.Ui.Engine_HudWidgets.Find(of) is null)
			{
				doc.Error(At(at, "of"), $"'{of}' is not a widget: {string.Join(", ", Refii.Ui.Engine_HudWidgets.All.Select(w => w.Id))}; nothing is copied");
			}

			foreach (string key in new[] { "hue", "hueElements", "icon", "values", "bars", "slides" })
			{
				if (Has(clone, key))
				{
					doc.Warn(At(at, key), "a copy of a widget draws exactly what the widget draws; ignored");
				}
			}
		}

		doc.Vector(clone, "offset", at, 2, 2, extraIgnored: false);
		double? hue = Has(clone, "hue") ? doc.LooseNumber(clone.GetProperty("hue"), At(at, "hue")) : null;
		List<string>? hueElements = doc.TextList(clone, "hueElements", at);
		if (hue is { } shift && shift != 0 && hueElements is not { Count: > 0 })
		{
			doc.Warn(At(at, "hue"), "a hue with no \"hueElements\" recolours nothing");
		}

		if (Has(clone, "icon"))
		{
			string where = At(at, "icon");
			JsonElement icon = clone.GetProperty("icon");
			if (doc.Object(icon, where, "must be an object { \"element\", \"textures\" }; ignored"))
			{
				doc.Keys(icon, where, HudIconKeys);
				doc.Text(icon, "element", where, required: true);
				doc.TextList(icon, "textures", where, required: true);
			}
		}

		var meters = new HashSet<string>(StringComparer.Ordinal);
		if (Has(clone, "values") &&
			doc.Object(clone.GetProperty("values"), At(at, "values"), "must be an object of meter name to source; ignored"))
		{
			doc.DuplicateKeys(clone.GetProperty("values"), At(at, "values"));
			foreach (JsonProperty meter in clone.GetProperty("values").EnumerateObject())
			{
				string where = At(At(at, "values"), meter.Name);
				meters.Add(meter.Name);
				if (doc.LooseText(meter.Value, where) is { } expression)
				{
					MeterExpression(doc, where, expression);
				}
			}
		}

		if (Has(clone, "bars") &&
			doc.Object(clone.GetProperty("bars"), At(at, "bars"), "must be an object of element name to meter name; ignored"))
		{
			doc.DuplicateKeys(clone.GetProperty("bars"), At(at, "bars"));
			foreach (JsonProperty bar in clone.GetProperty("bars").EnumerateObject())
			{
				string where = At(At(at, "bars"), bar.Name);
				if (doc.LooseText(bar.Value, where) is { } meter && !meters.Contains(meter))
				{
					doc.Error(where, $"names meter '{meter}', which \"values\" does not feed; the bar is not redrawn");
				}
			}
		}

		// "slides": element name → the slide number the copy's element stands on, as the game
		// counts them (1 past the master); a number or a numeric string. Which elements own slides
		// is hud.bgf's (the HUD meter lab prints them); it is checked at build.
		if (Has(clone, "slides") &&
			doc.Object(clone.GetProperty("slides"), At(at, "slides"), "must be an object of element name to slide number; ignored"))
		{
			doc.DuplicateKeys(clone.GetProperty("slides"), At(at, "slides"));
			foreach (JsonProperty slide in clone.GetProperty("slides").EnumerateObject())
			{
				string where = At(At(at, "slides"), slide.Name);
				double? number = slide.Value.ValueKind == JsonValueKind.String
					? (double.TryParse(slide.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : null)
					: doc.LooseNumber(slide.Value, where);
				if (number is not { } n || n < 1 || Math.Floor(n) != n)
				{
					doc.Warn(where, "must be a slide number as the game counts them (1 past the master slide); ignored");
				}
			}
		}
	}

	private static void CheckHudPiece(Doc doc, JsonElement piece, string at, HashSet<string> ids, HashSet<string>? groups = null)
	{
		doc.Keys(piece, at, HudPieceKeys);
		string? id = doc.Text(piece, "id", at, required: true);
		if (id is { Length: > 0 } && !ids.Add(id))
		{
			doc.Warn(At(at, "id"), $"a second piece '{id}' in this file; the later one replaces the earlier");
		}

		if (doc.Text(piece, "group", at) is { Length: > 0 } group && groups?.Contains(group) != true)
		{
			doc.Warn(At(at, "group"), $"names '{group}', which this file's \"groups\" does not state; the piece moves with it only if another enabled mod states it");
		}

		string kind = doc.Text(piece, "kind", at) ?? string.Empty;
		if (kind.Length != 0 && Array.IndexOf(HudPieceKinds, kind) < 0)
		{
			doc.Error(At(at, "kind"), $"'{kind}' is not a piece kind: meter, panel, picture or text; it is read as a meter");
			kind = "meter";
		}

		doc.Vector(piece, "anchor", at, 2, 2, extraIgnored: false);
		doc.Vector(piece, "offset", at, 2, 2, extraIgnored: false);
		if (kind == "text")
		{
			CheckHudText(doc, piece, at);
			return;
		}

		foreach (string key in HudTextKeys)
		{
			if (Has(piece, key))
			{
				doc.Warn(At(at, key), "read only by a \"text\" piece; ignored");
			}
		}

		doc.Vector(piece, "size", at, 2, 2, extraIgnored: false);
		foreach (string colour in new[] { "fill", "back", "chip", "border" })
		{
			doc.Colour(piece, colour, at);
		}

		foreach (string number in new[] { "widthPerUnit", "minWidth", "maxWidth", "borderWidth", "corner" })
		{
			if (Has(piece, number))
			{
				doc.LooseNumber(piece.GetProperty(number), At(at, number));
			}
		}

		doc.Text(piece, "follow", at);
		doc.Text(piece, "followElement", at);
		ValueSource(doc, piece, "widthFrom", at, required: false);
		if (kind is "" or "meter")
		{
			ValueSource(doc, piece, "value", at, required: true, why: "a meter needs \"value\"; without it the piece never draws");
			ValueSource(doc, piece, "max", at, required: false);
		}
		else if (kind == "picture")
		{
			if (Has(piece, "textures"))
			{
				List<string>? textures = doc.TextList(piece, "textures", at);
				for (int i = 0; textures is not null && i < textures.Count; i++)
				{
					TexturePath(doc, At(At(at, "textures"), i), textures[i]);
				}

				ValueSource(doc, piece, "index", at, required: true,
					why: "a picture with \"textures\" needs \"index\", the source that picks one; without it the piece never draws");
			}
			else if (doc.Text(piece, "texture", at) is { Length: > 0 } texture)
			{
				TexturePath(doc, At(at, "texture"), texture);
			}
			else
			{
				doc.Warn(at, "a picture with no \"texture\" and no \"textures\" draws only what hud_icon(p, \"<id>\", texture) states for it");
			}

			if (Has(piece, "region") && (piece.GetProperty("region").ValueKind != JsonValueKind.Array ||
				piece.GetProperty("region").GetArrayLength() != 4))
			{
				doc.Error(At(at, "region"), "\"region\" is [u, v, w, h], fractions of the texture (0..1)");
			}

			doc.Colour(piece, "tint", at);
		}
		else if (Has(piece, "tint"))
		{
			doc.Warn(At(at, "tint"), "read only by a \"picture\" piece; ignored");
		}
	}

	/// <summary>
	/// A <c>text</c> piece (Engine_HudModPieces / Engine_HudModText): <c>text</c> required (a literal,
	/// a Babel tag or <c>mod:&lt;key&gt;</c>), <c>format</c> for a value, <c>size</c> a POINT size,
	/// <c>colour</c> HTML hex, <c>align</c> left, centre or right. Placement as every piece.
	/// </summary>
	private static void CheckHudText(Doc doc, JsonElement piece, string at)
	{
		string text = doc.Text(piece, "text", at, required: true) ?? string.Empty;
		string format = doc.Text(piece, "format", at) ?? string.Empty;
		bool value = Refii.Ui.Engine_UiValueSources.Parse(text).Named;
		if (Refii.Ui.Engine_UiValueSources.TextProblem(text) is { } problem)
		{
			doc.Error(At(at, "text"), problem);
		}

		if (format.Length != 0)
		{
			if (!value)
			{
				doc.Warn(At(at, "format"), "formats a value source (mod:, game:, player:, hero:); this text is not one, so it is ignored");
			}
			else if (format.Contains('{', StringComparison.Ordinal) && !format.Contains("{0", StringComparison.Ordinal))
			{
				doc.Warn(At(at, "format"), "names no {0}; the value is not shown in it");
			}
		}

		if (Has(piece, "size") && piece.GetProperty("size").ValueKind == JsonValueKind.Array)
		{
			doc.Error(At(at, "size"), "a text piece's size is its point size (a number), not [w, h]; it is drawn at the default");
		}
		else if (Has(piece, "size") &&
			doc.LooseNumber(piece.GetProperty("size"), At(at, "size")) is { } size && size < 1)
		{
			doc.Error(At(at, "size"), "a text piece's size is its point size, 1 or more; it is drawn at 1");
		}

		doc.Colour(piece, "colour", at);
		if (doc.Text(piece, "align", at) is { Length: > 0 } align &&
			Array.IndexOf(Refii.Ui.Engine_HudModText.AlignNames, align.Trim().ToLowerInvariant()) < 0)
		{
			doc.Warn(At(at, "align"), $"'{align}' is not left, centre or right; it is drawn left-aligned");
		}

		foreach (string key in new[] { "value", "max", "fill", "back", "chip", "border", "texture", "textures", "index", "widthFrom" })
		{
			if (Has(piece, key))
			{
				doc.Warn(At(at, key), "not read by a \"text\" piece; ignored");
			}
		}

		doc.Text(piece, "follow", at);
		doc.Text(piece, "followElement", at);

		// The rims: an outline all round and a drop shadow, each a colour with its width or offset.
		doc.Colour(piece, "outline", at);
		doc.Colour(piece, "shadow", at);
		if (doc.Number(piece, "outlineWidth", at) is { } width && width <= 0)
		{
			doc.Warn(At(at, "outlineWidth"), "an outline 0 wide or less is not drawn");
		}

		doc.Vector(piece, "shadowOffset", at, 2, 2, extraIgnored: false);
		foreach ((string size, string colour) in new[] { ("outlineWidth", "outline"), ("shadowOffset", "shadow") })
		{
			if (Has(piece, size) && !Has(piece, colour))
			{
				doc.Warn(At(at, size), $"read only with \"{colour}\", the colour; ignored");
			}
		}
	}

	/// <summary>A field holding a number or a value source; <paramref name="why"/> is the message for a missing required one.</summary>
	private static void ValueSource(Doc doc, JsonElement record, string key, string at, bool required, string? why = null)
	{
		string where = At(at, key);
		if (!record.TryGetProperty(key, out JsonElement value))
		{
			if (required)
			{
				doc.Error(where, why ?? $"\"{key}\" is required");
			}

			return;
		}

		if (value.ValueKind == JsonValueKind.Number)
		{
			return;
		}

		if (value.ValueKind != JsonValueKind.String)
		{
			doc.Error(where, $"must be a number or a value source, not {Describe(value)}");
			return;
		}

		string text = value.GetString() ?? string.Empty;
		if (text.Trim().Length == 0)
		{
			if (required)
			{
				doc.Error(where, why ?? $"\"{key}\" is empty");
			}

			return;
		}

		Source(doc, where, text);
	}

	/// <summary>
	/// Engine_UiValueSources.Number: a number, <c>mod:&lt;key&gt;</c>, <c>game:&lt;meter&gt;</c>,
	/// <c>player:&lt;key&gt;</c>, <c>setting:&lt;mod&gt;.&lt;name&gt;</c>, <c>hero:health</c> or <c>hero:health_max</c>.
	/// </summary>
	private static void Source(Doc doc, string at, string text)
	{
		if (Refii.Ui.Engine_UiValueSources.NumberProblem(text) is { } problem)
		{
			doc.Error(at, problem);
		}
	}

	/// <summary>Engine_HudModPieces.MeterValue: <c>source</c>, <c>source/cap</c>, or <c>none</c> to hold the meter still.</summary>
	private static void MeterExpression(Doc doc, string at, string expression)
	{
		if (expression == "none")
		{
			return;
		}

		int slash = expression.LastIndexOf('/');
		if (slash <= 0)
		{
			Source(doc, at, expression);
			return;
		}

		Source(doc, at, expression[..slash]);
		Source(doc, at, expression[(slash + 1)..]);
	}

	/// <summary>A picture piece's texture goes through Godot's ResourceLoader, which resolves a scheme.</summary>
	private static void TexturePath(Doc doc, string at, string path)
	{
		// A HUD picture loads any path (Engine_HudModPieces.LoadAnyTexture): res://, a mod's PNG, or a
		// game texture with or without game://. Only a path that can be none of them is named.
		if (path.Length != 0 && !path.Contains("://", StringComparison.Ordinal) &&
			!path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
		{
			doc.Warn(at, $"'{path}' is neither a game texture (.tex) nor a mod's PNG; a HUD picture loads art/…/x.tex, art/x.png or res://");
		}
	}

	// ---------------------------------------------------------------------------------------
	// ui/templates.json — Engine_UiTemplates.ApplyOverrideText / PatchMenu / ParseMenu / ParseEntry / ParseScreen
	// ---------------------------------------------------------------------------------------

	private static readonly string[] TemplatesKeys = { "menus", "screens", "modals", "rowActions" };

	private static readonly string[] RowActionKeys = { "button", "tag", "command" };

	private static readonly string[] ModalKeys = { "kind", "confirm", "cancel", "alternate" };

	private static readonly string[] MenuKeys = { "patch", "highlight", "title", "rowCommand", "source", "entries" };

	private static readonly string[] EntryKeys =
	{
		"name", "tag", "command", "opens", "hostOnly", "drop", "source", "icon", "eventType", "rating", "counted", "children",
		"when",
	};

	/// <summary>PatchMenu's per-entry placement fields, read only in a <c>"patch": true</c> menu.</summary>
	private static readonly string[] PatchEntryKeys = { "parent", "after", "before", "remove" };

	private static readonly string[] EntryAndPatchKeys = EntryKeys.Concat(PatchEntryKeys).ToArray();

	private static readonly string[] ScreenTextKeys =
	{
		"name", "menuScript", "previewAnchor", "pageElement", "menu", "provider", "revealedAccept", "revealedBack",
		"flipAction", "xAction", "rowAction", "scrollContent", "scrollMask", "scrollBar", "flowMeasured", "flowBase", "flowTailAnchor",
		"presentation", "pageRootUnit", "backdropUnit", "boxPresentation",
	};

	private static readonly string[] ScreenFlagKeys =
	{
		"ownsList", "restFromFile", "purse", "flowWhenDrawn", "commitsByHighlight", "ownsRevealedInput", "hasSpellBlade", "fadesTopEnd", "fadesBottomEnd",
	};

	private static readonly string[] ScreenListKeys =
	{
		"chrome", "panelSubtrees", "ownedElsewhere", "boxUnits", "pages", "guestTextUnits",
		"wideLeft", "wideRight", "wideGround", "wideStretch", "flowMoved", "flowTailMoved",
	};

	private static readonly string[] ScreenKeys = ScreenTextKeys.Concat(ScreenFlagKeys).Concat(ScreenListKeys)
		.Concat(new[] { "pageSlide", "listLift", "boxPaths", "pageStackAt", "pageSpan", "statFields", "panelFields", "menuScriptByMode", "restPose", "restOpacity" })
		.Concat(new[] { "patch", "fields", "switches" })
		.ToArray();

	/// <summary>
	/// <c>menus</c> (by name: a whole menu, or with <c>"patch": true</c> per-entry edits by name) and
	/// <c>screens</c> (by numeric screen id: the whole record, or with <c>"patch": true</c> per field).
	/// </summary>
	private static void CheckTemplates(Doc doc, JsonElement root)
	{
		if (!doc.Object(root, string.Empty, "must be a JSON object; the file is ignored"))
		{
			return;
		}

		doc.Keys(root, string.Empty, TemplatesKeys);
		if (Has(root, "menus") && doc.Object(root.GetProperty("menus"), "menus", "must be an object of menus by name; ignored"))
		{
			doc.DuplicateKeys(root.GetProperty("menus"), "menus");
			foreach (JsonProperty menu in root.GetProperty("menus").EnumerateObject())
			{
				string at = At("menus", menu.Name);
				if (!doc.Object(menu.Value, at, "must be an object; the menu is ignored"))
				{
					continue;
				}

				doc.Keys(menu.Value, at, MenuKeys);
				bool patch = doc.Flag(menu.Value, "patch", at) ?? false;
				foreach (string key in new[] { "highlight", "title", "rowCommand", "source" })
				{
					doc.Text(menu.Value, key, at);
				}

				if (menu.Value.TryGetProperty("rowCommand", out JsonElement rowCommand) && rowCommand.ValueKind == JsonValueKind.String)
				{
					MenuCommand(doc, At(at, "rowCommand"), rowCommand.GetString() ?? string.Empty);
				}

				if (doc.Array(menu.Value, "entries", at) is { } entries)
				{
					Entries(doc, entries, At(at, "entries"), patch);
				}
			}
		}

		if (Has(root, "modals") &&
			doc.Object(root.GetProperty("modals"), "modals", "must be an object of modal records by key (\"13\", \"0@22\"); ignored"))
		{
			doc.DuplicateKeys(root.GetProperty("modals"), "modals");
			foreach (JsonProperty modal in root.GetProperty("modals").EnumerateObject())
			{
				string at = At("modals", modal.Name);
				if (!Refii.Ui.Engine_UiModals.TryParseKey(modal.Name, out _, out _))
				{
					doc.Error(at, $"'{modal.Name}' is not a modal key: \"<modal id>\" or \"<modal id>@<screen id>\"; ignored");
					continue;
				}

				if (modal.Value.ValueKind == JsonValueKind.Null)
				{
					continue;
				}

				if (!doc.Object(modal.Value, at, "must be an object { \"kind\", \"confirm\", \"cancel\" } or null; ignored"))
				{
					continue;
				}

				doc.Keys(modal.Value, at, ModalKeys);
				if (Refii.Ui.Engine_UiModals.ParseRecord(modal.Name, modal.Value, out string error) is null)
				{
					doc.Error(at, error + "; the record is ignored");
				}
			}
		}

		if (Has(root, Refii.Ui.Engine_UiRowActions.Section))
		{
			CheckRowActions(doc, root.GetProperty(Refii.Ui.Engine_UiRowActions.Section));
		}

		if (Has(root, "screens") && doc.Object(root.GetProperty("screens"), "screens", "must be an object of screens by numeric id; ignored"))
		{
			doc.DuplicateKeys(root.GetProperty("screens"), "screens");
			foreach (JsonProperty screen in root.GetProperty("screens").EnumerateObject())
			{
				string at = At("screens", screen.Name);
				if (!uint.TryParse(screen.Name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
				{
					doc.Error(at, $"'{screen.Name}' is not a screen id; a screen is keyed by its number (\"5\"), so it is ignored");
					continue;
				}

				if (doc.Object(screen.Value, at, "must be an object; the screen is ignored"))
				{
					CheckScreen(doc, screen.Value, at);
					CheckScreenData(doc, screen.Value, at, uint.Parse(screen.Name, CultureInfo.InvariantCulture));
				}
			}
		}
	}

	private static void Entries(Doc doc, JsonElement entries, string at, bool patch)
	{
		int i = 0;
		foreach (JsonElement entry in entries.EnumerateArray())
		{
			string where = At(at, i++);
			if (!doc.Object(entry, where, "must be an object; skipped"))
			{
				continue;
			}

			doc.Keys(entry, where, EntryAndPatchKeys);
			if (!patch)
			{
				foreach (string key in PatchEntryKeys)
				{
					if (Has(entry, key))
					{
						doc.Warn(At(where, key), "read only in a \"patch\": true menu; ignored here");
					}
				}
			}

			doc.Text(entry, "name", where, required: true);
			foreach (string key in new[] { "tag", "command", "opens", "source", "icon" })
			{
				doc.Text(entry, key, where);
			}

			if (doc.Text(entry, "when", where) is { Length: > 0 } when)
			{
				RowCondition(doc, At(where, "when"), when);
			}

			if (entry.TryGetProperty("source", out JsonElement entrySource) && entrySource.ValueKind == JsonValueKind.String)
			{
				RowSource(doc, At(where, "source"), entrySource.GetString() ?? string.Empty);
			}

			if (entry.TryGetProperty("command", out JsonElement command) && command.ValueKind == JsonValueKind.String)
			{
				MenuCommand(doc, At(where, "command"), command.GetString() ?? string.Empty);
			}

			if (entry.TryGetProperty("opens", out JsonElement opens) && opens.ValueKind == JsonValueKind.String &&
				(opens.GetString() ?? string.Empty).Trim().Length == 0)
			{
				doc.Error(At(where, "opens"), "names no menu; the entry opens nothing");
			}

			foreach (string key in new[] { "hostOnly", "drop", "counted" })
			{
				doc.Flag(entry, key, where);
			}

			foreach (string key in new[] { "eventType", "rating" })
			{
				if (Has(entry, key))
				{
					doc.LooseNumber(entry.GetProperty(key), At(where, key), integer: true);
				}
			}

			if (patch)
			{
				doc.Text(entry, "parent", where);
				doc.Text(entry, "after", where);
				doc.Text(entry, "before", where);
				doc.Flag(entry, "remove", where);
				if (Has(entry, "after") && Has(entry, "before"))
				{
					doc.Warn(where, "both \"after\" and \"before\"; \"after\" is tried first and \"before\" only when that entry is missing");
				}
			}

			// Children are parsed as whole entries, never as patches.
			if (doc.Array(entry, "children", where) is { } children)
			{
				Entries(doc, children, At(where, "children"), patch: false);
			}
		}
	}

	/// <summary>
	/// A row command of the mod-menu grammar (Engine_UiModMenuCommand): <c>Opens:&lt;menu&gt;</c> must
	/// name a menu, <c>Mod:lua:&lt;function&gt;[:&lt;arg&gt;]</c> a Lua name path. Whether the menu
	/// exists is the engine's to say — another mod may define it.
	/// </summary>
	/// <summary>
	/// <c>rowActions</c> (Engine_UiRowActions.ApplyText): an object of lists by menu template name
	/// (<c>ItemsMenu</c>, <c>WeaponsMenu</c>, <c>ExpressionsMenu</c>, <c>AbilitiesMenu</c>, a mod menu's
	/// name), each record <c>{ "button": X | Y, "tag", "command" }</c>; <c>null</c> or <c>[]</c> removes
	/// the list.
	/// </summary>
	private static void CheckRowActions(Doc doc, JsonElement block)
	{
		string section = Refii.Ui.Engine_UiRowActions.Section;
		if (!doc.Object(block, section, "must be an object of action lists by menu template name; ignored"))
		{
			return;
		}

		doc.DuplicateKeys(block, section);
		foreach (JsonProperty menu in block.EnumerateObject())
		{
			string at = At(section, menu.Name);
			if (menu.Name.Trim().Length == 0)
			{
				doc.Error(at, "a menu needs a name; ignored");
				continue;
			}

			if (menu.Value.ValueKind == JsonValueKind.Null)
			{
				continue;
			}

			if (menu.Value.ValueKind != JsonValueKind.Array)
			{
				doc.Error(at, "must be a list of { \"button\", \"tag\", \"command\" } or null; ignored");
				continue;
			}

			int index = 0;
			foreach (JsonElement record in menu.Value.EnumerateArray())
			{
				string where = At(at, index++);
				if (!doc.Object(record, where, "must be an object { \"button\", \"tag\", \"command\" }; ignored"))
				{
					continue;
				}

				doc.Keys(record, where, RowActionKeys);
				doc.Text(record, "tag", where, strict: true);
				string? button = doc.Text(record, "button", where, required: true, strict: true);
				if (button is not null && !Refii.Ui.Engine_UiRowActions.Buttons.Contains(button.Trim(), StringComparer.OrdinalIgnoreCase))
				{
					doc.Error(At(where, "button"), $"'{button}' is not a button a screen list can hold: {string.Join(" or ", Refii.Ui.Engine_UiRowActions.Buttons)}; ignored");
				}

				string? command = doc.Text(record, "command", where, required: true, strict: true);
				if (command is not null && command.Trim().Length != 0)
				{
					MenuCommand(doc, At(where, "command"), command.Trim());
				}
			}
		}
	}

	private static void MenuCommand(Doc doc, string at, string command)
	{
		if (command.StartsWith(Refii.Ui.Engine_UiHostScreens.OpenPrefix, StringComparison.Ordinal) &&
			Refii.Ui.Engine_UiHostScreens.ParseId(command[Refii.Ui.Engine_UiHostScreens.OpenPrefix.Length..]) is null)
		{
			doc.Error(at, $"'{command}' names no screen id: Screen:open:<id>; the row opens nothing");
		}
		else if (command.StartsWith(Refii.Ui.Engine_UiModMenuCommand.OpensPrefix, StringComparison.Ordinal) &&
			Refii.Ui.Engine_UiModMenuCommand.Parse(command).Verb == Refii.Ui.Engine_UiModMenuCommand.Verb.None)
		{
			doc.Error(at, "'Opens:' names no menu; the row opens nothing");
		}
		else if (command.StartsWith(Refii.Ui.Engine_UiModMenuCommand.LuaPrefix, StringComparison.Ordinal) &&
			Refii.Ui.Engine_UiModMenuCommand.Parse(command).Verb == Refii.Ui.Engine_UiModMenuCommand.Verb.None)
		{
			doc.Error(at, $"'{command}' does not name a Lua function: Mod:lua:<name or table.name>[:<arg>]; the row calls nothing");
		}
	}

	/// <summary>
	/// A row condition (Engine_UiRowConditions): names joined by <c>,</c>, each optionally <c>!</c>.
	/// A name the game does not state is a warning — a Godot script may register it — and the row
	/// is hidden until something does.
	/// </summary>
	private static void RowCondition(Doc doc, string at, string when)
	{
		foreach (string name in Refii.Ui.Engine_UiRowConditions.NamesIn(when))
		{
			if (!Refii.Ui.Engine_UiRowConditions.Game.Contains(name))
			{
				doc.Warn(at, $"'{name}' is not a condition the game states ({string.Join(", ", Refii.Ui.Engine_UiRowConditions.Game)}); " +
					"unless a script registers it the row never shows");
			}
		}
	}

	/// <summary>A mod row source (Engine_UiModRows): <c>values:</c> must bind something readable.</summary>
	private static void RowSource(Doc doc, string at, string source)
	{
		const string values = Refii.Ui.Engine_UiModRows.ValuesSource + ":";
		if (!source.StartsWith(values, StringComparison.Ordinal))
		{
			return;
		}

		string[] items = source[values.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (items.Length == 0)
		{
			doc.Error(at, "'values:' binds nothing: values:<mod:key or player:key>[|format]=<label>,...; the menu shows no rows");
		}

		foreach (string item in items)
		{
			int equals = item.IndexOf('=');
			string binding = equals < 0 ? item : item[..equals];
			if (!Refii.Ui.Engine_UiValueSource.IsValue(binding))
			{
				binding = Refii.Ui.Engine_UiValueSource.ModPrefix + binding;
			}

			if (Refii.Ui.Engine_UiValueSource.Problem(binding) is { } problem)
			{
				doc.Error(at, $"'{item}': {problem}");
			}
		}
	}

	/// <summary>
	/// A screen's <c>patch</c> flag, its data page (<c>fields</c>, <c>switches</c>: element path to a
	/// binding) and whether a host screen (id 1000 and up) can be stood by <c>Screen:open:</c>.
	/// </summary>
	private static void CheckScreenData(Doc doc, JsonElement screen, string at, uint id)
	{
		bool patch = doc.Flag(screen, "patch", at) ?? false;
		foreach (string map in new[] { "fields", "switches" })
		{
			if (!Has(screen, map))
			{
				continue;
			}

			string where = At(at, map);
			if (!doc.Object(screen.GetProperty(map), where, "must be an object of element path to binding; ignored"))
			{
				continue;
			}

			foreach (JsonProperty field in screen.GetProperty(map).EnumerateObject())
			{
				if (field.Value.ValueKind == JsonValueKind.Null && patch)
				{
					continue;
				}

				if (doc.LooseText(field.Value, At(where, field.Name)) is { } binding &&
					Refii.Ui.Engine_UiValueSource.Problem(binding) is { } problem)
				{
					doc.Error(At(where, field.Name), problem);
				}
			}
		}

		if (screen.TryGetProperty("provider", out JsonElement provider) && provider.ValueKind == JsonValueKind.String &&
			provider.GetString() is { Length: > 0 } name && !Refii.Ui.Engine_UiPageProviders.ShippedNames.Contains(name))
		{
			doc.Warn(At(at, "provider"), $"'{name}' is not one of the game's page providers " +
				$"({string.Join(", ", Refii.Ui.Engine_UiPageProviders.ShippedNames)}); unless a script registers it the page stays empty");
		}

		if (!patch && id >= Refii.Ui.Engine_UiBookSet.HostOnlyScreenId &&
			(Has(screen, "fields") || Has(screen, "switches")) && (!Has(screen, "menu") || !Has(screen, "presentation")))
		{
			doc.Warn(at, "a host screen states a \"menu\" and a \"presentation\" to be opened by Screen:open:; this one cannot stand");
		}
	}

	private static void CheckScreen(Doc doc, JsonElement screen, string at)
	{
		doc.Keys(screen, at, ScreenKeys);
		foreach (string key in ScreenTextKeys)
		{
			doc.Text(screen, key, at);
		}

		foreach (string key in ScreenFlagKeys)
		{
			doc.Flag(screen, key, at);
		}

		foreach (string key in ScreenListKeys)
		{
			doc.TextList(screen, key, at);
		}

		if (Has(screen, "pageSlide"))
		{
			doc.LooseNumber(screen.GetProperty("pageSlide"), At(at, "pageSlide"), integer: true);
		}

		if (Has(screen, "listLift"))
		{
			doc.LooseNumber(screen.GetProperty("listLift"), At(at, "listLift"));
		}

		doc.Vector(screen, "pageStackAt", at, 3, 3, extraIgnored: false);
		doc.Vector(screen, "pageSpan", at, 2, 2, extraIgnored: false);
		if (Has(screen, "boxPaths") &&
			doc.Object(screen.GetProperty("boxPaths"), At(at, "boxPaths"), "must be an object of path to path; ignored"))
		{
			foreach (JsonProperty path in screen.GetProperty("boxPaths").EnumerateObject())
			{
				doc.LooseText(path.Value, At(At(at, "boxPaths"), path.Name));
			}
		}

		if (Has(screen, "menuScriptByMode") &&
			doc.Object(screen.GetProperty("menuScriptByMode"), At(at, "menuScriptByMode"), "must be an object of mode number to script; ignored"))
		{
			foreach (JsonProperty mode in screen.GetProperty("menuScriptByMode").EnumerateObject())
			{
				string where = At(At(at, "menuScriptByMode"), mode.Name);
				if (!int.TryParse(mode.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
				{
					doc.Error(where, $"'{mode.Name}' is not a mode number; ignored");
				}
				else if (doc.LooseText(mode.Value, where) is not { Length: > 0 })
				{
					doc.Error(where, "names no script; ignored");
				}
			}
		}

		if (doc.Array(screen, "statFields", at) is { } stats)
		{
			int i = 0;
			foreach (JsonElement stat in stats.EnumerateArray())
			{
				string where = At(At(at, "statFields"), i++);
				if (!doc.Object(stat, where, "must be an object { \"id\", \"path\" }; skipped"))
				{
					continue;
				}

				doc.Keys(stat, where, new[] { "id", "path" });
				if (!Has(stat, "id") || !Has(stat, "path"))
				{
					doc.Error(where, "a stat field needs both \"id\" and \"path\"; skipped");
				}

				if (Has(stat, "id"))
				{
					doc.LooseNumber(stat.GetProperty("id"), At(where, "id"), integer: true);
				}

				doc.Text(stat, "path", where);
			}
		}

		// THE INFO PANEL DRAWS NOTHING (Engine_UiScreenPanel): the presentation it painted from is
		// headless, so a stated field is read and kept, and the mod is told.
		foreach (string inert in new[] { "panelFields", "panelSubtrees" })
		{
			if (screen.TryGetProperty(inert, out JsonElement list) &&
				list.ValueKind == JsonValueKind.Array && list.GetArrayLength() != 0)
			{
				doc.Warn(At(at, inert), "is read but draws nothing: the info panel painted the guest's " +
					"own presentation text, and presentations are headless now. A game screen's page text is " +
					"its provider's; a host screen states its own with \"provider\": \"data\" and \"fields\"");
			}
		}

		if (doc.Array(screen, "panelFields", at) is { } fields)
		{
			int i = 0;
			foreach (JsonElement field in fields.EnumerateArray())
			{
				string where = At(At(at, "panelFields"), i++);
				if (!doc.Object(field, where, "must be an object { \"path\", \"multiline\", \"alternate\" }; skipped"))
				{
					continue;
				}

				doc.Keys(field, where, new[] { "path", "multiline", "alternate" });
				doc.Text(field, "path", where, required: true);
				doc.Flag(field, "multiline", where);
				doc.Flag(field, "alternate", where);
			}
		}
	}

	// ---------------------------------------------------------------------------------------
	// ui/spellwheel.json — Engine_UiSpellPicks.Parse
	// ---------------------------------------------------------------------------------------

	private static readonly string[] PickKeys =
	{
		"name", "store", "inEffect", "unlocked", "exclude", "empty", "scale", "apply", "remove",
		"legacySaveKey", "legacySaveKeyTwo",
	};

	/// <summary>
	/// An object with a <c>picks</c> list: each needs <c>name</c> and <c>store</c> (a player value
	/// key with <c>{slot}</c>, which the engine writes); <c>inEffect</c> and <c>unlocked</c> are value
	/// sources, <c>scale</c> 0.2..1, <c>exclude</c> a list of will types 0..7. Every enabled mod's
	/// picks are drawn under each slot, so a <c>name</c> without the mod's prefix is warned: a later
	/// mod's pick of the same name replaces it.
	/// </summary>
	private static void CheckSpellWheel(Doc doc, JsonElement root)
	{
		if (root.ValueKind == JsonValueKind.Object && Has(root, "secondary"))
		{
			doc.Error("secondary", "is the old setting-channel form; state the pick in a \"picks\" list " +
				"(name, store, inEffect, unlocked …): the engine stores each player's pick and raises SpellPick");
		}

		if (root.ValueKind != JsonValueKind.Object ||
			!root.TryGetProperty("picks", out JsonElement picks) || picks.ValueKind != JsonValueKind.Array)
		{
			doc.Error(string.Empty, "must be an object with a \"picks\" list; the file is ignored");
			return;
		}

		doc.Keys(root, string.Empty, new[] { "picks", "secondary" });
		var names = new HashSet<string>(StringComparer.Ordinal);
		int n = 0;
		foreach (JsonElement pick in picks.EnumerateArray())
		{
			string at = At("picks", n++);
			if (!doc.Object(pick, at, "must be an object; skipped"))
			{
				continue;
			}

			doc.Keys(pick, at, PickKeys);
			string? name = doc.Text(pick, "name", at, required: true);
			if (name is { Length: > 0 } && name.Any(char.IsWhiteSpace))
			{
				doc.Error(At(at, "name"), "must have no spaces: the SpellPick event carries it");
			}

			if (name is { Length: > 0 } && !names.Add(name))
			{
				doc.Warn(At(at, "name"), $"a second pick '{name}' in this file; the later one replaces the earlier");
			}

			// Every mod's picks are drawn side by side, and a later mod's pick of the SAME name
			// replaces an earlier one's: a bare name meets another mod's by accident.
			if (name is { Length: > 0 } && !name.Contains('.', StringComparison.Ordinal))
			{
				doc.Warn(At(at, "name"), $"'{name}' has no mod prefix ('my_mod.{name}'): a later mod's pick of the same name replaces this one on the selectors");
			}

			string? store = doc.Text(pick, "store", at, required: true);
			if (store is { Length: > 0 })
			{
				if (store == Engine_ModUiFiles.GameStore)
				{
					doc.Error(At(at, "store"), "\"game\" is the slot's own spell, the game's pick; a mod's pick is a player value key");
				}
				else if (!PlayerKey(store.Replace(Engine_ModUiFiles.SlotToken, "1", StringComparison.Ordinal)))
				{
					doc.Error(At(at, "store"), $"'{store}' is not a player value key (letters, digits, '_', '.', '-', and {{slot}})");
				}
				else if (!store.Contains(Engine_ModUiFiles.SlotToken, StringComparison.Ordinal))
				{
					doc.Warn(At(at, "store"), "has no {slot}: one pick serves every slot");
				}
			}

			foreach (string key in new[] { "inEffect", "unlocked" })
			{
				if (doc.Text(pick, key, at) is { Length: > 0 } source && Refii.Ui.Engine_UiValueSources.NumberProblem(source) is { } problem)
				{
					doc.Error(At(at, key), problem);
				}
			}

			foreach (string key in new[] { "empty", "apply", "remove" })
			{
				doc.Text(pick, key, at);
			}

			// RefiiModSave keys a save from before kept each slot's pick in ({slot} the slot's number):
			// the engine declaration carries them as one word each.
			foreach (string key in new[] { "legacySaveKey", "legacySaveKeyTwo" })
			{
				if (doc.Text(pick, key, at) is { Length: > 0 } legacy && legacy.Any(char.IsWhiteSpace))
				{
					doc.Error(At(at, key), "must have no spaces: the engine's declaration carries it as one word");
				}
			}

			doc.Number(pick, "scale", at, strict: true, min: 0.2, max: 1, clamps: true);
			if (doc.Array(pick, "exclude", at) is { } exclude)
			{
				int i = 0;
				foreach (JsonElement item in exclude.EnumerateArray())
				{
					string where = At(At(at, "exclude"), i++);
					if (doc.LooseNumber(item, where, integer: true) is { } type && (type < 0 || type >= ModSpells.TypeNames.Length))
					{
						doc.Warn(where, $"{Format(type)} is no will type; the eight are 0..7 ({string.Join(", ", ModSpells.TypeNames)})");
					}
				}
			}
		}
	}

	// ---------------------------------------------------------------------------------------
	// ui/abilities.json — Engine_UiModAbilities.Load / Pool
	// ---------------------------------------------------------------------------------------

	private static readonly string[] AbilityKeys =
	{
		"name", "category", "nameTag", "descriptionTag", "maxLevel", "levelKey", "costLike", "costLikeSetting",
		"refund", "legacySaveKey", "stats", "stat", "image", "disciplines", "totalXpTag",
	};

	/// <summary>The fields of the setting-counter form the engine's purchase replaced, and what took each one's place.</summary>
	private static readonly (string Key, string Instead)[] RetiredAbilityKeys =
	{
		("level", "levelKey: the saved player value the engine keeps the level in"),
		("cost", "nothing: the engine states the price (<levelKey>.cost) from costLike"),
		("buy", "nothing: A runs the engine's purchase"),
		("refundValue", "nothing: the engine states the refund (<levelKey>.refund)"),
	};

	/// <summary>Engine_UiModAbilities.Pool's disciplines; any other draws on General.</summary>
	private static readonly string[] Disciplines = { "Strength", "Skill", "Will", "General" };

	/// <summary>A player store key: letters, digits, '_', '.', '-' (src/runtime/src/core/player_state.cpp KeyChar), at most 120 so the engine's suffixes fit.</summary>
	private static bool PlayerKey(string key) =>
		key.Length is > 0 and <= 120 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

	/// <summary>
	/// An object with an <c>abilities</c> array; each needs <c>name</c>, <c>category</c>,
	/// <c>levelKey</c> and <c>costLike</c> — the engine runs the purchase from them (Engine_UiModAbilities.Parse).
	/// </summary>
	private static void CheckAbilities(Doc doc, JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object ||
			!root.TryGetProperty("abilities", out JsonElement abilities) || abilities.ValueKind != JsonValueKind.Array)
		{
			doc.Error(string.Empty, "must be an object with an \"abilities\" array; the file is ignored");
			return;
		}

		doc.Keys(root, string.Empty, new[] { "abilities" });
		var names = new HashSet<string>(StringComparer.Ordinal);
		int i = 0;
		foreach (JsonElement ability in abilities.EnumerateArray())
		{
			string at = At("abilities", i++);
			if (!doc.Object(ability, at, "must be an object; skipped"))
			{
				continue;
			}

			foreach ((string key, string instead) in RetiredAbilityKeys)
			{
				if (Has(ability, key))
				{
					doc.Error(At(at, key), $"is the old setting-counter form; the engine runs the purchase now — use {instead}");
				}
			}

			doc.Keys(ability, at, AbilityKeys.Concat(RetiredAbilityKeys.Select(r => r.Key)).ToArray());
			string? name = doc.Text(ability, "name", at, required: true);
			string? category = doc.Text(ability, "category", at, required: true);
			string? levelKey = doc.Text(ability, "levelKey", at, required: true);
			if (levelKey is { Length: > 0 } && !PlayerKey(levelKey))
			{
				doc.Error(At(at, "levelKey"), $"'{levelKey}' is not a player value key (letters, digits, '_', '.', '-'; at most 120)");
			}

			if (name is { Length: > 0 } && name.Any(char.IsWhiteSpace))
			{
				doc.Error(At(at, "name"), "must have no spaces: the engine's purchase names the ability by it");
			}

			if (!Has(ability, "costLike"))
			{
				doc.Error(At(at, "costLike"), "is required: the ability type (1..14) whose experience table prices the levels");
			}
			else
			{
				doc.Number(ability, "costLike", at, integer: true, min: 1, max: 14);
			}

			foreach (string key in new[] { "nameTag", "descriptionTag", "costLikeSetting", "legacySaveKey", "image", "totalXpTag" })
			{
				doc.Text(ability, key, at);
			}

			if (doc.Text(ability, "refund", at) is { } refund && refund != Engine_ModUiFiles.HalfRefund && refund != "none")
			{
				doc.Error(At(at, "refund"), $"'{refund}' is no refund rule: \"half\" (the shipped unlearn) or \"none\"");
			}

			if (doc.Text(ability, "stat", at) is { Length: > 0 } stat && Refii.Ui.Engine_UiValueSources.NumberProblem(stat) is { } problem)
			{
				doc.Error(At(at, "stat"), problem);
			}

			double? maxLevel = Has(ability, "maxLevel")
				? doc.LooseNumber(ability.GetProperty("maxLevel"), At(at, "maxLevel"), integer: true, min: 1)
				: 5;
			if (doc.Array(ability, "stats", at) is { } stats)
			{
				int n = 0;
				foreach (JsonElement value in stats.EnumerateArray())
				{
					doc.LooseNumber(value, At(At(at, "stats"), n++));
				}

				if (maxLevel is { } levels && n != (int)levels + 1)
				{
					doc.Warn(At(at, "stats"), $"states {n} value(s); one per level 0..{(int)levels} ({(int)levels + 1}) reads each level's own");
				}
			}

			List<string>? disciplines = doc.TextList(ability, "disciplines", at);
			foreach ((string discipline, int index) in (disciplines ?? new List<string>()).Select((d, n) => (d, n)))
			{
				if (discipline.Length != 0 && Array.IndexOf(Disciplines, discipline) < 0)
				{
					doc.Warn(At(At(at, "disciplines"), index),
						$"'{discipline}' is not a discipline ({string.Join(", ", Disciplines)}); its share is drawn from General");
				}
			}

			if (category == "Hybrid" && disciplines is not { Count: > 0 })
			{
				doc.Warn(At(at, "category"), "a Hybrid ability names its \"disciplines\"; without them it draws on General alone");
			}

			if (name is { Length: > 0 } && !names.Add(name))
			{
				doc.Warn(At(at, "name"), $"a second ability '{name}' in this file; the later one replaces the earlier");
			}
		}
	}

	// ---------------------------------------------------------------------------------------
	// mod.toml — ModWorkspace.ParseModToml / ParseArray, ModRequirement.Parse
	// ---------------------------------------------------------------------------------------

	private static readonly string[] ManifestKeys = { "name", "title", "version", "author", "description", "requires", "after" };

	/// <summary>Comparison operators ModRequirement.Parse does not understand; only <c>&gt;=</c> is.</summary>
	private static readonly char[] VersionOperators = { '<', '>', '=', '!', '^', '~' };

	/// <summary>
	/// One <c>key = value</c> per line, read line by line: <c>#</c> comments and <c>[table]</c> lines are
	/// skipped (their keys still read as top-level), strings quoted, <c>requires</c>/<c>after</c> a list
	/// on ONE line, each requirement <c>name</c> or <c>name &gt;= version</c>.
	/// </summary>
	private static void CheckManifest(Doc doc, string text)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		string[] lines = text.Replace("\r\n", "\n").Split('\n');
		for (int n = 0; n < lines.Length; n++)
		{
			string trimmed = lines[n].Trim();
			string line = $"line {n + 1}";
			if (trimmed.Length == 0 || trimmed[0] == '#')
			{
				continue;
			}

			if (trimmed[0] == '[')
			{
				doc.Warn(line, $"'{trimmed}': tables are not read; the keys after it are read as top-level keys");
				continue;
			}

			int equals = trimmed.IndexOf('=');
			if (equals < 0)
			{
				doc.Error(line, $"'{trimmed}' is not a key = value line; it is ignored (a list must open and close on one line)");
				continue;
			}

			string key = trimmed[..equals].Trim().ToLowerInvariant();
			string raw = trimmed[(equals + 1)..].Trim();
			string at = $"{key} ({line})";
			if (Array.IndexOf(ManifestKeys, key) < 0)
			{
				doc.Warn(at, UnknownKey(key, ManifestKeys));
				continue;
			}

			string canonical = key == "title" ? "name" : key;
			if (!seen.Add(canonical))
			{
				doc.Warn(at, key is "name" or "title"
					? "the title is stated twice (name and title are one field); the last one is read"
					: "stated twice; the last one is read");
			}

			if (key is "requires" or "after")
			{
				ManifestList(doc, at, key, raw);
				continue;
			}

			if (raw.Length != 0 && raw[0] is '"' or '\'' && (raw.Length < 2 || raw[^1] != raw[0]))
			{
				doc.Error(at, "the value does not end at its closing quote; an inline comment or trailing text is read as part of it");
				continue;
			}

			string value = Unquote(raw);
			if (raw.Length != 0 && value.Length == raw.Length)
			{
				doc.Warn(at, $"TOML text is quoted: {key} = \"{raw}\"");
			}

			if (key == "version")
			{
				if (value.Trim().Length == 0)
				{
					doc.Warn(at, "an empty version; a requirement with a version floor never accepts this mod");
				}
				else
				{
					VersionSegments(doc, at, value);
				}
			}
		}
	}

	private static void ManifestList(Doc doc, string at, string key, string raw)
	{
		if (raw.StartsWith('[') && !raw.EndsWith(']'))
		{
			doc.Error(at, "the list must close on the same line: " + (key == "requires"
				? "requires = [\"souls_combat >= 0.1\", \"hd-ui\"]"
				: "after = [\"hd-ui\", \"souls_combat\"]"));
			return;
		}

		string inner = raw.StartsWith('[') ? raw[1..^1] : raw;
		foreach (string item in inner.Split(',').Select(part => Unquote(part.Trim())).Where(part => part.Length != 0))
		{
			if (key == "after")
			{
				if (item.IndexOfAny(VersionOperators) >= 0)
				{
					doc.Warn(at, $"'{item}': \"after\" names mods only, with no version; this reads as a mod named '{item}'");
				}

				continue;
			}

			ModRequirement? requirement = ModRequirement.Parse(item);
			if (requirement is null)
			{
				doc.Error(at, $"'{item}' names no mod; write \"<mod> >= <version>\" or \"<mod>\"");
				continue;
			}

			if (requirement.Name.IndexOfAny(VersionOperators) >= 0 || requirement.MinVersion.IndexOfAny(VersionOperators) >= 0)
			{
				doc.Error(at, $"'{item}': only \"<mod> >= <version>\" is understood; this reads as a mod named " +
					$"'{requirement.Name}'{(requirement.MinVersion.Length == 0 ? string.Empty : $" at least '{requirement.MinVersion}'")}");
				continue;
			}

			if (item.Contains(">=", StringComparison.Ordinal) && requirement.MinVersion.Length == 0)
			{
				doc.Warn(at, $"'{item}': '>=' with no version accepts any version");
			}
			else if (requirement.MinVersion.Length != 0)
			{
				VersionSegments(doc, at, requirement.MinVersion);
			}
		}
	}

	/// <summary>ModRequirement.CompareVersions compares a segment numerically only when both sides are whole numbers.</summary>
	private static void VersionSegments(Doc doc, string at, string version)
	{
		foreach (string segment in version.Trim().Split('.'))
		{
			if (!int.TryParse(segment.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
			{
				doc.Warn(at, $"'{version}': version segments compare as numbers only when each is a whole number; " +
					$"'{segment}' compares as text");
				return;
			}
		}
	}

	/// <summary>ModWorkspace.Unquote: one pair of matching outer quotes removed.</summary>
	private static string Unquote(string value) =>
		value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
			? value[1..^1]
			: value;

	// ---------------------------------------------------------------------------------------
	// The field checks every kind shares
	// ---------------------------------------------------------------------------------------

	/// <summary>A pointer one key deeper: <c>a.b</c>, or <c>a["b.c"]</c> for a key that is not a plain name.</summary>
	private static string At(string at, string key)
	{
		bool plain = key.Length != 0 && key.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');
		if (!plain)
		{
			return $"{at}[\"{key}\"]";
		}

		return at.Length == 0 ? key : at + "." + key;
	}

	private static string At(string at, int index) => $"{at}[{index}]";

	private static bool Has(JsonElement record, string key) =>
		record.ValueKind == JsonValueKind.Object && record.TryGetProperty(key, out _);

	private static string Describe(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.Object => "an object",
		JsonValueKind.Array => "a list",
		JsonValueKind.String => $"the text '{value.GetString()}'",
		JsonValueKind.Number => $"the number {value.GetRawText()}",
		JsonValueKind.True => "true",
		JsonValueKind.False => "false",
		_ => "null",
	};

	private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

	/// <summary>The message for a key no loader reads, naming the nearest known key when one is close.</summary>
	private static string UnknownKey(string key, IReadOnlyList<string> known)
	{
		string? nearest = null;
		int best = int.MaxValue;
		foreach (string candidate in known)
		{
			int distance = string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)
				? 0
				: Distance(candidate.ToLowerInvariant(), key.ToLowerInvariant());
			if (distance < best)
			{
				best = distance;
				nearest = candidate;
			}
		}

		string message = $"'{key}' is not a field here and is ignored";
		int close = key.Length <= 3 ? 1 : key.Length <= 6 ? 2 : 3;
		if (nearest is not null && best <= close)
		{
			return message + $"; did you mean '{nearest}'?";
		}

		return known.Count <= 12 ? message + $" (the fields are {string.Join(", ", known)})" : message;
	}

	/// <summary>Levenshtein distance, for <see cref="UnknownKey"/>'s suggestion.</summary>
	private static int Distance(string a, string b)
	{
		var row = new int[b.Length + 1];
		for (int j = 0; j <= b.Length; j++)
		{
			row[j] = j;
		}

		for (int i = 1; i <= a.Length; i++)
		{
			int diagonal = row[0];
			row[0] = i;
			for (int j = 1; j <= b.Length; j++)
			{
				int above = row[j];
				row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
				diagonal = above;
			}
		}

		return row[b.Length];
	}

	/// <summary>
	/// One file's issues as they are found. The field readers mirror the two ways the loaders read
	/// a Variant: STRICT (only at the right type, anything else dropped — an error) and LOOSE
	/// (<c>AsString</c>/<c>AsDouble</c>/<c>AsBool</c>, which convert — a warning where the
	/// conversion is unlikely to be what was meant, an error where it yields nothing useful).
	/// </summary>
	private sealed class Doc
	{
		public Doc(string path)
		{
			Path = path;
		}

		public string Path { get; }

		public List<ModIssue> Issues { get; } = new();

		public void Error(string at, string message) => Issues.Add(new ModIssue(Path, at, true, message));

		public void Warn(string at, string message) => Issues.Add(new ModIssue(Path, at, false, message));

		/// <summary>Whether <paramref name="value"/> is an object; <paramref name="otherwise"/> is the error when not.</summary>
		public bool Object(JsonElement value, string at, string otherwise)
		{
			if (value.ValueKind == JsonValueKind.Object)
			{
				return true;
			}

			Error(at, otherwise.StartsWith("must", StringComparison.Ordinal) || otherwise.StartsWith("the", StringComparison.Ordinal)
				? $"{otherwise} (it is {Describe(value)})"
				: otherwise);
			return false;
		}

		/// <summary>Unknown keys (ignored by the loader) and repeated keys (the last is read), as warnings.</summary>
		public void Keys(JsonElement record, string at, IReadOnlyList<string> known)
		{
			DuplicateKeys(record, at);
			foreach (JsonProperty property in record.EnumerateObject())
			{
				if (!known.Contains(property.Name))
				{
					Warn(At(at, property.Name), UnknownKey(property.Name, known));
				}
			}
		}

		public void DuplicateKeys(JsonElement record, string at)
		{
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (JsonProperty property in record.EnumerateObject())
			{
				if (!seen.Add(property.Name))
				{
					Warn(At(at, property.Name), "stated twice; the last one is read");
				}
			}
		}

		/// <summary>A text field; null when absent or unusable.</summary>
		public string? Text(JsonElement record, string key, string at, bool required = false, bool strict = false)
		{
			string where = At(at, key);
			if (!record.TryGetProperty(key, out JsonElement value))
			{
				if (required)
				{
					Error(where, $"\"{key}\" is required");
				}

				return null;
			}

			if (value.ValueKind == JsonValueKind.String)
			{
				string text = value.GetString() ?? string.Empty;
				if (required && text.Trim().Length == 0)
				{
					Error(where, $"\"{key}\" is required and is empty");
				}

				return text;
			}

			if (!strict && value.ValueKind == JsonValueKind.Number)
			{
				Warn(where, $"a number where text is expected; read as '{value.GetRawText()}'");
				return value.GetRawText();
			}

			Error(where, $"must be text, not {Describe(value)}" + (strict ? "; the loader ignores it" : string.Empty));
			return null;
		}

		/// <summary>A loose text value outside a field (a map's value).</summary>
		public string? LooseText(JsonElement value, string at)
		{
			if (value.ValueKind == JsonValueKind.String)
			{
				return value.GetString() ?? string.Empty;
			}

			if (value.ValueKind == JsonValueKind.Number)
			{
				Warn(at, $"a number where text is expected; read as '{value.GetRawText()}'");
				return value.GetRawText();
			}

			Error(at, $"must be text, not {Describe(value)}");
			return null;
		}

		/// <summary>A string, or a list of strings (fx recipes' <c>when</c>, read with AsString per item).</summary>
		public List<string>? TextOrList(JsonElement record, string key, string at) =>
			record.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
				? new List<string> { value.GetString() ?? string.Empty }
				: TextList(record, key, at);

		/// <summary>
		/// A list of text; <paramref name="single"/> also takes one string. Strict lists drop a
		/// non-string item (an error); loose ones read a number as its digits.
		/// </summary>
		public List<string>? TextList(JsonElement record, string key, string at, bool single = false, bool strict = false, bool required = false)
		{
			string where = At(at, key);
			if (!record.TryGetProperty(key, out JsonElement value))
			{
				if (required)
				{
					Error(where, $"\"{key}\" is required");
				}

				return null;
			}

			if (single && value.ValueKind == JsonValueKind.String)
			{
				return new List<string> { value.GetString() ?? string.Empty };
			}

			if (value.ValueKind != JsonValueKind.Array)
			{
				Error(where, (single ? "must be text or a list of text" : "must be a list of text") +
					$", not {Describe(value)}; the loader ignores it");
				return null;
			}

			var items = new List<string>();
			int i = 0;
			foreach (JsonElement item in value.EnumerateArray())
			{
				string itemAt = At(where, i++);
				if (item.ValueKind == JsonValueKind.String)
				{
					string text = item.GetString() ?? string.Empty;
					if (text.Trim().Length == 0)
					{
						Warn(itemAt, "empty; skipped");
					}

					items.Add(text);
				}
				else if (!strict && item.ValueKind == JsonValueKind.Number)
				{
					Warn(itemAt, $"a number where text is expected; read as '{item.GetRawText()}'");
					items.Add(item.GetRawText());
				}
				else
				{
					Error(itemAt, $"must be text, not {Describe(item)}" + (strict ? "; skipped" : string.Empty));
				}
			}

			return items;
		}

		/// <summary>A list field; null when absent or not a list (the latter an error: the loader ignores it).</summary>
		public JsonElement? Array(JsonElement record, string key, string at)
		{
			if (!record.TryGetProperty(key, out JsonElement value))
			{
				return null;
			}

			if (value.ValueKind != JsonValueKind.Array)
			{
				Error(At(at, key), $"must be a list, not {Describe(value)}; the loader ignores it");
				return null;
			}

			return value;
		}

		/// <summary>
		/// A number field. Strict: read only when it is a JSON number. <paramref name="clamps"/>: the
		/// loader clamps an out-of-range value (a warning) rather than misbehaving (an error).
		/// </summary>
		public double? Number(
			JsonElement record, string key, string at, bool strict = false, double? min = null, double? max = null,
			bool integer = false, bool clamps = false)
		{
			if (!record.TryGetProperty(key, out JsonElement value))
			{
				return null;
			}

			string where = At(at, key);
			if (strict && value.ValueKind != JsonValueKind.Number)
			{
				Error(where, $"must be a number, not {Describe(value)}; the loader ignores it and uses its default");
				return null;
			}

			return LooseNumber(value, where, integer, min, max, clamps);
		}

		/// <summary>A value read with AsDouble/AsInt32: text holding a number and true/false convert.</summary>
		public double? LooseNumber(
			JsonElement value, string at, bool integer = false, double? min = null, double? max = null, bool clamps = false)
		{
			double number;
			switch (value.ValueKind)
			{
				case JsonValueKind.Number:
					number = value.GetDouble();
					break;
				case JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number):
					Warn(at, "a number written as text; write it without quotes");
					break;
				case JsonValueKind.True:
				case JsonValueKind.False:
					number = value.ValueKind == JsonValueKind.True ? 1 : 0;
					Warn(at, $"true/false where a number is expected; read as {Format(number)}");
					break;
				default:
					Error(at, $"must be a number, not {Describe(value)}");
					return null;
			}

			if (integer && number != Math.Truncate(number))
			{
				Warn(at, $"must be a whole number; {Format(number)} is read as {Format(Math.Truncate(number))}");
			}

			if (min is { } low && number < low)
			{
				Range(at, $"{Format(number)} is below the least allowed, {Format(low)}", clamps);
			}

			if (max is { } high && number > high)
			{
				Range(at, $"{Format(number)} is above the most allowed, {Format(high)}", clamps);
			}

			return number;
		}

		private void Range(string at, string message, bool clamps)
		{
			if (clamps)
			{
				Warn(at, message + "; it is clamped");
			}
			else
			{
				Error(at, message);
			}
		}

		/// <summary>A true/false field read with AsBool.</summary>
		public bool? Flag(JsonElement record, string key, string at)
		{
			if (!record.TryGetProperty(key, out JsonElement value))
			{
				return null;
			}

			string where = At(at, key);
			switch (value.ValueKind)
			{
				case JsonValueKind.True:
					return true;
				case JsonValueKind.False:
					return false;
				case JsonValueKind.Number:
					Warn(where, $"a number where true or false is expected; read as {(value.GetDouble() != 0 ? "true" : "false")}");
					return value.GetDouble() != 0;
				default:
					Error(where, $"must be true or false, not {Describe(value)}" +
						(value.ValueKind == JsonValueKind.String ? " (text is not read as the word it spells)" : string.Empty));
					return null;
			}
		}

		/// <summary>
		/// A list of <paramref name="least"/>..<paramref name="most"/> numbers. Fewer, or a wrong
		/// count where the loader wants it exact, and the loader uses its default (an error); more
		/// where <paramref name="extraIgnored"/> and the extras are dropped (a warning).
		/// </summary>
		public void Vector(JsonElement record, string key, string at, int least, int most, bool extraIgnored)
		{
			if (!record.TryGetProperty(key, out JsonElement value))
			{
				return;
			}

			string where = At(at, key);
			string shape = least == most ? $"{least} numbers" : $"{least} to {most} numbers";
			if (value.ValueKind != JsonValueKind.Array)
			{
				Error(where, $"must be a list of {shape}, not {Describe(value)}; the loader uses its default");
				return;
			}

			int count = value.GetArrayLength();
			if (count < least || (count > most && !extraIgnored))
			{
				Error(where, $"must hold {shape}, not {count}; the loader uses its default");
				return;
			}

			if (count > most)
			{
				Warn(where, $"holds {count} numbers; only the first {most} are read");
			}

			int i = 0;
			foreach (JsonElement item in value.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Number)
				{
					Error(At(where, i), $"must be a number, not {Describe(item)}");
				}

				i++;
			}
		}

		/// <summary>An HTML hex colour as <c>Color.HtmlIsValid</c> takes it: #rgb, #rgba, #rrggbb or #rrggbbaa, the # optional.</summary>
		public void Colour(JsonElement record, string key, string at)
		{
			if (!record.TryGetProperty(key, out JsonElement value))
			{
				return;
			}

			string text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
			string digits = text.StartsWith('#') ? text[1..] : text;
			if (value.ValueKind != JsonValueKind.String ||
				digits.Length is not (3 or 4 or 6 or 8) || !digits.All(Uri.IsHexDigit))
			{
				Error(At(at, key), $"'{text}' is not a colour: write #rrggbb or #rrggbbaa (hex); the default colour is used");
			}
		}
	}
}
