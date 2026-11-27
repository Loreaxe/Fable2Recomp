using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using Refii.Core;
using Refii.Host;
using Refii.Level;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

public partial class GameDecorate : Node
{
    public static bool Active { get; private set; }
    private static ulong _selectedUid;
    private static ulong _aimUid;
    private static readonly Dictionary<ulong, uint> RoomAddresses = new();
    private static readonly Dictionary<ulong, uint> RoomModels = new();
    private static readonly HashSet<ulong> RefreshHighlights = new();
    public static bool Selected(ulong uid) => Active && uid != 0 && uid == _selectedUid;
    public static bool Targeted(ulong uid) => Active && uid != 0 && (uid == _selectedUid || uid == _aimUid);
    /// <summary>Whether <see cref="Appearance"/> can do anything this walk: not decorating, nothing to refresh, it cannot.</summary>
    public static bool RestatesEntities => Active || RefreshHighlights.Count > 0;
    public static HostBridge.GuestEntity Appearance(HostBridge.GuestEntity entity)
    {
        if (Active && RoomAddresses.TryGetValue(entity.UniqueId, out uint address) && address != entity.Address)
            RoomAddresses[entity.UniqueId] = entity.Address;
        if (Active && RoomAddresses.ContainsKey(entity.UniqueId) && entity.ModelHash != 0) RoomModels[entity.UniqueId] = entity.ModelHash;
        if (Targeted(entity.UniqueId))
            return entity with { Highlight = 0x19FF26FFu, Changed = true };
        return RefreshHighlights.Count > 0 && RefreshHighlights.Remove(entity.UniqueId) ? entity with { Changed = true } : entity;
    }
    private static void SelectUid(ulong uid)
    {
        if (_selectedUid != uid && _selectedUid != 0) RefreshHighlights.Add(_selectedUid);
        _selectedUid = uid;
    }
    private readonly List<CanvasLayer> _hidden = new();
    private readonly List<string> _labels = new();
    private readonly List<uint> _cards = new();
    private readonly List<int> _stored = new();
    private int _storedId;
    private Vector2 _tilt, _tiltOrigin;
    private int _editAxis = 1;
    private Vector3 Angles { get => new(_tilt.X, _rotation, _tilt.Y); set { _tilt = new Vector2(value.X, value.Z); _rotation = value.Y; } }
    private Vector3 OriginAngles => new(_tiltOrigin.X, _rotationOrigin, _tiltOrigin.Y);
    private readonly List<(ulong Uid, string Label)> _targets = new();
    private readonly Engine_DecoratePicking _picking = new();
    private readonly Engine_DecorateCollision _collision = new();
    private readonly Engine_DecorateInterior _interior = new();
    private Mesh? _interiorMesh;
    private Transform3D _interiorPose;
    private uint _building;
    private double _interiorPoll;
    private bool _collisionEnabled, _blocked, _collisionDirty;
    private bool _acceptedPreview, _editingRotation, _replaceRotationText;
    private Vector3 _acceptedPiece;
    private Vector3 _acceptedAngles;
    private float _rotationOrigin, _rotationBeforeEdit;
    private string _rotationEntry = "";
    private bool _pendingMove;
    private Vector3 _pendingPiece;
    private Vector3 _pendingAngles;
    private double _collisionPoll;
    private Node3D? _reticle;
    private StandardMaterial3D? _reticleMaterial;
    private MeshInstance3D? _ghost;
    private Task<Mesh?>? _ghostLoad;
    private Transform3D _ghostSource, _ghostOffset;
    private uint _previewCard;
    private double _ghostPoll;
    private bool HasSelection => _selectedUid != 0 || _previewCard != 0 || _storedId != 0;
    /// <summary>The node that hosts the play: the desktop's GameSession, or the host this was added
    /// to where the guest is stepped without one (Android), as the mirror and photo mode allow.</summary>
    private Node? _session;
    private Camera3D? _camera, _uiCamera;
    private CanvasLayer? _layer;
    private SubViewportContainer? _container;
    private Engine_UiDecorate? _ui;
    private Transform3D _original;
    private Input.MouseModeEnum _mouseMode;
    private Vector3 _position, _piece, _dragOffset, _dragPlanePoint, _dragPlaneNormal;
    private Vector2 _mouse, _size, _cursor;
    private float _yaw, _pitch, _rotation, _scroll;
    private uint _held = uint.MaxValue, _generation;
    private int _row, _aimIndex = -1, _revision = -1, _device = -1;
    private double _poll, _touch, _config, _repeat, _wait, _aimPoll;
    private bool _room, _waiting, _dirty, _enabled, _uiHidden, _dragReady, _cancelPending, _awaitSelection;
    private bool _adding, _looking;
    private bool _guestRequested;

    public static string? KeyLabel(string button) => button switch
    {
        "A" => "Enter", "B" => "Esc", "Y" => "Del", "X" => "Tab", "Back" => "H", "LS" => "Shift", "RS" => "U",
        "Start" => "F10", "LB" => ",", "RB" => ".", "DPad" => "K", _ => null
    };

    public override void _Ready() => ProcessPriority = 100;
    private static float Read(string key) => HostBridge.TryGetModValue(Engine_DecorateRuntime.Prefix + key, out float value) && float.IsFinite(value) ? value : 0;
    private static ulong ReadUid(string key)
    {
        ulong id = 0;
        for (int i = 0; i < 4; i++) id |= (ulong)Math.Clamp((int)Read(key + i), 0, 65535) << (i * 16);
        return id;
    }
    private static void Command(string method, string arguments = "") => HostBridge.PostModCommand(
        "run_lua if RefiiDecorate then RefiiDecorate.Call(\"" + method + "\"" + (arguments.Length == 0 ? "" : "," + arguments) + ") end");

    public override void _Process(double delta)
    {
        _config -= delta;
        if (_config <= 0 && HostBridge.IsInGame)
        {
            _config = 2;
            _enabled = Engine_RefiiConfig.Bool(Engine_RefiiConfig.Read(out _), Engine_DecorateCatalog.Setting, false);
            HostBridge.PostModCommand("publish refii.decorate.enabled " + (_enabled ? "1" : "0"));
        }
        if (!HostBridge.IsInGame) { Close(false); RefreshHighlights.Clear(); _revision = -1; _guestRequested = false; return; }
        _poll -= delta;
        if (_poll <= 0)
        {
            _poll = 0.1;
            uint generation = Fable2Bridge.GetNewGameState().Generation;
            if (generation != _generation) { Close(false); RefreshHighlights.Clear(); }
            _generation = generation;
            _guestRequested = Read("active") == 1;
            if (Active && (!_guestRequested || !_enabled)) { Close(_guestRequested); return; }
            if (!Active && _enabled && _guestRequested && !GamePhotoMode.Active && !GameMorphMirror.OwnsInput &&
                GameUi.WorldInteractionAllowed && GameSession.Current is not { Paused: true }) Open();
            if (Active)
            {
                int revision = (int)Read("revision");
                if (revision != _revision)
                {
                    _revision = revision;
                    ulong selected = Read("selected") == 1 ? ReadUid("uid") : 0;
                    int status = (int)Read("status");
                    uint card = Read("selected") == 1 ? (uint)ReadUid("card") : 0;
                    int previousStored = _storedId;
                    _storedId = _cancelPending ? 0 : (int)Read("stored");
                    bool newlySelected = ReconcileSelection(selected, status, card) || _storedId != 0 && previousStored != _storedId;
                    SetAim(-1);
                    if (HasSelection)
                    {
                        _room = true;
                        if (newlySelected)
                        {
                            _piece = new Vector3(-Read("x"), Read("z"), Read("y"));
                            Angles = Engine_DecorateControls.Angles(new Quaternion(Read("qx"), Read("qy"), Read("qz"), Read("qw")));
                            _rotationOrigin = _rotation; _tiltOrigin = _tilt;
                            _ghostSource = Engine_DecorateControls.Pose(_piece, Angles);
                            if (_adding && _camera is not null)
                            {
                                _piece = _camera.GlobalPosition - _camera.GlobalBasis.Z * 3;
                                _dirty = true;
                            }
                            _adding = false;
                            BeginGhost();
                            AnchorDrag();
                        }
                    }
                    else RefreshRows();
                    Refresh();
                    _ui?.Status(status switch
                    {
                        1 => "Select an object belonging to this home", 2 => "That item is no longer available",
                        3 => "Could not move this object. Try again.", _ => ""
                    });
                }
            }
        }
        if (!Active || _camera is null || _ui is null) return;
        if (!IsInstanceValid(_camera) || _session is null || !IsInstanceValid(_session)) { Close(); return; }
        _ghostPoll -= delta;
        _collisionPoll -= delta;
        _interiorPoll -= delta;
        if (_interiorPoll <= 0) UpdateInterior();
        if (HasSelection && (_ghost is not null || _ghostPoll <= 0)) UpdateGhost();
        _touch -= delta;
        if (_touch <= 0) { _touch = 1; Command("Touch"); }
        if (_waiting)
        {
            _wait -= delta;
            if (_wait <= 0) { _waiting = false; _awaitSelection = false; _ui.Status("The game did not confirm that action. Try again."); }
        }
        bool focused = GetWindow().HasFocus();
        int pad = GameInput.JoypadForPad(0);
        uint buttons = focused ? Buttons(pad) : 0;
        uint pressed = buttons & ~_held | TouchBack();
        _held = buttons;
        if (!focused) { SetAim(-1); _mouse = Vector2.Zero; _scroll = 0; _dragReady = false; Input.MouseMode = Input.MouseModeEnum.Visible; return; }
        if (_device != Engine_UiInputMode.Generation)
        {
            _device = Engine_UiInputMode.Generation;
            _ui.Help(_ui.HelpOpen);
            _aimPoll = 0;
            _dragReady = false;
            _mouse = Vector2.Zero;
            Refresh();
            Engine_UiKeyGlyphs.Update();
        }
        Resize();
        StateTouch();
        if (_editingRotation)
        {
            TouchRotationEdit();
            ProcessRotationInput(pressed);
            _mouse = Vector2.Zero; _scroll = 0;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            return;
        }
        Vector2 left = new(Axis(pad, JoyAxis.LeftX), Axis(pad, JoyAxis.LeftY));
        if ((pressed & 512) != 0) { Close(); return; }
        if ((pressed & 256) != 0) SetUiHidden(!_uiHidden);
        if ((pressed & 8) != 0) ToggleHelp();
        if ((pressed & 2) != 0) { Back(); return; }
        if (_ui.HelpOpen) { _mouse = Vector2.Zero; _scroll = 0; Input.MouseMode = Input.MouseModeEnum.Visible; return; }
        if ((pressed & 8192) != 0) { PutAway(); return; }
        if ((pressed & 4) != 0) ToggleInventory();
        if ((pressed & 4096) != 0) ToggleCollision();
        float seconds = Math.Clamp((float)delta, 0, 0.05f);
        _repeat -= delta;
        if (!HasSelection && !_room && !_uiHidden && ((pressed & 48) != 0 || (buttons & 48) != 0 && _repeat <= 0))
        {
            _row = Math.Clamp(_row + ((buttons & 16) != 0 ? -1 : 1), 0, Math.Max(0, _labels.Count - 1));
            _repeat = (pressed & 48) != 0 ? 0.3 : 0.1;
            Refresh();
        }
        ApplyTouch();
        bool fine = Held(Key.Ctrl);
        bool keyboard = Engine_UiInputMode.KeyboardMouse;
        bool worldControls = (_room || _uiHidden) && !_waiting;
        bool looking = keyboard && worldControls && Input.IsMouseButtonPressed(MouseButton.Right);
        var mouseMode = looking ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        if (Input.MouseMode != mouseMode) Input.MouseMode = mouseMode;
        Vector2 previousCursor = _cursor;
        UpdateCursor(keyboard, looking, keyboard && !looking ? TouchCursor(GetViewport().GetMousePosition()) : _size * 0.5f);
        Transform3D before = _camera.GlobalTransform;
        Transform3D after = before;
        if (worldControls)
        {
            var movement = keyboard ? new Vector3(Keys(Key.A, Key.D), Keys(Key.Q, Key.E), Keys(Key.W, Key.S))
                : new Vector3(left.X, Trigger(pad, JoyAxis.TriggerRight) - Trigger(pad, JoyAxis.TriggerLeft), left.Y);
            movement += TouchMove();
            Vector2 turn = keyboard ? Vector2.Zero : new Vector2(Axis(pad, JoyAxis.RightX), Axis(pad, JoyAxis.RightY));
            after = Engine_DecorateControls.Camera(ref _position, ref _yaw, ref _pitch,
                movement, turn, _mouse, seconds, fine, Held(Key.Shift) || Pad(pad, JoyButton.LeftStick));
            if (keyboard && _scroll != 0)
            {
                if (HasSelection)
                {
                    _pendingMove = false;
                    _piece.Y -= _scroll * (fine ? 0.05f : 0.25f);
                    _dirty = true; _dragReady = false;
                }
                else { _position += after.Basis.Z * _scroll; after.Origin = _position; }
            }
            if (!before.IsEqualApprox(after)) _camera.GlobalTransform = after;
        }
        _scroll = 0;
        _mouse = Vector2.Zero;
        bool cameraMoved = !before.IsEqualApprox(after);
        if (HasSelection && !_waiting)
        {
            if (!keyboard)
            {
                float depth = ((Pad(pad, JoyButton.DpadUp) ? 1 : 0) - (Pad(pad, JoyButton.DpadDown) ? 1 : 0)) * seconds * (fine ? 0.3f : 2);
                CarrySelection(before, after, depth);
            }
            else if (cameraMoved || looking || !_dragReady)
            {
                if (cameraMoved) CarrySelection(before, after, 0);
                AnchorDrag();
            }
            else if (_cursor != previousCursor) DragToCursor();
            float height = (keyboard ? Keys(Key.F, Key.R) : 0) + TouchHeight();
            Vector3 nudge = keyboard ? new Vector3(Keys(Key.Left, Key.Right), 0, Keys(Key.Up, Key.Down)) : Vector3.Zero;
            float turn = keyboard ? 0 : Engine_DecorateControls.Rotation(Pad(pad, JoyButton.LeftShoulder), Pad(pad, JoyButton.RightShoulder), seconds, fine);
            if ((pressed & 1024) != 0) turn -= Mathf.Pi / 4;
            if ((pressed & 2048) != 0) turn += Mathf.Pi / 4;
            turn += Keys(Key.Z, Key.C) * seconds * (fine ? 0.2f : 1.5f);
            turn += TouchTurn(seconds, fine);
            if (height != 0 || nudge != Vector3.Zero || turn != 0)
            {
                _pendingMove = false;
                _piece += (Basis.FromEuler(new Vector3(0, _yaw, 0)) * nudge + Vector3.Up * height) * seconds * (fine ? 0.15f : 1.2f);
                _rotation = Mathf.Wrap(_rotation + turn, -Mathf.Pi, Mathf.Pi);
                _dirty = true;
                AnchorDrag();
            }
            if (_dirty) UpdateGhost();
        }
        if (_collisionDirty && _collisionPoll <= 0) CheckCollision();
        _aimPoll -= delta;
        if (_aimPoll <= 0 || (pressed & 1) != 0)
        {
            _aimPoll = 0.067;
            UpdateAim(_cursor);
        }
        UpdateReticle();
        if ((pressed & 1) != 0) Choose();
    }

    private bool ReconcileSelection(ulong selected, int status, uint card)
    {
        bool chosen = selected != 0 || card != 0 || _storedId != 0;
        bool newlySelected = chosen && (selected != _selectedUid || card != _previewCard);
        _waiting = _cancelPending && (chosen || status == 0) || _awaitSelection && !chosen && status == 0;
        if (!_waiting) { _cancelPending = false; _awaitSelection = false; }
        SelectUid(_cancelPending ? 0 : selected);
        _previewCard = _cancelPending ? 0 : card;
        if (!HasSelection) ClearGhost();
        if (!_waiting && !HasSelection) _adding = false;
        return newlySelected;
    }

    private void CarrySelection(Transform3D before, Transform3D after, float depth)
    {
        if (!HasSelection || _waiting) return;
        Vector3 next = Engine_DecorateControls.Carry(before, after, _piece, depth);
        if (next.IsEqualApprox(_piece)) return;
        _pendingMove = false;
        _piece = next;
        _dirty = true;
        _dragReady = false;
    }

    private void UpdateCursor(bool keyboard, bool looking, Vector2 mouse)
    {
        if (_looking != looking) _dragReady = false;
        _looking = looking;
        _cursor = keyboard && !looking ? mouse : _size * 0.5f;
    }

    private bool CursorOnPlane(out Vector3 point)
    {
        point = default;
        if (_camera is null) return false;
        Vector3 origin = _camera.ProjectRayOrigin(_cursor), direction = _camera.ProjectRayNormal(_cursor);
        return Engine_DecorateControls.DragPoint(origin, direction, _dragPlanePoint, _dragPlaneNormal, out point);
    }

    private void AnchorDrag()
    {
        _dragPlanePoint = _piece;
        _dragPlaneNormal = _camera?.GlobalBasis.Z ?? Vector3.Up;
        _dragReady = CursorOnPlane(out Vector3 point);
        if (_dragReady) _dragOffset = _piece - point;
    }

    private void DragToCursor()
    {
        if (_waiting || !HasSelection || !_dragReady || OverPanel(_cursor) || !CursorOnPlane(out Vector3 point)) return;
        Vector3 next = point + _dragOffset;
        if (next.IsEqualApprox(_piece)) return;
        _pendingMove = false;
        _piece = next;
        _dirty = true;
    }

    private static Transform3D PreviewPose(Vector3 position, float yaw) => Engine_DecorateControls.Pose(position, yaw);

    private void BeginGhost()
    {
        ClearGhost();
        _ghostPoll = 0;
        _dirty = true;
        string? model = null;
        if (_previewCard != 0 && Engine_DecorateCatalog.Current?.Items.TryGetValue(_previewCard, out var item) == true) model = item.Model;
        else if (_storedId != 0 && Engine_DecorateCatalog.Current?.Models.TryGetValue((uint)ReadUid("model"), out var storedModel) == true) model = storedModel;
        if (model is not null)
        {
            string path = Refii.Models.Engine_BnkMeshLoader.PathPrefix + model;
            _ghostLoad = Task.Run(() =>
            {
                try { return ResourceLoader.Load<Mesh>(path, "Mesh"); }
                catch { return null; }
            });
        }
    }

    private void CreateGhost(Mesh mesh, Transform3D source, uint layers)
    {
        _ghostOffset = _ghostSource.AffineInverse() * source;
        _ghost = new MeshInstance3D
        {
            Name = "FurniturePreview", Mesh = mesh, Layers = layers,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(0.15f, 1, 0.3f, 0.4f),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            }
        };
        AddChild(_ghost);
        _ghost.GlobalTransform = source;
        _dirty = true;
    }

    private void UpdateGhost()
    {
        if (!HasSelection) return;
        if (_pendingMove)
        {
            _piece = _pendingPiece; Angles = _pendingAngles;
            _pendingMove = false; _dirty = true; _dragReady = false;
        }
        if (_ghost is null)
        {
            if (_ghostPoll > 0) return;
            _ghostPoll = 0.1;
            if (_previewCard != 0 || _storedId != 0)
            {
                if (_ghostLoad is not { IsCompleted: true } load) return;
                _ghostLoad = null;
                if (load.Result is not { } mesh || mesh.GetSurfaceCount() == 0)
                { _ui?.Status("This furniture preview could not be loaded"); return; }
                Transform3D offset = _storedId == 0 ? new Transform3D(new Basis(Vector3.Up, Mathf.Pi), Vector3.Zero) :
                    new Transform3D(new Basis(new Vector3(Read("offset1"), Read("offset2"), Read("offset3")),
                        new Vector3(Read("offset4"), Read("offset5"), Read("offset6")), new Vector3(Read("offset7"), Read("offset8"), Read("offset9"))),
                        new Vector3(Read("offset10"), Read("offset11"), Read("offset12")));
                CreateGhost(mesh, _ghostSource * offset, 1);
            }
            else
            {
                if (!RoomAddresses.TryGetValue(_selectedUid, out uint address) || GameEntities.Current is not { } entities ||
                    !entities.TryDecorationGeometry(address, out Mesh mesh, out Transform3D source, out uint layers)) return;
                CreateGhost(mesh, source, layers);
            }
        }
        if (!_dirty || _ghost is null) return;
        bool stopped = false;
        if (_collisionEnabled && _acceptedPreview)
        {
            float fraction = _collision.LimitMovement(_ghost, _ghostSource * _ghostOffset, _selectedUid != 0,
                _interior, _ghostOffset, _acceptedPiece, _acceptedAngles, _piece, Angles, false);
            if (fraction < 1)
            {
                stopped = _collision.MovementBlocked;
                Vector3 requested = _piece;
                if (!stopped) { _pendingPiece = _piece; _pendingAngles = Angles; _pendingMove = true; }
                _piece = _acceptedPiece.Lerp(_piece, fraction);
                Angles = Engine_DecorateControls.InterpolateAngles(_acceptedAngles, Angles, fraction);
                if (stopped) _piece = _collision.Slide(_ghost, _ghostSource * _ghostOffset, _selectedUid != 0,
                    _interior, _ghostOffset, _piece, Angles, requested, false);
                _dragReady = false;
            }
            _acceptedPiece = _piece; _acceptedAngles = Angles;
        }
        _ghost.GlobalTransform = Engine_DecorateControls.Pose(_piece, Angles) * _ghostOffset;
        _dirty = false;
        _collisionDirty = !_acceptedPreview;
        if (!_acceptedPreview) CheckCollision();
        else _ui?.Status(stopped ? "Movement stopped by collision" : "");
        RefreshRotation();
    }

    private void ToggleCollision()
    {
        _collisionEnabled = !_collisionEnabled;
        _pendingMove = false;
        _acceptedPreview = false;
        CheckCollision();
        _ui?.Collision(_collisionEnabled);
    }

    private void CheckCollision()
    {
        bool outside = _collisionEnabled && _ghost is { Mesh: { } mesh } && (!_interior.Contains(mesh.GetAabb(), _ghost.GlobalTransform) || _interior.CrossesSurface(mesh.GetAabb(), _ghost.GlobalTransform));
        _blocked = outside;
        _collisionDirty = false;
        _collisionPoll = 0.1;
        _acceptedPreview = !_blocked && _ghost is not null;
        if (_acceptedPreview) { _acceptedPiece = _piece; _acceptedAngles = Angles; }
        if (_ghost?.MaterialOverride is StandardMaterial3D material)
            material.AlbedoColor = _blocked ? new Color(1, 0.12f, 0.08f, 0.45f) : new Color(0.15f, 1, 0.3f, 0.4f);
        _ui?.Status(outside ? _interior.Ready ? "Keep the object inside this home" : "Waiting for this home's interior"
            : _blocked ? "Blocked by another object or surface" : "");
    }

    private void UpdateInterior()
    {
        _interiorPoll = 0.5;
        uint building = HostBridge.ModHandleAddress((uint)ReadUid("building"));
        if (building != _building)
        {
            _building = building; _interior.Clear(); _interiorMesh = null;
            _collisionDirty = true;
        }
        if (building == 0 || GameEntities.Current is not { } entities ||
            !entities.TryDecorationInterior(building, out Mesh mesh, out Transform3D transform))
        {
            if (_interior.Ready) { _interior.Clear(); _interiorMesh = null; _collisionDirty = true; }
            return;
        }
        if (_interiorMesh == mesh && _interiorPose.IsEqualApprox(transform)) return;
        _interior.Build(mesh.GetFaces(), transform);
        _interiorMesh = mesh; _interiorPose = transform; _collisionDirty = true;
    }

    private void ClearGhost()
    {
        if (_ghost is not null) { _ghost.Visible = false; _ghost.QueueFree(); }
        _ghost = null;
        _ghostLoad = null;
        _blocked = false; _collisionDirty = false;
        _acceptedPreview = false; _editingRotation = false;
        _pendingMove = false;
    }

    private void Choose()
    {
        if (_editingRotation) { EndRotationEdit(true); return; }
        if (_waiting || _ui?.HelpOpen == true) return;
        if (HasSelection)
        {
            if (_ghost is null) { _ui?.Status("Wait for the furniture preview before placing"); return; }
            _pendingMove = false;
            UpdateGhost();
            CheckCollision();
            if (_blocked) return;
            Quaternion q = Engine_DecorateControls.GuestRotation(Angles);
            Command("Commit", FormattableString.Invariant($"{-_piece.X:R},{_piece.Z:R},{_piece.Y:R},{_rotation:R},{q.X:R},{q.Y:R},{q.Z:R},{q.W:R}"));
        }
        else
        {
            if (_room || _uiHidden)
            {
                if (_aimIndex < 0) return;
                Command("Select", (_aimIndex + 1).ToString(CultureInfo.InvariantCulture));
            }
            else if (_row < 0 || _row >= _cards.Count) return;
            else
            {
                _adding = true;
                if (_stored[_row] != 0) Command("Take", _stored[_row].ToString(CultureInfo.InvariantCulture));
                else Command("Add", $"\"0x{_cards[_row]:x8}\"");
            }
            _awaitSelection = true;
        }
        SetAim(-1);
        _waiting = true; _wait = 3;
    }

    private void PutAway()
    {
        if (!HasSelection || _waiting || _ui?.HelpOpen == true) return;
        if (_previewCard != 0 || _storedId != 0) { Back(); return; }
        if (_ghost is null || !RoomModels.TryGetValue(_selectedUid, out uint model))
        { _ui?.Status("Wait for the object to load"); return; }
        Transform3D p = _ghostOffset;
        Command("Store", FormattableString.Invariant($"\"0x{model:x8}\",{p.Basis.X.X:R},{p.Basis.X.Y:R},{p.Basis.X.Z:R},{p.Basis.Y.X:R},{p.Basis.Y.Y:R},{p.Basis.Y.Z:R},{p.Basis.Z.X:R},{p.Basis.Z.Y:R},{p.Basis.Z.Z:R},{p.Origin.X:R},{p.Origin.Y:R},{p.Origin.Z:R}"));
        _pendingMove = false; _waiting = true; _wait = 3;
    }

    private void UpdateAim(Vector2 screen)
    {
        if (_camera is null || _waiting || HasSelection || _ui?.HelpOpen == true ||
            !Engine_UiInputMode.KeyboardMouse && !_room && !_uiHidden ||
            Engine_UiInputMode.KeyboardMouse && OverPanel(screen)) { SetAim(-1); return; }
        Vector3 origin = _camera.ProjectRayOrigin(screen), direction = _camera.ProjectRayNormal(screen);
        float nearest = Engine_DecoratePicking.Range;
        int target = -1;
        if (GameEntities.Current is { } entities)
        {
            for (int i = 0; i < _targets.Count; i++)
            {
                if (!RoomAddresses.TryGetValue(_targets[i].Uid, out uint address) ||
                    !entities.TryDecorationGeometry(address, out Mesh mesh, out Transform3D transform, out uint layers) ||
                    (layers & _camera.CullMask) == 0) continue;
                if (_picking.Hit(mesh, transform, origin, direction, nearest, out float distance))
                { nearest = distance; target = i; }
            }
        }
        SetAim(target);
    }

    private void SetAim(int index)
    {
        ulong uid = index >= 0 && index < _targets.Count ? _targets[index].Uid : 0;
        if (_aimUid == uid && _aimIndex == index) return;
        if (_aimUid != 0 && _aimUid != _selectedUid) RefreshHighlights.Add(_aimUid);
        _aimUid = uid; _aimIndex = uid == 0 ? -1 : index;
        if (_reticleMaterial is not null) _reticleMaterial.AlbedoColor = uid == 0 ? Colors.White : new Color(0.1f, 1, 0.15f);
        Refresh();
    }

    private void UpdateReticle()
    {
        if (_reticle is null) return;
        bool shown = !Engine_UiInputMode.KeyboardMouse && (_room || _uiHidden) && _ui?.HelpOpen != true;
        if (_reticle.Visible != shown) _reticle.Visible = shown;
        if (shown && _uiCamera is not null)
        {
            Vector3 point = _uiCamera.ProjectRayOrigin(_cursor);
            Vector3 position = new(point.X, point.Y, 20);
            if (_reticle.Position != position) _reticle.Position = position;
        }
        if (_reticleMaterial is not null)
        {
            Color color = !HasSelection && _aimUid == 0 ? Colors.White : new Color(0.1f, 1, 0.15f);
            if (_reticleMaterial.AlbedoColor != color) _reticleMaterial.AlbedoColor = color;
        }
    }

    private void SetUiHidden(bool hidden)
    {
        if (_editingRotation) EndRotationEdit(true);
        _uiHidden = hidden;
        if (_ui is not null) _ui.Visible = !hidden;
        if (hidden && _ui?.HelpOpen == true) { _ui.Help(false); Refresh(); }
        if (!hidden) Refresh();
        _aimPoll = 0;
        UpdateReticle();
    }

    private void ToggleHelp()
    {
        if (_ui is null) return;
        SetUiHidden(false);
        _ui.Help(!_ui.HelpOpen);
        SetAim(-1); Refresh(); UpdateReticle();
    }

    private void ToggleInventory()
    {
        if (HasSelection || _waiting) return;
        bool hidden = _uiHidden;
        SetUiHidden(false);
        _room = hidden ? false : !_room;
        SetAim(-1); RefreshRows(); UpdateReticle();
    }

    private bool OverPanel(Vector2 screen)
    {
        if (_uiHidden || _uiCamera is null || _ui is null) return false;
        Vector3 at = _uiCamera.ProjectRayOrigin(screen) - _ui.Position;
        return _ui.PanelBounds.HasPoint(new Vector2(at.X, at.Y));
    }

    private void Back()
    {
        if (_editingRotation) { EndRotationEdit(false); return; }
        if (_ui?.HelpOpen == true) { _ui.Help(false); Refresh(); UpdateReticle(); return; }
        if (HasSelection || _waiting)
        {
            Command("Cancel");
            _dirty = false; _dragReady = false; _cancelPending = true; _awaitSelection = false; _adding = false;
            SelectUid(0); _previewCard = 0; _storedId = 0; ClearGhost(); SetAim(-1); _room = true; _waiting = true; _wait = 3;
            Refresh(); UpdateReticle();
            return;
        }
        _room = true; SetAim(-1); RefreshRows(); UpdateReticle();
    }

    private void RefreshRows()
    {
        _labels.Clear(); _cards.Clear(); _stored.Clear();
        SetAim(-1);
        _targets.Clear();
        int count = Math.Clamp((int)Read("count"), 0, 1024);
        var previous = new Dictionary<ulong, uint>(RoomAddresses);
        RoomAddresses.Clear();
        for (int i = 1; i <= count; i++)
        {
            int kind = Math.Clamp((int)Read("room" + i + ".kind"), 0, Engine_DecorateCatalog.Types.Length - 1);
            ulong uid = ReadUid("room" + i + ".uid");
            _targets.Add((uid, Engine_DecorateCatalog.Types[kind]));
            if (uid != 0) RoomAddresses[uid] = previous.GetValueOrDefault(uid);
            if (_room) _labels.Add(Engine_DecorateCatalog.Types[kind]);
        }
        if (!_room)
        {
            foreach ((uint record, string label) in Engine_UiInventorySource.Carried(Engine_UiInventorySource.Category.Furniture))
                if (Engine_DecorateCatalog.Current?.Items.ContainsKey(record) == true)
                { _cards.Add(record); _stored.Add(0); _labels.Add(label); }
            int storedCount = Math.Clamp((int)Read("storedcount"), 0, 4096);
            for (int i = 1; i <= storedCount; i++)
            {
                int id = (int)Read("stored" + i + ".id");
                int kind = Math.Clamp((int)Read("stored" + i + ".kind"), 0, Engine_DecorateCatalog.Types.Length - 1);
                if (id <= 0) continue;
                _cards.Add(0); _stored.Add(id); _labels.Add(Engine_DecorateCatalog.Types[kind]);
            }
        }
        if (!_room) _row = Math.Clamp(_row, 0, Math.Max(0, _labels.Count - 1));
        Refresh();
    }
    private void Refresh()
    {
        _ui?.ShowRows(_labels, _row, _room, HasSelection,
            _aimIndex >= 0 && _aimIndex < _targets.Count ? _targets[_aimIndex].Label : "");
        RefreshRotation();
    }

    private string RotationText() => Engine_DecorateControls.RotationDegrees(Angles[_editAxis], OriginAngles[_editAxis]).ToString("0.0", CultureInfo.InvariantCulture);

    private void RefreshRotation()
    {
        if (_ui is null || !HasSelection) return;
        _ui.ShowRotation(new Vector3(Engine_DecorateControls.RotationDegrees(_tilt.X, _tiltOrigin.X),
            Engine_DecorateControls.RotationDegrees(_rotation, _rotationOrigin), Engine_DecorateControls.RotationDegrees(_tilt.Y, _tiltOrigin.Y)));
        if (_editingRotation) _ui.ShowRotation(_rotationEntry, true, _editAxis);
    }

    private void SetAngle(float value)
    {
        Vector3 angles = Angles; angles[_editAxis] = value; Angles = angles;
    }

    private void EditAxis(int axis)
    {
        if (_editingRotation) EndRotationEdit(true);
        if (_editingRotation) return;
        _editAxis = axis; BeginRotationEdit();
    }

    private void BeginRotationEdit()
    {
        if (!HasSelection || _waiting || _editingRotation) return;
        SetUiHidden(false);
        _rotationBeforeEdit = Angles[_editAxis];
        _pendingMove = false;
        _rotationEntry = RotationText();
        _editingRotation = true; _replaceRotationText = true;
        _mouse = Vector2.Zero; _scroll = 0;
        _ui?.Status("Enter degrees; Enter to accept, Esc to cancel");
        RefreshRotation();
    }

    private void EndRotationEdit(bool accept)
    {
        if (!_editingRotation) return;
        if (accept && !Engine_DecorateControls.TryRotation(_rotationEntry, OriginAngles[_editAxis], out _))
        { _ui?.Status("Enter a valid angle"); return; }
        _editingRotation = false;
        if (!accept) { _pendingMove = false; SetAngle(_rotationBeforeEdit); _dirty = true; UpdateGhost(); }
        _dragReady = false; _mouse = Vector2.Zero; _scroll = 0;
        _held = Buttons(GameInput.JoypadForPad(0));
        _ui?.Status(""); RefreshRotation();
    }

    private void AdjustRotation(float degrees)
    {
        if (!HasSelection || _waiting) return;
        _pendingMove = false;
        SetAngle(Mathf.Wrap(Angles[_editAxis] + Mathf.DegToRad(degrees), -Mathf.Pi, Mathf.Pi));
        _dirty = true; UpdateGhost();
        if (_editingRotation) { _rotationEntry = RotationText(); _replaceRotationText = true; RefreshRotation(); }
    }

    private void ProcessRotationInput(uint pressed)
    {
        if (!Engine_UiInputMode.KeyboardMouse)
        {
            EndRotationEdit(true);
            if (_editingRotation) EndRotationEdit(false);
            return;
        }
        if ((pressed & 2) != 0) { EndRotationEdit(false); return; }
        if ((pressed & 1) != 0) { EndRotationEdit(true); return; }
        if ((pressed & 512) != 0) { Close(); return; }
    }

    private void TypeRotation(InputEventKey key)
    {
        if (!key.Pressed) return;
        if (key.CtrlPressed && key.Keycode == Key.A) { _replaceRotationText = true; return; }
        if (key.Keycode is Key.Backspace or Key.Delete)
        {
            _rotationEntry = _replaceRotationText || key.Keycode == Key.Delete ? "" : _rotationEntry.Length > 0 ? _rotationEntry[..^1] : "";
            _replaceRotationText = false;
        }
        else if (key.Unicode is >= 48 and <= 57 or 45 or 46)
        {
            if (_replaceRotationText) _rotationEntry = "";
            _replaceRotationText = false;
            if (_rotationEntry.Length < 9) _rotationEntry += (char)key.Unicode;
        }
        else return;
        if (Engine_DecorateControls.TryRotation(_rotationEntry, OriginAngles[_editAxis], out float yaw))
        { _pendingMove = false; SetAngle(yaw); _dirty = true; UpdateGhost(); }
        RefreshRotation();
    }

    private void Open()
    {
        if (Active || GameSession.Current is { Paused: true } || GetViewport().GetCamera3D() is not { } camera) return;
        _session = (Node?)GameSession.Current ?? GetParent(); _camera = camera; _original = camera.GlobalTransform; _mouseMode = Input.MouseMode;
        var angles = _original.Basis.GetEuler(); _yaw = angles.Y; _pitch = angles.X;
        _position = _original.Origin;
        _held = uint.MaxValue; _revision = -1; _waiting = false; _room = true; _row = 0; _touch = 0; _uiHidden = false; _aimPoll = 0;
        _cancelPending = false; _awaitSelection = false; _dragReady = false; _scroll = 0; _adding = false; _looking = false;
        _interiorPoll = 0; _building = 0;
        Active = true;
        try
        {
            GameInput.ReleaseForPhotoMode();
            Hide(GetTree().Root);
            BuildUi();
            RefreshRows();
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        catch (Exception e) { Close(); GD.PushError("[decorate] " + e.Message); }
    }

    private void Hide(Node node)
    {
        if (node == this || node is Refii.Fx.Engine_FxDisplacement || node.IsInGroup(GamePhotoMode.KeepLayerGroup)) return;
        if (node is CanvasLayer { Visible: true } layer) { _hidden.Add(layer); layer.Visible = false; }
        foreach (Node child in node.GetChildren()) Hide(child);
    }

    private void BuildUi()
    {
        var theme = (GameSession.Current?.GetParent() ?? _session)?.GetNodeOrNull<GameUi>("GameUi")?.PhotoModeTheme
            ?? throw new InvalidOperationException("Fable 2's native UI is not ready.");
        _layer = new CanvasLayer { Name = "DecorateControls", Layer = 121 }; AddChild(_layer);
        _container = new SubViewportContainer { Stretch = true, MouseFilter = Control.MouseFilterEnum.Ignore }; _layer.AddChild(_container);
        var viewport = new SubViewport { OwnWorld3D = true, TransparentBg = true, GuiDisableInput = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always }; _container.AddChild(viewport);
        _uiCamera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal,
            Position = new Vector3(0, 0, 20000), Far = 40000, Current = true }; viewport.AddChild(_uiCamera);
        _ui = new Engine_UiDecorate(); viewport.AddChild(_ui); _ui.Build(theme);
        _ui.Collision(_collisionEnabled);
        _reticle = new Node3D { Name = "FurnitureCrosshair" }; viewport.AddChild(_reticle);
        _reticleMaterial = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = Colors.White, NoDepthTest = true, DisableFog = true };
        for (int i = 0; i < 4; i++)
        {
            bool horizontal = i < 2;
            float offset = i % 2 == 0 ? -8 : 8;
            _reticle.AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = horizontal ? new Vector2(7, 2) : new Vector2(2, 7) },
                Position = new Vector3(horizontal ? offset : 0, horizontal ? 0 : offset, 20), MaterialOverride = _reticleMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
        UpdateReticle();
        _size = Vector2.Zero; Resize(); Engine_UiKeyGlyphs.Update();
    }

    private void Resize()
    {
        Vector2 size = GetViewport().GetVisibleRect().Size;
        if (_size == size || _container is null || _uiCamera is null || _ui is null) return;
        _cursor = _size == Vector2.Zero ? size * 0.5f : _cursor * size / _size;
        _size = size; _container.Size = size;
        float aspect = size.X / Math.Max(1, size.Y);
        bool narrow = aspect < (Engine_UiDecorate.Width + 40) / 720;
        _uiCamera.KeepAspect = narrow ? Camera3D.KeepAspectEnum.Width : Camera3D.KeepAspectEnum.Height;
        _uiCamera.Size = narrow ? Engine_UiDecorate.Width + 40 : 720;
        _ui.Layout(narrow ? Engine_UiDecorate.Width + 40 : 720 * aspect);
    }

    private void Close(bool notify = true)
    {
        if (!Active) return;
        ClearGhost(); _previewCard = 0; _storedId = 0;
        if (notify && HostBridge.IsInGame) Command("End");
        if (_camera is not null && IsInstanceValid(_camera)) _camera.GlobalTransform = _original;
        if (_layer is not null) { _layer.Visible = false; _layer.QueueFree(); }
        foreach (var layer in _hidden) if (IsInstanceValid(layer)) layer.Visible = true;
        SetAim(-1); _targets.Clear(); RoomAddresses.Clear(); RoomModels.Clear(); _picking.Dispose(); _collision.Clear();
        _interior.Clear(); _interiorMesh = null; _building = 0;
        _hidden.Clear(); _labels.Clear(); _cards.Clear(); _stored.Clear();
        Input.MouseMode = _mouseMode;
        _layer = null; _ui = null; _camera = null; _container = null; _uiCamera = null; _session = null;
        _reticle = null; _reticleMaterial = null; _uiHidden = false;
        SelectUid(0); Active = false; _mouse = Vector2.Zero; _dirty = false; _dragReady = false; _cancelPending = false; _awaitSelection = false; _adding = false;
        GameInput.ReleaseForPhotoMode(); Engine_UiKeyGlyphs.Update();
    }
    public override void _ExitTree() => Close();

    public override void _Input(InputEvent input)
    {
        // The fingers are the touch controls' (GameDecorate.Touch.cs), and their mouse is not the desktop's.
        if (!Active || _ui is null || input is InputEventScreenTouch or InputEventScreenDrag) return;
        if (input.Device != (int)InputEvent.DeviceIdEmulation &&
            (input is InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true } || input is InputEventMouseMotion motion && motion.Relative.LengthSquared() > 4))
            Engine_UiInputMode.KeyboardMouse = true;
        else if (input is InputEventJoypadButton { Pressed: true } || input is InputEventJoypadMotion axis && Math.Abs(axis.AxisValue) > 0.5f)
            Engine_UiInputMode.KeyboardMouse = false;
        if (_editingRotation)
        {
            if (input is InputEventKey key) TypeRotation(key);
            else if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } edit && OverPanel(edit.Position)) ClickPanel(edit.Position);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (input is InputEventMouseMotion look && Input.MouseMode == Input.MouseModeEnum.Captured && !_ui.HelpOpen) _mouse += look.Relative;
        if (input is InputEventMouseButton { Pressed: true } wheel && !_ui.HelpOpen && wheel.Device != (int)InputEvent.DeviceIdEmulation &&
            wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            _scroll += wheel.ButtonIndex == MouseButton.WheelUp ? -0.6f : 0.6f;
        if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click && _uiCamera is not null)
        {
            _cursor = click.Position;
            if (!OverPanel(click.Position))
            {
                if (!_ui.HelpOpen)
                {
                    UpdateAim(click.Position);
                    if (HasSelection) { DragToCursor(); Choose(); }
                    else if (_aimIndex >= 0)
                    {
                        _room = true;
                        Choose();
                    }
                }
                GetViewport().SetInputAsHandled();
                return;
            }
            ClickPanel(click.Position);
        }
        GetViewport().SetInputAsHandled();
    }

    private void ClickPanel(Vector2 screen)
    {
        if (_uiCamera is null || _ui is null) return;
        Vector3 at = _uiCamera.ProjectRayOrigin(screen) - _ui.Position;
        int hit = _ui.Hit(new Vector2(at.X, at.Y));
        if (_editingRotation && hit != Engine_UiDecorate.PageSize && hit != Engine_UiDecorate.PageSize + 1 && hit < Engine_UiDecorate.PageSize + 9) return;
        if (hit >= 0 && hit < Engine_UiDecorate.PageSize && !HasSelection && !_room)
        { _row = Math.Min(_labels.Count - 1, _row / Engine_UiDecorate.PageSize * Engine_UiDecorate.PageSize + hit); if (_row >= 0) Choose(); _row = Math.Max(0, _row); }
        else if (hit == Engine_UiDecorate.PageSize) Choose();
        else if (hit == Engine_UiDecorate.PageSize + 1) Back();
        else if (hit == Engine_UiDecorate.PageSize + 2) ToggleInventory();
        else if (hit == Engine_UiDecorate.PageSize + 3) ToggleHelp();
        else if (hit == Engine_UiDecorate.PageSize + 6) SetUiHidden(!_uiHidden);
        else if (hit == Engine_UiDecorate.PageSize + 7) Close();
        else if (hit == Engine_UiDecorate.PageSize + 8) ToggleCollision();
        else if (hit == Engine_UiDecorate.PageSize + 9) PutAway();
        else if (hit >= Engine_UiDecorate.PageSize + 10 && hit <= Engine_UiDecorate.PageSize + 12) EditAxis(hit - Engine_UiDecorate.PageSize - 10);
        else if (hit >= Engine_UiDecorate.PageSize + 4 && hit <= Engine_UiDecorate.PageSize + 5 && !_room)
        { _row = Math.Clamp(_row + (hit == Engine_UiDecorate.PageSize + 4 ? -1 : 1) * Engine_UiDecorate.PageSize, 0, Math.Max(0, _labels.Count - 1)); Refresh(); }
    }
    private static bool Held(Key key) => Input.IsPhysicalKeyPressed(key);
    private static float Keys(Key minus, Key plus) => (Held(plus) ? 1 : 0) - (Held(minus) ? 1 : 0);
    private static bool Pad(int pad, JoyButton button) => pad >= 0 && Input.IsJoyButtonPressed(pad, button);
    private static float Trigger(int pad, JoyAxis axis) => pad >= 0 ? Math.Max(0, Input.GetJoyAxis(pad, axis)) : 0;
    private static float Axis(int pad, JoyAxis axis)
    {
        float value = pad >= 0 ? Input.GetJoyAxis(pad, axis) : 0;
        return Math.Abs(value) <= 0.18f ? 0 : Math.Sign(value) * (Math.Abs(value) - 0.18f) / 0.82f;
    }
    private static uint Buttons(int pad)
    {
        uint b = 0;
        if (Held(Key.Enter) || Pad(pad, JoyButton.A)) b |= 1;
        b |= TouchButtons();
        if (Held(Key.Escape) || Pad(pad, JoyButton.B)) b |= 2;
        if (Held(Key.Tab) || Pad(pad, JoyButton.X)) b |= 4;
        if (Held(Key.H) || Pad(pad, JoyButton.Back)) b |= 8;
        if (Held(Key.Up) || Pad(pad, JoyButton.DpadUp) || Axis(pad, JoyAxis.LeftY) < -0.5f) b |= 16;
        if (Held(Key.Down) || Pad(pad, JoyButton.DpadDown) || Axis(pad, JoyAxis.LeftY) > 0.5f) b |= 32;
        if (Held(Key.Left) || Pad(pad, JoyButton.DpadLeft)) b |= 64;
        if (Held(Key.Right) || Pad(pad, JoyButton.DpadRight)) b |= 128;
        if (Held(Key.U) || Pad(pad, JoyButton.RightStick)) b |= 256;
        if (Held(Key.F10) || Pad(pad, JoyButton.Start)) b |= 512;
        if (Held(Key.Comma)) b |= 1024;
        if (Held(Key.Period)) b |= 2048;
        if (Held(Key.K) || Pad(pad, JoyButton.DpadLeft)) b |= 4096;
        if (Held(Key.Delete) || Pad(pad, JoyButton.Y)) b |= 8192;
        return b;
    }
}
