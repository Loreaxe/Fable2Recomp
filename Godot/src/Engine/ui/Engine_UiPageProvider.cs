using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>Everything a detail page can be filled from, gathered once per ask.</summary>
public readonly struct Engine_UiPageContext
{
	public required uint ScreenId { get; init; }

	public required Engine_UiExpandableMenu.Item? Row { get; init; }

	public required Refii.Host.HostBridge.HeroVitals Vitals { get; init; }

	public required Refii.Host.HostBridge.HeroDerived Hero { get; init; }

	public required List<Refii.Host.HostBridge.CompletedQuest> Quests { get; init; }

	public required Refii.Host.HostBridge.CrimeScreen Crime { get; init; }

	public required Refii.Host.HostBridge.OptionSliders Sliders { get; init; }

	public required Refii.Text.Engine_BabelDatabase? Strings { get; init; }

	public bool OptionsPageOpen { get; init; }

	/// <summary>Whether the rows on screen are the host's rather than the guest's pool.</summary>
	public bool HostRows { get; init; }
}

/// <summary>
/// ONE SHAPE FOR EVERY DETAIL PAGE.
///
/// A screen used to be six cases: `Owns`, `Values`, `Textures`, `Active`, `Positions` and
/// `Slides` each switched on the screen id in `Engine_UiDetailPages`, so a screen's behaviour was
/// spread over six places and every screen could be wrong in a different way. A provider is that
/// screen's whole answer, and the template names it — so adding a screen is a template record,
/// and a fault in the shared fill breaks every screen identically instead of one obscurely.
///
/// Every member has a default, so a provider states only what its screen actually has.
/// </summary>
public interface Engine_UiPageProvider
{
	/// <summary>Field values as final display text, by dotted element path.</summary>
	IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page) =>
		Engine_UiPageProviders.NoValues;

	/// <summary>Quads the provider textures itself, by element path to texture id.</summary>
	IReadOnlyDictionary<string, uint> Textures(in Engine_UiPageContext page) =>
		Engine_UiPageProviders.NoTextures;

	/// <summary>Elements the provider switches on or off outright.</summary>
	IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page) =>
		Engine_UiPageProviders.NoActive;

	/// <summary>
	/// Elements whose OPACITY the provider states outright, 0..1.
	///
	/// The last resort, and the only one that reaches an element AUTHORED at zero. A value or a
	/// switch makes the resolve fall back to the slide/authored opacity, which is enough for
	/// something the guest merely slid dark — but the co-op screen's two `ProfileSelector.
	/// Buttons` groups are authored at 0, so nothing that only DROPS the guest's value can ever
	/// light them, and their children (the X and Y prompts, the profile titles) inherit the
	/// zero. Stating the number is the only way to own it.
	///
	/// <see cref="float.NaN"/> RELEASES instead: the guest's stated number is dropped and the
	/// element takes its slide's or authored opacity. A `.*` suffix claims the subtree either way.
	/// </summary>
	IReadOnlyDictionary<string, float> Opacities(in Engine_UiPageContext page) =>
		Engine_UiPageProviders.NoOpacities;

	/// <summary>Elements the provider places itself.</summary>
	IReadOnlyDictionary<string, Vector3> Positions(in Engine_UiPageContext page) =>
		Engine_UiPageProviders.NoPositions;

	/// <summary>Components the provider switches to a stated slide.</summary>
	IReadOnlyDictionary<string, int> Slides(in Engine_UiPageContext page) =>
		Engine_UiPageProviders.NoSlides;

	/// <summary>Elements the provider TURNS, as a stated `rotation.z` in radians — the world map's
	/// minimap image, which the guest rotates by `MinimapDegreesRotationToNorth`.</summary>
	IReadOnlyDictionary<string, float> Rotations(in Engine_UiPageContext page) =>
		Engine_UiPageProviders.NoRotations;

	/// <summary>
	/// A page with no list and no screen of its own — a box page — whose book is filled each time
	/// it draws (<see cref="Engine_UiDetailPages.FillOnDraw"/>) rather than by the screen feeds.
	/// </summary>
	bool FillsOnDraw => false;

	/// <summary>Elements this page tints (the quad's vertex colour, `sub_82425C58`) — see
	/// <see cref="Engine_UiOwnedBook.HostTints"/>.</summary>
	IReadOnlyDictionary<string, Godot.Color> Tints(in Engine_UiPageContext page) =>
		Engine_UiPageProviders.NoTints;

	/// <summary>
	/// Whether this screen's pages draw the option levels (`HostBridge.GetOptionSliders`). Only
	/// the screen that has them says so.
	/// </summary>
	bool NeedsOptionSliders => false;

	/// <summary>
	/// A chance to ask the host for what this screen's fill will need, before it is filled.
	///
	/// The world map is why this exists: the region pump samples on its own thread and answers a
	/// tick later, so naming the region now is what makes the NEXT frame's answer this cursor's.
	/// That was a screen-id branch in `GameUi`; it is the screen's own business.
	/// </summary>
	void Prepare(in Engine_UiPageContext page)
	{
	}
}
