namespace Refii.Ui;

/// <summary>
/// WHICH DETAIL MAP A GUI QUAD TAKES — one rule for both titles.
/// </summary>
/// <remarks>
/// <para>THE RECORD. A `.fac` UV record's mode field (<see cref="FacUvRecord.DetailMapMode"/>) is
/// read and tested against 4 alone: Fable 2 TU1 `8286E4B0 cmpwi cr6,r5,4`, Fable 3 TU3
/// `82B7804C cmpwi cr6,r5,4`, Fable 3 disc `82B42E2C cmpwi cr6,r5,4`; 4 sets the quad's
/// detail flag (`ori r9,r11,4; stw r9,0x10(r29)`, TU3 82B78060 / disc 82B42E40). The next field,
/// the slot (<see cref="FacUvRecord.SecondaryTexture"/>), goes to the theme pick: Fable 2 TU1
/// `8286E4EC bl sub_8286F578`, Fable 3 TU3 `82B78088 bl sub_82B79808`, disc
/// `82B42E68 bl sub_82B445E8`, which writes the detail texture's hash to the quad's +0x20C.</para>
/// <para>THE PICK. Slot 1 and 2 read words 1 and 2 of the theme table's row, any other slot but 3
/// word 0; slot 3 is `Art\GUI\Persistant\frame_tiled_background.tex` by name. The row is
/// `[[gui+0x30]+0x24]` (TU3 `82B79840 lwz r9,0x30(r11); 82B79844 lwz r11,0x24(r9)`, disc
/// 82B44620 / 82B44624), zeroed with the table (TU3 `825ED6EC stw r31,0x24(r30)` before
/// `825ED6F0 bl sub_825EDA28`, disc 825BF904 / 825BF908). The table is built with the same nine
/// names in the same order on both titles (Fable 2 TU1 sub_826DB1D8, Fable 3 TU3 sub_825EDA28,
/// disc sub_825BFC40): row 0 FrameDetailMap, ButtonDetailMap, frames_01.</para>
/// <para>THE ART. Fable 3's banks carry all four at the same paths as Fable 2's
/// (`art\gui\frames\framedetailmap.tex`, `buttondetailmap.tex`, `frames_01.tex`,
/// `art\gui\persistant\frame_tiled_background.tex` in gui_textures.bnk), so the rule needs no
/// title. Fable 3's own documents never ask for one: every UV record of its 33 360 `.fac` files has
/// mode 0.</para>
/// </remarks>
public static class Engine_UiDetailMaps
{
	/// <summary>The only mode value that asks for a detail map.</summary>
	public const int DetailMapped = 4;

	/// <summary>Whether a record's mode field asks for a detail map.</summary>
	public static bool Asks(int mode) => mode == DetailMapped;

	/// <summary>The detail map a slot names, from the shipped theme row 0.</summary>
	public static string PathFor(int slot) => slot switch
	{
		1 => @"art\gui\frames\buttondetailmap.tex",
		2 => @"art\gui\frames\frames_01.tex",
		3 => @"art\gui\persistant\frame_tiled_background.tex",
		_ => @"art\gui\frames\framedetailmap.tex",
	};
}
