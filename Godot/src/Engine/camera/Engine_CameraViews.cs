using Godot;

namespace Refii.Camera;

/// <summary>
/// THE VIEWS THE SHELL DRAWS, per player, for the camera mod API's read calls: the pose each player's
/// picture is drawn with this frame, the part of the window it fills, and the screen (the co-op screen
/// the player chose and whether two views are drawn). Godot owns all of it; it is stated to the title's
/// bridge every drawn frame (<see cref="Words"/>, src/runtime/include/core/camera_views.h) so the Lua and
/// native surfaces answer the same values on the guest thread, and <see cref="Refii.Mods.RefiiHost"/>
/// answers from here directly.
/// </summary>
/// <remarks>
/// <para>AXES. Poses are kept in the guest's world axes, as every position the mod API hands out:
/// <see cref="ToGuest"/> is Godot's <c>(x, y, z)</c> to <c>(-x, z, y)</c>, a rotation (its own inverse
/// up to the sign of x), so a camera's right is <c>forward × up</c> in either frame.</para>
/// <para>THE DRAWN ANGLES. A view keeps either its height's angle (the window, a half wider than 4:3) or
/// its width's (a half narrower than 4:3, <see cref="Camera3D.KeepAspectEnum.Width"/>);
/// <see cref="Tangents"/> gives both half-angle tangents of the picture actually drawn, from which a ray
/// through any point of the view is <c>forward + u·tan(h/2)·right + v·tan(v/2)·up</c>.</para>
/// <para>ONE SHARED SCREEN. When the local players share one view, player two's picture is player one's:
/// both are stated with the one pose and the whole window. A player whose picture is not drawn on this
/// machine (no player two, an online partner) is stated as not drawn.</para>
/// </remarks>
public static class Engine_CameraViews
{
	/// <summary>The screen modes (core/camera_views.h kScreen*).</summary>
	public const int ScreenShared = 0;
	public const int ScreenSplitVertical = 1;
	public const int ScreenSplitHorizontal = 2;
	public const int ScreenHybrid = 3;

	/// <summary>Words per player in the stated layout (kViewWords) and in all (kViewsWords).</summary>
	public const int ViewWords = 20;
	public const int ViewsWords = 2 + 2 * ViewWords;

	/// <summary>
	/// One player's drawn picture. <c>Centre</c> is the picture's centre off the camera's axis, as tangents
	/// along its right and up: zero but through hybrid's swap, where a half shows an off-centre part of the
	/// centred picture its camera draws (Engine_CameraSwap.Shown).
	/// </summary>
	public readonly record struct View(
		Vector3 Position,
		Vector3 Forward,
		Vector3 Up,
		float HorizontalFov,
		float VerticalFov,
		bool Cut,
		Rect2 Rect,
		Vector2 Centre = default)
	{
		/// <summary>The camera's right, guest axes: <c>forward × up</c>.</summary>
		public Vector3 Right => Forward.Cross(Up).Normalized();
	}

	private static readonly View?[] Views = new View?[2];
	private static readonly float[] Stated = new float[ViewsWords];

	/// <summary>The co-op screen chosen (<see cref="ScreenShared"/> ..), as last stated.</summary>
	public static int ScreenMode { get; private set; }

	/// <summary>Whether two views are drawn now.</summary>
	public static bool Split { get; private set; }

	/// <summary>Player <paramref name="player"/>'s picture this frame, or null where none is drawn here.</summary>
	public static View? For(int player) => player is 1 or 2 ? Views[player - 1] : null;

	/// <summary>States the screen for this frame.</summary>
	public static void Screen(int mode, bool split)
	{
		ScreenMode = mode is >= ScreenShared and <= ScreenHybrid ? mode : ScreenShared;
		Split = split;
	}

	/// <summary>States player <paramref name="player"/>'s picture (null: none drawn here).</summary>
	public static void Draw(int player, View? view)
	{
		if (player is 1 or 2)
		{
			Views[player - 1] = view;
		}
	}

	/// <summary>
	/// A picture from the Godot camera that draws it: its transform (Godot axes), the angle it keeps and
	/// on which side, its view's aspect (width / height) and the part of the window the view fills.
	/// </summary>
	public static View FromCamera(Transform3D transform, float fovDegrees, bool keepsHeight, float aspect, Rect2 rect, bool cut,
		Vector2 centre = default)
	{
		(float th, float tv) = Tangents(fovDegrees, keepsHeight, aspect);
		return new View(
			ToGuest(transform.Origin),
			ToGuest(-transform.Basis.Z).Normalized(),
			ToGuest(transform.Basis.Y).Normalized(),
			2f * Mathf.Atan(th),
			2f * Mathf.Atan(tv),
			cut,
			rect,
			centre);
	}

	/// <summary>
	/// The tangents of the drawn picture's half-angles, horizontal and vertical: the kept side's from
	/// the camera's fov, the other through the view's aspect (width / height).
	/// </summary>
	public static (float Horizontal, float Vertical) Tangents(float fovDegrees, bool keepsHeight, float aspect)
	{
		float kept = Mathf.Tan(Mathf.DegToRad(fovDegrees) * 0.5f);
		float a = aspect > 0f && float.IsFinite(aspect) ? aspect : 1f;
		return keepsHeight ? (kept * a, kept) : (kept, kept / a);
	}

	/// <summary>Godot's axes to the guest's: <c>(-x, z, y)</c>.</summary>
	public static Vector3 ToGuest(Vector3 godot) => new(-godot.X, godot.Z, godot.Y);

	/// <summary>The guest's axes to Godot's: <c>(-x, z, y)</c>.</summary>
	public static Vector3 ToGodot(Vector3 guest) => new(-guest.X, guest.Z, guest.Y);

	/// <summary>
	/// The ray through point (<paramref name="x"/>, <paramref name="y"/>) of the view, each 0..1 from the
	/// view's top left (0.5, 0.5 its centre): origin the camera's position, direction unit length, guest axes.
	/// Through an off-centre picture (<see cref="View.Centre"/>) the point is the picture's, not the axis's.
	/// </summary>
	public static (Vector3 Origin, Vector3 Direction) Ray(View view, float x, float y)
	{
		float u = 2f * x - 1f;
		float v = 1f - 2f * y;
		float th = Mathf.Tan(view.HorizontalFov * 0.5f);
		float tv = Mathf.Tan(view.VerticalFov * 0.5f);
		Vector3 direction = view.Forward + view.Right * (view.Centre.X + u * th) + view.Up * (view.Centre.Y + v * tv);
		return (view.Position, direction.Normalized());
	}

	/// <summary>
	/// The drawn transform with a display offset (camera_offset, seven values: right, up, back metres
	/// along the camera's own axes; yaw, pitch, roll degrees about its up, right and back), Godot axes.
	/// </summary>
	public static Transform3D Offset(Transform3D drawn, float[]? seven)
	{
		if (seven is not { Length: >= 7 })
		{
			return drawn;
		}

		Basis basis = drawn.Basis;
		Vector3 origin = drawn.Origin + basis.X * seven[0] + basis.Y * seven[1] + basis.Z * seven[2];
		Basis turn = new Basis(Vector3.Up, Mathf.DegToRad(seven[3]))
			* new Basis(Vector3.Right, Mathf.DegToRad(seven[4]))
			* new Basis(Vector3.Back, Mathf.DegToRad(seven[5]));
		return new Transform3D((basis * turn).Orthonormalized(), origin);
	}

	/// <summary>A drawn fov (degrees) with the offset's fov added, kept inside 1..179.</summary>
	public static float OffsetFov(float fov, float[]? seven) =>
		seven is { Length: >= 7 } ? Mathf.Clamp(fov + seven[6], 1f, 179f) : fov;

	/// <summary>The stated layout (core/camera_views.h): the screen, then each player's view.</summary>
	public static float[] Words()
	{
		float[] w = Stated;
		System.Array.Clear(w);
		w[0] = ScreenMode;
		w[1] = Split ? 1f : 0f;
		for (int player = 0; player < 2; ++player)
		{
			if (Views[player] is not { } view)
			{
				continue;
			}

			int at = 2 + player * ViewWords;
			(w[at + 0], w[at + 1], w[at + 2]) = (view.Position.X, view.Position.Y, view.Position.Z);
			(w[at + 3], w[at + 4], w[at + 5]) = (view.Forward.X, view.Forward.Y, view.Forward.Z);
			(w[at + 6], w[at + 7], w[at + 8]) = (view.Up.X, view.Up.Y, view.Up.Z);
			w[at + 9] = view.HorizontalFov;
			w[at + 10] = view.VerticalFov;
			w[at + 11] = view.Cut ? 1f : 0f;
			(w[at + 12], w[at + 13], w[at + 14], w[at + 15]) = (view.Rect.Position.X, view.Rect.Position.Y, view.Rect.Size.X, view.Rect.Size.Y);
			w[at + 16] = 1f;
			(w[at + 17], w[at + 18]) = (view.Centre.X, view.Centre.Y);
		}

		return w;
	}

	/// <summary>Forgets every view (a session's end).</summary>
	public static void Clear()
	{
		Views[0] = null;
		Views[1] = null;
		ScreenMode = ScreenShared;
		Split = false;
	}
}
