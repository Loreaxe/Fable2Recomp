namespace Refii.Tests.Modding;

using System.IO;
using GdUnit4;
using Godot;
using static GdUnit4.Assertions;

/// <summary>Skate's panel, the Portal Gun on a controller, and how a mod's pad bindings match a real joypad.</summary>
[TestSuite]
public class ModPadBindingsTests
{
    private static string Read(string root, string mod, params string[] parts)
    {
        string path = GodotModScripts.ModFolder(root, mod);
        foreach (string part in parts) path = Path.Combine(path, part);
        return File.ReadAllText(path);
    }

    /// <summary>Skate's controls have no switch any more: riding with a pad or keyboard always shows them.</summary>
    [TestCase]
    public void SkateControlsAlwaysShowWhileRiding()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        AssertThat(Read(root, "skate", "input.json")).NotContains("\"help");
        string script = Read(root, "skate", "godot", "skate.gd");
        AssertThat(script).NotContains("show_controls").NotContains("skate.help");
        AssertThat(script).Contains("if not host.touch_mode():\n\t\t_draw_controls(scale)");
    }

    /// <summary>A prompt a mod draws uses the game's own button pictures, from the paths the game names.</summary>
    [TestCase]
    public void ModPromptsUseTheGamesButtonArt()
    {
        AssertThat(Refii.Ui.Engine_UiKeyGlyphs.ShippedArtPath("A")).IsEqual("art/gui/controller/icon_button_a.tex");
        AssertThat(Refii.Ui.Engine_UiKeyGlyphs.ShippedArtPath("LB")).IsEqual("art/gui/controller/24x24/leftbutton.tex");
        AssertThat(Refii.Ui.Engine_UiKeyGlyphs.ShippedArtPath("DPad")).IsEqual("art/gui/controller/24x24/dpad.tex");
        AssertThat(Refii.Ui.Engine_UiKeyGlyphs.ShippedArtPath("Nope")).IsNull();
    }

    /// <summary>A controller's Y only aims; a trigger fires, and only while Y is down.</summary>
    [TestCase]
    public void PortalGunAimsOnYAndFiresOnTheTriggers()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string input = Read(root, "portal_gun", "input.json");
        AssertThat(input).Contains("\"fire_blue\": { \"label\": \"{PORTAL_GUN_INPUT_FIRE_BLUE}\", \"buttons\": [\"LT\"] }");
        AssertThat(input).Contains("\"fire_orange\": { \"label\": \"{PORTAL_GUN_INPUT_FIRE_ORANGE}\", \"buttons\": [\"RT\"] }");
        string script = Read(root, "portal_gun", "godot", "portal_gun.gd");
        AssertThat(script).Contains("func _pad_fire(which: int) -> void:\n\tif not pad_aim or pad_rearm > 0.0:\n\t\treturn");
        AssertThat(script).Contains("elif name == \"portal_gun.fire_blue\":\n\t\t_pad_fire(1)");
        AssertThat(script).Contains("elif name == \"portal_gun.fire_orange\":\n\t\t_pad_fire(2)");
        AssertThat(script).Contains("(left_held or right_held or pad_aim)");

        // Y holds the game's own aim; letting go of it without a trigger opens nothing.
        AssertThat(script).Contains("host.press(player, \"Y\", 1 if left_held or right_held or (pad_aim and pad_rearm <= 0.0) else 0)");
        AssertThat(script).Contains("if not pressed and pad_rearm <= 0.0:\n\t\tshot_void_left = TAP_AIM_SECONDS");
        AssertThat(script).Contains("if shot_void_left > 0.0:\n\t\t\t\tshot_void_left = 0.0\n\t\t\telse:\n\t\t\t\t_fire(colour)");
        AssertThat(Read(root, "portal_gun", "mod", "camera.lua")).Contains("ranged_tightness = 0.1");
    }

    /// <summary>On a controller melee and magic stay the game's, and the triggers are the gun's only while Y aims.</summary>
    [TestCase]
    public void PortalGunLeavesAControllersOtherButtonsAlone()
    {
        string? root = GodotModScripts.RepoRoot();
        if (root == null)
        {
            return;
        }

        string script = Read(root, "portal_gun", "godot", "portal_gun.gd");
        AssertThat(script).Contains("var mouse: bool = claimed and not touch and not on_pad");
        AssertThat(script).Contains("var triggers: bool = claimed and not touch and pad_aim");
        AssertThat(script).Contains("host.claim_input(player, \"portal_gun.fire_orange\", triggers)");
        AssertThat(script).Contains("host.claim_input(player, \"portal_gun.no_melee\", mouse)");
        AssertThat(script).Contains("host.claim_input(player, \"portal_gun.no_magic\", mouse)");
    }

    /// <summary>A mod's pad binding in the InputMap answers a press from a real joypad (the Satchel's A, B, X and Y).</summary>
    [TestCase, RequireGodotRuntime]
    public void AnInputMapPadBindingMatchesAJoypadPress()
    {
        const string action = "refii_test.all_devices";
        InputMap.AddAction(action);
        try
        {
            InputMap.ActionAddEvent(action, new InputEventJoypadButton { ButtonIndex = JoyButton.A, Device = Refii.Mods.ModInput.AllDevices });
            var press = new InputEventJoypadButton { ButtonIndex = JoyButton.A, Pressed = true, Device = 0 };
            AssertThat(press.IsActionPressed(action)).IsTrue();
        }
        finally
        {
            InputMap.EraseAction(action);
        }
    }
}
