using System;
using System.Collections.Generic;
using System.Text;
using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// THE CONTROLS PAGE: Options › Controls › Controller and Keyboard, one host options page
/// (<see cref="Engine_UiHostOptionsPage"/> — the shared furniture, cursor, stepping and prompts)
/// with three sections: the controller (glyph style, glyph pack, each player's remap of the game's
/// buttons), the keyboard and mouse (every <c>GameInput.KeyActions</c> row), then every mod's
/// actions (<see cref="Refii.Mods.ModInputUi"/>).
/// </summary>
/// <remarks>
/// <para>A binding row steps through: "press a key" (the far left — stepping onto it arms a
/// capture, <see cref="Engine_UiInputCapture"/>, and the next fresh press is the binding), Default,
/// Unbound, then every bindable input; a stored list the steps cannot spell (two sources, an
/// unlisted key) is kept as one more position at the right end. A reset row puts its section back
/// to Default. A writes every row to refii.toml in one write — a row at Default removes its key —
/// and the bindings take effect at once; B leaves everything as it was.</para>
/// <para>Rebinding targets the GAME's buttons, never an action: a remap row is "which physical
/// inputs press the game's X"; a key row is the existing keyboard table's. A caption names the
/// input in the player's glyph style (Cross, not A, on a PlayStation pad) and says what else the
/// same input is bound to.</para>
/// <para>The one exception is a key action that declares itself pad-bindable
/// (<c>GameInput.KeyAction.PadBindable</c>): it gets a controller row under its key row
/// (<see cref="PadActionLine"/>), unbound by default, whose capture takes a chord. Both rows store
/// into the one <c>key_*</c> setting.</para>
/// </remarks>
public sealed class Engine_UiControlsPage : Engine_UiHostOptionsPage
{
	public const string OpenCommand = "options:input";
	public const string PageName = "ControllerAndKeyboard";
	public const string PageTitle = "Controller and Keyboard";

	public static Engine_UiControlsPage Page { get; } = new();

	public override string Name => PageName;
	public override string Title => PageTitle;

	/// <summary>One row of the page.</summary>
	public abstract class Line
	{
		/// <summary>The rows a reset row resets: its section's.</summary>
		public string Section = string.Empty;

		public abstract string Label { get; }

		public virtual int Choices => 1;

		public virtual string Caption(int index) => string.Empty;

		/// <summary>The row's index for the stored values <paramref name="read"/> gives (empty for unset).</summary>
		public virtual int IndexOfStored(Func<string, string> read) => 0;

		/// <summary>What the row writes at <paramref name="index"/>: setting → value, null to remove it.</summary>
		public virtual void Store(int index, IDictionary<string, string?> values)
		{
		}

		/// <summary>The index of the row's default.</summary>
		public virtual int DefaultIndex => 0;

		/// <summary>The device a capture at index 0 takes, or null for a row without one.</summary>
		public virtual Engine_UiInputCapture.Kind? Captures => null;

		/// <summary>The index a captured input's name stands at, or −1 when the row cannot take it.</summary>
		public virtual int IndexOfCaptured(string name) => -1;

		/// <summary>The inputs (lowercased names) the row binds at <paramref name="index"/>, for the conflict notes.</summary>
		public virtual IReadOnlyList<string> Bound(int index) => Array.Empty<string>();
	}

	/// <summary>A section's heading.</summary>
	public sealed class Header(string title) : Line
	{
		public override string Label => title.ToUpperInvariant();
	}

	/// <summary>A setting chosen from a list; index 0 is its default and removes the key.</summary>
	public sealed class ChoiceLine(string label, string key, IReadOnlyList<(string Value, string Caption)> options) : Line
	{
		/// <summary>A stored value the list does not offer, kept as the last position.</summary>
		private string? _extra;

		public override string Label => label;
		public override int Choices => options.Count + (_extra is null ? 0 : 1);
		public override string Caption(int index) =>
			index < options.Count ? options[index].Caption : _extra ?? string.Empty;

		public override int IndexOfStored(Func<string, string> read)
		{
			string stored = read(key).Trim().Trim('"').Trim();
			_extra = null;
			for (int i = 0; i < options.Count; i++)
			{
				if (string.Equals(options[i].Value, stored, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}

			if (stored.Length == 0)
			{
				return 0;
			}

			_extra = stored;
			return options.Count;
		}

		public override void Store(int index, IDictionary<string, string?> values) =>
			values[key] = index == 0 ? null : index < options.Count ? options[index].Value : _extra;
	}

	/// <summary>Steps onto "reset" and every row of <see cref="Target"/> goes back to its default.</summary>
	public sealed class ResetLine(string label, string target) : Line
	{
		public string Target => target;
		public override string Label => label;
		public override int Choices => 2;
		public override string Caption(int index) => index == 0 ? "-" : "Reset (A saves, B keeps)";
	}

	/// <summary>
	/// The game's button <paramref name="game"/> on <paramref name="player"/>'s controller: which
	/// physical inputs press it (<see cref="Engine_PadRemap"/>, <c>padmap_*</c> / <c>padmap2_*</c>).
	/// </summary>
	public sealed class PadLine(int player, PadInput game, Func<GlyphStyle> style, Func<bool> ps5, string? role) : Line
	{
		public const int CaptureIndex = 0;
		public const int DefaultPosition = 1;
		public const int UnboundIndex = 2;
		public const int FirstInput = 3;

		/// <summary>A stored source list the steps cannot spell (two or more), as the last position.</summary>
		private IReadOnlyList<PadInput>? _extra;

		public int Player => player;
		public PadInput Game => game;
		public string SettingKey => Engine_PadRemap.SettingKey(player, game);

		public override string Label
		{
			get
			{
				string name = Engine_ControllerFamily.Label(style(), game, ps5());
				return string.IsNullOrEmpty(role) ? name : name + ": " + role;
			}
		}

		public override int DefaultIndex => DefaultPosition;
		public override Engine_UiInputCapture.Kind? Captures => Engine_UiInputCapture.Kind.Pad;
		public override int Choices => FirstInput + Engine_PadRemap.Count + (_extra is null ? 0 : 1);

		/// <summary>The physical inputs the row binds at <paramref name="index"/>.</summary>
		public IReadOnlyList<PadInput> Sources(int index) => index switch
		{
			DefaultPosition or CaptureIndex => new[] { game },
			UnboundIndex => Array.Empty<PadInput>(),
			_ when index - FirstInput < Engine_PadRemap.Count => new[] { (PadInput)(index - FirstInput) },
			_ => _extra ?? new[] { game },
		};

		public override string Caption(int index) => index switch
		{
			CaptureIndex => "Press a button...",
			DefaultPosition => "Default (" + Names(new[] { game }) + ")",
			UnboundIndex => "Unbound",
			_ => Names(Sources(index)),
		};

		private string Names(IReadOnlyList<PadInput> inputs)
		{
			var text = new StringBuilder();
			foreach (PadInput input in inputs)
			{
				text.Append(text.Length == 0 ? string.Empty : " + ").Append(Engine_ControllerFamily.Label(style(), input, ps5()));
			}

			return text.ToString();
		}

		public override int IndexOfStored(Func<string, string> read)
		{
			_extra = null;
			IReadOnlyList<PadInput>? stored = Engine_PadRemap.ParseStored(read(SettingKey));
			if (stored is null)
			{
				return DefaultPosition;
			}

			if (stored.Count == 0)
			{
				return UnboundIndex;
			}

			if (stored.Count == 1)
			{
				return stored[0] == game ? DefaultPosition : FirstInput + (int)stored[0];
			}

			_extra = stored;
			return FirstInput + Engine_PadRemap.Count;
		}

		public override void Store(int index, IDictionary<string, string?> values) =>
			values[SettingKey] = index switch
			{
				DefaultPosition or CaptureIndex => null,
				_ => Sources(index) is { Count: 1 } one && one[0] == game ? null : Engine_PadRemap.Format(Sources(index)),
			};

		public override int IndexOfCaptured(string name) =>
			Engine_PadRemap.TryParse(name, out PadInput input) ? (input == game ? DefaultPosition : FirstInput + (int)input) : -1;

		public override IReadOnlyList<string> Bound(int index)
		{
			var names = new List<string>();
			foreach (PadInput input in Sources(index))
			{
				names.Add("pad" + player + ":" + input);
			}

			return names;
		}
	}

	/// <summary>One keyboard and mouse action of the game's table (<c>key_&lt;action&gt;</c>).</summary>
	public sealed class KeyLine(string action, string defaults, IReadOnlyList<string> candidates,
		Func<string, bool>? isDefault = null) : Line
	{
		public const int CaptureIndex = 0;
		public const int DefaultPosition = 1;
		public const int UnboundIndex = 2;
		public const int FirstInput = 3;

		/// <summary>A stored list the steps cannot spell (several keys, an unlisted name), as the last position.</summary>
		private string? _extra;

		public string Action => action;
		public string SettingKey => "key_" + action;
		public string Defaults => defaults;

		public override string Label
		{
			get
			{
				string spaced = action.Replace('_', ' ');
				return spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced[1..];
			}
		}

		public override int DefaultIndex => DefaultPosition;
		public override Engine_UiInputCapture.Kind? Captures => Engine_UiInputCapture.Kind.Keyboard;
		public override int Choices => FirstInput + candidates.Count + (_extra is null ? 0 : 1);

		/// <summary>The stored text at an index: null for the default, <c>none</c> unbound, else the names.</summary>
		public string? Text(int index) => index switch
		{
			DefaultPosition or CaptureIndex => null,
			UnboundIndex => "none",
			_ when index - FirstInput < candidates.Count => candidates[index - FirstInput],
			_ => _extra,
		};

		public override string Caption(int index) => index switch
		{
			CaptureIndex => "Press a key...",
			DefaultPosition => "Default (" + Spaced(defaults) + ")",
			UnboundIndex => "Unbound",
			_ => Spaced(Text(index) ?? defaults),
		};

		private static string Spaced(string list) => string.Join(", ", Refii.Mods.ModInput.Names(list));

		public override int IndexOfStored(Func<string, string> read)
		{
			_extra = null;
			string stored = Engine_PadActionBinding.KeysOf(read(SettingKey));
			if (stored.Length == 0 || string.Equals(stored, defaults, StringComparison.OrdinalIgnoreCase) ||
				isDefault?.Invoke(stored) == true)
			{
				return DefaultPosition;
			}

			if (stored.Equals("none", StringComparison.OrdinalIgnoreCase))
			{
				return UnboundIndex;
			}

			int at = IndexOf(stored);
			if (at >= 0)
			{
				return FirstInput + at;
			}

			_extra = stored;
			return FirstInput + candidates.Count;
		}

		private int IndexOf(string name)
		{
			for (int i = 0; i < candidates.Count; i++)
			{
				if (string.Equals(candidates[i], name, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}

			return -1;
		}

		public override void Store(int index, IDictionary<string, string?> values) => values[SettingKey] = Text(index);

		public override int IndexOfCaptured(string name)
		{
			int at = IndexOf(name);
			if (at >= 0)
			{
				return FirstInput + at;
			}

			_extra = name;
			return FirstInput + candidates.Count;
		}

		public override IReadOnlyList<string> Bound(int index)
		{
			if (index == UnboundIndex)
			{
				return Array.Empty<string>();
			}

			var names = new List<string>();
			foreach (string name in Refii.Mods.ModInput.Names(Text(index) ?? defaults))
			{
				names.Add("key:" + name.ToLowerInvariant());
			}

			return names;
		}

		/// <summary>Whether the game's own defaults already put <paramref name="bound"/> here (Escape on pause and back).</summary>
		public bool DefaultsBind(string bound)
		{
			foreach (string name in Refii.Mods.ModInput.Names(defaults))
			{
				if ("key:" + name.ToLowerInvariant() == bound)
				{
					return true;
				}
			}

			return false;
		}
	}

	/// <summary>
	/// The controller row of a pad-bindable key action: the chord of the game's buttons that also
	/// presses it (the <c>Pad:</c> tokens of <c>key_&lt;action&gt;</c>, <see cref="Engine_PadActionBinding"/>).
	/// Unbound is its default. It stores after its key row, into the same setting.
	/// </summary>
	public sealed class PadActionLine(string action, Func<GlyphStyle> style, Func<bool> ps5) : Line
	{
		public const int CaptureIndex = 0;
		public const int UnboundIndex = 1;
		public const int FirstInput = 2;

		/// <summary>Stored chords the steps cannot spell (a chord of several inputs, or several chords), as the last position.</summary>
		private IReadOnlyList<ushort>? _extra;

		public string Action => action;
		public string SettingKey => "key_" + action;

		public override string Label
		{
			get
			{
				string spaced = action.Replace('_', ' ');
				return (spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced[1..]) + " (controller)";
			}
		}

		public override int DefaultIndex => UnboundIndex;
		public override Engine_UiInputCapture.Kind? Captures => Engine_UiInputCapture.Kind.PadChord;
		public override int Choices => FirstInput + Engine_PadRemap.Count + (_extra is null ? 0 : 1);

		/// <summary>The chords the row binds at <paramref name="index"/>.</summary>
		public IReadOnlyList<ushort> Chords(int index) => index switch
		{
			CaptureIndex or UnboundIndex => Array.Empty<ushort>(),
			_ when index - FirstInput < Engine_PadRemap.Count => new[] { (ushort)(1 << (index - FirstInput)) },
			_ => _extra ?? Array.Empty<ushort>(),
		};

		public override string Caption(int index)
		{
			if (index == CaptureIndex)
			{
				return "Press a button, or hold several...";
			}

			IReadOnlyList<ushort> chords = Chords(index);
			if (chords.Count == 0)
			{
				return "Unbound";
			}

			var text = new StringBuilder();
			foreach (ushort chord in chords)
			{
				text.Append(text.Length == 0 ? string.Empty : ", ");
				bool first = true;
				foreach (PadInput input in Engine_PadActionBinding.Inputs(chord))
				{
					text.Append(first ? string.Empty : " + ").Append(Engine_ControllerFamily.Label(style(), input, ps5()));
					first = false;
				}
			}

			return text.ToString();
		}

		private int IndexOf(IReadOnlyList<ushort> chords)
		{
			_extra = null;
			if (chords.Count == 0)
			{
				return UnboundIndex;
			}

			if (chords.Count == 1 && Engine_PadActionBinding.Inputs(chords[0]) is { Count: 1 } one)
			{
				return FirstInput + (int)one[0];
			}

			_extra = chords;
			return FirstInput + Engine_PadRemap.Count;
		}

		public override int IndexOfStored(Func<string, string> read) => IndexOf(Engine_PadActionBinding.Chords(read(SettingKey)));

		/// <summary>The key row stored the setting's keys just before (null for the default); the chords join them.</summary>
		public override void Store(int index, IDictionary<string, string?> values)
		{
			values.TryGetValue(SettingKey, out string? keys);
			values[SettingKey] = Engine_PadActionBinding.Combine(
				Engine_PadActionBinding.KeysOf(keys), Engine_PadActionBinding.Format(Chords(index)));
		}

		/// <summary>
		/// A captured chord's physical inputs as the GAME buttons they press for player one — the
		/// chord names the game's buttons, as a mod's pad binding does. −1 for an input that presses none.
		/// </summary>
		public override int IndexOfCaptured(string name)
		{
			ushort chord = 0;
			foreach (string part in name.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				if (!Engine_PadRemap.TryParse(part, out PadInput physical))
				{
					return -1;
				}

				IReadOnlyList<PadInput> targets = Engine_PadRemap.For(1).Targets(physical);
				if (targets.Count == 0)
				{
					return -1;
				}

				chord |= (ushort)(1 << (int)targets[0]);
			}

			return chord == 0 ? -1 : IndexOf(new[] { chord });
		}

		public override IReadOnlyList<string> Bound(int index)
		{
			var names = new List<string>();
			foreach (ushort chord in Chords(index))
			{
				names.Add("padaction:" + Engine_PadActionBinding.ChordName(chord));
			}

			return names;
		}
	}

	/// <summary>One mod action's row on one device (<see cref="Refii.Mods.ModInputUi.Row"/>), behind a capture position.</summary>
	public sealed class ModLine(Refii.Mods.ModInputUi.Row row) : Line
	{
		public override string Label => row.Label;
		public override int Choices => row.Choices + 1;
		public override int DefaultIndex => 1 + Refii.Mods.ModInputUi.DefaultIndex;

		public override Engine_UiInputCapture.Kind? Captures =>
			row.Device == Refii.Mods.ModInputUi.Device.Keyboard ? Engine_UiInputCapture.Kind.Keyboard : Engine_UiInputCapture.Kind.Pad;

		public override string Caption(int index) =>
			index == 0 ? (row.Device == Refii.Mods.ModInputUi.Device.Keyboard ? "Press a key..." : "Press a button...") : row.Caption(index - 1);

		public override int IndexOfStored(Func<string, string> read) => 1 + row.IndexOfStored(read(row.SettingKey));

		public override void Store(int index, IDictionary<string, string?> values) =>
			values[row.SettingKey] = index == 0 ? null : row.StoredAt(index - 1);

		/// <summary>
		/// A captured key by its name; a captured pad input as the GAME button it presses for player
		/// one — a mod's pad binding names the game's buttons.
		/// </summary>
		public override int IndexOfCaptured(string name)
		{
			if (row.Device == Refii.Mods.ModInputUi.Device.Pad && Engine_PadRemap.TryParse(name, out PadInput physical))
			{
				IReadOnlyList<PadInput> targets = Engine_PadRemap.For(1).Targets(physical);
				name = targets.Count != 0 ? Engine_PadRemap.Name(targets[0]) : name;
			}

			int at = row.IndexOfStored(name);
			return at == Refii.Mods.ModInputUi.DefaultIndex ? -1 : at + 1;
		}
	}

	private List<Line> _lines = new();

	private int[] _committed = Array.Empty<int>();

	/// <summary>The row whose capture is armed, −1 for none.</summary>
	private int _captureRow = -1;

	/// <summary>The packs on offer, re-scanned at most this often while the page is previewed.</summary>
	private const ulong PackScanMs = 5000;

	private List<string> _packs = new();
	private ulong _packsAt;
	private bool _packsScanned;

	/// <summary>What the rows were last built for: the mods' sections and whether player two has a controller section.</summary>
	private IReadOnlyList<Refii.Mods.ModInputUi.Section>? _builtMods;
	private bool _builtCoop;

	public IReadOnlyList<Line> Lines => _lines;

	public override int Count => _lines.Count;
	public override string RowTitle(int row) => row < _lines.Count ? _lines[row].Label : string.Empty;
	public override int Choices(int row) => row < _lines.Count ? _lines[row].Choices : 1;

	public override string Caption(int row, int index)
	{
		if (row >= _lines.Count)
		{
			return string.Empty;
		}

		Line line = _lines[row];
		if (row == _captureRow && Engine_UiInputCapture.Armed is not null)
		{
			return line.Captures == Engine_UiInputCapture.Kind.Keyboard
				? "Press a key... (Esc cancels)"
				: "Press a button... (Esc cancels)";
		}

		string caption = line.Caption(index);
		return Conflicts(row, index) is { Length: > 0 } also ? caption + "  (also " + also + ")" : caption;
	}

	/// <summary>The other rows binding any input <paramref name="row"/> binds at <paramref name="index"/>, by label.</summary>
	private string Conflicts(int row, int index)
	{
		Line line = _lines[row];
		IReadOnlyList<string> bound = line.Bound(index);
		if (bound.Count == 0)
		{
			return string.Empty;
		}

		var others = new List<string>();
		for (int other = 0; other < _lines.Count && others.Count < 2; other++)
		{
			if (other == row || _lines[other].GetType() != line.GetType())
			{
				continue;
			}

			foreach (string input in _lines[other].Bound(PendingIndex(other)))
			{
				bool designed = line is KeyLine mine && _lines[other] is KeyLine theirs &&
					mine.DefaultsBind(input) && theirs.DefaultsBind(input);
				if (!designed && Contains(bound, input))
				{
					others.Add(_lines[other] is PadLine pad ? Engine_ControllerFamily.Label(StyleOf(pad.Player), pad.Game, Ps5Of(pad.Player)) : _lines[other].Label);
					break;
				}
			}
		}

		return string.Join(", ", others);
	}

	private static bool Contains(IReadOnlyList<string> list, string value)
	{
		foreach (string item in list)
		{
			if (item == value)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>A row's index as the open page holds it, or as committed for the list level's preview.</summary>
	private int PendingIndex(int row) =>
		Current == this ? Engine_TitleGame.Active.OptionsCursorIndex(row) : row < _committed.Length ? _committed[row] : 0;

	/// <summary>Rebuilds the rows and reads every one from refii.toml.</summary>
	public override int[] Read()
	{
		Build();
		IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);
		string ReadKey(string key) => values.TryGetValue(key, out string? value) ? value : string.Empty;
		_committed = new int[_lines.Count];
		for (int i = 0; i < _lines.Count; i++)
		{
			_committed[i] = _lines[i].IndexOfStored(ReadKey);
		}

		return (int[])_committed.Clone();
	}

	private static GlyphStyle StyleOf(int player) =>
		Engine_ControllerFamily.Resolve(Engine_UiKeyGlyphs.StyleSetting,
			Refii.Game.GameInput.FamilyOfPad(Refii.Game.GameInput.PadOfPlayer(player)));

	private static bool Ps5Of(int player) =>
		Refii.Game.GameInput.FamilyOfPad(Refii.Game.GameInput.PadOfPlayer(player)) == ControllerFamily.PlayStation5;

	/// <summary>The rows: controller, keyboard and mouse, then the mods'.</summary>
	private void Build()
	{
		ulong now = Godot.Time.GetTicksMsec();
		bool rescanned = false;
		if (!_packsScanned || now - _packsAt > PackScanMs)
		{
			List<string> found = Engine_UiGlyphPack.Available();
			rescanned = !_packsScanned || string.Join("\n", found) != string.Join("\n", _packs);
			_packs = found;
			_packsAt = now;
			_packsScanned = true;
		}

		IReadOnlyList<Refii.Mods.ModInputUi.Section> mods = Refii.Mods.ModInputUi.Sections();
		bool coop = Engine_TitleGame.Active.LocalCoop;
		if (!rescanned && _lines.Count != 0 && ReferenceEquals(mods, _builtMods) && coop == _builtCoop)
		{
			return;
		}

		_builtMods = mods;
		_builtCoop = coop;
		var lines = new List<Line>();
		void Add(string section, Line line)
		{
			line.Section = section;
			lines.Add(line);
		}

		Add("all", new ResetLine("Reset everything", "all"));
		Add("glyphs", new Header("Controller"));
		Add("glyphs", new ChoiceLine("Button prompts", "glyph_style", new[]
		{
			("auto", "Automatic (follow the controller)"),
			("xbox", "Xbox"),
			("playstation", "PlayStation"),
			("switch", "Switch"),
		}));
		var packs = new List<(string, string)> { (string.Empty, "Built-in") };
		foreach (string pack in _packs)
		{
			packs.Add((pack, pack));
		}

		Add("glyphs", new ChoiceLine("Glyph pack", "glyph_pack", packs));

		int players = coop ? 2 : 1;
		for (int player = 1; player <= players; player++)
		{
			string section = "pad" + player;
			int p = player;
			Add(section, new Header(players == 1 ? "Controller buttons" : $"Player {player} controller buttons"));
			Add(section, new ResetLine("Reset controller buttons", section));
			for (int i = 0; i < Engine_PadRemap.Count; i++)
			{
				var game = (PadInput)i;
				Add(section, new PadLine(player, game, () => StyleOf(p), () => Ps5Of(p), Engine_TitleGame.Active.PadButtonRole(game)));
			}
		}

		// The mouse's own settings, in game as in the launcher (the same refii.toml keys GameInput
		// reads): how far the camera turns, the horizontal invert, and the cursor kept in the window.
		Add("mouse", new Header("Mouse"));
		Add("mouse", new ChoiceLine("Mouse sensitivity", "mouse_sensitivity", new[]
		{
			("1.0", "1.0 (default)"),
			("0.25", "0.25"),
			("0.5", "0.5"),
			("0.75", "0.75"),
			("1.25", "1.25"),
			("1.5", "1.5"),
			("2.0", "2.0"),
			("3.0", "3.0"),
		}));
		Add("mouse", new ChoiceLine("Invert camera left and right", "camera_invert_x", new[]
		{
			("false", "Off"),
			("true", "On"),
		}));
		Add("mouse", new ChoiceLine("Keep the cursor in the window in play", "mouse_lock", new[]
		{
			("true", "On"),
			("false", "Off"),
		}));

		Add("keys", new Header("Keyboard and mouse"));
		Add("keys", new ResetLine("Reset keyboard and mouse", "keys"));
		IReadOnlyList<string> candidates = KeyCandidates();
		foreach (Refii.Game.GameInput.KeyAction action in Refii.Game.GameInput.KeyActions)
		{
			Add("keys", new KeyLine(action.Name, action.Defaults, candidates, action.IsDefault));
			if (action.PadBindable)
			{
				Add("keys", new PadActionLine(action.Name, () => StyleOf(1), () => Ps5Of(1)));
			}
		}

		foreach (Refii.Mods.ModInputUi.Section mod in mods)
		{
			string section = "mod:" + mod.Mod;
			Add(section, new Header(mod.Title));
			Add(section, new ResetLine("Reset " + mod.Title, section));
			foreach (Refii.Mods.ModInputUi.Row row in mod.Rows)
			{
				Add(section, new ModLine(row));
			}
		}

		_lines = lines;
	}

	private static IReadOnlyList<string>? _keyCandidates;

	/// <summary>The keys a key row steps through (the mods' list, by Godot's names) and the five mouse buttons.</summary>
	private static IReadOnlyList<string> KeyCandidates()
	{
		if (_keyCandidates is null)
		{
			var names = new List<string>();
			foreach (Godot.Key key in Refii.Mods.ModInputUi.KeyCandidates)
			{
				names.Add(Refii.Mods.ModInputUi.KeyName(key));
			}

			names.AddRange(new[] { "MouseLeft", "MouseRight", "MouseMiddle", "Mouse4", "Mouse5" });
			_keyCandidates = names;
		}

		return _keyCandidates;
	}

	/// <summary>
	/// A step on a row: onto "press a key" arms the capture; onto "reset" puts the section back to
	/// its defaults.
	/// </summary>
	public override void Changed(int row, int index)
	{
		if (row >= _lines.Count)
		{
			return;
		}

		Line line = _lines[row];
		if (line is ResetLine reset && index == 1)
		{
			for (int other = 0; other < _lines.Count; other++)
			{
				if (other != row && (reset.Target == "all" || _lines[other].Section == reset.Target) && _lines[other] is not ResetLine)
				{
					Engine_TitleGame.Active.SetOptionsCursorIndex(other, _lines[other].DefaultIndex);
				}
			}

			return;
		}

		if (line.Captures is { } kind && index == 0)
		{
			_captureRow = row;
			Engine_UiInputCapture.Begin(kind,
				name =>
				{
					_captureRow = -1;
					if (Current == this)
					{
						int at = line.IndexOfCaptured(name);
						Engine_TitleGame.Active.SetOptionsCursorIndex(row, at >= 0 ? at : line.DefaultIndex);
					}
				},
				() =>
				{
					_captureRow = -1;
					if (Current == this)
					{
						Engine_TitleGame.Active.SetOptionsCursorIndex(row, line.DefaultIndex);
					}
				});
		}
	}

	/// <summary>A: every row to refii.toml as one write, and the bindings in force at once.</summary>
	public override bool Commit(Func<int, int> index)
	{
		Engine_UiInputCapture.Cancel();
		var values = new Dictionary<string, string?>(StringComparer.Ordinal);
		for (int row = 0; row < _lines.Count; row++)
		{
			_lines[row].Store(index(row), values);
		}

		var write = new Dictionary<string, string>(StringComparer.Ordinal);
		var remove = new List<string>();
		foreach ((string key, string? value) in values)
		{
			if (value is null)
			{
				remove.Add(key);
			}
			else
			{
				write[key] = value;
			}
		}

		if (!Engine_UiScreenActions.WriteSettings(write, remove))
		{
			return false;
		}

		Refii.Game.GameInput.RereadControlSettings();
		return true;
	}
}
