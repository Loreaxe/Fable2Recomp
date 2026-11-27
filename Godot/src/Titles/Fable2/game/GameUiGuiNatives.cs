using Godot;
using Refii.Core;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// The routing table for the UI cut: what Godot does with each of the guest's 120 `GUI.*`
/// natives.
/// </summary>
/// <remarks>
/// The cut is at the semantic natives the game VM calls, not at the Gameface DOM below them.
/// Every call arrives here with its arguments (docs/ui_native_cut.md) and is answered one of
/// three ways:
///
/// <list type="bullet">
/// <item><c>Register</c> — Godot owns it and draws the screen it affects.</item>
/// <item><c>Observe</c> — deliberately not owned, with the reason; the census counts these
/// apart from the unrouted.</item>
/// <item>Neither — the six natives whose implementation is a bare `blr`. They cannot arrive:
/// interception keys on the implementation pointer, all six share one, and the payload refuses
/// to record a stub.</item>
/// </list>
///
/// Taking a call does not suppress it: the guest's implementation still runs, so predicates
/// keep answering out of guest state, and the guest's own copy of every widget is still built.
///
/// Suppression, when a family is complete, must NOT repoint an implementation at `nullsub_1`.
/// The shared thunk hands the implementation `L` in r3 and returns whatever comes back as the
/// LUA RESULT COUNT (`82227d44 mr r3, r5`; `82227d50 bctrl`; r3 untouched to `blr`), so a bare
/// `blr` returns a pointer as a result count. Hook the THUNK instead (`sub_82227D28` for shape
/// (a), the per-arity thunks for shape (b)) and set `r3 = 0` — right for the 100 of 120 that
/// push no value.
///
/// The unit of suppression is a FAMILY, not a native: fifteen predicates read state the setters
/// beside them write, so a suppressed setter freezes its predicate. Owning the family is
/// necessary but not sufficient — the side effects must be inert too. Opening any display box
/// pushes a GameFlow command that suspends gameplay, the minigame tally opens a screen and runs
/// `GUI:OnEnter()`, the golf meter plays sounds, and the expression pip posts an event when a
/// second player is armed.
/// </remarks>
public partial class GameUi
{
	/// <summary>
	/// The routing table — which `GUI.*` natives Godot answers, and which it deliberately does
	/// not.
	/// </summary>
	/// <remarks>
	/// Built lazily because the surfaces it routes into are built lazily; registering against a
	/// null HUD would capture null in a closure and never recover. Argument orders and key
	/// hashes here are decompiled facts — see docs/ui_native_cut.md.
	/// </remarks>
	private void EnsureGuiNativeRoutes()
	{
		if (_guiRoutesReady || _hud is null)
		{
			return;
		}

		_guiRoutesReady = true;
		Fable2PageProviders.Widgets = _widgets;

		// The fade family is deliberately not routed: the host draws the pixels from the fader as
		// its one start `sub_823CE950` and its per-tick advance `sub_822C1FC0` state it
		// (screen_fade_stated.cpp) — progress `t`, from-colour, to-colour and the lock. The
		// on-screen colour is never stored; `sub_822C7970` lerps it per read.
		//
		// Four guest consumers hard-gate on it, two of which deadlock rather than glitch:
		//   `CmdLoadSaveGame_Update` (0x823D7778) stays in state 1 while it fades and does NOT
		//                            check the lock — a `t` that stops never completes.
		//   `CmdLevelChange_Update`  (0x823D5AA8) gates twice but honours the lock;
		//                            `LockScreenFade` is the shipped mid-fade escape hatch.
		//   `sub_8230E788`           skips its entire body until `IsScreenFullyFadedIn`.
		//   `sub_82311EE8`           routes the signed-out warning differently.
		//
		// Shipped bug not to reproduce: `FadeScreenOutToColour(r, g, b, seconds)` passes only
		// three colour bytes; the fourth (alpha) is whatever the caller's stack slot held.
		const string fade = "OWN THE PIXELS, NOT THE STATE — four guest consumers HARD-GATE on " +
			"the fade object at *(GUI+64), and two of them deadlock rather than glitch";
		foreach (string name in new[]
		{
			"FadeScreenIn", "FadeScreenOut", "FadeScreenOutToColour",
			"IsScreenFading", "IsScreenFullyFaded", "IsScreenFullyFadedIn",
			"IsScreenFadeLocked", "LockScreenFade", "UnlockScreenFade",
		})
		{
			_guiNatives.Observe(name, fade);
		}

		// `SetGUIText(element, tag)` — arg 1 is an element name, arg 2 a Babel tag; only the
		// second is localised. The key is FNV-1 of the RAW element name, into the map at
		// `elemMgr + 28`.
		_guiNatives.Register("SetGUIText", call =>
		{
			string element = call.Argument(0).Text;
			if (element.Length == 0)
			{
				return;
			}

			string display = Engine_UiGuiText.Resolve(call.Argument(1).Text, _presentation?.Strings);
			_hud.Values.StateText(Engine_GameHash.Fnv1(element), display);
		});

		// `ToggleElement(element, visible)` — same key rule, no Babel path on either argument.
		// A missing second argument reads false, the guest's own convention.
		_guiNatives.Register("ToggleElement", call =>
		{
			string element = call.Argument(0).Text;
			if (element.Length != 0)
			{
				_hud.Values.StateVisible(Engine_GameHash.Fnv1(element), call.Argument(1).Boolean);
			}
		});

		RouteMeters();
		RouteDisplayBoxes();
		RouteTimers();

		// The platform keyboard (`@Keyboard.Open`, keyboard_ui.cpp): the villager card's rename and
		// the dog's naming ask it; Engine_UiPlatformKeyboard answers.
		Engine_UiPlatformKeyboard.Register(_guiNatives);
		RoutePredicates();
		RouteNotYetOwned();
	}

	/// <summary>The two meter natives — see the section note.</summary>
	private void RouteMeters()
	{
		// ---- THE METERS ---------------------------------------------------------------------
		//
		// Most of the HUD's meters travel on this channel, not on the registry `GetHudMeters`
		// polls: only `PlayerHealth` and `PlayerHealthMax` persist there through gameplay.
		//
		// One implementation, two units: `SetMeterPercentOverTime` (`sub_823069A8`) takes an
		// integer percent and tail-calls the ratio form with `percent * 0.01`. Both are
		// converted to the 0..100 the rest of the HUD speaks, on the way in.
		//
		// Key is `Fnv1(rawElementName)` (basis 0x811C9DC5, `sub_82306928`), into the store at
		// `*(*(GUI+32)+108)` — the same key `SetGUIText` and `ToggleElement` use.
		// Argument 3 is the seek time in seconds.
		_guiNatives.Register("SetMeterRatioOverTime", call =>
		{
			string element = call.Argument(0).Text;
			if (element.Length != 0)
			{
				_hud?.Values.StateMeter(
					Engine_GameHash.Fnv1(element), call.Argument(1).Number * 100f,
					call.Argument(2).Number);
			}
		});

		_guiNatives.Register("SetMeterPercentOverTime", call =>
		{
			string element = call.Argument(0).Text;
			if (element.Length != 0)
			{
				_hud?.Values.StateMeter(
					Engine_GameHash.Fnv1(element), call.Argument(1).Number,
					call.Argument(2).Number);
			}
		});

	}

	/// <summary>Boxes, counters and the two table natives.</summary>
	private void RouteDisplayBoxes()
	{
		// ---- THE DISPLAY BOXES -------------------------------------------------------------
		//
		// Each takes a tag plus substitutions: `sub_82302270` resolves the tag, then walks the
		// remaining stack slots as `%1`, `%2`, … — numbers integer-formatted, strings resolved
		// as tags in their own right. `Engine_UiGuiText` implements that rule.
		_guiNatives.Register("DisplayMessageBox", call =>
			_boxes.MessageBox(Localise(call, 0)));

		// The MessageBox CLASS stating itself, not a native: its Open and Close are hooked
		// (hooks/display_box_hooks.cpp) because most boxes of the class are built natively and
		// never pass a `GUI.*` call. Argument 0 is the box's address, argument 1 its identity.
		_guiNatives.Register("@MessageBox.Open", call =>
		{
			if (GuestAddress(call) is { } box)
			{
				string body = call.Argument(2).Text ?? string.Empty;
				_boxes.GuestMessageOpened(box, call.Argument(1).Text, body);
				Engine_UiDisplayBoxes.Opened(box, Engine_UiDisplayBoxes.Message, call.Argument(1).Text, body,
					System.Array.Empty<string>());
			}
		});

		// The display boxes' classes stating themselves (display_box_hooks.cpp): box address, name,
		// body, kind — the class's own type: 2 query, 3 numeric adjuster, 4 quest box, 5 the
		// intermission strip, 6 acceptance, 7 button, 8 the hold-to-accept card — then the class's
		// own fields (Engine_UiDisplayBoxes). Acceptance and button open through
		// `g_DisplayBox:OpenAsAcceptanceBox()`, whose title is `GUI_ACCEPTANCE_BOX`.
		_guiNatives.Register("@DisplayBox.Open", call =>
		{
			if (GuestAddress(call) is not { } box || !int.TryParse(call.Argument(3).Text, out int kind))
			{
				return;
			}

			string body = call.Argument(2).Text ?? string.Empty;
			var fields = new System.Collections.Generic.List<string>();
			for (int i = 4; i < call.Arguments.Count; ++i)
			{
				fields.Add(call.Argument(i).Text ?? string.Empty);
			}

			Engine_UiDisplayBoxes.Opened(box, kind, call.Argument(1).Text ?? string.Empty, body, fields);
			if (kind == 5)
			{
				_surfaces.Intermission.Opened(box, kind, body);
			}
			else if (kind is 6 or 7)
			{
				_surfaces.DisplayBox.Opened(box, kind, body,
					Engine_UiGuiText.Resolve("GUI_ACCEPTANCE_BOX", _presentation?.Strings));
			}
		});

		_guiNatives.Register("@DisplayBox.Close", call =>
		{
			if (GuestAddress(call) is { } box)
			{
				Engine_UiDisplayBoxes.Closed(box);
				_surfaces.Intermission.Closed(box);
				_surfaces.DisplayBox.Closed(box);
			}
		});

		// The menu box's populate stating it (display_box_hooks.cpp, THE MENU BOX): the controller,
		// the title as the populate wrote it, cancelable, the Orb's player card, then each row's tag
		// prefixed `+` enabled or `-` disabled — resolved as the populate resolves it
		// (`sub_8230E0E8`). Restated on a row's enable; the close states the answer it posted.
		_guiNatives.Register("@MenuBox.Open", call =>
		{
			if (GuestAddress(call) is not { } box)
			{
				return;
			}

			var options = new System.Collections.Generic.List<Engine_UiGuiBoxes.MenuOption>();
			for (int i = 4; i < call.Arguments.Count; ++i)
			{
				if (Engine_UiDisplayBoxes.Row(call.Argument(i).Text ?? string.Empty) is { } row)
				{
					options.Add(new Engine_UiGuiBoxes.MenuOption(
						Engine_UiGuiText.Resolve(row.Tag, _presentation?.Strings), row.Enabled));
				}
			}

			Engine_UiDisplayBoxes.MenuOpened(box,
				new Engine_UiGuiBoxes.MenuBoxModel(call.Argument(1).Text ?? string.Empty, options,
					call.Argument(2).Text == "1"),
				call.Argument(3).Text == "1");
		});

		_guiNatives.Register("@MenuBox.Select", call =>
		{
			if (GuestAddress(call) is { } box && int.TryParse(call.Argument(1).Text, out int index))
			{
				Engine_UiDisplayBoxes.MenuSelected(box, index);
			}
		});

		_guiNatives.Register("@MenuBox.Close", call =>
		{
			if (GuestAddress(call) is { } box)
			{
				GD.Print($"[game-ui] menu box 0x{box:X8} closed by the guest — answer {call.Argument(1).Text}");
				Engine_UiDisplayBoxes.MenuClosed(box);
			}
		});

		// The registry slide writers (gui_registry_hooks.cpp): registry index, name hash, slide,
		// and whether the write played it.
		_guiNatives.Register("@Gui.Slide", call =>
		{
			if (int.TryParse(call.Argument(0).Text, out int registry) &&
				uint.TryParse(call.Argument(1).Text, out uint hash) &&
				int.TryParse(call.Argument(2).Text, out int slide))
			{
				_hud?.Values.StateRegistrySlide(registry, hash, slide, call.Argument(3).Text == "1");
			}
		});

		// The TopBox composer's front record, its hidden write and its texts (gui_registry_hooks.cpp).
		_guiNatives.Register("@Hud.TopBoxFront", call =>
		{
			var words = new uint[8];
			for (int i = 0; i < words.Length; ++i)
			{
				if (!uint.TryParse(call.Argument(i).Text, out words[i]))
				{
					return;
				}
			}

			Engine_UiHudTopBox.StateFront(
				words[0] != 0u, (int)words[1], words[2], words[3],
				words[4] != 0u, words[5] != 0u, words[6] != 0u, words[7] != 0u);
		});

		_guiNatives.Register("@Hud.TopBoxHidden", call =>
			Engine_UiHudTopBox.StateHidden(call.Argument(0).Text == "1"));

		_guiNatives.Register("@Gui.TextGroup", call =>
		{
			if (uint.TryParse(call.Argument(1).Text, out uint element))
			{
				Engine_UiHudTopBox.StateText(element, call.Argument(2).Text ?? string.Empty);
			}
		});

		// The targeted readout's publisher stating its entity (gui_registry_hooks.cpp), 0 for none.
		_guiNatives.Register("@Hud.Targeted", call =>
		{
			if (uint.TryParse(call.Argument(0).Text, out uint entity))
			{
				Engine_UiHudTargetedQuery.Entity = entity;
			}
		});

		_guiNatives.Register("@Gui.Image", call =>
		{
			if (uint.TryParse(call.Argument(0).Text, out uint hash) &&
				uint.TryParse(call.Argument(1).Text, out uint texture))
			{
				_hud?.Values.StateRegistryImage(hash, texture);
			}
		});

		// The modal router's opener stating its box: kind, modal id, text (display_box_hooks.cpp); an
		// adjuster then its value, min, max, step, flags and its widget, hex.
		_guiNatives.Register("@Modal.Open", call =>
		{
			if (uint.TryParse(call.Argument(0).Text, out uint kind) &&
				uint.TryParse(call.Argument(1).Text, out uint id))
			{
				Engine_UiModalText.Stated(kind, id, call.Argument(2).Text ?? string.Empty);
				if (kind == 5u && call.Arguments.Count >= 9 &&
					int.TryParse(call.Argument(3).Text, out int value) &&
					int.TryParse(call.Argument(4).Text, out int min) &&
					int.TryParse(call.Argument(5).Text, out int max) &&
					int.TryParse(call.Argument(6).Text, out int step) &&
					int.TryParse(call.Argument(7).Text, out int flags) &&
					uint.TryParse(call.Argument(8).Text, System.Globalization.NumberStyles.HexNumber,
						System.Globalization.CultureInfo.InvariantCulture, out uint widget))
				{
					Engine_UiModalText.StatedAdjuster(id, value, min, max, step, flags, widget);
				}
			}
		});

		// The adjuster widget's render `sub_826B8BB0` stating what it shows (display_box_hooks.cpp):
		// widget, value, min, max, step, flags.
		_guiNatives.Register("@Adjuster.Render", call =>
		{
			if (GuestAddress(call) is { } widget &&
				int.TryParse(call.Argument(1).Text, out int value) &&
				int.TryParse(call.Argument(2).Text, out int min) &&
				int.TryParse(call.Argument(3).Text, out int max) &&
				int.TryParse(call.Argument(4).Text, out int step) &&
				int.TryParse(call.Argument(5).Text, out int flags))
			{
				Engine_UiNumericRender.Stated(widget, value, min, max, step, flags);
			}
		});

		// The Orb box's player card stating its name `sub_823DB850` r5 (display_box_hooks.cpp): the
		// menu-box controller and the name.
		_guiNatives.Register("@GamerCard.Name", call =>
		{
			if (GuestAddress(call) is { } box)
			{
				Engine_UiDisplayBoxes.GamerCardNamed(box, call.Argument(1).Text ?? string.Empty);
			}
		});

		_guiNatives.Register("@MessageBox.Close", call =>
		{
			if (GuestAddress(call) is { } box)
			{
				_boxes.GuestMessageClosed(box, call.Argument(1).Text);
				Engine_UiDisplayBoxes.Closed(box);
			}
		});

		// A box the core co-op Lua built for one player on split screen (display_box_hooks.cpp):
		// argument 0 the box's address, argument 1 the player. It stands in their half, on their pad.
		_guiNatives.Register("@Box.Seat", call =>
		{
			if (GuestAddress(call) is { } box && int.TryParse(call.Argument(1).Text, out int player) &&
				player is 1 or 2)
			{
				_boxes.SeatBox(box, player);
			}
		});

		// Time Control's dial as its draw was given it (slow_time_dial_stated.cpp): the record's
		// address, the alpha, then eight words per segment; its close states it down.
		_guiNatives.Register("@SlowTime.Draw", call =>
		{
			if (GuestAddress(call) is not { } dial || call.Arguments.Count != 18)
			{
				return;
			}

			var words = new uint[17];
			for (int i = 0; i < words.Length; ++i)
			{
				if (!uint.TryParse(call.Argument(i + 1).Text, System.Globalization.NumberStyles.Integer,
					System.Globalization.CultureInfo.InvariantCulture, out words[i]))
				{
					return;
				}
			}

			_statedSlowTimeDials[dial] = words;
		});

		_guiNatives.Register("@SlowTime.End", call =>
		{
			if (GuestAddress(call) is { } dial)
			{
				_statedSlowTimeDials.Remove(dial);
			}
		});

		// The aim reticule's setter stating which object a player aims with — not a native
		// (hooks/reticule_hooks.cpp). Arguments: player 0/1, the object's address, active. The
		// record's own active byte decides what is drawn; the object is kept for its aim point.
		_guiNatives.Register("@Reticule", call =>
		{
			if (call.Argument(0).Text is "0" or "1" &&
				uint.TryParse(call.Argument(1).Text, System.Globalization.NumberStyles.HexNumber,
					System.Globalization.CultureInfo.InvariantCulture, out uint aiming))
			{
				_reticuleObject[call.Argument(0).Text == "0" ? 0 : 1] = aiming;
			}
		});

		// `DisplayInfoBox` is the odd one: its id is a LITERAL 0, and no string hashes to 0, so
		// it is unreachable from RemoveDisplayBox by construction. Six seconds, fire and forget.
		_guiNatives.Register("DisplayInfoBox", call =>
			_boxes.InfoBox(Localise(call, 0)));

		// `DisplayInfoBoxEx(name, seconds, tag, …)` — the removable one. Arg 1 is an identity,
		// not a tag, and must not be localised; arg 2 is seconds (the guest stores ms).
		_guiNatives.Register("DisplayInfoBoxEx", call =>
			_boxes.InfoBoxEx(
				call.Argument(0).Text, call.Argument(1).Number, Localise(call, 2)));

		_guiNatives.Register("RemoveDisplayBox", call =>
			_boxes.Remove(call.Argument(0).Text));

		// `SetCounter(name, tag, %1, %2)` — arg 1 is the identity. A repeat call with the same
		// name updates in place and keeps the widget, as the guest's pool walk does. The values
		// are truncated, not rounded — the guest applies `fctiwz`.
		_guiNatives.Register("SetCounter", call =>
		{
			string name = call.Argument(0).Text;
			Engine_UiGuiBoxes.Counter counter = _boxes.SetCounter(
				name, call.Argument(1).Text,
				(int)call.Argument(2).Number, (int)call.Argument(3).Number);

			counter.Text = Engine_UiGuiText.Substitute(
				Engine_UiGuiText.Resolve(counter.Tag, _presentation?.Strings),
				new[] { counter.First.ToString(), counter.Second.ToString() });
		});

		// `RemoveCounter` and `RemoveTimer` are the SAME guest function (`sub_82305918`
		// registered under both names), so they share one pool and one namespace here too.
		_guiNatives.Register("RemoveCounter", call => _boxes.RemoveCounter(call.Argument(0).Text));
		_guiNatives.Register("RemoveTimer", call => _boxes.RemoveCounter(call.Argument(0).Text));

		// `DisplayReceivedItem(entity)` takes a userdata — a two-level entity handle — not a
		// value readable off the stack. Screen 8 is already owned by
		// Engine_UiReceiveItemsSurface, which reads the item from the screen the guest fills in.
		_guiNatives.Observe("DisplayReceivedItem",
			"its argument is an entity userdata, not a value; screen 8 is already owned by " +
			"Engine_UiReceiveItemsSurface");

		// ---- THE TWO TABLE NATIVES ---------------------------------------------------------
		//
		// `DisplayInfoBoxParams(table, tag, …)` reads eleven fields out of a Lua table through
		// `lua_getfield` (payload ABI 84). Arg 2 is the tag, not arg 1 — the guest's gate is
		// `(top - base) & ~7 < 0x10` and both branches read index 2 — so substitutions start
		// at arg 3.
		//
		// The guest's defaults are not uniform: `ShowAButton`/`ShowYButton` default TRUE while
		// every other boolean defaults false; `LifeTime` defaults to -1.0 (persistent); `Name`
		// to the literal "unnamed"; `AcceptText`/`HelpText` to the tags `GUI_DIALOG_ACCEPT` and
		// `GUI_DIALOG_HELP`.
		_guiNatives.Register("DisplayInfoBoxParams", call =>
		{
			Engine_UiModel.GuiNativeArgument table = call.Argument(0);
			string Tag(string field, string fallback) => Engine_UiGuiText.Resolve(
				table.FieldText(field, fallback), _presentation?.Strings);

			_boxes.InfoBoxParams(
				table.FieldText("Name", Engine_UiGuiBoxes.ParamsDefaultName),
				table.Fields is null ? string.Empty : Tag("Title", string.Empty),
				table.FieldNumber("LifeTime", -1f),
				Localise(call, 1),
				(int)table.FieldNumber("DisplayBoxStyle", 0f),
				new Engine_UiGuiBoxes.BoxButtons(
					ShowA: table.FieldBoolean("ShowAButton", true),
					HoldA: table.FieldBoolean("IsHoldAButton", false),
					HoldAReduceDialogue:
						table.FieldBoolean("IsHoldAButtonReduceDialogue", false),
					ShowY: table.FieldBoolean("ShowYButton", true),
					HoldY: table.FieldBoolean("IsHoldYButton", false),
					AcceptText: Tag("AcceptText", "GUI_DIALOG_ACCEPT"),
					HelpText: Tag("HelpText", "GUI_DIALOG_HELP")));
		});

		// `DisplayMenuBox(body, option…)` / `DisplayUncancelableMenuBox` fill the one menu-box
		// controller (`sub_82303BF0`: the rows by `sub_823DBA40`, a table row's `Enabled` by
		// `sub_823DBBD0`, cancelable at `+14`) and open it; the open's populate states what the box
		// holds (`@MenuBox.Open`) for this and every other opener — the Orb, the sleep menu.
		const string menuBox =
			"fills the menu box's rows; its populate states the box when it opens (@MenuBox.Open)";
		_guiNatives.Observe("DisplayMenuBox", menuBox);
		_guiNatives.Observe("DisplayUncancelableMenuBox", menuBox);

	}

	/// <summary>Timers, which share the counter pool.</summary>
	private void RouteTimers()
	{
		// ---- THE MOVIES ---------------------------------------------------------------------
		//
		// Five natives, one implementation: all are thunks into
		// `sub_823C0D90(player, name, gendered, localised, sound)` differing in three `li`
		// immediates. They play the SAME `.bik` and choose a different audio track within it —
		// 1 male / 2 female for en-uk, 3 and 4 for any other language with a mounted `videos`
		// package. Worth taking even though the host already plays by name: `player+8` is
		// written synchronously inside the Lua call, before playback waits on a fade and a
		// gameflow push, and the flags carry the track, which no filename can.
		//
		// Playback is deliberately NOT taken over: the guest's movie state machine advances
		// 3->4 off the Bink "finished" byte (`823c18d0 lbz r11, 0xDD`), so a host owning
		// playback would leave gameflow state 10 pushed and `IsMoviePlaying` true for ever.
		void Movie(Engine_UiModel.GuiNativeCall call, bool gendered, bool localised, bool sound)
		{
			string name = call.Argument(0).Text;
			if (name.Length == 0)
			{
				return;
			}

			Engine_UiGuiMovie.Request request = _movies.State(name, gendered, localised, sound);
			GD.Print($"[game-ui] the guest asked for a movie: {_movies.Describe()} " +
				$"(request {_movies.Asked})");
			_ = request;
		}

		_guiNatives.Register("PlayMovieInternal",
			call => Movie(call, gendered: false, localised: false, sound: true));
		_guiNatives.Register("PlaySilentMovieInternal",
			call => Movie(call, gendered: false, localised: false, sound: false));
		_guiNatives.Register("PlayLocalisedMovieInternal",
			call => Movie(call, gendered: false, localised: true, sound: true));
		_guiNatives.Register("PlayMovieWithGenderedVoiceInternal",
			call => Movie(call, gendered: true, localised: false, sound: true));
		_guiNatives.Register("PlayLocalisedMovieWithGenderedVoiceInternal",
			call => Movie(call, gendered: true, localised: true, sound: true));

		// `SetDPadCenter(bool)` does two things: it stores the bool at `dpadObject+0x22`, whose
		// one reader (`sub_8299EC60` at 0x8299ecd0) nudges a `Group.Text` element's Y and is
		// inert once Godot draws the ring; and it fires a Gameface logic event on the
		// `UIContract` presentation — `setCenterOn`/`setCenterOff`, `dword_83497B78` /
		// `dword_83497B7C`, hashes initialised at 0x8324CF9C — which is the visible effect.
		_guiNatives.Register("SetDPadCenter", call =>
			_hud?.Values.StateDpadCentered(call.Argument(0).Boolean));

		// ---- THE TIMERS ---------------------------------------------------------------------
		//
		// One pool with the counters, because the guest has one: `RemoveTimer` and
		// `RemoveCounter` are the same function (`sub_82305918`) under two names.
		//
		// `SetTimer(name, seconds)` carries no text — `sub_82309540` marshals exactly
		// (string, number) — so the widget draws the clock alone.
		_guiNatives.Register("SetTimer", call =>
			_boxes.SetTimer(call.Argument(0).Text, string.Empty,
				call.Argument(1).Number, countsUp: false));

		_guiNatives.Register("PauseTimer", call =>
			_boxes.HoldTimer(call.Argument(0).Text, paused: true));

		_guiNatives.Register("PlayTimer", call =>
			_boxes.HoldTimer(call.Argument(0).Text, paused: false));

		// Both text forms are `(name, seconds, tag)`, shape (a) — two-instruction thunks into one
		// body differing by a literal: 0 for `SetTimerWithText` (count down), 1 for
		// `SetCountUpTimerWithText` (count up). Arg 1 is an identity, hashed through
		// `Fnv1HashWithBasis(.., 0x811C9DC5)` at 0x82305868; arg 3 is a Babel tag, copied
		// verbatim and resolved at display time with `%1`/`%2` both fixed at 0. `SetTimer` is
		// the same function with an empty tag.
		_guiNatives.Register("SetTimerWithText", call =>
			_boxes.SetTimer(call.Argument(0).Text, call.Argument(2).Text,
				call.Argument(1).Number, countsUp: false));

		_guiNatives.Register("SetCountUpTimerWithText", call =>
			_boxes.SetTimer(call.Argument(0).Text, call.Argument(2).Text,
				call.Argument(1).Number, countsUp: true));

	}

	/// <summary>
	/// Would answer the predicates whose setters this file owns, instead of the guest.
	/// Nothing is suppressed or answered today.
	/// </summary>
	private void PublishPredicates()
	{
		Engine_UiSource source = Engine_UiSource.Current;

		// The rule: own the SETTERS and the SURFACE before answering a predicate. The
		// expression / job-fill family has no surface yet, so it runs in the guest and its
		// writes reach the HUD through the element channels. `_widgets` is the translation of
		// record.
		_ = source;
	}

	/// <summary>The 20 natives that return a value.</summary>
	private void RoutePredicates()
	{
		// ---- THE HEADLINE, THE TOP BOX AND THE JOB MULTIPLIER --------------------------------
		//
		// `DisplayHeadlineText(tag, seconds, mode)` — arg 3 is a boolean (`lua_toboolean`).
		// One headline at a fixed slot; no pool, no key. An unresolved tag puts up NOTHING
		// (the guest clears the duration and mode byte again), unlike the box family, which
		// falls back to showing the tag.
		_guiNatives.Register("DisplayHeadlineText", call =>
		{
			string tag = call.Argument(0).Text;
			string display = Engine_UiGuiText.Resolve(tag, _presentation?.Strings);

			// Resolve falls back to the literal, so a tag returning itself is a miss.
			bool resolved = display.Length != 0 &&
				!string.Equals(display, tag, System.StringComparison.Ordinal);
			_boxes.ShowHeadline(resolved ? display : string.Empty,
				call.Argument(1).Number, call.Argument(2).Boolean);
		});

		_guiNatives.Register("ClearHeadlineText", _ => _boxes.ClearHeadline());

		// `SetTopBoxHidden(hidden)` — Lua-truthy (`lua_toboolean`), not an int: the number 0
		// hides the box, because 0 is true in Lua.
		_guiNatives.Register("SetTopBoxHidden", call =>
			_boxes.SetTopBoxHidden(call.Argument(0).Boolean));

		// `SetJobMultiplierLevel(value)` — the value, not an index: printed with `%d` onto
		// `JobMultiplier.Group.Text` and stored verbatim. Which slide plays depends on how it
		// moved against the cached one — see the model, which the HUD draws (StateJobMultiplier).
		// The native still runs: an increase plays `SE_GUI_MULTIPLIER_UP` (0x823CAD14 / 0x82819834).
		_guiNatives.Register("SetJobMultiplierLevel", call =>
			_boxes.SetJobMultiplier((int)call.Argument(0).Number));

		_guiNatives.Register("TurnJobMulitplierOff", call =>
			_boxes.TurnJobMultiplierOff(call.Argument(0).Boolean));

		// `SetShouldDrawPlayerIcons(bool)` suppresses exactly two icons: the offscreen hero and
		// henchman markers (request types 52 and 53), the only records in the vector at
		// `elemMgr+100`. Nothing else in the world-icon manager reads the flag.
		_guiNatives.Register("SetShouldDrawPlayerIcons", call =>
			_hud?.Values.StatePlayerIconsDrawn(call.Argument(0).Boolean));

		// ---- THE EXPRESSION METER ------------------------------------------------------------
		//
		// OBSERVED, NOT OWNED. The meter is the guest's: `StartExpressionMeterPip` and the fill
		// starters write the object at `*(*(GUI+32)+44)`, the GUI tick advances the pip
		// (`sub_822ACBF8`), the guest's own A press judges it (`sub_823D21C0`) and the four
		// predicates answer from it. A host copy here judged nothing and answered nothing; the
		// HUD draws what the meter's own methods state (`@Hud.ExpressionMeter`) —
		// Engine_UiHudExpressionMeter.
		foreach (string meterNative in new[]
			{
				"TurnOnExpressionMeter", "TurnOffExpressionMeter", "StartExpressionMeterPip",
				"StartBarJobFill", "TurnOnExpressionStaticFill", "TurnOnExpressionDynamicFill",
				"SetLowFillColour", "SetMedFillColour", "SetHighFillColour",
				"SetFillColourPercentages",
			})
		{
			_guiNatives.Observe(meterNative,
				"the meter object is the guest's and is read back for drawing");
		}

		// ---- THE GOLF SWING METER ------------------------------------------------------------
		//
		// Six table keys, all angles: the names say pixels and percent, but the guest scales
		// them into a 1.1707964-radian span before storing. The defaults below are the guest's,
		// taken when a key is missing, nil, or the wrong type — no coercion on this path.
		_guiNatives.Register("DisplayGolfSwingMeter", call =>
		{
			Engine_UiModel.GuiNativeArgument table = call.Argument(0);
			_widgets.ConfigureGolf(
				table.FieldNumber("BarSizePixels", 1.0f),
				table.FieldNumber("SweetSpotMinSizePercentage", 0.02f),
				table.FieldNumber("SweetSpotInitialSizePercentage", 0.02f),
				table.FieldNumber("SweetSpotShrinkPixelsPerSecond", 0.05f),
				table.FieldNumber("SweetSpotMovementPixelsPerSecond", 1.0f),
				table.FieldBoolean("IsPressToClose", false));
		});

		_guiNatives.Register("CloseGolfSwingMeter", _ => _widgets.CloseGolf());

		// Golf is not suppressed: with `IsPressToClose` false (the common case) `Display` only
		// CONFIGURES, and the meter is opened later by the guest's own input hooks
		// (`sub_82405538` on press, `sub_82403DD0` on release), which the host has no route to.
		// Its sounds are reproduced host-side in Engine_UiGuiWidgets.

		// ---- THE MINIGAME TALLY --------------------------------------------------------------
		//
		// "Add" means add a ROW, not add to a total: the amount is assigned to the row this call
		// claims. Five rows; a sixth is dropped silently.
		// The native still runs: its `handler+40` latch gates the close and its EnterGuiMode is the
		// pause. Each tag is one Babel lookup, `sub_8230E0E8` / disc `sub_82301D40` (title 0x82306C4C /
		// 0x82908874, button 0x82306C34 / 0x8290885C).
		_guiNatives.Register("TurnOnMiniGameComplete", call =>
			_widgets.TurnOnMiniGame(
				Engine_UiGuiText.Resolve(call.Argument(0).Text, _presentation?.Strings),
				Engine_UiGuiText.Resolve(call.Argument(1).Text, _presentation?.Strings)));

		_guiNatives.Register("TurnOffMiniGameComplete", _ => _widgets.TurnOffMiniGame());

		_guiNatives.Register("AddAmountToMiniGameComplete", call =>
			_widgets.AddMiniGameAmount(
				Engine_UiGuiText.Resolve(call.Argument(0).Text, _presentation?.Strings),
				(int)call.Argument(1).Number));

		// ---- THE EPILOGUE, THE NEXT EPISODE AND THE LOADING TIPS -----------------------------
		//
		// `SetGoldRewardInEpilogueScreen(gold)` writes TWO elements at once, and the label
		// element is called `EpilogueRenown` while the tag it carries is GOLD. A zero blanks
		// both rather than printing "0".
		_guiNatives.Register("SetGoldRewardInEpilogueScreen", call =>
			_screens.SetEpilogueGold((int)call.Argument(0).Number,
				Engine_UiGuiText.Resolve("GUI_QUEST_COMPLETION_GOLD", _presentation?.Strings)));

		// The tag is resolved AT CALL TIME, not at draw — what the screen stores is already
		// localised text. Two slots; the third and later overwrite the second.
		_guiNatives.Register("AddItemRewardToEpilogueScreen", call =>
			_screens.AddEpilogueReward(
				Engine_UiGuiText.Resolve(call.Argument(0).Text, _presentation?.Strings)));

		// `TurnOnNextEpisode` — EIGHT arguments. 1..5 are Babel tags, 6 is a RESOURCE identity
		// (lowercased and resource-hashed, never Babel-resolved), 7 is ignored outright, and 8
		// is a number the screen keeps. Argument 5 doubles as the offer selector: the guest
		// compares it against the four `GUI_SCREEN_EPISODE_BUY_EPISODE_*` tags.
		_guiNatives.Register("TurnOnNextEpisode", call =>
		{
			string Tag(int index) =>
				Engine_UiGuiText.Resolve(call.Argument(index).Text, _presentation?.Strings);

			_screens.ShowEpisode(
				Tag(0), Tag(1), Tag(2), Tag(3), Tag(4),
				call.Argument(4).Text, call.Argument(5).Text,
				Engine_UiGuiText.Resolve("GUI_SCREEN_EPISODE_DONT_BUY", _presentation?.Strings));
		});

		_guiNatives.Register("TurnOffNextEpisode", _ => _screens.HideEpisode());

		// A tag GROUP name, not a tag — its members become the rotating tips.
		_guiNatives.Register("SetLoadingScreenTextTagGroupName", call =>
			_screens.SetLoadingTipGroup(call.Argument(0).Text));

		// ---- THE REMAINING BOXES AND TOASTS --------------------------------------------------
		//
		// `DisplayIntermissionScreen(tag, ...)` — six seconds hardcoded, then the usual 0.75 s
		// close fade. The `WithLifetime` form's leading number never reaches the local box: it
		// goes only into a co-op network message, so the two behave identically here.
		_guiNatives.Register("DisplayIntermissionScreen", call =>
			_boxes.InfoBoxEx(string.Empty, Engine_UiGuiBoxes.InfoBoxSeconds, Localise(call, 0)));

		_guiNatives.Register("DisplayIntermissionScreenWithLifetime", call =>
			_boxes.InfoBoxEx(string.Empty, Engine_UiGuiBoxes.InfoBoxSeconds, Localise(call, 1)));

		// `DisplayDownloadMessageBox(tag, buttonCaption, offerId)` — three strings; only the
		// first is localised, the caption is verbatim, the third is a decimal offer id. The
		// box is owned; the marketplace purchase is stated, not performed.
		_guiNatives.Register("DisplayDownloadMessageBox", call =>
		{
			Engine_UiGuiBoxes.Box box = _boxes.MessageBoxNamed(
				"DownloadMsgBox", Engine_UiGuiText.Resolve(
					call.Argument(0).Text, _presentation?.Strings));
			box.AcceptCaption = call.Argument(1).Text;
			box.MarketplaceOffer = call.Argument(2).Text;
		});

		// `DisplayReceivedMoney(amount)` / `DisplayReceivedDiscount(percent)` open the received-item
		// card (screen 8) on `ObjectInventoryGoldBag` / `ObjectInventoryDiscount` (`sub_823C44E8` /
		// `sub_823C45E8`): a pausing card that waits for its Done, stated by the card's own draw and
		// drawn by Engine_UiReceiveItemsSurface.
		const string receivedCard =
			"opens the received-item card (screen 8), which its own surface draws from the card's stated draw";
		_guiNatives.Observe("DisplayReceivedMoney", receivedCard);
		_guiNatives.Observe("DisplayReceivedDiscount", receivedCard);

		// `DisplayCornerBoxTradeItemInfo(name, ...)` is not a box: it publishes three corner-text
		// records keyed by the name and two suffixed variants. Persistent — no lifetime float,
		// so only its Remove takes it down.
		_guiNatives.Register("DisplayCornerBoxTradeItemInfo", call =>
		{
			string name = call.Argument(0).Text;
			if (name.Length == 0)
			{
				return;
			}

			_screens.StateText(name, Engine_UiGuiText.Resolve(
				call.Argument(1).Text, _presentation?.Strings));
			_screens.StateText(name + "TRADE_ITEM_TYPE_TITLE", Engine_UiGuiText.Resolve(
				"TEXT_DLC1_QUEST_QO750_TRADE_ITEM_TYPE", _presentation?.Strings));
			_screens.StateText(name + "ITEMS_OWNED_AND_NEEDED_COUNTER", Engine_UiGuiText.Resolve(
				"TEXT_DLC1_QUEST_QO750_TRADE_ITEM_NEEDED", _presentation?.Strings));
		});

		_guiNatives.Register("RemoveCornerBoxTradeItemInfo", call =>
		{
			string name = call.Argument(0).Text;
			if (name.Length != 0)
			{
				_screens.StateText(name, string.Empty);
				_screens.StateText(name + "TRADE_ITEM_TYPE_TITLE", string.Empty);
				_screens.StateText(name + "ITEMS_OWNED_AND_NEEDED_COUNTER", string.Empty);
			}
		});

		// `PlaySound(name)` — a sound by name, which is the whole contract.
		_guiNatives.Register("PlaySound", call => _screens.PlaySound(call.Argument(0).Text));

		// ---- THE PREDICATES -----------------------------------------------------------------
		//
		// Only 20 of the 120 push a value, and none is a `Display*` — so swallowing a `Display*`
		// cannot change script control flow.
		//
		// The first five answer from GAME state (the gameflow stack, a gameplay global, the
		// camera frustum), which no UI decision touches.
		const string gameState =
			"a predicate answered from GAME state (the gameflow stack, a gameplay global, or the " +
			"camera frustum) — nothing the host does can change its answer";

		// All three read the gameflow stack's top state id, through
		// `*(*(*(0x83496AB8+12)+0x80)+4)` and its vtable slot +4. Ids 7 = full cutscene,
		// 9 and 10 = movie, 1 and 8 = the pair the fade's hold gate also tests. They TRAP on an
		// empty gameflow stack rather than answering false.
		_guiNatives.Observe("IsFullCutscenePlaying", gameState + " (gameflow id 7)");
		_guiNatives.Observe("IsMoviePlaying", gameState + " (gameflow id 9 or 10)");
		_guiNatives.Observe("IsUIActive",
			"gameflow id 1 or 8, OR IsMoviePlaying, OR the movie player's own state — it reads " +
			"NO GUI-manager and no renderer state, so drawing the UI elsewhere cannot change it");
		_guiNatives.Observe("IsEntityOnScreen",
			"pure spatial — the camera frustum against the entity's bounds and its visibility " +
			"bit; owning it would mean reproducing the guest's camera exactly, for no gain");
		_guiNatives.Observe("IsDeathMingameEnabled",
			"a flat gameplay global, byte_83496B58 — nothing to do with the UI at all");

		// The other fifteen read the guest's OWN UI state, written by the natives beside them,
		// and stay correct only while those setters keep executing inside the guest. This is the
		// list of setters that may never be suppressed while their predicate is live.
		const string uiState =
			"a predicate over the guest's OWN UI state, written by the setters beside it — " +
			"correct only while those setters still run, so neither may be suppressed alone";
		// `IsCounterShown` cannot be answered here even though we own the counter pool: its
		// answer depends on its ARGUMENT, and the suppression lever holds one answer per native.
		_guiNatives.Observe("IsCounterShown",
			uiState + " — and its answer depends on the NAME it is passed, which one stored " +
			"answer per native cannot serve");

		// The box family cannot be suppressed: a box's appearance is ours, its modality is game
		// state. Opening one runs `sub_823D0A70(box, 0)`, which closes the quick wheel
		// (`sub_8236F340`), quiets two element-manager channels, stands the context D-pad down,
		// and calls `GameFlow_QueueCommand` — which suspends the incumbent gameflow state and is
		// why the game pauses under a dialogue.
		_guiNatives.Observe("IsDisplayBoxActive",
			"ORs the message-box manager at *(GUI+0x1C)+52 with a 12-channel vector at " +
			"*(GUI+0x20)+0x58 — and the family cannot be suppressed anyway, because opening a " +
			"box pushes a GameFlow command that suspends gameplay");

		_guiNatives.Observe("IsQuickMenuOpen",
			"a raw byte at *(GUI+0x1C)+0x23, returned UNNORMALISED so the Lua truth value is " +
			"the byte itself; its writers have not been located, so what would keep it correct " +
			"is unknown");

		// Two are already broken in the shipped TU: their only writers
		// (`SetTemporaryDeathScreenEnabled`, `ModifyTemporaryDeathScreenPercentage`) are stubs
		// in this build, so the fields they read — fade+16 and fade+20 — are never written.
		const string deadWriter =
			"reads a field whose ONLY writer is a stub in this build, so it answers the same " +
			"thing for ever — shipped behaviour, not ours";
		_guiNatives.Observe("IsTemporaryDeathScreenEnabled", deadWriter);
		_guiNatives.Observe("DidHeroSurviveTemporaryDeathScreen", deadWriter);

		// `GetFillPercent` requires the vector at `*(*(GUI+32)+32)+28` to hold at least two
		// elements and executes `__trap()` when it does not, so anything that stops the fill
		// natives running has to account for it first.
		_guiNatives.Observe("GetFillPercent",
			"returns a FLOAT, and executes __trap() if the fill vector was never populated — " +
			"the one predicate here that fails hard rather than answering wrong");

	}

	/// <summary>Everything refused, each with its reason.</summary>
	private void RouteNotYetOwned()
	{
		// ---- THE QUESTION BOXES --------------------------------------------------------------
		//
		// A question box returns nothing and stores nothing a host can read: its answer travels
		// as a MESSAGE EVENT the scripts collect with `MessageEvents.IsMessagePosted` — type 36
		// carrying 1 or 0 for a yes/no, 37 or 38 carrying the chosen number for the adjuster.
		// So the host draws the box and posts `BoxAction:<code>@<box>`, which runs the GUEST's
		// own handler and posts the message; closing our own copy would strand the caller.
		//
		// The natives only BUILD the box and queue it; the queue's tick shows it later through
		// its class's Open, which states it (`@DisplayBox.Open`, display_box_hooks.cpp) — the
		// body as the native resolved it, the adjuster's widget, the quest box's fields. The
		// calls state nothing the box does not.
		const string statedAtOpen =
			"builds and queues a display box; the box's own class Open states it when it shows " +
			"(@DisplayBox.Open, display_box_hooks.cpp)";
		foreach (string name in new[]
		{
			"DisplayYesNoQuestion", "DisplayPubGameYesNoQuestion", "DisplayQuestPrimaryQuestion",
			"DisplayNumericAdjusterParams", "DisplayQuestBoxParams",
		})
		{
			_guiNatives.Observe(name, statedAtOpen);
		}

		// The acceptance box has `li r3,0; blr` in both input slots — its whole interaction is
		// in the Gameface Lua behind `g_DisplayBox:OpenAsAcceptanceBox()`. The button box takes
		// input at another vtable slot as an input-phase record, and its enum is unresolved.
		// Their TEXT is the call's (`DisplayAcceptanceBox(name, text)`, `DisplayButtonBox(name,
		// text)`: name → ctrl+4, text → ctrl+8, which the open `sub_8299EE40` writes into
		// `FramePositioningGroup.Text`) — recorded for the display-box surface. Their INPUT stays
		// the guest's: the acceptance box's input slots are both `li r3,0; blr` (the interaction
		// is in the Gameface Lua), and the button box takes an input-phase record at vtable slot 4
		// whose 0x0B..0x0E enum is unresolved.
		_guiNatives.Register("DisplayAcceptanceBox", call =>
			Engine_UiStatedSurface.StatedDisplayText = Localise(call, 1));
		_guiNatives.Register("DisplayButtonBox", call =>
			Engine_UiStatedSurface.StatedDisplayText = Localise(call, 1));

		// The full-screen screens are already owned as screens, each with a surface driven by
		// the screen the guest stands up; the native still opens it.
		const string ownedScreen =
			"opens a screen that is ALREADY owned by its own surface — the native still opens " +
			"it, and routing this would stand a second copy up beside the first";
		foreach (string name in new[]
		{
			"DisplayCrimeScreen", "DisplayShopScreen", "DisplayRealEstateScreen",
			"DisplayReceivedItem",
		})
		{
			_guiNatives.Observe(name, ownedScreen);
		}

		// `SetLoadingScreenBackgroundName` is write-only in this build: its global's whole
		// cross-reference set is the setter, the save serializer and a static destructor. The
		// loading screen's frame comes from a different hardcoded global with no writer.
		_guiNatives.Observe("SetLoadingScreenBackgroundName",
			"stored and round-tripped through the savegame, and read by NOTHING — the loading " +
			"screen's frame comes from a different hardcoded global");

		// ---- THE ELEMENT FADES ----------------------------------------------------------------
		//
		// These are `GotoSlide` calls: each hashes the element name as `SetGUIText` does and
		// posts a slide index — 1 in, 2 out fast, 3 out slow on the `SlideFaderName` channel
		// (one higher on `SlideFader2Name`, whose elements carry an extra leading slide).
		// There is no duration to port: the timing is the slide record's own
		// `endTime - startTime` in the `.bgf` and the curve is its authored Bézier keyframes.
		void Fade(Engine_UiModel.GuiNativeCall call, int slide)
		{
			string element = call.Argument(0).Text;
			if (element.Length != 0)
			{
				_hud?.Values.StateFade(Engine_GameHash.Fnv1(element), slide);
			}
		}

		_guiNatives.Register("FadeElementIn", call => Fade(call, 1));
		_guiNatives.Register("FadeElementOutFast", call => Fade(call, 2));
		_guiNatives.Register("FadeElementOutSlow", call => Fade(call, 3));

		// Everything else, each with the reason it is not owned.
		var pending = new System.Collections.Generic.Dictionary<string, string>(
			System.StringComparer.Ordinal)
		{
			["ShowGDCUI"] =
				"a PERMANENT NO-OP in this build — it stores through byte_83496AEF, which is 0 in " +
				"the image and whose only cross-reference in the whole binary is that read, so the " +
				"store never executes",
			["DisplayDivorceMessage"] =
				"takes an ENTITY userdata, not a value — the same reason DisplayReceivedItem " +
				"cannot be routed off its arguments",
			["SetGhosting"] =
				"(screenId, itemName, ghosted) — it stages String1/Float1 and runs the chunk " +
				"`g_Menu:SetItemGhosting()`, which the ABI 82 chunk capture ALREADY carries, so " +
				"routing it here would apply the same statement twice",
			// THE D-PAD STATES ITSELF. Both natives run in the guest, and what they change on
			// screen reaches the HUD as the d-pad widget's own `GoToSlide` on
			// `dpad.dpad.dpadmenu` (`sub_823D35B0`, disc `sub_824072A0`: 2 open, 1 closed), a
			// headless element statement the HUD stand plays. Routing the natives as well would
			// state the ring twice.
			["SetHUDSuggestionMode"] =
				"writes the `HUDDPadMenu` widget's mode, `*(*(GUI+32)+56) = r3` " +
				"(`Lua_GUI_SetHUDSuggestionMode` 0x82306508, disc 0x82908130). Its readers " +
				"test `== 2`: the d-pad's open (`sub_823D2A68`, disc `sub_824067C0`) then opens " +
				"the ring even while disabled, and an expression start (`sub_82413BB8`) shuts " +
				"it. The open and the close are the widget's own GoToSlide on " +
				"`dpad.dpad.dpadmenu`, already drawn from its statement",
			["SetDPadEnabled"] =
				"writes `dpad+32` and calls `sub_823D2898` (`Lua_GUI_SetDPadEnabled` " +
				"0x82305FC8, disc 0x82907BF0 / `sub_824065F0`): an open or opening ring " +
				"(`dpad+24` 1 or 2) that is no longer enabled closes through `sub_823D2B50` " +
				"(disc `sub_824068A8`) — GoToSlide 1 on `dpad.dpad.dpadmenu`, already drawn " +
				"from its statement — and releases input blocks 185 and 186",
			["PlayFullCutscene"] =
				"pushes a gameflow command (id 7); the push SUSPENDS the incumbent state, which is " +
				"how starting a cutscene aborts a playing movie — a consequence a host reproducing " +
				"only the picture would lose",
			["StopFullCutscene"] =
				"sub_82318800(list, 7) — removes the cutscene state by id and resumes whatever it " +
				"suspended; the gameflow stack is the guest's and nothing host-side stands in for it",
			["SetInteractiveCutsceneMode"] =
				"one byte, `stb r3, 9(elemMgr)` at 0x8230615C — but NOTHING that reads it has been " +
				"found, so what owning it would change is unknown",
			["SetInteractiveCutsceneBorder"] =
				"one byte, `stb r3, 0xA(elemMgr)` at 0x82306174 — same as the mode byte beside it, " +
				"and with the same gap: no reader located",
			["AddWorldIcon"] = "only a request: the icons are stated at their own Init, Update and destructor " +
				"(world_icon_hooks.cpp, RefiiHostGetWorldSprites)",
			["AddWorldIconWithOffset"] = "only a request: the icons are stated at their own entries",
			["RemoveWorldIcon"] = "only a request: the icons are stated at their own entries",
			["FillWorldIconSetEyeIsOpen"] = "only a request: the icons are stated at their own entries",
			["SetExperienceShouldShowAbilities"] =
				"a BOOL at +0 of the `HUDExperience` widget (`stb r3, 0(r9)` at 0x82307510), not " +
				"a flag on any screen — the guest's save serialiser names it " +
				"`HUDExperience/ShouldShowAbilities`, so it also round-trips through the save. " +
				"It is NOT a dead write: `lbz r11, 0(r30)` at 0x822CF40C gates the " +
				"`HUDSuggestionLevelUp` publish, so clearing it suppresses the spend-experience " +
				"prompt. That prompt goes to binding registry 8 and we walk 1 and 6 only, so " +
				"there is nothing host-side for it to reach yet",
		};

		foreach ((string name, string reason) in pending)
		{
			_guiNatives.Observe(name, reason);
		}

		// The six stubs below all bind to one empty function — a bare `blr` — in this TU.
		// Interception keys on the implementation pointer, the only identity a call carries, so
		// a call through it cannot be attributed to any one of the six; the payload detects the
		// `blr` and records no name, which is why none appears in the census. They are named
		// anyway so that a build where the detection does not fire arrives explained. Fast
		// travel is dispatched as a world-map event, not through `DisplayFastTravelScreen`.
		const string stub =
			"its implementation is a bare `blr` in this build — the native does nothing, and " +
			"all six stubs share that one function so a call through it cannot be attributed";
		foreach (string name in new[]
		{
			"DisplayFastTravelScreen", "AddHealthBar", "RemoveHealthBar",
			"SetTemporaryDeathScreenEnabled", "ModifyTemporaryDeathScreenPercentage",
			"SetCutsceneVignette",
		})
		{
			_guiNatives.Observe(name, stub);
		}
	}


	/// <summary>A stated guest address — argument 0 of the `@` events, eight hex digits.</summary>
	private static uint? GuestAddress(Engine_UiModel.GuiNativeCall call) =>
		uint.TryParse(call.Argument(0).Text, System.Globalization.NumberStyles.HexNumber,
			System.Globalization.CultureInfo.InvariantCulture, out uint address) && address != 0u
			? address
			: null;

	/// <summary>
	/// A call's text argument, resolved and with its trailing arguments substituted.
	/// </summary>
	/// <remarks>
	/// The substitutions are every remaining argument in order, as `sub_82302270` does: numbers
	/// integer-formatted, strings resolved as tags in their own right.
	/// </remarks>
	private string Localise(Engine_UiModel.GuiNativeCall call, int textIndex)
	{
		string text = Engine_UiGuiText.Resolve(
			call.Argument(textIndex).Text, _presentation?.Strings);

		if (call.Arguments is not { Count: > 0 } arguments || arguments.Count <= textIndex + 1)
		{
			return text;
		}

		var values = new System.Collections.Generic.List<string>();
		for (int i = textIndex + 1; i < arguments.Count; ++i)
		{
			Engine_UiModel.GuiNativeArgument argument = arguments[i];
			values.Add(argument.Kind switch
			{
				// Integer-formatted, matching `sub_8230AE78`.
				Engine_UiModel.GuiNativeArgumentKind.Number => ((int)argument.Number).ToString(),
				Engine_UiModel.GuiNativeArgumentKind.String =>
					Engine_UiGuiText.Resolve(argument.Text, _presentation?.Strings),
				_ => argument.Text,
			});
		}

		return Engine_UiGuiText.Substitute(text, values);
	}

	// ---- THE JOB MULTIPLIER AND THE MINIGAME TALLY, DRAWN FROM THEIR NATIVES ---------------

	/// <summary>`hud.bgf`'s multiplier root and its text (`sub_823CAAD8`'s names `unk_83497B90` / `unk_83497B94`).</summary>
	private const string JobMultiplierRoot = "Layer.JobMultiplier";

	private const string JobMultiplierTextPath = "Layer.JobMultiplier.Group.Text";

	private Engine_UiHudStand? _jobStand;
	private int _jobRoot = -1;
	private int _jobText = -1;
	private uint _jobPresentation;
	private ulong _jobMoves;
	private int _jobSlide;

	/// <summary>
	/// The multiplier (`GUI+80+20`: +0 the root, +4 the text) stands on what `SetJobMultiplierLevel` and
	/// `TurnJobMulitplierOff` state (Engine_UiGuiBoxes): the root's slide and the text; the guest's own
	/// writes of them are dropped. The HUD's build (`sub_823CA428` 0x823CA5AC → `sub_823CAAD8`, disc
	/// `sub_828195F8`) enters the root on slide 4 (0x823CAB94 / 0x828196B4). The root's opacity —
	/// 0 at the build (0x823CABA0 / 0x828196C0), 100 from zero (`sub_823CABB0` 0x823CACCC / 0x828197EC)
	/// — is no slide's (the slides write `Group`) and stays the store's statement.
	/// </summary>
	private void StateJobMultiplier(Engine_UiHudStand stand)
	{
		if (!ReferenceEquals(stand, _jobStand))
		{
			_jobRoot = stand.Find(JobMultiplierRoot);
			_jobText = stand.Find(JobMultiplierTextPath);
			if (_jobRoot < 0 || _jobText < 0)
			{
				GD.PushWarning("[job-multiplier] hud.bgf has no Layer.JobMultiplier / Group.Text; the multiplier is not drawn");
			}

			stand.OwnSlide(_jobRoot);
			stand.OwnText(_jobText);
		}

		if (_jobRoot < 0 || _jobText < 0)
		{
			_jobStand = stand;
			return;
		}

		if (_hudAddress != _jobPresentation)
		{
			_jobPresentation = _hudAddress;
			_jobSlide = 4;
			stand.StateSlide(_jobRoot, _jobSlide);
		}
		else if (!ReferenceEquals(stand, _jobStand) && _jobSlide > 0)
		{
			stand.StateSlide(_jobRoot, _jobSlide);
			if (_boxes.JobMultiplierText.Length != 0)
			{
				stand.StateText(_jobText, _boxes.JobMultiplierText);
			}
		}

		_jobStand = stand;
		if (_boxes.JobMultiplierMoves == _jobMoves)
		{
			return;
		}

		_jobMoves = _boxes.JobMultiplierMoves;
		_jobSlide = _boxes.JobMultiplierSlide;
		stand.StateSlide(_jobRoot, _jobSlide);
		if (_boxes.JobMultiplierText.Length != 0)
		{
			stand.StateText(_jobText, _boxes.JobMultiplierText);
		}

		GD.Print($"[job-multiplier] level {_boxes.JobMultiplier}: slide {_jobSlide}, text '{_boxes.JobMultiplierText}'");
	}

	/// <summary>
	/// The game is gone and its in-game GUI with it (LeaveGame): the next game's GUI starts with no
	/// multiplier level and no tally rows.
	/// </summary>
	private void LeaveGuiNativeModels()
	{
		_boxes.ResetJobMultiplier();
		_jobPresentation = 0;
		_jobMoves = _boxes.JobMultiplierMoves;
		_jobSlide = 0;
		_widgets.TurnOffMiniGame();
	}

	/// <summary>The minigame tally's screen id (`li r4,0xB` in both builds' TurnOn, 0x823CDD00 / 0x8258CC38).</summary>
	private const uint MiniGameScreenId = 11u;

	private uint _tallyFrame;

	/// <summary>
	/// The tally steps once per guest frame (Engine_GuestFrame, one per HF pass): with the GUI active,
	/// as the tally's EnterGuiMode makes it, the game loop opens the GUI's tick window on every
	/// iteration, not only on game ticks (0x8227711C → `sub_821A1140` 0x821A11D8, disc 0x8227703C →
	/// `sub_821A11E8` 0x821A1280). It closes when screen 11 is seen leaving.
	/// </summary>
	private void StepMiniGameTally()
	{
		uint frame = Refii.Core.Engine_GuestFrame.Frame;
		uint frames = unchecked(frame - _tallyFrame);
		_tallyFrame = frame;
		if (!_widgets.MiniGameUp)
		{
			return;
		}

		_widgets.ObserveMiniGameScreen(Refii.Host.HostBridge.GetPauseState() is { ScreenId: MiniGameScreenId });
		if (!_widgets.MiniGameUp)
		{
			GD.Print("[minigame] screen 11 left: the tally closed");
			return;
		}

		_widgets.StepMiniGame(frames);
	}
}
