namespace Refii.Tests.Camera;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// Fable 2 split screen: the particle systems' level of detail answers either player's camera
/// (Fable2/src/hooks/src/coop_particle_view.cpp). Native code: the rows and the steps are read from the source,
/// both builds.
/// </summary>
[TestSuite]
public class Fable2ParticleViewTests
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

	private static readonly (string Name, uint Tu1, uint Disc)[] Hooked =
	{
		("rex_lhWorld_Frame", 0x821DFDD0u, 0x821DF990u),
		("rex_ParticleView_SetCamera", 0x832318C8u, 0x8322B050u),
		("rex_ParticleSystem_LevelOfDetail", 0x83233EF0u, 0x8322D678u),
	};

	private static void InOrder(string body, string[] steps)
	{
		int at = 0;
		foreach (string step in steps)
		{
			int next = body.IndexOf(step, at, StringComparison.Ordinal);
			AssertThat(next >= at).OverrideFailureMessage(step).IsTrue();
			at = next + step.Length;
		}
	}

	[TestCase]
	public void TheThreeSeamsAreNamedOnBothBuilds()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string tu1 = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2TU1", "refii_config.toml"));
		string disc = File.ReadAllText(Path.Combine(root, "Fable2", "versions", "Fable2", "refii_config.toml"));
		string hooks = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "coop_particle_view.cpp"));
		foreach ((string name, uint a, uint b) in Hooked)
		{
			AssertThat(tu1.Contains($"0x{a:X8} = {{ name = \"{name}\" }}\n", StringComparison.Ordinal)).OverrideFailureMessage("TU1 " + name).IsTrue();
			AssertThat(disc.Contains($"0x{b:X8} = {{ name = \"{name}\" }}\n", StringComparison.Ordinal)).OverrideFailureMessage("disc " + name).IsTrue();
			AssertThat(tu1.Split("\"" + name + "\"").Length).IsEqual(2);
			AssertThat(disc.Split("\"" + name + "\"").Length).IsEqual(2);
			AssertThat(hooks.Contains("REX_EXTERN(" + name + ") {", StringComparison.Ordinal)).OverrideFailureMessage("hook " + name).IsTrue();
		}

		string cmake = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "CMakeLists.txt"));
		AssertThat(cmake.Contains("src/coop_particle_view.cpp", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void PlayerTwosViewIsTheGuestsOwnSetterOnPlayerTwosBlock()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string hooks = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "coop_particle_view.cpp"));
		int frameAt = hooks.IndexOf("REX_EXTERN(rex_lhWorld_Frame) {", StringComparison.Ordinal);
		int setAt = hooks.IndexOf("REX_EXTERN(rex_ParticleView_SetCamera) {", StringComparison.Ordinal);
		int detailAt = hooks.IndexOf("REX_EXTERN(rex_ParticleSystem_LevelOfDetail) {", StringComparison.Ordinal);
		AssertThat(frameAt > 0 && setAt > frameAt && detailAt > setAt).IsTrue();

		// Not split: the shipped frame, the stack untouched.
		InOrder(hooks.Substring(frameAt, setAt - frameAt), new[]
		{
			"if (t_slot != 0u || RefiiCoopSplitScreen() == 0) {\n    __imp__rex_lhWorld_Frame(ctx, base);\n    return;",
			"t_slot = work.r1.u32;\n  __imp__rex_lhWorld_Frame(work, base);\n  t_slot = 0u;\n  t_world_view = 0u;\n  t_second_view = 0u;",
		});
		InOrder(hooks.Substring(setAt, detailAt - setAt), new[]
		{
			"__imp__rex_ParticleView_SetCamera(ctx, base);",
			"if (slot == 0u || !Plausible(view)) {\n    return;",
			"RefiiCoopSplitPartnerCameraHolder(base);",
			"call.r4.u32 = holder;\n  __imp__rex_lhCamera_ScreenBlock(call, base);",
			"std::memcpy(base + second, base + view, kViewBytes);",
			"LoadVector(set.v1, base, block + 0x00u);\n  LoadVector(set.v2, base, block + 0x20u);\n  set.f1.f64 = LoadF32(base, block + 0x30u);\n  __imp__rex_ParticleView_SetCamera(set, base);",
			"t_world_view = view;\n  t_second_view = second;",
		});
		InOrder(hooks.Substring(detailAt), new[]
		{
			"if (second == 0u || ctx.r4.u32 != t_world_view || !Plausible(holder)) {\n    __imp__rex_ParticleSystem_LevelOfDetail(ctx, base);\n    return;",
			"if (state == 0u) {\n    return;",
			"system + 0x18u",
			"refii::vfs::StoreGuestU32(base, holder, system);\n  refii::vfs::StoreGuestU32(base, holder + 4u, count);",
			"AddReference(base, count);",
			"again.r4.u32 = second;",
			"if (refii::vfs::LoadGuestU32(base, system + 8u) > state) {",
		});
	}

	[TestCase]
	public void TheListenerAndTheParticlesAskOneHolder()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string heroes = File.ReadAllText(Path.Combine(root, "Fable2", "src", "hooks", "src", "coop_hero_hooks.cpp"));
		int defined = heroes.IndexOf("extern \"C\" uint32_t RefiiCoopSplitPartnerCameraHolder(uint8_t* base) {", StringComparison.Ordinal);
		AssertThat(defined > 0).IsTrue();
		InOrder(heroes.Substring(defined), new[]
		{
			"RefiiCoopSplitScreen() == 0",
			"PlayerForKey(base, 2u)",
			"manager != g_second_manager_updated.load(std::memory_order_acquire)",
		});
		int listener = heroes.IndexOf("REX_EXTERN(rex_lhAudio_SetListener) {", StringComparison.Ordinal);
		AssertThat(listener > defined).IsTrue();
		InOrder(heroes.Substring(listener), new[]
		{
			"const uint32_t holder = RefiiCoopSplitPartnerCameraHolder(base);",
			"call.r4.u32 = holder;\n  __imp__rex_lhCamera_ScreenBlock(call, base);",
		});
	}
}
