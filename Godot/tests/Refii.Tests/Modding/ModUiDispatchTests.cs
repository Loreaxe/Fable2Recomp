namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;
using Refii.Ui;
using Refii.Titles.Fable2;

/// <summary>
/// The rebuilt UI on the surfaces mods use (Part G.18): the one command dispatcher and its
/// refusals, the open-screen verb's argument, the game's templates as a file composed like a mod's
/// (and identical to the authored records), screen patches merged per field, the one pause root
/// under row conditions, the value resolver a data page and a row source bind through, and the
/// mod row sources' bus text. Pure .NET; no Godot type in the host.
/// </summary>
[TestSuite]
public class ModUiDispatchTests
{
    private static List<ModIssue> Check(string path, string text) => ModValidate.Validate(path, text).ToList();

    private static string Lines(IEnumerable<ModIssue> issues) =>
        string.Join("\n", issues.Select(i => (i.Error ? "error: " : "warning: ") + i));

    // ---- G.18 #7 the game is a mod ---------------------------------------------------------

    /// <summary>
    /// THE SHIPPED FILE IS THE AUTHORED RECORDS. The game runs <see cref="Engine_UiGameTemplates.Text"/>
    /// through the reader every mod's file takes; composed, it must be the C#-authored records record
    /// for record, byte for byte in the snapshot's form. When the authored records change, regenerate
    /// the file: set REFII_WRITE_GAME_TEMPLATES to a path and run this test, then paste the text.
    /// </summary>
    [TestCase]
    public void TheGamesTemplatesFileComposesToTheAuthoredRecords()
    {
        if (Environment.GetEnvironmentVariable("REFII_WRITE_GAME_TEMPLATES") is { Length: > 0 } path)
        {
            System.IO.File.WriteAllText(path, Fable2UiTemplates.GameTemplatesText());
        }

        string authored = Fable2UiTemplates.AuthoredDefaults().SnapshotText();
        string composed = Engine_UiTemplates.ComposeGame().SnapshotText();
        int at = 0;
        while (at < Math.Min(authored.Length, composed.Length) && authored[at] == composed[at])
        {
            ++at;
        }

        AssertThat(composed == authored)
            .OverrideFailureMessage($"the game's file composes differently from the authored records at char {at}: " +
                $"authored '…{authored.Substring(Math.Max(0, at - 80), Math.Min(160, authored.Length - Math.Max(0, at - 80)))}…' " +
                $"composed '…{composed.Substring(Math.Max(0, at - 80), Math.Min(160, composed.Length - Math.Max(0, at - 80)))}…'")
            .IsTrue();

        // And the file is what the generator writes now — nothing stale left in it.
        AssertThat(Engine_UiGameTemplates.Text.Trim().Replace("\r\n", "\n") == Fable2UiTemplates.GameTemplatesText().Trim().Replace("\r\n", "\n")).IsTrue();
    }

    [TestCase]
    public void TheGamesTemplatesFileValidatesClean()
    {
        List<ModIssue> issues = Check(Engine_UiTemplates.ModTemplatesPath, Engine_UiGameTemplates.Text);
        AssertThat(issues.Count(i => i.Error)).OverrideFailureMessage("the game's file has errors:\n" + Lines(issues)).IsEqual(0);

        // Every field the game states is one the validator knows, and none is one that draws nothing.
        AssertThat(issues.Count(i => i.Message.Contains("is not a field here", StringComparison.Ordinal) ||
                i.Message.Contains("draws nothing", StringComparison.Ordinal) ||
                i.Message.Contains("is not a condition the game states", StringComparison.Ordinal)))
            .OverrideFailureMessage("the game's file states fields or conditions the validator rejects:\n" + Lines(issues)).IsEqual(0);
    }

    [TestCase]
    public void TheSnapshotReadsBackToItself()
    {
        Engine_UiTemplateSet game = Engine_UiTemplates.ComposeGame();
        string snapshot = game.SnapshotText();
        var again = new Engine_UiTemplateSet();
        again.ApplyText(snapshot, string.Empty, "snapshot");
        AssertThat(again.SnapshotText()).IsEqual(snapshot);
    }

    // ---- G.18 #7 one pause root --------------------------------------------------------------

    [TestCase]
    public void OnePauseRootServesBothPlayers()
    {
        Engine_UiTemplateSet game = Engine_UiTemplates.ComposeGame();
        AssertThat(game.Menus.ContainsKey("pause-root-coop")).IsFalse();
        Engine_UiMenuTemplate pause = game.Menus[Engine_PauseMenu.RootTemplate];

        static List<string> Built(Engine_UiMenuTemplate menu, bool playerTwo) => menu.Entries
            .Where(e => Engine_UiRowConditions.Holds(e.When, Engine_UiRowConditions.PauseFacts(playerTwo)))
            .Select(e => e.Name + (e.HostOnly ? "*" : string.Empty)).ToList();

        AssertThat(string.Join(",", Built(pause, false))).IsEqual(
            "Saving,Map,Abilities,Items,Weapons,Clothing,Logbook,Achievements*,Multiplayer*,Options,Kick*");
        AssertThat(string.Join(",", Built(pause, true))).IsEqual(
            "Map*,Abilities*,Items*,Weapons*,Clothing*,Logbook*,Achievements*,Multiplayer*,Options,HenchmanQuit*");
        AssertThat(pause.Entries.Single(e => e.Name == "Achievements").Command)
            .IsEqual(Engine_UiHostScreens.OpenCommand(Engine_UiAchievementPage.ScreenId));

        // Player two's way out is the original's one row (`sub_8298EDE0`): the game's own key, the
        // guest's own box behind it, and no quit to the title or the desktop under it.
        Engine_UiMenuEntry quit = pause.Entries.Single(e => e.Name == "HenchmanQuit");
        AssertThat(quit.Tag).IsEqual("GUI_SCREEN_PAUSE_HENCHMAN_QUIT");
        AssertThat(quit.Command).IsEqual(Engine_PauseMenu.HenchmanQuitCommand);
        AssertThat(quit.Children.Count).IsEqual(0);
        AssertThat(Fable2PortedScreens.HenchmanQuit(2)).IsEqual("PauseHenchmanQuit:2");

        // The console's quit pair says the game's own words.
        Engine_UiMenuEntry saving = pause.Entries.Single(e => e.Name == "Saving");
        AssertThat(saving.Children.Single(e => e.Name == "SaveAndQuit").Tag).IsEqual("GUI_SCREEN_PAUSE_SAVE_AND_QUIT");
        AssertThat(saving.Children.Single(e => e.Name == "QuitDontSave").Tag).IsEqual("GUI_SCREEN_PAUSE_QUIT_DONT_SAVE");
    }

    [TestCase]
    public void RowConditionsReadNamesNegationsAndLists()
    {
        var facts = new Dictionary<string, bool> { ["a"] = true, ["b"] = false };
        AssertThat(Engine_UiRowConditions.Holds(string.Empty, facts)).IsTrue();
        AssertThat(Engine_UiRowConditions.Holds("a", facts)).IsTrue();
        AssertThat(Engine_UiRowConditions.Holds("!a", facts)).IsFalse();
        AssertThat(Engine_UiRowConditions.Holds("a, !b", facts)).IsTrue();
        AssertThat(Engine_UiRowConditions.Holds("a,b", facts)).IsFalse();

        // A name nobody states does not hold.
        AssertThat(Engine_UiRowConditions.Holds("nobody.states.this", facts)).IsFalse();
        AssertThat(Engine_UiRowConditions.Holds("!nobody.states.this", facts)).IsTrue();
        AssertThat(string.Join(",", Engine_UiRowConditions.NamesIn("!a, b"))).IsEqual("a,b");
    }

    [TestCase]
    public void APatchReachesEveryVariantOrTheOneItNames()
    {
        Engine_UiTemplateSet game = Engine_UiTemplates.ComposeGame();

        // One mod adds a row after Options: one patch, both players.
        game.ApplyText("{ \"menus\": { \"pause-root\": { \"patch\": true, \"entries\": [" +
            "{ \"name\": \"Bestiary\", \"tag\": \"Bestiary\", \"command\": \"Screen:open:1050\", \"after\": \"Options\" } ] } } }",
            "bestiary", "test");
        List<string> names = game.Menus["pause-root"].Entries.Select(e => e.Name).ToList();
        AssertThat(names.IndexOf("Bestiary")).IsEqual(names.IndexOf("Options") + 1);

        // A patch naming no "when" replaces every variant with its one row.
        game.ApplyText("{ \"menus\": { \"pause-root\": { \"patch\": true, \"entries\": [" +
            "{ \"name\": \"Map\", \"tag\": \"Atlas\", \"command\": \"PauseTab:2\" } ] } } }", "atlas", "test");
        AssertThat(game.Menus["pause-root"].Entries.Count(e => e.Name == "Map")).IsEqual(1);

        // A patch naming a "when" addresses that variant alone.
        Engine_UiTemplateSet again = Engine_UiTemplates.ComposeGame();
        again.ApplyText("{ \"menus\": { \"pause-root\": { \"patch\": true, \"entries\": [" +
            "{ \"name\": \"Items\", \"when\": \"player-two\", \"remove\": true } ] } } }", "p2", "test");
        List<Engine_UiMenuEntry> items = again.Menus["pause-root"].Entries.Where(e => e.Name == "Items").ToList();
        AssertThat(items.Count).IsEqual(1);
        AssertThat(items[0].When).IsEqual("!player-two");
    }

    [TestCase]
    public void TheValidatorReadsTheNewForms()
    {
        List<ModIssue> issues = Check(Engine_UiTemplates.ModTemplatesPath,
            "{ \"menus\": { \"pause-root\": { \"patch\": true, \"entries\": [" +
            "  { \"name\": \"A\", \"tag\": \"A\", \"when\": \"!player-two, coop\", \"command\": \"Screen:open:1050\" }," +
            "  { \"name\": \"B\", \"tag\": \"B\", \"when\": \"nobody\", \"command\": \"Screen:open:x\" }," +
            "  { \"name\": \"C\", \"tag\": \"C\", \"source\": \"values:mod:\" } ] } }," +
            " \"screens\": {" +
            "  \"17\": { \"patch\": true, \"fields\": { \"P.T\": \"row:health\", \"P.U\": null } }," +
            "  \"1050\": { \"provider\": \"data\", \"fields\": { \"P.T\": \"player:souls.level|0\" } } } }");
        string all = Lines(issues);
        AssertThat(issues.Any(i => i.Error && i.Message.Contains("Screen:open:x", StringComparison.Ordinal)))
            .OverrideFailureMessage(all).IsTrue();
        AssertThat(issues.Any(i => !i.Error && i.Message.Contains("'nobody' is not a condition", StringComparison.Ordinal)))
            .OverrideFailureMessage(all).IsTrue();
        AssertThat(issues.Any(i => i.Error && i.Pointer.Contains("source", StringComparison.Ordinal)))
            .OverrideFailureMessage(all).IsTrue();
        AssertThat(issues.Any(i => i.Error && i.Message.Contains("row:", StringComparison.Ordinal)))
            .OverrideFailureMessage(all).IsTrue();
        AssertThat(issues.Any(i => !i.Error && i.Message.Contains("cannot stand", StringComparison.Ordinal)))
            .OverrideFailureMessage(all).IsTrue();

        // The good forms say nothing: a known condition, a screen id, a patch's null, a player: binding.
        AssertThat(issues.Any(i => i.Pointer.Contains("entries/0", StringComparison.Ordinal)))
            .OverrideFailureMessage(all).IsFalse();
        AssertThat(issues.Any(i => i.Pointer.Contains("P.U", StringComparison.Ordinal))).OverrideFailureMessage(all).IsFalse();
        AssertThat(issues.Any(i => i.Message.Contains("not a key", StringComparison.OrdinalIgnoreCase) &&
            i.Pointer.Contains("patch", StringComparison.Ordinal))).OverrideFailureMessage(all).IsFalse();
    }

    [TestCase]
    public void TheStudiosPatchOfOneVariantReachesThatVariant()
    {
        Engine_UiTemplateSet game = Engine_UiTemplates.ComposeGame();
        JsonObject baseMenu = Engine_UiTemplateJson.MenuJson(game.Menus["pause-root"]);
        var edited = (JsonObject)baseMenu.DeepClone();
        foreach (JsonNode? node in (JsonArray)edited["entries"]!)
        {
            if (node is JsonObject entry && Engine_UiMenuEdit.Name(entry) == "Map" &&
                Engine_UiMenuEdit.Text(entry, "when") == "player-two")
            {
                entry["tag"] = "Atlas";
            }
        }

        JsonObject patch = Engine_UiMenuEdit.Patch(baseMenu, edited)!;
        JsonArray ops = (JsonArray)patch["entries"]!;
        AssertThat(ops.Count).IsEqual(1);
        AssertThat(Engine_UiMenuEdit.Text((JsonObject)ops[0]!, "when")).IsEqual("player-two");

        game.ApplyText(new JsonObject { ["menus"] = new JsonObject { ["pause-root"] = patch } }.ToJsonString(), "atlas", "test");
        List<Engine_UiMenuEntry> maps = game.Menus["pause-root"].Entries.Where(e => e.Name == "Map").ToList();
        AssertThat(maps.Count).IsEqual(2);
        AssertThat(maps.Single(e => e.When == "player-two").Tag).IsEqual("Atlas");
        AssertThat(maps.Single(e => e.When == "!player-two").Tag).IsEqual("GUI_SCREEN_PAUSE_WORLD_MAP");
    }

    // ---- G.18 #7 screen patches --------------------------------------------------------------

    [TestCase]
    public void AScreenPatchMergesPerField()
    {
        Engine_UiTemplateSet game = Engine_UiTemplates.ComposeGame();
        Engine_UiScreenTemplate before = game.Screens[17];
        string presentation = before.Presentation;
        int chrome = before.ChromeUnits.Count;

        game.ApplyText("{ \"screens\": { \"17\": { \"patch\": true, \"backdropUnit\": \"my_backdrop\"," +
            " \"fields\": { \"Foreground.Book.Title\": \"mod:my.title\" } } } }", "logbook_mod", "test");
        Engine_UiScreenTemplate after = game.Screens[17];
        AssertThat(after.BackdropUnit).IsEqual("my_backdrop");
        AssertThat(after.Presentation).IsEqual(presentation);
        AssertThat(after.ChromeUnits.Count).IsEqual(chrome);
        AssertThat(after.Fields["Foreground.Book.Title"]).IsEqual("mod:my.title");
        AssertThat(game.Owners["screen:17/backdropUnit"]).IsEqual("logbook_mod");
        AssertThat(game.Owners.ContainsKey("screen:17")).IsFalse();

        // The record the patch started from is untouched; a second patch merges into the map.
        AssertThat(before.BackdropUnit == "my_backdrop").IsFalse();
        game.ApplyText("{ \"screens\": { \"17\": { \"patch\": true, \"fields\": { \"Foreground.Book.Text\": \"Hello\"," +
            " \"Foreground.Book.Title\": null } } } }", "other", "test");
        AssertThat(game.Screens[17].Fields.ContainsKey("Foreground.Book.Text")).IsTrue();
        AssertThat(game.Screens[17].Fields.ContainsKey("Foreground.Book.Title")).IsFalse();

        // A whole record still replaces the whole record.
        game.ApplyText("{ \"screens\": { \"17\": { \"name\": \"mine\" } } }", "whole", "test");
        AssertThat(game.Screens[17].Presentation).IsEqual(string.Empty);
        AssertThat(game.Screens[17].ListLift).IsEqual(Engine_UiPresentationDepth.BookOffset);
    }

    [TestCase]
    public void AModScreenRecordReadsItsDataPage()
    {
        var set = new Engine_UiTemplateSet();
        set.ApplyText("{ \"screens\": { \"1050\": { \"name\": \"bestiary\", \"presentation\": \"logbookscreen\"," +
            " \"menu\": \"bestiary-root\", \"provider\": \"data\", \"fields\": { \"A.B\": \"row:label\" }," +
            " \"switches\": { \"A.Page\": \"true\" } } } }", "bestiary", "test");
        Engine_UiScreenTemplate screen = set.Screens[1050];
        AssertThat(screen.Provider).IsEqual(Engine_UiPageProviders.Data);
        AssertThat(screen.Menu).IsEqual("bestiary-root");
        JsonObject written = Engine_UiTemplateJson.ScreenJson(screen, full: false);
        AssertThat(written["fields"]!["A.B"]!.GetValue<string>()).IsEqual("row:label");
        AssertThat(written.ContainsKey("chrome")).IsFalse();
    }

    // ---- G.18 #1 the one dispatcher ----------------------------------------------------------

    private static Engine_UiCommandSet Dispatcher(List<string> ran)
    {
        var set = new Engine_UiCommandSet();
        set.Register(Fable2UiVerbs.PauseTab, (argument, context) =>
        {
            ran.Add("tab " + argument + " " + context.Row);
            return Engine_UiCommandResult.Done;
        });
        set.Register(Fable2UiVerbs.Inventory, (argument, _) =>
        {
            ran.Add("inventory " + argument);
            return Engine_UiCommandResult.Done;
        });
        set.Register(new Engine_UiVerb(Engine_UiDyeFlow.SelectAction, "x", "x", Array.Empty<Engine_UiCommandList>()), (argument, _) =>
        {
            ran.Add("dye " + argument);
            return Engine_UiCommandResult.Done;
        });
        set.Register(Engine_UiVerbs.QuitToDesktop, (_, context) =>
        {
            ran.Add("quit " + context.Command);
            return Engine_UiCommandResult.Done;
        });
        return set;
    }

    [TestCase]
    public void TheDispatcherMatchesTheLongestVerb()
    {
        var ran = new List<string>();
        Engine_UiCommandSet set = Dispatcher(ran);
        var pause = new Engine_UiCommandContext(Engine_UiCommandList.Pause, "pause-root", "Map");

        AssertThat(set.Run("PauseTab:2", pause).Outcome).IsEqual(Engine_UiCommandOutcome.Done);
        AssertThat(set.Run("Inventory:use:77", pause).Outcome).IsEqual(Engine_UiCommandOutcome.Done);
        AssertThat(set.Run("Inventory:dyeselect:5", pause).Outcome).IsEqual(Engine_UiCommandOutcome.Done);
        AssertThat(string.Join("|", ran)).IsEqual("tab 2 Map|inventory use:77|dye 5");

        // An exact verb is the whole command, not a prefix of a longer one.
        AssertThat(set.Run("QuitToDesktopNow", new Engine_UiCommandContext(Engine_UiCommandList.FrontEnd)).Outcome)
            .IsEqual(Engine_UiCommandOutcome.NotOurs);

        // A command no verb claims is the list's own — the front end posts it to the guest by name.
        AssertThat(set.Run("New Game", new Engine_UiCommandContext(Engine_UiCommandList.FrontEnd)).Outcome)
            .IsEqual(Engine_UiCommandOutcome.NotOurs);
        AssertThat(set.Run(null, pause).Outcome).IsEqual(Engine_UiCommandOutcome.NotOurs);
    }

    [TestCase]
    public void AVerbRefusesOnAListItDoesNotRunOn()
    {
        var ran = new List<string>();
        Engine_UiCommandSet set = Dispatcher(ran);
        Engine_UiCommandResult onScreen = set.Run("PauseTab:3", new Engine_UiCommandContext(Engine_UiCommandList.Screen));
        AssertThat(onScreen.Outcome).IsEqual(Engine_UiCommandOutcome.Refused);
        AssertThat(onScreen.Message).Contains("pause list only");
        AssertThat(set.Run("QuitToDesktop", new Engine_UiCommandContext(Engine_UiCommandList.Pause)).Outcome)
            .IsEqual(Engine_UiCommandOutcome.Refused);
        AssertThat(ran.Count).IsEqual(0);
    }

    [TestCase]
    public void AModVerbNeverReplacesTheGamesAndFailsAlone()
    {
        var ran = new List<string>();
        Engine_UiCommandSet set = Dispatcher(ran);
        var theirs = new Engine_UiVerb(Fable2UiVerbs.PauseTab.Name, "x", "x", Array.Empty<Engine_UiCommandList>(), "mod");
        AssertThat(set.Register(theirs, (_, _) => Engine_UiCommandResult.Done)).IsFalse();

        var own = new Engine_UiVerb("Bestiary:", "Bestiary:<beast>", "x", Array.Empty<Engine_UiCommandList>(), "mod");
        AssertThat(set.Register(own, (_, _) => throw new InvalidOperationException("boom"))).IsTrue();
        Engine_UiCommandResult failed = set.Run("Bestiary:troll", new Engine_UiCommandContext(Engine_UiCommandList.HostList));
        AssertThat(failed.Outcome).IsEqual(Engine_UiCommandOutcome.Refused);
        AssertThat(failed.Message).Contains("boom");
        AssertThat(set.Unregister("Bestiary:", "mod")).IsTrue();
        AssertThat(set.Unregister(Fable2UiVerbs.PauseTab.Name, "game")).IsFalse();
    }

    [TestCase]
    public void TheGameVerbsAreDescribedOnce()
    {
        AssertThat(Engine_UiVerbs.Game.Select(v => v.Name).Distinct().Count()).IsEqual(Engine_UiVerbs.Game.Count);
        AssertThat(Engine_UiVerbs.Of("PauseTab:4")).IsEqual(Fable2UiVerbs.PauseTab);
        AssertThat(Engine_UiVerbs.Of("Inventory:dyeselect:3")).IsEqual(Fable2UiVerbs.Inventory);
        AssertThat(Engine_UiVerbs.Of("Screen:open:1013")).IsEqual(Engine_UiVerbs.OpenScreen);
        AssertThat(Engine_UiVerbs.Of("New Game")).IsNull();
        AssertThat(Fable2UiVerbs.PauseTab.RunsOn(Engine_UiCommandList.Screen)).IsFalse();
        AssertThat(Engine_UiVerbs.Lua.RunsOn(Engine_UiCommandList.Screen)).IsTrue();
    }

    [TestCase]
    public void TheOpenScreenVerbReadsAnId()
    {
        AssertThat(Engine_UiHostScreens.OpenCommand(1050)).IsEqual("Screen:open:1050");
        AssertThat(Engine_UiHostScreens.ParseId("1013")).IsEqual(1013u);
        AssertThat(Engine_UiHostScreens.ParseId(" 7 ")).IsEqual(7u);
        AssertThat(Engine_UiHostScreens.ParseId("x").HasValue).IsFalse();
        AssertThat(Engine_UiHostScreens.ParseId("-3").HasValue).IsFalse();
    }

    // ---- G.18 #2 / #3 the value resolver, data pages and row sources ----------------------------

    private static Engine_UiValueSource.Lookups Values(string row = "troll", string label = "Troll") => new(
        Values: new Engine_UiValueSources.Context
        {
            Player = 2,
            Mod = key => key switch { "souls.stamina" => 42.5f, "bestiary.troll.kills" => 3f, _ => null },
            PlayerValue = (player, key) => player == 2 && key == "souls.level" ? 7f : null,
        },
        Tag: tag => tag == "GUI_OK" ? "OK" : null,
        RowName: row, RowLabel: label);

    [TestCase]
    public void BindingsResolveEveryForm()
    {
        Engine_UiValueSource.Lookups lookups = Values();
        AssertThat(Engine_UiValueSource.Resolve("mod:souls.stamina", lookups)).IsEqual("42.5");
        AssertThat(Engine_UiValueSource.Resolve("mod:souls.stamina|0.00", lookups)).IsEqual("42.50");
        AssertThat(Engine_UiValueSource.Resolve("player:souls.level|Level {0}", lookups)).IsEqual("Level 7");
        AssertThat(Engine_UiValueSource.Resolve("mod:bestiary.{row}.kills", lookups)).IsEqual("3");
        AssertThat(Engine_UiValueSource.Resolve("row:label", lookups)).IsEqual("Troll");
        AssertThat(Engine_UiValueSource.Resolve("row:name", lookups)).IsEqual("troll");
        AssertThat(Engine_UiValueSource.Resolve("GUI_OK", lookups)).IsEqual("OK");
        AssertThat(Engine_UiValueSource.Resolve("Plain words | kept", lookups)).IsEqual("Plain words | kept");

        // Not stated says nothing — never a number it is not.
        AssertThat(Engine_UiValueSource.Resolve("mod:nobody", lookups)).IsNull();
        AssertThat(Engine_UiValueSource.Resolve("player:nobody", lookups)).IsNull();
        AssertThat(Engine_UiValueSource.Resolve("   ", lookups)).IsNull();

        AssertThat(Engine_UiValueSource.Truth("mod:souls.stamina", lookups)).IsTrue();
        AssertThat(Engine_UiValueSource.Truth("mod:nobody", lookups)).IsFalse();
        AssertThat(Engine_UiValueSource.Truth("true", lookups)).IsTrue();
        AssertThat(Engine_UiValueSource.Truth("false", lookups)).IsFalse();
        AssertThat(Engine_UiValueSource.Problem("mod:")).IsNotNull();
        AssertThat(Engine_UiValueSource.Problem("row:health")).IsNotNull();
        AssertThat(Engine_UiValueSource.Problem("player:souls.level|0")).IsNull();
    }

    [TestCase]
    public void ADataPageFillsItsFieldsAndSwitches()
    {
        Engine_UiValueSource.Lookups lookups = Values();
        Dictionary<string, string> values = Engine_UiDataPage.Fill(new Dictionary<string, string>
        {
            ["Page.Title"] = "row:label",
            ["Page.Kills"] = "mod:bestiary.{row}.kills|0 slain",
            ["Page.Missing"] = "mod:nobody",
        }, lookups);
        AssertThat(values["Page.Title"]).IsEqual("Troll");
        AssertThat(values["Page.Kills"]).IsEqual("3 slain");
        AssertThat(values.ContainsKey("Page.Missing")).IsFalse();

        Dictionary<string, bool> active = Engine_UiDataPage.Switch(new Dictionary<string, string>
        {
            ["Page"] = "true", ["Other"] = "mod:nobody",
        }, lookups);
        AssertThat(active["Page"]).IsTrue();
        AssertThat(active["Other"]).IsFalse();
    }

    [TestCase]
    public void RowsCrossTheBusAndReadBack()
    {
        var rows = new List<Engine_UiModRows.Stated>
        {
            new("troll", "Troll; the big one", "Mod:lua:bestiary.show:troll", false),
            new("balverine", "Bal|verine", null, true),
            new("back\\slash", string.Empty, null, false),
        };
        string text = Engine_UiModRows.RowsText(rows);
        List<Engine_UiModRows.Stated> back = Engine_UiModRows.ParseRows(text);
        AssertThat(back.Count).IsEqual(3);
        AssertThat(back[0]).IsEqual(rows[0]);
        AssertThat(back[1]).IsEqual(rows[1]);
        AssertThat(back[2].Name).IsEqual("back\\slash");

        // The Lua preamble's shortest form: a name alone is its own label.
        AssertThat(Engine_UiModRows.ParseRows("a|a||0;b|b||1").Select(r => r.Name + r.Ghosted).ToList())
            .ContainsExactly("aFalse", "bTrue");
        AssertThat(Engine_UiModRows.ParseRows(string.Empty).Count).IsEqual(0);

        Engine_UiModRows.Request? request = Engine_UiModRows.Parse(RefiiHost.ParseFieldsPlain(
            "op='menu_rows',player=2,menu='bestiary-root',rows='" + text + "'"));
        AssertThat(request!.Value.Op).IsEqual(Engine_UiModRows.Op.MenuRows);
        AssertThat(request.Value.Player).IsEqual(2);
        AssertThat(request.Value.Rows.Count).IsEqual(3);
        AssertThat(Engine_UiModRows.Parse(RefiiHost.ParseFieldsPlain("op='open_screen',player=0,screen=1050"))!.Value.Screen)
            .IsEqual(1050u);
        AssertThat(Engine_UiModRows.Parse(RefiiHost.ParseFieldsPlain("op='open_screen',screen=0")).HasValue).IsFalse();
    }

    // A stated label is "a Babel tag or literal": the list draws a sourced row's label as text, so
    // the rows source resolves it, and a name alone stands in for an empty label before that.
    [TestCase]
    public void StatedRowLabelsResolveThroughTheBook()
    {
        var book = new Dictionary<string, string> { ["INV_ITEM_POTION_HEALTH_1_NAME"] = "Health Potion" };
        var stated = new List<Engine_UiModRows.Stated>
        {
            new("p1", "INV_ITEM_POTION_HEALTH_1_NAME", "Mod:lua:hotbar.assign:1", false),
            new("p2", "4   Inferno", null, false),
            new("POTION", string.Empty, null, false),
        };

        List<Engine_UiMenuSources.Row> rows = Engine_UiModRows.ToRows(stated,
            label => book.TryGetValue(label, out string? text) ? text : label);
        AssertThat(rows.Select(r => r.Label).ToList()).ContainsExactly("Health Potion", "4   Inferno", "POTION");
        AssertThat(rows[0].Command).IsEqual("Mod:lua:hotbar.assign:1");

        // As stated with no resolver, and the book's resolver keeps a literal with no book loaded.
        AssertThat(Engine_UiModRows.ToRows(stated)[0].Label).IsEqual("INV_ITEM_POTION_HEALTH_1_NAME");
        AssertThat(Engine_UiGuiText.Resolve("4   Inferno", null)).IsEqual("4   Inferno");
    }

    [TestCase]
    public void SetMenuRowsLuaCompiles()
    {
        bool ok = Refii.Lua.Engine_LuaDecompiler.TryCheckSource(ModLua.MenuRowsRuntime, "@set_menu_rows", out string error);
        if (!ok && error.Contains("not available", StringComparison.Ordinal))
        {
            return;  // no compiler in this test host: the mount checks the init chunk at run time
        }

        AssertThat(ok).OverrideFailureMessage($"set_menu_rows does not compile: {error}").IsTrue();
    }

    [TestCase]
    public void TheMenuSurfacesAreOnEverySurface()
    {
        string native = string.Join("\n", ModApi.Helpers.Select(h => h.Signature));
        AssertThat(native.Contains("refii_mod_set_menu_rows(", StringComparison.Ordinal)).IsTrue();
        AssertThat(native.Contains("refii_mod_open_screen(", StringComparison.Ordinal)).IsTrue();
        string lua = string.Join("\n", ModApi.LuaHelpers.Select(h => h.Signature));
        AssertThat(lua.Contains("Refii.set_menu_rows(", StringComparison.Ordinal)).IsTrue();
        AssertThat(lua.Contains("Refii.open_screen(", StringComparison.Ordinal)).IsTrue();
        string godot = string.Join("\n", ModApi.GodotHelpers.Select(h => h.Signature));
        AssertThat(godot.Contains("set_menu_rows(menu, rows, player", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("open_screen(screen, player", StringComparison.Ordinal)).IsTrue();
        AssertThat(godot.Contains("register_command(verb, handler", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void StatedRowsArePerPlayer()
    {
        Engine_UiModRows.Clear();
        Engine_UiModRows.Set("list", new[] { new Engine_UiModRows.Stated("shared", "Shared", null, false) }, 0);
        Engine_UiModRows.Set("list", new[] { new Engine_UiModRows.Stated("two", "Two", null, false) }, 2);
        AssertThat(Engine_UiModRows.For("list", 1).Single().Name).IsEqual("shared");
        AssertThat(Engine_UiModRows.For("list", 2).Single().Name).IsEqual("two");
        Engine_UiModRows.Set("list", Array.Empty<Engine_UiModRows.Stated>(), 2);
        AssertThat(Engine_UiModRows.For("list", 2).Single().Name).IsEqual("shared");
        Engine_UiModRows.Clear();
        AssertThat(Engine_UiModRows.For("list", 1).Count).IsEqual(0);
    }

    [TestCase]
    public void ValueRowsLabelPublishedValues()
    {
        List<Engine_UiModRows.Stated> rows = Engine_UiModRows.ValueRows(
            "souls.stamina=Stamina, player:souls.level|0=Level, mod:nobody=Nobody", Values());
        AssertThat(rows.Select(r => r.Label).ToList()).ContainsExactly("Stamina: 42.5", "Level: 7", "Nobody");
        AssertThat(rows[1].Name).IsEqual("souls.level");
        AssertThat(Engine_UiModRows.ToRows(rows)[0].Committable).IsFalse();
    }
}
