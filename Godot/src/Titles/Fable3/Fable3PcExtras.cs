namespace Refii.Titles.Fable3;

/// <summary>
/// The functions the Fable III PC build puts on keys that the 360 scheme binds to nothing, as the
/// guest tick actions that run them on the 360 guest's own functions
/// (Fable3/src/hooks/src/pc_extras.cpp): the quicksave (the manual-save test, then the save) and
/// the safety toggle. Each names the local player it is for,
/// so either player's input can carry it. Pure, so the tests can run it.
/// </summary>
/// <remarks>
/// The PC's third, the lite pause (P), has no action: the 360 builds do not hold the function
/// behind it (pc_extras.cpp's header).
/// </remarks>
public static class Fable3PcExtras
{
	/// <summary>`pc-extra:quicksave:&lt;p&gt;`.</summary>
	public static string QuickSave(int player) => "pc-extra:quicksave:" + Fable3ActingPlayer.Field(player);

	/// <summary>`pc-extra:safety:&lt;p&gt;`.</summary>
	public static string Safety(int player) => "pc-extra:safety:" + Fable3ActingPlayer.Field(player);
}
