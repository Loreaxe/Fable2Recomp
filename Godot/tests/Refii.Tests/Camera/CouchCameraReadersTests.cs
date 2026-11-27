namespace Refii.Tests.Camera;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// Camera stage C2f, Fable 3: each couch player's camera for the sim's readers while split — the stick jab
/// bracketed, "the" camera's cached block answering the other screen's inside a bracket, the other screen's
/// block being player two's (its +0x46 test, its bracket), every two-screen test of its consumers answered,
/// and Kaim's 50 m filters asked for either view. Rows on both builds; the run is the game lane's (NOTES.md).
/// </summary>
[TestSuite]
public class CouchCameraReadersTests
{
	private static string? RepoRoot()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null; i++)
		{
			if (File.Exists(Path.Combine(at, "Fable2", "versions", "Fable2TU1", "refii_config.toml")))
			{
				return at;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	private static string Midasm(string name, string address, string? register, string target) =>
		"[[midasm_hook]]\nname = \"" + name + "\"\naddress = " + address + "\n" +
		(register is null ? string.Empty : "registers = [\"" + register + "\"]\n") +
		"jump_address_on_true = " + target + "\n";

	[TestCase]
	public void BothBuildsNameTheReadersAndTheTwoScreenTests()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		foreach ((string build, string[] functions, (string At, string To) here, (string At, string To)[] two) in new[]
		{
			("Fable3TU3",
				new[] { "0x82934470", "0x82221610", "0x82560180", "0x8275ACF8", "0x8275AFA8" },
				("0x8256021C", "0x82560220"),
				new[] { ("0x821F19E0", "0x821F19EC"), ("0x821FE1C4", "0x821FE1D4"), ("0x82194038", "0x8219403C"),
					("0x822093C0", "0x822093C4"), ("0x8223395C", "0x82233960") }),
			("Fable3",
				new[] { "0x828DB6B8", "0x82222728", "0x8252FC70", "0x82726978", "0x82726C28" },
				("0x8252FD0C", "0x8252FD10"),
				new[] { ("0x821F23B0", "0x821F23BC"), ("0x821FE9D4", "0x821FE9E4"), ("0x82193D70", "0x82193D74"),
					("0x82209DE0", "0x82209DE4"), ("0x82234BA4", "0x82234BA8"), ("0x821D6FA4", "0x821D6FA8") }),
		})
		{
			string config = File.ReadAllText(Path.Combine(root, "Fable3", "versions", build, "refiii_config.toml"));
			string[] names =
			{
				"rex_CInputProcessStickJabDetection_ProcessInput", "rex_Camera_CachedBlock", "rex_Camera_OtherScreenBlock",
				"rex_Kaim_CEntityInfoFilterF2Visibility_Filter", "rex_Kaim_CEntityInfoFilterF2Sight_Filter",
			};
			for (int i = 0; i < names.Length; i++)
			{
				AssertThat(config.Contains(functions[i] + " = { name = \"" + names[i] + "\" }", StringComparison.Ordinal))
					.OverrideFailureMessage(build + " " + names[i]).IsTrue();
			}

			AssertThat(config.Contains(Midasm("RefiiF3OtherScreenIsHere", here.At, "r4", here.To), StringComparison.Ordinal))
				.OverrideFailureMessage(build + " other screen").IsTrue();
			foreach ((string at, string to) in two)
			{
				AssertThat(config.Contains(Midasm("RefiiF3TwoScreens", at, null, to), StringComparison.Ordinal))
					.OverrideFailureMessage(build + " two screens " + at).IsTrue();
			}
		}

		string cmake = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "CMakeLists.txt"));
		AssertThat(cmake.Contains("${CMAKE_CURRENT_SOURCE_DIR}/src/coop_camera_readers.cpp", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void EveryAnswerNeedsTheSplitOrABracket()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string readers = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "coop_camera_readers.cpp"));
		foreach (string line in new[]
		{
			"if (!coopcam::SplitInEffect(GuestBase())) {\n    return false;\n  }",
			"return coopcam::PartnerDepth() > 0u && coopcam::SplitInEffect(base) && r4.u32 != 0u &&\n         r4.u32 == coopcam::PartnerHolder(base);",
			"const coopcam::PartnerScope scope(coopcam::PartnerDepth() == 0u && coopcam::SplitInEffect(base));",
			"if (coopcam::PartnerDepth() > 0u && coopcam::SplitInEffect(base)) {\n    __imp__rex_Camera_OtherScreenBlock(ctx, base);\n    return;\n  }",
			"const bool partner = coopcam::SplitInEffect(base) && coopcam::IsPartnerHero(base, entity);",
			"if (ctx.r3.u32 == 0u || coopcam::PartnerDepth() > 0u || !coopcam::SplitInEffect(base)) {\n    return;\n  }",
			"if (again.r3.u32 == 0u) {\n    ctx.r3.u64 = 0u;",
		})
		{
			AssertThat(readers.Contains(line, StringComparison.Ordinal)).OverrideFailureMessage(line).IsTrue();
		}

		// The split itself needs a couch game and player two's camera manager.
		string hooks = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "coop_camera_hooks.cpp"));
		AssertThat(hooks.Contains("return base != nullptr && g_split.load(std::memory_order_relaxed) && ::PartnerHolder(base, Players(base)) != 0u;", StringComparison.Ordinal)).IsTrue();
		AssertThat(hooks.Contains("if (!Couch(base)) {\n    return 0u;\n  }", StringComparison.Ordinal)).IsTrue();
	}
}
