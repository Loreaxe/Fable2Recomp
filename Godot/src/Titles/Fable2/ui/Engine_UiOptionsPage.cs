using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The options screen's pickboxes, owned — screen 18's pages 1 and 2.
///
/// A pickbox is a CHILD text element: `sub_828821D0` stores the chosen index and writes the
/// localised option into `&lt;pickboxPath&gt;.grp.text`, so every key here ends in that suffix.
///
/// Page 1's row labels are not ours: its five label globals only ever reach `sub_82426328`, the
/// opacity show/hide, and the captions are authored in `optionsscreen.bgf`. Pages 3 and 4 do
/// state theirs as Babel tags.
///
/// Pages 3 and 4 also ride a second channel: gamma and the three volumes are PIP POSITIONS, not
/// text — `sub_82881E38` writes `position.x = value * 440 - 210` onto `&lt;slider&gt;.grp.pip` —
/// so they go through <see cref="Engine_UiOwnedBook.HostPositions"/> while their labels and the
/// speaker-mode pickbox use the text channel.
///
/// The committed five are the levels the guest's own calls state (the profile's gamma getter and
/// setter, the audio manager's four setters — `HostBridge.GetOptionSliders`); an open page's pips
/// are its slider widgets' stated values.
/// </summary>
public static class Engine_UiOptionsPage
{
	/// <summary>The suffix every pickbox's text child carries.</summary>
	private const string TextChild = ".grp.text";

	private const string Subtitles = "Options.Subtitles_pickbox" + TextChild;

	private const string Breadcrumb = "Options.BreadcrumbSize_pickbox" + TextChild;

	private const string TutorialBoxes = "Options.TutorialBoxes_pickbox" + TextChild;

	private const string MultiplayerOrbs = "Options.MultiplayerOrbs_pickbox" + TextChild;

	private const string AutoJoinable = "Options.Auto_joinable_pickbox" + TextChild;

	private const string InvertCamera = "Page2.Contents.pickbox1" + TextChild;

	/// <summary>
	/// The two-state tags. OFF is index 0 — `sub_829BE2C0` pushes OFF before ON and selects on
	/// `value != 0`.
	/// </summary>
	private const string OffTag = "GUI_CONTROLS_OFF";

	private const string OnTag = "GUI_CONTROLS_ON";

	/// <summary>
	/// The breadcrumb row's captions by pickbox index: sizes 0, 0.5, 1, 1.5, 2 (`flt_8209B4A8`, the
	/// thresholds its populate `sub_829BE4C8` steps the index through).
	/// </summary>
	private static readonly string[] BreadcrumbTags =
	{
		"GUI_OPTIONS_BCRUMB_OFF",
		"GUI_OPTIONS_BCRUMB_LOW",
		"GUI_OPTIONS_BCRUMB_MEDIUM",
		"GUI_OPTIONS_BCRUMB_HIGH",
		"GUI_OPTIONS_BCRUMB_VERYHIGH",
	};

	/// <summary>Multiplayer orbs is three-state, not a toggle.</summary>
	private static readonly string[] OrbModes =
	{
		OffTag,
		"GUI_SCREEN_OPTIONS_ORBS_FRIENDS",
		"GUI_SCREEN_OPTIONS_ORBS_ALL",
	};

	/// <summary>Page 3's one slider, and page 4's three — the paths the guest writes.</summary>
	private const string Gamma = "Page3.Contents.slider1";

	private const string Sfx = "Page4.Contents.slider1";

	private const string Music = "Page4.Contents.slider2";

	private const string Speech = "Page4.Contents.slider3";

	/// <summary>The speaker-mode pickbox — page 4's only TEXT row.</summary>
	private const string Speaker = "Page4.Contents.pickbox1" + TextChild;

	/// <summary>The moving part of a slider. The pip is a CHILD, never the slider itself.</summary>
	private const string PipChild = ".grp.pip";

	/// <summary>
	/// The pip's travel, exactly as `sub_82881E38` computes it — `flt_8210177C` is 440.0 and
	/// `flt_82101778` is 210.0, so a full bar spans x -210..230 rather than being centred.
	/// </summary>
	private const float PipSpan = 440.0f;

	private const float PipOrigin = 210.0f;

	/// <summary>The slider row captions, and the speaker modes.</summary>
	/// <summary>
	/// The Babel tag each slider's caption carries, kept for when we can place them: the guest
	/// writes these into the slider WIDGET (`sub_829C08B8` and its two siblings), so they are not
	/// reachable by element path — see Values.
	/// </summary>
	private static readonly (string Path, string Tag)[] SliderLabels =
	{
		(Gamma, "GUI_SCREEN_OPTIONS_GAMMA"),
		(Sfx, "GUI_SCREEN_OPTIONS_SOUNDS"),
		(Music, "GUI_SCREEN_OPTIONS_MUSIC"),
		(Speech, "GUI_SCREEN_OPTIONS_VOICE"),
	};

	private static readonly string[] SpeakerModes =
	{
		"GUI_SCREEN_OPTIONS_AUDIO_TV",
		"GUI_SCREEN_OPTIONS_AUDIO_HIFI",
		"GUI_SCREEN_OPTIONS_AUDIO_SURROUND",
	};

	/// <summary>
	/// Where each slider's pip sits for the current settings — EMPTY, not zeroed, while the
	/// screen is shut: a pip at value 0 is a real reading, so stating nothing leaves the authored
	/// pip where it is.
	/// </summary>
	public static IReadOnlyDictionary<string, Godot.Vector3> Positions(
		Refii.Host.HostBridge.OptionSliders sliders)
	{
		var places = new Dictionary<string, Godot.Vector3>(4);
		if (!sliders.Known)
		{
			return places;
		}

		// The pips follow the slider WIDGETS' stated values (see Engine_UiOptionsPending).
		sliders = Engine_UiOptionsPending.Pending(sliders);

		Place(places, Gamma, sliders.Gamma);
		Place(places, Sfx, sliders.Sfx);
		Place(places, Music, sliders.Music);
		Place(places, Speech, sliders.Speech);
		return places;
	}

	/// <summary>
	/// One pip, at the guest's own arithmetic. `y` and `z` are stated too — `sub_824251E0` writes
	/// all three every time, y = 0 and z = -1; an x-only write leaves the pip behind the bar.
	/// </summary>
	private static void Place(
		Dictionary<string, Godot.Vector3> places, string slider, float value)
	{
		float clamped = value < 0f ? 0f : value > 1f ? 1f : value;
		places[slider + PipChild] =
			new Godot.Vector3((clamped * PipSpan) - PipOrigin, 0f, -1f);
	}

	/// <summary>The pickbox captions for the current settings, or blanks.</summary>
	/// <param name="pageOpen">
	/// True while the page level is open: only the open page's rows are painted. Otherwise every
	/// page's rows are.
	/// </param>
	/// <remarks>
	/// Every caption is the index the guest's pickbox STATED (`@PickBox.Set`). The screen's enter
	/// runs each page's populate (TU1 `sub_826D9D40` → `sub_82990610`, vtable +4 per page; disc
	/// `sub_82582968`), which sets every pickbox to the committed setting: Game `sub_829BE130`
	/// (disc `sub_828D7588`) — subtitles, breadcrumb, tutorials, orbs, auto-joinable — and Controls
	/// `sub_829BFCF0` (disc `sub_828D9268`) — invert. LEFT/RIGHT moves the open page's pickbox at
	/// once (`sub_828820B0`), A commits it (`sub_829BF698`) and B re-runs the populate, so outside an
	/// open page the stated index IS the committed setting.
	/// </remarks>
	public static IReadOnlyDictionary<string, string> Values(
		Refii.Host.HostBridge.OptionSliders sliders,
		Refii.Text.Engine_BabelDatabase? strings,
		bool pageOpen = false)
	{
		var values = new Dictionary<string, string>(11);
		if (!pageOpen)
		{
			values[Speaker] = string.Empty;
		}

		// THE FOUR SLIDER CAPTIONS ARE NOT OURS TO WRITE: `sub_829C08B8` hands the caption to a slider
		// WIDGET (`sub_82881EF0`), not to an element, so there is no `slider1.grp.text` to paint.

		if (pageOpen)
		{
			if (Engine_UiOptionsPending.Open)
			{
				switch (Engine_UiOptionsPending.Page)
				{
					case "Game":
						GamePage(values, strings, Engine_UiOptionsPending.IndexOf);
						break;
					case "Controls":
						values[InvertCamera] = Toggle(strings, Engine_UiOptionsPending.IndexOf(0));
						break;
					case "Audio":
						values[Speaker] = Pick(strings, SpeakerModes, Engine_UiOptionsPending.IndexOf(3));
						break;
				}
			}

			return values;
		}

		if (sliders.Known)
		{
			int mode = Engine_UiOptionsPending.Pending(sliders).Speaker;
			values[Speaker] = mode >= 0 && mode < SpeakerModes.Length
				? Babel(strings, SpeakerModes[mode])
				: string.Empty;
		}

		GamePage(values, strings, row => Engine_UiOptionsPending.StatedIndex("Game", row));
		values[InvertCamera] = Toggle(strings, Engine_UiOptionsPending.StatedIndex("Controls", 0));
		return values;
	}

	/// <summary>The Game page's five pickboxes, each at the index <paramref name="indexOf"/> answers for its row.</summary>
	private static void GamePage(
		Dictionary<string, string> values, Refii.Text.Engine_BabelDatabase? strings, Func<int, int> indexOf)
	{
		values[Subtitles] = Toggle(strings, indexOf(0));
		values[Breadcrumb] = Pick(strings, BreadcrumbTags, indexOf(1));
		values[TutorialBoxes] = Toggle(strings, indexOf(2));
		values[MultiplayerOrbs] = Pick(strings, OrbModes, indexOf(3));
		values[AutoJoinable] = Toggle(strings, indexOf(4));
	}

	private static string Toggle(Refii.Text.Engine_BabelDatabase? strings, bool on) =>
		Babel(strings, on ? OnTag : OffTag);

	/// <summary>A two-state row at a pickbox index; blank for an index the guest has not stated.</summary>
	private static string Toggle(Refii.Text.Engine_BabelDatabase? strings, int index) =>
		index < 0 ? string.Empty : Toggle(strings, index != 0);

	/// <summary>A pickbox's caption at its index; blank when unstated or past its table.</summary>
	private static string Pick(Refii.Text.Engine_BabelDatabase? strings, string[] tags, int index) =>
		index >= 0 && index < tags.Length ? Babel(strings, tags[index]) : string.Empty;

	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is not null && strings.TryGetString(tag, out string value) ? value : string.Empty;
}
