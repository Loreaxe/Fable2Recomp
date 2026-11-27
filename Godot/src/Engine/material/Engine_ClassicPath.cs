using Godot;

namespace Refii.Models;

/// <summary>
/// The classic renderer's per-frame drive: the nodes it owns under a host and the order its
/// feeds pump. One implementation for the running game (<c>GameSession</c>) and the level
/// viewer lab, so a frame drawn in either goes through the same code.
/// </summary>
/// <remarks>
/// THE LAB REPLAYS A RECORDED FRAME. Without the guest the bridge states nothing, so the feeds
/// take their replay arrays instead (<see cref="Engine_ClassicFeed.Replay"/>,
/// <see cref="Engine_ClassicLights.Replay"/>, <see cref="Engine_ClassicMist.Replay"/>): the
/// 148-float frame, the 20-float lights and the 36-float mists exactly as the native exports
/// lay them out, saved from a live run by the debug channel's `classic` op.
/// </remarks>
public static class Engine_ClassicPath
{
    /// <summary>Adds the classic renderer's nodes under <paramref name="host"/> when the path is on.</summary>
    public static void Ensure(Node host)
    {
        if (!Engine_ClassicMaterial.Active)
        {
            return;
        }
        if (Engine_ClassicShadows.Instance is null)
        {
            host.AddChild(new Engine_ClassicShadows { Name = "ClassicShadows" });
        }
        if (Engine_ClassicSpotShadows.Instance is null)
        {
            host.AddChild(new Engine_ClassicSpotShadows { Name = "ClassicSpotShadows" });
        }
        if (Engine_ClassicShadowSpheres.Instance is null)
        {
            host.AddChild(new Engine_ClassicShadowSpheres());
        }
        if (Engine_ClassicMist.Instance is null)
        {
            host.AddChild(new Engine_ClassicMist { Name = "ClassicMist" });
        }
        if (Refii.Level.Engine_ClassicSkyDome.Instance is null)
        {
            host.AddChild(new Refii.Level.Engine_ClassicSkyDome());
        }
        if (Engine_ClassicParticles.Instance is null)
        {
            host.AddChild(new Engine_ClassicParticles { Name = "ClassicParticles" });
        }
        if (Refii.Level.Engine_ClassicWeather.Instance is null)
        {
            host.AddChild(new Refii.Level.Engine_ClassicWeather());
        }
        if (Engine_ClassicDisplacement.Instance is null)
        {
            host.AddChild(new Engine_ClassicDisplacement());
        }
        if (Engine_ClassicDecals.Instance is null)
        {
            host.AddChild(new Engine_ClassicDecals());
        }
        if (Engine_ClassicBillboards.Instance is null)
        {
            host.AddChild(new Engine_ClassicBillboards());
        }
        if (Engine_ClassicFur.Instance is null)
        {
            host.AddChild(new Engine_ClassicFur());
        }
        if (Refii.Fx.Engine_FxMeshTwins.Instance is null)
        {
            host.AddChild(new Refii.Fx.Engine_FxMeshTwins());
        }
    }

    /// <summary>
    /// The rain and snow, one instance per view: each stands across its own view's camera.
    /// Player two's is built while the two views are drawn and freed when they close
    /// (Engine_Views.Open); a rejoin hides it.
    /// </summary>
    private static void PumpWeather(Node host, Camera3D camera, Vector2 viewport)
    {
        bool split = Engine_ClassicViews.Count > 1 && Engine_ClassicViews.Camera(1) is not null;
        Camera3D first = split ? Engine_ClassicViews.Camera(0) ?? camera : camera;
        Vector2 firstSize = split && Refii.Core.Engine_Views.FirstViewport is { } one ? (Vector2)one.Size : viewport;
        Refii.Level.Engine_ClassicWeather.Instance?.Pump(first, firstSize);

        if (!split)
        {
            if (!Refii.Core.Engine_Views.Open)
            {
                Refii.Level.Engine_ClassicWeather.Second?.QueueFree();
            }
            else if (Refii.Level.Engine_ClassicWeather.Second is { Visible: true } kept)
            {
                kept.Visible = false;
            }

            return;
        }

        if (Refii.Level.Engine_ClassicWeather.Second is not { } second)
        {
            host.AddChild(new Refii.Level.Engine_ClassicWeather { View = 1 });
            return;
        }

        if (!second.Visible)
        {
            second.Visible = true;
        }

        Vector2 secondSize = Refii.Core.Engine_Views.SecondViewport is { } two ? (Vector2)two.Size : viewport;
        second.Pump(Engine_ClassicViews.Camera(1)!, secondSize);
    }

    /// <summary>
    /// The sky dome, one per view: each takes the mist volume its own view's eye stands in.
    /// Player two's is built while the two views are drawn and freed when they close
    /// (Engine_Views.Open); a rejoin hides it.
    /// </summary>
    private static void PumpSky(Node host, Camera3D camera)
    {
        Refii.Level.Engine_ClassicSkyDome.Instance?.Pump(camera);
        if (Engine_ClassicViews.Count < 2 || Engine_ClassicViews.Camera(1) is not { } secondCamera)
        {
            if (!Refii.Core.Engine_Views.Open)
            {
                Refii.Level.Engine_ClassicSkyDome.Second?.QueueFree();
            }
            else if (Refii.Level.Engine_ClassicSkyDome.Second is { Visible: true } kept)
            {
                kept.Visible = false;
            }

            return;
        }

        if (Refii.Level.Engine_ClassicSkyDome.Second is not { } second)
        {
            second = new Refii.Level.Engine_ClassicSkyDome { View = 1 };
            host.AddChild(second);
        }
        else if (!second.Visible)
        {
            second.Visible = true;
        }

        second.Pump(secondCamera);
    }

    /// <summary>
    /// Pumps every feed for the frame drawn from <paramref name="camera"/> and publishes the
    /// constant file. Main thread, once a frame.
    /// </summary>
    /// <remarks>
    /// <paramref name="camera"/> is the window's camera, which player one's view follows; on
    /// split screen the feeds that belong to a view take each view's own camera from
    /// <see cref="Engine_ClassicViews"/> (the frame constants, the cascades, the view-space
    /// material constants, the displacement target).
    /// </remarks>
    public static void Pump(Node host, Camera3D? camera, Vector2 viewport)
    {
        if (!Engine_ClassicMaterial.Active)
        {
            return;
        }
        Engine_ClassicViews.Begin(camera);
        Engine_ClassicConstants.EnsureRegistered();
        using (Refii.Core.Engine_FramePhases.Measure("classic.feed"))
        {
            Engine_ClassicFeed.Pump(camera);
            Engine_ClassicLights.Pump(camera);
            Engine_ClassicViews.WriteHostRegisters();
        }
        using (Refii.Core.Engine_FramePhases.Measure("classic.mist"))
        {
            Engine_ClassicMist.Instance?.Pump(Engine_ClassicFeed.GlobalMistStrength, Engine_ClassicFeed.Seconds);
        }
        using (Refii.Core.Engine_FramePhases.Measure("classic.lit"))
        {
            Engine_ClassicLitInstances.Pump();
            Engine_ClassicSpotShadows.Instance?.Place();
            Engine_ClassicLoad.PumpSettle();
        }
        if (camera is not null)
        {
            using (Refii.Core.Engine_FramePhases.Measure("classic.particles"))
            {
                Engine_ClassicParticles.Instance?.Pump(camera);
                Refii.Fx.Engine_FxMeshTwins.Instance?.Pump(camera);
                Engine_ClassicDisplacement.Instance?.Pump(camera, viewport);
                Engine_ClassicDecals.Instance?.Pump(camera);
                Engine_ClassicShadowSpheres.Instance?.Pump(Engine_ClassicViews.Camera(0) ?? camera);
            }
            PumpWeather(host, camera, viewport);
            using (Refii.Core.Engine_FramePhases.Measure("classic.billboards"))
            {
                Engine_ClassicBillboards.Instance?.Pump(camera);
            }
            using (Refii.Core.Engine_FramePhases.Measure("classic.fur"))
            {
                Engine_ClassicFur.Instance?.Pump(camera);
            }
        }
        using (Refii.Core.Engine_FramePhases.Measure("classic.water"))
        {
            Refii.Level.Engine_ClassicWater.Pump(viewport);
        }
        using (Refii.Core.Engine_FramePhases.Measure("classic.transfer"))
        {
            Engine_ClassicTransfer.PumpPending();
        }
        if (camera is not null)
        {
            using (Refii.Core.Engine_FramePhases.Measure("classic.shadows"))
            {
                if (Engine_ClassicShadows.Instance is null)
                {
                    host.AddChild(new Engine_ClassicShadows { Name = "ClassicShadows" });
                }
                if (Engine_ClassicFeed.TrySun(out _, out Vector3 travel, out _))
                {
                    Engine_ClassicShadows.Instance?.Pump(camera, travel);
                }
            }
            using (Refii.Core.Engine_FramePhases.Measure("classic.sky"))
            {
                PumpSky(host, camera);
            }
        }
        using (Refii.Core.Engine_FramePhases.Measure("classic.publish"))
        {
            Engine_ClassicConstants.Publish();
            Engine_ClassicVertexBakes.Flush();
            Engine_ClassicPrtRows.Flush();
        }
    }

    /// <summary>Loads a recorded frame (the `classic` op's `frame`, `lightsRaw`, `mistsRaw`) as the feeds' source.</summary>
    public static bool LoadReplay(string path, out string error)
    {
        error = string.Empty;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            System.Text.Json.JsonElement root = document.RootElement;
            static float[] Floats(System.Text.Json.JsonElement element)
            {
                var list = new System.Collections.Generic.List<float>();
                foreach (System.Text.Json.JsonElement value in element.EnumerateArray())
                {
                    list.Add((float)value.GetDouble());
                }
                return list.ToArray();
            }
            if (!root.TryGetProperty("frame", out System.Text.Json.JsonElement frame))
            {
                error = "no 'frame' array";
                return false;
            }
            Engine_ClassicFeed.Replay = Floats(frame);
            Engine_ClassicLights.Replay = root.TryGetProperty("lights", out System.Text.Json.JsonElement lights) ? Floats(lights) : System.Array.Empty<float>();
            Engine_ClassicMist.Replay = root.TryGetProperty("mists", out System.Text.Json.JsonElement mists) ? Floats(mists) : System.Array.Empty<float>();
            return true;
        }
        catch (System.Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>The status line the session logs.</summary>
    public static string Status()
    {
        (long feedFrames, long fed) = Engine_ClassicFeed.Counts;
        (int attached, int lit) = Engine_ClassicLitInstances.Counts;
        (int built, int fellBack) = Engine_ClassicMaterial.Counts;
        var particles = Engine_ClassicParticles.Instance?.Counts ?? (0, 0, 0);
        (int compositions, double compileMs) = Engine_ClassicShaders.BuildCounts;
        return $"[classic] status: feed {fed}/{feedFrames} frames, lights {Engine_ClassicLights.Lights.Count}, " +
            $"compositions {compositions} ({compileMs:0} ms compiling), " +
            $"lit instances {lit}/{attached}, mists {Engine_ClassicMist.Volumes.Count}, " +
            $"particles {particles.Item2} in {particles.Item1} groups ({particles.Item3} displacement skipped; " +
            $"surfaces {Engine_ClassicParticles.InPlaceUploads} rewritten in place, {Engine_ClassicParticles.Rebuilds} rebuilt), " +
            $"water surfaces {Refii.Level.Engine_ClassicWater.Count}, materials {built} built / {fellBack} fell back, tree ambients {Engine_ClassicTransfer.TreeAmbients}, " +
            $"sun casters {Engine_ClassicCasters.Shown}/{Engine_ClassicCasters.Count} in reach, " +
            $"rain {(Refii.Level.Engine_ClassicWeather.Instance?.Raining == true ? "on" : "off")}, " +
            $"snow blocks {Refii.Level.Engine_ClassicWeather.Instance?.SnowBlocks ?? 0}, " +
            $"displacement groups {Engine_ClassicDisplacement.Drawn}, decals {Engine_ClassicDecals.Instance?.Drawn ?? 0}, " +
            $"billboards {Billboards()}, fur {Fur()}, fx meshes {Refii.Fx.Engine_FxMeshTwins.Instance?.Drawn ?? 0}, " +
            $"roofs {Refii.Level.Engine_ClassicRoofs.Roofs.Count} of {Refii.Level.Engine_ClassicRoofs.Count} buildings";
    }

    private static string Fur()
    {
        if (Engine_ClassicFur.Instance is not { } fur)
        {
            return "off";
        }
        (int instances, int shells) = fur.Counts;
        return $"{instances} furred, {shells} shells";
    }

    private static string Billboards()
    {
        if (Engine_ClassicBillboards.Instance is not { } billboards)
        {
            return "off";
        }
        (int kinds, int visible, int placements) = billboards.Counts;
        return $"{placements} placements of {kinds} models, {visible} captured (B {Engine_ClassicBillboards.Brightness:0.###})";
    }
}
