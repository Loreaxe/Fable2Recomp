using Godot;

namespace Refii.Titles.Fable2;

/// <summary>
/// Whose menu it is, in local co-op: the pause book and every screen opened from it, a screen a
/// player opened in the world (shop, gift, real estate, map), and a box built for one player. The
/// owner is the one the guest's input routing serves (<see cref="Fable2Input.MenuOwner"/>), followed
/// while it is stated and kept through a box over that player's menu, when the stated owner drops to
/// none (<see cref="Fable2ActingPlayer.OpenMenuOwner"/>). Nothing is drawn for it: the game's own
/// boxes name the player. It is logged on change, `[game-ui] menu owner: player N over screen S`.
/// </summary>
public partial class GameUi
{
	/// <summary>The owner of the menu that stays up; 0 when none is.</summary>
	private int _menuOwnerNamed;

	private string _menuOwnerLogged = string.Empty;

	private void SayMenuOwner()
	{
		if (_presentation is null || !GodotObject.IsInstanceValid(_presentation))
		{
			return;
		}

		// 31 is gameplay with the pause root parked (the banner and UpScreenId read it so): no menu.
		uint screen = OwnedScreenId() is var owned && owned != 31u ? owned : 0u;
		bool bannerUp = _pauseBanner is not null && IsInstanceValid(_pauseBanner) && _pauseBanner.Visible;
		// The stated owner outlives the menu (it stays player one's in play), so it names a menu
		// only while a box is standing (a consent, a mod's box, a seated box).
		bool boxUp = Fable2Input.ModalBoxOwnsInput || Fable2Input.DisplayMessageBoxUp ||
			Fable2Input.MenuBoxOwnsInput || Fable2Input.ReceiveItemsOwnsInput;
		bool ownerStated = Fable2Input.MenuOwner != 0 && _spells.WheelPlayer == 0 && boxUp;
		bool menuUp = bannerUp || (screen != 0 && screen != CoopScreenId) || ownerStated;

		// The co-op screen names both players on its own pages; solo and online have one player here.
		_menuOwnerNamed = Fable2ActingPlayer.OpenMenuOwner(
			_menuOwnerNamed,
			CoopSession.LocalCoop && menuUp && screen != CoopScreenId,
			_spells.WheelPlayer == 0 ? Fable2Input.MenuOwner : 0,
			Fable2Bridge.GetMenuOwner());

		if (_menuOwnerNamed == 0)
		{
			_menuOwnerLogged = string.Empty;
			return;
		}

		string said = $"{_menuOwnerNamed}|{screen}";
		if (said != _menuOwnerLogged)
		{
			_menuOwnerLogged = said;
			GD.Print($"[game-ui] menu owner: player {_menuOwnerNamed} over screen {screen}");
		}
	}
}
