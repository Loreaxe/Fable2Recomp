using System;
using System.Collections.Generic;

namespace Refii.Core;

/// <summary>
/// Which game the app is installing, building and running, and the values that differ by it:
/// the title's folder, which <c>versions/</c> folder the generator reads, what its compiled
/// module is called, and whether hard mods can build for it.
/// </summary>
/// <remarks>
/// ONE VALUE, READ BY EVERYONE. The launcher sets <see cref="Active"/> from its switch and hands it
/// to the game process in <see cref="EnvironmentVariable"/>, which this type reads at start, so the
/// launcher, the game-code build and the running game cannot disagree about the title.
///
/// EVERY TITLE IS ITS OWN FOLDER, the same shape for each: <c>assets/&lt;Folder&gt;/</c> holds the
/// install and everything derived from it (<c>game</c>, <c>update</c>, <c>unpacked</c>,
/// <c>generated</c>, <c>mods</c>, <c>metadata</c>), and <c>bin/&lt;Folder&gt;/</c> holds
/// the title's ReXGlue instance — its host bridge, vfs, Lua runtime and compiled module — beside
/// the shared natives in <c>bin/</c>. The native runtime derives the leaves from the game data root
/// the host states, so it needs no title knowledge of its own.
///
/// THE KEY IS THE PROJECT'S NAME FOR THE TITLE: re + Fable II is refii, re + Fable III is refiii.
/// </remarks>
public sealed class Engine_Title
{
	/// <summary>Set by the launcher on the game process: the <see cref="Key"/> of the title to run.</summary>
	public const string EnvironmentVariable = "REFII_TITLE";

	/// <summary>
	/// Set by the launcher on the game process: the compiled module's base name, which the native
	/// payload loader opens beside the title's host bridge instead of <c>refii_generated</c>.
	/// </summary>
	public const string PayloadEnvironmentVariable = "REFII_PAYLOAD_MODULE";

	/// <summary>
	/// The libraries that can make up a title's ReXGlue instance. Each title ships its own in
	/// <c>bin/&lt;Folder&gt;/</c>, so the resolver looks for these there and nowhere else; which of
	/// them a title has is its <see cref="Instance"/>.
	/// </summary>
	public static IReadOnlySet<string> InstanceLibraries { get; } =
		new HashSet<string>(StringComparer.Ordinal) { "refii_hostbridge", "refii_vfs", "refii_lua" };

	private Engine_Title(
		string key, string name, string remakeName, uint titleId, string folder, string moduleName,
		string discVersion, string updatedVersion, bool hasHardMods, string modExtension,
		uint editorFolderColour, bool hashedModelNodes, params string[] instance)
	{
		Instance = new HashSet<string>(instance, StringComparer.Ordinal);
		Key = key;
		Name = name;
		RemakeName = remakeName;
		TitleId = titleId;
		Folder = folder;
		ModuleName = moduleName;
		ModExtension = modExtension;
		EditorFolderColour = editorFolderColour;
		_discVersion = discVersion;
		_updatedVersion = updatedVersion;
		HasHardMods = hasHardMods;
		HashedModelNodes = hashedModelNodes;
	}

	private readonly string _discVersion;
	private readonly string _updatedVersion;

	/// <summary>The stable id the launcher stores and the game process is given.</summary>
	public string Key { get; }

	/// <summary>The game: "Fable 2", for every message about the player's disc and install.</summary>
	public string Name { get; }

	/// <summary>What we call our version of it: "Fable 2 Return to Albion".</summary>
	public string RemakeName { get; }

	/// <summary>The title id the executable's execution info carries.</summary>
	public uint TitleId { get; }

	/// <summary>The title's folder under <c>assets/</c> and <c>bin/</c>, and in the source tree.</summary>
	public string Folder { get; }

	/// <summary>The compiled module's base name, before the platform's prefix and extension.</summary>
	public string ModuleName { get; }

	/// <summary>
	/// The title's mod package extension, dot included: <c>.f2mod</c>, <c>.f3mod</c>. Every title's
	/// package is the same format; the extension says which game it is for.
	/// </summary>
	public string ModExtension { get; }

	/// <summary>The title's folder colour in the editor's game tree, as 0xRRGGBBAA.</summary>
	public uint EditorFolderColour { get; }

	/// <summary>Whether a path names a mod package of any title.</summary>
	public static bool IsModPackagePath(string path)
	{
		foreach (Engine_Title title in All)
		{
			if (path.EndsWith(title.ModExtension, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Whether hard mods can build for this title: native mod sources are written against Fable 2's
	/// hook seams, so a title without them takes none. Every title compiles its own hooks
	/// (<c>bin/recompile/&lt;Folder&gt;/hook_sources.cmake</c>) whether or not it takes hard mods.
	/// </summary>
	public bool HasHardMods { get; }

	/// <summary>
	/// Whether the title's model files name a skeleton node only by the hash of its name (the
	/// hashed <c>.mdl</c>/<c>.mdl.gmd</c> layout), so its Godot bones are named "0x%08X".
	/// </summary>
	public bool HashedModelNodes { get; }

	/// <summary>How the title's Gameface text element lays out: box canvas, font buckets, line advance.</summary>
	public Refii.Ui.Engine_UiTextContract UiText { get; private init; } = Refii.Ui.Engine_UiTextContract.Fable2;

	/// <summary>The title's host-only menu labels as a phone says them (Refii.AndroidRuntime.AndroidMenuWords).</summary>
	public IReadOnlyDictionary<string, string> PhoneHostLabels { get; private init; } =
		Refii.AndroidRuntime.AndroidMenuWords.None;

	/// <summary>
	/// The instance libraries this title ships in <c>bin/&lt;Folder&gt;/</c>. Every title has its
	/// host bridge; the vfs and Lua runtime are Fable 2's hooks and the code they call.
	/// </summary>
	public IReadOnlySet<string> Instance { get; }

	/// <summary>Whether a title update can be installed, i.e. there is a config for the patched executable.</summary>
	public bool SupportsTitleUpdate => _updatedVersion.Length != 0;

	/// <summary>The <c>versions/</c> folder describing the executable the generator reads.</summary>
	public string VersionFolder(bool titleUpdateInstalled) =>
		titleUpdateInstalled && SupportsTitleUpdate ? _updatedVersion : _discVersion;

	/// <summary>
	/// Whether <paramref name="versionFolder"/> is the disc executable of a title that also has a
	/// title update: the payload built from it is told so (REFII_GUEST_BUILD_UNPATCHED), and its
	/// hooks and host bridge then translate every address the source names in the updated build.
	/// </summary>
	public bool IsUnpatched(string versionFolder) => SupportsTitleUpdate && versionFolder == _discVersion;

	/// <summary><c>assets/&lt;Folder&gt;</c>: the title's install and everything derived from it.</summary>
	public string AssetsDirectory => System.IO.Path.Combine(Engine_AppRoot.AssetsDirectory, Folder);

	/// <summary><c>bin/&lt;Folder&gt;</c>: the title's ReXGlue instance.</summary>
	public string BinDirectory => System.IO.Path.Combine(Engine_AppRoot.BinDirectory, Folder);

	/// <summary>
	/// The title's folder in a save profile (<see cref="Refii.Save.Engine_SaveLayout"/>): the game's own
	/// name, "Fable 2" and "Fable 3", as a player looks for it.
	/// </summary>
	public string SaveFolder => Name;

	/// <summary>
	/// How many saved games the title keeps. Fable 2: 10 — the game ships 6, stored over the image's 150
	/// by the front end's enter (0x82378D38 <c>li r10, 6</c>, 0x82378D3C <c>stw r10, dword_83319620</c>)
	/// and compared by the New Game guard (0x826E0148) and the save create (0x82445104..0x8244510C:
	/// <c>sub_824431E8</c>'s count <c>bge</c> the limit); our post-hook on that enter stores 10, a rule
	/// change the user asked for (frontend_stated.cpp). Fable 3: 5, the save create's own constant
	/// (0x822F4D50 <c>bl sub_822F2370</c>, 0x822F4D54 <c>cmpwi r3, 5</c>, 0x822F4D58 <c>bge</c> refuses).
	/// </summary>
	public int SaveSlots { get; private init; } = 6;

	/// <summary>
	/// The files the patched guest reads from the update volume's <c>data\</c>, lifted out of the
	/// player's own title update package beside the patch (<c>Refii.Launcher.Core.Installer</c>).
	/// Fable 2 TU1: <c>tu1_data.bnk</c>, opened as <c>update:\data\tu1_data.bnk</c> by
	/// GameInit_MountDataFileSystems (0x822F2868). Fable 3 TU3: the package manager's refresh
	/// (sub_822EE318, from the boot sub_822D7260 at 0x822D74F0) opens <c>update:\data</c>
	/// (sub_822EC6D8: sub_82DB8C20 at 0x822EC740), reads <c>package_collection_info.xml</c>, else
	/// <c>.xmb</c> (0x822EC760 / 0x822EC7B8; the package ships the .xmb), opens the bank its
	/// <c>&lt;bankName&gt;</c> names (<c>tu1.bnk</c>, sub_823B41C0 at 0x823B4FA8; <c>.dat</c> beside it)
	/// and mounts it with its startup.vfsconfig (sub_822ECC38, 0x822ECE74 / 0x822ECEB0). Without the
	/// collection file the refresh returns at 0x822EC828 and the update's data layer is never mounted.
	/// <c>tu1.manifest.pkg</c> is read by nothing.
	/// </summary>
	public IReadOnlyList<string> TitleUpdateDataFiles { get; private init; } = System.Array.Empty<string>();

	/// <summary>
	/// The title's folder in the active save profile, <c>assets/saves/&lt;profile&gt;/&lt;SaveFolder&gt;</c>:
	/// its saves, settings, achievements and per-player state.
	/// </summary>
	public string SaveDirectory => Refii.Save.Engine_SaveLayout.GameDirectory(Engine_AppRoot.SaveDirectory, TitleId);

	/// <summary>
	/// <c>cache/&lt;Folder&gt;</c>: everything regenerable from this title's install — converted and
	/// enhanced textures, shadow bakes. Inside the app's one cache container, so clearing all
	/// derived data is still one folder and clearing one game's is its own.
	/// </summary>
	public string CacheDirectory => System.IO.Path.Combine(Engine_AppRoot.CacheDirectory, Folder);

	/// <summary>
	/// <c>logs/&lt;Folder&gt;</c>: this title's logs — the guest's segments (<c>&lt;Key&gt;_NNN.log</c>),
	/// its crash record, the hang watchdog's dumps and the game's Godot log. The launcher's own log
	/// stays in <c>logs/</c>.
	/// </summary>
	public string LogsDirectory => System.IO.Path.Combine(Engine_AppRoot.LogsDirectory, Folder);

	/// <summary>The file pattern of this title's guest log segments in <see cref="LogsDirectory"/>.</summary>
	public string GuestLogPattern => Key + "_*.log*";

	/// <summary>The game's Godot log in <see cref="LogsDirectory"/>, passed as <c>--log-file</c> at spawn.</summary>
	public string GodotLogPath => System.IO.Path.Combine(LogsDirectory, "godot.log");

	/// <summary>
	/// The title's runtime config file, at the app root: <c>&lt;Key&gt;.toml</c>, so <c>refii.toml</c>
	/// for Fable 2 and <c>refiii.toml</c> for Fable 3. The one file a player edits for that game:
	/// the launcher's settings for it, the Godot side's switches and the guest's cvars all come
	/// from it, and no title reads or writes another's.
	/// </summary>
	public string ConfigFileName => Key + ".toml";

	/// <summary><c>&lt;app root&gt;/&lt;ConfigFileName&gt;</c>: where the launcher writes it and the guest reads it.</summary>
	public string ConfigPath => System.IO.Path.Combine(Engine_AppRoot.Path, ConfigFileName);

	/// <summary>The game data directory relative to an <c>assets/</c> root: <c>Fable2/game</c>.</summary>
	public string GameDataRelative => System.IO.Path.Combine(Folder, "game");

	public static readonly Engine_Title Fable2 = new(
		"refii", "Fable 2", "Fable 2: Return to Albion", 0x4D5307F1, "Fable2", "refii_generated",
		"Fable2", "Fable2TU1", hasHardMods: true, ".f2mod", 0x5DD978FFu, hashedModelNodes: false,
		"refii_hostbridge", "refii_vfs", "refii_lua")
	{
		PhoneHostLabels = Refii.AndroidRuntime.AndroidMenuWords.Fable2,
		SaveSlots = 10,
		TitleUpdateDataFiles = new[] { "tu1_data.bnk" },
	};

	/// <summary>Fable 3 in Return to Albion.</summary>
	public static readonly Engine_Title Fable3 = new(
		"refiii", "Fable 3", "Fable 3: Return to Albion", 0x4D5308D6, "Fable3", "refiii_generated",
		"Fable3", "Fable3TU3", hasHardMods: true, ".f3mod", 0xE0474CFFu, hashedModelNodes: true,
		"refii_hostbridge", "refii_vfs")
	{
		UiText = Refii.Ui.Engine_UiTextContract.Fable3,
		SaveSlots = 5,
		TitleUpdateDataFiles = new[] { "package_collection_info.xmb", "tu1.bnk", "tu1.bnk.dat" },
	};

	public static IReadOnlyList<Engine_Title> All { get; } = new[] { Fable2, Fable3 };

	public static Engine_Title? Find(string? key)
	{
		foreach (Engine_Title title in All)
		{
			if (string.Equals(title.Key, key, StringComparison.OrdinalIgnoreCase))
			{
				return title;
			}
		}

		return null;
	}

	/// <summary>
	/// The title this process works on: the one its launcher named, or Fable 2. The launcher
	/// assigns it again from its own setting before anything reads it.
	/// </summary>
	public static Engine_Title Active { get; set; } =
		Find(Environment.GetEnvironmentVariable(EnvironmentVariable)) ?? Fable2;

	public override string ToString() => Name;
}
