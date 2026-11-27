using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// What one title's `hud.bgf` is to the shared layout (<see cref="Engine_HudLayout"/>) and view
/// (<see cref="Engine_HudView"/>): the widgets cut whole for a caller that states none, the shipped
/// placements over the derived ones, whether the widgets drawn by their own readers stand as
/// pieces, the view's default layout, and the seat rules — whose each piece is on a shared screen.
/// A title states its own through <see cref="Refii.Core.Engine_TitleGame.HudLayout"/>.
/// </summary>
/// <remarks>
/// This base states nothing: no groups, no placements, no widgets, and every piece is the
/// screen's (<see cref="Engine_UiSeats.Seat.Whole"/>) with no player-two source.
/// </remarks>
public class Engine_HudTitleLayout
{
    /// <summary>A title that states nothing about its HUD.</summary>
    public static readonly Engine_HudTitleLayout None = new();

    private static readonly Dictionary<string, (Engine_HudLayout.Anchor Anchor, Vector2 Offset)> NoArrangement = new();

    /// <summary>The widgets cut whole when a caller of <see cref="Engine_HudLayout.FromBindings"/> states none.</summary>
    public virtual IReadOnlyList<Engine_HudLayout.GroupSubtree> Groups => Array.Empty<Engine_HudLayout.GroupSubtree>();

    /// <summary>Placements, by piece id, over the derived ones of a layout cut from <see cref="Groups"/>.</summary>
    public virtual IReadOnlyDictionary<string, (Engine_HudLayout.Anchor Anchor, Vector2 Offset)> Arrangement => NoArrangement;

    /// <summary>Whether a layout cut from <see cref="Groups"/> adds the own-reader widgets (<see cref="Engine_HudLayout.AddWidgets"/>).</summary>
    public virtual bool AddsWidgets => false;

    /// <summary>The layout a view holds before it is given one.</summary>
    public virtual Engine_HudLayout Shipped() => new();

    /// <summary>Whose piece this is on a screen both players share.</summary>
    public virtual Engine_UiSeats.Seat OwnerOf(Engine_HudLayout.Piece piece) => Engine_UiSeats.Seat.Whole;

    /// <summary>A screen's piece each half draws from its own player's values on split screen.</summary>
    public virtual bool EachPlayerOnSplit(Engine_HudLayout.Piece piece) => false;

    /// <summary>A piece of the d-pad.</summary>
    public virtual bool IsDpad(Engine_HudLayout.Piece piece) => false;

    /// <summary>The expression wheel, which stands in its owner's half on split screen.</summary>
    public virtual bool IsExpressionWheel(Engine_HudLayout.Piece piece) => false;

    /// <summary>A screen's piece both halves show from the one shared feed on split screen.</summary>
    public virtual bool SharedOnSplit(Engine_HudLayout.Piece piece) => false;

    /// <summary>The timing meter, which belongs to whichever player armed it.</summary>
    public virtual bool IsExpressionMeter(Engine_HudLayout.Piece piece) => false;

    /// <summary>
    /// Where a piece stands in a view seated in one player's half (<see cref="Engine_HudView.Seat"/> not
    /// Whole): the piece as it is, unless the title places it otherwise in a half. The whole window always
    /// places the piece as it is.
    /// </summary>
    public virtual Engine_HudLayout.Piece SeatedPiece(Engine_HudLayout.Piece piece) => piece;

    /// <summary>The id of the piece stating player two's version of player one's piece <paramref name="id"/>, or null.</summary>
    public virtual string? PlayerTwoSourceId(string id) => null;

    /// <summary>The id of player one's piece that player two's source <paramref name="id"/> keys, or null.</summary>
    public virtual string? PlayerOneIdOf(string id) => null;

    /// <summary>
    /// Whether a view seated in one player's half draws nothing outside that half
    /// (<see cref="Engine_HudView"/>, the UI shaders' `seat_rect`): the half is that player's screen,
    /// and art the file authors past a 1280-wide screen's edge (a slid-off alert, an edge highlight)
    /// stays off it instead of landing in the other player's half.
    /// </summary>
    public virtual bool KeepsToSeat => false;
}
