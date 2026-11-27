namespace Refii.Titles.Fable3;

using Godot;
using Refii.Ui;

/// <summary>
/// Fable 3's achievements: the shared system driven from this title's UI, and the shared toast
/// standing on a canvas of its own. <see cref="Refii.Achievements.Engine_Achievements.Drive"/>
/// parses the title's XDBF (<see cref="Fable3UiSource.GetTitleXdbf"/>), ingests the guest's
/// `XUserWriteAchievements` writes (<see cref="Fable3UiSource.GetAchievementUnlocks"/>), persists
/// them to the per-title store and states the unlocked set back to the guest's enumerator — all
/// as Fable 2's GameUi does, so unlocks pop and persist the same way.
/// </summary>
/// <remarks>
/// THE TOAST WORKS IN DISPLAY PIXELS and the canvas in 720-high units: the mount under it scales
/// pixels to units every frame and the toast is handed the window as its canvas, exactly the
/// arrangement of Fable 2's BuildAchievementToast. Z 9000: over everything else on this canvas.
/// </remarks>
public partial class Fable3Achievements : Fable3Canvas
{
	private Engine_AchievementToast _toast = null!;
	private readonly Engine_UiViewportIdle _idle = new();
	private Node3D _mount = null!;

	protected override void Build()
	{
		_toast = new Engine_AchievementToast { Name = "AchievementToast", Theme = Theme };
		_mount = new Node3D { Name = "ToastMount", Position = new Vector3(0f, 0f, 9000f) };
		_mount.AddChild(_toast);
		Root.AddChild(_mount);
	}

	protected override void ThemeChanged() => _toast.Theme = Theme;

	protected override void Frame(double delta)
	{
		Refii.Achievements.Engine_Achievements.Drive();
		int requests = Refii.Achievements.Engine_Achievements.TakeUiRequests();
		if (requests > 0)
		{
			// The guest's `XamShowAchievementsUI` opens the console's guide blade, which on this
			// platform opens nothing; logged so a request is seen, as Fable 2's is.
			GD.Print($"[achievements] the guest asked for the achievements UI ({requests}x)");
		}

		Vector2 window = GetViewportRect().Size;
		if (window.Y > 0f)
		{
			float unitsPerPixel = UnitsPerPixel;
			_mount.Scale = new Vector3(unitsPerPixel, unitsPerPixel, 1f);
			_toast.Canvas = window;
		}

		// Idle the full-window 3D toast SubViewport whenever no achievement toast is visible — after
		// one frame without it: the toast's last visible frame still has some of its fade left.
		Engine_UiSceneBuilder.SetViewportActive(CanvasViewport, _idle.Renders(Refii.Game.GameWorld.CoverHeld, _toast.Visible));
	}
}
