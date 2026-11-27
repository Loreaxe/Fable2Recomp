namespace Refii.Tests.Titles;

using System;
using System.IO;
using GdUnit4;
using Godot;
using Refii.Ui;
using static GdUnit4.Assertions;

/// <summary>
/// The world sprites on a split couch screen (Engine_HudSpriteView, one mechanism for both titles): which half
/// a stated owner keeps a sprite to, a half's anchor landing where its seated layer draws it, the whole window
/// mapping exactly as Fable 3's own projection did, and the owner uid's word order. Pure arithmetic on Godot's
/// value types and source contracts; no Godot object is built.
/// </summary>
[TestSuite]
public class Fable3SplitSpritesTests
{
	private const ulong One = 0x000001D800000000ul;
	private const ulong Two = 0x000030A700000000ul;

	[TestCase]
	public void AStatedOwnerKeepsItsSpriteToItsHalf()
	{
		// Player one's choice: player one's half only. Player two's: player two's only.
		AssertThat(Engine_HudSpriteView.DrawnIn(0, One, One, Two)).IsTrue();
		AssertThat(Engine_HudSpriteView.DrawnIn(1, One, One, Two)).IsFalse();
		AssertThat(Engine_HudSpriteView.DrawnIn(0, Two, One, Two)).IsFalse();
		AssertThat(Engine_HudSpriteView.DrawnIn(1, Two, One, Two)).IsTrue();
		// No owner (a null-owner choice, a pooled prompt, any icon): every half that sees it.
		AssertThat(Engine_HudSpriteView.DrawnIn(0, 0ul, One, Two)).IsTrue();
		AssertThat(Engine_HudSpriteView.DrawnIn(1, 0ul, One, Two)).IsTrue();
		// An owner neither local hero is: both halves, as the shared screen showed it.
		AssertThat(Engine_HudSpriteView.DrawnIn(0, 0x1234ul, One, Two)).IsTrue();
		AssertThat(Engine_HudSpriteView.DrawnIn(1, 0x1234ul, One, Two)).IsTrue();
		// No local heroes read (no local co-op): never kept from a half.
		AssertThat(Engine_HudSpriteView.DrawnIn(1, One, 0ul, 0ul)).IsTrue();
	}

	/// <summary>
	/// One screen keeps Fable 3's own canvas mapping bit for bit: `(p - w/2) * (720 / PixelsPerUnit / h)`, y up,
	/// at every window of 4:3 or wider; the seat transform of the whole window is the identity.
	/// </summary>
	[TestCase]
	public void TheWholeWindowMapsExactlyAsBefore()
	{
		foreach (Vector2 window in new[] { new Vector2(2560f, 1440f), new Vector2(1920f, 1080f), new Vector2(3440f, 1440f),
			new Vector2(1280f, 800f), new Vector2(1024f, 768f), new Vector2(1281f, 721f) })
		{
			var view = new Engine_HudSpriteView
			{
				Viewport = window,
				Frame = Engine_UiSeats.Of(window, Engine_UiSeats.Seat.Whole),
			};
			float unitsPerPixel = Engine_UiSceneBuilder.CanvasHeight / Engine_UiSceneBuilder.PixelsPerUnit / window.Y;
			foreach (Vector2 pixel in new[] { Vector2.Zero, window * 0.5f, new Vector2(123.456f, 987.654f), window })
			{
				Vector2 seat = view.Frame.FromPixel(view.SeatPixel(pixel));
				AssertThat(seat.X).IsEqual((pixel.X - (window.X * 0.5f)) * unitsPerPixel);
				AssertThat(seat.Y).IsEqual(((window.Y * 0.5f) - pixel.Y) * unitsPerPixel);
				Vector2 onWindow = view.WindowPixel(pixel);
				AssertThat(onWindow.X).IsEqual(pixel.X);
				AssertThat(onWindow.Y).IsEqual(pixel.Y);
			}

			AssertThat(view.Frame.Scale).IsEqual(1f);
			AssertThat(view.Frame.Transform(0f) == Transform3D.Identity).IsTrue();
		}
	}

	/// <summary>
	/// A half's anchor, placed on the half's own canvas and carried by the layer's seat transform, lands on the
	/// window at the half's origin plus the camera pixel scaled onto the half — where the rings and markers put it —
	/// and a half's canvas unit is `Scale` window canvas units.
	/// </summary>
	[TestCase]
	public void AHalfsAnchorLandsWhereItsLayerDrawsIt()
	{
		var window = new Vector2(2560f, 1440f);
		float windowUnitsPerPixel = Engine_HudLayout.CanvasExtent(window).Y / window.Y;
		foreach (bool stacked in new[] { false, true })
		{
			Refii.Core.Engine_Views.SetStacked(stacked);
			try
			{
				foreach (Engine_UiSeats.Seat seat in new[] { Engine_UiSeats.Seat.PlayerOne, Engine_UiSeats.Seat.PlayerTwo })
				{
					Engine_UiSeats.Frame frame = Engine_UiSeats.Of(window, seat);
					// The view rendered at half the half's size: the pixel is scaled onto the half.
					var view = new Engine_HudSpriteView { Viewport = frame.Window * 0.5f, Frame = frame };
					var pixel = new Vector2(300f, 200f);
					Vector2 canvas = frame.ToCanvas(frame.FromPixel(view.SeatPixel(pixel)));
					var drawn = new Vector2(
						(canvas.X / windowUnitsPerPixel) + (window.X * 0.5f),
						(window.Y * 0.5f) - (canvas.Y / windowUnitsPerPixel));
					Vector2 expected = frame.Origin + (pixel * 2f);
					AssertThat(view.WindowPixel(pixel).X).IsEqualApprox(expected.X, 0.001f);
					AssertThat(view.WindowPixel(pixel).Y).IsEqualApprox(expected.Y, 0.001f);
					AssertThat(drawn.X).IsEqualApprox(expected.X, 0.01f);
					AssertThat(drawn.Y).IsEqualApprox(expected.Y, 0.01f);
					// Window pixels per seat unit: the ring's unit (window pixels per window unit times Scale).
					float ringUnit = window.Y * Engine_UiSceneBuilder.PixelsPerUnit / Engine_UiSceneBuilder.CanvasHeight * frame.Scale;
					AssertThat(ringUnit).IsEqualApprox(1f / frame.UnitsPerPixel, 0.0001f);
					// Inside the half's view only.
					AssertThat(view.Shows(pixel)).IsTrue();
					AssertThat(view.Shows(view.Viewport + Vector2.One)).IsFalse();
				}
			}
			finally
			{
				Refii.Core.Engine_Views.SetStacked(false);
			}
		}
	}

	[TestCase]
	public void BothTitlesSeatTheirSpritesThroughTheOneView()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null && !File.Exists(Path.Combine(at, "Godot", "src", "Engine", "ui", "Engine_HudSpriteView.cs")); i++)
		{
			at = Path.GetDirectoryName(at);
		}

		if (at is null)
		{
			return;
		}

		string fable2 = File.ReadAllText(Path.Combine(at, "Godot", "src", "Titles", "Fable2", "game", "GameUi.cs"));
		AssertThat(fable2.Contains("class SpriteView", StringComparison.Ordinal)).IsFalse();
		AssertThat(fable2.Contains("Engine_HudSpriteView.Aim(mine, theirs, windowCamera, window, testSight: true);", StringComparison.Ordinal)).IsTrue();

		string fable3 = File.ReadAllText(Path.Combine(at, "Godot", "src", "Titles", "Fable3", "Fable3WorldIcons.cs"));
		foreach (string line in new[]
		{
			"bool split = Engine_HudSpriteView.Aim(_views[0], _views[1], camera, window, testSight: false);",
			"Place = (guest, _) => PlaceIn(_views[0], guest),",
			"Place = (guest, _) => PlaceIn(_views[1], guest),",
			"_spritesTwo.Feed(_listTwo);",
			// The owner uid is rec+8 high, rec+12 low: the guest compares rec+8 with the hero's +0x18.
			"((ulong)_choiceWords[w + 1] << 32) | _choiceWords[w + 2]",
		})
		{
			AssertThat(fable3.Contains(line, StringComparison.Ordinal)).OverrideFailureMessage(line).IsTrue();
		}

		AssertThat(fable3.Contains("_projector", StringComparison.Ordinal)).IsFalse();
	}
}
