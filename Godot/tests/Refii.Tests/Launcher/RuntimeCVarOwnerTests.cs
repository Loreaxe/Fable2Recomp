namespace Refii.Tests.Launcher;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using Refii.Core;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

/// <summary>
/// WHICH TITLE A SETTING APPLIES TO IS EVIDENCE, read back from the source tree: a native cvar's
/// owner is where its <c>REXCVAR_DEFINE_*</c> sits (a title's <c>src/</c>, or the shared SDK and
/// host), and a Godot setting read only by one title's code is that title's. A title's screen and
/// file carry only the settings that apply to it.
/// </summary>
[TestSuite]
public class RuntimeCVarOwnerTests
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

	private static readonly Regex CVarDefine = new(@"REXCVAR_DEFINE_[A-Z0-9_]+\(\s*([A-Za-z0-9_]+)\s*,", RegexOptions.Compiled);

	private static readonly string[] NativeExtensions = { ".cpp", ".h", ".hpp", ".inc", ".cc" };

	/// <summary>Each natively defined cvar name, and the top-level folders its definitions sit in.</summary>
	private static Dictionary<string, HashSet<string>> NativeDefinitionSites(string root)
	{
		var sites = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		string sep = Path.DirectorySeparatorChar.ToString();
		var roots = new List<string>
		{
			Path.Combine(root, "src"),
			Path.Combine(root, "rexglue-net", "rexglue-sdk", "src"),
			Path.Combine(root, "rexglue-net", "rexglue-sdk", "include"),
			Path.Combine(root, "rexglue-net", "shadow"),
		};
		roots.AddRange(Engine_Title.All.Select(title => Path.Combine(root, title.Folder, "src")));
		foreach (string top in roots.Where(Directory.Exists))
		{
			foreach (string file in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories))
			{
				if (!NativeExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) ||
					file.Contains(sep + "thirdparty" + sep, StringComparison.Ordinal) ||
					file.Contains(sep + "generated" + sep, StringComparison.Ordinal) ||
					file.Contains(sep + "build" + sep, StringComparison.Ordinal))
				{
					continue;
				}

				string folder = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)[0];
				foreach (Match match in CVarDefine.Matches(File.ReadAllText(file)))
				{
					if (!sites.TryGetValue(match.Groups[1].Value, out HashSet<string>? set))
					{
						sites[match.Groups[1].Value] = set = new HashSet<string>(StringComparer.Ordinal);
					}

					set.Add(folder);
				}
			}
		}

		return sites;
	}

	[TestCase]
	public void EveryOwnerIsATitle()
	{
		var folders = Engine_Title.All.Select(title => title.Folder).ToHashSet(StringComparer.Ordinal);
		foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All)
		{
			if (definition.OwnerTitle is { } owner)
			{
				AssertThat(folders.Contains(owner))
					.OverrideFailureMessage(definition.Name + " is owned by " + owner + ", which is no title").IsTrue();
			}
		}
	}

	[TestCase]
	public void ANativeCVarIsOwnedWhereItIsDefined()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();
		Dictionary<string, HashSet<string>> sites = NativeDefinitionSites(root!);
		var folders = Engine_Title.All.Select(title => title.Folder).ToHashSet(StringComparer.Ordinal);

		var offenders = new List<string>();
		int checkedCount = 0;
		foreach (RuntimeCVarDefinition definition in RuntimeCVars.Definitions.Concat(RuntimeCVarsGodot.Definitions))
		{
			if (!sites.TryGetValue(definition.Name, out HashSet<string>? where))
			{
				continue;
			}

			++checkedCount;
			string? expected = where.Count == 1 && folders.Contains(where.First()) ? where.First() : null;
			if (definition.OwnerTitle != expected)
			{
				offenders.Add($"{definition.Name}: owner {definition.OwnerTitle ?? "shared"}, defined in {string.Join("+", where)}");
			}
		}

		AssertThat(checkedCount > 100).OverrideFailureMessage($"only {checkedCount} native definitions found").IsTrue();
		AssertThat(offenders.Count)
			.OverrideFailureMessage("owners disagree with the definition sites: " + string.Join(", ", offenders.Take(20)))
			.IsEqual(0);
	}

	[TestCase]
	public void AGodotSettingReadOnlyByATitleIsThatTitles()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();
		string src = Path.Combine(root!, "Godot", "src");
		string settings = Path.Combine(src, "launcher", "settings") + Path.DirectorySeparatorChar;
		string titles = Path.Combine(src, "Titles") + Path.DirectorySeparatorChar;
		var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
			.Where(file => !file.StartsWith(settings, StringComparison.Ordinal))
			.Select(file => (Path: file, Text: File.ReadAllText(file)))
			.ToList();

		var offenders = new List<string>();
		foreach (RuntimeCVarDefinition definition in RuntimeCVarsGodot.Definitions)
		{
			string literal = "\"" + definition.Name + "\"";
			var readers = files.Where(file => file.Text.Contains(literal, StringComparison.Ordinal)).ToList();
			if (readers.Count == 0)
			{
				continue;
			}

			var owners = readers
				.Select(file => file.Path.StartsWith(titles, StringComparison.Ordinal)
					? file.Path[titles.Length..].Split(Path.DirectorySeparatorChar)[0]
					: null)
				.Distinct()
				.ToList();
			if (owners.Count == 1 && owners[0] is { } only && definition.OwnerTitle != only)
			{
				offenders.Add($"{definition.Name}: read only under Titles/{only}, owner {definition.OwnerTitle ?? "shared"}");
			}
		}

		AssertThat(offenders.Count)
			.OverrideFailureMessage("a title's own setting is marked otherwise: " + string.Join(", ", offenders))
			.IsEqual(0);
	}

	[TestCase]
	public void EachTitleListsOnlyItsOwnSettings()
	{
		IReadOnlyList<RuntimeCVarDefinition> fable2 = RuntimeCVarSet.For(Engine_Title.Fable2);
		IReadOnlyList<RuntimeCVarDefinition> fable3 = RuntimeCVarSet.For(Engine_Title.Fable3);

		foreach (string fable2Only in new[]
				 {
					 "vfs_trace_vfs", "vfs_loose_asset_root", "lua_trace_script_loads", "developer_menu_enabled",
					 "pubgame_vector_shapes", "pubgame_raster_scale_percent",
					 "decorate_mode_enabled",
				 })
		{
			AssertThat(fable2.Any(d => d.Name == fable2Only)).OverrideFailureMessage(fable2Only + " missing from Fable 2").IsTrue();
			AssertThat(fable3.Any(d => d.Name == fable2Only)).OverrideFailureMessage(fable2Only + " listed for Fable 3").IsFalse();
		}

		foreach (string shared in new[]
				 {
					 "log_level", "user_language", "camera_invert_y", "lighting_preset", "hdr_output",
					 "coop_screen_mode", "coop_split_screen_orientation",
					 "display_resolution", "mouse_input", "texture_upscaling_enabled",
					 "mouse_sensitivity", "discord_presence", "fx_live_effects",
				 })
		{
			AssertThat(fable2.Any(d => d.Name == shared)).OverrideFailureMessage(shared + " missing from Fable 2").IsTrue();
			AssertThat(fable3.Any(d => d.Name == shared)).OverrideFailureMessage(shared + " missing from Fable 3").IsTrue();
		}

		AssertThat(fable3.All(d => d.AppliesTo(Engine_Title.Fable3))).IsTrue();
		AssertThat(fable2.All(d => d.AppliesTo(Engine_Title.Fable2))).IsTrue();
	}
}
