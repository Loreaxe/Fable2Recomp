namespace Refii.Tests.Modding;

using System;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// The bundled mods that shape the camera (Souls Combat on both titles, Portal Gun, First Person) state
/// it through the camera mod API and wrap none of the game's camera scripts. Reads the mods' sources.
/// </summary>
[TestSuite]
public class ModCameraMigrationTests
{
    private static string? RepoRoot()
    {
        string? at = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (File.Exists(Path.Combine(at, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h")))
            {
                return at;
            }

            at = Path.GetDirectoryName(at);
        }

        return null;
    }

    private static string Read(string root, params string[] path) => File.ReadAllText(Path.Combine(new[] { root }.Concat(path).ToArray()));

    /// <summary>The bundled mods that shape the camera state it through the API and wrap no camera script.</summary>
    [TestCase]
    public void TheBundledModsStateTheirCameraThroughTheApi()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string[] wraps =
        {
            "SACCamera.CalculateActiveZoomLevel =", "SACCamera.GetDesiredCombatCamWeight =", "SACCamera.CalculateCentreAndCage =",
            "SACCamera.GetHeight =", "SACCamera.CalculateDesiredDirection =", "MagicSACCamera.IsValid =", "RangedCombatCamera.GetTightness =",
            "RangedCombatCamera.GetOffsetDegrees =", "FirstPersonCamera.Activate =", "FirstPersonCamera.FrameUpdate =", "BestCameraFunction =",
            "SACCamera.HeightAdjustSpeed =", "SACCamera.CameraMoveDamping =",
        };
        var files = new (string[] Path, string[] States)[]
        {
            (new[] { "Fable2", "mods-src", "souls_combat", "mod", "camera.lua" },
                new[] { "Refii.camera_tune(p,", "Refii.camera_frame(p,", "Refii.camera_combat_blend(p, 0)", "Refii.camera_interest(p,", "Refii.camera_pitch_hold(p, true, { shared = true })", "levels = \"first\"" }),
            (new[] { "Fable3", "mods-src", "souls_combat", "mod", "camera.lua" },
                new[] { "Refii.camera_tune(p,", "Refii.camera_frame(p,", "Refii.camera_combat_blend(p, 0)", "Refii.camera_interest(p,", "Refii.camera_pitch_hold(p, true, { shared = true })", "Refii.camera_mode_allow(p, magic, false)", "levels = \"all\"" }),
            (new[] { "Fable3", "mods-src", "souls_combat", "mod", "lockon.lua" }, new[] { "Refii.camera_lock_frame(p, want)" }),
            (new[] { "Fable2", "mods-src", "portal_gun", "mod", "camera.lua" },
                new[] { "Refii.camera_tune(p,", "Refii.camera_frame(p,", "Refii.camera_focus(p,", "Refii.camera_pitch_hold(p, aiming, { min = 1.2, max = 3.0 })", "ranged_pitch_limit = 70" }),
        };
        foreach ((string[] path, string[] states) in files)
        {
            string text = Read(root, path);
            foreach (string wrap in wraps)
            {
                AssertThat(text.Contains(wrap, StringComparison.Ordinal)).OverrideFailureMessage(string.Join("/", path) + " still wraps " + wrap).IsFalse();
            }

            foreach (string state in states)
            {
                AssertThat(text.Contains(state, StringComparison.Ordinal)).OverrideFailureMessage(string.Join("/", path) + " lacks " + state).IsTrue();
            }
        }

        AssertThat(File.Exists(Path.Combine(root, "Fable2", "mods-src", "first_person", "mod", "camera.lua"))).IsFalse();
        string script = Read(root, "Fable2", "mods-src", "first_person", "godot", "first_person.gd");
        AssertThat(script.Contains("host.camera_tune(p, view if here else VIEW_OFF, MOD)", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>portal_gun's Godot script aims and places its portal views from its player's own drawn view
    /// (camera_ray, camera_pose: their half on a split screen), not from the window's Camera3D pose.</summary>
    [TestCase]
    public void PortalGunAimsAlongItsPlayersOwnView()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string portal = Read(root, "Fable2", "mods-src", "portal_gun", "godot", "portal_gun.gd");
        AssertThat(portal.Contains("var line: Dictionary = host.camera_ray(player)", StringComparison.Ordinal)).IsTrue();
        AssertThat(portal.Contains("var pose: Dictionary = host.camera_pose(player)", StringComparison.Ordinal)).IsTrue();
        AssertThat(portal.Contains("eye.global_transform = turned * here.affine_inverse() * drawn", StringComparison.Ordinal)).IsTrue();
        AssertThat(portal.Contains("camera.global_transform", StringComparison.Ordinal)).IsFalse();
    }
}
