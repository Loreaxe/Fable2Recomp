using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.Game;

/// <summary>
/// The debug channel's `pads` report: every joypad on the roster and every guest pad, with who
/// holds what. Reads only.
/// </summary>
public partial class GameInput
{
	/// <summary>The holders of the four guest pads, −1 free: what a dummy connect is checked against.</summary>
	public static IReadOnlyList<int> PadHolders => _padDevice;

	/// <summary>The guest pad <paramref name="joypad"/> holds, or −1.</summary>
	public static int PadOfJoypad(int joypad) => joypad >= 0 ? System.Array.IndexOf(_padDevice, joypad) : -1;

	/// <summary>
	/// The joypads (id, name, known, dummy, family, the guest pad held or −1, that pad's player or 0,
	/// the buttons Godot's polled state holds now as an XInput mask) and the guest pads (the joypad
	/// holding it or −1, the title's player or 0, whether the guest is told it is connected).
	/// </summary>
	public static (List<Dictionary<string, object?>> Joypads, List<Dictionary<string, object?>> Pads) DescribePads()
	{
		EnsureJoypadsHooked();
		GameTitleInput title = Engine_TitleGame.Active.Input;
		var joypads = new List<Dictionary<string, object?>>();
		foreach ((int id, bool known) in _connectedJoypads)
		{
			int pad = System.Array.IndexOf(_padDevice, id);
			joypads.Add(new Dictionary<string, object?>
			{
				["id"] = id,
				["name"] = Engine_Joypads.Name(id),
				["known"] = known,
				["dummy"] = Engine_DummyJoypad.Is(id),
				["family"] = FamilyOfJoypad(id).ToString(),
				["pad"] = pad,
				["player"] = pad >= 0 ? title.ModPlayer(pad) : 0,
				["buttons"] = JoypadButtons(id),
			});
		}

		var pads = new List<Dictionary<string, object?>>();
		for (int pad = 0; pad < MaxControllers; ++pad)
		{
			pads.Add(new Dictionary<string, object?>
			{
				["pad"] = pad,
				["joypad"] = _padDevice[pad],
				["player"] = title.ModPlayer(pad),
				["connected"] = pad == 0 || _padDevice[pad] >= 0 || GameDebugServer.PadConnected(pad),
			});
		}

		return (joypads, pads);
	}
}
