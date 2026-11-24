using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Refii.Core;

namespace Refii.Save;

/// <summary>
/// Moves every save the app has ever written, in every layout it has used, into the one save
/// folder (<see cref="Engine_SaveLayout"/>), once, before the guest starts.
/// </summary>
/// <remarks>
/// THE SOURCES are content roots in the layouts the native side used before: <c>&lt;xuid&gt;/&lt;tid&gt;/
/// &lt;type&gt;/&lt;package&gt;</c> with <c>&lt;xuid&gt;/&lt;tid&gt;/Headers/&lt;type&gt;/&lt;package&gt;.header</c>, the settings at
/// <c>&lt;tid&gt;/profile/&lt;name&gt;</c> or <c>&lt;xuid&gt;/&lt;tid&gt;/profile/&lt;name&gt;</c>, the oldest bare <c>&lt;tid&gt;/
/// &lt;type&gt;/&lt;package&gt;</c>, PR 29's set-aside <c>.&lt;xuid&gt;.replaced-&lt;secs&gt;</c> and <c>.&lt;xuid&gt;.adopting</c>,
/// <c>&lt;xuid&gt;/refii-snapshots/&lt;save&gt;</c>, <c>rexnet/</c>, <c>achievements.refii</c> and
/// <c>achievements/&lt;tid&gt;.toml</c>; a Xenia content root has the first shape; an export root is
/// <c>&lt;tid&gt;/&lt;type&gt;/&lt;package&gt;</c>.
///
/// THE RULES. A source inside this install is MOVED (a rename) and gets a <c>MIGRATED.txt</c>; one
/// outside it is COPIED and never written. Nothing is deleted and nothing is overwritten. A package
/// whose files are byte-identical to one already placed is merged (skipped); one whose name is taken
/// takes the next free name and its header is rewritten to match. A title's saves fill its slots
/// newest first (<see cref="Engine_Title.SaveSlots"/>, the guest's own limit); the rest go to
/// <c>&lt;Game&gt;/Older saves/&lt;source&gt;/</c>, which the guest never enumerates. ONE identity survives,
/// key, display name and friends: the first key in source order (Fable 2's install first). Every other
/// identity is kept whole under <c>rexnet/retired/&lt;source&gt;/</c>; one from this install (Fable 3's)
/// also gives the kept friend lists its entries.
///
/// ONCE PER SOURCE: a source is recorded in <c>profiles.toml</c> when it has been taken whole, so a
/// later launch never copies an external save the player has since deleted back in. A source with a
/// failure is not recorded and is tried again, where everything already placed merges.
/// </remarks>
public static class Engine_SaveMigration
{
	public const string MarkerFileName = "MIGRATED.txt";

	/// <summary>One folder to migrate from.</summary>
	/// <param name="Path">The content root (or export root).</param>
	/// <param name="Label">Its name in the log and under Older saves; folder-safe.</param>
	/// <param name="Owned">Inside this install: moved. Otherwise copied.</param>
	/// <param name="DefaultTitle">The title of what the layout does not say a title for (snapshots, achievements.refii).</param>
	/// <param name="Exports">An export root, <c>&lt;tid&gt;/&lt;type&gt;/&lt;package&gt;</c>.</param>
	/// <param name="Titles">When set, only these titles' content is taken.</param>
	public sealed record Source(string Path, string Label, bool Owned, uint DefaultTitle, bool Exports = false,
		IReadOnlyCollection<uint>? Titles = null);

	/// <summary>What one run did.</summary>
	public sealed class Report
	{
		public bool Ran { get; set; }
		public string? ProfileDirectory { get; set; }
		public List<string> Log { get; } = new();
		public int Moved { get; set; }
		public int Copied { get; set; }
		public int Merged { get; set; }
		public int Renamed { get; set; }
		public int Failures { get; set; }

		/// <summary>Per title: saves placed in the slots and saves put in Older saves.</summary>
		public Dictionary<uint, (int Slotted, int Older)> Saves { get; } = new();

		public string Summary =>
			$"moved {Moved}, copied {Copied}, merged {Merged} identical, renamed {Renamed}, failures {Failures}; " +
			string.Join(", ", Saves.OrderBy(pair => pair.Key).Select(pair =>
				$"{Engine_SaveLayout.GameFolder(pair.Key)}: {pair.Value.Slotted} in slots, {pair.Value.Older} in Older saves"));
	}

	/// <summary>A save put aside in Older saves, which the launcher can swap back in.</summary>
	public sealed record OlderSave(string Label, string Name, string Directory, string? Header, DateTime Modified);

	private static readonly Regex XuidGeneration = new(@"\A\.?[0-9A-Fa-f]{16}(\.replaced-[0-9]+|\.adopting)?\z", RegexOptions.CultureInvariant);
	private static readonly Regex Hex8 = new(@"\A[0-9A-Fa-f]{8}\z", RegexOptions.CultureInvariant);
	private static readonly Regex NumberedName = new(@"\A(.*?)([0-9]+)\z", RegexOptions.CultureInvariant);

	private static readonly string[] FriendFiles =
	{
		"friends.txt", "friends_pending.txt", "friends_inbound.txt", "friend_names.txt",
	};

	private const string IdentityKeyFile = "identity.key";
	private const string RetiredFolder = "retired";
	private const string SnapshotsFolder = "refii-snapshots";
	private const string AchievementsFile = "achievements.refii";

	/// <summary>Offset of XCONTENT_DATA's 42-byte file name (+2 padding) in a <c>.header</c>: after device id, type and the 128-char display name.</summary>
	private const int HeaderFileNameOffset = 4 + 4 + 256;
	private const int HeaderFileNameBytes = 42 + 2;

	private sealed record Package(Source Source, string Generation, uint Title, string Type, string Name, string Directory, string? Header, DateTime When);
	private sealed record SettingsSet(Source Source, string Generation, uint Title, string Name, string Directory, DateTime When);
	private sealed record Snapshot(Source Source, string Generation, uint Title, string Save, string Directory);
	private sealed record Loose(Source Source, string Generation, uint Title, string Kind, string File);
	private sealed record IdentityDir(Source Source, string Generation, string Directory);
	private sealed record ExportPackage(Source Source, uint Title, string Type, string Name, string Directory);

	private sealed class Scan
	{
		public List<Package> Packages { get; } = new();
		public List<SettingsSet> Settings { get; } = new();
		public List<Snapshot> Snapshots { get; } = new();
		public List<Loose> Loose { get; } = new();
		public List<IdentityDir> Identities { get; } = new();
		public List<ExportPackage> Exports { get; } = new();
		public bool Empty => Packages.Count + Settings.Count + Snapshots.Count + Loose.Count + Identities.Count + Exports.Count == 0;
	}

	/// <summary>
	/// The sources of an install whose assets are <paramref name="assets"/>: its own (moved), then, when
	/// <paramref name="home"/> is given, the folders outside it earlier builds and Xenia wrote (copied).
	/// </summary>
	public static List<Source> DefaultSources(string assets, string? home, string? dataHome = null, string? documents = null)
	{
		uint fable2 = Engine_Title.Fable2.TitleId;
		uint fable3 = Engine_Title.Fable3.TitleId;
		var sources = new List<Source>();

		void Own(string path, string label, uint title)
		{
			sources.Add(new Source(path, label, Owned: true, title));
			string? parent = Path.GetDirectoryName(path);
			string name = Path.GetFileName(path);
			if (parent is null || !Directory.Exists(parent))
			{
				return;
			}

			foreach (string sibling in Directory.EnumerateDirectories(parent).OrderBy(p => p, StringComparer.Ordinal))
			{
				string siblingName = Path.GetFileName(sibling);
				if (!siblingName.StartsWith(name + ".", StringComparison.Ordinal))
				{
					continue;
				}

				string suffix = siblingName[(name.Length + 1)..];
				if (suffix.StartsWith("import-", StringComparison.Ordinal) || suffix.StartsWith("replaced-", StringComparison.Ordinal))
				{
					sources.Add(new Source(sibling, label + " " + suffix, Owned: true, title));
				}
			}
		}

		Own(Path.Combine(assets, Engine_Title.Fable2.Folder, "save"), "Fable2 save", fable2);
		Own(Path.Combine(assets, "save"), "save before the title split", fable2);
		Own(Path.Combine(assets, Engine_Title.Fable3.Folder, "save"), "Fable3 save", fable3);
		sources.Add(new Source(Path.Combine(assets, Engine_Title.Fable2.Folder, Engine_SaveLayout.ExportFolderName),
			"Fable2 save-export", Owned: true, fable2, Exports: true));
		sources.Add(new Source(Path.Combine(assets, Engine_Title.Fable3.Folder, Engine_SaveLayout.ExportFolderName),
			"Fable3 save-export", Owned: true, fable3, Exports: true));

		if (string.IsNullOrEmpty(home))
		{
			return sources;
		}

		var ours = new[] { fable2, fable3 };
		string data = string.IsNullOrEmpty(dataHome) ? Path.Combine(home, ".local", "share") : dataHome;
		string docs = string.IsNullOrEmpty(documents) ? Path.Combine(home, "Documents") : documents;
		sources.Add(new Source(Path.Combine(data, "refii"), "local share refii", Owned: false, fable2));
		sources.Add(new Source(Path.Combine(home, ".config", "Fable2Recomp", "save"), "Fable2Recomp", Owned: false, fable2));
		sources.Add(new Source(Path.Combine(docs, "refii"), "Documents refii", Owned: false, fable2));

		var xenia = new List<string> { Path.Combine(docs, "Xenia", "content"), Path.Combine(data, "Xenia", "content") };
		string games = Path.Combine(home, "Games");
		if (Directory.Exists(games))
		{
			foreach (string prefix in SafeDirectories(games))
			{
				xenia.Add(Path.Combine(prefix, "content"));
				string drive = Path.Combine(prefix, "drive_c");
				if (Directory.Exists(drive))
				{
					xenia.AddRange(SafeDirectories(drive).Select(folder => Path.Combine(folder, "content")));
				}
			}
		}

		foreach (string root in xenia.Distinct(StringComparer.Ordinal).Where(Directory.Exists))
		{
			string where = Path.GetRelativePath(home, Path.GetDirectoryName(root)!).Replace(Path.DirectorySeparatorChar, ' ');
			sources.Add(new Source(root, Engine_SaveLayout.SanitizeFolderName("Xenia " + where), Owned: false, fable2, Titles: ours));
		}

		return sources;
	}

	/// <summary>Runs every source not yet migrated into the active profile under <paramref name="savesRoot"/>, creating the profile when there is none.</summary>
	public static Report Run(string savesRoot, IReadOnlyList<Source> sources, DateTime now)
	{
		var report = new Report();
		Engine_SaveProfiles profiles = Engine_SaveProfiles.Load(savesRoot);
		var done = new HashSet<string>(profiles.MigratedSources, StringComparer.Ordinal);
		string full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
		string rootFull = full(savesRoot);
		List<Source> pending = sources
			.Where(source => !done.Contains(full(source.Path)) && Directory.Exists(source.Path) &&
				!full(source.Path).StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
				full(source.Path) != rootFull)
			.ToList();
		if (pending.Count == 0)
		{
			return report;
		}

		var scan = new Scan();
		var unread = new HashSet<Source>();
		foreach (Source source in pending)
		{
			if (!ScanSource(source, scan, report)) unread.Add(source);
		}

		report.Ran = true;
		Engine_SaveProfiles.Profile active = profiles.Active ?? CreateProfile(profiles, scan, report);
		profiles.ActiveId = active.Id;
		string profile = profiles.DirectoryOf(active);
		report.ProfileDirectory = profile;
		Directory.CreateDirectory(profile);

		HashSet<Source> failed = Place(profile, active.Folder, scan, savesRoot, now, pending, report);
		failed.UnionWith(unread);
		foreach (Source source in pending)
		{
			if (failed.Contains(source))
			{
				report.Log.Add($"source kept for the next launch (a step failed): {source.Path}");
				continue;
			}

			profiles.MigratedSources.Add(full(source.Path));
		}

		profiles.Save();
		report.Log.Add("summary: " + report.Summary);
		AppendLog(savesRoot, now, report);
		return report;
	}

	/// <summary>
	/// Migrates <paramref name="sources"/> into the profile folder <paramref name="profileDirectory"/> as
	/// it stands, with no profile list and no record of what was taken: an import's staging profile.
	/// The log goes to <paramref name="logRoot"/>'s migration.log when given.
	/// </summary>
	public static Report MigrateInto(string profileDirectory, IReadOnlyList<Source> sources, string? logRoot, DateTime now)
	{
		var report = new Report { Ran = true, ProfileDirectory = profileDirectory };
		var scan = new Scan();
		List<Source> present = sources.Where(source => Directory.Exists(source.Path)).ToList();
		foreach (Source source in present)
		{
			ScanSource(source, scan, report);
		}

		Directory.CreateDirectory(profileDirectory);
		Place(profileDirectory, Path.GetFileName(profileDirectory), scan, logRoot, now, present, report);
		report.Log.Add("summary: " + report.Summary);
		if (logRoot is not null)
		{
			AppendLog(logRoot, now, report);
		}

		return report;
	}

	/// <summary>Places everything scanned; returns the sources a step failed for. Marks every owned source that was taken whole.</summary>
	private static HashSet<Source> Place(string profile, string profileFolder, Scan scan, string? savesRoot, DateTime now,
		IReadOnlyList<Source> sources, Report report)
	{
		var failedSources = new HashSet<Source>();
		void Note(Source source, bool ok, bool moved)
		{
			if (!ok) failedSources.Add(source);
		}

		MergeIdentities(profile, scan, report, Note);
		var placed = PlacePackages(profile, scan, report, Note);
		PlaceSettings(profile, profileFolder, scan, report, Note);
		PlaceSnapshots(profile, scan, placed, report, Note);
		PlaceLoose(profile, scan, report, Note);
		PlaceExports(profile, scan, report, Note);

		foreach (Source source in sources)
		{
			if (source.Owned && !failedSources.Contains(source))
			{
				WriteMarker(source.Path, savesRoot ?? Path.GetDirectoryName(profile)!, profile, now, report);
			}
		}

		return failedSources;
	}

	// ---- scanning -------------------------------------------------------------------------------

	private static bool ScanSource(Source source, Scan scan, Report report)
	{
		try
		{
			if (source.Exports)
			{
				foreach (string titleDir in SafeDirectories(source.Path))
				{
					if (ParseTitle(Path.GetFileName(titleDir)) is not { } title || !Wanted(source, title)) continue;
					foreach (string typeDir in SafeDirectories(titleDir).Where(d => Hex8.IsMatch(Path.GetFileName(d))))
					{
						foreach (string package in SafeDirectories(typeDir))
						{
							scan.Exports.Add(new ExportPackage(source, title, Path.GetFileName(typeDir), Path.GetFileName(package), package));
						}
					}
				}

				return true;
			}

			foreach (string child in SafeDirectories(source.Path))
			{
				string name = Path.GetFileName(child);
				if (name == Engine_SaveLayout.IdentityFolderName)
				{
					scan.Identities.Add(new IdentityDir(source, source.Label, child));
				}
				else if (XuidGeneration.IsMatch(name))
				{
					string generation = source.Label + " " + name.TrimStart('.');
					foreach (string sub in SafeDirectories(child))
					{
						string subName = Path.GetFileName(sub);
						if (ParseTitle(subName) is { } title)
						{
							ScanTitle(source, generation, title, sub, scan);
						}
						else if (subName == SnapshotsFolder)
						{
							foreach (string save in SafeDirectories(sub))
							{
								scan.Snapshots.Add(new Snapshot(source, generation, source.DefaultTitle, Path.GetFileName(save), save));
							}
						}
					}
				}
				else if (ParseTitle(name) is { } bareTitle)
				{
					ScanTitle(source, source.Label, bareTitle, child, scan);
				}
				else if (name == "achievements")
				{
					foreach (string file in Directory.EnumerateFiles(child, "*.toml"))
					{
						if (ParseTitle(Path.GetFileNameWithoutExtension(file)) is { } title && Wanted(source, title))
						{
							scan.Loose.Add(new Loose(source, source.Label, title, Engine_SaveLayout.SdkAchievementsFileName, file));
						}
					}
				}
			}

			string achievements = Path.Combine(source.Path, AchievementsFile);
			if (File.Exists(achievements) && Wanted(source, source.DefaultTitle))
			{
				scan.Loose.Add(new Loose(source, source.Label, source.DefaultTitle, AchievementsFile, achievements));
			}

			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			report.Failures++;
			report.Log.Add($"could not read {source.Path}: {exception.Message}");
			return false;
		}
	}

	private static void ScanTitle(Source source, string generation, uint title, string titleDir, Scan scan)
	{
		if (!Wanted(source, title))
		{
			return;
		}

		foreach (string sub in SafeDirectories(titleDir))
		{
			string name = Path.GetFileName(sub);
			if (name == Engine_SaveLayout.SettingsFolderName)
			{
				foreach (string settings in SafeDirectories(sub))
				{
					scan.Settings.Add(new SettingsSet(source, generation, title, Path.GetFileName(settings), settings, NewestWrite(settings)));
				}
			}
			else if (Hex8.IsMatch(name))
			{
				foreach (string package in SafeDirectories(sub))
				{
					string packageName = Path.GetFileName(package);
					string header = Path.Combine(titleDir, Engine_SaveLayout.HeadersFolderName, name, packageName + ".header");
					scan.Packages.Add(new Package(source, generation, title, name.ToUpperInvariant(), packageName, package,
						File.Exists(header) ? header : null, PackageTime(package)));
				}
			}
		}
	}

	private static bool Wanted(Source source, uint title) =>
		title < 0xFFFE0000u && (source.Titles is null || source.Titles.Contains(title));

	private static uint? ParseTitle(string name) =>
		Hex8.IsMatch(name) ? uint.Parse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;

	// ---- the profile and its identity -----------------------------------------------------------

	private static Engine_SaveProfiles.Profile CreateProfile(Engine_SaveProfiles profiles, Scan scan, Report report)
	{
		IdentityDir? kept = scan.Identities.FirstOrDefault(identity => File.Exists(Path.Combine(identity.Directory, IdentityKeyFile)));
		string? stated = null;
		if (kept is not null)
		{
			string nameFile = Path.Combine(kept.Directory, Engine_SaveLayout.ProfileNameFileName);
			stated = File.Exists(nameFile) ? File.ReadAllText(nameFile).Trim() : null;
		}

		string? named = stated;
		if (string.IsNullOrEmpty(named))
		{
			// The guest names its settings folder after the profile it signs in with; the kept
			// identity's own root says what that identity was called.
			IEnumerable<SettingsSet> candidates = scan.Settings
				.Where(settings => kept is null || settings.Source == kept.Source)
				.Where(settings => settings.Name != "User")
				.OrderBy(settings => settings.Title == Engine_Title.Fable2.TitleId ? 0 : 1)
				.ThenByDescending(settings => settings.When);
			named = candidates.FirstOrDefault()?.Name;
		}

		bool provisional = string.IsNullOrEmpty(stated);
		Engine_SaveProfiles.Profile profile = profiles.Create(named ?? Engine_SaveProfiles.DefaultFolder, provisional, report.Log);
		return profile;
	}

	private static void MergeIdentities(string profile, Scan scan, Report report, Action<Source, bool, bool> note)
	{
		string target = Engine_SaveLayout.Identity(profile);
		bool haveKey = File.Exists(Path.Combine(target, IdentityKeyFile));
		IdentityDir? kept = haveKey ? null : scan.Identities.FirstOrDefault(identity => File.Exists(Path.Combine(identity.Directory, IdentityKeyFile)));
		foreach (IdentityDir identity in scan.Identities)
		{
			bool isKept = identity == kept;
			string retired = Path.Combine(target, RetiredFolder, Engine_SaveLayout.SanitizeFolderName(identity.Generation));
			foreach (string file in SafeFiles(identity.Directory))
			{
				string name = Path.GetFileName(file);
				bool ok = true, moved = false;
				try
				{
					// The install's own other identities (Fable 3's) give the kept one their friends; an
					// identity from outside the install is another player's history and is only kept.
					if (FriendFiles.Contains(name, StringComparer.Ordinal) && (isKept || identity.Source.Owned))
					{
						int added = MergeLines(file, Path.Combine(target, name));
						report.Log.Add($"friends: {added} new entr{(added == 1 ? "y" : "ies")} from {file} into {Path.Combine(target, name)}");
						continue;
					}

					string destination = Path.Combine(target, name);
					if (!isKept || File.Exists(destination))
					{
						destination = Path.Combine(retired, name);
					}

					if (File.Exists(destination))
					{
						report.Log.Add($"skip {file}: {destination} exists");
						continue;
					}

					moved = Transfer(file, destination, identity.Source.Owned, report);
					report.Log.Add($"{Verb(identity.Source)} {file} -> {destination}{(isKept && name == IdentityKeyFile ? " (the kept identity)" : string.Empty)}");
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					ok = false;
					report.Failures++;
					report.Log.Add($"could not take {file}: {exception.Message}");
				}

				note(identity.Source, ok, moved);
			}
		}
	}

	/// <summary>Adds the lines of <paramref name="from"/> whose peer (the first field) <paramref name="into"/> does not hold; returns how many.</summary>
	private static int MergeLines(string from, string into)
	{
		var lines = File.Exists(into) ? File.ReadAllLines(into).ToList() : new List<string>();
		var keys = new HashSet<string>(lines.Select(LineKey).Where(key => key.Length != 0), StringComparer.Ordinal);
		int added = 0;
		foreach (string line in File.ReadAllLines(from))
		{
			string key = LineKey(line);
			if (key.Length == 0)
			{
				if (lines.Count == 0 && line.TrimStart().StartsWith('#'))
				{
					lines.Add(line);
				}

				continue;
			}

			if (keys.Add(key))
			{
				lines.Add(line);
				added++;
			}
		}

		Directory.CreateDirectory(Path.GetDirectoryName(into)!);
		File.WriteAllText(into, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
		return added;
	}

	private static string LineKey(string line)
	{
		string trimmed = line.Trim();
		if (trimmed.Length == 0 || trimmed.StartsWith('#'))
		{
			return string.Empty;
		}

		int space = trimmed.IndexOfAny(new[] { '\t', ' ' });
		return space < 0 ? trimmed : trimmed[..space];
	}

	// ---- packages ---------------------------------------------------------------------------------

	/// <summary>(generation, title, original name) -> the slot name a save was placed under.</summary>
	private static Dictionary<(string, uint, string), string> PlacePackages(string profile, Scan scan, Report report,
		Action<Source, bool, bool> note)
	{
		var placed = new Dictionary<(string, uint, string), string>();
		foreach (var group in scan.Packages.GroupBy(p => (p.Title, p.Type)).OrderBy(g => g.Key.Title).ThenBy(g => g.Key.Type, StringComparer.Ordinal))
		{
			(uint title, string type) = group.Key;
			string game = Engine_SaveLayout.GameDirectory(profile, title);
			string target = Path.Combine(game, type);
			string headers = Path.Combine(game, Engine_SaveLayout.HeadersFolderName, type);
			bool saves = type == Engine_SaveLayout.SaveGameType;
			int limit = saves ? Engine_Title.All.FirstOrDefault(t => t.TitleId == title)?.SaveSlots ?? int.MaxValue : int.MaxValue;

			var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
			int existing = 0;
			foreach (string present in SafeDirectories(target))
			{
				existing++;
				hashes.TryAdd(Hash(present), Path.GetFileName(present));
			}

			int free = Math.Max(0, limit - existing);
			int slotted = 0, older = 0;
			IEnumerable<Package> incoming = group
				.OrderByDescending(p => p.When)
				.ThenBy(p => scan.Packages.IndexOf(p));
			foreach (Package package in incoming)
			{
				bool ok = true, moved = false;
				try
				{
					if (!SafeFiles(package.Directory, recursive: true).Any())
					{
						report.Log.Add($"skip {package.Directory}: empty");
						continue;
					}

					string hash = Hash(package.Directory);
					if (hashes.TryGetValue(hash, out string? same))
					{
						report.Merged++;
						report.Log.Add($"merged {package.Directory}: identical to {same}");
						continue;
					}

					string folder, headerFolder;
					if (free > 0)
					{
						folder = target;
						headerFolder = headers;
						free--;
						slotted++;
					}
					else
					{
						string aside = Path.Combine(game, Engine_SaveLayout.OlderSavesFolderName, Engine_SaveLayout.SanitizeFolderName(package.Generation));
						folder = Path.Combine(aside, type);
						headerFolder = Path.Combine(aside, Engine_SaveLayout.HeadersFolderName, type);
						older++;
					}

					string name = FreeName(folder, headerFolder, package.Name);
					string destination = Path.Combine(folder, name);
					moved = Transfer(package.Directory, destination, package.Source.Owned, report);
					hashes[hash] = (folder == target ? string.Empty : Engine_SaveLayout.OlderSavesFolderName + "/") + name;
					if (folder == target)
					{
						placed[(package.Generation, title, package.Name)] = name;
					}

					string renamed = name != package.Name ? $" (renamed from {package.Name})" : string.Empty;
					if (renamed.Length != 0) report.Renamed++;
					report.Log.Add($"{Verb(package.Source)} {package.Directory} -> {destination}{renamed}{(folder == target ? string.Empty : " (past the slot limit)")}");
					if (package.Header is { } header)
					{
						string headerDestination = Path.Combine(headerFolder, name + ".header");
						Transfer(header, headerDestination, package.Source.Owned, report);
						if (name != package.Name)
						{
							RenameHeader(headerDestination, name);
						}

						report.Log.Add($"{Verb(package.Source)} {header} -> {headerDestination}");
					}
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					ok = false;
					report.Failures++;
					report.Log.Add($"could not take {package.Directory}: {exception.Message}");
				}

				note(package.Source, ok, moved);
			}

			if (saves)
			{
				(int s, int o) = report.Saves.TryGetValue(title, out var counts) ? counts : (0, 0);
				report.Saves[title] = (s + slotted, o + older);
			}
		}

		return placed;
	}

	/// <summary><paramref name="name"/> when neither a package nor a header holds it, else the next free number (Hero000 -> Hero006).</summary>
	public static string FreeName(string folder, string headerFolder, string name)
	{
		bool taken(string candidate) =>
			Directory.Exists(Path.Combine(folder, candidate)) || File.Exists(Path.Combine(folder, candidate)) ||
			File.Exists(Path.Combine(headerFolder, candidate + ".header"));
		if (!taken(name))
		{
			return name;
		}

		Match match = NumberedName.Match(name);
		string stem = match.Success ? match.Groups[1].Value : name;
		int width = match.Success ? match.Groups[2].Value.Length : 3;
		int number = match.Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
		string candidate;
		do
		{
			number++;
			candidate = stem + number.ToString(new string('0', width), CultureInfo.InvariantCulture);
		}
		while (taken(candidate) || candidate.Length > 42);

		return candidate;
	}

	/// <summary>Writes <paramref name="name"/> over a header's XCONTENT_DATA file name (42 bytes and the two padding bytes, zeroed).</summary>
	public static void RenameHeader(string header, string name)
	{
		using var stream = new FileStream(header, FileMode.Open, FileAccess.ReadWrite);
		if (stream.Length < HeaderFileNameOffset + HeaderFileNameBytes)
		{
			return;
		}

		byte[] field = new byte[HeaderFileNameBytes];
		byte[] text = Encoding.ASCII.GetBytes(name);
		Array.Copy(text, field, Math.Min(text.Length, 42));
		stream.Position = HeaderFileNameOffset;
		stream.Write(field);
	}

	/// <summary>SHA-256 over a package's files: each relative path, length and bytes, in ordinal order.</summary>
	private static string Hash(string directory)
	{
		using Engine_Hash.Incremental hash = Engine_Hash.CreateIncrementalSha256();
		foreach (string file in SafeFiles(directory, recursive: true).OrderBy(f => f, StringComparer.Ordinal))
		{
			string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
			hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
			byte[] bytes = File.ReadAllBytes(file);
			hash.AppendData(BitConverter.GetBytes((long)bytes.Length));
			hash.AppendData(bytes);
		}

		return Convert.ToHexString(hash.GetHashAndReset());
	}

	// ---- settings, snapshots, achievements, exports -------------------------------------------------

	private static void PlaceSettings(string profile, string profileFolder, Scan scan, Report report, Action<Source, bool, bool> note)
	{
		foreach (var group in scan.Settings.GroupBy(s => s.Title))
		{
			string target = Engine_SaveLayout.Settings(profile, group.Key);
			bool filled = SafeFiles(target, recursive: true).Any();
			// Source order first (the kept identity's install leads), then the settings of the name the
			// profile is called by, then any other name before the signed-out "User", newest first.
			IEnumerable<SettingsSet> ordered = group
				.OrderBy(s => scan.Settings.FindIndex(x => x.Source == s.Source))
				.ThenBy(s => s.Name == profileFolder ? 0 : s.Name == "User" ? 2 : 1)
				.ThenByDescending(s => s.When);
			foreach (SettingsSet settings in ordered)
			{
				bool ok = true, moved = false;
				try
				{
					string destination = filled
						? Path.Combine(Engine_SaveLayout.OlderSaves(profile, group.Key), Engine_SaveLayout.SanitizeFolderName(settings.Generation),
							Engine_SaveLayout.SettingsFolderName, Engine_SaveLayout.SanitizeFolderName(settings.Name))
						: target;
					if (Directory.Exists(destination) && SafeFiles(destination, recursive: true).Any())
					{
						report.Log.Add($"skip {settings.Directory}: {destination} exists");
						continue;
					}

					if (Directory.Exists(destination))
					{
						Directory.Delete(destination);
					}

					moved = Transfer(settings.Directory, destination, settings.Source.Owned, report);
					report.Log.Add($"{Verb(settings.Source)} settings {settings.Directory} -> {destination}");
					filled = true;
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					ok = false;
					report.Failures++;
					report.Log.Add($"could not take {settings.Directory}: {exception.Message}");
				}

				note(settings.Source, ok, moved);
			}
		}
	}

	private static void PlaceSnapshots(string profile, Scan scan, Dictionary<(string, uint, string), string> placed, Report report,
		Action<Source, bool, bool> note)
	{
		foreach (Snapshot snapshot in scan.Snapshots)
		{
			bool ok = true, moved = false;
			try
			{
				string game = Engine_SaveLayout.GameDirectory(profile, snapshot.Title);
				string destination = placed.TryGetValue((snapshot.Generation, snapshot.Title, snapshot.Save), out string? slot)
					? Path.Combine(game, SnapshotsFolder, slot)
					: Path.Combine(game, Engine_SaveLayout.OlderSavesFolderName, Engine_SaveLayout.SanitizeFolderName(snapshot.Generation),
						SnapshotsFolder, snapshot.Save);
				if (Directory.Exists(destination))
				{
					destination = Path.Combine(game, Engine_SaveLayout.OlderSavesFolderName, Engine_SaveLayout.SanitizeFolderName(snapshot.Generation),
						SnapshotsFolder, snapshot.Save);
				}

				if (Directory.Exists(destination))
				{
					report.Log.Add($"skip {snapshot.Directory}: {destination} exists");
					continue;
				}

				moved = Transfer(snapshot.Directory, destination, snapshot.Source.Owned, report);
				report.Log.Add($"{Verb(snapshot.Source)} snapshots {snapshot.Directory} -> {destination}");
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				ok = false;
				report.Failures++;
				report.Log.Add($"could not take {snapshot.Directory}: {exception.Message}");
			}

			note(snapshot.Source, ok, moved);
		}
	}

	private static void PlaceLoose(string profile, Scan scan, Report report, Action<Source, bool, bool> note)
	{
		foreach (Loose loose in scan.Loose)
		{
			bool ok = true, moved = false;
			try
			{
				string game = Engine_SaveLayout.GameDirectory(profile, loose.Title);
				string destination = Path.Combine(game, loose.Kind);
				if (!File.Exists(destination))
				{
					moved = Transfer(loose.File, destination, loose.Source.Owned, report);
					report.Log.Add($"{Verb(loose.Source)} {loose.File} -> {destination}");
				}
				else if (loose.Kind == AchievementsFile && MergeAchievements(loose.File, destination) is { } added)
				{
					report.Log.Add($"merged {added} unlock(s) from {loose.File} into {destination}");
				}
				else
				{
					string aside = Path.Combine(game, Engine_SaveLayout.OlderSavesFolderName, Engine_SaveLayout.SanitizeFolderName(loose.Generation), loose.Kind);
					if (File.Exists(aside))
					{
						report.Log.Add($"skip {loose.File}: {aside} exists");
						continue;
					}

					moved = Transfer(loose.File, aside, loose.Source.Owned, report);
					report.Log.Add($"{Verb(loose.Source)} {loose.File} -> {aside}");
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				ok = false;
				report.Failures++;
				report.Log.Add($"could not take {loose.File}: {exception.Message}");
			}

			note(loose.Source, ok, moved);
		}
	}

	/// <summary>Adds the unlocks of <paramref name="from"/> to <paramref name="into"/>; null when either cannot be read.</summary>
	private static int? MergeAchievements(string from, string into)
	{
		var source = Refii.Achievements.Engine_AchievementStore.Load(from);
		var target = Refii.Achievements.Engine_AchievementStore.Load(into);
		if (source.LoadProblem is not null || target.LoadProblem is not null)
		{
			return null;
		}

		int added = 0;
		foreach (var unlock in source.Unlocks)
		{
			if (target.Add(unlock.UserIndex, unlock.AchievementId, unlock.UnlockedUnixSeconds))
			{
				added++;
			}
		}

		if (added != 0)
		{
			target.Save(into);
		}

		return added;
	}

	private static void PlaceExports(string profile, Scan scan, Report report, Action<Source, bool, bool> note)
	{
		foreach (ExportPackage export in scan.Exports)
		{
			bool ok = true, moved = false;
			try
			{
				string game = Engine_SaveLayout.GameDirectory(profile, export.Title);
				string tail = Path.Combine(Engine_SaveLayout.ExportFolderName, Engine_SaveLayout.TitleIdText(export.Title), export.Type, export.Name);
				string destination = Path.Combine(game, tail);
				if (Directory.Exists(destination))
				{
					destination = Path.Combine(game, Engine_SaveLayout.OlderSavesFolderName, export.Source.Label, tail);
				}

				if (Directory.Exists(destination))
				{
					report.Log.Add($"skip {export.Directory}: {destination} exists");
					continue;
				}

				moved = Transfer(export.Directory, destination, export.Source.Owned, report);
				report.Log.Add($"{Verb(export.Source)} export {export.Directory} -> {destination}");
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				ok = false;
				report.Failures++;
				report.Log.Add($"could not take {export.Directory}: {exception.Message}");
			}

			note(export.Source, ok, moved);
		}
	}

	// ---- Older saves --------------------------------------------------------------------------------

	/// <summary>The saved games set aside for <paramref name="titleId"/> in a profile, newest first.</summary>
	public static List<OlderSave> ListOlderSaves(string profileDirectory, uint titleId)
	{
		var list = new List<OlderSave>();
		string older = Engine_SaveLayout.OlderSaves(profileDirectory, titleId);
		foreach (string label in SafeDirectories(older))
		{
			string saves = Path.Combine(label, Engine_SaveLayout.SaveGameType);
			foreach (string package in SafeDirectories(saves))
			{
				string name = Path.GetFileName(package);
				string header = Path.Combine(label, Engine_SaveLayout.HeadersFolderName, Engine_SaveLayout.SaveGameType, name + ".header");
				list.Add(new OlderSave(Path.GetFileName(label), name, package, File.Exists(header) ? header : null, PackageTime(package)));
			}
		}

		return list.OrderByDescending(save => save.Modified).ToList();
	}

	/// <summary>
	/// Puts an older save back into the title's slots. When every slot is used, <paramref name="outgoing"/>
	/// (a save in the slots) goes to <c>Older saves/Swapped out/</c> first. Returns the name it now has.
	/// </summary>
	public static string? SwapIn(string profileDirectory, uint titleId, OlderSave save, string? outgoing, out string error)
	{
		error = string.Empty;
		string slots = Engine_SaveLayout.SaveGames(profileDirectory, titleId);
		string headers = Engine_SaveLayout.Headers(profileDirectory, titleId);
		int limit = Engine_Title.All.FirstOrDefault(t => t.TitleId == titleId)?.SaveSlots ?? int.MaxValue;
		try
		{
			int used = SafeDirectories(slots).Count();
			if (used >= limit)
			{
				if (string.IsNullOrEmpty(outgoing) || !Directory.Exists(Path.Combine(slots, outgoing)) ||
					outgoing.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
				{
					error = $"every slot is used ({used} of {limit}): choose a save to swap out";
					return null;
				}

				if (SetAside(profileDirectory, titleId, outgoing, SwappedOutLabel, out error) is null)
				{
					return null;
				}
			}

			string name = FreeName(slots, headers, save.Name);
			Transfer(save.Directory, Path.Combine(slots, name), owned: true, null);
			if (save.Header is { } header)
			{
				string target = Path.Combine(headers, name + ".header");
				Transfer(header, target, owned: true, null);
				if (name != save.Name) RenameHeader(target, name);
			}

			return name;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			error = exception.Message;
			return null;
		}
	}

	/// <summary>Older saves' group for a save the launcher's Swap took out of the slots.</summary>
	public const string SwappedOutLabel = "Swapped out";

	/// <summary>Older saves' group for a save the game's New Game replaced at the save limit.</summary>
	public const string OverwrittenLabel = "Overwritten";

	/// <summary>
	/// Moves one save out of the title's slots into <c>Older saves/&lt;label&gt;/</c>: its folder
	/// (<c>00000001/&lt;name&gt;</c>) and its header (<c>Headers/00000001/&lt;name&gt;.header</c>), the
	/// layout and files the original game and Xenia read, unchanged. Nothing is deleted: a name
	/// already there takes the next free number (Hero003 -&gt; Hero004) with the header's file name
	/// rewritten to match, and a move that could only copy leaves the original where it was and
	/// fails. Returns the name it has there, or null with <paramref name="error"/>. The launcher's
	/// Swap and the game's replace-a-save both come through here.
	/// </summary>
	public static string? SetAside(string profileDirectory, uint titleId, string name, string label, out string error)
	{
		error = string.Empty;
		string slots = Engine_SaveLayout.SaveGames(profileDirectory, titleId);
		string headers = Engine_SaveLayout.Headers(profileDirectory, titleId);
		if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." ||
			!Directory.Exists(Path.Combine(slots, name)))
		{
			error = $"no save named '{name}' in the slots";
			return null;
		}

		try
		{
			string aside = Path.Combine(Engine_SaveLayout.OlderSaves(profileDirectory, titleId), Engine_SaveLayout.SanitizeFolderName(label));
			string asideSaves = Path.Combine(aside, Engine_SaveLayout.SaveGameType);
			string asideHeaders = Path.Combine(aside, Engine_SaveLayout.HeadersFolderName, Engine_SaveLayout.SaveGameType);
			string asideName = FreeName(asideSaves, asideHeaders, name);
			if (!Transfer(Path.Combine(slots, name), Path.Combine(asideSaves, asideName), owned: true, null))
			{
				error = $"'{name}' could only be copied to {asideSaves}, not moved; the original is still in the slots";
				return null;
			}

			string header = Path.Combine(headers, name + ".header");
			if (File.Exists(header))
			{
				string target = Path.Combine(asideHeaders, asideName + ".header");
				Transfer(header, target, owned: true, null);
				if (asideName != name) RenameHeader(target, asideName);
			}

			return asideName;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			error = exception.Message;
			return null;
		}
	}

	// ---- file operations ----------------------------------------------------------------------------

	/// <summary>Moves (owned) or copies a file or folder to a destination that must not exist. True when it moved.</summary>
	private static bool Transfer(string source, string destination, bool owned, Report? report)
	{
		if (Directory.Exists(destination) || File.Exists(destination))
		{
			throw new IOException(destination + " already exists");
		}

		Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		bool directory = Directory.Exists(source);
		if (owned)
		{
			try
			{
				if (directory) Directory.Move(source, destination);
				else File.Move(source, destination);
				if (report is not null) report.Moved++;
				return true;
			}
			catch (IOException) when (!Directory.Exists(destination) && !File.Exists(destination))
			{
				// Another volume: a rename cannot cross it, so the copy is taken and the original kept.
			}
		}

		if (directory) CopyDirectory(source, destination);
		else CopyFile(source, destination);
		if (report is not null) report.Copied++;
		return false;
	}

	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (string entry in Directory.EnumerateFileSystemEntries(source))
		{
			if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
			{
				continue;
			}

			string target = Path.Combine(destination, Path.GetFileName(entry));
			if (Directory.Exists(entry)) CopyDirectory(entry, target);
			else CopyFile(entry, target);
		}

		Directory.SetLastWriteTimeUtc(destination, Directory.GetLastWriteTimeUtc(source));
	}

	private static void CopyFile(string source, string destination)
	{
		File.Copy(source, destination, overwrite: false);
		File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
	}

	private static IEnumerable<string> SafeDirectories(string path)
	{
		if (!Directory.Exists(path))
		{
			return Array.Empty<string>();
		}

		return Directory.EnumerateDirectories(path)
			.Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0)
			.OrderBy(d => d, StringComparer.Ordinal)
			.ToList();
	}

	private static IEnumerable<string> SafeFiles(string path, bool recursive = false)
	{
		if (!Directory.Exists(path))
		{
			return Array.Empty<string>();
		}

		var options = new EnumerationOptions
		{
			RecurseSubdirectories = recursive, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false,
		};
		return Directory.EnumerateFiles(path, "*", options).OrderBy(f => f, StringComparer.Ordinal).ToList();
	}

	/// <summary>When a save was last written: its main save, else its newest file, else the folder.</summary>
	private static DateTime PackageTime(string directory)
	{
		string main = Path.Combine(directory, Engine_SaveDirectory.MainSave);
		return File.Exists(main) ? File.GetLastWriteTimeUtc(main) : NewestWrite(directory);
	}

	private static DateTime NewestWrite(string directory)
	{
		DateTime newest = Directory.GetLastWriteTimeUtc(directory);
		foreach (string file in SafeFiles(directory, recursive: true))
		{
			DateTime when = File.GetLastWriteTimeUtc(file);
			if (when > newest) newest = when;
		}

		return newest;
	}

	private static string Verb(Source source) => source.Owned ? "moved" : "copied";

	private static void WriteMarker(string sourceRoot, string savesRoot, string profile, DateTime now, Report report)
	{
		try
		{
			File.WriteAllText(Path.Combine(sourceRoot, MarkerFileName),
				$"The saves that were here moved to {profile} on {now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}.\n" +
				$"Every move is listed in {Path.Combine(savesRoot, Engine_SaveLayout.MigrationLogName)}. Anything still here was\n" +
				"identical to a save already moved, or is not a save. Nothing here is read any more.\n",
				new UTF8Encoding(false));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			report.Log.Add($"could not write {Path.Combine(sourceRoot, MarkerFileName)}: {exception.Message}");
		}
	}

	private static void AppendLog(string savesRoot, DateTime now, Report report)
	{
		string stamp = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
		var text = new StringBuilder();
		foreach (string line in report.Log)
		{
			text.Append(stamp).Append("  ").Append(line).Append('\n');
		}

		File.AppendAllText(Path.Combine(savesRoot, Engine_SaveLayout.MigrationLogName), text.ToString(), new UTF8Encoding(false));
	}
}
