namespace Refii.Titles.Fable3;

using System;
using System.Collections.Generic;
using Refii.Ui;

/// <summary>
/// THE ROWS AN INTERACTION MODE'S POPULATE PUSHED — the real-estate mode's (modes 6 and 7) and the
/// crime mode's (9) — for the interaction list Godot draws, and the lever that asks a real-estate row
/// by its id.
/// </summary>
/// <remarks>
/// <para>WHAT THE GUEST STATES (real_estate_mode.cpp). Both populates push each row {tag, icon, id}
/// through one push (TU3 0x82B5E868, disc 0x82B2B4C0), stated as the populate's own:
/// `@RealEstate.Row(mode, id, tag, icon)` (populate TU3 0x82B56120, disc 0x82B19D28) and
/// `@Crime.Row(…)` (TU3 0x82B68080, disc 0x82B2ADC0), then `@RealEstate.Listed(mode)` /
/// `@Crime.Listed(mode)` once it returned. The real-estate populate never clears — its relist does
/// (`@RealEstate.Rows.Clear`, TU3 0x82B58BC0, disc 0x82B1C7E0) and its close ends them
/// (`@RealEstate.Close`); the crime populate empties its vector first (`@Crime.Rows.Clear`). The row
/// writer that follows a populate draws the same rows into the list the guest's cursor walks
/// (`@Interaction.List`), so row i here is row i of that cursor.</para>
/// <para>THE LEVER `realestate:&lt;p&gt;:row:&lt;id&gt;` runs the interaction manager's input with A for
/// local player p and the row handler with this id in place of the cursor's: the manager's, the
/// mode's and the player's gates are the guest's. The crime list has no such lever: a click walks
/// the guest's cursor and presses A, as on any list. Labels are the tags in the game's book, as
/// each mode's own label slot localises them (`sub_823CBB30`).</para>
/// </remarks>
public sealed class Fable3PopulatedRows
{
	public readonly record struct Row(int Id, string Tag, string Icon);

	public static readonly Fable3PopulatedRows RealEstate = new("@RealEstate", "realestate:");
	public static readonly Fable3PopulatedRows Crime = new("@Crime", null);

	private readonly string _prefix;
	private readonly string? _lever;
	private readonly List<Row> _rows = new();
	private uint _mode;
	private bool _listed;
	private bool _listWas;

	private Fable3PopulatedRows(string prefix, string? lever)
	{
		_prefix = prefix;
		_lever = lever;
	}

	/// <summary>Bumped on every statement of either model.</summary>
	public static ulong Generation { get; private set; }

	public IReadOnlyList<Row> Rows => _rows;

	public uint Mode => _mode;

	/// <summary>Whether a click asks a row by its id (the real-estate mode) rather than walking the cursor.</summary>
	public bool AsksById => _lever is not null;

	/// <summary>
	/// Whether the list up is this mode's: its populate stated its rows since the list last went
	/// down (<see cref="Observe"/>), the mode is not closed, and the guest is writing a list.
	/// </summary>
	public bool Listing => _listed && Fable3UiStatements.InteractionListUp && _rows.Count > 0;

	/// <summary>The model whose rows are the list up, if any.</summary>
	public static Fable3PopulatedRows? Up => RealEstate.Listing ? RealEstate : Crime.Listing ? Crime : null;

	/// <summary>Once a frame, whatever shows: the list going down ends each mode's claim on it.</summary>
	public static void Observe(bool listUp)
	{
		RealEstate.ObserveOne(listUp);
		Crime.ObserveOne(listUp);
	}

	private void ObserveOne(bool listUp)
	{
		if (_listWas && !listUp)
		{
			_listed = false;
		}

		_listWas = listUp;
	}

	/// <summary>`realestate:&lt;p&gt;:row:&lt;id&gt;`; null for a model without a lever.</summary>
	public string? Lever(int player, int id) => _lever is null ? null : _lever + Fable3ActingPlayer.Field(player) + ":row:" + id;

	/// <summary>The row's label: its tag in <paramref name="book"/>, the tag itself when the book has none.</summary>
	public static string Label(Row row, Func<string, string?> book) => book(row.Tag) is { Length: > 0 } text ? text : row.Tag;

	/// <summary>The row at the guest's cursor plus <paramref name="offset"/>, when there is one.</summary>
	public Row? At(int cursor, int offset)
	{
		int index = cursor + offset;
		return index >= 0 && index < _rows.Count ? _rows[index] : null;
	}

	public static void Clear()
	{
		RealEstate.ClearOne();
		Crime.ClearOne();
		Generation++;
	}

	private void ClearOne()
	{
		_rows.Clear();
		_mode = 0u;
		_listed = false;
		_listWas = false;
	}

	public static void Register(Engine_UiGuiNatives router)
	{
		foreach (Fable3PopulatedRows model in new[] { RealEstate, Crime })
		{
			foreach (string suffix in Suffixes)
			{
				router.Register(model._prefix + suffix, call => model.Take(call));
			}
		}
	}

	private static readonly string[] Suffixes = { ".Row", ".Listed", ".Rows.Clear", ".Close" };

	/// <summary>Takes one of either model's statements (the router's and the tests' entry); false when it is neither's.</summary>
	public static bool TakeAny(Engine_UiModel.GuiNativeCall call) => RealEstate.Take(call) || Crime.Take(call);

	/// <summary>Takes one statement; false when it is not this model's.</summary>
	public bool Take(Engine_UiModel.GuiNativeCall call)
	{
		if (!call.Name.StartsWith(_prefix + ".", StringComparison.Ordinal))
		{
			return false;
		}

		uint mode = Fable3Elements.Parse(call.Argument(0).Text);
		switch (call.Name[_prefix.Length..])
		{
			case ".Row":
				if (mode != _mode)
				{
					_rows.Clear();
					_mode = mode;
					_listed = false;
				}

				_rows.Add(new Row((int)call.Argument(1).Number, call.Argument(2).Text, call.Argument(3).Text));
				break;
			case ".Listed":
				if (mode != _mode)
				{
					_rows.Clear();
					_mode = mode;
				}

				_listed = true;
				Fable3Elements.Log($"[fable3-rows] {_prefix} mode {mode:X8} lists {_rows.Count} rows");
				break;
			case ".Rows.Clear":
				if (mode == _mode)
				{
					_rows.Clear();
					_listed = false;
				}

				break;
			case ".Close":
				if (mode == _mode)
				{
					_rows.Clear();
					_listed = false;
					_mode = 0u;
				}

				break;
			default:
				return false;
		}

		Generation++;
		return true;
	}
}
