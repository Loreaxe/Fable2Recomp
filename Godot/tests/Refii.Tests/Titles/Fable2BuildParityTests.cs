namespace Refii.Tests.Titles;

using GdUnit4;

/// <summary>
/// TU1 AND THE UNPATCHED DISC BUILD STAY AT PARITY. Source names every guest address by its TU1
/// value and <c>refii::guest::Address</c> / <c>ToTu1</c> (C#: <c>GuestAddress</c> /
/// <c>GuestAddressToTu1</c>) maps it through Fable2/versions/Fable2/guest_addresses.inc; an
/// address with no row is returned unchanged, which is wrong memory on the disc build. Hooks bind
/// by NAME through the two per-build configs (Fable2/versions/Fable2TU1 is TU1, Fable2/versions/Fable2
/// the disc), so a name in one and not the other is a hook that silently does nothing on one build.
/// Read from the source tree only.
/// </summary>
[TestSuite]
public class Fable2BuildParityTests
{
	/// <summary>
	/// Literals in the guest's address range that are NOT TU1 guest addresses, each with the reason.
	/// A new one fails until it is mapped (a row) or listed here with its reason.
	/// </summary>
	private static readonly (string File, uint Value, string Why)[] NotAddresses =
	{
		("Fable2/src/hostbridge/src/host_bridge.cpp", 0x83000000u, "upper bound of a code-pointer sanity range"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A7A000u, "TU1 trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A7C000u, "TU1 trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82B6D000u, "TU1 trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82B6E800u, "TU1 trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A6DA58u, "disc trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A6EF30u, "disc trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A6C0C0u, "disc trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A6C5ACu, "disc trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A676C0u, "disc trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A67D18u, "disc trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82B67608u, "disc trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82B68BB0u, "disc trace span bound"),
		("Godot/src/Engine/equipment/items/Engine_RecordNameCatalog.cs", 0x82DF620Fu, "record name hash"),
		("Godot/src/Engine/equipment/items/Engine_RecordNameCatalog.cs", 0x835C36C9u, "record name hash"),
		("Godot/src/Engine/level/environment/Engine_ToneMapTheme.cs", 0x82D8E57Au, "theme name hash"),
		("Godot/src/Engine/entities/Engine_EntityGameplay.cs", 0x83632C03u, "property name hash"),
		("Godot/src/Engine/entities/Engine_EntityGameplay.cs", 0x820BD770u, "property name hash"),
	};

	/// <summary>
	/// Names bound in one build's config only, each proven from the binaries. The TU1 functions are
	/// declared weak in source, so the disc payload still links.
	/// </summary>
	private static readonly (string Name, bool NamedOnly, string Why)[] OneBuildNames =
	{
		("rex_lhCoopText_DonationRecipient", true,
			"episodic DLC text; the disc executable contains no EPISODIC string at all"),
		("rex_lhGameInit_RegisterBankProvider", true,
			"TU1 split it out of GameInit; no counterpart body in the disc .text, nothing binds it"),
		("rex_lhGuiRegistry_SetImage", true,
			"inlined into the disc epilogue sub_824F23A0; the disc states it via RefiiGuiImageInlined"),
		("RefiiGuiImageInlined", false,
			"the disc's counterpart of the TU1 rex_lhGuiRegistry_SetImage hook"),
		("rex_lhGuiNextEpisodeScreen_Update", true,
			"screen 13, the Next Episode advert: TU1 only — the disc has no TurnOnNextEpisode, NextEpisode.bgf or GUI_SCREEN_EPISODE_BUY_* string"),
		("rex_lhGuiNextEpisodeScreen_HandleAction", true,
			"screen 13, the Next Episode advert: TU1 only (see its Update)"),
		("rex_lhGuiNextEpisodeScreen_Close", true,
			"screen 13, the Next Episode advert: TU1 only (see its Update)"),
	};

	private static readonly string[] Native = { ".cpp", ".h", ".hpp", ".cc", ".c", ".inc" };

	private static readonly TitleBuildParity Spec = new()
	{
		Table = "Fable2/versions/Fable2/guest_addresses.inc",
		NamedConfig = "Fable2/versions/Fable2TU1/refii_config.toml",
		DiscConfig = "Fable2/versions/Fable2/refii_config.toml",
		NamedBuild = "TU1",
		HookRoot = "Fable2/src/",
		Sources = new (string, string[])[]
		{
			("Fable2/src", Native),
			("src/runtime", Native),
			("src/host", Native),
			("src/hostcore", Native),
			("src/payload", Native),
			("src/abi", Native),
			("src/achievement", Native),
			("rexglue-net/shadow/rexglue-sdk", Native),
			("Godot/src", new[] { ".cs" }),
		},
		Excluded = new[] { "/Fable3/" },
		NotAddresses = NotAddresses,
		OneBuildNames = OneBuildNames,
		MinimumRows = 501,
		MinimumNames = 2001,
		SameRegisterNames = true,
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
