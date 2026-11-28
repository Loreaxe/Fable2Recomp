using Godot;
using Refii.Host;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// PLAYER TWO'S TARGETED READOUT AND D-PAD on split screen. The guest's HUD holds one of each, for
/// the local hero; player two's half draws player one's pieces (Engine_HudLayout.EachPlayerOnSplit)
/// from the same reads and the same builders, called for hero two (host_bridge.cpp
/// RefiiHostGetPlayerTwoTargetedQuery, guest_tick_pump.cpp SamplePlayerTwoHud), stated over the
/// shared feed the way player one's publisher states it (<see cref="Engine_UiHudTargetedQuery"/>)
/// and folded into a resolve of player two's own (<see cref="ResolvePlayerTwo"/>).
/// </summary>
public partial class GameUi
{
	/// <summary>Player two's targeted-readout slides over the shared ones (ordinal → slide).</summary>
	private readonly System.Collections.Generic.Dictionary<int, int> _hudTwoQuerySlides = new();

	private static readonly Engine_HudValues.Handle TargetedTitle = Engine_HudValues.Handle.For("TargetedQueryTitle");
	private static readonly Engine_HudValues.Handle TargetedGiftMeter = Engine_HudValues.Handle.For("TargetedGift");
	private static readonly Engine_HudValues.Handle TargetedOpinionMeter = Engine_HudValues.Handle.For("TargetedQueryOpinion");

	private static readonly string[] TargetedToggles =
	{
		"TargetedQuery", "TargetedGift", "TargetedQueryButton", "TargetedDivorce", "TargetedMarry",
	};

	/// <summary>
	/// The d-pad's four slot elements in the guest's slot order — up, left, down, right
	/// (`sub_823D34B8`: 0x83497C84, 0x83497C8C, 0x83497C88, 0x83497C90, named at static init).
	/// </summary>
	private static readonly string[] DpadSlotPaths =
	{
		"dpad.dpad.dpadmenu.group.up.up", "dpad.dpad.dpadmenu.group.left.left",
		"dpad.dpad.dpadmenu.group.down.down", "dpad.dpad.dpadmenu.group.right.right",
	};

	/// <summary>
	/// The d-pad's per-slot toggles in the same slot order. Hero one's are stated by the shipped
	/// scripts (`GUI.ToggleElement`) for the one HUD; nothing states them for hero two, so player
	/// two's slot is on while their fill gives it art.
	/// </summary>
	private static readonly string[] DpadSlotToggles = { "DPadUp", "DPadLeft", "DPadDown", "DPadRight" };

	/// <summary>Player two's d-pad slot toggles over the shared ones (ordinal → slide).</summary>
	private readonly System.Collections.Generic.Dictionary<int, int> _hudTwoDpadSlides = new();

	private Engine_UiElementResolve? _hudTwoResolve;
	private Engine_UiScreen? _hudTwoResolveScreen;
	private bool[]? _hudTwoActiveIn;
	private float?[]? _hudTwoStatedIn;
	private bool[]? _hudTwoActive;

	private int[][]? _dpadSlotOrdinals;
	private Engine_UiScreen? _dpadSlotScreen;
	private string _twoTitle = string.Empty;
	private float _twoGift;
	private bool _twoHudStated;

	/// <summary>Player two's own statement for their half, held through hybrid's swap (Fable2PlayerTwoHudHold).</summary>
	private Fable2PlayerTwoHudHold<Fable2Bridge.PlayerTwoHud> _twoHudHold;

	/// <summary>No statement yet for player two's half: no title, every d-pad slot empty.</summary>
	private static readonly Fable2Bridge.PlayerTwoHud NothingStatedTwo =
		new(0u, string.Empty, new[] { string.Empty, string.Empty, string.Empty, string.Empty });

	private void DrivePlayerTwoHud()
	{
		var overlay = _hudTwo?.Values;

		// Player two's half stands while two views are drawn — through all of hybrid's swap, though the
		// guest's builders state hero two's d-pad and title only while it is told the screen is split.
		// The half keeps its own last statement until it is parked; nothing in it falls back to the
		// shared HUD's, which are player one's.
		bool drawn = Refii.Core.Engine_Views.Split && CoopSession.LocalCoop && overlay?.Under is not null &&
			_hud?.Bindings is not null;
		Fable2Bridge.PlayerTwoHud? hud = _twoHudHold.Take(drawn, drawn ? Fable2Bridge.GetPlayerTwoHud() : null);
		if (!drawn || _hud?.Bindings is not { } bindings || overlay is null)
		{
			if (_twoHudStated)
			{
				_twoHudStated = false;
				_hudTwoQuerySlides.Clear();
				ClearDpadTwo();
				overlay?.Override(TargetedTitle, null);
				overlay?.OverrideMeter(TargetedGiftMeter, null);
				overlay?.OverrideMeter(TargetedOpinionMeter, null);
			}

			return;
		}

		_twoHudStated = true;
		Fable2Bridge.PlayerTwoHud stated = hud ?? NothingStatedTwo;
		HostBridge.TargetedQuery query = Fable2Bridge.GetPlayerTwoTargetedQuery();
		bool up = query.Known && query.Present;

		// The toggles and the fader as `Engine_UiHudTargetedQuery` states them for player one: a
		// toggle folds to 2 on and 1 off, `TargetedQuery`'s SlideFader2 to 2 in and 3 out, the slide
		// set verbatim.
		void Toggle(string name, bool on)
		{
			foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideToggle, Refii.Core.Engine_GameHash.Fnv1(name)))
			{
				_hudTwoQuerySlides[ordinal] = on ? 2 : 1;
			}
		}

		foreach (string name in TargetedToggles)
		{
			Toggle(name, name switch
			{
				"TargetedQuery" => up,
				"TargetedGift" => up && query.Gift,
				"TargetedQueryButton" => up && query.Button,
				_ => false,
			});
		}

		uint queryHash = Refii.Core.Engine_GameHash.Fnv1("TargetedQuery");
		foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideFader2, queryHash))
		{
			_hudTwoQuerySlides[ordinal] = up ? 2 : 3;
		}

		if (up && query.Entity != 0u && query.SlideSet > 0)
		{
			foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideSet, queryHash))
			{
				_hudTwoQuerySlides[ordinal] = query.SlideSet;
			}
		}

		// The title and meters stay as last stated while the readout fades out, as the guest's do.
		if (up && stated.Target != 0u)
		{
			_twoTitle = stated.Title;
			if (query.Gift)
			{
				_twoGift = query.GiftValue * 100f;
			}

			overlay.OverrideMeter(TargetedOpinionMeter, query.Opinion * 100f);
		}

		overlay.Override(TargetedTitle, _twoTitle);
		overlay.OverrideMeter(TargetedGiftMeter, _twoGift);

		DriveDpadTwo(stated.Icons, bindings);
	}

	/// <summary>
	/// Player two's d-pad icons onto the four slot elements of their half, and each slot's toggle:
	/// on (2) while the fill gave it art, off (1) when it gave none, as `sub_823FE848` folds a toggle.
	/// </summary>
	private void DriveDpadTwo(string[] icons, Engine_UiBindings bindings)
	{
		for (int i = 0; i < DpadSlotToggles.Length && i < icons.Length; ++i)
		{
			foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideToggle, Refii.Core.Engine_GameHash.Fnv1(DpadSlotToggles[i])))
			{
				_hudTwoDpadSlides[ordinal] = icons[i].Length != 0 ? 2 : 1;
			}
		}

		int[][] slots = DpadSlotOrdinals();
		Refii.Textures.Engine_TextureNameTable? names = _presentation?.TextureNames;
		for (int i = 0; i < slots.Length && i < icons.Length; ++i)
		{
			uint texture = 0u;
			if (icons[i].Length != 0)
			{
				names?.Add(icons[i]);
				texture = Refii.Core.Engine_GameHash.ResourcePath(icons[i].ToLowerInvariant());
			}

			foreach (int ordinal in slots[i])
			{
				_hudTwoTextures[ordinal] = texture != 0 ? texture : _hudScreen?.Nodes[ordinal].Quad?.TextureHash ?? 0;
			}
		}

		// A mod's stated icons and slides for player two (hud_icon / hud_slide), over the fill's.
		ApplyHudOverridesTwo(bindings);
	}

	private void ClearDpadTwo()
	{
		_hudTwoDpadSlides.Clear();
		foreach (int[] slot in _dpadSlotOrdinals ?? System.Array.Empty<int[]>())
		{
			foreach (int ordinal in slot)
			{
				_hudTwoTextures.Remove(ordinal);
			}
		}
	}

	/// <summary>
	/// PLAYER TWO'S OWN RESOLVE. Player one's pass folds the guest's slides, active bits and stated
	/// opacity for the one HUD; player two's half takes that same input, folds player two's own
	/// slides over it (experience, targeted readout, d-pad) with the same owner release player
	/// one's stated slides take, and resolves it again — so a piece in their half is up and down as
	/// player two's state says, not player one's. False when player two states nothing of their
	/// own, and their half shares player one's resolve.
	/// </summary>
	private bool ResolvePlayerTwo()
	{
		if (_hudScreen is not { } doc || _hudResolve is not { } shared || _hudStated is not { } stated ||
			_hud?.LiveSlides is not { } live || live.Count != doc.Nodes.Length ||
			stated.Length != live.Count || shared.Active.Length != live.Count ||
			SlidesForPlayerTwo(live) is not int[] slides || ReferenceEquals(slides, live))
		{
			return false;
		}

		bool[] active = Buffer(ref _hudTwoActiveIn, live.Count);
		System.Array.Copy(shared.Active, active, live.Count);
		float?[] own = Buffer(ref _hudTwoStatedIn, live.Count);
		System.Array.Copy(stated, own, live.Count);
		Release(_hudTwoSlides);
		Release(_hudTwoQuerySlides);
		Release(_hudTwoDpadSlides);
		Release(_hudTwoOverrideSlides);
		ApplyHudHold(ref _hudHoldTwo, ref _hudHoldTwoKey, doc, slides, active, own, 2);

		if (_hudTwoResolve is null || !ReferenceEquals(_hudTwoResolveScreen, doc))
		{
			_hudTwoResolve = new Engine_UiElementResolve(doc);
			_hudTwoResolveScreen = doc;
		}

		// The stand's tracks and told writes, as player one's resolve takes them.
		_hudTwoResolve.Animate(shared.Animated, shared.Closed);
		_hudTwoResolve.Apply(slides, active, own, _hudStand?.SwitchedOn);
		bool[] drawn = Buffer(ref _hudTwoActive, live.Count);
		for (int i = 0; i < live.Count; ++i)
		{
			drawn[i] = _hudTwoResolve.Drawn[i] && _hudTwoResolve.Opacity[i] > 0.01f;
		}

		return true;

		void Release(System.Collections.Generic.Dictionary<int, int> overrides)
		{
			foreach ((int ordinal, int slide) in overrides)
			{
				if (ordinal >= 0 && ordinal < live.Count)
				{
					ReleaseHudOwnerSlide(doc, ordinal, slide, active, own);
				}
			}
		}
	}

	/// <summary>
	/// The quads each d-pad slot element paints: the named element when it carries a quad, else the
	/// leaf quads beneath it. Matched by trailing path segments, as the owned books match keys
	/// (the presentation's own root name may not be in the document's path).
	/// </summary>
	private int[][] DpadSlotOrdinals()
	{
		if (_dpadSlotOrdinals is not null && ReferenceEquals(_dpadSlotScreen, _hudScreen))
		{
			return _dpadSlotOrdinals;
		}

		_dpadSlotScreen = _hudScreen;
		_dpadSlotOrdinals = new int[DpadSlotPaths.Length][];
		var paths = new System.Collections.Generic.List<(string Path, UiNode Node)>();
		foreach (UiNode node in _hudScreen?.Nodes ?? System.Array.Empty<UiNode>())
		{
			var parts = new System.Collections.Generic.List<string>();
			for (UiNode? walk = node; walk is not null; walk = walk.Parent)
			{
				if (walk.HasAuthoredName)
				{
					parts.Add(walk.Name);
				}
			}

			parts.Reverse();
			paths.Add((string.Join('.', parts), node));
		}

		for (int i = 0; i < DpadSlotPaths.Length; ++i)
		{
			UiNode? found = null;
			string key = DpadSlotPaths[i];
			while (found is null && key.Length != 0)
			{
				foreach ((string path, UiNode node) in paths)
				{
					if (string.Equals(path, key, System.StringComparison.OrdinalIgnoreCase) ||
						path.EndsWith("." + key, System.StringComparison.OrdinalIgnoreCase))
					{
						found = node;
						break;
					}
				}

				int dot = key.IndexOf('.');
				key = found is null && dot >= 0 && key.IndexOf('.', dot + 1) >= 0 ? key[(dot + 1)..] : string.Empty;
			}

			var ordinals = new System.Collections.Generic.List<int>();
			if (found?.Quad is not null)
			{
				ordinals.Add(found.Ordinal);
			}
			else if (found is not null)
			{
				CollectLeafQuads(found, ordinals);
			}

			_dpadSlotOrdinals[i] = ordinals.ToArray();
			GD.Print($"[hud-two] d-pad slot {DpadSlotPaths[i]} → {(ordinals.Count == 0 ? "not found" : string.Join(",", ordinals))}");
		}

		return _dpadSlotOrdinals;
	}

	private static void CollectLeafQuads(UiNode node, System.Collections.Generic.List<int> into)
	{
		foreach (UiNode child in node.Children)
		{
			if (child.Children.Count == 0 && child.Quad is not null)
			{
				into.Add(child.Ordinal);
			}
			else
			{
				CollectLeafQuads(child, into);
			}
		}
	}
}
