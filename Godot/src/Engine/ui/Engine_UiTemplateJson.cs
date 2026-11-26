using System.Collections.Generic;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace Refii.Ui;

/// <summary>
/// THE ONE READER AND WRITER of <c>ui/templates.json</c> records — menus, entries and screens —
/// for every file that states them: the game's own (<see cref="Engine_UiGameTemplates"/>), each
/// mod's, the player's, the snapshot and the Mod Studio's preview.
/// </summary>
/// <remarks>
/// <para>Pure .NET over <see cref="System.Text.Json"/>, so the composition is held by tests without
/// a Godot type in the host. Values read loosely, the way a hand-written file is written: a number
/// where text is expected is its text, a number or <c>"true"</c> where a flag is expected is a flag.
/// Comments and trailing commas are allowed.</para>
/// <para>A SCREEN read <paramref name="over"/> an existing record is a PATCH: only the fields the
/// record states change. A list replaces the list; a map (<c>boxPaths</c>, <c>menuScriptByMode</c>,
/// <c>fields</c>, <c>switches</c>) merges per key, a <c>null</c> value removing that key.</para>
/// </remarks>
public static class Engine_UiTemplateJson
{
	public static readonly JsonDocumentOptions ReadOptions = new()
	{
		CommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	/// <summary>
	/// The writer's options. THE RESOLVER IS STATED: the exported game runs without System.Text.Json's
	/// implicit reflection resolver, and options that state none throw on their first write
	/// ("must specify a TypeInfoResolver") — which, thrown from the HUD build, left the game with no UI.
	/// </summary>
	public static readonly JsonSerializerOptions WriteOptions = new()
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
	};

	/// <summary>A file's root object, or null when the text is not a JSON object.</summary>
	public static JsonObject? ParseObject(string text)
	{
		try
		{
			return JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public static string Write(JsonNode node) => node.ToJsonString(WriteOptions).Replace("\r\n", "\n");

	// ---- loose values ----------------------------------------------------------------------

	public static string Text(JsonNode? node) => node switch
	{
		JsonValue value when value.TryGetValue(out string? text) => text,
		JsonValue value when value.TryGetValue(out bool flag) => flag ? "true" : "false",
		JsonValue value => value.ToJsonString(),
		_ => string.Empty,
	};

	public static bool Flag(JsonNode? node) => node switch
	{
		JsonValue value when value.TryGetValue(out bool flag) => flag,
		JsonValue value when TryNumber(value, out double number) => number != 0,
		JsonValue value when value.TryGetValue(out string? text) =>
			text.Trim() is "true" or "1" or "on" or "yes",
		_ => false,
	};

	public static double Number(JsonNode? node, double fallback = 0) => node switch
	{
		JsonValue value when TryNumber(value, out double number) => number,
		JsonValue value when value.TryGetValue(out string? text) &&
			double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
		JsonValue value when value.TryGetValue(out bool flag) => flag ? 1 : 0,
		_ => fallback,
	};

	/// <summary>
	/// A number however the node holds it: parsed text answers every numeric type, a value built in
	/// memory only its own, so each is asked.
	/// </summary>
	private static bool TryNumber(JsonValue value, out double number)
	{
		if (value.TryGetValue(out number))
		{
			return true;
		}

		if (value.TryGetValue(out float single)) { number = single; return true; }
		if (value.TryGetValue(out long whole)) { number = whole; return true; }
		if (value.TryGetValue(out int small)) { number = small; return true; }
		if (value.TryGetValue(out uint unsigned)) { number = unsigned; return true; }
		if (value.TryGetValue(out decimal exact)) { number = (double)exact; return true; }
		return false;
	}

	public static float Single(JsonNode? node, float fallback = 0f) =>
		node is JsonValue value && value.TryGetValue(out float single) ? single : (float)Number(node, fallback);

	public static int Integer(JsonNode? node, int fallback = 0) => (int)Number(node, fallback);

	private static string TextOr(JsonObject record, string key, string fallback) =>
		record.TryGetPropertyValue(key, out JsonNode? node) ? Text(node) : fallback;

	private static void Strings(JsonObject record, string key, ref List<string> into)
	{
		if (!record.TryGetPropertyValue(key, out JsonNode? node))
		{
			return;
		}

		into = new List<string>();
		if (node is not JsonArray array)
		{
			return;
		}

		foreach (JsonNode? item in array)
		{
			string text = Text(item);
			if (text.Length != 0)
			{
				into.Add(text);
			}
		}
	}

	private static void Map(JsonObject record, string key, Dictionary<string, string> into, bool merge)
	{
		if (!record.TryGetPropertyValue(key, out JsonNode? node))
		{
			return;
		}

		if (!merge)
		{
			into.Clear();
		}

		if (node is not JsonObject map)
		{
			return;
		}

		foreach ((string name, JsonNode? value) in map)
		{
			if (value is null)
			{
				into.Remove(name);
			}
			else
			{
				into[name] = Text(value);
			}
		}
	}

	// ---- menus -------------------------------------------------------------------------------

	/// <summary>A whole menu record (<c>{ highlight, title, source, rowCommand, entries }</c>), or null when it is not an object.</summary>
	public static Engine_UiMenuTemplate? ParseMenu(string name, JsonNode? node)
	{
		if (node is not JsonObject record)
		{
			return null;
		}

		var menu = new Engine_UiMenuTemplate { Name = name };
		ApplyMenuFields(menu, record);
		if (record.TryGetPropertyValue("entries", out JsonNode? entries) && entries is JsonArray list)
		{
			foreach (JsonNode? entry in list)
			{
				if (ParseEntry(entry) is { } parsed)
				{
					menu.Entries.Add(parsed);
				}
			}
		}

		return menu;
	}

	/// <summary>The menu-level fields a record states, written over <paramref name="menu"/> — a patch's and a whole record's alike.</summary>
	public static void ApplyMenuFields(Engine_UiMenuTemplate menu, JsonObject record)
	{
		menu.Highlight = TextOr(record, "highlight", menu.Highlight);
		menu.Title = TextOr(record, "title", menu.Title);
		menu.RowCommand = TextOr(record, "rowCommand", menu.RowCommand);
		menu.Source = TextOr(record, "source", menu.Source);
	}

	/// <summary>One entry and its children, or null when it is not an object or names nothing.</summary>
	public static Engine_UiMenuEntry? ParseEntry(JsonNode? node)
	{
		if (node is not JsonObject record)
		{
			return null;
		}

		var entry = new Engine_UiMenuEntry
		{
			Name = TextOr(record, "name", string.Empty),
			Tag = TextOr(record, "tag", string.Empty),
			Command = record.TryGetPropertyValue("command", out JsonNode? command) ? Text(command) : null,
			Opens = record.TryGetPropertyValue("opens", out JsonNode? opens) ? Text(opens) : null,
			HostOnly = record.TryGetPropertyValue("hostOnly", out JsonNode? host) && Flag(host),
			Drop = record.TryGetPropertyValue("drop", out JsonNode? drop) && Flag(drop),
			Source = TextOr(record, "source", string.Empty),
			Icon = TextOr(record, "icon", string.Empty),
			When = TextOr(record, "when", string.Empty),
			EventType = record.TryGetPropertyValue("eventType", out JsonNode? type) ? Integer(type) : 0,
			Rating = record.TryGetPropertyValue("rating", out JsonNode? rating)
				? Integer(rating)
				: Engine_UiMenuConstants.EmptyRatingSlide,
			Counted = !record.TryGetPropertyValue("counted", out JsonNode? counted) || Flag(counted),
		};

		if (entry.Name.Length == 0)
		{
			return null;
		}

		if (record.TryGetPropertyValue("children", out JsonNode? children) && children is JsonArray list)
		{
			foreach (JsonNode? child in list)
			{
				if (ParseEntry(child) is { } parsed)
				{
					entry.Children.Add(parsed);
				}
			}
		}

		return entry;
	}

	public static JsonObject MenuJson(Engine_UiMenuTemplate menu)
	{
		var entries = new JsonArray();
		foreach (Engine_UiMenuEntry entry in menu.Entries)
		{
			entries.Add(EntryJson(entry));
		}

		return new JsonObject
		{
			["highlight"] = menu.Highlight,
			["title"] = menu.Title,
			["source"] = menu.Source,
			["rowCommand"] = menu.RowCommand,
			["entries"] = entries,
		};
	}

	/// <summary>An entry as a file states it: name and tag always, the rest only where it is not the default.</summary>
	public static JsonObject EntryJson(Engine_UiMenuEntry entry)
	{
		var record = new JsonObject
		{
			["name"] = entry.Name,
			["tag"] = entry.Tag,
		};

		if (entry.Command is not null)
		{
			record["command"] = entry.Command;
		}

		if (entry.Opens is not null)
		{
			record["opens"] = entry.Opens;
		}

		if (entry.Source.Length != 0)
		{
			record["source"] = entry.Source;
		}

		if (entry.When.Length != 0)
		{
			record["when"] = entry.When;
		}

		if (entry.Drop)
		{
			record["drop"] = true;
		}

		if (entry.HostOnly)
		{
			record["hostOnly"] = true;
		}

		if (entry.Icon.Length != 0)
		{
			record["icon"] = entry.Icon;
		}

		if (entry.EventType != 0)
		{
			record["eventType"] = entry.EventType;
		}

		if (entry.Rating != Engine_UiMenuConstants.EmptyRatingSlide)
		{
			record["rating"] = entry.Rating;
		}

		if (!entry.Counted)
		{
			record["counted"] = false;
		}

		if (entry.Children.Count != 0)
		{
			var children = new JsonArray();
			foreach (Engine_UiMenuEntry child in entry.Children)
			{
				children.Add(EntryJson(child));
			}

			record["children"] = children;
		}

		return record;
	}

	// ---- screens -----------------------------------------------------------------------------

	/// <summary>
	/// A screen record, or null when it is not an object. With <paramref name="over"/> it is a PATCH
	/// of that record (a copy is changed; <paramref name="over"/> is left as it was): every field
	/// the record does not state keeps its value. Without, a field it does not state takes the
	/// reader's default.
	/// </summary>
	public static Engine_UiScreenTemplate? ParseScreen(uint id, JsonNode? node, Engine_UiScreenTemplate? over = null)
	{
		if (node is not JsonObject record)
		{
			return null;
		}

		bool patch = over is not null;
		Engine_UiScreenTemplate screen = over is null
			? new Engine_UiScreenTemplate
			{
				ScreenId = id,
				Name = $"screen{id}",
				ListLift = Engine_UiPresentationDepth.BookOffset,
			}
			: Clone(over);
		screen.ScreenId = id;

		screen.Name = TextOr(record, "name", screen.Name);
		screen.MenuScript = TextOr(record, "menuScript", screen.MenuScript);
		screen.PreviewAnchor = TextOr(record, "previewAnchor", screen.PreviewAnchor);
		screen.PageElement = TextOr(record, "pageElement", screen.PageElement);
		screen.Menu = TextOr(record, "menu", screen.Menu);
		screen.Provider = TextOr(record, "provider", screen.Provider);
		screen.RevealedAccept = TextOr(record, "revealedAccept", screen.RevealedAccept);
		screen.RevealedBack = TextOr(record, "revealedBack", screen.RevealedBack);
		screen.FlipAction = TextOr(record, "flipAction", screen.FlipAction);
		screen.XAction = TextOr(record, "xAction", screen.XAction);
		screen.RowAction = TextOr(record, "rowAction", screen.RowAction);
		screen.ScrollContent = TextOr(record, "scrollContent", screen.ScrollContent);
		screen.ScrollMask = TextOr(record, "scrollMask", screen.ScrollMask);
		screen.ScrollBar = TextOr(record, "scrollBar", screen.ScrollBar);
		screen.FlowMeasured = TextOr(record, "flowMeasured", screen.FlowMeasured);
		screen.FlowBase = TextOr(record, "flowBase", screen.FlowBase);
		screen.FlowTailAnchor = TextOr(record, "flowTailAnchor", screen.FlowTailAnchor);
		screen.Presentation = TextOr(record, "presentation", screen.Presentation);
		screen.PageRootUnit = TextOr(record, "pageRootUnit", screen.PageRootUnit);
		screen.BackdropUnit = TextOr(record, "backdropUnit", screen.BackdropUnit);
		screen.BoxPresentation = TextOr(record, "boxPresentation", screen.BoxPresentation);

		if (record.TryGetPropertyValue("ownsList", out JsonNode? owns)) screen.OwnsList = Flag(owns);
		if (record.TryGetPropertyValue("restFromFile", out JsonNode? rest)) screen.RestFromFile = Flag(rest);
		if (record.TryGetPropertyValue("purse", out JsonNode? purse)) screen.Purse = Flag(purse);
		if (record.TryGetPropertyValue("flowWhenDrawn", out JsonNode? flowDrawn)) screen.FlowWhenDrawn = Flag(flowDrawn);
		if (record.TryGetPropertyValue("commitsByHighlight", out JsonNode? byHighlight)) screen.CommitsByHighlight = Flag(byHighlight);
		if (record.TryGetPropertyValue("ownsRevealedInput", out JsonNode? revealed)) screen.OwnsRevealedInput = Flag(revealed);
		if (record.TryGetPropertyValue("hasSpellBlade", out JsonNode? blade)) screen.HasSpellBlade = Flag(blade);
		if (record.TryGetPropertyValue("fadesTopEnd", out JsonNode? top)) screen.FadesTopEnd = Flag(top);
		if (record.TryGetPropertyValue("fadesBottomEnd", out JsonNode? bottom)) screen.FadesBottomEnd = Flag(bottom);
		if (record.TryGetPropertyValue("pageSlide", out JsonNode? slide)) screen.PageSlide = (int)Number(slide);
		if (record.TryGetPropertyValue("listLift", out JsonNode? lift)) screen.ListLift = Single(lift);

		Strings(record, "chrome", ref screen.ChromeUnits);
		Strings(record, "panelSubtrees", ref screen.PanelSubtrees);
		Strings(record, "ownedElsewhere", ref screen.OwnedElsewhere);
		Strings(record, "boxUnits", ref screen.BoxUnits);
		Strings(record, "pages", ref screen.Pages);
		Strings(record, "guestTextUnits", ref screen.GuestTextUnits);
		Strings(record, "wideLeft", ref screen.WideLeft);
		Strings(record, "wideRight", ref screen.WideRight);
		Strings(record, "wideGround", ref screen.WideGround);
		Strings(record, "wideStretch", ref screen.WideStretch);
		if (record.TryGetPropertyValue("pageSpan", out JsonNode? spanNode))
		{
			screen.PageSpan = spanNode is JsonArray { Count: 2 } span
				? new Vector2(Single(span[0]), Single(span[1]))
				: null;
		}

		Strings(record, "flowMoved", ref screen.FlowMoved);
		Strings(record, "flowTailMoved", ref screen.FlowTailMoved);

		Map(record, "boxPaths", screen.BoxPaths, patch);
		Map(record, "fields", screen.Fields, patch);
		Map(record, "switches", screen.Switches, patch);

		if (record.TryGetPropertyValue("menuScriptByMode", out JsonNode? modesNode))
		{
			if (!patch)
			{
				screen.MenuScriptByMode.Clear();
			}

			if (modesNode is JsonObject modes)
			{
				foreach ((string key, JsonNode? value) in modes)
				{
					if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mode))
					{
						continue;
					}

					if (value is null)
					{
						screen.MenuScriptByMode.Remove(mode);
					}
					else if (Text(value) is { Length: > 0 } script)
					{
						screen.MenuScriptByMode[mode] = script;
					}
				}
			}
		}

		if (record.TryGetPropertyValue("restPose", out JsonNode? poseNode))
		{
			if (!patch)
			{
				screen.RestPose.Clear();
			}

			if (poseNode is JsonObject poses)
			{
				foreach ((string path, JsonNode? value) in poses)
				{
					if (value is not JsonArray slots)
					{
						screen.RestPose.Remove(path);
						continue;
					}

					var pose = new float?[6];
					for (int slot = 0; slot < pose.Length && slot < slots.Count; ++slot)
					{
						pose[slot] = slots[slot] is null ? null : Single(slots[slot]);
					}

					screen.RestPose[path] = pose;
				}
			}
		}

		if (record.TryGetPropertyValue("restOpacity", out JsonNode? fadeNode))
		{
			if (!patch)
			{
				screen.RestOpacity.Clear();
			}

			if (fadeNode is JsonObject fades)
			{
				foreach ((string path, JsonNode? value) in fades)
				{
					if (value is null)
					{
						screen.RestOpacity.Remove(path);
					}
					else
					{
						screen.RestOpacity[path] = Single(value);
					}
				}
			}
		}

		if (record.TryGetPropertyValue("pageStackAt", out JsonNode? stackNode))
		{
			screen.PageStackAt = stackNode is JsonArray { Count: 3 } xyz
				? new Vector3(Single(xyz[0]), Single(xyz[1]), Single(xyz[2]))
				: null;
		}

		if (record.TryGetPropertyValue("statFields", out JsonNode? statsNode))
		{
			screen.StatFields = new List<(int Id, string Path)>();
			if (statsNode is JsonArray stats)
			{
				foreach (JsonNode? stat in stats)
				{
					if (stat is JsonObject statRecord &&
						statRecord.TryGetPropertyValue("id", out JsonNode? statId) &&
						statRecord.TryGetPropertyValue("path", out JsonNode? statPath))
					{
						screen.StatFields.Add((Integer(statId), Text(statPath)));
					}
				}
			}
		}

		if (record.TryGetPropertyValue("panelFields", out JsonNode? fieldsNode))
		{
			screen.PanelFields = new List<Engine_UiScreenPanel.Field>();
			if (fieldsNode is JsonArray fields)
			{
				foreach (JsonNode? field in fields)
				{
					if (field is JsonObject fieldRecord && fieldRecord.TryGetPropertyValue("path", out JsonNode? path))
					{
						screen.PanelFields.Add(new Engine_UiScreenPanel.Field(
							Text(path),
							fieldRecord.TryGetPropertyValue("multiline", out JsonNode? multi) && Flag(multi),
							fieldRecord.TryGetPropertyValue("alternate", out JsonNode? alt) && Flag(alt)));
					}
				}
			}
		}

		return screen;
	}

	/// <summary>A deep copy, for a patch that must not change the record it starts from.</summary>
	public static Engine_UiScreenTemplate Clone(Engine_UiScreenTemplate screen)
	{
		Engine_UiScreenTemplate copy = screen.ShallowCopy();
		copy.MenuScriptByMode = new Dictionary<int, string>(screen.MenuScriptByMode);
		copy.PanelFields = new List<Engine_UiScreenPanel.Field>(screen.PanelFields);
		copy.PanelSubtrees = new List<string>(screen.PanelSubtrees);
		copy.ChromeUnits = new List<string>(screen.ChromeUnits);
		copy.Pages = new List<string>(screen.Pages);
		copy.BoxUnits = new List<string>(screen.BoxUnits);
		copy.BoxPaths = new Dictionary<string, string>(screen.BoxPaths, System.StringComparer.Ordinal);
		copy.RestPose = new Dictionary<string, float?[]>(screen.RestPose, System.StringComparer.Ordinal);
		copy.RestOpacity = new Dictionary<string, float>(screen.RestOpacity, System.StringComparer.Ordinal);
		copy.WideLeft = new List<string>(screen.WideLeft);
		copy.WideRight = new List<string>(screen.WideRight);
		copy.WideStretch = new List<string>(screen.WideStretch);
		copy.WideGround = new List<string>(screen.WideGround);
		copy.PageSpan = screen.PageSpan;
		copy.OwnedElsewhere = new List<string>(screen.OwnedElsewhere);
		copy.GuestTextUnits = new List<string>(screen.GuestTextUnits);
		copy.StatFields = new List<(int Id, string Path)>(screen.StatFields);
		copy.FlowMoved = new List<string>(screen.FlowMoved);
		copy.FlowTailMoved = new List<string>(screen.FlowTailMoved);
		copy.Fields = new Dictionary<string, string>(screen.Fields, System.StringComparer.Ordinal);
		copy.Switches = new Dictionary<string, string>(screen.Switches, System.StringComparer.Ordinal);
		return copy;
	}

	/// <summary>
	/// A screen record. <paramref name="full"/> writes every field (the snapshot — the override
	/// file's documentation); otherwise only what differs from the reader's default, which reads
	/// back to the same record (the shipped game file).
	/// </summary>
	public static JsonObject ScreenJson(Engine_UiScreenTemplate screen, bool full = true)
	{
		var record = new JsonObject();

		void Put(string key, string value)
		{
			if (full || value.Length != 0)
			{
				record[key] = value;
			}
		}

		void PutFlag(string key, bool value, bool fallback)
		{
			if (full || value != fallback)
			{
				record[key] = value;
			}
		}

		void PutList(string key, List<string> values)
		{
			if (full || values.Count != 0)
			{
				var array = new JsonArray();
				foreach (string value in values)
				{
					array.Add(value);
				}

				record[key] = array;
			}
		}

		void PutMap(string key, Dictionary<string, string> values)
		{
			if (full || values.Count != 0)
			{
				var map = new JsonObject();
				foreach ((string name, string value) in values)
				{
					map[name] = value;
				}

				record[key] = map;
			}
		}

		if (full || screen.Name != $"screen{screen.ScreenId}")
		{
			record["name"] = screen.Name;
		}

		PutFlag("ownsList", screen.OwnsList, false);
		PutFlag("restFromFile", screen.RestFromFile, false);
		PutFlag("purse", screen.Purse, false);
		PutFlag("commitsByHighlight", screen.CommitsByHighlight, false);
		Put("rowAction", screen.RowAction);
		Put("menuScript", screen.MenuScript);
		if (full || screen.MenuScriptByMode.Count != 0)
		{
			var modes = new JsonObject();
			foreach ((int mode, string script) in screen.MenuScriptByMode)
			{
				modes[mode.ToString(CultureInfo.InvariantCulture)] = script;
			}

			record["menuScriptByMode"] = modes;
		}

		Put("previewAnchor", screen.PreviewAnchor);
		Put("pageElement", screen.PageElement);
		if (full || screen.PageSlide != 0)
		{
			record["pageSlide"] = screen.PageSlide;
		}

		Put("menu", screen.Menu);
		Put("provider", screen.Provider);
		PutFlag("ownsRevealedInput", screen.OwnsRevealedInput, false);
		Put("revealedAccept", screen.RevealedAccept);
		Put("revealedBack", screen.RevealedBack);
		Put("flipAction", screen.FlipAction);
		Put("xAction", screen.XAction);
		PutFlag("hasSpellBlade", screen.HasSpellBlade, false);
		Put("scrollContent", screen.ScrollContent);
		Put("scrollMask", screen.ScrollMask);
		Put("scrollBar", screen.ScrollBar);
		Put("flowMeasured", screen.FlowMeasured);
		Put("flowBase", screen.FlowBase);
		PutList("flowMoved", screen.FlowMoved);
		PutFlag("flowWhenDrawn", screen.FlowWhenDrawn, false);
		Put("flowTailAnchor", screen.FlowTailAnchor);
		PutList("flowTailMoved", screen.FlowTailMoved);
		PutFlag("fadesTopEnd", screen.FadesTopEnd, true);
		PutFlag("fadesBottomEnd", screen.FadesBottomEnd, true);
		Put("presentation", screen.Presentation);
		Put("pageRootUnit", screen.PageRootUnit);
		Put("backdropUnit", screen.BackdropUnit);
		if (full || screen.ListLift != Engine_UiPresentationDepth.BookOffset)
		{
			record["listLift"] = screen.ListLift;
		}

		PutList("chrome", screen.ChromeUnits);
		PutList("panelSubtrees", screen.PanelSubtrees);
		PutList("ownedElsewhere", screen.OwnedElsewhere);
		Put("boxPresentation", screen.BoxPresentation);
		PutList("boxUnits", screen.BoxUnits);
		PutMap("boxPaths", screen.BoxPaths);
		PutList("wideLeft", screen.WideLeft);
		PutList("wideRight", screen.WideRight);
		PutList("wideGround", screen.WideGround);
		PutList("wideStretch", screen.WideStretch);
		if (screen.PageSpan is Vector2 pageSpan)
		{
			record["pageSpan"] = new JsonArray { pageSpan.X, pageSpan.Y };
		}

		if (full || screen.PanelFields.Count != 0)
		{
			var fields = new JsonArray();
			foreach (Engine_UiScreenPanel.Field field in screen.PanelFields)
			{
				fields.Add(new JsonObject
				{
					["path"] = field.Path,
					["multiline"] = field.Multiline,
					["alternate"] = field.Alternate,
				});
			}

			record["panelFields"] = fields;
		}

		PutList("pages", screen.Pages);
		PutList("guestTextUnits", screen.GuestTextUnits);
		if (full || screen.StatFields.Count != 0)
		{
			var stats = new JsonArray();
			foreach ((int statId, string statPath) in screen.StatFields)
			{
				stats.Add(new JsonObject { ["id"] = statId, ["path"] = statPath });
			}

			record["statFields"] = stats;
		}

		PutMap("fields", screen.Fields);
		PutMap("switches", screen.Switches);

		if (screen.PageStackAt is Vector3 stackAt)
		{
			record["pageStackAt"] = new JsonArray { stackAt.X, stackAt.Y, stackAt.Z };
		}

		if (screen.RestPose.Count != 0)
		{
			var poses = new JsonObject();
			foreach ((string path, float?[] pose) in screen.RestPose)
			{
				int last = pose.Length - 1;
				while (last >= 0 && pose[last] is null)
				{
					--last;
				}

				var slots = new JsonArray();
				for (int slot = 0; slot <= last; ++slot)
				{
					slots.Add(pose[slot] is { } value ? JsonValue.Create(value) : null);
				}

				poses[path] = slots;
			}

			record["restPose"] = poses;
		}

		if (screen.RestOpacity.Count != 0)
		{
			var fades = new JsonObject();
			foreach ((string path, float opacity) in screen.RestOpacity)
			{
				fades[path] = opacity;
			}

			record["restOpacity"] = fades;
		}

		return record;
	}
}
