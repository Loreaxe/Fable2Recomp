using Godot;

namespace Refii.Camera;

/// <summary>
/// THE CO-OP SCREEN: the mode the players chose for local co-op, and hybrid's rule for when the one
/// shared view splits in two and when it rejoins. Pure; the split view (Refii.Game.GameSplitView) asks it
/// every drawn frame and the title's co-op session states its answer to the guest.
/// </summary>
/// <remarks>
/// <para>THREE MODES. <see cref="Mode.Shared"/>: one view, and the guest's leash stands (the original).
/// <see cref="Mode.Split"/>: a view per player for the whole game, the players free.
/// <see cref="Mode.Hybrid"/>: one view while the heroes are within the leash's distance, a view each once
/// they pass it, one again when they are back inside it; the players are free throughout. The orientation
/// (<see cref="OrientationKey"/>) lays out both split and hybrid.</para>
/// <para>THE LEASH'S OWN MEASURE. The guest's leash refuses a step that ends farther than the stated
/// distance from the other hero, head to head in 3-D: Fable 2 sub_821A0298 (disc sub_821A0340), distance
/// <c>lfs f0,flt_8331914C; fcmpu cr6,f31,f0</c> 0x821A0AE8 (disc flt_833190F8 0x821A0B90), f31 the
/// <c>vmsum3fp128</c> length of the whole head-to-head difference; Fable 3 sub_8279C050 (disc
/// sub_82765928), flt_832DE528 0x8279C748 (disc flt_8329E3D8 0x82766024). The distance is what
/// <c>Debug.SetLimitCharacterDistance</c> was last called with (SACCamera:SetMaxCoopDistance, the active
/// zoom level's MaxCoopDistance). Each title's core camera chunk states the distance the SHARED camera
/// last passed it and the heads' separation every camera tick (<c>Refii._coop_tether_stated</c>, the
/// shell's stated values 21..23): the camera framing one hero scales the zoom's distance by its hero
/// (SACCamera:UpdateZoomLevel, <c>self.Secondary and 1 or SACCamera.CalculateScale(self.Player, ...)</c>),
/// so the split screen's own statement is not the leash the shared screen holds. Hybrid splits when the
/// separation passes the distance and rejoins when it is <see cref="HysteresisMetres"/> inside it.</para>
/// <para>THE SWAP. Hybrid's split and rejoin move <see cref="Step"/> between 0 (the shared picture) and 1
/// (a picture each) over <see cref="SwapSeconds"/>; Engine_CameraSwap draws each half between the two.
/// The dedicated modes cut.</para>
/// </remarks>
public static class Engine_CameraScreens
{
	/// <summary>The setting: <c>shared</c>, <c>split</c> or <c>hybrid</c>.</summary>
	public const string ModeKey = "coop_screen_mode";

	/// <summary>The setting it replaces (true split, false shared), read only while <see cref="ModeKey"/> is unset.</summary>
	public const string LegacySplitKey = "coop_split_screen";

	/// <summary>How split and hybrid divide the window: <c>vertical</c> (side by side) or <c>horizontal</c> (stacked).</summary>
	public const string OrientationKey = "coop_split_screen_orientation";

	/// <summary>The setting's values, in <see cref="Mode"/> order.</summary>
	public static readonly string[] Names = { "shared", "split", "hybrid" };

	/// <summary>The local co-op screen modes.</summary>
	public enum Mode
	{
		Shared = 0,
		Split = 1,
		Hybrid = 2,
	}

	/// <summary>How far inside the stated distance the heroes must come before hybrid rejoins (metres).</summary>
	public const float HysteresisMetres = 1.5f;

	/// <summary>
	/// The time hybrid's swap takes, seconds: <c>SACCamera.CameraTransitionTime = 2</c>
	/// (stringandcagecamera.lua: Fable 2 line 9, Fable 3 line 54), the shipped camera's own blend between zoom sets.
	/// </summary>
	public const float SwapSeconds = 2f;

	/// <summary>The rows the co-op screen steps through: shared, split side by side, split stacked, hybrid side by side, hybrid stacked.</summary>
	public const int Rows = 5;

	/// <summary>
	/// The mode a setting's text names; with no text, the old switch's (true split, false shared). An
	/// unknown word reads as the default, split.
	/// </summary>
	public static Mode Parse(string? text, bool legacySplit)
	{
		string word = (text ?? string.Empty).Trim().Trim('"');
		if (word.Length == 0)
		{
			return legacySplit ? Mode.Split : Mode.Shared;
		}

		for (int i = 0; i < Names.Length; ++i)
		{
			if (word.Equals(Names[i], System.StringComparison.OrdinalIgnoreCase))
			{
				return (Mode)i;
			}
		}

		return Mode.Split;
	}

	/// <summary>The setting's word for <paramref name="mode"/>.</summary>
	public static string Name(Mode mode) => Names[(int)mode is >= 0 and < 3 ? (int)mode : 1];

	/// <summary>The mode the players chose, from the settings.</summary>
	public static Mode Chosen =>
		Parse(Refii.Core.Engine_Switches.String(ModeKey, string.Empty), Refii.Core.Engine_Switches.Flag(LegacySplitKey, true));

	/// <summary>Whether split and hybrid stack the views (player one top) rather than set them side by side.</summary>
	public static bool Stacked =>
		Refii.Core.Engine_Switches.String(OrientationKey, "vertical").Equals("horizontal", System.StringComparison.OrdinalIgnoreCase);

	/// <summary>The screen the camera mod API states (Engine_CameraViews.Screen*).</summary>
	public static int Screen(Mode mode, bool stacked) => mode switch
	{
		Mode.Shared => Engine_CameraViews.ScreenShared,
		Mode.Hybrid => Engine_CameraViews.ScreenHybrid,
		_ => stacked ? Engine_CameraViews.ScreenSplitHorizontal : Engine_CameraViews.ScreenSplitVertical,
	};

	/// <summary>The co-op screen's row for a mode and orientation (0 .. <see cref="Rows"/> - 1).</summary>
	public static int Row(Mode mode, bool stacked) => mode switch
	{
		Mode.Shared => 0,
		Mode.Split => stacked ? 2 : 1,
		_ => stacked ? 4 : 3,
	};

	/// <summary>
	/// The mode and orientation a co-op screen row names; the shared row keeps the orientation it is given.
	/// </summary>
	public static (Mode Mode, bool Stacked) FromRow(int row, bool stacked)
	{
		int r = ((row % Rows) + Rows) % Rows;
		return r switch
		{
			0 => (Mode.Shared, stacked),
			1 => (Mode.Split, false),
			2 => (Mode.Split, true),
			3 => (Mode.Hybrid, false),
			_ => (Mode.Hybrid, true),
		};
	}

	/// <summary>
	/// Hybrid's state after this tick: split once the separation passes the stated distance, rejoined once
	/// it is <see cref="HysteresisMetres"/> inside it. A distance or separation that is not a number, or a
	/// distance that is not positive, keeps the state as it is.
	/// </summary>
	public static bool HybridSplits(bool split, float stated, float separation)
	{
		if (!float.IsFinite(stated) || stated <= 0f || !float.IsFinite(separation) || separation < 0f)
		{
			return split;
		}

		return split ? separation >= RejoinBelow(stated) : separation > stated;
	}

	/// <summary>The separation hybrid rejoins under: <see cref="HysteresisMetres"/> inside the stated distance.</summary>
	public static float RejoinBelow(float stated) => stated - HysteresisMetres;

	/// <summary>
	/// The log line for hybrid's decision at the tick it is made, with the two stated numbers it
	/// compared. The swap then runs <see cref="SwapSeconds"/>; the drawn picture changes over it.
	/// </summary>
	public static string Decision(bool split, float stated, float separation) => split
		? string.Create(System.Globalization.CultureInfo.InvariantCulture,
			$"[split-view] hybrid splits: separation {separation:0.00} m past the stated distance {stated:0.00} m")
		: string.Create(System.Globalization.CultureInfo.InvariantCulture,
			$"[split-view] hybrid rejoins: separation {separation:0.00} m under {RejoinBelow(stated):0.00} m (stated distance {stated:0.00} m)");

	/// <summary>
	/// The swap's progress after <paramref name="seconds"/>: toward 1 (a picture each) while
	/// <paramref name="apart"/>, else toward 0 (the shared picture), the whole way in <see cref="SwapSeconds"/>.
	/// </summary>
	public static float Step(float progress, bool apart, float seconds)
	{
		float p = float.IsFinite(progress) ? Mathf.Clamp(progress, 0f, 1f) : 0f;
		if (!float.IsFinite(seconds) || seconds <= 0f)
		{
			return p;
		}

		float by = seconds / SwapSeconds;
		return apart ? Mathf.Min(1f, p + by) : Mathf.Max(0f, p - by);
	}

	/// <summary>The swap's weight for a progress: smoothstep, 0 and 1 at the ends, still at both.</summary>
	public static float Ease(float progress)
	{
		float p = float.IsFinite(progress) ? Mathf.Clamp(progress, 0f, 1f) : 0f;
		return p * p * (3f - 2f * p);
	}
}
