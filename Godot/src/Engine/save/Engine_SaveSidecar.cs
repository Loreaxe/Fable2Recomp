using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Refii.Save;

/// <summary>
/// Refii's own file beside a save — `refii_save.json` in the save's folder — in LAYERS that each
/// apply only when present. The guest's files are the base and stay the console game's format;
/// nothing of ours is ever inside them, so a save moves to a console or Xenia as it is.
/// </summary>
/// <remarks>
/// THE SAVE-FORMAT CONTRACT:
/// <code>
/// {
///   "schema": "refii-save/1",
///   "refii": { "build": "…", "written": "…", "player_state": ["refii_player.txt", "refii_blob_*.bin"],
///              "character": { "id": "…", "name": "…", "achievements": [id…] } },
///   "mods":  { "api": 2, "digest": "…",
///              "set":  [ { "name", "title", "version", "sha256", "order", "native", "session",
///                          "records": ["0x…"], "keys": ["name."] } ],
///              "held": { "&lt;name&gt;": { "title", "version", "inventory": [item…], "worn": [modifier…],
///                                     "melee": "0x…", "ranged": "0x…", "player": { "1": { key: value } } } } },
///   "archon": { "bank": "&lt;sha256&gt;", "patched": true, "runner": "1.04", "mods": [ { "name", "version" } ] }
/// }
/// </code>
/// - `refii` — "if refii": what OUR build persists per save that the vanilla game does not. Today
///   that is the player store's files (<c>refii_player.txt</c> and <c>refii_blob_*.bin</c>, written
///   by the native player state beside the save) — named here so an export knows what to carry
///   or drop. A refii save without mods has only this layer. `character` is the hero the save holds
///   (Refii's own id: the guest's SaveUID changes at every write) and that hero's achievements as they
///   were at the write, so a moved or copied save carries them (Engine_AchievementCharacters).
/// - `mods` — "if modded": the stamp (the mod set the save was last written under, with each
///   mod's added GDB records and its save-key prefixes) and the records filtered out of the save
///   for mods that were missing, held so re-enabling the mod puts them back.
/// - a TITLE'S LAYERS — a mod manager outside ours that the title's saves can be written under
///   (<see cref="Refii.Mods.Engine_ExternalModManager"/>; Fable 2's Archon's Toolbox, `archon`),
///   kept as the JSON that manager wrote (<see cref="Layers"/>). Nothing of theirs is held: their
///   state is inside the save, which is kept verbatim.
/// A reader without a layer ignores it; a vanilla save has none; the layers are independent.
/// The schema name is versioned so later layers can be added.
///
/// Values are written by hand (<see cref="Utf8JsonWriter"/> / <see cref="JsonDocument"/>): no
/// serializer options, so nothing here can throw for want of a type resolver in the game.
/// </remarks>
public sealed class SaveSidecar
{
	public const string FileName = "refii_save.json";
	public const string Schema = "refii-save/1";

	/// <summary>The player store's files beside a save, as the native player state names them.</summary>
	public static readonly string[] PlayerStateFiles = { "refii_player.txt", "refii_blob_*.bin" };

	// ---------------------------------------------------------------- the refii layer

	public sealed class RefiiLayer
	{
		public string Build = string.Empty;
		public string Written = string.Empty;
		public List<string> PlayerState = new(PlayerStateFiles);
		public CharacterLayer? Character;
	}

	/// <summary>The hero a save holds: its id, a name for it, and its unlocked achievements.</summary>
	public sealed class CharacterLayer
	{
		public string Id = string.Empty;
		public string Name = string.Empty;
		public List<uint> Achievements = new();
	}

	// ---------------------------------------------------------------- the mods layer

	/// <summary>One mod of the stamp, in load order.</summary>
	public sealed class ModEntry
	{
		public string Name = string.Empty;
		public string Title = string.Empty;
		public string Version = string.Empty;
		public string Sha256 = string.Empty;
		public int Order;
		public bool Native;
		/// <summary>
		/// A session mod: applied for a co-op session only (mod sync), never the player's own. It
		/// never fuses: its records are not held and its absence raises no box.
		/// </summary>
		public bool Session;
		/// <summary>The GDB record ids the mod's patch ADDS (the base lacks them).</summary>
		public List<uint> Records = new();
		/// <summary>Save-key prefixes the mod owns (its name and a dot, by convention).</summary>
		public List<string> Keys = new();

		public string Label => (Title.Length == 0 ? Name : Title) + (Version.Length == 0 ? string.Empty : " " + Version);
	}

	/// <summary>What was filtered out of the save for one mod, kept to put back.</summary>
	public sealed class HeldRecords
	{
		public string Title = string.Empty;
		public string Version = string.Empty;
		public List<SaveNode> Inventory = new();
		/// <summary>Worn-modifier nodes whose item was removed; restored pointing at the item again.</summary>
		public List<(uint Record, SaveNode Modifier)> Worn = new();
		public uint Melee;
		public uint Ranged;
		/// <summary>Player store lines per player ("1" / "2"): key → value text.</summary>
		public Dictionary<string, Dictionary<string, string>> Player = new(StringComparer.Ordinal);

		public bool IsEmpty => Inventory.Count == 0 && Worn.Count == 0 && Melee == 0 && Ranged == 0 && Player.Count == 0;
	}

	public sealed class ModsLayer
	{
		public int Api;
		public string Digest = string.Empty;
		public List<ModEntry> Set = new();
		public Dictionary<string, HeldRecords> Held = new(StringComparer.OrdinalIgnoreCase);

		public ModEntry? Find(string name) => Set.Find(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
	}

	public RefiiLayer? Refii;
	public ModsLayer? Mods;

	/// <summary>
	/// Every layer this file does not know, by key, as its JSON value — a title's external mod
	/// manager's above all. Read and written back unchanged, so a layer is never lost to a reader
	/// that does not know it.
	/// </summary>
	public Dictionary<string, string> Layers = new(StringComparer.Ordinal);

	/// <summary>
	/// A save a mod layer stamps: written under at least one mod, holding records, or written with
	/// mods enabled in the active title's external mod manager.
	/// </summary>
	public bool Modded =>
		(Mods is { } mods && (mods.Set.Count != 0 || mods.Held.Count != 0)) ||
		(global::Refii.Core.Engine_TitleGame.Active.ExternalMods is { } external &&
		 Layers.TryGetValue(external.Layer, out string? layer) && external.ModCount(layer) != 0);

	// ---------------------------------------------------------------- files

	public static string PathFor(string saveDirectory) => Path.Combine(saveDirectory, FileName);

	/// <summary>The sidecar beside a save, or an empty one (no layers) when there is none or it is unreadable.</summary>
	public static SaveSidecar Read(string saveDirectory)
	{
		string path = PathFor(saveDirectory);
		try
		{
			return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : new SaveSidecar();
		}
		catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or FormatException)
		{
			return new SaveSidecar();
		}
	}

	public void Write(string saveDirectory)
	{
		string path = PathFor(saveDirectory);
		string temporary = path + ".tmp";
		File.WriteAllBytes(temporary, ToJson());
		File.Move(temporary, path, overwrite: true);
	}

	// ---------------------------------------------------------------- JSON

	public byte[] ToJson()
	{
		using var buffer = new MemoryStream();
		using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
		{
			w.WriteStartObject();
			w.WriteString("schema", Schema);
			if (Refii is { } refii)
			{
				w.WriteStartObject("refii");
				w.WriteString("build", refii.Build);
				w.WriteString("written", refii.Written);
				w.WriteStartArray("player_state");
				foreach (string file in refii.PlayerState) w.WriteStringValue(file);
				w.WriteEndArray();
				if (refii.Character is { Id.Length: > 0 } character)
				{
					w.WriteStartObject("character");
					w.WriteString("id", character.Id);
					w.WriteString("name", character.Name);
					w.WriteStartArray("achievements");
					foreach (uint id in character.Achievements) w.WriteNumberValue(id);
					w.WriteEndArray();
					w.WriteEndObject();
				}

				w.WriteEndObject();
			}

			if (Mods is { } mods)
			{
				w.WriteStartObject("mods");
				w.WriteNumber("api", mods.Api);
				w.WriteString("digest", mods.Digest);
				w.WriteStartArray("set");
				foreach (ModEntry m in mods.Set)
				{
					w.WriteStartObject();
					w.WriteString("name", m.Name);
					w.WriteString("title", m.Title);
					w.WriteString("version", m.Version);
					w.WriteString("sha256", m.Sha256);
					w.WriteNumber("order", m.Order);
					w.WriteBoolean("native", m.Native);
					w.WriteBoolean("session", m.Session);
					w.WriteStartArray("records");
					foreach (uint id in m.Records) w.WriteStringValue(Hex(id));
					w.WriteEndArray();
					w.WriteStartArray("keys");
					foreach (string key in m.Keys) w.WriteStringValue(key);
					w.WriteEndArray();
					w.WriteEndObject();
				}

				w.WriteEndArray();
				w.WriteStartObject("held");
				foreach ((string name, HeldRecords held) in mods.Held)
				{
					w.WriteStartObject(name);
					w.WriteString("title", held.Title);
					w.WriteString("version", held.Version);
					w.WriteStartArray("inventory");
					foreach (SaveNode item in held.Inventory) WriteNode(w, item);
					w.WriteEndArray();
					w.WriteStartArray("worn");
					foreach ((uint record, SaveNode modifier) in held.Worn)
					{
						w.WriteStartObject();
						w.WriteString("record", Hex(record));
						w.WritePropertyName("modifier");
						WriteNode(w, modifier);
						w.WriteEndObject();
					}

					w.WriteEndArray();
					w.WriteString("melee", Hex(held.Melee));
					w.WriteString("ranged", Hex(held.Ranged));
					w.WriteStartObject("player");
					foreach ((string player, Dictionary<string, string> values) in held.Player)
					{
						w.WriteStartObject(player);
						foreach ((string key, string value) in values) w.WriteString(key, value);
						w.WriteEndObject();
					}

					w.WriteEndObject();
					w.WriteEndObject();
				}

				w.WriteEndObject();
				w.WriteEndObject();
			}

			foreach ((string key, string layer) in Layers)
			{
				w.WritePropertyName(key);
				w.WriteRawValue(layer);
			}

			w.WriteEndObject();
		}

		return buffer.ToArray();
	}

	public static SaveSidecar Parse(byte[] json)
	{
		var sidecar = new SaveSidecar();
		using JsonDocument doc = JsonDocument.Parse(json);
		JsonElement root = doc.RootElement;
		if (root.ValueKind != JsonValueKind.Object)
		{
			throw new InvalidDataException("sidecar is not an object");
		}

		// The schema's major is the file's; a later one is still read for the layers known here.
		if (root.TryGetProperty("refii", out JsonElement refii) && refii.ValueKind == JsonValueKind.Object)
		{
			var layer = new RefiiLayer { Build = Str(refii, "build"), Written = Str(refii, "written") };
			if (refii.TryGetProperty("player_state", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
			{
				layer.PlayerState.Clear();
				foreach (JsonElement file in files.EnumerateArray()) layer.PlayerState.Add(file.GetString() ?? string.Empty);
			}

			if (refii.TryGetProperty("character", out JsonElement character) && character.ValueKind == JsonValueKind.Object &&
				Str(character, "id") is { Length: > 0 } characterId)
			{
				var hero = new CharacterLayer { Id = characterId, Name = Str(character, "name") };
				if (character.TryGetProperty("achievements", out JsonElement ids) && ids.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement id in ids.EnumerateArray())
					{
						if (id.ValueKind == JsonValueKind.Number && id.TryGetUInt32(out uint value) && value != 0)
						{
							hero.Achievements.Add(value);
						}
					}
				}

				layer.Character = hero;
			}

			sidecar.Refii = layer;
		}

		if (root.TryGetProperty("mods", out JsonElement mods) && mods.ValueKind == JsonValueKind.Object)
		{
			var layer = new ModsLayer { Api = Int(mods, "api"), Digest = Str(mods, "digest") };
			if (mods.TryGetProperty("set", out JsonElement set) && set.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement m in set.EnumerateArray())
				{
					var entry = new ModEntry
					{
						Name = Str(m, "name"), Title = Str(m, "title"), Version = Str(m, "version"), Sha256 = Str(m, "sha256"),
						Order = Int(m, "order"), Native = Bool(m, "native"), Session = Bool(m, "session"),
					};
					if (m.TryGetProperty("records", out JsonElement records) && records.ValueKind == JsonValueKind.Array)
						foreach (JsonElement id in records.EnumerateArray()) entry.Records.Add(ParseHex(id.GetString()));
					if (m.TryGetProperty("keys", out JsonElement keys) && keys.ValueKind == JsonValueKind.Array)
						foreach (JsonElement key in keys.EnumerateArray()) entry.Keys.Add(key.GetString() ?? string.Empty);
					if (entry.Name.Length != 0) layer.Set.Add(entry);
				}
			}

			if (mods.TryGetProperty("held", out JsonElement held) && held.ValueKind == JsonValueKind.Object)
			{
				foreach (JsonProperty h in held.EnumerateObject())
				{
					var records = new HeldRecords
					{
						Title = Str(h.Value, "title"), Version = Str(h.Value, "version"),
						Melee = ParseHex(Str(h.Value, "melee")), Ranged = ParseHex(Str(h.Value, "ranged")),
					};
					if (h.Value.TryGetProperty("inventory", out JsonElement inventory) && inventory.ValueKind == JsonValueKind.Array)
						foreach (JsonElement item in inventory.EnumerateArray()) records.Inventory.Add(ReadNode(item));
					if (h.Value.TryGetProperty("worn", out JsonElement worn) && worn.ValueKind == JsonValueKind.Array)
						foreach (JsonElement entry in worn.EnumerateArray())
							if (entry.TryGetProperty("modifier", out JsonElement modifier))
								records.Worn.Add((ParseHex(Str(entry, "record")), ReadNode(modifier)));
					if (h.Value.TryGetProperty("player", out JsonElement player) && player.ValueKind == JsonValueKind.Object)
					{
						foreach (JsonProperty p in player.EnumerateObject())
						{
							var values = new Dictionary<string, string>(StringComparer.Ordinal);
							foreach (JsonProperty kv in p.Value.EnumerateObject()) values[kv.Name] = kv.Value.GetString() ?? string.Empty;
							records.Player[p.Name] = values;
						}
					}

					layer.Held[h.Name] = records;
				}
			}

			sidecar.Mods = layer;
		}

		foreach (JsonProperty layer in root.EnumerateObject())
		{
			if (layer.Name is not ("schema" or "refii" or "mods"))
			{
				sidecar.Layers[layer.Name] = layer.Value.GetRawText();
			}
		}

		return sidecar;
	}

	// ---------------------------------------------------------------- a save node as JSON

	/// <summary>`{"n":name,"a":[[key,value]…],"t":value,"ta":n,"c":[…]}`; a value is `{"s"|"u"|"i"|"fb"|"b": …}` (a float by its raw bits).</summary>
	public static void WriteNode(Utf8JsonWriter w, SaveNode node)
	{
		w.WriteStartObject();
		w.WriteString("n", node.Name);
		if (node.Attributes.Count != 0)
		{
			w.WriteStartArray("a");
			foreach ((string key, object value) in node.Attributes)
			{
				w.WriteStartArray();
				w.WriteStringValue(key);
				WriteValue(w, value);
				w.WriteEndArray();
			}

			w.WriteEndArray();
		}

		if (node.Text is { } text)
		{
			w.WritePropertyName("t");
			WriteValue(w, text);
			w.WriteNumber("ta", node.TextAt);
		}

		if (node.Children.Count != 0)
		{
			w.WriteStartArray("c");
			foreach (SaveNode child in node.Children) WriteNode(w, child);
			w.WriteEndArray();
		}

		w.WriteEndObject();
	}

	private static void WriteValue(Utf8JsonWriter w, object value)
	{
		w.WriteStartObject();
		switch (value)
		{
			case string s: w.WriteString("s", s); break;
			case uint u: w.WriteNumber("u", u); break;
			case int i: w.WriteNumber("i", i); break;
			case float f: w.WriteNumber("fb", unchecked((uint)BitConverter.SingleToInt32Bits(f))); break;
			case bool b: w.WriteBoolean("b", b); break;
			default: throw new InvalidDataException($"save value of type {value.GetType().Name}");
		}

		w.WriteEndObject();
	}

	public static SaveNode ReadNode(JsonElement e)
	{
		var node = new SaveNode(Str(e, "n"));
		if (e.TryGetProperty("a", out JsonElement attributes) && attributes.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement pair in attributes.EnumerateArray())
			{
				if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2)
				{
					node.Attributes.Add((pair[0].GetString() ?? string.Empty, ReadValue(pair[1])));
				}
			}
		}

		if (e.TryGetProperty("t", out JsonElement text))
		{
			node.Text = ReadValue(text);
			node.TextAt = Int(e, "ta");
		}

		if (e.TryGetProperty("c", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement child in children.EnumerateArray()) node.Children.Add(ReadNode(child));
		}

		return node;
	}

	private static object ReadValue(JsonElement e)
	{
		if (e.TryGetProperty("s", out JsonElement s)) return s.GetString() ?? string.Empty;
		if (e.TryGetProperty("u", out JsonElement u)) return u.GetUInt32();
		if (e.TryGetProperty("i", out JsonElement i)) return i.GetInt32();
		if (e.TryGetProperty("fb", out JsonElement fb)) return BitConverter.Int32BitsToSingle(unchecked((int)fb.GetUInt32()));
		if (e.TryGetProperty("b", out JsonElement b)) return b.GetBoolean();
		throw new InvalidDataException("save value without a type");
	}

	// ---------------------------------------------------------------- helpers

	public static string Hex(uint id) => "0x" + id.ToString("X8", CultureInfo.InvariantCulture);

	public static uint ParseHex(string? text)
	{
		if (string.IsNullOrEmpty(text)) return 0u;
		string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
		return uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value) ? value : 0u;
	}

	private static string Str(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

	private static int Int(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

	private static bool Bool(JsonElement e, string name) =>
		e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.True);

	public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

	/// <summary>Player store text (`key=value` lines, `#` comments) as key → value, in file order.</summary>
	public static List<(string Key, string Value)> ParsePlayerStore(string text)
	{
		var lines = new List<(string, string)>();
		foreach (string raw in text.Split('\n'))
		{
			string line = raw.TrimEnd('\r');
			int equals = line.IndexOf('=');
			if (line.Length == 0 || line[0] == '#' || equals <= 0)
			{
				continue;
			}

			lines.Add((line[..equals], line[(equals + 1)..]));
		}

		return lines;
	}

	public static string PlayerStoreText(IEnumerable<(string Key, string Value)> lines)
	{
		var text = new StringBuilder("# Refii: this save's hero's mod values (refii_mod_player_*), key=value\n");
		foreach ((string key, string value) in lines)
		{
			text.Append(key).Append('=').Append(value).Append('\n');
		}

		return text.ToString();
	}
}
