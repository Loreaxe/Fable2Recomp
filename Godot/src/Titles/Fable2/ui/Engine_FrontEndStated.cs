namespace Refii.Titles.Fable2;

using System.Collections.Generic;
using System.Globalization;
using Refii.Ui;

/// <summary>
/// What the front end's own entries STATE: the load fan's cards from StampSaveCard (TU1
/// `sub_826E1408`, disc `sub_826C9828`) and the dialog from OpenDialog (TU1 `sub_826E40E0`, disc
/// `sub_826CB158`) (gui_headless_presentation.cpp), and the enumerated saves from the card text
/// builder (TU1 `sub_826E14F8`, disc `sub_826C9918`; frontend_stated.cpp). Each statement carries
/// a sequence, so a reader takes only what was stated after a point it names.
/// </summary>
public sealed class Engine_FrontEndStated
{
	/// <summary>
	/// One save's card: <paramref name="Slot"/> is the card it is stamped on, 1-based (r6 = index +
	/// root+0x4C, TU1 0x826e0744 / disc root+0x40, 0x826c8b74); <paramref name="Texture"/> the
	/// resource hash the card carries — the save's screenshot, else the blank `dword_8349B600` every
	/// card was given first (0x826e06ac..0x826e070c).
	/// </summary>
	public readonly record struct Card(int Slot, uint Texture, ulong Sequence);

	/// <summary>
	/// The dialog as OpenDialog was called: r3 the owner, r4 the OK text, r5 the body, r6
	/// allow-cancel; with it the cancel caption's tag `GUI_BUTTONS_CANCEL` and the Y glyph
	/// `abyx.tex` (`dword_8349B694`), without it an empty caption and `blank.tex`
	/// (`dword_8349B690`) — the guest's own pick (0x826e41d4).
	/// </summary>
	public readonly record struct Dialog(
		uint Owner, string Ok, string Body, bool AllowCancel, string CancelTag, uint Glyph, ulong Sequence);

	/// <summary>
	/// One enumerated save, in the guest's own order (the index `LoadSave` commits by):
	/// <paramref name="File"/> the file name the builder handed its header reader (`sub_82447160`
	/// r5), <paramref name="Text"/> the finished, localised card text it pushed (`sub_8243BE88`) —
	/// display name, level and save context, or the guest's version / corrupt-save message.
	/// </summary>
	public readonly record struct Save(string File, string Text);

	/// <summary>The last statement's sequence, 0 before any.</summary>
	public ulong Sequence { get; private set; }

	/// <summary>The last dialog stated, or null.</summary>
	public Dialog? LastDialog { get; private set; }

	private readonly Dictionary<int, Card> _cards = new();
	private readonly List<Save> _building = new();
	private List<Save> _saves = new();
	private ulong _savesSequence;

	/// <summary>`@FrontEnd.SaveText`: save index, file name, card text. Index 0 starts a list.</summary>
	public void SaveText(Engine_UiModel.GuiNativeCall call)
	{
		if (!int.TryParse(call.Argument(0).Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int save))
		{
			return;
		}

		if (save == 0)
		{
			_building.Clear();
		}

		_building.Add(new Save(call.Argument(1).Text ?? string.Empty, call.Argument(2).Text ?? string.Empty));
	}

	/// <summary>`@FrontEnd.SaveList`: the builder returned, having stated this many saves.</summary>
	public void SaveList(Engine_UiModel.GuiNativeCall call)
	{
		_saves = new List<Save>(_building);
		_building.Clear();
		_savesSequence = ++Sequence;
	}

	/// <summary>The saves of the last list stated after <paramref name="since"/>; null until one is.</summary>
	public List<Save>? SavesSince(ulong since) => _savesSequence > since ? _saves : null;

	/// <summary>`@FrontEnd.SaveCard`: save index, card slot, texture hash (hex).</summary>
	public void SaveCard(Engine_UiModel.GuiNativeCall call)
	{
		if (int.TryParse(call.Argument(0).Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int save) &&
			int.TryParse(call.Argument(1).Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot) &&
			Hex(call.Argument(2).Text) is { } texture)
		{
			_cards[save] = new Card(slot, texture, ++Sequence);
		}
	}

	/// <summary>`@FrontEnd.Dialog`: owner (hex), OK, body, allow-cancel, cancel tag, glyph hash (hex).</summary>
	public void OpenDialog(Engine_UiModel.GuiNativeCall call)
	{
		if (Hex(call.Argument(0).Text) is not { } owner)
		{
			return;
		}

		LastDialog = new Dialog(owner, call.Argument(1).Text ?? string.Empty, call.Argument(2).Text ?? string.Empty,
			call.Argument(3).Text == "1", call.Argument(4).Text ?? string.Empty,
			Hex(call.Argument(5).Text) ?? 0u, ++Sequence);
	}

	/// <summary>
	/// The cards of saves 0 .. <paramref name="count"/> - 1, each stated after
	/// <paramref name="since"/>; null until all of them are.
	/// </summary>
	public List<Card>? CardsSince(ulong since, int count)
	{
		var cards = new List<Card>(count);
		for (int save = 0; save < count; ++save)
		{
			if (!_cards.TryGetValue(save, out Card card) || card.Sequence <= since)
			{
				return null;
			}

			cards.Add(card);
		}

		return cards;
	}

	private static uint? Hex(string? text) =>
		uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value) ? value : null;
}
