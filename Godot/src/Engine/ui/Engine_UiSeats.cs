using Godot;

namespace Refii.Ui;

/// <summary>
/// Which part of the window a piece of UI belongs to: the whole of it, or one local player's half
/// on split screen — and the transform that puts a layer authored for a whole screen into a half.
/// </summary>
/// <remarks>
/// <para>A HALF IS A SCREEN OF ITS OWN. Every UI layer under the presentation is placed in one
/// canvas, centre-origin and +Y up, fitted to the window by the canvas camera
/// (<see cref="Engine_UiSceneBuilder.FitCanvasCamera"/>). A seat lays a layer out exactly as it would
/// lay it out on a window the size of the half — same anchors, same <see cref="Engine_HudLayout.CanvasExtent"/>
/// rule, same proportions — and <see cref="Frame.Transform"/> then maps that seat canvas onto the
/// half's rectangle of the real canvas: one uniform scale, one offset. A layer under that
/// transform needs no knowledge of the split at all.</para>
/// <para>The halves match <c>Refii.Game.GameSplitView</c>, as it states them
/// (<see cref="Refii.Core.Engine_Views.Stacked"/>): side by side, player one LEFT and player two
/// RIGHT, each half the window's width and all of its height; stacked, player one TOP and player
/// two BOTTOM, each all of the window's width and half its height.</para>
/// </remarks>
public static class Engine_UiSeats
{
    public enum Seat
    {
        /// <summary>The whole window — solo, and local co-op on a shared screen.</summary>
        Whole,

        /// <summary>Player one's half on split screen: the left, or the top when stacked.</summary>
        PlayerOne,

        /// <summary>Player two's half on split screen: the right, or the bottom when stacked.</summary>
        PlayerTwo,
    }

    /// <summary>
    /// The group of the node that holds the menus (<c>Engine_UiPresentationHost.MenuLayer</c>).
    /// Nothing seats it today — menus fill the window on split screen too — so every transform
    /// below is the identity; they keep a clip measured in laid-out space if a layer is ever seated.
    /// </summary>
    public const string MenuLayerGroup = "refii_ui_menu_layer";

    /// <summary>
    /// A node's transform in the laid-out UI space: its global transform with any menu seat
    /// taken back out. Clip rects are measured in this space (the UI shaders un-seat the
    /// fragment the same way), so a list opened while its menu is seated clips where it is laid
    /// out, not where the seat moved it.
    /// </summary>
    public static Transform3D LayoutTransform(Node3D node)
    {
        Transform3D world = node.GlobalTransform;
        for (Node? up = node.GetParent(); up is not null; up = up.GetParent())
        {
            if (up is Node3D layer && layer.IsInGroup(MenuLayerGroup))
            {
                // world = H * L * local, with H the layer's parent: laid out = H * local.
                Transform3D parent = layer.GetParent() is Node3D above ? above.GlobalTransform : Transform3D.Identity;
                return parent * layer.Transform.AffineInverse() * parent.AffineInverse() * world;
            }
        }

        return world;
    }

    /// <summary>
    /// The seat a node's menu stands in, as the map from laid-out space to the window: identity
    /// unless the node is under a seated menu layer.
    /// </summary>
    public static Transform3D SeatOf(Node3D node)
    {
        for (Node? up = node.GetParent(); up is not null; up = up.GetParent())
        {
            if (up is Node3D layer && layer.IsInGroup(MenuLayerGroup))
            {
                Transform3D parent = layer.GetParent() is Node3D above ? above.GlobalTransform : Transform3D.Identity;
                return parent * layer.Transform * parent.AffineInverse();
            }
        }

        return Transform3D.Identity;
    }

    /// <summary>A window point (the pointer) in the laid-out space of <paramref name="node"/>'s menu.</summary>
    public static Vector2 PointToLayout(Node3D node, Vector2 point)
    {
        Vector3 laid = SeatOf(node).AffineInverse() * new Vector3(point.X, point.Y, 0f);
        return new Vector2(laid.X, laid.Y);
    }

    /// <summary>A laid-out rect of <paramref name="node"/>'s menu (a clip) where it stands on the window.</summary>
    public static Rect2 RectToWindow(Node3D node, Rect2 rect)
    {
        Transform3D seat = SeatOf(node);
        Vector3 a = seat * new Vector3(rect.Position.X, rect.Position.Y, 0f);
        Vector3 b = seat * new Vector3(rect.End.X, rect.End.Y, 0f);
        return new Rect2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y), Mathf.Abs(b.X - a.X), Mathf.Abs(b.Y - a.Y));
    }

    /// <summary>The seat a player's own UI takes: their half while split, else the whole window.</summary>
    public static Seat For(int player, bool split) =>
        !split ? Seat.Whole : player == 2 ? Seat.PlayerTwo : Seat.PlayerOne;

    /// <summary>One seat's place on the window and on the canvas.</summary>
    /// <param name="Window">The seat's size in window pixels — what its anchors resolve against.</param>
    /// <param name="Origin">The seat's top-left corner in window pixels.</param>
    /// <param name="Centre">The seat's centre on the real canvas, in canvas units.</param>
    /// <param name="Scale">Real canvas units per seat canvas unit.</param>
    public readonly record struct Frame(Seat Seat, Vector2 Window, Vector2 Origin, Vector2 Centre, float Scale)
    {
        public bool IsWhole => Seat == Seat.Whole;

        /// <summary>The seat's visible canvas, in its own units — <see cref="Engine_HudLayout.CanvasExtent"/>
        /// of its window.</summary>
        public Vector2 Extent => Engine_HudLayout.CanvasExtent(Window);

        /// <summary>Seat canvas units per seat pixel.</summary>
        public float UnitsPerPixel => Window.Y > 0f ? Extent.Y / Window.Y : 1f;

        /// <summary>A point on the seat's own pixels (top-left origin, +Y down) on the seat's canvas.</summary>
        public Vector2 FromPixel(Vector2 pixel) => new(
            (pixel.X - (Window.X * 0.5f)) * UnitsPerPixel,
            ((Window.Y * 0.5f) - pixel.Y) * UnitsPerPixel);

        /// <summary>A seat canvas point on the real canvas.</summary>
        public Vector2 ToCanvas(Vector2 seat) => Centre + (seat * Scale);

        /// <summary>A real canvas point on this seat's canvas.</summary>
        public Vector2 FromCanvas(Vector2 canvas) =>
            Scale > 0f ? (canvas - Centre) / Scale : canvas - Centre;

        /// <summary>The transform a layer node takes so that its seat canvas lands on the half, at
        /// depth <paramref name="z"/>.</summary>
        public Transform3D Transform(float z) => new(
            Basis.Identity.Scaled(new Vector3(Scale, Scale, 1f)),
            new Vector3(Centre.X, Centre.Y, z));

        /// <summary>
        /// Seat canvas units per pixel of the guest's backbuffer, for the sprites the guest states
        /// in its own pixels (reticule petals, the slow-time dial). The whole window keeps the
        /// visible-extent rule those layers have always used; a half takes the DESIGN height, so
        /// its sprites shrink with the half exactly as the HUD pieces do (one design unit per seat
        /// unit, <see cref="Engine_HudLayout.Scale"/>).
        /// </summary>
        public float UnitsPerGuestPixel(float guestHeight)
        {
            if (guestHeight <= 0f)
            {
                return 1f;
            }

            return IsWhole
                ? Extent.Y / guestHeight
                : Engine_HudLayout.ReferenceHeight / Engine_UiSceneBuilder.PixelsPerUnit / guestHeight;
        }
    }

    /// <summary>
    /// Where <paramref name="seat"/> lies for a window of <paramref name="window"/> pixels. The
    /// whole window is the identity frame: its layers draw exactly where they always have.
    /// </summary>
    public static Frame Of(Vector2 window, Seat seat)
    {
        if (seat == Seat.Whole || window.X <= 0f || window.Y <= 0f)
        {
            return new Frame(Seat.Whole, window, Vector2.Zero, Vector2.Zero, 1f);
        }

        bool stacked = Refii.Core.Engine_Views.Stacked;
        Vector2 size = stacked ? new Vector2(window.X, window.Y * 0.5f) : new Vector2(window.X * 0.5f, window.Y);
        Vector2 origin = seat != Seat.PlayerTwo ? Vector2.Zero
            : stacked ? new Vector2(0f, window.Y * 0.5f)
            : new Vector2(window.X * 0.5f, 0f);

        // The canvas camera shows CanvasExtent(window) across the window at one scale on both axes,
        // so a window pixel is the same number of canvas units either way.
        Vector2 full = Engine_HudLayout.CanvasExtent(window);
        float unitsPerPixel = full.Y / window.Y;
        Vector2 middle = origin + (size * 0.5f);
        var centre = new Vector2(
            (middle.X - (window.X * 0.5f)) * unitsPerPixel,
            ((window.Y * 0.5f) - middle.Y) * unitsPerPixel);

        Vector2 own = Engine_HudLayout.CanvasExtent(size);
        float scale = own.Y > 0f ? size.Y * unitsPerPixel / own.Y : 1f;
        return new Frame(seat, size, origin, centre, scale);
    }
}
