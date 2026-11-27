namespace Refii.Titles.Fable3;

using Godot;
using Refii.Ui;
using static Refii.Ui.Engine_HudLayout;

/// <summary>
/// Fable 3's `hud.bgf` seat rules for the shared HUD view (<see cref="Engine_HudTitleLayout"/>): whose each
/// widget is, so a split couch screen draws each player's HUD in their own half (<see cref="Engine_HudView.Seat"/>).
/// Fable 3 authors both couch players' widgets in the one file — `dpad` / `dpad2`, `MoneyAlert` /
/// `MoneyAlertSecondPlayer`, `FollowerCollection` / `FollowerCollection2` (the cut groups,
/// <see cref="Fable3Hud.Groups"/>) — so player two's half draws player one's widget keyed to player two's copy
/// (<see cref="PlayerTwoSourceId"/>), at player one's place in that half, as Fable 2's player-two view does.
/// The groups, placements and widgets stay the title's own (<see cref="Fable3Hud"/> states its groups), so
/// nothing here changes a whole-window HUD: every piece is admitted on the whole window.
/// </summary>
public sealed class Fable3HudLayout : Engine_HudTitleLayout
{
    /// <summary>The one instance <see cref="Fable3Title.HudLayout"/> answers.</summary>
    public static readonly Fable3HudLayout Instance = new();

    private Fable3HudLayout()
    {
    }

    /// <summary>Player one's widget and player two's copy of it, by piece id (`group.{label}.{name}`).</summary>
    public static readonly (string One, string Two)[] Pairs =
    {
        ("group.Dpad.dpad", "group.Dpad2.dpad2"),
        ("group.MoneyAlert.MoneyAlert", "group.MoneyAlertSecondPlayer.MoneyAlertSecondPlayer"),
        ("group.FollowerCollection.FollowerCollection", "group.FollowerCollection2.FollowerCollection2"),
    };

    /// <summary>
    /// The widgets of the one shared world each half shows from the same feed: the quest / info box, the
    /// subtitles, the counters and timers and the objective list.
    /// </summary>
    public static readonly string[] Shared =
    {
        "group.QuestBox.TopBox", "group.Subtitles.Subtitles", "group.Counters.CornerBox",
        "group.ObjectiveList.ObjectiveList",
    };

    public override Engine_UiSeats.Seat OwnerOf(Piece piece)
    {
        foreach ((string one, string two) in Pairs)
        {
            if (piece.Id == two)
            {
                return Engine_UiSeats.Seat.PlayerTwo;
            }

            if (piece.Id == one)
            {
                return Engine_UiSeats.Seat.PlayerOne;
            }
        }

        return Engine_UiSeats.Seat.Whole;
    }

    public override bool SharedOnSplit(Piece piece) => System.Array.IndexOf(Shared, piece.Id) >= 0;

    /// <summary>
    /// Fable 3 authors its HUD for one shared 1280-wide screen: art past that screen's edge is off it
    /// (the quest box's `TopBox_Style4_Highlight` at -476 under `TopBox.Group`, 512 wide; the money
    /// alerts' `MainPlayer` slid to -800 at rest). In a 950-unit half that art crosses the divider, so
    /// each seated view is clipped to its half.
    /// </summary>
    public override bool KeepsToSeat => true;

    public override string? PlayerTwoSourceId(string id)
    {
        foreach ((string one, string two) in Pairs)
        {
            if (id == one)
            {
                return two;
            }
        }

        return null;
    }

    public override string? PlayerOneIdOf(string id)
    {
        foreach ((string one, string two) in Pairs)
        {
            if (id == two)
            {
                return one;
            }
        }

        return null;
    }

    /// <summary>Player one's and player two's money alert, by piece id.</summary>
    public const string MoneyOne = "group.MoneyAlert.MoneyAlert", MoneyTwo = "group.MoneyAlertSecondPlayer.MoneyAlertSecondPlayer";

    /// <summary>
    /// The money alerts in a split half. `hud.bgf` authors each component near the centre (MoneyAlert
    /// widescreenXPos 125, MoneyAlertSecondPlayer -210) and its `MoneyAlert` / `MoneyAlertSecondPlayer` meter slides
    /// the art beneath (`MainPlayer` at -800 at rest) out to the edge of a 16:9 screen; cut from its contents the
    /// group is anchored on the centre, and on the whole window it stands as cut. In a half (950 units) that
    /// centre-anchored place lies 158 units past the half's own edge, so a seated view hangs player one's alert
    /// off the half's left edge and player two's off its right, each at the distance it has from that edge on a
    /// 16:9 window. A piece the player has moved (not centre-anchored) is left as moved.
    /// </summary>
    public override Piece SeatedPiece(Piece piece)
    {
        float side = piece.Id == MoneyOne ? -1f : piece.Id == MoneyTwo ? 1f : 0f;
        if (side == 0f || piece.Anchor.X != 0.5f)
        {
            return piece;
        }

        float half = CanvasExtent(new Vector2(16f, 9f)).X * 0.5f;
        return piece with
        {
            Anchor = new Engine_HudLayout.Anchor(side < 0f ? 0f : 1f, piece.Anchor.Y),
            Offset = new Vector2(piece.Offset.X - (side * half), piece.Offset.Y),
        };
    }
}
