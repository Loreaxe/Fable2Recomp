using System;
using System.Collections.Generic;

namespace Refii.Launcher.Core;

/// <summary>Top-level sub-tabs in the overhauled launcher settings panel.</summary>
public enum SettingsTab
{
    /// <summary>Window mode, resolution, frame rate, upscaling, and anti-aliasing.</summary>
    Display,

    /// <summary>Lighting, shadows, global illumination, textures, weather, and world draw detail.</summary>
    Graphics,

    /// <summary>Gameplay features, cutscene skipping, Discord Rich Presence, and co-op/online play.</summary>
    Gameplay,

    /// <summary>Mouse look, camera inversion, controller vibration, button glyphs, and pad remapping.</summary>
    Controls,

    /// <summary>Interactive click-to-bind keyboard &amp; mouse actions plus mod input bindings.</summary>
    Keybindings,

    /// <summary>Developer overlays, GPU/Tracy/RenderDoc profilers, engine streaming, and VFS diagnostics.</summary>
    Developer,
}

/// <summary>Progressive disclosure tier for a setting inside its tab.</summary>
public enum SettingTier
{
    /// <summary>Shown immediately in the tab's main sections.</summary>
    Essential,

    /// <summary>Tucked inside the tab's collapsible <c>Advanced</c> accordion (and always findable via Search).</summary>
    Advanced,

    /// <summary>Shown only in the <see cref="SettingsTab.Developer"/> tab when Developer settings are enabled.</summary>
    Developer,
}

/// <summary>Bounded slider/spin range and unit suffix for numeric settings.</summary>
public readonly record struct NumericRange(
    double Min,
    double Max,
    double Step,
    string Suffix = "",
    string? ZeroLabel = null,
    bool AllowGreater = false);

/// <summary>Classification and presentation metadata for a runtime CVar in the launcher settings panel.</summary>
public sealed record SettingPresentation(
    SettingsTab Tab,
    string Section,
    SettingTier Tier,
    string Label,
    string? Summary = null,
    NumericRange? Range = null,
    int Order = 1000,
    bool HandledByHeroOrComposite = false,
    bool HandledByLightingModal = false,
    string? ContextualParentKey = null);

/// <summary>
/// Maps every runtime CVar to a modern game settings tab, section, disclosure tier, friendly label,
/// bounded slider range, and contextual visibility rule.
/// </summary>
public static class DesktopSettingMetadata
{
    /// <summary>Display label for each <see cref="SettingsTab"/>.</summary>
    public static string TabTitle(SettingsTab tab) => tab switch
    {
        SettingsTab.Display => "Display",
        SettingsTab.Graphics => "Graphics",
        SettingsTab.Gameplay => "Gameplay",
        SettingsTab.Controls => "Controls",
        SettingsTab.Keybindings => "Keys",
        SettingsTab.Developer => "Debug",
        _ => tab.ToString(),
    };

    /// <summary>Full descriptive title for each <see cref="SettingsTab"/> (used in headers and search).</summary>
    public static string TabFullName(SettingsTab tab) => tab switch
    {
        SettingsTab.Display => "Display",
        SettingsTab.Graphics => "Graphics",
        SettingsTab.Gameplay => "Gameplay",
        SettingsTab.Controls => "Controls",
        SettingsTab.Keybindings => "Keybindings",
        SettingsTab.Developer => "Debug & Profiling",
        _ => tab.ToString(),
    };

    /// <summary>Returns the friendly UI label for <paramref name="definition"/>.</summary>
    public static string Label(RuntimeCVarDefinition definition) =>
        Classify(definition).Label;

    /// <summary>Returns the complete <see cref="SettingPresentation"/> for <paramref name="definition"/>.</summary>
    public static SettingPresentation Classify(RuntimeCVarDefinition definition) =>
        Refii.AndroidRuntime.AndroidSettingsLayout.Present(definition, ClassifyShared(definition)); // the phone's tiers, android/AndroidSettingsLayout.cs

    private static SettingPresentation ClassifyShared(RuntimeCVarDefinition definition)
    {
        if (Explicit.TryGetValue(definition.Name, out SettingPresentation? known))
        {
            return known;
        }

        // Keybindings: key_<action> and mod key_<mod>.<action>
        if (definition.Name.StartsWith("key_", StringComparison.Ordinal))
        {
            if (definition.Category.StartsWith(RuntimeCVarsGodot.ControlsCategory + ": ", StringComparison.Ordinal))
            {
                string modTitle = definition.Category[(RuntimeCVarsGodot.ControlsCategory.Length + 2)..];
                return new SettingPresentation(
                    SettingsTab.Keybindings,
                    "Mod: " + modTitle,
                    SettingTier.Essential,
                    FormatModActionLabel(definition),
                    Order: 500);
            }

            (string section, string label, int order) = ClassifyKeyAction(definition.Name[4..]);
            return new SettingPresentation(
                SettingsTab.Keybindings,
                section,
                SettingTier.Essential,
                label,
                Order: order);
        }

        // Controller button remapping: pad_p1_*, pad_p2_*, and mod pad_<mod>.<action>
        if (definition.Name.StartsWith("pad_p1_", StringComparison.Ordinal))
        {
            return new SettingPresentation(
                SettingsTab.Controls,
                "Player 1 Controller Remap",
                SettingTier.Advanced,
                "P1 " + definition.Name["pad_p1_".Length..].ToUpperInvariant(),
                Order: 400);
        }

        if (definition.Name.StartsWith("pad_p2_", StringComparison.Ordinal))
        {
            return new SettingPresentation(
                SettingsTab.Controls,
                "Player 2 Controller Remap",
                SettingTier.Advanced,
                "P2 " + definition.Name["pad_p2_".Length..].ToUpperInvariant(),
                Order: 500);
        }

        if (definition.Name.StartsWith("pad_", StringComparison.Ordinal) &&
            definition.Category.StartsWith(RuntimeCVarsGodot.ControlsCategory + ": ", StringComparison.Ordinal))
        {
            string modTitle = definition.Category[(RuntimeCVarsGodot.ControlsCategory.Length + 2)..];
            return new SettingPresentation(
                SettingsTab.Controls,
                modTitle + " (Controller)",
                SettingTier.Advanced,
                FormatModActionLabel(definition),
                Order: 600);
        }

        // Remaining Lighting category dials live inside LightingSettingsDialog and remain searchable under Graphics.
        if (definition.Category == RuntimeCVarsGodot.LightingCategory)
        {
            return new SettingPresentation(
                SettingsTab.Graphics,
                "Lighting & Shadows (Fine-Tuning)",
                SettingTier.Advanced,
                RuntimeSettingsModel.FormatDisplayName(definition),
                Order: 850,
                HandledByLightingModal: true);
        }

        // Remaining RexNet multiplayer knobs
        if (definition.Category is "RexNet" or RuntimeCVarsGodot.MultiplayerCategory)
        {
            return new SettingPresentation(
                SettingsTab.Gameplay,
                "Online Network Tuning",
                SettingTier.Advanced,
                RuntimeSettingsModel.FormatDisplayName(definition),
                Order: 700);
        }

        // Developer & native runtime diagnostics
        if (definition.Category == RuntimeCVarsGodot.DeveloperCategory ||
            !GodotDefinedKeys.Contains(definition.Name))
        {
            return new SettingPresentation(
                SettingsTab.Developer,
                definition.Category == RuntimeCVarsGodot.DeveloperCategory
                    ? "Additional Diagnostics"
                    : "Native Runtime & VFS (" + definition.Category + ")",
                SettingTier.Advanced,
                RuntimeSettingsModel.FormatDisplayName(definition),
                Order: 900);
        }

        // Fallback for any newly added Godot CVar so nothing ever disappears silently.
        return definition.Category switch
        {
            RuntimeCVarsGodot.DisplayCategory => new SettingPresentation(
                SettingsTab.Display,
                "Advanced Display",
                SettingTier.Advanced,
                RuntimeSettingsModel.FormatDisplayName(definition)),
            RuntimeCVarsGodot.Category or RuntimeCVarsGodot.VideoCategory => new SettingPresentation(
                SettingsTab.Graphics,
                "Advanced Graphics",
                SettingTier.Advanced,
                RuntimeSettingsModel.FormatDisplayName(definition)),
            RuntimeCVarsGodot.ControlsCategory => new SettingPresentation(
                SettingsTab.Controls,
                "Additional Controls",
                SettingTier.Advanced,
                RuntimeSettingsModel.FormatDisplayName(definition)),
            _ => new SettingPresentation(
                SettingsTab.Gameplay,
                "Additional Options",
                SettingTier.Advanced,
                RuntimeSettingsModel.FormatDisplayName(definition)),
        };
    }

    /// <summary>
    /// Whether a contextual setting should be shown right now given the current <paramref name="values"/>.
    /// In search results, contextual children are always shown.
    /// </summary>
    public static bool IsContextuallyVisible(
        RuntimeCVarDefinition definition,
        IReadOnlyDictionary<string, string> values)
    {
        SettingPresentation meta = Classify(definition);
        if (meta.ContextualParentKey is null)
        {
            return true;
        }

        if (meta.ContextualParentKey == "render_scaler")
        {
            string scaler = values.TryGetValue("render_scaler", out string? s) ? s.Trim().Trim('"') : "bilinear";
            return scaler is "fsr" or "fsr2";
        }

        if (meta.ContextualParentKey == "dynamic_sky_and_weather")
        {
            string enabled = values.TryGetValue("dynamic_sky_and_weather", out string? d) ? d : "false";
            return RuntimeConfig.NormalizeBoolValue(enabled) == "true";
        }

        if (meta.ContextualParentKey == "placed_light_shadows")
        {
            string enabled = values.TryGetValue("placed_light_shadows", out string? p) ? p : "true";
            return RuntimeConfig.NormalizeBoolValue(enabled) == "true";
        }

        return true;
    }

    private static string FormatModActionLabel(RuntimeCVarDefinition definition)
    {
        int colon = definition.Description.IndexOf(" on the ", StringComparison.Ordinal);
        if (colon > 0)
        {
            return definition.Description[..colon];
        }

        int dot = definition.Name.IndexOf('.');
        return dot >= 0 && dot + 1 < definition.Name.Length
            ? RuntimeSettingsModel.FormatDisplayName(definition with { Name = definition.Name[(dot + 1)..] })
            : RuntimeSettingsModel.FormatDisplayName(definition);
    }

    private static (string Section, string Label, int Order) ClassifyKeyAction(string actionName) =>
        actionName switch
        {
            "move_forward" => ("Movement", "Move Forward", 10),
            "move_back" => ("Movement", "Move Back", 20),
            "move_left" => ("Movement", "Strafe Left", 30),
            "move_right" => ("Movement", "Strafe Right", 40),
            "sprint" => ("Movement", "Sprint (Hold)", 50),
            "walk" => ("Movement", "Walk (Hold)", 60),

            "attack" => ("Combat & Modes", "Primary Attack / Charge", 110),
            "melee_mode" => ("Combat & Modes", "Switch to Melee Mode", 120),
            "magic_mode" => ("Combat & Modes", "Switch to Magic Mode", 130),
            "ranged_mode" => ("Combat & Modes", "Switch to Ranged Mode", 140),
            "melee" => ("Combat & Modes", "Direct Melee Attack", 150),
            "ranged" => ("Combat & Modes", "Direct Ranged Attack", 160),
            "magic" => ("Combat & Modes", "Direct Magic Cast", 170),
            "target_lock" => ("Combat & Modes", "Lock Target (Hold)", 180),
            "spells_menu" => ("Combat & Modes", "Spell Selection (Hold)", 190),

            "interact" => ("Camera & Interaction", "Interact / Confirm", 210),
            "expression_wheel" => ("Camera & Interaction", "Expression Wheel (Hold)", 220),
            "first_person" => ("Camera & Interaction", "First-Person View (Hold)", 230),
            "camera_mode" => ("Camera & Interaction", "Toggle Camera Distance", 240),
            "photo_mode" => ("Camera & Interaction", "Photo Mode", 250),

            "pause" => ("Menus & Item Slots", "Pause / Sanctuary Book", 310),
            "back" => ("Menus & Item Slots", "Menu Back", 320),
            "item_up" => ("Menus & Item Slots", "Item Slot Up (D-Pad Up)", 330),
            "item_down" => ("Menus & Item Slots", "Item Slot Down (D-Pad Down)", 340),
            "item_left" => ("Menus & Item Slots", "Item Slot Left (D-Pad Left)", 350),
            "item_right" => ("Menus & Item Slots", "Item Slot Right (D-Pad Right)", 360),
            "tab_prev" => ("Menus & Item Slots", "Previous Menu Tab", 370),
            "tab_next" => ("Menus & Item Slots", "Next Menu Tab", 380),

            _ => ("Other Actions", PrettifyWords(actionName), 400),
        };

    private static string PrettifyWords(string snake)
    {
        string[] parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        }

        return string.Join(' ', parts);
    }

    private static readonly HashSet<string> GodotDefinedKeys = BuildGodotDefinedKeys();

    private static HashSet<string> BuildGodotDefinedKeys()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (RuntimeCVarDefinition def in RuntimeCVarsGodot.Definitions)
        {
            set.Add(def.Name);
        }

        return set;
    }

    /// <summary>The names <see cref="Explicit"/> presents; each must be a cvar's own name to match it.</summary>
    internal static IEnumerable<string> ExplicitNames => Explicit.Keys;

    private static readonly Dictionary<string, SettingPresentation> Explicit =
        new(StringComparer.Ordinal)
        {
            // ==================== HERO / COMPOSITE ====================
            [DesktopGraphicsPresets.Key] = new(
                SettingsTab.Graphics, "Presets", SettingTier.Essential, "Performance Preset",
                "Overall rendering performance tier across upscaling, GI, shadows, fog, and LOD.",
                Order: 1, HandledByHeroOrComposite: true),
            [GraphicsPresets.Key] = new(
                SettingsTab.Graphics, "Presets", SettingTier.Essential, "Graphics Preset",
                "Overall mobile rendering quality preset.",
                Order: 2, HandledByHeroOrComposite: true),
            ["lighting_model"] = new(
                SettingsTab.Graphics, "Presets", SettingTier.Advanced, "Lighting Pipeline",
                "Modern PBR lighting or Classic 2008 shader lighting (set together by Look).",
                Order: 705),
            ["materials_modernized"] = new(
                SettingsTab.Graphics, "Textures & Materials", SettingTier.Advanced, "Modernized Materials Master Switch",
                "Master remaster material switch (set together by Look).",
                Order: 710),

            // ==================== DISPLAY TAB ====================
            ["fullscreen"] = new(
                SettingsTab.Display, "Window & Monitor", SettingTier.Essential, "Exclusive Fullscreen",
                Order: 10, HandledByHeroOrComposite: true),
            ["borderless"] = new(
                SettingsTab.Display, "Window & Monitor", SettingTier.Essential, "Borderless Window",
                Order: 11, HandledByHeroOrComposite: true),
            ["display_aspect"] = new(
                SettingsTab.Display, "Window & Monitor", SettingTier.Essential, "Aspect Ratio",
                "Filters the resolution list to match your display shape.",
                Order: 20),
            ["display_resolution"] = new(
                SettingsTab.Display, "Window & Monitor", SettingTier.Essential, "Resolution",
                "Windowed size, or Match Window to render natively at whatever size you resize the window to.",
                Order: 30),
            ["monitor"] = new(
                SettingsTab.Display, "Window & Monitor", SettingTier.Essential, "Display Monitor",
                "Which monitor fullscreen opens on (0 = primary display).",
                new NumericRange(0, 7, 1), Order: 40),

            ["vsync_mode"] = new(
                SettingsTab.Display, "Frame Rate & Sync", SettingTier.Essential, "Vertical Sync (VSync)",
                "Mailbox eliminates tearing without adding input latency; On locks to refresh rate.",
                Order: 50),
            ["max_fps"] = new(
                SettingsTab.Display, "Frame Rate & Sync", SettingTier.Essential, "Frame Rate Limit",
                "Caps rendered FPS to save power and heat; simulation always runs at full speed.",
                new NumericRange(0, 240, 5, " FPS", "Uncapped"), Order: 60),

            ["render_scaler"] = new(
                SettingsTab.Display, "Upscaling & Anti-Aliasing", SettingTier.Essential, "Upscaler",
                "FSR 1.0 & FSR 2.2 boost frame rate on high-DPI displays while keeping UI razor-sharp.",
                Order: 70),
            ["fsr_quality"] = new(
                SettingsTab.Display, "Upscaling & Anti-Aliasing", SettingTier.Essential, "FSR Quality Mode",
                "Internal 3D resolution used by FSR before sharpening up to your window.",
                Order: 75, ContextualParentKey: "render_scaler"),
            ["fsr_sharpness"] = new(
                SettingsTab.Display, "Upscaling & Anti-Aliasing", SettingTier.Essential, "FSR Sharpness",
                "0.0 is sharpest; 0.2 is the balanced default; higher values soften sharpening.",
                new NumericRange(0.0, 2.0, 0.05), Order: 76, ContextualParentKey: "render_scaler"),
            ["render_scale_percent"] = new(
                SettingsTab.Display, "Upscaling & Anti-Aliasing", SettingTier.Essential, "3D Render Scale",
                "Internal 3D resolution percentage (UI & HUD remain native 100%).",
                new NumericRange(50, 200, 5, "%"), Order: 80),
            ["screen_space_aa"] = new(
                SettingsTab.Display, "Upscaling & Anti-Aliasing", SettingTier.Essential, "Post-Process Anti-Aliasing",
                "FXAA is fast and light; SMAA preserves sharper surface detail.",
                Order: 90),
            ["msaa"] = new(
                SettingsTab.Display, "Upscaling & Anti-Aliasing", SettingTier.Essential, "Multisample AA (MSAA)",
                "Hardware geometry edge anti-aliasing.",
                Order: 100),
            ["taa"] = new(
                SettingsTab.Display, "Upscaling & Anti-Aliasing", SettingTier.Essential, "Temporal AA (TAA)",
                "Smooths shimmering edges across frames (not needed when FSR 2.2 is active).",
                Order: 110),

            ["ui_text_style"] = new(
                SettingsTab.Display, "Interface", SettingTier.Essential, "HUD & Menu Font Style",
                "Crisp original Xbox 360 pixel alignment or smooth modern font antialiasing.",
                Order: 120),

            // Display — Advanced
            ["perf_overlay"] = new(
                SettingsTab.Display, "Advanced Display", SettingTier.Advanced, "Performance Overlay on Startup (F3)",
                "Shows live FPS, CPU/GPU frame time, draw calls, and VRAM (toggle anytime with F3).",
                Order: 510),
            ["rendering_driver"] = new(
                SettingsTab.Display, "Advanced Display", SettingTier.Advanced, "Graphics API",
                "Direct3D 12 (required for Windows HDR) or Vulkan. Applies on next launch.",
                Order: 520),
            ["render_thread"] = new(
                SettingsTab.Display, "Advanced Display", SettingTier.Advanced, "Multi-Threaded Renderer",
                "Runs Godot rendering on a dedicated thread. Applies on next launch.",
                Order: 530),
            ["debanding"] = new(
                SettingsTab.Display, "Advanced Display", SettingTier.Advanced, "Gradient Debanding",
                "Subtle dithering to smooth sky and dark cave gradients.",
                Order: 540),
            ["draw_latency_ticks"] = new(
                SettingsTab.Display, "Advanced Display", SettingTier.Advanced, "Simulation Interpolation Latency",
                "0 automatically tracks frame time; raise only if motion stutters.",
                new NumericRange(0.0, 2.0, 0.05, " ticks", "Auto"), Order: 550),

            // ==================== GRAPHICS TAB ====================
            ["lighting_preset"] = new(
                SettingsTab.Graphics, "Lighting & Shadows", SettingTier.Essential, "Lighting & Shadow Range",
                "Controls torch/lamp draw reach, shadow fade distance, and contact AO resolution.",
                Order: 10),
            ["sdfgi_enabled"] = new(
                SettingsTab.Graphics, "Lighting & Shadows", SettingTier.Essential, "Global Illumination (SDFGI)",
                "Real-time bounced sky and surface light. Turn off for a major FPS boost on integrated GPUs.",
                Order: 20),
            ["volumetric_fog"] = new(
                SettingsTab.Graphics, "Lighting & Shadows", SettingTier.Essential, "3D Volumetric Fog & Light Rays",
                "Atmospheric light shafts through mist, trees, and cave openings.",
                Order: 30),
            ["sun_shadows"] = new(
                SettingsTab.Graphics, "Lighting & Shadows", SettingTier.Essential, "Directional Sun & Moon Shadows",
                "Real-time cascading shadows from the sun and moon.",
                Order: 40),
            ["placed_light_shadows"] = new(
                SettingsTab.Graphics, "Lighting & Shadows", SettingTier.Essential, "Dynamic Torch & Lamp Shadows",
                "Characters and creatures cast live shadows from nearby torches and street lamps.",
                Order: 50),
            ["placed_shadow_limit"] = new(
                SettingsTab.Graphics, "Lighting & Shadows", SettingTier.Essential, "Dynamic Shadowing Lamps",
                "Maximum nearby torches and lamps casting live cubemap shadows at once (Low: 1, Balanced: 2, High: 4, Ultra: 8).",
                new NumericRange(1, 8, 1, " lamps"), Order: 52, ContextualParentKey: "placed_light_shadows"),
            ["hdr_output"] = new(
                SettingsTab.Graphics, "Lighting & Shadows", SettingTier.Essential, "HDR Display Output",
                "Outputs unclamped high-dynamic-range highlights on supported HDR monitors.",
                Order: 60),
            ["bloom_scale"] = new(
                SettingsTab.Graphics, "Lighting & Shadows", SettingTier.Essential, "Bloom Intensity",
                "Dreamy Fable highlight glow strength (0.25 is calibrated default; 0 turns scene bloom off).",
                new NumericRange(0.0, 2.0, 0.05), Order: 70),

            ["texture_upscaling_enabled"] = new(
                SettingsTab.Graphics, "Textures & Materials", SettingTier.Essential, "HD Enhanced Textures",
                "Enhances world, character, and UI textures at runtime while keeping disk cache.",
                Order: 110),
            ["texture_orm_enabled"] = new(
                SettingsTab.Graphics, "Textures & Materials", SettingTier.Essential, "Modern PBR Surface Detail",
                "Derives cavity occlusion, roughness, and metallic reflections from shipped maps.",
                Order: 120),
            ["anisotropic_filtering"] = new(
                SettingsTab.Graphics, "Textures & Materials", SettingTier.Essential, "Anisotropic Filtering",
                "Keeps cobblestones, roads, and floors crisp at oblique camera angles.",
                Order: 130),

            ["entity_draw_distance_scale"] = new(
                SettingsTab.Graphics, "World Detail & Atmosphere", SettingTier.Essential, "Character & NPC Draw Distance",
                "Multiplier on how far townsfolk, enemies, and carried props stay visible (1x = Xbox 360).",
                new NumericRange(0.5, 3.0, 0.25, "x"), Order: 210),
            ["prop_draw_distance_scale"] = new(
                SettingsTab.Graphics, "World Detail & Atmosphere", SettingTier.Essential, "Scenery Draw Distance",
                "Multiplier on foliage, rocks, and distant scenery draw distances (1x = Xbox 360).",
                new NumericRange(0.5, 3.0, 0.25, "x"), Order: 220),
            [Refii.Physics.Engine_ClothBody.DistanceScaleKey] = new(
                SettingsTab.Graphics, "World Detail & Atmosphere", SettingTier.Essential, "Cloth & Banner Wind Distance",
                "Multiplier on how far banners, flags, and garments blow in the wind (1x = Xbox 360; 0 = Off).",
                new NumericRange(0.0, 4.0, 0.25, "x", ZeroLabel: "Off", AllowGreater: true), Order: 222),
            ["corpse_lifetime_seconds"] = new(
                SettingsTab.Graphics, "World Detail & Atmosphere", SettingTier.Essential, "Corpse Persistence Time",
                "How long fallen enemy and creature corpses remain visible before fading out (0 = original game; no upper cap in config).",
                new NumericRange(0, 3600, 5, "s", ZeroLabel: "Default", AllowGreater: true), Order: 225),
            ["fx_weapon_augment_always_active"] = new(
                SettingsTab.Graphics, "World Detail & Atmosphere", SettingTier.Essential, "Continuous Weapon Augment Glow",
                "Keeps elemental weapon augment particles active while walking and running.",
                Order: 230),
            ["water_quality"] = new(
                SettingsTab.Graphics, "World Detail & Atmosphere", SettingTier.Essential, "Water Quality",
                "Controls lake and ocean reflections, underwater clarity, wave detail, and shoreline foam (applies immediately).",
                Order: 235),
            ["dynamic_sky_and_weather"] = new(
                SettingsTab.Graphics, "World Detail & Atmosphere", SettingTier.Essential, "Dynamic Procedural Sky & Weather",
                "Optional procedural sky dome with moving clouds, storms, and surface wetness.",
                Order: 240),
            ["dynamic_sky_weather"] = new(
                SettingsTab.Graphics, "World Detail & Atmosphere", SettingTier.Essential, "Weather Override",
                Order: 241, ContextualParentKey: "dynamic_sky_and_weather"),
            ["dynamic_sky_time_of_day"] = new(
                SettingsTab.Graphics, "World Detail & Atmosphere", SettingTier.Essential, "Time of Day Override",
                Order: 242, ContextualParentKey: "dynamic_sky_and_weather"),
            ["dynamic_sky_surface_weather"] = new(
                SettingsTab.Graphics, "World Detail & Atmosphere", SettingTier.Essential, "Rain Puddles & Snow Accumulation",
                Order: 243, ContextualParentKey: "dynamic_sky_and_weather"),
            ["dynamic_sky_volumetric_clouds"] = new(
                SettingsTab.Graphics, "World Detail & Atmosphere", SettingTier.Essential, "3D Raymarched Clouds",
                Order: 244, ContextualParentKey: "dynamic_sky_and_weather"),
            ["dynamic_sky_lighting_override"] = new(
                SettingsTab.Graphics, "World Detail & Atmosphere", SettingTier.Essential, "Procedural Sunlight Override",
                Order: 245, ContextualParentKey: "dynamic_sky_and_weather"),
            ["dynamic_sky_cloud_shadows"] = new(
                SettingsTab.Graphics, "World Detail & Atmosphere", SettingTier.Essential, "Moving Cloud Shadows",
                Order: 246, ContextualParentKey: "dynamic_sky_and_weather"),
            ["dynamic_sky_aurora"] = new(
                SettingsTab.Graphics, "World Detail & Atmosphere", SettingTier.Essential, "Night Aurora",
                Order: 247, ContextualParentKey: "dynamic_sky_and_weather"),

            // Graphics — Advanced
            ["texture_orm_height_enabled"] = new(
                SettingsTab.Graphics, "Advanced Texture & Material Tuning", SettingTier.Advanced, "Surface Parallax Height Maps",
                "Solves normal-map height for close-up cobblestone and brickwork depth.",
                Order: 610),
            ["texture_orm_ao_strength_percent"] = new(
                SettingsTab.Graphics, "Advanced Texture & Material Tuning", SettingTier.Advanced, "Material Cavity AO Strength",
                null, new NumericRange(0, 100, 5, "%"), Order: 620),
            ["texture_orm_metallic_strength_percent"] = new(
                SettingsTab.Graphics, "Advanced Texture & Material Tuning", SettingTier.Advanced, "Material Metallic Strength",
                null, new NumericRange(0, 100, 5, "%"), Order: 630),
            ["texture_upscaling_model"] = new(
                SettingsTab.Graphics, "Advanced Texture & Material Tuning", SettingTier.Advanced, "Texture Upscaler Filter",
                Order: 640),
            ["texture_upscaling_when"] = new(
                SettingsTab.Graphics, "Advanced Texture & Material Tuning", SettingTier.Advanced, "Texture Enhancement Timing",
                Order: 650),
            ["texture_upscaling_workers"] = new(
                SettingsTab.Graphics, "Advanced Texture & Material Tuning", SettingTier.Advanced, "Texture Upscaler Worker Threads",
                null, new NumericRange(0, 4, 1, "", "Auto"), Order: 660),
            ["texture_upscaling_weights"] = new(
                SettingsTab.Graphics, "Advanced Texture & Material Tuning", SettingTier.Advanced, "Neural Upscaler Weights Name",
                Order: 670),
            ["texture_cache_enabled"] = new(
                SettingsTab.Graphics, "Advanced Texture & Material Tuning", SettingTier.Advanced, "Disk Texture Cache",
                Order: 680),
            ["corpse_max_count"] = new(
                SettingsTab.Graphics, "Advanced Graphics", SettingTier.Advanced, "Max Persistent Corpses",
                "Maximum fallen corpses kept in the active region before the oldest distant corpse fades early (0 = Auto; -1 = Unlimited).",
                new NumericRange(0, 256, 4, "", ZeroLabel: "Auto", AllowGreater: true), Order: 685),
            ["fx_remaster_magic_circle"] = new(
                SettingsTab.Graphics, "Advanced Graphics", SettingTier.Advanced, "Remaster Will Magic Circle",
                "Adds layered arcane Will rings, rising perimeter motes, and dynamic cyan ground light to spell circles.",
                Order: 686),
            ["texture_cache_max_mb"] = new(
                SettingsTab.Graphics, "Advanced Texture & Material Tuning", SettingTier.Advanced, "Texture Cache Disk Limit",
                null, new NumericRange(0, 32768, 512, " MB", "Auto"), Order: 690),
            ["texture_mipmap_bias"] = new(
                SettingsTab.Graphics, "Advanced Texture & Material Tuning", SettingTier.Advanced, "Texture Mipmap Bias",
                null, new NumericRange(-1.0, 1.0, 0.05), Order: 700),

            ["mesh_lod_pixels"] = new(
                SettingsTab.Graphics, "Advanced Geometry & Culling", SettingTier.Advanced, "Mesh LOD Switch Threshold",
                "Higher switches distant models to simpler meshes sooner (1.0 = Xbox 360).",
                new NumericRange(0.5, 3.0, 0.25), Order: 720),
            ["prop_cull_distance_scale"] = new(
                SettingsTab.Graphics, "Advanced Geometry & Culling", SettingTier.Advanced, "Small Clutter Cull Scale",
                null, new NumericRange(0.0, 2.0, 0.05, "x", "Off"), Order: 730),
            ["prop_cull_min_pixels"] = new(
                SettingsTab.Graphics, "Advanced Geometry & Culling", SettingTier.Advanced, "Minimum Prop Screen Height",
                null, new NumericRange(0, 16, 1, " px", "Off"), Order: 740),
            ["prop_fade_fraction"] = new(
                SettingsTab.Graphics, "Advanced Geometry & Culling", SettingTier.Advanced, "Scenery Distance Fade Range",
                null, new NumericRange(0.0, 0.5, 0.05), Order: 750),
            ["tree_card_lod"] = new(
                SettingsTab.Graphics, "Advanced Geometry & Culling", SettingTier.Advanced, "Thin Distant Tree Leaf Cards",
                Order: 760),
            ["tree_card_far_levels"] = new(
                SettingsTab.Graphics, "Advanced Geometry & Culling", SettingTier.Advanced, "Extra-Far Tree Crown Thinning",
                Order: 765),
            ["tree_far_reach"] = new(
                SettingsTab.Graphics, "Advanced Geometry & Culling", SettingTier.Advanced, "Maximum Tree Draw Distance",
                null, new NumericRange(0, 1500, 50, " m", "Level Default"), Order: 770),
            ["tree_lod_bias"] = new(
                SettingsTab.Graphics, "Advanced Geometry & Culling", SettingTier.Advanced, "Distant Tree LOD Bias",
                null, new NumericRange(0.25, 2.0, 0.25), Order: 780),
            ["terrain_lod_bias"] = new(
                SettingsTab.Graphics, "Advanced Geometry & Culling", SettingTier.Advanced, "Terrain Geometry LOD Bias",
                null, new NumericRange(0.25, 2.0, 0.25), Order: 790),
            ["terrain_normal_maps"] = new(
                SettingsTab.Graphics, "Advanced Geometry & Culling", SettingTier.Advanced, "Terrain Bump & Cavity Maps",
                Order: 800),
            ["foliage_cell_metres"] = new(
                SettingsTab.Graphics, "Advanced Geometry & Culling", SettingTier.Advanced, "Ground Foliage Cell Size",
                null, new NumericRange(0, 64, 4, " m", "Default (32 m)"), Order: 810),
            ["cull_back_faces"] = new(
                SettingsTab.Graphics, "Advanced Geometry & Culling", SettingTier.Advanced, "Solid Surface Back-Face Culling",
                Order: 820),
            ["occlusion_culling"] = new(
                SettingsTab.Graphics, "Advanced Geometry & Culling", SettingTier.Advanced, "Town Building Occlusion Culling",
                "Experimental: skips geometry hidden behind solid town walls.",
                Order: 830),
            ["fx_live_effects"] = new(
                SettingsTab.Graphics, "Advanced Geometry & Culling", SettingTier.Advanced, "Dynamic Combat & Spell Particle FX",
                Order: 840),

            // ==================== GAMEPLAY TAB ====================
            ["decorate_mode_enabled"] = new(
                SettingsTab.Gameplay, "Features & Media", SettingTier.Essential, "Home Redecorate Free Placement",
                "Allows free furniture placement inside owned homes via the Redecorate option.",
                Order: 10),
            ["video_skip"] = new(
                SettingsTab.Gameplay, "Features & Media", SettingTier.Essential, "Skip Cutscene Videos on Click / A",
                "Lets left click or the controller A button skip skippable fullscreen movies.",
                Order: 20),
            ["pubgame_vector_shapes"] = new(
                SettingsTab.Gameplay, "Features & Media", SettingTier.Essential, "Sharp Vector Pub Games Artwork",
                "Renders Keystone, Spinnerbox, and Fortune's Tower cleanly at any resolution.",
                Order: 30),
            ["discord_presence"] = new(
                SettingsTab.Gameplay, "Features & Media", SettingTier.Essential, "Discord Rich Presence",
                "Displays the current game and region on your Discord profile.",
                Order: 40),

            [Refii.Camera.Engine_CameraScreens.ModeKey] = new(
                SettingsTab.Gameplay, "Co-op & Online Multiplayer", SettingTier.Essential, "Local Co-op Screen",
                "Shared screen, split screen, or shared until the players move apart.",
                Order: 110),
            [Refii.Game.GameSplitView.OrientationKey] = new(
                SettingsTab.Gameplay, "Co-op & Online Multiplayer", SettingTier.Essential, "Split-Screen Orientation",
                Order: 120),
            ["rexnet"] = new(
                SettingsTab.Gameplay, "Co-op & Online Multiplayer", SettingTier.Essential, "RexNet Online Orbs & Co-op",
                "Enables peer-to-peer online multiplayer and friend orbs.",
                Order: 130),
            ["rexnet_display_name"] = new(
                SettingsTab.Gameplay, "Co-op & Online Multiplayer", SettingTier.Essential, "Online Display Name",
                "Name shown to friends in RexNet multiplayer.",
                Order: 140),

            // Gameplay — Advanced
            ["video_skip_after"] = new(
                SettingsTab.Gameplay, "Advanced Gameplay & Network", SettingTier.Advanced, "Movie Skip Safety Delay",
                null, new NumericRange(0.0, 3.0, 0.05, " s"), Order: 510),
            ["pubgame_raster_scale_percent"] = new(
                SettingsTab.Gameplay, "Advanced Gameplay & Network", SettingTier.Advanced, "Pub Games Bitmap Scale (Fallback)",
                null, new NumericRange(0, 250, 25, "%", "Match Window"), Order: 520),
            ["rexnet_bootstrap"] = new(
                SettingsTab.Gameplay, "Advanced Gameplay & Network", SettingTier.Advanced, "RexNet DHT Bootstrap Nodes",
                Order: 530),

            // ==================== CONTROLS TAB ====================
            ["mouse_sensitivity"] = new(
                SettingsTab.Controls, "Mouse & Camera", SettingTier.Essential, "Mouse Look Sensitivity",
                "Camera rotation speed with the mouse (1.0x = default).",
                new NumericRange(0.1, 4.0, 0.05, "x"), Order: 10),
            ["mouse_zoom_sensitivity"] = new(
                SettingsTab.Controls, "Mouse & Camera", SettingTier.Essential, "Zoom-Scaled Aim Sensitivity",
                "Scales mouse look speed proportionally to FOV when aiming down zoomed sights.",
                Order: 15),
            ["mouse_input"] = new(
                SettingsTab.Controls, "Mouse & Camera", SettingTier.Essential, "Enable Mouse Camera & Menu Input",
                "Turn off to play purely on controller or keyboard without mouse capture.",
                Order: 20),
            ["mouse_lock"] = new(
                SettingsTab.Controls, "Mouse & Camera", SettingTier.Essential, "Confine Mouse Cursor to Window",
                "Locks the cursor inside the game window during active gameplay.",
                Order: 30),
            ["camera_invert_y"] = new(
                SettingsTab.Controls, "Mouse & Camera", SettingTier.Essential, "Invert Camera Vertical (Y-Axis)",
                Order: 40),
            ["camera_invert_x"] = new(
                SettingsTab.Controls, "Mouse & Camera", SettingTier.Essential, "Invert Camera Horizontal (X-Axis)",
                Order: 50),

            ["controller_vibration"] = new(
                SettingsTab.Controls, "Controller & Prompts", SettingTier.Essential, "Controller Rumble / Vibration",
                Order: 110),
            ["glyph_style"] = new(
                SettingsTab.Controls, "Controller & Prompts", SettingTier.Essential, "Button Prompt Style",
                "Auto-detects Xbox, PlayStation, or Switch controllers, or locks one icon set.",
                Order: 120),

            // Controls — Advanced
            ["glyph_pack"] = new(
                SettingsTab.Controls, "Custom Prompts", SettingTier.Advanced, "Custom Button Glyph Pack Folder",
                Order: 350),

            // ==================== DEBUG & PROFILING TAB ====================
            ["tracy"] = new(
                SettingsTab.Developer, "Profiling & Frame Capture", SettingTier.Essential, "Tracy Frame Profiler",
                "Enables live CPU/GPU telemetry zones for attachment with the Tracy profiler.",
                Order: 10),
            ["tracy_advanced"] = new(
                SettingsTab.Developer, "Profiling & Frame Capture", SettingTier.Essential, "Tracy Call-Stack Sampling",
                "Enables high-detail call-stack sampling inside Tracy captures.",
                Order: 15),
            ["gpu_profile"] = new(
                SettingsTab.Developer, "Profiling & Frame Capture", SettingTier.Essential, "Log Per-Pass GPU Timings",
                Order: 20),
            ["hitch_log_ms"] = new(
                SettingsTab.Developer, "Profiling & Frame Capture", SettingTier.Essential, "Frame Hitch Warning Threshold",
                null, new NumericRange(0, 500, 10, " ms", "Off"), Order: 25),
            ["renderdoc"] = new(
                SettingsTab.Developer, "Profiling & Frame Capture", SettingTier.Essential, "Arm RenderDoc Capture Layer (F12)",
                Order: 30),
            ["renderdoc_path"] = new(
                SettingsTab.Developer, "Profiling & Frame Capture", SettingTier.Essential, "RenderDoc Installation Path",
                Order: 35),

            ["developer_menu_enabled"] = new(
                SettingsTab.Developer, "Debug Overlays & Inspection", SettingTier.Essential, "In-Game Developer Menu (F6)",
                Order: 50),
            ["lighting_debug_view"] = new(
                SettingsTab.Developer, "Debug Overlays & Inspection", SettingTier.Essential, "Lighting Term Debug View",
                Order: 55),
            ["debug_channel"] = new(
                SettingsTab.Developer, "Debug Overlays & Inspection", SettingTier.Essential, "Loopback Debug Channel",
                Order: 60),
            ["postfx_dump_period"] = new(
                SettingsTab.Developer, "Debug Overlays & Inspection", SettingTier.Essential, "Post-FX Histogram Log Interval",
                Order: 65),
            ["postfx_flicker_trap"] = new(
                SettingsTab.Developer, "Debug Overlays & Inspection", SettingTier.Essential, "Luminance Jump Flicker Trap",
                Order: 70),
            ["fx_live_report"] = new(
                SettingsTab.Developer, "Debug Overlays & Inspection", SettingTier.Essential, "Periodic Live Particle Census Log",
                Order: 75),

            ["log_level"] = new(
                SettingsTab.Developer, "Logging & Trace Diagnostics", SettingTier.Essential, "Runtime Log Verbosity",
                Order: 110),
            ["log_verbose"] = new(
                SettingsTab.Developer, "Logging & Trace Diagnostics", SettingTier.Essential, "Verbose Trace Logging",
                Order: 115),
            ["log_noisy"] = new(
                SettingsTab.Developer, "Logging & Trace Diagnostics", SettingTier.Essential, "High-Frequency Noisy Logs",
                Order: 120),
            ["lua_trace_script_loads"] = new(
                SettingsTab.Developer, "Logging & Trace Diagnostics", SettingTier.Essential, "Trace Lua Script Loads",
                Order: 125),
            ["vfs_trace_loose_assets"] = new(
                SettingsTab.Developer, "Logging & Trace Diagnostics", SettingTier.Essential, "Trace Loose Asset Overrides",
                Order: 130),
            ["vfs_trace_vfs"] = new(
                SettingsTab.Developer, "Logging & Trace Diagnostics", SettingTier.Essential, "Trace VFS & Bank Streams",
                Order: 135),
            ["xam_trace_msgs"] = new(
                SettingsTab.Developer, "Logging & Trace Diagnostics", SettingTier.Essential, "Trace XAM App Messages",
                Order: 140),

            // Debug — Advanced
            ["log_file"] = new(
                SettingsTab.Developer, "Log File & Rotation", SettingTier.Advanced, "Custom Log File Path",
                Order: 180),
            ["log_flush_interval"] = new(
                SettingsTab.Developer, "Log File & Rotation", SettingTier.Advanced, "Periodic Log Flush Interval",
                null, new NumericRange(0, 60, 1, " s", "Default"), Order: 185),
            ["log_max_file_size_mb"] = new(
                SettingsTab.Developer, "Log File & Rotation", SettingTier.Advanced, "Max Log File Size",
                null, new NumericRange(1, 100, 1, " MB"), Order: 190),
            ["log_max_files"] = new(
                SettingsTab.Developer, "Log File & Rotation", SettingTier.Advanced, "Rotated Log Files Kept",
                null, new NumericRange(1, 100, 1), Order: 195),

            ["shader_warmup_enabled"] = new(
                SettingsTab.Developer, "Engine & Streaming Internals", SettingTier.Advanced, "Startup Shader Pipeline Warmup",
                Order: 210),
            ["gc_heaps"] = new(
                SettingsTab.Developer, "Engine & Streaming Internals", SettingTier.Advanced, ".NET GC Worker Heaps",
                null, new NumericRange(0, 16, 1, "", "Single-Threaded"), Order: 220),
            ["entity_skip_index"] = new(
                SettingsTab.Developer, "Engine & Streaming Internals", SettingTier.Advanced, "Static Entity Walk Skip Index",
                Order: 230),
            ["entity_still_reconcile_scale"] = new(
                SettingsTab.Developer, "Engine & Streaming Internals", SettingTier.Advanced, "Still Entity Reconcile Interval",
                null, new NumericRange(1, 8, 1, "x"), Order: 240),
            ["entity_create_budget_ms"] = new(
                SettingsTab.Developer, "Engine & Streaming Internals", SettingTier.Advanced, "Per-Frame Entity Spawn Budget",
                null, new NumericRange(0, 20, 0.5, " ms", "Legacy (8/frame)"), Order: 250),
            ["far_rig_pose_hz"] = new(
                SettingsTab.Developer, "Engine & Streaming Internals", SettingTier.Advanced, "Distant Character Pose Rate",
                null, new NumericRange(0, 30, 0.5, " Hz", "Default"), Order: 260),
            ["far_rig_pose_spread"] = new(
                SettingsTab.Developer, "Engine & Streaming Internals", SettingTier.Advanced, "Stagger Distant Character Poses",
                Order: 265),
            ["loh_compaction_defer_seconds"] = new(
                SettingsTab.Developer, "Engine & Streaming Internals", SettingTier.Advanced, "Deferred LOH Compaction Delay",
                null, new NumericRange(0, 120, 5, " s", "Immediate on Load"), Order: 270),
            ["hero_texture_sources_mb"] = new(
                SettingsTab.Developer, "Engine & Streaming Internals", SettingTier.Advanced, "Hero Morph Texture Cache Cap",
                null, new NumericRange(0, 2048, 64, " MB", "Unlimited"), Order: 280),
            ["anim_collapse_constant_tracks"] = new(
                SettingsTab.Developer, "Engine & Streaming Internals", SettingTier.Advanced, "Collapse Constant Animation Tracks",
                Order: 290),
            ["fx_cpu_fastpaths"] = new(
                SettingsTab.Developer, "Engine & Streaming Internals", SettingTier.Advanced, "Particle FX CPU Fast Paths",
                Order: 300),
            ["fx_no_depth_reads"] = new(
                SettingsTab.Developer, "Engine & Streaming Internals", SettingTier.Advanced, "Skip Soft-Edge Depth Reads in FX",
                Order: 310),
            ["sky_radiance_lean"] = new(
                SettingsTab.Developer, "Engine & Streaming Internals", SettingTier.Advanced, "Lean Sky Radiance Reflection Map",
                Order: 320),
        };
}
