using System;
using System.Collections.Generic;
using System.Globalization;
using Refii.Launcher.Core;
using Refii.Text;

namespace Refii.Ui;

/// <summary>
/// THE DISPLAY ROWS IN GAME — the launcher's own Display settings, ONE TABLE: the same keys, read and
/// written by the launcher's own model (<see cref="RuntimeSettingsModel"/>, which saves both titles'
/// config files), so a change here is the launcher's change and the launcher shows it. The rows: the
/// window mode (the launcher's composite of <c>fullscreen</c> and <c>borderless</c>,
/// <see cref="RuntimeSettingsModel.GetWindowMode"/>), the resolution (<c>display_resolution</c>, the
/// launcher's sizes for the chosen aspect, <see cref="RuntimeSettingsModel.OptionsFor"/>), vertical
/// sync (<c>vsync_mode</c>, its definition's choices) and the frame rate limit (<c>max_fps</c>, the
/// launcher's 0..240 by 5, 0 uncapped). A writes them and the open window takes them at once
/// (<see cref="Refii.Game.GameWindow.ApplyDisplaySettings"/>); B leaves them. Fable 3 shows them
/// under its Game Settings' Display tab (the PC layout) and in its settings hub.
/// </summary>
public sealed class Engine_UiDisplayOptions : Engine_UiHostOptionsPage
{
	public const string OpenCommand = "options:display";

	public static Engine_UiDisplayOptions Page { get; } = new();

	public override string Name => "Display";

	public override string Title => Engine_OwnWords.Say("REFII_DISPLAY");

	/// <summary>The window modes, as the launcher's composite names them.</summary>
	public static readonly string[] WindowModes = { "windowed", "borderless", "fullscreen" };

	/// <summary>The rows: window mode, resolution, vertical sync, frame rate limit.</summary>
	public const int RowCount = 4;

	/// <summary>The launcher's frame-limit range: 0 (uncapped) to 240 by 5.</summary>
	public const int FrameLimitTop = 240;

	public const int FrameLimitStep = 5;

	/// <summary>One row: its label (our words), its choices (the stored value, the caption), the stored value's index.</summary>
	public sealed record Row(string LabelKey, string Key, IReadOnlyList<(string Value, string Caption)> Choices, int Index);

	private readonly List<Row> _rows = new();
	private RuntimeSettingsModel? _model;

	/// <summary>
	/// The four rows from the launcher's values (pure: the resolution and vsync choices are handed in,
	/// as the launcher answers them). A stored value the list does not offer is kept as its last choice.
	/// </summary>
	public static List<Row> Build(
		IReadOnlyDictionary<string, string> values,
		IReadOnlyList<(string Value, string Caption)> resolutions,
		IReadOnlyList<(string Value, string Caption)> vsyncs)
	{
		string Value(string key, string fallback) => values.TryGetValue(key, out string? v) && v.Trim().Trim('"').Length > 0
			? v.Trim().Trim('"')
			: fallback;
		bool On(string key) => RuntimeConfig.NormalizeBoolValue(Value(key, "false")) == "true";

		Row Choice(string label, string key, IReadOnlyList<(string Value, string Caption)> choices, string stored)
		{
			var list = new List<(string Value, string Caption)>(choices);
			int index = list.FindIndex(choice => string.Equals(choice.Value, stored, StringComparison.OrdinalIgnoreCase));
			if (index < 0)
			{
				list.Add((stored, stored));
				index = list.Count - 1;
			}

			return new Row(label, key, list, index);
		}

		string mode = On("fullscreen") ? "fullscreen" : On("borderless") ? "borderless" : "windowed";
		var modes = new List<(string, string)>
		{
			("windowed", Engine_OwnWords.Say("REFII_DISPLAY_WINDOWED")),
			("borderless", Engine_OwnWords.Say("REFII_DISPLAY_BORDERLESS")),
			("fullscreen", Engine_OwnWords.Say("REFII_DISPLAY_FULLSCREEN")),
		};
		var limits = new List<(string, string)>();
		for (int fps = 0; fps <= FrameLimitTop; fps += FrameLimitStep)
		{
			limits.Add((fps.ToString(CultureInfo.InvariantCulture),
				fps == 0 ? Engine_OwnWords.Say("REFII_DISPLAY_UNCAPPED") : fps.ToString(CultureInfo.InvariantCulture) + " FPS"));
		}

		return new List<Row>
		{
			Choice("REFII_DISPLAY_WINDOW_MODE", "window_mode", modes, mode),
			Choice("REFII_DISPLAY_RESOLUTION", "display_resolution", resolutions, Value("display_resolution", "1280x720")),
			Choice("REFII_DISPLAY_VSYNC", "vsync_mode", vsyncs, Value("vsync_mode", "mailbox").ToLowerInvariant()),
			Choice("REFII_DISPLAY_FRAME_LIMIT", "max_fps", limits, Value("max_fps", "0")),
		};
	}

	/// <summary>The rows' chosen <paramref name="index"/> into the launcher's values (the window mode as its two keys).</summary>
	public static void Store(IReadOnlyList<Row> rows, Func<int, int> index, IDictionary<string, string> values)
	{
		for (int row = 0; row < rows.Count; row++)
		{
			IReadOnlyList<(string Value, string Caption)> choices = rows[row].Choices;
			string chosen = choices[Math.Clamp(index(row), 0, choices.Count - 1)].Value;
			if (rows[row].Key == "window_mode")
			{
				values["fullscreen"] = chosen == "fullscreen" ? "true" : "false";
				values["borderless"] = chosen == "borderless" ? "true" : "false";
			}
			else
			{
				values[rows[row].Key] = chosen;
			}
		}
	}

	/// <summary>The launcher's own caption for a vsync value: our words for the four it offers.</summary>
	private static string VsyncCaption(RuntimeCVarOption option) => option.Value.ToLowerInvariant() switch
	{
		"mailbox" => Engine_OwnWords.Say("REFII_DISPLAY_VSYNC_MAILBOX"),
		"on" => Engine_OwnWords.Say("REFII_DISPLAY_ON"),
		"adaptive" => Engine_OwnWords.Say("REFII_DISPLAY_VSYNC_ADAPTIVE"),
		"off" => Engine_OwnWords.Say("REFII_DISPLAY_OFF"),
		_ => option.Label,
	};

	private static RuntimeCVarDefinition? Definition(string name)
	{
		foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.Active)
		{
			if (definition.Name == name)
			{
				return definition;
			}
		}

		return null;
	}

	public override int Count
	{
		get
		{
			if (_rows.Count == 0)
			{
				Read();
			}

			return _rows.Count;
		}
	}

	public override string RowTitle(int row) => row >= 0 && row < _rows.Count ? Engine_OwnWords.Say(_rows[row].LabelKey) : string.Empty;

	public override int Choices(int row) => row >= 0 && row < _rows.Count ? _rows[row].Choices.Count : 1;

	public override string Caption(int row, int index) =>
		row >= 0 && row < _rows.Count && index >= 0 && index < _rows[row].Choices.Count ? _rows[row].Choices[index].Caption : string.Empty;

	/// <summary>Reads the launcher's settings afresh (its model, both titles' files) and answers each row's stored index.</summary>
	public override int[] Read()
	{
		_model = new RuntimeSettingsModel();
		_model.Initialize();
		IReadOnlyDictionary<string, string> values = _model.Values;
		var resolutions = new List<(string, string)>();
		if (Definition("display_resolution") is { } resolution)
		{
			foreach (RuntimeCVarOption option in RuntimeSettingsModel.OptionsFor(resolution, values))
			{
				resolutions.Add((option.Value, option.Value == "native" ? Engine_OwnWords.Say("REFII_DISPLAY_MATCH_WINDOW") : option.Label));
			}
		}

		var vsyncs = new List<(string, string)>();
		if (Definition("vsync_mode") is { } vsync)
		{
			foreach (RuntimeCVarOption option in RuntimeSettingsModel.OptionsFor(vsync, values))
			{
				vsyncs.Add((option.Value.ToLowerInvariant(), VsyncCaption(option)));
			}
		}

		_rows.Clear();
		_rows.AddRange(Build(values, resolutions, vsyncs));
		var indices = new int[_rows.Count];
		for (int row = 0; row < indices.Length; row++)
		{
			indices[row] = _rows[row].Index;
		}

		return indices;
	}

	/// <summary>Whether <paramref name="index"/> differs from what the launcher's files hold.</summary>
	public bool Differs(Func<int, int> index)
	{
		for (int row = 0; row < _rows.Count; row++)
		{
			if (index(row) != _rows[row].Index)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>A: the rows into the launcher's model and saved by it, then the window takes them.</summary>
	public override bool Commit(Func<int, int> index)
	{
		if (_model is null)
		{
			Read();
		}

		if (_model is null || !Differs(index))
		{
			return true;
		}

		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		Store(_rows, index, values);
		foreach ((string key, string value) in values)
		{
			_model.Values[key] = value;
		}

		_model.Save();
		Godot.GD.Print($"[display-options] {_model.StatusMessage}: {string.Join(", ", values)}");
		Read();
		Refii.Game.GameWindow.ApplyDisplaySettings();
		return true;
	}
}
