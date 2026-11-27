using Godot;
using Refii.Ui;

namespace Refii.Titles.Fable2;

/// <summary>
/// Whose menu it is, in local co-op (<see cref="Engine_UiMenuOwnerTag"/>): the pause book and every
/// screen opened from it, a screen a player opened in the world (shop, gift, real estate, map), and a
/// box built for one player. The owner is the one the guest's input routing serves
/// (<see cref="Fable2Input.MenuOwner"/>), followed while it is stated and kept through a box over that
/// player's menu, when the stated owner drops to none (<see cref="Fable2ActingPlayer.TagOwner"/>).
/// </summary>
public partial class GameUi
{
	private Engine_UiMenuOwnerTag? _menuOwnerTag;

	/// <summary>The owner shown while a menu stays up; 0 when none is.</summary>
	private int _menuOwnerShown;

	private const int MenuOwnerSize = 22;

	/// <summary>The size the tag takes, from the banner's title (three quarters of it).</summary>
	private int _menuOwnerSize = MenuOwnerSize;

	/// <summary>Where the tag stood beside the banner's title last.</summary>
	private Vector3? _menuOwnerAt;

	/// <summary>
	/// Over every presentation slab (depth × 1000) a book stands on, and under the achievement
	/// toast (9000).
	/// </summary>
	private const float TagDepth = 8900f;

	private void DriveMenuOwnerTag(Vector2 window)
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
		_menuOwnerShown = Fable2ActingPlayer.TagOwner(
			_menuOwnerShown,
			CoopSession.LocalCoop && menuUp && screen != CoopScreenId,
			_spells.WheelPlayer == 0 ? Fable2Input.MenuOwner : 0,
			Fable2Bridge.GetMenuOwner());

		if (_menuOwnerShown == 0)
		{
			if (_menuOwnerTag is not null && IsInstanceValid(_menuOwnerTag))
			{
				_menuOwnerTag.Visible = false;
			}

			_menuOwnerLogged = string.Empty;
			return;
		}

		if (_menuOwnerTag is null || !IsInstanceValid(_menuOwnerTag))
		{
			_menuOwnerTag = new Engine_UiMenuOwnerTag { Name = "MenuOwnerTag", Theme = _presentation.Theme };
			_presentation.MenuLayer.AddChild(_menuOwnerTag);
		}

		_menuOwnerTag.Theme ??= _presentation.Theme;

		if (bannerUp && Engine_UiTheme.FindByName(_pauseBanner!, "Text_15") is MeshInstance3D title &&
			title.Mesh is { } mesh && title.IsVisibleInTree())
		{
			// IN THE BANNER'S OWN BAND, never over a list: on the title's line, just after the title as
			// it is drawn, in front of it.
			Aabb drawn = title.GlobalTransform * mesh.GetAabb();
			int size = title.HasMeta("text_size") ? (int)title.GetMeta("text_size") : 24;
			_menuOwnerSize = Mathf.Max(14, size * 3 / 4);
			Vector3 next = _presentation.MenuLayer.GlobalTransform.AffineInverse() *
				new Vector3(drawn.End.X + (_menuOwnerSize * 0.6f), drawn.GetCenter().Y, drawn.End.Z);
			_menuOwnerAt = new Vector3(next.X, next.Y, Mathf.Max(next.Z + 1f, TagDepth));
		}

		// A screen opened in the world has no banner: the tag stands centred at the top of the window,
		// above where any list draws.
		bool beside = bannerUp && _menuOwnerAt is not null;
		Vector3 at = beside
			? _menuOwnerAt!.Value
			: new Vector3(0f, (Engine_HudLayout.CanvasExtent(window).Y * 0.5f) - 40f, TagDepth);
		_menuOwnerTag.Show(_menuOwnerShown, at, _menuOwnerSize, leftAligned: beside);
		string said = $"{_menuOwnerShown}|{screen}|{bannerUp}|{ownerStated}";
		if (said != _menuOwnerLogged)
		{
			_menuOwnerLogged = said;
			GD.Print($"[game-ui] menu owner tag: player {_menuOwnerShown} over screen {screen} " +
				$"(banner {(bannerUp ? "up" : "down")}, stated owner {(ownerStated ? "with a box" : "none")}) at {at}");
		}
	}

	private string _menuOwnerLogged = string.Empty;
}
