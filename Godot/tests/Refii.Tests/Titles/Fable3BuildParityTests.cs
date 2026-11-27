namespace Refii.Tests.Titles;

using GdUnit4;

/// <summary>
/// TU3 AND THE DISC BUILD OF FABLE 3 STAY AT PARITY. Source names every guest address by its TU3
/// value and <c>refii::guest::Address</c> / <c>ToTu3</c> (Fable3/src/vfs/include/fable3_guest_build.h;
/// C#: <c>GuestAddress</c> / <c>GuestAddressToTu1</c>, the shell ABI's name for the named build) maps it
/// through Fable3/versions/Fable3/guest_addresses.inc. Hooks bind by NAME through the two per-build
/// configs (Fable3/versions/Fable3TU3 is TU3, Fable3/versions/Fable3 the disc). Read from the source
/// tree only.
/// </summary>
[TestSuite]
public class Fable3BuildParityTests
{
	/// <summary>Literals in the guest's address range that are not TU3 addresses with a disc row.</summary>
	private static readonly (string File, uint Value, string Why)[] NotAddresses =
	{
		("Fable3/src/hooks/src/display_box_hooks.cpp", 0x820B9A54u,
			"TU3-only vtable (DisplayNonInteractiveMessageBox, stored at 0x82545FF8); the disc has no such class and tests it only on TU3"),
	};

	/// <summary>Names bound in one build's config only, each proven from the binaries; the functions are weak in source.</summary>
	private static readonly (string Name, bool NamedOnly, string Why)[] OneBuildNames =
	{
		("rex_Splash_Accept", true,
			"TU3 moved the START accept out of the input into sub_82319C38, also called by the Startup update (0x82319788); the disc's is inline"),
		("RefiiF3TopBoxAnswered", true,
			"TU3's TopBox press answer posts through the generic sub_824D1BC0 (0x824D1BA0); the disc has rex_TopBox_Answer"),
		("rex_TopBox_Answer", false,
			"disc sub_824B70E0(M, answer): TU3 has no counterpart (its callers call sub_824D1BC0 instead)"),
		("RefiiF3BotFlushBlocks", false,
			"disc 0x821AD4EC: TU3 already takes the blocking enter at these four sites (its own `bne loc_82460688`, 0x82460620)"),
		("RefiiF3BotFlushLocked", false,
			"disc 0x821AD540, the proof line of the forced flush; TU3 runs Lionhead's own code there"),
		("RefiiF3PathfinderUnqueued", false,
			"disc 0x8238FB94: TU3's worker has the live-queue test itself (0x823AE3A8..0x823AE3D8)"),
	};

	private static readonly string[] Native = { ".cpp", ".h", ".hpp", ".cc", ".c", ".inc" };

	private static readonly TitleBuildParity Spec = new()
	{
		Table = "Fable3/versions/Fable3/guest_addresses.inc",
		NamedConfig = "Fable3/versions/Fable3TU3/refiii_config.toml",
		DiscConfig = "Fable3/versions/Fable3/refiii_config.toml",
		NamedBuild = "TU3",
		HookRoot = "Fable3/src/",
		Sources = new (string, string[])[]
		{
			("Fable3/src", Native),
			("Godot/src/Titles/Fable3", new[] { ".cs" }),
		},
		Excluded = new[] { "/tests/" },
		NotAddresses = NotAddresses,
		OneBuildNames = OneBuildNames,
		MinimumRows = 150,
		MinimumNames = 240,
		// TU3 keeps two hooks' values in other registers (0x82980214 r25, 0x829B7930 r26): the same
		// number, so the same C++ signature.
		SameRegisterNames = false,
	};

	[TestCase]
	public void AddressTableIsSortedAndOneToOne() => Spec.AddressTableIsSortedAndOneToOne();

	[TestCase]
	public void EveryGuestAddressLiteralHasARow() => Spec.EveryGuestAddressLiteralHasARow();

	[TestCase]
	public void BothConfigsBindTheSameNames() => Spec.BothConfigsBindTheSameNames();

	[TestCase]
	public void EveryHookedNameBindsOnBothBuilds() => Spec.EveryHookedNameBindsOnBothBuilds();
}
