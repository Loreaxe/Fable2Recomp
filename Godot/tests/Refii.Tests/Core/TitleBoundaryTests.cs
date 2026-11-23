namespace Refii.Tests.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// THE SHELL NEVER NAMES A TITLE. Shared C# (Godot/addons, and everything under Godot/src outside Titles/) must not
/// reference <c>Refii.Titles.*</c>; each title's C# lives in its own namespace and names no other
/// title; and the shared native build (src/, cmake/) reaches into no title folder. Read from the
/// source tree, so a dependency in the wrong direction fails here rather than surviving as a habit.
/// </summary>
[TestSuite]
public class TitleBoundaryTests
{
	private static string? RepoRoot()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null; i++)
		{
			if (File.Exists(Path.Combine(at, "Godot", "project.godot")) &&
				Directory.Exists(Path.Combine(at, "Fable2")))
			{
				return at;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	private static readonly Regex TitleNamespace = new(@"\bRefii\.Titles\.(\w+)", RegexOptions.Compiled);

	private static IEnumerable<string> CSharpUnder(string directory) =>
		Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories);

	[TestCase]
	public void SharedCSharpNamesNoTitle()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();
		string src = Path.Combine(root!, "Godot", "src");
		string titles = Path.Combine(src, "Titles") + Path.DirectorySeparatorChar;

		var offenders = new List<string>();
		IEnumerable<string> shared = CSharpUnder(src).Where(f => !f.StartsWith(titles, StringComparison.Ordinal))
			.Concat(CSharpUnder(Path.Combine(root!, "Godot", "addons")));
		foreach (string file in shared)
		{
			string[] lines = File.ReadAllLines(file);
			for (int i = 0; i < lines.Length; i++)
			{
				if (TitleNamespace.IsMatch(lines[i]))
				{
					offenders.Add(Path.GetRelativePath(root!, file) + ":" + (i + 1));
				}
			}
		}

		AssertThat(offenders.Count)
			.OverrideFailureMessage("shared code names a title: " + string.Join(", ", offenders.Take(20)))
			.IsEqual(0);
	}

	[TestCase]
	public void EachTitleKeepsToItsOwnNamespace()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();
		string titles = Path.Combine(root!, "Godot", "src", "Titles");
		AssertThat(Directory.Exists(titles)).OverrideFailureMessage("no Godot/src/Titles").IsTrue();

		var offenders = new List<string>();
		foreach (string titleDirectory in Directory.EnumerateDirectories(titles))
		{
			string folder = Path.GetFileName(titleDirectory);
			foreach (string file in CSharpUnder(titleDirectory))
			{
				string text = File.ReadAllText(file);
				string relative = Path.GetRelativePath(root!, file);
				if (!Regex.IsMatch(text, @"^namespace Refii\.Titles\." + Regex.Escape(folder) + @"\b", RegexOptions.Multiline))
				{
					offenders.Add(relative + " (not in Refii.Titles." + folder + ")");
				}

				foreach (Match match in TitleNamespace.Matches(text))
				{
					if (match.Groups[1].Value != folder)
					{
						offenders.Add(relative + " (names Refii.Titles." + match.Groups[1].Value + ")");
					}
				}
			}
		}

		AssertThat(offenders.Count)
			.OverrideFailureMessage("title namespace broken: " + string.Join(", ", offenders.Take(20)))
			.IsEqual(0);
	}

	private static readonly Regex GuestAddressRead =
		new(@"\b(?:GuestAddress|Peek\w*|TryPeekWord)\(\s*0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);

	/// <summary>
	/// A guest address is one title's binary: shared C# asks the title's bridge or the title's
	/// contract for what lives there and never reads one itself.
	/// </summary>
	[TestCase]
	public void SharedCSharpReadsNoGuestAddress()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();
		string src = Path.Combine(root!, "Godot", "src");
		string titles = Path.Combine(src, "Titles") + Path.DirectorySeparatorChar;

		var offenders = new List<string>();
		foreach (string file in CSharpUnder(src).Where(f => !f.StartsWith(titles, StringComparison.Ordinal))
			.Concat(CSharpUnder(Path.Combine(root!, "Godot", "addons"))))
		{
			string[] lines = File.ReadAllLines(file);
			for (int i = 0; i < lines.Length; i++)
			{
				string code = lines[i].Split("//")[0];
				if (GuestAddressRead.IsMatch(code))
				{
					offenders.Add(Path.GetRelativePath(root!, file) + ":" + (i + 1));
				}
			}
		}

		AssertThat(offenders.Count)
			.OverrideFailureMessage("shared code reads a guest address: " + string.Join(", ", offenders.Take(20)))
			.IsEqual(0);
	}

	private static readonly Regex RigNameLiteral = new(@"""Shadow_", RegexOptions.Compiled);

	private static readonly Regex HexKeyLiteral = new(@"\b0x[0-9A-Fa-f]{8}u?\b", RegexOptions.Compiled);

	private static readonly Regex ClipKeyContext = new(@"(?i)clip|\bEnsure\(", RegexOptions.Compiled);

	private static readonly Regex TitleComparison = new(@"\bEngine_Title\.(?:Fable2|Fable3)\b", RegexOptions.Compiled);

	/// <summary>
	/// Files allowed a title literal in the scanned code, as "relative/path.cs: why". Empty: a rig
	/// name, a clip key or (in the rig code) a title test belongs to the title's
	/// <c>Engine_TitleRig</c>, and the files whose job is naming titles
	/// (<c>Godot/src/Engine/core/Engine_Title*.cs</c>) name no rig or clip either.
	/// </summary>
	private static readonly string[] RigLiteralExemptions = Array.Empty<string>();

	/// <summary>A line with its <c>//</c> comment cut off, string and char literals kept whole.</summary>
	private static string CodeOf(string line)
	{
		bool inString = false;
		bool verbatim = false;
		for (int i = 0; i < line.Length; i++)
		{
			char c = line[i];
			if (inString)
			{
				if (c == '\\' && !verbatim)
				{
					i++;
				}
				else if (c == '"')
				{
					inString = false;
				}

				continue;
			}

			if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
			{
				return line[..i];
			}

			if (c == '\'')
			{
				int from = i + (i + 1 < line.Length && line[i + 1] == '\\' ? 3 : 2);
				int close = from < line.Length ? line.IndexOf('\'', from) : -1;
				if (close > i && close - i <= 4)
				{
					i = close;
				}

				continue;
			}

			if (c == '"')
			{
				inString = true;
				verbatim = (i > 0 && line[i - 1] == '@') || (i > 1 && line[i - 2] == '@');
			}
		}

		return line;
	}

	/// <summary>
	/// THE SHARED CODE NAMES NO TITLE'S RIG. Everything under Godot/src/Engine, Godot/src/game and
	/// Godot/src/host asks the title's <c>Engine_TitleRig</c> for a bone by role and a face's clips;
	/// a <c>"Shadow_*</c> name or an 8-digit clip key there is one title's knowledge in shared code,
	/// and quietly does nothing on the other. The rig code (the animation and entity code and the
	/// entity walk) also asks it for the guest's pick rules, so a test of which title is running is
	/// one title's rig knowledge there; elsewhere it is a format's layout, which is not a rig's.
	/// </summary>
	[TestCase]
	public void SharedRigCodeNamesNoTitleRig()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();
		string src = Path.Combine(root!, "Godot", "src");
		IEnumerable<string> scanned = CSharpUnder(Path.Combine(src, "Engine"))
			.Concat(CSharpUnder(Path.Combine(src, "game")))
			.Concat(CSharpUnder(Path.Combine(src, "host")));
		string animation = Path.Combine(src, "Engine", "animation") + Path.DirectorySeparatorChar;
		string entities = Path.Combine(src, "Engine", "entities") + Path.DirectorySeparatorChar;
		string entityWalk = Path.Combine(src, "game", "GameEntities.cs");
		var exempt = new HashSet<string>(
			RigLiteralExemptions.Select(e => e.Split(':')[0].Trim().Replace('/', Path.DirectorySeparatorChar)),
			StringComparer.Ordinal);

		var offenders = new List<string>();
		int files = 0;
		int rigFiles = 0;
		foreach (string file in scanned)
		{
			string relative = Path.GetRelativePath(root!, file);
			if (exempt.Contains(relative))
			{
				continue;
			}

			++files;
			bool rigCode = file.StartsWith(animation, StringComparison.Ordinal) ||
				file.StartsWith(entities, StringComparison.Ordinal) ||
				string.Equals(file, entityWalk, StringComparison.Ordinal);
			rigFiles += rigCode ? 1 : 0;
			string[] lines = File.ReadAllLines(file);
			for (int i = 0; i < lines.Length; i++)
			{
				string code = CodeOf(lines[i]);
				string? why = RigNameLiteral.IsMatch(code) ? "rig name"
					: rigCode && TitleComparison.IsMatch(code) ? "title test"
					: HexKeyLiteral.IsMatch(code) && ClipKeyContext.IsMatch(code) ? "clip key"
					: null;
				if (why is not null)
				{
					offenders.Add(relative + ":" + (i + 1) + " (" + why + ")");
				}
			}
		}

		AssertThat(files > 500).OverrideFailureMessage($"only {files} files scanned").IsTrue();
		AssertThat(rigFiles > 50).OverrideFailureMessage($"only {rigFiles} rig code files scanned").IsTrue();
		AssertThat(offenders.Count)
			.OverrideFailureMessage("shared code names a title's rig: " + string.Join(", ", offenders.Take(20)))
			.IsEqual(0);
	}

	/// <summary>The scan's own reading: a literal in code is seen, the same text in a comment is not.</summary>
	[TestCase]
	public void RigScanReadsCodeNotComments()
	{
		AssertThat(RigNameLiteral.IsMatch(CodeOf("rig.TryBone(\"Shadow_Head\", out bone);"))).IsTrue();
		AssertThat(RigNameLiteral.IsMatch(CodeOf("int b = 0; // was \"Shadow_Head\""))).IsFalse();
		AssertThat(RigNameLiteral.IsMatch(CodeOf("string s = \"a // b\" + \"Shadow_Hip\";"))).IsTrue();
		string clip = CodeOf("private const uint PrimaryAmbientFaceClipKey = 0xE903635Fu;");
		AssertThat(HexKeyLiteral.IsMatch(clip) && ClipKeyContext.IsMatch(clip)).IsTrue();
		string hash = CodeOf("public const uint Animation = 0x8F32748Du;");
		AssertThat(HexKeyLiteral.IsMatch(hash) && ClipKeyContext.IsMatch(hash)).IsFalse();
		AssertThat(TitleComparison.IsMatch(CodeOf("bool f3 = Engine_Title.Active == Engine_Title.Fable3;"))).IsTrue();
	}

	private static readonly Regex NativeExport =
		new(@"REFII_HOST_ABI_EXPORT[^;{(]*?\b(Refii\w+)\s*\(", RegexOptions.Compiled);

	private static readonly Regex ManagedImport = new(
		@"\[(?:System\.Runtime\.InteropServices\.)?DllImport\((?<args>[^\]]*)\)\]\s*(?:\[[^\]]*\]\s*)*[\w\s]*\bstatic\s+extern\s+[^(]*?\b(?<name>Refii\w+)\s*\(",
		RegexOptions.Compiled);

	private static HashSet<string> ExportsIn(IEnumerable<string> files)
	{
		var names = new HashSet<string>(StringComparer.Ordinal);
		foreach (string file in files)
		{
			foreach (Match match in NativeExport.Matches(File.ReadAllText(file)))
			{
				names.Add(match.Groups[1].Value);
			}
		}

		return names;
	}

	/// <summary>
	/// THE SHELL BINDS ONE ABI. Every export the shared HostBridge and NetBridge declare is answered by every
	/// title's bridge — by the core, or by the title's own feeds — so a title switch never meets
	/// an entry point that is not there. And Fable 3's "nothing stated" answers are only for
	/// exports the shell still binds: one the shell dropped is deleted, not kept.
	/// </summary>
	[TestCase]
	public void EveryTitleBridgeAnswersTheShellsAbi()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();

		var bound = new HashSet<string>(StringComparer.Ordinal);
		string bridge = File.ReadAllText(Path.Combine(root!, "Godot", "src", "host", "HostBridge.cs")) +
			File.ReadAllText(Path.Combine(root!, "Godot", "src", "host", "NetBridge.cs")) +
			File.ReadAllText(Path.Combine(root!, "Godot", "src", "android", "HostBridge.Android.cs"));
		foreach (Match match in ManagedImport.Matches(bridge))
		{
			Match entry = Regex.Match(match.Groups["args"].Value, @"EntryPoint\s*=\s*""(\w+)""");
			bound.Add(entry.Success ? entry.Groups[1].Value : match.Groups["name"].Value);
		}

		AssertThat(bound.Count > 200).OverrideFailureMessage($"only {bound.Count} shell imports parsed").IsTrue();

		HashSet<string> core = ExportsIn(Directory.EnumerateFiles(Path.Combine(root!, "src", "hostcore", "src"), "*.cpp"));
		var offenders = new List<string>();
		foreach (string title in new[] { "Fable2", "Fable3" })
		{
			string feeds = Path.Combine(root!, title, "src", "hostbridge", "src");
			HashSet<string> answered = ExportsIn(Directory.EnumerateFiles(feeds, "*.cpp"));
			answered.UnionWith(core);
			offenders.AddRange(bound.Where(name => !answered.Contains(name)).Select(name => title + " lacks " + name));
		}

		HashSet<string> unstated = ExportsIn(new[] { Path.Combine(root!, "Fable3", "src", "hostbridge", "src", "unstated_feeds.cpp") });
		offenders.AddRange(unstated.Where(name => !bound.Contains(name)).Select(name => "Fable3 answers unbound " + name));
		offenders.AddRange(unstated.Where(core.Contains).Select(name => "Fable3 shadows the core's " + name));

		AssertThat(offenders.Count)
			.OverrideFailureMessage("the shell's ABI is not answered alike: " + string.Join(", ", offenders.Take(20)))
			.IsEqual(0);
	}

	[TestCase]
	public void SharedNativeBuildReachesIntoNoTitleFolder()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();

		var offenders = new List<string>();
		var files = Directory.EnumerateFiles(Path.Combine(root!, "src"), "CMakeLists.txt", SearchOption.AllDirectories)
			.Concat(Directory.EnumerateFiles(Path.Combine(root!, "src"), "*.cmake", SearchOption.AllDirectories))
			.Where(f => !f.Contains(Path.DirectorySeparatorChar + "thirdparty" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
				&& !f.Contains(Path.DirectorySeparatorChar + "assetbrowser" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
		foreach (string file in files)
		{
			string[] lines = File.ReadAllLines(file);
			for (int i = 0; i < lines.Length; i++)
			{
				string code = lines[i].Split('#')[0];
				if (Regex.IsMatch(code, @"\bFable[23]/"))
				{
					offenders.Add(Path.GetRelativePath(root!, file) + ":" + (i + 1));
				}
			}
		}

		AssertThat(offenders.Count)
			.OverrideFailureMessage("shared native build reaches into a title: " + string.Join(", ", offenders))
			.IsEqual(0);
	}
}
