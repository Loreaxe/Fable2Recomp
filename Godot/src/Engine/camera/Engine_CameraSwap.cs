using Godot;

namespace Refii.Camera;

/// <summary>
/// THE PICTURE OF HYBRID'S SWAP: each half of the window between the part of the shared picture it covers
/// and its own player's picture. Pure math over the two Godot camera projections the split view uses.
/// </summary>
/// <remarks>
/// <para>AN UNCHANGED PICTURE AT THE DIVIDER. At progress 0 each half shows exactly its part of the window's
/// picture: player one's camera, cut at the divider (<see cref="Crop"/>, an off-axis frustum), so the screen
/// reads as before with a line down it. At 1 each half shows its own player's camera framed for the half
/// (<see cref="Own"/>, the split view's angle rule). Between, the frustum's size and offset and player two's
/// pose move together by the eased weight; player one's pose is the window camera's throughout.</para>
/// <para>THE FRUSTUM. A picture on the near plane with the height kept: <c>Size</c> is its height and
/// <c>Offset</c> its centre's displacement from the camera's axis there, so a perspective camera of vertical
/// angle <c>v</c> is <c>Size = 2·near·tan(v/2)</c>, offset zero.</para>
/// <para>NO CAMERA IS EVER OFF-AXIS. An off-centre frustum is not a camera Godot draws correctly: its
/// volumetric fog builds its froxels from <c>Projection.get_viewport_half_extents</c> and fits the sun's
/// cascades to <c>set_perspective(get_fov(), …)</c>, both of which assume a symmetric frustum (the note in
/// core/math/projection.cpp), and every view mirror and post pass of ours rebuilds rays from the diagonal.
/// So each half draws the CENTRED picture that holds its frustum — twice the half along the divider's axis
/// (<see cref="Full"/>, <see cref="CoverVerticalDegrees"/>) — and shows the frustum's part of it
/// (<see cref="Shown"/>): the same pixels the off-axis frustum would give, through a camera every system
/// reads right.</para>
/// </remarks>
public static class Engine_CameraSwap
{
	/// <summary>A view's picture on the near plane, height kept: its height and its centre's offset.</summary>
	public readonly record struct Frustum(float Size, Vector2 Offset);

	/// <summary>Whether a view of this size keeps its width's angle (narrower than 4:3), as the split view's rule.</summary>
	public static bool Narrow(Vector2I size) => size.X * 3 < size.Y * 4;

	/// <summary>
	/// The part of the window's picture view <paramref name="view"/> (0 player one, 1 player two) covers: the
	/// window camera's vertical angle over the window's aspect (width / height), cut at the divider — side by
	/// side the left or right half, stacked the top or bottom half.
	/// </summary>
	public static Frustum Crop(float windowVerticalDegrees, float windowAspect, float near, bool stacked, int view)
	{
		float half = near * Mathf.Tan(Mathf.DegToRad(windowVerticalDegrees) * 0.5f);
		float aspect = windowAspect > 0f && float.IsFinite(windowAspect) ? windowAspect : 1f;
		if (stacked)
		{
			return new Frustum(half, new Vector2(0f, view == 0 ? half * 0.5f : -half * 0.5f));
		}

		return new Frustum(2f * half, new Vector2(view == 0 ? -aspect * half * 0.5f : aspect * half * 0.5f, 0f));
	}

	/// <summary>
	/// A view's own picture for a stated horizontal angle: kept as stated across a view narrower than 4:3,
	/// else the 4:3-derived vertical angle <c>2·atan(0.75·tan(h/2))</c> the window keeps; centred.
	/// </summary>
	public static Frustum Own(float horizontalDegrees, float aspect, float near, bool narrow)
	{
		float t = Mathf.Tan(Mathf.DegToRad(horizontalDegrees) * 0.5f);
		float a = aspect > 0f && float.IsFinite(aspect) ? aspect : 1f;
		return new Frustum(narrow ? 2f * near * t / a : 2f * near * 0.75f * t, Vector2.Zero);
	}

	/// <summary>The frustum <paramref name="weight"/> of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
	public static Frustum Blend(Frustum from, Frustum to, float weight) =>
		new(Mathf.Lerp(from.Size, to.Size, weight), from.Offset.Lerp(to.Offset, weight));

	/// <summary>The pose <paramref name="weight"/> of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
	public static Transform3D Pose(Transform3D from, Transform3D to, float weight) =>
		weight <= 0f ? from : weight >= 1f ? to : from.InterpolateWith(to, weight);

	/// <summary>
	/// The size a view draws at during the swap: twice <paramref name="half"/> along the divider's axis, so its
	/// centred picture holds every frustum the swap passes through (<see cref="Crop"/> sits at one edge of it,
	/// <see cref="Own"/> in its middle).
	/// </summary>
	public static Vector2I Full(Vector2I half, bool stacked) =>
		stacked ? new Vector2I(half.X, half.Y * 2) : new Vector2I(half.X * 2, half.Y);

	/// <summary>
	/// The vertical angle (degrees) of the centred camera, height kept, whose picture at <see cref="Full"/> holds
	/// <paramref name="frustum"/> at the half's pixel scale: its height side by side, twice it stacked.
	/// </summary>
	public static float CoverVerticalDegrees(Frustum frustum, float near, bool stacked) =>
		VerticalDegrees(new Frustum(stacked ? 2f * frustum.Size : frustum.Size, Vector2.Zero), near);

	/// <summary>
	/// The part of the <see cref="Full"/> picture <paramref name="frustum"/> covers, in that picture's pixels:
	/// <paramref name="half"/> in size, slid along the divider's axis by the frustum's offset (the crop of player
	/// one's half at the start, the middle at the end). Clamped inside the picture.
	/// </summary>
	public static Rect2I Shown(Frustum frustum, Vector2I half, bool stacked)
	{
		if (!float.IsFinite(frustum.Size) || frustum.Size <= 0f || half.X <= 0 || half.Y <= 0)
		{
			return new Rect2I(stacked ? new Vector2I(0, half.Y / 2) : new Vector2I(half.X / 2, 0), half);
		}

		if (stacked)
		{
			float top = (0.5f - frustum.Offset.Y / frustum.Size) * half.Y;
			return new Rect2I(0, Mathf.Clamp(Mathf.RoundToInt(top), 0, half.Y), half.X, half.Y);
		}

		float width = frustum.Size * half.X / half.Y;
		float left = (frustum.Offset.X / width + 0.5f) * half.X;
		return new Rect2I(Mathf.Clamp(Mathf.RoundToInt(left), 0, half.X), 0, half.X, half.Y);
	}

	/// <summary>
	/// The vertical angle (degrees) a frustum's height spans on the near plane, for the views the camera mod API
	/// states: the picture's own extent; its off-axis part is stated apart (the frustum's offset over the near
	/// distance, Engine_CameraViews.View.Centre).
	/// </summary>
	public static float VerticalDegrees(Frustum frustum, float near) =>
		near > 0f ? Mathf.RadToDeg(2f * Mathf.Atan(frustum.Size * 0.5f / near)) : 0f;
}
