namespace Refii.Tests.Camera;

using System;
using System.IO;
using GdUnit4;
using Refii.Entities;
using Refii.Host;
using static GdUnit4.Assertions;

/// <summary>
/// A camera's fade is its own view's: player two's camera keeps its own book and writes no guest camera alpha (both
/// titles' core camera chunks), states the book to the shell, and each split half draws its own camera's fade.
/// </summary>
[TestSuite]
public class CameraFadeViewTests
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

	private static HostBridge.EntityAppearance Look(float alpha, float camera) =>
		HostBridge.EntityAppearance.Constructed with { Alpha = alpha, CameraAlpha = camera };

	[TestCase]
	public void EachHalfTakesItsOwnCamerasFade()
	{
		// Player one's camera faded the crate to 0.4; player two's never did: opaque in player two's half.
		(float one, float two) = Engine_ViewFade.Opacities(Look(1f, 0.4f), null);
		AssertThat(one).IsEqual(0.4f);
		AssertThat(two).IsEqual(1f);
		AssertThat(Engine_ViewFade.Differs(one, two)).IsTrue();

		// Player two's camera faded it to 0.3; player one's never did.
		(one, two) = Engine_ViewFade.Opacities(Look(1f, 1f), 0.3f);
		AssertThat(one).IsEqual(1f);
		AssertThat(two).IsEqual(0.3f);

		// The gameplay alpha reaches both halves (a dying body fades in each).
		(one, two) = Engine_ViewFade.Opacities(Look(0.5f, 1f), 0.5f);
		AssertThat(one).IsEqual(0.5f);
		AssertThat(two).IsEqual(0.25f);
	}

	[TestCase]
	public void AlikeWithinOneStepOfTheBlendIsOneDrawing()
	{
		AssertThat(Engine_ViewFade.Differs(1f, 1f)).IsFalse();
		AssertThat(Engine_ViewFade.Differs(0.5f, 0.5f + 0.5f / 255f)).IsFalse();
		AssertThat(Engine_ViewFade.Differs(0.5f, 0.5f + 1.5f / 255f)).IsTrue();
		// An entity in neither book is constructed: both halves draw it opaque, no second drawing.
		(float one, float two) = Engine_ViewFade.Opacities(HostBridge.EntityAppearance.Constructed, null);
		AssertThat(Engine_ViewFade.Differs(one, two)).IsFalse();
	}

	[TestCase]
	public void AFadeAtTheEdgeOfItsRadiusDoesNotChurn()
	{
		// Player one's camera ramps the alpha across 1 at the edge of its radius: 1, 0.99, 1, 0.99 ...
		bool perView = false;
		int alike = 0, flips = 0;
		float[] stated = { 1f, 0.99f, 1f, 0.996f, 1f, 0.99f, 1f, 1f, 0.99f, 1f };
		for (int walk = 0; walk < 200; walk++)
		{
			bool was = perView;
			(perView, alike) = Engine_ViewFade.Hold(perView, alike, stated[walk % stated.Length], 1f);
			flips += was != perView ? 1 : 0;
		}

		AssertThat(flips).IsLessEqual(1);   // started once and held
		AssertThat(perView).IsTrue();
	}

	[TestCase]
	public void AFadeThatEndsLetsGoAfterItsHalvesStayAlike()
	{
		(bool perView, int alike) = Engine_ViewFade.Hold(false, 0, 0.5f, 1f);
		AssertThat(perView).IsTrue();
		for (int walk = 1; walk < Engine_ViewFade.LeaveWalks; walk++)
		{
			(perView, alike) = Engine_ViewFade.Hold(perView, alike, 1f, 1f);
			AssertThat(perView).IsTrue();
		}

		(perView, alike) = Engine_ViewFade.Hold(perView, alike, 1f, 1f);
		AssertThat(perView).IsFalse();
		AssertThat(alike).IsEqual(0);
		// Below the start step a difference never starts a drawing per half.
		AssertThat(Engine_ViewFade.Hold(false, 0, 1f, 1f - 1.5f / 255f).PerView).IsFalse();
	}

	[TestCase]
	public void TransparencyIsTheBlendBelowOpaque()
	{
		AssertThat(Engine_ViewFade.TransparencyFor(1f)).IsEqual(0f);
		AssertThat(Engine_ViewFade.TransparencyFor(0.9995f)).IsEqual(0f);
		AssertThat(Engine_ViewFade.TransparencyFor(0.25f)).IsEqual(0.75f);
	}

	[TestCase]
	public void InstanceUniformsAreNamedFromTheShaderSource()
	{
		const string body = "shader_type spatial;\n#include \"res://a.gdshaderinc\"\ninstance uniform vec4 entity_tint = vec4(1.0);\n" +
			"uniform float not_instance;\ninstance uniform highp float fable_ambient_scale : hint_range(0, 4) = 1.0;\n";
		const string included = "#include \"res://a.gdshaderinc\"\ninstance uniform vec4 probe_r;\ninstance uniform vec4 entity_tint;\n";
		var names = Refii.Core.Engine_InstanceUniformNames.Parse(body, path => path == "res://a.gdshaderinc" ? included : null);
		AssertThat(string.Join(",", names)).IsEqual("probe_r,entity_tint,fable_ambient_scale");
	}

	[TestCase]
	public void NoTwinAsksTheServerForItsParameterList()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		// The render thread fills that list after the caller has returned under 4.7.1's separate render thread
		// (int7/crash_1654.txt): a per-view drawing names its instance uniforms from the shader source.
		foreach (string file in Directory.GetFiles(Path.Combine(root, "Godot", "src", "Engine", "entities"), "*.cs"))
		{
			AssertThat(File.ReadAllText(file).Contains("InstanceGeometryGetShaderParameterList", StringComparison.Ordinal))
				.OverrideFailureMessage(file + " asks the server for an instance's parameter list").IsFalse();
		}
	}

	[TestCase]
	public void PlayerTwosCameraWritesNoGuestFieldOnEitherTitle()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string lua2 = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable2", "host", "CoopHeroLua.cs"));
		AssertThat(lua2.Contains("for _, name in ipairs({ \"FadeEntityBasedOnPositionIfNeeded\", \"FadeUpEntitiesIfNeeded\" }) do", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua2.Contains("CameraBase.FadedEntities = C.PartnerFadeBook()", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua2.Contains("C.SetCameraAlpha = C.Permanent(function(...)\n\t\t\t\tif C.PartnerCameraUpdating() then\n\t\t\t\t\treturn\n\t\t\t\tend", StringComparison.Ordinal)).IsTrue();

		string lua3 = File.ReadAllText(Path.Combine(root, "Godot", "src", "Titles", "Fable3", "Fable3CoreLua.cs"));
		AssertThat(lua3.Contains("for _, name in ipairs({ \"FadeEntityBasedOnPositionIfNeeded\", \"FadeUpEntitiesIfNeeded\" }) do", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua3.Contains("CameraBase.FadedEntities = CC.FadeBook()", StringComparison.Ordinal)).IsTrue();
		AssertThat(lua3.Contains("Refii._coop_camera_alpha = function(...)\n\t\t\tif CC.PartnerUpdating() then return end", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void BothTitlesStateTheBookToTheShell()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string f2 = File.ReadAllText(Path.Combine(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp"));
		AssertThat(f2.Contains("{\"_partner_camera_alphas\", &NativePartnerCameraAlphas, 0},", StringComparison.Ordinal)).IsTrue();
		AssertThat(f2.Contains("refii_bridge_partner_camera_alphas_set(entities, alphas, count);", StringComparison.Ordinal)).IsTrue();
		string f3 = File.ReadAllText(Path.Combine(root, "Fable3", "src", "hooks", "src", "mod_lua_hooks.cpp"));
		AssertThat(f3.Contains("{\"_partner_camera_alphas\", &NativePartnerCameraAlphas, 0u},", StringComparison.Ordinal)).IsTrue();
		AssertThat(f3.Contains("refii_bridge_partner_camera_alphas_set(entities, alphas, count);", StringComparison.Ordinal)).IsTrue();
		string exports = File.ReadAllText(Path.Combine(root, "src", "hostcore", "src", "host_exports.cpp"));
		AssertThat(exports.Contains("REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPartnerCameraAlphas(", StringComparison.Ordinal)).IsTrue();
		AssertThat(exports.Contains("RefiiHostBridgeAbiVersion(void) { return " + HostBridge.ExpectedAbiVersion + "; }", StringComparison.Ordinal)).IsTrue();
	}
}
