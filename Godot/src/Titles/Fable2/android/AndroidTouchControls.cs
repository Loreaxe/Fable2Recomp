using System.Linq;
using System.Collections.Generic;
using Godot;
using Refii.Game;
using Refii.Host;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.AndroidRuntime;

/// <summary>
/// The touch controls: the pad drawn on the glass, so the game plays from the phone's screen
/// without a controller. The move stick lands under the left thumb, a drag anywhere else turns
/// the camera, and the pad's buttons sit where the thumbs rest; in a menu the controls shrink and
/// a tap goes to the row under it.
/// </summary>
/// <remarks>
/// <para>THE FINGERS ARE THIS NODE'S, ALL OF THEM. Godot's touch-to-mouse emulation follows one
/// finger and cannot tell a thumb on the stick from a tap on a menu row, so it is switched off
/// while this node stands, and the fingers that land on no control are handed back as a mouse
/// (<see cref="AndroidTouchPointer"/>, posted through <c>Input.ParseInputEvent</c> with the
/// emulation device id) — the menus and the diagnostics buttons take them as they take the
/// desktop's mouse. The open dev menu is the exception: its window takes every finger before this
/// node could, so there the emulation is on again (<see cref="EmulatesMouse"/>).</para>
/// <para>The pad it holds reaches the guest through <see cref="GameInput.Touch"/>, merged into
/// pad 0 with every other device; the camera drag through <see cref="GameInput.PostTouchLook"/>.
/// Using a controller hides the controls (the device switch, <see cref="Engine_UiInputMode"/>);
/// the next touch brings them back.</para>
/// <para>Settings, in refii.toml: <c>touch_controls</c> (on), <c>touch_scale</c> (1.0),
/// <c>touch_opacity</c> (0.5) and <c>touch_look_sensitivity</c> (1.0).</para>
/// <para>DRAWN ONLY WHEN SOMETHING DRAWN CHANGES: every circle and arc is a vertex and index buffer
/// made and freed on the render thread. Whatever <see cref="_Draw"/> starts reading that can change
/// goes in <see cref="TouchDrawnOverlay"/>; the stick has its own items (<see cref="AndroidTouchStick"/>),
/// so a moving thumb redraws nothing.</para>
/// </remarks>
public partial class AndroidTouchControls : Control, IAndroidTouchControls
{
    private readonly AndroidTouchPad _pad = new();
    private readonly AndroidTouchPointer _pointer = new();
    private readonly AndroidTouchWheelRelease _emotesRelease = new();
    private int _wheelPicksSeen;
    private readonly List<Rect2> _passThrough = new();
    private bool _savedEmulation;
    private bool _enabled = true;
    private bool _devMenuOpen;
    private float _scale = 1f, _opacity = 0.5f, _lookSensitivity = 1f;
    private double _settingsClock;
    private IReadOnlyDictionary<string, string>? _lastValues;
    private Vector2 _laidOutSize;
    private float _laidOutScale;
    private TouchContext _laidOutContext = (TouchContext)(-1);
    private (Vector2 Centre, float Reach)? _laidOutSlots;
    private TouchHero _laidOutHero = TouchHero.Everything;
    private bool _laidOutPhotoPanel = true;
    private bool _laidOutPlacing;

    /// <summary>The finger decorating has taken, dragging the held piece (GameDecorate.TouchGrab), or -1.</summary>
    private int _grabFinger = -1;
    private string _laidOutPubGame = string.Empty;
    private int _laidOutCustom = -1;

    /// <summary>What the enabled mods ask of the screen, as last laid out (AndroidTouchModControls).</summary>
    private AndroidTouchModControls.Offer _laidOutMods = AndroidTouchModControls.Offer.None;

    /// <summary><see cref="FromHud"/> as one delegate: a method group makes a new one per call.</summary>
    private System.Func<Rect2, Rect2?>? _fromHud;

    /// <summary>The spells the touch spell picker offers while it is open, or null while it is closed.</summary>
    private IReadOnlyList<(int Will, string Name)>? _pickerSpells, _laidOutPicker;

    /// <summary>
    /// Whether a touch hold on Spells is hiding the game's spell wheel: from the finger landing
    /// until the guest has closed the wheel the trigger opened, so it never flashes up on release.
    /// </summary>
    private bool _hidingWheel;

    /// <summary>The will icons drawn so far, loaded once each.</summary>
    private readonly Dictionary<int, Texture2D?> _willIcons = new();
    private TouchHero _hero = TouchHero.Everything;
    private double _heroClock;
    private Vector2 _pointerWindow;

    /// <summary>One box restyled for every pill and the D-pad: each draw is recorded as it is made.</summary>
    private readonly StyleBoxFlat _box = new() { AntiAliasing = true };

    private readonly AndroidTouchStick _stick = new() { Name = "Stick" };
    private TouchDrawnOverlay _drawn;
    private double _drawnAt;

    /// <summary>The last picture went without art that has not loaded yet: it is drawn again until it has.</summary>
    private bool _artMissing;

    /// <summary>The editor preference to show only Options, including after the next touch.</summary>
    private bool _hideControls;

    /// <summary>Whether the controls are switched on (`touch_controls`).</summary>
    public bool ControlsEnabled => _enabled;

    /// <inheritdoc/>
    public IReadOnlyList<TouchControl> LaidOut => _pad.Controls;

    /// <summary>The context the controls are laid out for, for the status report.</summary>
    public TouchContext CurrentContext => _pad.Context;

    /// <summary>Runs when Options is tapped, with the button's rectangle to open the diagnostics beside.</summary>
    public System.Action<Rect2>? OptionsPressed { get; set; }

    /// <summary>Optional supplier of screen rectangles other UI owns (the diagnostics buttons), added to the list it is given.</summary>
    public System.Action<List<Rect2>>? PassThrough { get; set; }

    /// <summary>The viewport's visible size and final transform, taken again when it signals a new size (which a new stretch also is).</summary>
    private Vector2 _viewportSize;
    private Transform2D _toWindow = Transform2D.Identity;
    private Viewport? _sizedViewport;

    /// <summary>
    /// Whether the last frame ran whole, hidden, with nothing held: the pad, the pointer and what they
    /// told the game were at rest, so a hidden frame that finds them so has nothing to do.
    /// </summary>
    private bool _restedHidden;

    private static double Now => Time.GetTicksUsec() / 1_000_000.0;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        ProcessMode = ProcessModeEnum.Always;
        // Before GameInput samples the pad this frame.
        ProcessPriority = -50;
        _savedEmulation = Input.EmulateMouseFromTouch;
        // Photo mode hides every layer but this one's: the camera is flown from it.
        GetParent()?.AddToGroup(GamePhotoMode.KeepLayerGroup);
        AddChild(_stick);
        ReadSettings(force: true);
        ApplyEmulation();
        // A phone with no controller starts on the screen: the menus open as touch menus.
        if (_enabled && !AnyKnownJoypad())
        {
            Engine_UiInputMode.Touch = true;
        }
    }

    public override void _EnterTree()
    {
        _sizedViewport = GetViewport();
        _sizedViewport.SizeChanged += TakeViewportSize;
        TakeViewportSize();
    }

    /// <summary>Takes the viewport's size and final transform, read per frame before: they change only with its size or stretch.</summary>
    private void TakeViewportSize()
    {
        if (_sizedViewport is null || !GodotObject.IsInstanceValid(_sizedViewport))
        {
            return;
        }

        _viewportSize = _sizedViewport.GetVisibleRect().Size;
        _toWindow = _sizedViewport.GetFinalTransform();
    }

    public override void _ExitTree()
    {
        if (_sizedViewport is not null && GodotObject.IsInstanceValid(_sizedViewport))
        {
            _sizedViewport.SizeChanged -= TakeViewportSize;
        }

        _sizedViewport = null;
        _pad.Cancel();
        GameInput.Touch = default;
        Refii.Mods.ModInputTouch.SetPad(0u, 0u, 0u);
        Refii.Mods.ModInputTouch.SetHeld(System.Array.Empty<string>());
        Input.EmulateMouseFromTouch = _savedEmulation;
    }

    /// <summary>Lets go of every finger when the app is left: a press must not outlive the focus.</summary>
    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut || what == NotificationApplicationPaused)
        {
            _pad.Cancel();
            _pointer.Cancel();
            FlushPointer();
            GameInput.Touch = default;
            _grabFinger = -1;
        }
    }

    public override void _Input(InputEvent @event)
    {
        // Under the dev menu Godot's emulation is the pointer (EmulatesMouse); a finger fed here too would click twice.
        if (!_enabled || GameDevMenu.WindowOpen)
        {
            return;
        }

        switch (@event)
        {
            case InputEventScreenTouch touch:
                if (touch.Pressed)
                {
                    Engine_UiInputMode.Touch = true;
                    if (GrabsPiece(touch.Position))
                    {
                        _grabFinger = touch.Index;
                    }
                    else
                    {
                        _pad.Down(touch.Index, touch.Position, Now);
                    }
                }
                else if (touch.Index == _grabFinger)
                {
                    _grabFinger = -1;
                }
                else
                {
                    _pad.Up(touch.Index, touch.Position, Now);
                }

                break;
            case InputEventScreenDrag drag when drag.Index == _grabFinger:
                GameDecorate.TouchDrag(drag.Position);
                break;
            case InputEventScreenDrag drag:
                _pad.Move(drag.Index, drag.Position, drag.Relative);
                break;
            default:
                return;
        }

        GetViewport().SetInputAsHandled();
        FlushPointer();
    }

    /// <summary>
    /// Whether a finger landing at <paramref name="at"/> takes the held piece while decorating: on
    /// it, and on no button or panel, which keep their fingers.
    /// </summary>
    private bool GrabsPiece(Vector2 at) =>
        _pad.Context == TouchContext.Decorate && _grabFinger < 0 &&
        !_passThrough.Any(rect => rect.HasPoint(at)) &&
        !_pad.Controls.Any(control => control.Kind is not (TouchControlKind.Stick or TouchControlKind.Look) && control.Hit(at)) &&
        GameDecorate.TouchGrab(at);

    public override void _Process(double delta)
    {
        ReadSettings(force: false);
        if (GameDevMenu.WindowOpen != _devMenuOpen)
        {
            // The fingers change hands: whatever the controls held lets go before Godot's emulation takes over, or back.
            _devMenuOpen = GameDevMenu.WindowOpen;
            _pad.Cancel();
            _pointer.Cancel();
            FlushPointer();
            GameInput.Touch = default;
            ApplyEmulation();
        }

        TouchContext context = _enabled ? Context() : TouchContext.Hidden;
        Vector2 size = _viewportSize;
        // Refreshed in place each frame: the pad holds this list, and the buttons it names lay out late.
        // Nothing is added while the diagnostics are closed (AndroidDiagnosticButtons.AnyRects).
        _passThrough.Clear();
        PassThrough?.Invoke(_passThrough);
        if (RestsHidden(context, _restedHidden, _laidOutContext, size == _laidOutSize && _scale == _laidOutScale &&
                AndroidTouchCustomLayout.Version == _laidOutCustom, _pad.Idle, _pointer.Idle, _hidingWheel))
        {
            // Nothing held and nothing to lay out: what the pad told the game last frame (nothing) stands.
            _wheelPicksSeen = GameUi.ExpressionWheelPicks;
            Present(Now);
            return;
        }

        if (context == TouchContext.Photo && GamePhotoMode.PanelRect is { } panel)
        {
            _passThrough.Add(panel);
        }
        else if (context == TouchContext.Mirror && GameMorphMirror.PanelRect is { } mirrorPanel)
        {
            _passThrough.Add(mirrorPanel);
        }
        else if (context == TouchContext.Decorate && GameDecorate.PanelRect is { } decoratePanel)
        {
            _passThrough.Add(decoratePanel);
        }

        (Vector2 Centre, float Reach)? slots = ItemSlots(size);
        bool slotsMoved = slots is { } fresh && (_laidOutSlots is not { } was ||
            fresh.Centre.DistanceTo(was.Centre) > 2f || Mathf.Abs(fresh.Reach - was.Reach) > 2f);
        TouchHero hero = Hero(delta, context == TouchContext.World);
        bool photoPanel = context == TouchContext.Decorate ? GameDecorate.PanelShown : GamePhotoMode.PanelShown;
        bool placing = context == TouchContext.Decorate && GameDecorate.Placing;
        string pubGame = GamePubGames.Showing;
        AndroidTouchModControls.Offer mods = context == TouchContext.World
            ? AndroidTouchModControls.Now(_fromHud ??= FromHud, _laidOutMods)
            : AndroidTouchModControls.Offer.None;
        if (context != TouchContext.World)
        {
            // The picker is the play's: a menu, a wheel or photo mode closes it.
            _pickerSpells = null;
        }

        if (context != _laidOutContext || size != _laidOutSize || _scale != _laidOutScale || slotsMoved ||
            hero != _laidOutHero || photoPanel != _laidOutPhotoPanel || placing != _laidOutPlacing || _pickerSpells != _laidOutPicker ||
            pubGame != _laidOutPubGame || AndroidTouchCustomLayout.Version != _laidOutCustom || !ReferenceEquals(mods, _laidOutMods))
        {
            _laidOutContext = context;
            _laidOutSize = size;
            _laidOutScale = _scale;
            _laidOutSlots = slots;
            _laidOutHero = hero;
            _laidOutPhotoPanel = photoPanel;
            _laidOutPlacing = placing;
            _laidOutPicker = _pickerSpells;
            _laidOutPubGame = pubGame;
            _laidOutCustom = AndroidTouchCustomLayout.Version;
            _laidOutMods = mods;
            IReadOnlyList<TouchControl> controls = AndroidTouchCustomLayout.Apply(
                AndroidTouchLayout.Build(size, _scale, context, slots, hero, photoPanel, pubGame, placing, mods), size, context);
            if (_pickerSpells is { } offered)
            {
                // Modal while it stands: the picker and its backdrop take every touch first.
                controls = AndroidTouchLayout.BuildSpellPicker(size, _scale, offered).Concat(controls).ToList();
            }

            _pad.SetLayout(context, controls, _passThrough,
                AndroidTouchLayout.StickRadius * AndroidTouchLayout.Unit(size, _scale));
        }

        foreach (string action in _pad.TakeActions())
        {
            if (action == "Options" && _pad.Controls.FirstOrDefault(c => c.Id == "Options") is { } options)
            {
                OptionsPressed?.Invoke(options.Bounds);
            }
            else if (action == AndroidTouchPad.PhotoTap)
            {
                GamePhotoMode.ShowPanel();
            }
            else if (action == AndroidTouchLayout.DecorateTap)
            {
                GameDecorate.TouchTap(_pad.LastTap);
            }
            else if (action == "Panel")
            {
                GameDecorate.ShowPanel();
            }
            else if (action == "RT")
            {
                // Spells tapped: the touch picker, or closed again when it is already open. With
                // fewer than two spells there is nothing to choose: the button is only the hold.
                _pickerSpells = _pickerSpells is null && Engine_UiSpellAssignSet.SpellsMenuCanOpen ? OfferedSpells() : null;
            }
            else if (action.StartsWith(AndroidTouchLayout.SpellPrefix, System.StringComparison.Ordinal) &&
                int.TryParse(action.Substring(AndroidTouchLayout.SpellPrefix.Length), out int will))
            {
                Engine_UiSpellAssignSet.ChooseForEveryLevel(will);
                _pickerSpells = null;
            }
            else if (action == AndroidTouchLayout.SpellsClose)
            {
                _pickerSpells = null;
            }
        }

        // A latched Emotes follows the guest's wheel: Close, Turn Safety or a back-out closes it,
        // and the latch lets go with it (AndroidTouchWheelRelease).
        // An action picked inside a group (GameUi.ExpressionWheelPicks) lets go at once: a dog
        // command leaves the wheel standing, an expression closes it.
        int picks = GameUi.ExpressionWheelPicks;
        bool picked = picks != _wheelPicksSeen;
        _wheelPicksSeen = picks;
        if (_emotesRelease.Update(_pad.IsLatched("Back"), GameUi.ExpressionWheelOpen, delta) ||
            (picked && _pad.IsLatched("Back")))
        {
            _pad.Unlatch("Back");
        }

        // What a finger holds of the mods' pills and areas, for their input actions this frame.
        double now = Now;
        AndroidTouchModControls.Hold(_pad, _pad.Controls, now, AndroidTouchModControls.HasControls(_laidOutMods));
        TouchPadOutput output = _pad.Sample(now);
        _hidingWheel = _pad.Touching("RT") || (_hidingWheel && Engine_UiSpellAssignSet.GuestWheelOpen);
        Engine_UiSpellAssignSet.TouchHidesWheel = _hidingWheel;
        if (_hidingWheel && Engine_UiSpellAssignSet.GuestWheelOpen)
        {
            // THE HIDDEN WHEEL STILL TAKES THE PAD: the stick turns its carousel, which assigns as
            // it turns, and X and Y pick partners. Under a touch hold only the trigger reaches the
            // game, so nothing is chosen unseen; the orbs come in all the same.
            output = output with { Buttons = 0u, LeftTrigger = 0u, LeftX = 0, LeftY = 0 };
        }

        GameInput.Touch = new GameInput.TouchHold(output.Buttons, output.LeftTrigger, output.RightTrigger,
            output.LeftX, output.LeftY);
        // The mods' pad actions read what a finger presses: the stick's sprint holds A for the game alone.
        Refii.Mods.ModInputTouch.SetPad(_pad.Sprinting && !_pad.Touching("A") ? output.Buttons & ~GameInput.PadA : output.Buttons,
            output.LeftTrigger, output.RightTrigger);
        Vector2 look = _pad.TakeLook();
        if (look != Vector2.Zero && size.Y > 0f)
        {
            if (GamePhotoMode.Active)
            {
                GamePhotoMode.PostTouchLook(look / size.Y, _lookSensitivity);
            }
            else if (GameMorphMirror.OwnsInput)
            {
                GameMorphMirror.PostTouchTurn(look.X / size.Y * _lookSensitivity);
            }
            else if (GameDecorate.Active)
            {
                GameDecorate.PostTouchLook(look / size.Y, _lookSensitivity);
            }
            else
            {
                GameInput.PostTouchLook(look / size.Y, _lookSensitivity);
            }
        }

        _pointer.Tick(now);
        FlushPointer();
        // At rest once the game has been told nothing is held, hidden, with nothing in flight.
        _restedHidden = context == TouchContext.Hidden && output == TouchPadOutput.Neutral && !_hidingWheel &&
            _pad.Idle && _pointer.Idle;
        Present(now);
    }

    /// <summary>
    /// Whether a frame has nothing to do: hidden as the last frame was, which ran whole and left the
    /// pad at rest (<paramref name="restedHidden"/>), laid out for the same screen, and with no finger,
    /// pointer gesture or held-open wheel since. The layout, the pad's sample and what it tells the
    /// game would all come out as they did.
    /// </summary>
    /// <param name="layoutSame">Whether the size, scale and the player's own layout are those laid out.</param>
    public static bool RestsHidden(TouchContext context, bool restedHidden, TouchContext laidOutContext, bool layoutSame,
        bool padIdle, bool pointerIdle, bool hidingWheel) =>
        context == TouchContext.Hidden && laidOutContext == TouchContext.Hidden && restedHidden && layoutSame &&
        padIdle && pointerIdle && !hidingWheel;

    /// <summary>The frame's drawing: shown or not, the stick, and a redraw when what is drawn changed.</summary>
    private void Present(double now)
    {
        // Off the screen while a photo is taken, so the picture is the world's alone.
        Visible = _enabled && !GamePhotoMode.Capturing;
        _stick.Follow(AndroidTouchDrawState.Stick(_pad, _laidOutSize, _scale, _opacity, _pickerSpells is not null), _pad.StickKnob);
        // Hidden nothing is drawn, and showing the controls draws them again by themselves.
        if (IsVisibleInTree() && AndroidTouchDrawState.NeedsRedraw(OverlayNow(now), _drawn, _artMissing, now - _drawnAt))
        {
            QueueRedraw();
        }
    }

    /// <summary>What the main canvas item would draw now (<see cref="_Draw"/>).</summary>
    private TouchDrawnOverlay OverlayNow(double now) => AndroidTouchDrawState.Overlay(_pad, _laidOutSize, _scale, _opacity,
        _toWindow.Scale.Y, _laidOutSlots, Engine_UiSpellAssignSet.HeroSpells.On(0) ?? -1,
        Engine_UiKeyGlyphs.PadAtlas, _pickerSpells is not null, now);

    /// <summary>
    /// The spells the picker offers: every will the first power level may hold — each learned
    /// spell has at least that level — with its name. Null when the hero's spells are not known yet.
    /// </summary>
    private static IReadOnlyList<(int Will, string Name)>? OfferedSpells()
    {
        Refii.Ui.Engine_UiModel.SpellAssignState spells = Engine_UiSpellAssignSet.HeroSpells;
        if (!spells.Known)
        {
            return null;
        }

        var offered = new List<(int Will, string Name)>();
        foreach (int will in spells.Choices(0))
        {
            offered.Add((will, Engine_UiSpellAssignSet.WillName(will)));
        }

        return offered.Count == 0 ? null : offered;
    }

    /// <summary>Childhood gives the toy pistol through Inventory and Carrying. Check those when
    /// the equipped record is empty. An unavailable guest sample leaves the control accessible.</summary>
    internal static bool CanUseRanged(uint? equipped, System.Func<ulong> carried, System.Func<int> toyPistolCount) =>
        equipped != 0u || carried() != 0u || toyPistolCount() != 0;

    /// <summary>
    /// What the hero can do, re-read twice a second: a ranged weapon equipped (Y), a spell (B), two
    /// spells (the spell menu). Unknown counts as able — nothing is hidden on a guess.
    /// </summary>
    /// <param name="world">Whether the play is laid out: only its buttons stand on what the hero can do, so
    /// elsewhere nothing is asked of the guest and the last answer stands, the clock running on so the
    /// play's first frame asks afresh.</param>
    private TouchHero Hero(double delta, bool world)
    {
        // Fable 3 plays on these controls too, and its bridge has none of Fable 2's hero queries:
        // every button stays offered.
        if (Refii.Core.Engine_Title.Active != Refii.Core.Engine_Title.Fable2) return _hero;
        if (!world)
        {
            _heroClock -= delta;
            return _hero;
        }

        // A villager's Y prompt comes and goes as the hero looks about: read every frame.
        bool villager = Engine_UiHudTargetedQuery.Present && Engine_UiHudTargetedQuery.Button;
        _heroClock -= delta;
        if (_heroClock > 0.0)
        {
            return _hero = _hero with { Villager = villager };
        }

        _heroClock = 0.5;
        bool ranged = CanUseRanged(Fable2Bridge.HeroWeapons()?.Ranged,
            static () => Fable2Bridge.CoopHeldUids(1)?.Ranged ?? 0,
            static () => Fable2Bridge.GetItemCount(1, "ObjectInventoryPistol_Child"));
        int? spells = Engine_UiSpellAssignSet.HeroSpellTypes;
        _hero = new TouchHero(ranged, spells is not { } known || known >= 1, Engine_UiSpellAssignSet.SpellsMenuCanOpen, villager);
        return _hero;
    }

    /// <summary>
    /// The HUD's item slots (GameUi.HudItemSlots) on this canvas: the UI draws through its own
    /// camera, Y up from the centre, so the slots are mapped through the canvas rectangle the
    /// window shows (Engine_UiPointer.VisibleCanvas). Null until the HUD has drawn them.
    /// </summary>
    private static (Vector2 Centre, float Reach)? ItemSlots(Vector2 size)
    {
        if (GameUi.HudItemSlots is not { } slots || Engine_UiPointer.VisibleCanvas is not { } canvas ||
            canvas.Size.X <= 0f || canvas.Size.Y <= 0f)
        {
            return null;
        }

        float sx = size.X / canvas.Size.X, sy = size.Y / canvas.Size.Y;
        var centre = new Vector2((slots.Centre.X - canvas.Position.X) * sx,
            (canvas.Position.Y + canvas.Size.Y - slots.Centre.Y) * sy);
        return (centre, slots.Reach * sy);
    }

    /// <summary>
    /// A HUD piece's drawn box (the UI camera's canvas, +Y up) on this canvas, mapped as
    /// <see cref="ItemSlots"/> maps the item cross; null until the HUD's canvas is known.
    /// </summary>
    private Rect2? FromHud(Rect2 piece)
    {
        if (Engine_UiPointer.VisibleCanvas is not { } canvas || canvas.Size.X <= 0f || canvas.Size.Y <= 0f)
        {
            return null;
        }

        Vector2 size = _viewportSize;
        float sx = size.X / canvas.Size.X, sy = size.Y / canvas.Size.Y;
        // The piece's top edge (its largest Y) is the nearer the screen's top.
        var topLeft = new Vector2((piece.Position.X - canvas.Position.X) * sx,
            (canvas.Position.Y + canvas.Size.Y - piece.End.Y) * sy);
        return new Rect2(topLeft, new Vector2(piece.Size.X * sx, piece.Size.Y * sy));
    }

    /// <summary>What the screen shows: the play, a wheel held open over it, or a menu.</summary>
    private TouchContext Context()
    {
        // A controller in hand hides the controls until the next touch; the dev menu is a
        // desktop window and wants the plain pointer; before the guest is up nothing is played.
        if (_hideControls || !Engine_UiInputMode.Touch || GameDevMenu.WindowOpen || HostBridge.TryGetAbiVersion() is null)
        {
            return TouchContext.Hidden;
        }

        // A pub game's board covers everything, the world and its menus alike. Asked before the
        // loading screen: the guest keeps its loading screen up behind the board for the whole
        // session (GamePubGames.LayerIndex), so a board is always over one.
        if (GamePubGames.Showing.Length != 0)
        {
            return TouchContext.PubGame;
        }

        // A loading screen takes no input: the pad drawn over its art and tip was only in the way.
        if (GameLoadingScreen.Showing)
        {
            return TouchContext.Hidden;
        }

        if (GamePhotoMode.Active)
        {
            return TouchContext.Photo;
        }

        if (GameMorphMirror.OwnsInput)
        {
            return TouchContext.Mirror;
        }

        if (GameDecorate.Active)
        {
            return TouchContext.Decorate;
        }

        if (!GameInput.Playing)
        {
            // A mod's screen in the pause book's place takes the taps, and the pad does not reach the game.
            return Refii.Mods.ModScreen.Owned ? TouchContext.ModScreen : TouchContext.Menu;
        }

        // The trigger a Spells hold sends is the overlay's own flag: the play keeps its layout.
        return GameInput.InOverlay && !_hidingWheel ? TouchContext.Wheel : TouchContext.World;
    }

    private static bool AnyKnownJoypad()
    {
        foreach (int id in Input.GetConnectedJoypads())
        {
            if (Input.IsJoyKnown(id))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    /// <remarks>Past refii.toml's reload check (half a second), not at once: a read now would find the cached file.</remarks>
    public void RereadSettingsSoon() => _settingsClock = System.Math.Min(_settingsClock, 0.6);

    /// <summary>Re-reads the touch settings now and then: the launcher writes refii.toml while the game runs.</summary>
    private void ReadSettings(bool force)
    {
        _settingsClock -= GetProcessDeltaTime();
        if (!force && _settingsClock > 0.0)
        {
            return;
        }

        _settingsClock = 2.0;
        var values = Refii.Core.Engine_RefiiConfig.Read(out _);
        if (!force && ReferenceEquals(values, _lastValues))
        {
            return;
        }

        _lastValues = values;
        bool enabled = Refii.Core.Engine_RefiiConfig.Bool(values, "touch_controls", true);
        _hideControls = Refii.Core.Engine_RefiiConfig.Bool(values, AndroidQuickSettings.HideTouchControlsKey, false);
        _scale = Mathf.Clamp(Refii.Core.Engine_RefiiConfig.Float(values, "touch_scale", 1f),
            AndroidTouchLayout.MinScale, AndroidTouchLayout.MaxScale);
        _opacity = Mathf.Clamp(Refii.Core.Engine_RefiiConfig.Float(values, "touch_opacity", 0.5f), 0.1f, 1f);
        _lookSensitivity = Mathf.Clamp(Refii.Core.Engine_RefiiConfig.Float(values, "touch_look_sensitivity", 1f), 0.2f, 4f);
        if (enabled != _enabled)
        {
            _enabled = enabled;
            _pad.Cancel();
            _pointer.Cancel();
            FlushPointer();
            GameInput.Touch = default;
            ApplyEmulation();
        }
    }

    private void ApplyEmulation() => Input.EmulateMouseFromTouch = EmulatesMouse(_enabled, _devMenuOpen, _savedEmulation);

    /// <summary>
    /// Whether Godot's own touch-to-mouse emulation runs: off while the controls own the fingers, but
    /// on under the open dev menu. That menu is an embedded window, and a focused embedded window is
    /// handed every event before the root sees it — the controls never get the finger to pass on as a
    /// mouse, so its tabs, its close button and the Debug (F6) button outside it would never be clicked.
    /// Godot's emulated mouse goes through the root's window routing like the desktop's.
    /// </summary>
    /// <param name="controlsEnabled">`touch_controls`.</param>
    /// <param name="devMenuOpen">Whether the dev menu's window is open.</param>
    /// <param name="projectSetting">The project's own emulation setting, kept for when the controls are off.</param>
    public static bool EmulatesMouse(bool controlsEnabled, bool devMenuOpen, bool projectSetting) =>
        devMenuOpen || (!controlsEnabled && projectSetting);

    /// <summary>Posts the pointer finger's mouse actions as mouse events, in window coordinates.</summary>
    private void FlushPointer()
    {
        double now = Now;
        foreach (TouchPointerEvent touch in _pad.TakePointerEvents())
        {
            _pointer.Feed(touch, now);
        }

        IReadOnlyList<TouchMouseAction> actions = _pointer.Take();
        if (actions.Count == 0)
        {
            return;
        }

        Transform2D toWindow = _toWindow;
        foreach (TouchMouseAction action in actions)
        {
            Vector2 at = toWindow * action.Position;
            switch (action.Kind)
            {
                case TouchMouseKind.Hover:
                case TouchMouseKind.Drag:
                    Input.ParseInputEvent(new InputEventMouseMotion
                    {
                        Device = (int)InputEvent.DeviceIdEmulation, Position = at, GlobalPosition = at,
                        Relative = at - _pointerWindow,
                        ButtonMask = action.Kind == TouchMouseKind.Drag ? MouseButtonMask.Left : (MouseButtonMask)0,
                    });
                    break;
                case TouchMouseKind.Press:
                case TouchMouseKind.Release:
                    bool pressed = action.Kind == TouchMouseKind.Press;

                    Input.ParseInputEvent(new InputEventMouseButton
                    {
                        Device = (int)InputEvent.DeviceIdEmulation, Position = at, GlobalPosition = at,
                        ButtonIndex = MouseButton.Left, Pressed = pressed,
                        ButtonMask = pressed ? MouseButtonMask.Left : (MouseButtonMask)0,
                    });
                    break;
                case TouchMouseKind.Wheel:
                    MouseButton wheel = action.Notches > 0 ? MouseButton.WheelDown : MouseButton.WheelUp;
                    for (int i = 0; i < System.Math.Abs(action.Notches); ++i)
                    {
                        foreach (bool down in new[] { true, false })
                        {
                            Input.ParseInputEvent(new InputEventMouseButton
                            {
                                Device = (int)InputEvent.DeviceIdEmulation, Position = at, GlobalPosition = at,
                                ButtonIndex = wheel, Pressed = down, Factor = 1f,
                            });
                        }
                    }

                    break;
            }

            _pointerWindow = at;
        }
    }

    public override void _Draw()
    {
        _drawnAt = Now;
        _drawn = OverlayNow(_drawnAt);
        _artMissing = false;
        Font font = ThemeDB.FallbackFont;
        float unit = AndroidTouchLayout.Unit(_laidOutSize, _scale);
        // A menu's controls are a little lighter than the world's — its art is bright, and fainter
        // than this the see-through plates vanish into it.
        float idle = AndroidTouchDrawState.IdleAlpha(_opacity, _pad.Context);
        foreach (TouchControl control in _pad.Controls)
        {
            if (control.Id == AndroidTouchLayout.SpellsClose ||
                control.Id.StartsWith(AndroidTouchLayout.SpellPrefix, System.StringComparison.Ordinal) ||
                AndroidTouchModControls.IsArea(control))
            {
                continue;
            }

            bool active = _pad.IsActive(control.Id);
            float alpha = active ? Mathf.Min(1f, idle + 0.35f) : idle;
            switch (control.Kind)
            {
                case TouchControlKind.Stick:
                    // On its own items, above all this; but under the open picker, which is drawn last here.
                    if (_drawn.InlineStick.Origin is { } origin)
                    {
                        AndroidTouchStick.DrawBase(this, origin, _drawn.InlineStick);
                        AndroidTouchStick.DrawKnob(this, origin + _drawn.InlineKnob, _drawn.InlineStick);
                    }

                    continue;
                case TouchControlKind.Look:
                    continue;
                case TouchControlKind.DPad when control.OverHud:
                    DrawHudSlots(unit, idle);
                    continue;
                case TouchControlKind.DPad:
                    DrawDPad(control, font, unit, idle);
                    continue;
            }

            DrawControl(control, active, alpha, unit, font);
            DrawPadName(control, alpha, unit, font);
        }

        if (_pickerSpells is not null)
        {
            DrawSpellPicker(unit, font);
        }
    }

    /// <summary>
    /// The touch spell picker over the dimmed play: a plate of the game's keycap palette behind the
    /// row, each spell the game's own icon on a dark disc, ringed in gold, the spell each power
    /// level now casts from the first ringed brighter, and its name beneath in the game's font.
    /// </summary>
    private void DrawSpellPicker(float unit, Font fallback)
    {
        DrawRect(new Rect2(Vector2.Zero, _laidOutSize), Colors.Black with { A = 0.35f });
        Rect2? row = null;
        foreach (TouchControl control in _pad.Controls)
        {
            if (control.Id.StartsWith(AndroidTouchLayout.SpellPrefix, System.StringComparison.Ordinal))
            {
                row = row is { } so_far ? so_far.Merge(control.Bounds) : control.Bounds;
            }
        }

        if (row is not { } spellsRow)
        {
            return;
        }

        Color trim = Engine_UiKeyGlyphs.TrimColour;
        var plate = new Rect2(spellsRow.Position - new Vector2(28f, 22f) * unit, spellsRow.Size + new Vector2(56f, 80f) * unit);
        _box.BgColor = Engine_UiKeyGlyphs.PlateColour;
        _box.BorderColor = trim with { A = 0.8f };
        _box.SetBorderWidthAll((int)Mathf.Max(1f, 2f * unit));
        _box.SetCornerRadiusAll((int)(24f * unit));
        DrawStyleBox(_box, plate);

        int current = Engine_UiSpellAssignSet.HeroSpells.On(0) ?? -1;
        foreach (TouchControl control in _pad.Controls)
        {
            if (!control.Id.StartsWith(AndroidTouchLayout.SpellPrefix, System.StringComparison.Ordinal) ||
                !int.TryParse(control.Id.Substring(AndroidTouchLayout.SpellPrefix.Length), out int will))
            {
                continue;
            }

            Vector2 centre = control.Bounds.GetCenter();
            float radius = control.Bounds.Size.X * 0.5f;
            bool chosen = will == current;
            bool pressed = _pad.IsActive(control.Id);
            DrawCircle(centre, radius, Colors.Black with { A = 0.55f });
            if (!_willIcons.TryGetValue(will, out Texture2D? icon) || icon is null)
            {
                _willIcons[will] = icon = Engine_UiSpellAssignSet.WillIcon(will);
            }

            if (icon is not null)
            {
                DrawTextureRect(icon, control.Bounds.Grow(-radius * 0.12f), false, pressed ? Colors.White.Lightened(0.2f) : Colors.White);
            }
            else
            {
                _artMissing = true;
            }

            DrawArc(centre, radius, 0f, Mathf.Tau, 48, chosen ? trim : trim with { A = 0.45f }, (chosen ? 4f : 2f) * unit, true);
            if (control.Caption.Length > 0)
            {
                DrawText(control.Caption, new Vector2(centre.X, control.Bounds.End.Y + 8f * unit), 20f * unit,
                    chosen ? trim : trim with { A = 0.85f }, fallback, top: true);
            }
        }
    }

    /// <summary>
    /// One button in the game's own look: A, B, X and Y as see-through plates lettered in their
    /// own colours, the game's pressed controller art while held, their action named beneath in
    /// the game's font; every other button a dark plate with the gold trim and lettering of the
    /// game's keycap prompts. Until the UI has loaded that art and font, plainly drawn.
    /// </summary>
    private void DrawControl(TouchControl control, bool active, float alpha, float unit, Font fallback)
    {
        bool latched = _pad.IsLatched(control.Id);
        Vector2 centre = control.Bounds.GetCenter();
        Color trim = Engine_UiKeyGlyphs.TrimColour;
        if (control.Round && control.Label.Length == 1 && "ABXY".Contains(control.Label))
        {
            // A FACE BUTTON IS COLOURED GLASS AT REST: between the game's glossy controller art
            // (Engine_UiKeyGlyphs.PadAtlas), which over a dark scene reads as solid at any opacity
            // that keeps its colours, and a bare outline. Pressed, the game's pressed orb, opaque.
            if (active && Engine_UiKeyGlyphs.PadAtlas is { } atlas &&
                Engine_UiKeyGlyphs.PadAtlasCell(control.Label, pressed: true) is { } cell)
            {
                // The orb fills about four fifths of its atlas cell: the cell is drawn a quarter
                // larger than the button so the orb seen is the orb pressed.
                DrawTextureRectRegion(atlas, control.Bounds.Grow(control.Bounds.Size.X * 0.125f), cell, Colors.White);
            }
            else
            {
                // COLOURED GLASS: a disc of the button's own colour the scene shows through, the
                // game's orb laid over it at part strength for its gloss and its letter (a second,
                // crisp letter doubles the orb's), and a rim in the button's colour.
                float radius = control.Bounds.Size.X * 0.5f;
                Color tint = control.Color.Lightened(0.25f);
                DrawCircle(centre, radius, control.Color.Darkened(0.2f) with { A = alpha * 0.7f });
                if (Engine_UiKeyGlyphs.PadAtlas is { } gloss &&
                    Engine_UiKeyGlyphs.PadAtlasCell(control.Label, pressed: false) is { } idleCell)
                {
                    DrawTextureRectRegion(gloss, control.Bounds.Grow(control.Bounds.Size.X * 0.125f), idleCell,
                        Colors.White with { A = Mathf.Min(1f, alpha * 0.85f) });
                }

                DrawArc(centre, radius - 1.5f * unit, 0f, Mathf.Tau, 48, tint with { A = Mathf.Min(1f, alpha + 0.3f) },
                    3f * unit, true);
            }

            if (control.Caption.Length > 0)
            {
                DrawText(control.Caption, new Vector2(centre.X, control.Bounds.End.Y + 4f * unit), 20f * unit,
                    trim with { A = Mathf.Min(1f, alpha + 0.35f) }, fallback, top: true);
            }

            return;
        }

        // The keycaps' plate and trim; the plate fuller while pressed, the trim heavier latched.
        Color plate = Engine_UiKeyGlyphs.PlateColour with { A = alpha * (active ? 0.95f : 0.7f) };
        Color rim = trim with { A = alpha * (latched ? 1f : 0.6f) };
        float rimWidth = (latched ? 3.5f : 1.5f) * unit;
        if (control.Round)
        {
            float radius = control.Bounds.Size.X * 0.5f;
            DrawCircle(centre, radius, plate);
            DrawArc(centre, radius - rimWidth * 0.5f, 0f, Mathf.Tau, 48, rim, rimWidth, true);
        }
        else
        {
            _box.BgColor = plate;
            _box.BorderColor = rim;
            _box.SetCornerRadiusAll((int)(control.Bounds.Size.Y * 0.3f));
            _box.SetBorderWidthAll((int)Mathf.Max(1f, rimWidth));
            DrawStyleBox(_box, control.Bounds);
            if (DrawPillIcon(control, trim with { A = Mathf.Min(1f, alpha + 0.4f) }, unit))
            {
                // The word in the room right of the icon.
                float pillHeight = control.Bounds.Size.Y;
                float left = control.Bounds.Position.X + pillHeight * 0.85f, right = control.Bounds.End.X - pillHeight * 0.25f;
                DrawText(control.Label, new Vector2((left + right) * 0.5f, centre.Y), pillHeight * 0.42f,
                    trim with { A = Mathf.Min(1f, alpha + 0.4f) }, fallback, top: false);
                return;
            }
        }

        if (control.Round && control.Label.Length == 0 && control.Id is "RT" or "LT" or "LB" or "RB")
        {
            // SPELLS AND LOCK READ AT A GLANCE: Spells the game's own icon of the spell B casts
            // now, Lock a target ring; a menu's tab buttons a double chevron pointing their way.
            // Each named small beneath.
            float radius = control.Bounds.Size.X * 0.5f;
            Color ink = trim with { A = Mathf.Min(1f, alpha + 0.45f) };
            if (control.Id == "RT")
            {
                int will = Engine_UiSpellAssignSet.HeroSpells.On(0) ?? -1;
                if (will >= 0 && (!_willIcons.TryGetValue(will, out Texture2D? icon) || icon is null))
                {
                    _willIcons[will] = icon = Engine_UiSpellAssignSet.WillIcon(will);
                }

                if (will >= 0 && _willIcons[will] is { } spellIcon)
                {
                    DrawTextureRect(spellIcon, control.Bounds.Grow(-radius * 0.2f), false,
                        Colors.White with { A = Mathf.Min(1f, alpha + 0.45f) });
                }
                else
                {
                    // No spell to show: three orbs, what the hold draws in.
                    _artMissing |= will >= 0;
                    foreach ((float x, float y, float r) in new[] { (-0.22f, 0.12f, 0.17f), (0.2f, 0.16f, 0.13f), (0f, -0.2f, 0.15f) })
                    {
                        DrawCircle(centre + new Vector2(x, y) * radius, r * radius, ink);
                    }
                }

                // THE HOLD IS THE TRIGGER, WHICH DRAWS IN THE ORBS: a ring fills round the button
                // as a press becomes a hold, and stays full while it is one.
                float hold = _drawn.Hold;
                if (hold > 0f)
                {
                    DrawArc(centre, radius + 4f * unit, -Mathf.Pi * 0.5f, -Mathf.Pi * 0.5f + Mathf.Tau * hold, 48,
                        (hold >= 1f ? AndroidTouchLayout.YellowY : trim) with { A = Mathf.Min(1f, alpha + 0.5f) }, 5f * unit, true);
                }

                // What the hold does, as large as the name and over the button, where the room is:
                // under it stand the name and then B.
                DrawText(AndroidTouchLayout.OrbsHint, new Vector2(centre.X, control.Bounds.Position.Y - 26f * unit), 18f * unit,
                    trim with { A = Mathf.Min(1f, alpha + 0.35f) }, fallback, top: true);
            }
            else if (control.Id is "LB" or "RB")
            {
                // Two chevrons side by side, centred, pointing the tab's way.
                float way = control.Id == "LB" ? -1f : 1f, chevron = radius * 0.28f;
                foreach (float offset in new[] { -chevron * 0.6f, chevron * 0.6f })
                {
                    var point = new Vector2(centre.X + way * chevron * 0.5f + offset, centre.Y);
                    DrawPolyline(new[] { point + new Vector2(-way * chevron, -chevron), point, point + new Vector2(-way * chevron, chevron) },
                        ink, 3.5f * unit, true);
                }
            }
            else
            {
                DrawArc(centre, radius * 0.5f, 0f, Mathf.Tau, 40, ink, 3f * unit, true);
                for (int tick = 0; tick < 4; ++tick)
                {
                    Vector2 along = Vector2.Right.Rotated(tick * Mathf.Pi * 0.5f);
                    DrawLine(centre + along * radius * 0.28f, centre + along * radius * 0.78f, ink, 3f * unit, true);
                }

                DrawCircle(centre, radius * 0.08f, ink);
            }

            DrawText(control.Caption, new Vector2(centre.X, control.Bounds.End.Y + 4f * unit), 18f * unit,
                trim with { A = Mathf.Min(1f, alpha + 0.35f) }, fallback, top: true);
            return;
        }

        float across = control.Round ? control.Bounds.Size.X : control.Bounds.Size.Y * 2f;
        float size = across * (control.Label.Length <= 2 ? 0.40f : 0.22f);
        DrawText(control.Label, centre, size, trim with { A = Mathf.Min(1f, alpha + 0.4f) }, fallback, top: false);
        // A lettered round button says what it does beneath, as the face buttons do: a pub game's
        // LB (Max bet) and Keystone's RB (Rebet).
        if (control.Round && control.Caption.Length > 0)
        {
            DrawText(control.Caption, new Vector2(centre.X, control.Bounds.End.Y + 4f * unit), 18f * unit,
                trim with { A = Mathf.Min(1f, alpha + 0.35f) }, fallback, top: true);
        }
    }

    /// <summary>
    /// THE GAME'S PROMPTS NAME THE CONTROLLER'S BUTTONS — "hold RT", "press Back" — and the touch
    /// controls are named for what they do: Spells, Lock, Emotes, Menu. A small tag of the
    /// controller's name ties the two: on a round control's top corner toward the middle of the
    /// screen, on a pill's top edge over its icon.
    /// </summary>
    private void DrawPadName(TouchControl control, float alpha, float unit, Font fallback)
    {
        string name = AndroidTouchLayout.PadName(control);
        if (name.Length == 0 || name == control.Label)
        {
            return;
        }

        float inward = control.Bounds.GetCenter().X < GetViewportRect().Size.X * 0.5f ? 1f : -1f;
        Vector2 at;
        if (control.Round)
        {
            float radius = control.Bounds.Size.X * 0.5f;
            at = control.Bounds.GetCenter() + new Vector2(inward * radius * 0.72f, -radius * 0.72f);
        }
        else
        {
            // A pill's over its icon, at the left end, so the tag, the icon and the word line up.
            at = new Vector2(control.Bounds.Position.X + control.Bounds.Size.Y * 0.55f, control.Bounds.Position.Y);
        }

        float height = 15f * unit;
        var tag = new Rect2(at - new Vector2(name.Length * 4.5f * unit + 6f * unit, height * 0.5f + 2f * unit),
            new Vector2(name.Length * 9f * unit + 12f * unit, height + 4f * unit));
        _box.BgColor = Engine_UiKeyGlyphs.PlateColour with { A = Mathf.Min(1f, alpha + 0.2f) };
        _box.BorderColor = Engine_UiKeyGlyphs.TrimColour with { A = Mathf.Min(1f, alpha + 0.2f) };
        _box.SetCornerRadiusAll((int)(tag.Size.Y * 0.5f));
        _box.SetBorderWidthAll((int)Mathf.Max(1f, 1.5f * unit));
        DrawStyleBox(_box, tag);
        DrawText(name, at, height, Engine_UiKeyGlyphs.TrimColour with { A = Mathf.Min(1f, alpha + 0.45f) }, fallback, top: false);
    }

    /// <summary>
    /// The icon at the left of a top-row or corner pill, in the gold line art of Lock's target
    /// ring: Menu the controller's Start glyph (three lines), Emotes a smiling face, Options a gear,
    /// View an eye. False for a pill with no icon.
    /// </summary>
    private bool DrawPillIcon(TouchControl control, Color ink, float unit)
    {
        float h = control.Bounds.Size.Y, r = h * 0.24f, line = 2.5f * unit;
        var at = new Vector2(control.Bounds.Position.X + h * 0.5f, control.Bounds.GetCenter().Y);
        switch (control.Id)
        {
            case "Start":
                for (int row = -1; row <= 1; ++row)
                {
                    DrawLine(at + new Vector2(-r, row * r * 0.6f), at + new Vector2(r, row * r * 0.6f), ink, line * 1.2f, true);
                }

                return true;
            case "Back":
                DrawArc(at, r, 0f, Mathf.Tau, 32, ink, line, true);
                DrawCircle(at + new Vector2(-r * 0.36f, -r * 0.25f), r * 0.12f, ink);
                DrawCircle(at + new Vector2(r * 0.36f, -r * 0.25f), r * 0.12f, ink);
                DrawArc(at + new Vector2(0f, r * 0.02f), r * 0.52f, Mathf.Pi * 0.18f, Mathf.Pi * 0.82f, 16, ink, line, true);
                return true;
            case "Options":
                DrawArc(at, r * 0.6f, 0f, Mathf.Tau, 32, ink, line * 1.2f, true);
                for (int tooth = 0; tooth < 8; ++tooth)
                {
                    Vector2 along = Vector2.Right.Rotated(tooth * Mathf.Pi * 0.25f);
                    DrawLine(at + along * r * 0.62f, at + along * r, ink, line * 1.6f, true);
                }

                DrawCircle(at, r * 0.2f, ink);
                return true;
            case "LB" when !control.Round:
                Vector2[] almond = _almond;
                for (int i = 0; i <= 8; ++i)
                {
                    float x = -1f + i * 0.25f, y = 0.58f * (1f - x * x);
                    almond[i] = at + new Vector2(x * r, -y * r);
                    almond[16 - i] = at + new Vector2(x * r, y * r);
                }

                DrawPolyline(almond, ink, line, true);
                DrawCircle(at, r * 0.3f, ink);
                return true;
            case "Odds":
                // A die's face showing five, the odds being on the dice and the spins.
                DrawRect(new Rect2(at - new Vector2(r, r) * 0.85f, new Vector2(r, r) * 1.7f), ink, false, line);
                foreach (Vector2 pip in _diePips)
                {
                    DrawCircle(at + pip * r * 0.45f, r * 0.13f, ink);
                }

                return true;
            case "Stats":
                // Three bars rising from a base line.
                DrawLine(at + new Vector2(-r, r * 0.85f), at + new Vector2(r, r * 0.85f), ink, line, true);
                for (int bar = 0; bar < 3; ++bar)
                {
                    float x = (bar - 1) * r * 0.62f, height = r * (0.6f + bar * 0.5f);
                    DrawLine(at + new Vector2(x, r * 0.85f), at + new Vector2(x, r * 0.85f - height), ink, line * 1.8f, true);
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>The game-font images drawn so far, by text and pixel height.</summary>
    private readonly Dictionary<(string Text, int Pixels), ImageTexture> _texts = new();

    /// <summary>
    /// <paramref name="text"/> in the game's font, <paramref name="height"/> canvas units tall,
    /// centred on <paramref name="at"/> (or hanging from it, <paramref name="top"/>). Rendered once
    /// at the window's own pixel density and kept; the fallback font until the game's has loaded.
    /// </summary>
    private void DrawText(string text, Vector2 at, float height, Color colour, Font fallback, bool top)
    {
        float density = Mathf.Max(1f, GetViewport().GetFinalTransform().Scale.Y);
        int pixels = Mathf.Max(8, Mathf.RoundToInt(height * density));
        if (!_texts.TryGetValue((text, pixels), out ImageTexture? texture))
        {
            if (Engine_UiKeyGlyphs.TextImage(text, pixels, Colors.White, new Color(0f, 0f, 0f, 0.85f)) is not { } image)
            {
                _artMissing = true;
                int size = Mathf.RoundToInt(height);
                float y = top ? at.Y + size * 0.85f : at.Y + size * 0.35f;
                DrawString(fallback, new Vector2(at.X - 200f, y), text, HorizontalAlignment.Center, 400f, size, colour);
                return;
            }

            texture = ImageTexture.CreateFromImage(image);
            _texts[(text, pixels)] = texture;
        }

        Vector2 drawn = texture.GetSize() / density;
        var rect = new Rect2(new Vector2(at.X - drawn.X * 0.5f, top ? at.Y : at.Y - drawn.Y * 0.5f), drawn);
        DrawTextureRect(texture, rect, false, colour);
    }

    /// <summary>
    /// The items pressed where the game draws them: a faint ring round each of the HUD's four
    /// slots, so the game's own icons show through, and the held one lit.
    /// </summary>
    private void DrawHudSlots(float unit, float alpha)
    {
        // Drawn even while the HUD hides its slots, so the item buttons can always be found.
        (Vector2 Centre, float Reach) slots = _laidOutSlots ?? AndroidTouchLayout.DefaultItemSlots(_laidOutSize);
        float ring = Mathf.Max(slots.Reach * 0.62f, 16f * unit);
        foreach ((uint bit, Vector2 direction) in DPadArms)
        {
            Vector2 at = slots.Centre + direction * slots.Reach;
            bool lit = (_pad.DPadBits & bit) != 0;
            Color trim = Engine_UiKeyGlyphs.TrimColour;
            if (lit)
            {
                DrawCircle(at, ring, trim with { A = alpha * 0.45f });
            }

            DrawArc(at, ring, 0f, Mathf.Tau, 32, trim with { A = alpha * (lit ? 1f : 0.5f) },
                Mathf.Max(1f, 1.5f * unit), true);
        }
    }

    /// <summary>The rounded crosses drawn so far, by where they stand: each is built once.</summary>
    private readonly Dictionary<(Vector2 Centre, float Reach), (Vector2[] Fill, Vector2[] Trace)> _crosses = new();

    /// <summary>
    /// A D-pad's cross as one closed outline with every corner rounded: the outer corners of the arms
    /// by a third of an arm's width, the inner ones where the arms meet by a fifth (an opening, then
    /// a closing, of the sharp cross).
    /// </summary>
    // Its fixed tables and point scratch are kept (Godot copies a drawn polygon), not made again each draw.
    private static readonly (uint Bit, Vector2 Direction)[] DPadArms =
    {
        (GameInput.PadUp, Vector2.Up), (GameInput.PadDown, Vector2.Down),
        (GameInput.PadLeft, Vector2.Left), (GameInput.PadRight, Vector2.Right),
    };

    private readonly Vector2[] _almond = new Vector2[17];

    /// <summary>The pips of the Odds pill's die, a five, as offsets from its centre.</summary>
    private static readonly Vector2[] _diePips =
        { new(-1f, -1f), new(1f, -1f), Vector2.Zero, new(-1f, 1f), new(1f, 1f) };
    private readonly Vector2[] _chevron = new Vector2[3];

    private (Vector2[] Fill, Vector2[] Trace) RoundedCross(Vector2 centre, float reach, float arm)
    {
        if (_crosses.TryGetValue((centre, reach), out (Vector2[] Fill, Vector2[] Trace) cached))
        {
            return cached;
        }

        float h = arm * 0.5f;
        Vector2[] sharp =
        {
            new(-h, -reach), new(h, -reach), new(h, -h), new(reach, -h), new(reach, h), new(h, h),
            new(h, reach), new(-h, reach), new(-h, h), new(-reach, h), new(-reach, -h), new(-h, -h),
        };
        for (int i = 0; i < sharp.Length; ++i) sharp[i] += centre;
        Vector2[] Offset(Vector2[] polygon, float by) =>
            Geometry2D.OffsetPolygon(polygon, by, Geometry2D.PolyJoinType.Round) is { Count: > 0 } grown ? grown[0] : polygon;
        float outer = arm / 3f, inner = arm / 5f;
        Vector2[] rounded = Offset(Offset(Offset(Offset(sharp, -outer), outer), inner), -inner);
        // Closed for the trace; the fill takes the polygon as it is.
        var closed = new Vector2[rounded.Length + 1];
        rounded.CopyTo(closed, 0);
        closed[^1] = rounded[0];
        return _crosses[(centre, reach)] = (rounded, closed);
    }

    /// <summary>The D-pad: a joined cross, the held arm lit.</summary>
    private void DrawDPad(TouchControl control, Font font, float unit, float alpha)
    {
        Vector2 centre = control.Bounds.GetCenter();
        float reach = control.Bounds.Size.X * 0.5f, arm = reach * 0.72f;
        uint held = _pad.DPadBits;
        Color idleFill = Engine_UiKeyGlyphs.PlateColour with { A = alpha * 0.7f };
        Color litFill = Engine_UiKeyGlyphs.TrimColour with { A = alpha * 0.6f };
        Color rim = Engine_UiKeyGlyphs.TrimColour with { A = alpha * 0.7f };
        // One rounded outline, filled and then traced, so it reads over a dark street as well as a
        // bright sky and its corners round like the pills' and buttons'.
        (Vector2[] fill, Vector2[] trace) = RoundedCross(centre, reach, arm);
        DrawColoredPolygon(fill, idleFill);
        DrawPolyline(trace, rim, Mathf.Max(1f, 2f * unit), true);
        _box.SetCornerRadiusAll((int)(arm * 0.3f));
        _box.SetBorderWidthAll(0);
        foreach ((uint bit, Vector2 direction) in DPadArms)
        {
            // Each arm's tip, with a chevron pointing out; lit while held.
            Vector2 tip = centre + direction * (reach - arm * 0.5f);
            if ((held & bit) != 0)
            {
                _box.BgColor = litFill;
                DrawStyleBox(_box, new Rect2(tip - new Vector2(arm * 0.5f, arm * 0.5f), new Vector2(arm, arm)));
            }

            Vector2 side = new Vector2(-direction.Y, direction.X) * arm * 0.22f;
            Vector2 point = tip + direction * arm * 0.18f, back = tip - direction * arm * 0.12f;
            _chevron[0] = point;
            _chevron[1] = back + side;
            _chevron[2] = back - side;
            DrawColoredPolygon(_chevron, rim);
        }

        if (control.Caption.Length > 0)
        {
            int size = (int)(18f * unit);
            DrawString(font, new Vector2(control.Bounds.Position.X, centre.Y + size * 0.35f), control.Caption,
                HorizontalAlignment.Center, control.Bounds.Size.X, size, Colors.White with { A = Mathf.Min(1f, alpha + 0.2f) });
        }
    }
}
