using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Refii.Ui;

/// <summary>
/// The modal answer table — see <see cref="Engine_UiModalTemplate"/> — as a composable record set:
/// the shipped rows, then every mod's <c>ui/templates.json</c> <c>"modals"</c> in load order, then
/// the player's file. It decides who ANSWERS a box, never whether it is drawn: an unlisted box is
/// drawn and keeps the guest's own input.
/// </summary>
/// <remarks>
/// <para>A record is keyed <c>"&lt;modal id&gt;"</c> for any screen or
/// <c>"&lt;modal id&gt;@&lt;screen id&gt;"</c> for one screen, and is replaced WHOLE, like a
/// screen record:</para>
/// <code>
/// "modals": {
///   "13":   { "kind": "query", "confirm": 39, "cancel": 37 },
///   "20":   { "kind": "query", "confirm": 36, "alternate": 75 },
///   "0@22": { "kind": "message", "confirm": 36 },
///   "42":   null }
/// </code>
/// <para><c>kind</c> is <c>query</c> (confirm and cancel) or <c>message</c> (one dismiss code);
/// a query with no <c>cancel</c> leaves B doing nothing, and its <c>alternate</c> is the code X
/// answers, if any. A <c>null</c> record removes the row, so
/// the box keeps the guest's own input. <see cref="ToJson"/> writes every row in the same shape, so
/// the snapshot round-trips.</para>
/// <para>Pure .NET over <see cref="System.Text.Json"/>: the composition is held by tests without a
/// Godot type in the host.</para>
/// </remarks>
public sealed class Engine_UiModals
{
	public const string Section = "modals";

	/// <summary>
	/// The shipped rows. Unlisted boxes keep the guest's input deliberately: the marketplace offer
	/// (42) carries an X download action and a sign-in gate, and photo mode's box has no dismiss
	/// code at all.
	///
	/// The guest's own input still answers them under the UI cut: an unlisted box is drawn but not
	/// masked, and the router's modal input `sub_8236E458` (disc `sub_82336668`) answers boxes 42 and
	/// 44 itself and hands every other box's code to the C++ handler of the screen the opener pushed
	/// (`FindScreenHandler(*(*(GUI+56)+4))`, vtable +28 at 0x8236e5c8 / 0x823367cc). Nothing on that
	/// path reads a presentation or runs the cut Lua.
	///
	/// The co-op screen's boxes answer for player two only (`sub_8243A828`: `8243A838 cmplwi cr6,
	/// r5, 2`, disc 0x8247BA30): the query pair 39 / 41 takes 36 and, as a query (mode 3), 37; the
	/// notices 40 / 43 close on 36. The lever carries player two (Fable2ActingPlayer.BoxOwner).
	/// </summary>
	public static IReadOnlyList<Engine_UiModalTemplate> Shipped() => new[]
	{
		// QueryBoxes — the saving, shop and real-estate confirms all answer 46/47.
		Query(2, 46, 47), Query(15, 46, 47), Query(17, 46, 47), Query(19, 46, 47),
		Query(23, 46, 47), Query(24, 46, 47), Query(25, 46, 47),
		Query(29, 46, 47), Query(30, 46, 47), Query(31, 46, 47), Query(32, 46, 47),
		Query(33, 46, 47), Query(34, 46, 47),
		Query(3, 46, 47), Query(4, 46, 47), Query(5, 46, 47), Query(6, 46, 47),
		Query(8, 46, 47), Query(9, 46, 47), Query(10, 46, 47), Query(11, 46, 47),

		// The henchman and generic boxes answer the uniform accept/back pair.
		Query(26, 36, 37), Query(27, 36, 37), Query(28, 36, 37), Query(44, 36, 37),

		// The co-op screen's warnings and notices, player two's.
		Query(39, 36, 37), Query(41, 36, 37), Dismiss(40, 36), Dismiss(43, 36),

		// Fast travel confirms on the per-screen Y action.
		Query(13, 39, 37),

		// B does nothing here — see Engine_UiModalTemplate.Cancel. X equips what was bought
		// (Engine_UiModalTemplate.Alternate).
		Query(20, 36, null, alternate: 75),

		// MessageBoxes — a single dismiss. Id 0 is per-screen; the rest are uniform.
		Dismiss(0, 36, screenId: 22),   // shop: already wearing that tattoo
		Dismiss(0, 46, screenId: 28),   // real estate: buy denied / cannot resell
		Dismiss(1, 36),                 // abilities: maxed / not enough experience
		Dismiss(12, 36),                // world map: cannot fast travel
		Dismiss(16, 36), Dismiss(18, 36), Dismiss(21, 36), Dismiss(22, 36),
		Dismiss(38, 36),                // equipment / expression / augment notices
	};

	private static Engine_UiModalTemplate Query(uint id, uint confirm, uint? cancel, uint? alternate = null) =>
		new() { ModalId = id, Confirm = confirm, Cancel = cancel, Alternate = alternate };

	private static Engine_UiModalTemplate Dismiss(uint id, uint code, uint screenId = uint.MaxValue) =>
		new() { ModalId = id, ScreenId = screenId, Confirm = code, Dismissal = true };

	/// <summary>A row's key: <c>"13"</c>, or <c>"0@22"</c> for a row that holds on one screen.</summary>
	public static string KeyOf(Engine_UiModalTemplate modal) =>
		modal.ScreenId == uint.MaxValue
			? modal.ModalId.ToString(CultureInfo.InvariantCulture)
			: $"{modal.ModalId.ToString(CultureInfo.InvariantCulture)}@{modal.ScreenId.ToString(CultureInfo.InvariantCulture)}";

	/// <summary>Splits a key into its modal id and screen id (<see cref="uint.MaxValue"/> for any).</summary>
	public static bool TryParseKey(string key, out uint modalId, out uint screenId)
	{
		screenId = uint.MaxValue;
		int at = key.IndexOf('@');
		string id = at < 0 ? key : key[..at];
		if (!uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out modalId))
		{
			return false;
		}

		return at < 0 || (uint.TryParse(key[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out screenId) &&
			screenId != uint.MaxValue);
	}

	private readonly Dictionary<string, Engine_UiModalTemplate> _rows = new(StringComparer.Ordinal);

	/// <summary>A table holding the shipped rows.</summary>
	public Engine_UiModals()
	{
		foreach (Engine_UiModalTemplate modal in Shipped())
		{
			_rows[KeyOf(modal)] = modal;
		}
	}

	public int Count => _rows.Count;

	/// <summary>Every row, by key, in key order.</summary>
	public IEnumerable<KeyValuePair<string, Engine_UiModalTemplate>> Rows =>
		_rows.OrderBy(pair => pair.Key, StringComparer.Ordinal);

	/// <summary>
	/// The answer for a box, or null when it is not owned. A screen-specific row wins over the
	/// any-screen one, so id 0 can be per-screen while every other id states itself once.
	/// </summary>
	public Engine_UiModalTemplate? Find(uint modalId, uint screenId, bool dismissal)
	{
		Engine_UiModalTemplate? general = null;
		foreach (Engine_UiModalTemplate modal in _rows.Values)
		{
			if (modal.ModalId != modalId || modal.Dismissal != dismissal)
			{
				continue;
			}

			if (modal.ScreenId == screenId && screenId != uint.MaxValue)
			{
				return modal;
			}

			if (modal.ScreenId == uint.MaxValue)
			{
				general = modal;
			}
		}

		return general;
	}

	/// <summary>
	/// Applies one templates file's <c>"modals"</c> over the table. <paramref name="noteOwner"/> is
	/// told each piece it states (<c>"modal:&lt;key&gt;"</c>) so two mods stating one row are
	/// recorded as a conflict; <paramref name="warn"/> gets each record it refuses. Returns how many
	/// rows it set or removed; 0 for a file with no <c>"modals"</c> or that is not JSON (the
	/// templates loader reports that).
	/// </summary>
	public int ApplyText(string text, Action<string> noteOwner, Action<string> warn)
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(text);
		}
		catch (JsonException)
		{
			return 0;
		}

		using (document)
		{
			if (document.RootElement.ValueKind != JsonValueKind.Object ||
				!document.RootElement.TryGetProperty(Section, out JsonElement modals))
			{
				return 0;
			}

			if (modals.ValueKind != JsonValueKind.Object)
			{
				warn($"\"{Section}\" must be an object of modal records by key; ignored");
				return 0;
			}

			int taken = 0;
			foreach (JsonProperty entry in modals.EnumerateObject())
			{
				if (!TryParseKey(entry.Name, out _, out _))
				{
					warn($"modal '{entry.Name}' is not a key: \"<modal id>\" or \"<modal id>@<screen id>\"; ignored");
					continue;
				}

				if (entry.Value.ValueKind == JsonValueKind.Null)
				{
					noteOwner("modal:" + entry.Name);
					if (_rows.Remove(entry.Name))
					{
						++taken;
					}

					continue;
				}

				if (ParseRecord(entry.Name, entry.Value, out string error) is not { } modal)
				{
					warn($"modal '{entry.Name}': {error}; ignored");
					continue;
				}

				noteOwner("modal:" + entry.Name);
				_rows[entry.Name] = modal;
				++taken;
			}

			return taken;
		}
	}

	/// <summary>One record, or null with the reason.</summary>
	public static Engine_UiModalTemplate? ParseRecord(string key, JsonElement record, out string error)
	{
		error = string.Empty;
		if (!TryParseKey(key, out uint modalId, out uint screenId))
		{
			error = "the key is not \"<modal id>\" or \"<modal id>@<screen id>\"";
			return null;
		}

		if (record.ValueKind != JsonValueKind.Object)
		{
			error = "must be an object { \"kind\", \"confirm\", \"cancel\" } or null";
			return null;
		}

		string kind = record.TryGetProperty("kind", out JsonElement k) && k.ValueKind == JsonValueKind.String
			? (k.GetString() ?? string.Empty).Trim().ToLowerInvariant()
			: "query";
		if (kind is not ("query" or "message"))
		{
			error = $"'{kind}' is not a modal kind: query or message";
			return null;
		}

		if (Code(record, "confirm") is not { } confirm)
		{
			error = "\"confirm\" must be an input code (a whole number 0 or above)";
			return null;
		}

		uint? cancel = null;
		if (record.TryGetProperty("cancel", out JsonElement c) && c.ValueKind != JsonValueKind.Null)
		{
			if (kind == "message")
			{
				error = "a message box has one dismiss code; \"cancel\" belongs to a query";
				return null;
			}

			if (Code(record, "cancel") is not { } code)
			{
				error = "\"cancel\" must be an input code or null";
				return null;
			}

			cancel = code;
		}

		uint? alternate = null;
		if (record.TryGetProperty("alternate", out JsonElement x) && x.ValueKind != JsonValueKind.Null)
		{
			if (kind == "message")
			{
				error = "a message box has one dismiss code; \"alternate\" belongs to a query";
				return null;
			}

			if (Code(record, "alternate") is not { } code)
			{
				error = "\"alternate\" must be an input code or null";
				return null;
			}

			alternate = code;
		}

		return new Engine_UiModalTemplate
		{
			ModalId = modalId,
			ScreenId = screenId,
			Confirm = confirm,
			Cancel = cancel,
			Alternate = alternate,
			Dismissal = kind == "message",
		};
	}

	private static uint? Code(JsonElement record, string key) =>
		record.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
		value.TryGetDouble(out double number) && number >= 0 && number <= uint.MaxValue && number == Math.Floor(number)
			? (uint)number
			: null;

	/// <summary>Every row as the <c>"modals"</c> object a templates file states: what the snapshot writes.</summary>
	public string ToJson()
	{
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
		{
			writer.WriteStartObject();
			foreach ((string key, Engine_UiModalTemplate modal) in Rows)
			{
				writer.WriteStartObject(key);
				writer.WriteString("kind", modal.Dismissal ? "message" : "query");
				writer.WriteNumber("confirm", modal.Confirm);
				if (modal.Cancel is { } cancel)
				{
					writer.WriteNumber("cancel", cancel);
				}

				if (modal.Alternate is { } alternate)
				{
					writer.WriteNumber("alternate", alternate);
				}

				writer.WriteEndObject();
			}

			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}
}
