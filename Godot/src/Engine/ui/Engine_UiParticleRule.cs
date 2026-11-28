namespace Refii.Ui;

/// <summary>
/// THE GUI GLINT RULE, pure: when a `ParticleEffect` element's particle exists, and where on the
/// GUI stage it stands. No Godot type, so the rule is tested headless (UiParticleRuleTests);
/// <see cref="Engine_UiParticles"/> applies it to the drawn tree.
/// </summary>
/// <remarks>
/// <para>THE GUEST'S RULE, per element per frame — CHUDParticle's update, Fable 3 TU3 `sub_82BCEEA0`
/// / disc `sub_82B99D58`, Fable 2 TU1 `sub_8283FB80` / disc `sub_828C95E8` (instruction-identical in
/// each title): WANTED = the element's active bit (service +76 with 0x2000, F2 0x8283FBE4 / 0x8283FBEC
/// `li r5,0x2000` / `lwz r8,0x4C(r9)`) AND its cumulative opacity, clamped 0..1, above 0
/// (F2 `bl sub_82284368` 0x8283FC18, `fsel` clamp, `ble` 0x8283FC74) AND its cumulative scale.x above
/// 0 (`bl sub_82425850` 0x8283FCA8, `ble` 0x8283FCBC) AND its projected point on the GUI camera's
/// screen (`bl sub_82A3F7D0` 0x8283FD30, `beq` 0x8283FD3C). The create / kill (F2 `sub_8283FDF8`,
/// F3 `sub_82BCF0D8`) runs only when WANTED differs from "an instance exists" (F2 0x8283FDC0..0x8283FDD8
/// `cmplw r10,r11 ; beq` then `bl sub_8283FDF8`): every rising edge makes a NEW instance, started at
/// its own time 0, and every falling edge kills it at once.</para>
/// <para>WHERE IT STANDS. The guest projects the element's drawn point through the GUI camera and
/// un-projects it onto the plane ONE UNIT in front of that camera (F3 `sub_82C74F78`: z = 1.0), so the
/// particle is a real effect in the GUI 3D world under the 10.2° camera (`sub_821A5F20`), drawn at
/// the element's screen place. On the GUI stage (camera at the origin looking down −Z, the canvas
/// plane <see cref="CanvasPlane"/> metres out at <see cref="MetresPerCanvasUnit"/> per unit,
/// Engine_UiFigureStage) a canvas point (x, y) is therefore (x·0.01/40, y·0.01/40, −1).</para>
/// <para>ITS ORIENTATION. π about the GUI world's (0, 0, 1) (F3 `flt_82062CE4` = 3.1415927 about
/// `flt_82065440` = 1.0; F2 TU1 0x8283FF7C..0x8283FFC8: the splat of 1.0 masked to (0, 0, 1)
/// `vand` with `unk_82099150`, f1 = π `flt_82099040`, `bl sub_8223A070` then
/// `CParticleSystemEntityInstance_SetOrientation`), the world being z-up with the camera looking
/// along +Y. The stage's axes are the GUI world's as (x, z, −y) (Engine_UiFigureStage's own placement:
/// canvas → guest `(gx, −gz, gy)·0.01`, guest → stage `(x, z, −y)`), and an effect node's columns are
/// its guest rows taken X, Z, Y (the particle system's (x, z, y) swap, GameFx.ToGodot): rows
/// (−1,0,0), (0,−1,0), (0,0,1) give columns (−1,0,0), (0,1,0), (0,0,1) — <see cref="StageBasis"/>.</para>
/// </remarks>
public static class Engine_UiParticleRule
{
	/// <summary>Camera to the canvas plane, metres (Engine_UiFigureStage.CanvasPlaneDistance).</summary>
	public const float CanvasPlane = 40f;

	/// <summary>Metres per canvas unit on the canvas plane (Engine_UiFigureStage.MetresPerCanvasUnit).</summary>
	public const float MetresPerCanvasUnit = 0.01f;

	/// <summary>The glint plane: one unit in front of the GUI camera (F3 `sub_82C74F78`, z = 1.0).</summary>
	public const float Depth = 1f;

	/// <summary>What one frame does to an element's particle.</summary>
	public enum Edge
	{
		/// <summary>Nothing: wanted and alive agree.</summary>
		None,

		/// <summary>A new instance, at its own time 0.</summary>
		Create,

		/// <summary>The instance goes at once.</summary>
		Kill,
	}

	/// <summary>The guest's accumulated opacity clamp (`fsel` against 0.0 and 1.0); NaN is 0.</summary>
	public static float Opacity01(float accumulated) =>
		float.IsNaN(accumulated) ? 0f : accumulated < 0f ? 0f : accumulated > 1f ? 1f : accumulated;

	/// <summary>The four terms: drawn (active), opacity above 0, scale.x above 0, on the screen.</summary>
	public static bool Wanted(bool drawn, float opacity, float scaleX, bool onScreen) =>
		drawn && Opacity01(opacity) > 0f && scaleX > 0f && onScreen;

	/// <summary>The create / kill the update makes: only when wanted differs from alive.</summary>
	public static Edge Step(bool alive, bool wanted) =>
		wanted == alive ? Edge.None : wanted ? Edge.Create : Edge.Kill;

	/// <summary>
	/// The on-screen test (F3 `sub_82C74ED0`: inside the camera's viewport rect): a canvas point
	/// inside the visible canvas, <paramref name="halfWidth"/> by <paramref name="halfHeight"/>
	/// canvas units about its centre.
	/// </summary>
	public static bool OnCanvas(float x, float y, float halfWidth, float halfHeight) =>
		float.IsFinite(x) && float.IsFinite(y) &&
		x >= -halfWidth && x <= halfWidth && y >= -halfHeight && y <= halfHeight;

	/// <summary>A canvas point (canvas units from the stage's centre) on the glint plane.</summary>
	public static (float X, float Y, float Z) StagePoint(float canvasX, float canvasY)
	{
		float perUnit = MetresPerCanvasUnit * Depth / CanvasPlane;
		return (canvasX * perUnit, canvasY * perUnit, -Depth);
	}

	/// <summary>The effect node's basis columns on the stage (x, y, z), each as (x, y, z).</summary>
	public static readonly (float X, float Y, float Z)[] StageBasis =
	{
		(-1f, 0f, 0f),
		(0f, 1f, 0f),
		(0f, 0f, 1f),
	};

	/// <summary>
	/// The stage camera's vertical field of view, degrees, that puts <paramref name="visibleHeight"/>
	/// canvas units across the view at the canvas plane (Engine_UiFigureStage.Frame) — the guest's
	/// 10.2° to within a fraction of a degree at the remaster's 712-unit canvas.
	/// </summary>
	public static float FieldOfViewDegrees(float visibleHeight) =>
		(float)(2.0 * System.Math.Atan(0.5 * visibleHeight * MetresPerCanvasUnit / CanvasPlane) * (180.0 / System.Math.PI));

	/// <summary>
	/// Canvas units one stage unit at the glint plane covers: 1 / (2·tan(fov/2)) of the view height,
	/// which is <see cref="CanvasPlane"/> / <see cref="MetresPerCanvasUnit"/> / <see cref="Depth"/>
	/// whatever the visible height — a glint keeps its size against the canvas on any window.
	/// </summary>
	public static float CanvasUnitsPerStageUnit(float visibleHeight)
	{
		double tan = System.Math.Tan(FieldOfViewDegrees(visibleHeight) * (System.Math.PI / 360.0));
		return (float)(visibleHeight / (2.0 * tan * Depth));
	}

	/// <summary>
	/// Whether a one-shot (<see cref="Engine_UiParticles.Burst"/>) has played out: it spawned, nothing
	/// of it is alive, and it is past its settle time — or past the cap, whatever it reports. The
	/// guest's one-shot (F3 `sub_823CE480`) is never killed by its caller; its instance ends with its
	/// own lifetime.
	/// </summary>
	public static bool BurstDone(float seconds, int spawned, int alive, float settle = 0.25f, float cap = 10f) =>
		seconds >= cap || (seconds >= settle && spawned > 0 && alive == 0);
}
