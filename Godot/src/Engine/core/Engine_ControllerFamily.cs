using System;
using System.Globalization;

namespace Refii.Core;

/// <summary>Who made a controller, as far as its button vocabulary goes.</summary>
public enum ControllerFamily
{
	/// <summary>Nothing identified it: drawn as the Xbox pad, the layout Godot's SDL mapping is in.</summary>
	Unknown,
	Xbox,
	/// <summary>DualShock and older: the share button is Share.</summary>
	PlayStation,
	/// <summary>DualSense: the share button is Create.</summary>
	PlayStation5,
	Nintendo,
}

/// <summary>The glyph vocabulary a prompt is drawn in.</summary>
public enum GlyphStyle
{
	Xbox,
	PlayStation,
	Switch,
}

/// <summary>
/// Identifies a controller's family from what Godot states about the joypad: the USB vendor id
/// (<c>Input.GetJoyInfo</c>'s <c>vendor_id</c> where the platform supplies it, else the SDL GUID's
/// vendor field), then the device name. Pure — the Godot calls stay in <c>GameInput</c>.
/// </summary>
/// <remarks>
/// <para>The SDL GUID is 16 bytes as 32 hex digits: bus (LE16), CRC (LE16), vendor (LE16), zero,
/// product (LE16), zero, version (LE16), driver/zero. A GUID whose bus field is zero or whose
/// vendor field reads zero carries no ids (an XInput slot or a name hash), so the name decides.</para>
/// <para>Vendors: Sony 0x054C, Nintendo 0x057E, Microsoft 0x045E. A third-party pad under another
/// vendor id falls through to its name ("Pro Controller", "DualSense", ...) and else Unknown.</para>
/// </remarks>
public static class Engine_ControllerFamily
{
	public const int SonyVendor = 0x054C;
	public const int NintendoVendor = 0x057E;
	public const int MicrosoftVendor = 0x045E;

	/// <summary>DualSense and DualSense Edge product ids.</summary>
	private static readonly int[] DualSenseProducts = { 0x0CE6, 0x0DF2 };

	/// <summary>
	/// The family of a joypad. <paramref name="vendor"/> / <paramref name="product"/> are the
	/// platform's ids, or 0 when it states none.
	/// </summary>
	public static ControllerFamily Classify(string? name, string? guid, int vendor = 0, int product = 0)
	{
		if (vendor == 0 && GuidIds(guid) is { } ids)
		{
			vendor = ids.Vendor;
			product = ids.Product;
		}

		switch (vendor)
		{
			case SonyVendor:
				return Array.IndexOf(DualSenseProducts, product) >= 0 || NamesDualSense(name)
					? ControllerFamily.PlayStation5
					: ControllerFamily.PlayStation;
			case NintendoVendor:
				return ControllerFamily.Nintendo;
			case MicrosoftVendor:
				return ControllerFamily.Xbox;
		}

		return ByName(name);
	}

	/// <summary>The family a device name alone says.</summary>
	public static ControllerFamily ByName(string? name)
	{
		string n = (name ?? string.Empty).ToLowerInvariant();
		if (n.Length == 0)
		{
			return ControllerFamily.Unknown;
		}

		if (NamesDualSense(n))
		{
			return ControllerFamily.PlayStation5;
		}

		if (n.Contains("dualshock") || n.Contains("playstation") || n.Contains("ps4") || n.Contains("ps3") ||
			n.Contains("sony"))
		{
			return ControllerFamily.PlayStation;
		}

		if (n.Contains("pro controller") || n.Contains("joy-con") || n.Contains("joycon") ||
			n.Contains("nintendo") || n.Contains("switch") || n.Contains("snes controller") || n.Contains("n64 controller"))
		{
			return ControllerFamily.Nintendo;
		}

		if (n.Contains("xbox") || n.Contains("xinput") || n.Contains("x-box") || n.Contains("microsoft"))
		{
			return ControllerFamily.Xbox;
		}

		return ControllerFamily.Unknown;
	}

	private static bool NamesDualSense(string? name)
	{
		string n = (name ?? string.Empty).ToLowerInvariant();
		return n.Contains("dualsense") || n.Contains("ps5");
	}

	/// <summary>The vendor and product an SDL GUID carries, or null when it carries none.</summary>
	public static (int Vendor, int Product)? GuidIds(string? guid)
	{
		if (guid is null || guid.Length < 20)
		{
			return null;
		}

		if (!Le16(guid, 0, out int bus) || bus == 0 ||
			!Le16(guid, 8, out int vendor) || vendor == 0 ||
			!Le16(guid, 16, out int product))
		{
			return null;
		}

		return (vendor, product);
	}

	/// <summary>A little-endian 16-bit field at hex digit <paramref name="at"/>.</summary>
	private static bool Le16(string hex, int at, out int value)
	{
		value = 0;
		if (!byte.TryParse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte lo) ||
			!byte.TryParse(hex.AsSpan(at + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte hi))
		{
			return false;
		}

		value = lo | (hi << 8);
		return true;
	}

	/// <summary>
	/// What <paramref name="style"/> prints on the physical input at <paramref name="input"/>'s
	/// position: Cross for the south button on a PlayStation pad, B on a Switch pad.
	/// <paramref name="ps5"/> names the DualSense's Create for Back where the older pads say Share.
	/// </summary>
	public static string Label(GlyphStyle style, PadInput input, bool ps5 = false) => style switch
	{
		GlyphStyle.PlayStation => input switch
		{
			PadInput.A => "Cross",
			PadInput.B => "Circle",
			PadInput.X => "Square",
			PadInput.Y => "Triangle",
			PadInput.LB => "L1",
			PadInput.RB => "R1",
			PadInput.LT => "L2",
			PadInput.RT => "R2",
			PadInput.L3 => "L3",
			PadInput.R3 => "R3",
			PadInput.Start => "Options",
			PadInput.Back => ps5 ? "Create" : "Share",
			_ => DpadLabel(input),
		},
		GlyphStyle.Switch => input switch
		{
			PadInput.A => "B",
			PadInput.B => "A",
			PadInput.X => "Y",
			PadInput.Y => "X",
			PadInput.LB => "L",
			PadInput.RB => "R",
			PadInput.LT => "ZL",
			PadInput.RT => "ZR",
			PadInput.L3 => "LS",
			PadInput.R3 => "RS",
			PadInput.Start => "+",
			PadInput.Back => "-",
			_ => DpadLabel(input),
		},
		_ => input switch
		{
			PadInput.L3 => "LS",
			PadInput.R3 => "RS",
			PadInput.Start or PadInput.Back or PadInput.A or PadInput.B or PadInput.X or PadInput.Y or
				PadInput.LB or PadInput.RB or PadInput.LT or PadInput.RT => input.ToString(),
			_ => DpadLabel(input),
		},
	};

	private static string DpadLabel(PadInput input) => input switch
	{
		PadInput.DpadUp => "D-pad up",
		PadInput.DpadDown => "D-pad down",
		PadInput.DpadLeft => "D-pad left",
		_ => "D-pad right",
	};

	/// <summary>
	/// The player whose device the shared prompts follow: the open menu's owner on a split screen;
	/// with no menu open on a split screen, player one (each half is its player's, and player two's
	/// half takes player two's own set); on one screen, the player whose controller was used last.
	/// </summary>
	public static int PromptPlayer(bool split, int menuOwner, int lastUsedPlayer)
	{
		if (split)
		{
			return menuOwner is 1 or 2 ? menuOwner : 1;
		}

		return lastUsedPlayer is 1 or 2 ? lastUsedPlayer : 1;
	}

	/// <summary>The glyph set a surface for <paramref name="player"/> takes: 2 for player two's own, else the shared 1.</summary>
	public static int SurfaceSet(int player) => player == 2 ? 2 : 1;

	/// <summary>
	/// The glyph set a prompt takes whose guest record states its owner as a hero's 64-bit uid
	/// (<paramref name="ownerUid"/>, 0 for any player): player two's own set exactly when that owner is this
	/// machine's local player two (<paramref name="localTwoUid"/>, 0 when there is none), else the shared set.
	/// Only that player can answer such a prompt, so it reads in their controller on any screen layout.
	/// </summary>
	public static int OwnerSet(ulong ownerUid, ulong localTwoUid) =>
		SurfaceSet(ownerUid != 0ul && ownerUid == localTwoUid ? 2 : 1);

	/// <summary>
	/// The style a setting draws for a family: <c>auto</c> (or anything unrecognised) follows the
	/// device; <c>xbox</c>, <c>playstation</c> and <c>switch</c> force one.
	/// </summary>
	public static GlyphStyle Resolve(string? setting, ControllerFamily family) =>
		(setting ?? string.Empty).Trim().ToLowerInvariant() switch
		{
			"xbox" => GlyphStyle.Xbox,
			"playstation" or "ps" => GlyphStyle.PlayStation,
			"switch" or "nintendo" => GlyphStyle.Switch,
			_ => family switch
			{
				ControllerFamily.PlayStation or ControllerFamily.PlayStation5 => GlyphStyle.PlayStation,
				ControllerFamily.Nintendo => GlyphStyle.Switch,
				_ => GlyphStyle.Xbox,
			},
		};
}
