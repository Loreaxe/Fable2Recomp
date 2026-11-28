using System.Collections.Generic;
using Godot;
using Refii.Game;

namespace Refii.AndroidRuntime;

/// <summary>What the screen is showing, as far as the touch controls care.</summary>
public enum TouchContext
{
    /// <summary>Only Options drawn: a controller is the device, the guest is not up, or the dev menu is open. Every other touch is the pointer.</summary>
    Hidden,
    /// <summary>In the world: the move stick, the camera drag and the full pad.</summary>
    World,
    /// <summary>The expression wheel or the spell blade is held open: its opener, the face buttons and the D-pad; taps point at the wheel.</summary>
    Wheel,
    /// <summary>A menu, box, book or the front end: smaller buttons, and taps go to the menu under them.</summary>
    Menu,
    /// <summary>Photo mode: the move stick, the camera drag and Raise/Lower fly the photo camera; taps on its panel go to the panel.</summary>
    Photo,
    /// <summary>Murgo's mirror: its panel takes its taps, and a drag anywhere else turns the hero to look at.</summary>
    Mirror,
    /// <summary>A pub game's board (Keystone, Mage Dice, Wizard's Tower): the pad its legend names, and the board takes no taps.</summary>
    PubGame,
    /// <summary>Decorating a home: the stick and the drag fly its camera, a tap picks up or puts down, the held piece is dragged; taps on its panel go to the panel.</summary>
    Decorate,
    /// <summary>A mod's own screen stands where the pause book would (own_screen): taps go to it, and Close (Start) ends the pause.</summary>
    ModScreen,
}

/// <summary>How a control turns a finger into pad input.</summary>
public enum TouchControlKind
{
    /// <summary>Held while the finger is on it.</summary>
    Button,
    /// <summary>A pad hold made comfortable on glass: a tap latches it on until the next tap; a long press is an ordinary hold.</summary>
    Latch,
    /// <summary>The floating move stick: it centres where the thumb lands.</summary>
    Stick,
    /// <summary>The camera drag.</summary>
    Look,
    /// <summary>The D-pad: one control, the direction picked by where the finger is on it, so a thumb can roll between items.</summary>
    DPad,
    /// <summary>A host button, no pad input: tapped, it runs the host's action (Options).</summary>
    Action,
    /// <summary>
    /// A tap runs the host's action (Spells: the touch spell picker); a press held past
    /// <see cref="AndroidTouchPad.LatchTap"/> is the pad's own hold instead, for as long as the
    /// finger stays (Spells: the right trigger, which draws in experience orbs).
    /// </summary>
    Menu,
}

/// <summary>Which analogue trigger a control holds fully down.</summary>
public enum TouchTrigger { None, Left, Right }

/// <summary>One on-screen control, in canvas units.</summary>
/// <param name="Id">Stable across layouts, so a latch or a held finger survives a context change.</param>
/// <param name="Round">A circle filling <paramref name="Bounds"/>'s width, else a pill.</param>
/// <param name="Steers">A finger that slides off its start point turns the camera while the button stays held (aim with Y, steer a flourish).</param>
/// <param name="OverHud">A D-pad laid over the HUD's own item slots: nothing drawn but a ring per slot, the game's icons show through.</param>
public sealed record TouchControl(string Id, TouchControlKind Kind, Rect2 Bounds, bool Round, uint Bits,
    TouchTrigger Trigger, string Label, string Caption, Color Color, bool Steers = false, bool OverHud = false)
{
    /// <summary>Extra reach around a drawn control, in canvas units: fingers land a little wide.</summary>
    public const float Slop = 8f;

    /// <summary>Whether <paramref name="point"/> lands on this control.</summary>
    public bool Hit(Vector2 point) => Kind is TouchControlKind.Stick or TouchControlKind.Look
        ? Bounds.HasPoint(point)
        : Round
            ? point.DistanceTo(Bounds.GetCenter()) <= Bounds.Size.X * 0.5f + Slop
            : Bounds.Grow(Slop).HasPoint(point);
}

/// <summary>
/// What the hero can do right now, for the buttons that stand only when they would act: Y with a
/// ranged weapon on the body or a villager's Y prompt up (<paramref name="Villager"/>: their opinion
/// and the gift or proposal it offers), B with a spell, Spells with two (the game opens its spell
/// menu only then).
/// </summary>
public readonly record struct TouchHero(bool Ranged, bool Magic, bool SpellMenu, bool Villager = false)
{
    /// <summary>Everything shown: the state not known yet, nothing is hidden on a guess.</summary>
    public static readonly TouchHero Everything = new(true, true, true);
}

/// <summary>
/// Where every control stands for a screen size and context. Sized from the canvas height (the
/// Android canvas is 720 units tall whatever the phone, see project.godot's stretch), so a
/// control is the same size under the thumb on every phone held the same way.
/// </summary>
public static class AndroidTouchLayout
{
    /// <summary>The Xbox's own button colours, so the screen matches the game's prompts.</summary>
    public static readonly Color GreenA = new(0.38f, 0.73f, 0.16f);
    public static readonly Color RedB = new(0.91f, 0.18f, 0.18f);
    public static readonly Color BlueX = new(0.10f, 0.47f, 0.90f);
    public static readonly Color YellowY = new(1.00f, 0.73f, 0.00f);
    public static readonly Color Slate = new(0.18f, 0.20f, 0.24f);
    public static readonly Color Violet = new(0.42f, 0.34f, 0.72f);

    /// <summary>What a hold on Spells does, said over the button.</summary>
    public const string OrbsHint = "Hold: XP orbs";

    /// <summary>The move stick's base radius, in units; the knob travels this far (about 26 mm across on an S25+).</summary>
    public const float StickRadius = 128f;

    /// <summary>
    /// Stick zones and buttons, in hit-test order: buttons first, then the stick zone, the camera last.
    /// </summary>
    /// <remarks>
    /// SIZED FOR A THUMB, LAID OUT FOR ONE. A unit is about 0.1 mm on a phone held sideways (the
    /// canvas is 720 units over the screen's ~70 mm short side), and the sizes are the usual ones
    /// for a phone game: the main button about 15 mm across, the other face buttons about 12 mm,
    /// the shoulder buttons about 9 mm, the stick about 26 mm. The right thumb rests on A, the
    /// button pressed most, in the corner; X, Y and B sit on an arc around it and the shoulder
    /// buttons on a wider arc beyond, so every button is a short reach from the resting thumb.
    /// The letters and colours are the pad's, so the game's prompts name the buttons on the glass.
    ///
    /// AND OUT OF THE HUD'S WAY. Fable 2 draws health and the spell charge down the top left and
    /// the quick items in the bottom-left corner: nothing stands there but Options and View, below
    /// them. The items are pressed where the game draws them (<paramref name="itemSlots"/>), Menu
    /// takes the top-right corner, and the stick is drawn only under a thumb.
    /// </remarks>
    /// <param name="size">The canvas size the controls are laid out on.</param>
    /// <param name="scale">The player's `touch_scale`.</param>
    /// <param name="itemSlots">The HUD's item cross in canvas units — its centre and the distance to a
    /// slot — or null for where the game draws it on a 16:9-or-wider phone.</param>
    /// <param name="hero">What the hero can do (<see cref="TouchHero"/>); everything when null.</param>
    /// <param name="photoPanel">In photo mode, whether its panel is shown: with it hidden for a clear
    /// view only the stick and the camera stand, and a tap brings the panel back. Decorating, the
    /// same for its panel, which a Panel button brings back.</param>
    /// <param name="pubGame">At a pub game, which (GamePubGames.Showing): Keystone alone bets with X and RB.</param>
    /// <param name="placing">Decorating, whether a piece is held: its Place, Turn and Item buttons stand.</param>
    /// <param name="mods">In the world, what the enabled mods ask of the screen (<see cref="AndroidTouchModControls"/>):
    /// their pills in the top row left of Emotes, their areas, and captions on the pad's buttons.</param>
    public static IReadOnlyList<TouchControl> Build(Vector2 size, float scale, TouchContext context,
        (Vector2 Centre, float Reach)? itemSlots = null, TouchHero? hero = null, bool photoPanel = true,
        string pubGame = "", bool placing = false, AndroidTouchModControls.Offer? mods = null)
    {
        var controls = new List<TouchControl>();
        if (size.X <= 0f || size.Y <= 0f)
        {
            return controls;
        }

        float u = Unit(size, scale);
        float w = size.X, h = size.Y, margin = 20f * u;

        TouchHero can = hero ?? TouchHero.Everything;
        AndroidTouchModControls.Offer offer = context == TouchContext.World ? mods ?? AndroidTouchModControls.Offer.None
            : AndroidTouchModControls.Offer.None;
        string Caption(string button, string normal) => offer.Captions.TryGetValue(button, out string? said) ? said : normal;
        bool world = context == TouchContext.World;
        bool wheel = context == TouchContext.Wheel;
        bool pub = context == TouchContext.PubGame, keystone = pub && pubGame == "keystone";

        // THE TOP-RIGHT ROW, right to left: Menu (Start) and Emotes (Back). A chat's hold lives up
        // here, out of the fight's way; each keeps its place in every context, so a latched Emotes
        // is tapped closed where it was tapped open.
        // THE PILLS ARE A FINGER TALL: each its icon and its word, as wide as those need and no wider.
        float pillH = 56f * u, top = 14f * u, gap = 10f * u;
        float x = w - margin;
        Rect2 NextPill(float width)
        {
            x -= width;
            var rect = new Rect2(x, top, width, pillH);
            x -= gap;
            return rect;
        }

        Rect2 menuPill = NextPill(124f * u), emotesPill = NextPill(140f * u);
        if (context == TouchContext.Photo)
        {
            return BuildPhoto(controls, size, u, photoPanel);
        }

        if (context == TouchContext.Decorate)
        {
            return BuildDecorate(controls, size, u, photoPanel, placing);
        }

        if (context == TouchContext.Mirror)
        {
            // Apply, Cancel and Reset are on the mirror's own panel; its settings are also chosen
            // and adjusted with the D-pad, whose arrows are larger than the panel's. Then the drag.
            controls.Add(SettingsDPad(size, u));
            controls.Add(new TouchControl("Look", TouchControlKind.Look, new Rect2(Vector2.Zero, size), false, 0u,
                TouchTrigger.None, "", "", Slate));
            return controls;
        }


        // OPTIONS (the diagnostics, a host action) IN THE BOTTOM-LEFT CORNER, in every context: a
        // controller in hand still needs it, and there it is under the HUD's item cross, clear of
        // the top row and the fight. Inset from the left past the screen's rounded corner.
        Rect2 optionsPill = new(OptionsInset * u, h - top - pillH, 140f * u, pillH);
        controls.Add(Pill("Options", TouchControlKind.Action, optionsPill, 0u, TouchTrigger.None, "Options", "", Slate));
        // View (the first-person look, LB), seldom used, beside it.
        Rect2 viewPill = new(optionsPill.End.X + gap, optionsPill.Position.Y, 112f * u, pillH);
        if (context == TouchContext.Hidden)
        {
            return controls;
        }

        if (context == TouchContext.ModScreen)
        {
            // The screen is the mod's, pointer and all; the pad does not reach the game under it,
            // so only the way out stands: Start, which ends the pause and the screen with it.
            controls.Add(Pill("Start", TouchControlKind.Button, menuPill, GameInput.PadStart, TouchTrigger.None, "Close", "", Slate));
            return controls;
        }

        controls.Add(Pill("Start", TouchControlKind.Button, menuPill, GameInput.PadStart, TouchTrigger.None, "Menu", "", Slate));
        if (world || wheel)
        {
            controls.Add(Pill("Back", TouchControlKind.Latch, emotesPill, GameInput.PadBack, TouchTrigger.None, "Emotes", "", Slate));
        }

        if (world)
        {
            controls.Add(Pill("LB", TouchControlKind.Latch, viewPill, GameInput.PadLeftShoulder, TouchTrigger.None, "View", "", Slate));
            // THE MODS' PILLS go on along the top row, left of Emotes, short of the HUD's health.
            AndroidTouchModControls.AddPills(controls, offer, emotesPill.Position.X - gap, HudHealthRight * h / 720f, top, pillH, gap, u);
        }

        if (pub)
        {
            // A PUB GAME'S TRIGGERS ARE HELD TO READ: the odds (LT) and the stats (RT), named as every
            // game's own legend names them, latched by a tap so the card stays up while it is read,
            // tapped away again. RT beside Menu, LT mirroring it at the top left, which no HUD holds
            // over a board.
            controls.Add(Pill("Stats", TouchControlKind.Latch, new Rect2(menuPill.Position.X - gap - 132f * u, top, 132f * u, pillH),
                0u, TouchTrigger.Right, "Stats", "", Slate));
            // Each board's legend names LT its own way: Keystone's odds, Spinnerbox's payouts,
            // Fortune's Tower's tips.
            string odds = pubGame switch { "magedice" => "Payouts", "wizardstower" => "Tips", _ => "Odds" };
            controls.Add(Pill("Odds", TouchControlKind.Latch, new Rect2(OptionsInset * u, top, (odds.Length > 4 ? 176f : 132f) * u, pillH),
                0u, TouchTrigger.Left, odds, "", Slate));
        }
        // A menu is tapped more than it is pressed: the same places, a little smaller. A wheel held
        // open keeps the world's sizes and places, so the latched opener stays under the finger
        // that taps it closed.
        float k = context == TouchContext.Menu ? u * 0.85f : u;

        // The right thumb's resting place, and the arcs around it.
        var pivot = new Vector2(w - margin - 130f * u, h - margin - 130f * u);
        Vector2 Inner(float degrees) => pivot + Polar(178f * k, degrees);
        float face = 58f * k, shoulder = 46f * k;

        // A IS UNDER THE RESTING THUMB: talking, picking up, opening, sprinting and every
        // menu's accept — the button pressed most. X, the sword, is one short reach away. In the
        // world a button that would do nothing is not drawn — Y with no ranged weapon, B with no
        // spell — and the others keep their places, so the thumb's memory holds when one returns.
        // At a pub game A and B are what the board's legend says (raise and lower a bet, spin, deal,
        // accept and back), so only Y, every game's bank, is named; X stands for Keystone's "done".
        // A mod's caption names what its action does with the button now, and stands the button.
        controls.Add(Circle("A", pivot, 78f * k, GameInput.PadA, "A", Caption("A", world ? "Use" : pub ? "" : "Select"), GreenA, world));
        if (!pub || keystone)
        {
            controls.Add(Circle("X", Inner(185f), face, GameInput.PadX, "X", Caption("X", world ? "Melee" : ""), BlueX, world));
        }

        if (!world || can.Ranged || can.Villager || offer.Captions.ContainsKey("Y"))
        {
            // A targeted villager's Y prompt outranks the bow: it is what Y does then.
            string yCaption = pub ? "Bank" : !world ? "" : can.Villager ? "Info" : "Ranged";
            controls.Add(Circle("Y", Inner(230f), face, GameInput.PadY, "Y", Caption("Y", yCaption), YellowY, world));
        }

        if (!world || can.Magic || offer.Captions.ContainsKey("B"))
        {
            controls.Add(Circle("B", Inner(276f), face, GameInput.PadB, "B", Caption("B", world ? "Magic" : pub ? "" : "Back"), RedB, world));
        }

        // The camera mode (the right stick's click) is a double tap on the camera side (AndroidTouchPad).
        // SPELLS AND LOCK STAND AT THE SIDE EDGES, MIRRORED, on their triggers' sides as a controller
        // has them: Lock (LT) under the left thumb, Spells (RT) under the right, each found by feel
        // at its edge and clear of the camera drag over the middle of the screen. A third of the way
        // down: below a corner's punch-hole camera and above the one a phone centres on a side edge.
        // On the left that is where the HUD's spell-charge meter runs, so Android moves the meter in
        // (Fable 2's Fable2HudLayout.InsetSpellCharge). Between the HUD's
        // health above and B below, the pair shrinks rather than covering either at the largest
        // scales. Each keeps its place with the game's spell menu open, so a latch is tapped closed
        // where it was opened. The lock stays whatever the hero carries — it also aims an expression.
        float hud = h / 720f;
        float edgeLow = HudHealthBottom * hud + 8f * u, edgeHigh = Inner(276f).Y - face - 16f * k;
        float edgeRadius = Mathf.Min(54f * k, (edgeHigh - edgeLow) * 0.5f);
        float edgeY = Mathf.Clamp(h * EdgeHeight, edgeLow + edgeRadius, edgeHigh - edgeRadius);
        float edgeX = OptionsInset * u + edgeRadius;
        if (world)
        {
            // A hold is the right trigger, which draws in the experience orbs around the hero, so
            // the button stands whatever spells the hero has. With two to choose between a tap
            // opens the touch spell picker; the game's own spell menu, which the trigger also
            // opens, stays hidden under a touch hold (Engine_UiSpellAssignSet.TouchHidesWheel).
            // It is named Spells whatever the hero has; the hold's hint is drawn over it.
            controls.Add(Round("RT", TouchControlKind.Menu,
                new Vector2(w - edgeX, edgeY), edgeRadius, 0u, TouchTrigger.Right, "", "Spells", Violet));
        }

        if (world)
        {
            controls.Add(Round("LT", TouchControlKind.Latch, new Vector2(edgeX, edgeY), edgeRadius,
                0u, TouchTrigger.Left, "", "Lock", Violet, steers: true));
        }

        if (!world && !wheel)
        {
            // A MENU'S TABS ARE TURNED FROM THE SIDE EDGES, where Spells and Lock stand in play:
            // the previous tab on the left, the next on the right, each a chevron pointing its way.
            // Above the menus' D-pad, which stands below them on the left, however large the scale.
            float dpadTop = optionsPill.Position.Y - DPadClearance * u - 2f * MenuDPadReach * u;
            float tabY = Mathf.Max(top + pillH + gap + edgeRadius, Mathf.Min(edgeY, dpadTop - gap - edgeRadius));
            if (pub)
            {
                // A pub game's shoulders stand there too, lettered as its legend names them: LB the
                // largest bet in every game, RB Keystone's last bets placed again.
                controls.Add(Round("LB", TouchControlKind.Button, new Vector2(edgeX, tabY), edgeRadius,
                    GameInput.PadLeftShoulder, TouchTrigger.None, "LB", "Max bet", Slate));
                if (keystone)
                {
                    controls.Add(Round("RB", TouchControlKind.Button, new Vector2(w - edgeX, tabY), edgeRadius,
                        GameInput.PadRightShoulder, TouchTrigger.None, "RB", "Rebet", Slate));
                }
            }
            else
            {
                controls.Add(Round("LB", TouchControlKind.Button, new Vector2(edgeX, tabY), edgeRadius,
                    GameInput.PadLeftShoulder, TouchTrigger.None, "", "Prev", Slate));
                controls.Add(Round("RB", TouchControlKind.Button, new Vector2(w - edgeX, tabY), edgeRadius,
                    GameInput.PadRightShoulder, TouchTrigger.None, "", "Next", Slate));
            }
        }

        if (world || wheel)
        {
            // THE ITEMS ARE PRESSED WHERE THE GAME DRAWS THEM: a D-pad over the HUD's own four
            // slots, each slot's touch a finger wide whatever the art's size (the same reach a
            // wheel's spell blade is stepped with). Where the HUD has not said yet, where it draws
            // them on a 16:9-or-wider phone.
            (Vector2 centre, float reach) = itemSlots ?? DefaultItemSlots(size);
            // Stopped short of Options below it, about the same centre, so the slots press where drawn.
            float zone = Mathf.Min(Mathf.Max(reach * 2.2f, 82f * k), optionsPill.Position.Y - gap - centre.Y);
            controls.Add(new TouchControl("DPad", TouchControlKind.DPad,
                new Rect2(centre - new Vector2(zone, zone), new Vector2(zone * 2f, zone * 2f)), true, 0u,
                TouchTrigger.None, "", "", Slate, OverHud: true));
        }
        else
        {
            // A menu has no HUD under it: a drawn D-pad, bottom left under the left thumb, lifted
            // well clear of Options below it so a thumb pressing Down does not catch Options.
            float dpad = MenuDPadReach * u;
            Vector2 dpadCentre = new(margin + dpad, optionsPill.Position.Y - DPadClearance * u - dpad);
            controls.Add(new TouchControl("DPad", TouchControlKind.DPad,
                new Rect2(dpadCentre - new Vector2(dpad, dpad), new Vector2(dpad * 2f, dpad * 2f)), true, 0u,
                TouchTrigger.None, "", "", Slate));
        }

        // The mods' areas over their own drawing take a finger before the stick and the camera.
        AndroidTouchModControls.AddAreas(controls, offer);
        if (world)
        {
            // The stick lands wherever the left thumb does on the left of the glass; the camera is
            // every other free inch of it.
            float stickTop = h * 0.15f;
            controls.Add(new TouchControl("Stick", TouchControlKind.Stick,
                new Rect2(0f, stickTop, w * 0.45f, h - stickTop), false, 0u, TouchTrigger.None, "", "", Slate));
            controls.Add(new TouchControl("Look", TouchControlKind.Look,
                new Rect2(0f, 0f, w, h), false, 0u, TouchTrigger.None, "", "", Slate));
        }

        return controls;
    }

    /// <summary>
    /// A D-pad for a settings panel (photo mode's, the mirror's): up and down choose a setting,
    /// left and right adjust it, held to repeat. Drawn large at the top right under the right thumb,
    /// as the panels' own arrows are a few millimetres wide on a phone.
    /// </summary>
    private static TouchControl SettingsDPad(Vector2 size, float u)
    {
        float radius = 100f * u;
        var centre = new Vector2(size.X - 20f * u - radius, 14f * u + 48f * u + 20f * u + radius);
        return new TouchControl("DPad", TouchControlKind.DPad,
            new Rect2(centre - new Vector2(radius, radius), new Vector2(radius * 2f, radius * 2f)), true, 0u,
            TouchTrigger.None, "", "", Slate);
    }

    /// <summary>The id prefix of a spell in the picker, followed by its will (<see cref="BuildSpellPicker"/>).</summary>
    public const string SpellPrefix = "Spell:";

    /// <summary>The picker's backdrop: a touch anywhere off a spell closes the picker.</summary>
    public const string SpellsClose = "SpellsClose";

    /// <summary>
    /// THE TOUCH SPELL PICKER: the hero's spells in a row across the middle of the screen, each a
    /// tap target the size of a face button, named beneath; behind them a backdrop the size of the
    /// screen, so a touch off every spell closes the picker instead of reaching the game. Laid
    /// before the play's own controls: it is modal while it stands.
    /// </summary>
    /// <param name="spells">The wills to offer, in the order drawn, and their names.</param>
    public static IReadOnlyList<TouchControl> BuildSpellPicker(Vector2 size, float scale,
        IReadOnlyList<(int Will, string Name)> spells)
    {
        var controls = new List<TouchControl>();
        if (size.X <= 0f || size.Y <= 0f || spells.Count == 0)
        {
            return controls;
        }

        float u = Unit(size, scale);
        float radius = 54f * u, pitch = 2f * radius + 44f * u;
        // Narrowed to fit: eight spells at the largest scale still stand inside the screen.
        pitch = Mathf.Min(pitch, (size.X - 40f * u) / spells.Count);
        radius = Mathf.Min(radius, pitch * 0.5f - 6f * u);
        float x = size.X * 0.5f - pitch * (spells.Count - 1) * 0.5f, y = size.Y * 0.42f;
        foreach ((int will, string name) in spells)
        {
            controls.Add(Round(SpellPrefix + will, TouchControlKind.Action, new Vector2(x, y), radius, 0u,
                TouchTrigger.None, "", name, Violet));
            x += pitch;
        }

        controls.Add(new TouchControl(SpellsClose, TouchControlKind.Action, new Rect2(Vector2.Zero, size), false, 0u,
            TouchTrigger.None, "", "", Slate));
        return controls;
    }

    /// <summary>
    /// PHOTO MODE FLIES A FREE CAMERA. The stick moves it and a drag turns it, as in the world;
    /// Raise and Lower stand where A and B do, under the resting right thumb. The panel's own
    /// buttons (Take photo, Reset, Hide UI, Back and the settings' arrows) are tapped where it
    /// draws them — the touch controls leave a finger landing on it to the panel.
    /// </summary>
    private static IReadOnlyList<TouchControl> BuildPhoto(List<TouchControl> controls, Vector2 size, float u, bool panel)
    {
        float w = size.X, h = size.Y, margin = 20f * u;
        if (panel)
        {
            var pivot = new Vector2(w - margin - 130f * u, h - margin - 130f * u);
            controls.Add(Round("LT", TouchControlKind.Button, pivot, 66f * u, 0u, TouchTrigger.Left, "Lower", "", Violet));
            controls.Add(Round("RT", TouchControlKind.Button, pivot + Polar(178f * u, 250f), 66f * u, 0u,
                TouchTrigger.Right, "Raise", "", Violet));
            controls.Add(SettingsDPad(size, u));
        }

        float stickTop = h * 0.15f;
        controls.Add(new TouchControl("Stick", TouchControlKind.Stick,
            new Rect2(0f, stickTop, w * 0.45f, h - stickTop), false, 0u, TouchTrigger.None, "", "", Slate));
        controls.Add(new TouchControl("Look", TouchControlKind.Look,
            new Rect2(0f, 0f, w, h), false, 0u, TouchTrigger.None, "", "", Slate));
        return controls;
    }

    /// <summary>
    /// Where Fable 2's HUD draws its item cross on a 16:9-or-wider phone, measured on the S25+:
    /// the centre 129 units in and 162.5 up from the bottom of the 720-unit canvas, a slot 37.5
    /// units out, lifted by Android's Engine_HudLayout.AndroidDpadLift — used until the HUD states
    /// its own (GameUi.HudItemSlots).
    /// </summary>
    public static (Vector2 Centre, float Reach) DefaultItemSlots(Vector2 size)
    {
        float s = size.Y / 720f;
        return (new Vector2(129f * s, size.Y - (162.5f + Refii.Ui.Engine_HudLayout.AndroidDpadLift) * s), 37.5f * s);
    }

    /// <summary>A point <paramref name="radius"/> from the origin at <paramref name="degrees"/>, screen axes (180 left, 270 up).</summary>
    private static Vector2 Polar(float radius, float degrees) =>
        new(radius * Mathf.Cos(Mathf.DegToRad(degrees)), radius * Mathf.Sin(Mathf.DegToRad(degrees)));

    /// <summary>One layout unit in canvas units: a 720-unit-tall canvas at the player's scale.</summary>
    public static float Unit(Vector2 size, float scale) => size.Y / 720f * Mathf.Clamp(scale, MinScale, MaxScale);

    /// <summary>
    /// How far the D-pad drawn in menus reaches from its centre, in layout units (about 12.5 mm), a
    /// thumb's target each arm. Not shrunk with the menus' other controls.
    /// </summary>
    public const float MenuDPadReach = 125f;

    /// <summary>
    /// How far down the screen Spells and Lock stand, as a fraction of its height: clear of a
    /// corner's punch-hole camera and of one centred on a side edge.
    /// </summary>
    public const float EdgeHeight = 0.33f;

    /// <summary>Where the HUD's health ends below the top-left corner, in 720-unit canvas units.</summary>
    public const float HudHealthBottom = 120f;

    /// <summary>Where the HUD's health ends right of the top-left corner, in 720-unit canvas units: the mods' pills stop short of it.</summary>
    public const float HudHealthRight = 300f;


    /// <summary>Options' distance from the left edge, in layout units: past a phone's rounded corner (the S25+'s is 75 units).</summary>
    public const float OptionsInset = 40f;

    /// <summary>
    /// The space between the menus' drawn D-pad and Options below it, in layout units: about
    /// 6 mm, as Android lifts the HUD's own cross (Engine_HudLayout.AndroidDpadLift).
    /// </summary>
    public const float DPadClearance = 56f;

    /// <summary>The `touch_scale` range: past the top the arcs would run into the top row.</summary>
    public const float MinScale = 0.6f, MaxScale = 1.25f;

    /// <summary>A tap on the camera while decorating: the decoration picks up or puts down there (AndroidTouchPad.LastTap).</summary>
    public const string DecorateTap = "DecorateTap";

    /// <summary>
    /// DECORATING FLIES A FREE CAMERA, as photo mode does: the stick moves it, a drag turns it, and
    /// Raise and Lower stand by the resting right thumb. The furniture is handled with the finger:
    /// a tap on a piece picks it up, a tap on the room puts the held piece down there, and a drag
    /// that starts on the held piece slides it (AndroidTouchControls hands that finger to the
    /// decoration). While a piece is held, Place (A) puts it down where it stands, and Turn and
    /// Item move it as the pad's shoulders and D-pad do. The panel's own buttons are tapped where it
    /// draws them; hidden, a Panel button brings it back.
    /// </summary>
    private static IReadOnlyList<TouchControl> BuildDecorate(List<TouchControl> controls, Vector2 size, float u,
        bool panel, bool placing)
    {
        float w = size.X, h = size.Y, margin = 20f * u, pillH = 56f * u, gap = 10f * u;
        var pivot = new Vector2(w - margin - 130f * u, h - margin - 130f * u);
        if (!panel)
        {
            controls.Add(Pill("Panel", TouchControlKind.Action, new Rect2(OptionsInset * u, 14f * u, 140f * u, pillH),
                0u, TouchTrigger.None, "Panel", "", Slate));
        }

        if (placing)
        {
            controls.Add(Circle("Place", pivot, 78f * u, GameInput.PadA, "A", "Place", GreenA, false));
            // Turn and Item in two rows above the thumb's arc, each pair side by side.
            float pillW = 150f * u, right = w - margin - pillW, left = right - gap - pillW, row = h * 0.18f;
            controls.Add(Pill("TurnLeft", TouchControlKind.Button, new Rect2(left, row, pillW, pillH),
                GameInput.PadLeftShoulder, TouchTrigger.None, "Turn left", "", Slate));
            controls.Add(Pill("TurnRight", TouchControlKind.Button, new Rect2(right, row, pillW, pillH),
                GameInput.PadRightShoulder, TouchTrigger.None, "Turn right", "", Slate));
            controls.Add(Pill("ItemDown", TouchControlKind.Button, new Rect2(left, row + pillH + gap, pillW, pillH),
                GameInput.PadDown, TouchTrigger.None, "Item down", "", Slate));
            controls.Add(Pill("ItemUp", TouchControlKind.Button, new Rect2(right, row + pillH + gap, pillW, pillH),
                GameInput.PadUp, TouchTrigger.None, "Item up", "", Slate));
        }

        // Where they stand with a piece held or not, so the thumb finds them the same.
        controls.Add(Round("Lower", TouchControlKind.Button, pivot + Polar(178f * u, 180f), 58f * u, 0u,
            TouchTrigger.Left, "Lower", "", Violet));
        controls.Add(Round("Raise", TouchControlKind.Button, pivot + Polar(178f * u, 250f), 58f * u, 0u,
            TouchTrigger.Right, "Raise", "", Violet));
        float stickTop = h * 0.15f;
        controls.Add(new TouchControl("Stick", TouchControlKind.Stick,
            new Rect2(0f, stickTop, w * 0.45f, h - stickTop), false, 0u, TouchTrigger.None, "", "", Slate));
        controls.Add(new TouchControl("Look", TouchControlKind.Look,
            new Rect2(0f, 0f, w, h), false, 0u, TouchTrigger.None, "", "", Slate));
        return controls;
    }

    /// <summary>
    /// The controller's name for what a control presses, as the game's prompts show it — LT, RT,
    /// LB, RB, Start, Back — or empty where the control's own face already says it (A, B, X, Y) or
    /// it presses no one button (the D-pad, the stick, Options).
    /// </summary>
    public static string PadName(TouchControl control) =>
        control.Kind is TouchControlKind.Stick or TouchControlKind.Look or TouchControlKind.DPad ? string.Empty
        : control.Trigger == TouchTrigger.Left ? "LT"
        : control.Trigger == TouchTrigger.Right ? "RT"
        : control.Bits switch
        {
            GameInput.PadLeftShoulder => "LB",
            GameInput.PadRightShoulder => "RB",
            GameInput.PadStart => "Start",
            GameInput.PadBack => "Back",
            _ => string.Empty,
        };

    private static TouchControl Circle(string id, Vector2 centre, float radius, uint bits, string label,
        string caption, Color color, bool steers) =>
        new(id, TouchControlKind.Button, new Rect2(centre - new Vector2(radius, radius), new Vector2(radius * 2f, radius * 2f)),
            true, bits, TouchTrigger.None, label, caption, color, steers);

    private static TouchControl Round(string id, TouchControlKind kind, Vector2 centre, float radius, uint bits,
        TouchTrigger trigger, string label, string caption, Color color, bool steers = false) =>
        new(id, kind, new Rect2(centre - new Vector2(radius, radius), new Vector2(radius * 2f, radius * 2f)),
            true, bits, trigger, label, caption, color, steers);

    private static TouchControl Pill(string id, TouchControlKind kind, Rect2 bounds, uint bits, TouchTrigger trigger,
        string label, string caption, Color color, bool steers = false) =>
        new(id, kind, bounds, false, bits, trigger, label, caption, color, steers);
}
