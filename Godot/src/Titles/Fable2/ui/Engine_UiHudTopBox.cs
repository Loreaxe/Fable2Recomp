namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The HUD TopBox's slide toggles and fader, re-derived from the box the host holds.
/// </summary>
/// <remarks>
/// The guest's TopBox manager (`*(*(GUI+32)+88)`) still runs — a taken `GUI.*` call is not
/// suppressed — so its publisher `sub_821787D0` writes the strings into TextName registry 6 and
/// the hold progress into MeterName registry 1, both of which the host reads. What it cannot
/// read are registry 9 (SlideToggleName) and registry 3 (SlideFader2Name), and those carry the
/// box's visibility: `TopBox`, `ButtonOnly`, `TopTitle`, `TopMain`, the six button toggles and
/// the fade. This states exactly the toggles that publisher computes, from the same record —
/// read out of the guest's manager (it also holds records no Lua native made, the quest
/// objective for one) — and the held flags from the pad,
/// which the guest's `sub_8230D408` flips on press (phase 13) and release (phase 15).
///
/// Channels: style 1 → HeroJobBoxes (1), style 2 → QuestAcceptanceBoxes (2), else ScriptInfo
/// (3); the front record is the LAST of the LOWEST non-empty channel (`sub_8230D4F0`).
///
/// WHEN A BOX ENDS, THE GUEST ERASES ITS RECORD — there is no "done" flag to read. The manager's
/// HUD tick `sub_822BD220` runs `sub_822CD618` → `sub_822CD6F0` over every channel: a record's
/// `+12` float is its remaining milliseconds, `*(float*)(rec+12) -= dt` (0x822cd74c), and at
/// `<= 0` the record is freed and erased in the same tick (0x822cd754–0x822cd79c); a record
/// pushed with `+12 <= 0` is skipped and never expires. A script's `GUI.RemoveDisplayBox` erases
/// by name hash (`sub_82304C48` → `sub_826B9AA8` → `sub_8230D0C0` → `sub_8230C130`, memmove at
/// 0x8230c1a8). A completed hold (`sub_8230D2E0` meter ≥ 1.0 → `sub_8230D548`) posts message
/// 34/101 (0x8230d61c) and leaves the record for the script to remove. So "the front record is
/// present and the manager is not hidden" IS the guest's own end condition, and this follows
/// it: present → up, gone → down, no manager → down.
/// </remarks>
public static class Engine_UiHudTopBox
{
	/// <summary>
	/// FABLE 2'S TOPBOX WRAP WIDTH. The publisher marks both TopTitle
	/// and TopMain for the plain wrap (`li r24,1`, `stb r24` 0x82178bb8), so the fit wraps each at
	/// `{0, 0, max.x, max.y}` (`sub_823FCAD8` → `sub_82803138`). max.x is the `BoxName 'TopBox'`
	/// entry's +20, measured once with both meters at 100 (`sub_8218C9C0`, 0x8218cb04): 100 × the
	/// scale.x product up the chain — hud.bgf `Bar` 1.0 (TopBoxX track end) × `Bar` 7.8 × the
	/// panel `TopBox` 1.38 (its widescreen adapter's `WidescreenXWidth`). The strings are the
	/// record's, unwrapped (Engine_UiHudRegistryWrites.TakesText), so Godot wraps them here with
	/// its own font at the guest's width.
	/// </summary>
	public const float WrapWidth = 100f * 1.0f * 7.8f * 1.38f;

	/// <summary>
	/// The box's height range, canvas units: the same calibration (`sub_8218C9C0`) with the TopBoxY
	/// meter at 0 and 100 — 100 × hud.bgf `Bar` scale.y 0.38 → 1.18 (the panel's TopBoxY track).
	/// </summary>
	public static readonly Godot.Vector2 HeightRange = new(100f * 0.38f, 100f * 1.18f);

	/// <summary>
	/// The will-name record `sub_826B50B8` pushes on channel 10 while the spell blade is up —
	/// `sub_8230B560(rec, dword_8349AE44, …)` at 0x826b5200, the hash `sub_8325E1C8` seeds from
	/// "SpellAssignerInfo". The blade's label already draws that word, so the banner stays down
	/// for THIS record only; the quest objective `sub_8251D538` pushes on the same channel
	/// (`Fnv1("QuestObjective")`, 20 000 ms, 0x8251d800) shows like any other box.
	/// </summary>
	public const uint SpellAssignerInfoHash = 0xF2BA95B7u;

	/// <summary>The channel the interaction prompt rides; `sub_821787D0` returns for it at 0x82178978.</summary>
	public const int InteractionPromptChannel = 7;

	private static readonly string[] BoxToggles =
	{
		"ButtonOnly", "TopTitle", "TopMain", "RightTopButtonVisible",
		"LeftTopButtonVisible", "RightTopButtonIsY", "RightTopButtonMeterVisible",
		"LeftTopButtonMeterVisible", "RightTopButtonHeld", "LeftTopButtonHeld",
	};

	private static bool _wasUp;

	private static uint _shown;

	/// <summary>The diagnostic sink; the test host, which cannot call Godot, replaces it.</summary>
	public static System.Action<string> Log { get; set; } = line => Godot.GD.Print(line);

	/// <summary>
	/// The front record the guest's composer last composed (`@Hud.TopBoxFront`, gui_registry_hooks.cpp),
	/// not present while `GUI.SetTopBoxHidden` hides the box (`@Hud.TopBoxHidden`).
	/// </summary>
	public static Fable2Bridge.TopBoxFront Front =>
		_hidden ? _front with { Present = false } : _front;

	/// <summary>The body the composer stated for <see cref="Front"/> (`@Gui.TextGroup`), a literal.</summary>
	public static Fable2Bridge.TopBoxBody Body =>
		new(true, _front.Present, 3, 0u, 0u, 0u, 0u, _front.TitleTag, _front.NameHash, _body);

	private static Fable2Bridge.TopBoxFront _front = new(true, false, 0u, false, false, false, false, 0, 0u, 0);

	private static bool _hidden;

	private static string _body = string.Empty;

	private static string? _composing;

	private static readonly uint TopMainHash = Refii.Core.Engine_GameHash.Fnv1("TopMain");

	/// <summary>
	/// `@Hud.TopBoxFront present channel name title showA holdA showY holdY`: one composition's
	/// front record. The texts of that composition were stated before it.
	/// </summary>
	public static void StateFront(
		bool present, int channel, uint nameHash, uint titleTag, bool showA, bool holdA, bool showY, bool holdY)
	{
		_front = new Fable2Bridge.TopBoxFront(true, present, titleTag, showA, holdA, showY, holdY, channel, nameHash, 0);
		_body = present ? _composing ?? string.Empty : string.Empty;
		_composing = null;
	}

	/// <summary>`@Hud.TopBoxHidden hidden`.</summary>
	public static void StateHidden(bool hidden) => _hidden = hidden;

	/// <summary>`@Gui.TextGroup group element text`: a text the composer is stating; the body is `TopMain`'s.</summary>
	public static void StateText(uint elementHash, string text)
	{
		if (elementHash == TopMainHash)
		{
			_composing = text;
		}
	}

	/// <param name="front">The composer's front record, as stated (<see cref="Front"/>).</param>
	/// <param name="strings">Babel, for `TextManager_HasKey` on the title tag.</param>
	/// <param name="hostNotice">
	/// Refii's own notice (co-op joined/left, a partner waiting at an exit), shown as a body-only
	/// box while the guest's own box is down. The guest's boxes keep the box whenever they stand.
	/// </param>
	public static void Publish(
		Fable2Bridge.TopBoxFront front, bool aHeld, bool yHeld,
		Engine_HudValues values, Refii.Text.Engine_BabelDatabase? strings, string? hostNotice = null) =>
		Publish(front, aHeld, yHeld, values, strings, hostNotice,
			static () => Body);

	/// <param name="readBody">The front record's body, as the composer stated it.</param>
	public static void Publish(
		Fable2Bridge.TopBoxFront front, bool aHeld, bool yHeld,
		Engine_HudValues values, Refii.Text.Engine_BabelDatabase? strings, string? hostNotice,
		System.Func<Fable2Bridge.TopBoxBody> readBody)
	{
		// A box cannot be up without a readable manager; `Known` false (no manager, or no
		// bridge) states it down rather than freezing whatever was stated last.
		bool willName = front.Known && front.Present && front.NameHash == SpellAssignerInfoHash;
		bool guestUp = front.Known && front.Present && !willName;
		bool hostUp = front.Known && !guestUp && !string.IsNullOrEmpty(hostNotice);
		bool up = guestUp || hostUp;
		State(values, "TopBox", up);
		if (up != _wasUp)
		{
			// SlideFader2 `TopBox`: 2 in, 3 out fast — stated as the SlideFaderName index the
			// fold shifts by one for the second channel.
			values.StateFade(Refii.Core.Engine_GameHash.Fnv1("TopBox"), up ? 1 : 2);
			_wasUp = up;
		}

		if (!up)
		{
			Lower(values);
			if (willName && _shown != front.NameHash)
			{
				_shown = front.NameHash;
				Log($"[hud-topbox] front record 0x{front.NameHash:X8} on channel {front.Channel} — " +
					"SpellAssignerInfo, the will name; the spell blade draws it, so the banner stays down");
			}
			else if (!willName)
			{
				_shown = 0u;
			}

			return;
		}

		if (hostUp)
		{
			Lower(values);
			State(values, "TopMain", true);
			values.StateText(Refii.Core.Engine_GameHash.Fnv1("TopTitle"), string.Empty);
			values.StateText(Refii.Core.Engine_GameHash.Fnv1("TopMain"), hostNotice!);
			_shown = 0u;
			return;
		}

		// CHANNEL 7 IS THE INTERACTION PROMPT AND HAS NO TEXT. The publisher returns for it
		// before any text work (0x82178978): the box is up, the targeted-query slide is set
		// (`sub_823FD8E8(+112)`), and TopTitle/TopMain/ButtonOnly are never stated. Composing
		// them here from the registry is what put the previous box's body under a vendor's
		// name. Every box toggle goes down with the text: the previous record's buttons must
		// not outlive it.
		if (front.Channel == InteractionPromptChannel)
		{
			Lower(values);

			// AND THE PROMPT ITSELF. What channel 7 goes on to do is the whole prompt:
			// `sub_823FD8E8` raises `TargetedQuery` on BOTH channels the host cannot read —
			// `sub_828421F8(reg[3], hash, 2)` at 0x823fd954 (SlideFader2, 2 = in) and
			// `sub_823FE848(reg[9], hash, 1)` at 0x823fd994 (SlideToggle on) — the hash being
			// FNV-1 of "TargetedQuery" (0x820B0638). Its twin `sub_823FD9B0` lowers both. Both
			// are idempotent on the owner's own byte, so stating them per frame matches.
			// Re-derived here for the same reason TopBox is: registries 3 and 9 are not walked.
			values.StateText(Refii.Core.Engine_GameHash.Fnv1("TopMain"), string.Empty);
			if (_shown != front.NameHash)
			{
				_shown = front.NameHash;
				Log($"[hud-topbox] front record 0x{front.NameHash:X8} on channel 7 — interaction prompt, no text");
			}

			return;
		}

		// `TextManager_HasKey(title tag)` decides the title; the body is the FRONT RECORD's own
		// provider, composed here the way `sub_821787D0` composes it. Registry 6 is not read
		// for it: a record with no title and no body only sets ButtonOnly and never rewrites
		// TopMain, so the registry string outlives its record — "Poppy the Stall Vendor" over
		// "Find the abbot in Oakfield." was the previous box's body under the new title.

		string title = string.Empty;
		bool hasTitle = strings is not null && strings.TryGetString(front.TitleTag, out title);
		string body = ComposeBody(readBody(), strings);
		bool buttonOnly = !hasTitle && body.Length == 0;
		State(values, "ButtonOnly", buttonOnly);
		State(values, "TopTitle", !buttonOnly && hasTitle);
		State(values, "TopMain", !buttonOnly && body.Length != 0);

		// THE TITLE IS THE RECORD'S TOO. The publisher hands `TopTitle` = Babel(rec+4) and
		// `TopMain` to the TopBox's box fit (`sub_8230D700` at 0x82178b34 / 0x82178bc4, then
		// `sub_823FCEB0` 0x82178c14), which writes them into the text registry only through the
		// box's own record — so the strings are stated here from the record they come from.
		values.StateText(Refii.Core.Engine_GameHash.Fnv1("TopTitle"), hasTitle ? title : string.Empty);
		values.StateText(Refii.Core.Engine_GameHash.Fnv1("TopMain"), body);

		// The button toggles exactly as the publisher states them (0x82178c84–0x82179070):
		// every one is written on every publish, so a record without buttons lowers the
		// previous record's.
		bool showA = front.ShowA;
		bool showY = front.ShowY;
		bool holdA = front.HoldA;
		bool holdY = front.HoldY;
		State(values, "RightTopButtonVisible", showA || showY);
		State(values, "LeftTopButtonVisible", showA && showY);
		State(values, "RightTopButtonIsY", !showA && showY);
		State(values, "RightTopButtonMeterVisible", (showA && holdA) || (!showA && showY && holdY));
		State(values, "LeftTopButtonMeterVisible", showA && showY && holdY);
		State(values, "RightTopButtonHeld", showA ? aHeld : showY && yHeld);
		State(values, "LeftTopButtonHeld", showA && showY && yHeld);

		if (_shown != front.NameHash)
		{
			_shown = front.NameHash;
			Log($"[hud-topbox] front record 0x{front.NameHash:X8} on channel {front.Channel}" +
				$" — title {(hasTitle ? "yes" : "no")}, A {(showA ? (holdA ? "hold" : "press") : "off")}," +
				$" Y {(showY ? (holdY ? "hold" : "press") : "off")}");
		}
	}

	/// <summary>Every toggle inside the box down; `TopBox` itself is stated by the caller.</summary>
	private static void Lower(Engine_HudValues values)
	{
		foreach (string name in BoxToggles)
		{
			State(values, name, false);
		}
	}

	/// <summary>The body by its class: 3 is the literal the composer stated; 0 to 2 are Babel keys.</summary>
	private static string ComposeBody(Fable2Bridge.TopBoxBody body, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (!body.Known || !body.Present)
		{
			return string.Empty;
		}

		string Babel(uint key) =>
			strings is not null && strings.TryGetString(key, out string text) ? text : string.Empty;

		switch (body.Class)
		{
			case 0:
				return Babel(body.Key);
			case 1:
				return Babel(body.Key)
					.Replace("%1", ((int)body.Second).ToString())
					.Replace("%2", ((int)body.Third).ToString())
					.Replace("%3", ((int)body.Fourth).ToString());
			case 2:
				return Babel(body.Key)
					.Replace("%s", Babel(body.Second))
					.Replace("%1", Babel(body.Second))
					.Replace("%2", Babel(body.Third));
			case 3:
				return body.Literal;
			default:
				return string.Empty;
		}
	}

	// THE TARGETED-QUERY PROMPT MOVED OUT. It used to be re-derived here from the front
	// record's channel, which raised it on 7 and LOWERED it on everything else — and the guest
	// never lowers it that way: `sub_821787D0` returns without touching the group on any
	// channel but 7 (0x82178978), and the only lower is target loss inside `sub_82176808`.
	// `Engine_UiHudTargetedQuery` states it from the record's own raise byte instead, which is
	// authoritative, and also carries the registry-10 slide set this could not.

	private static void State(Engine_HudValues values, string name, bool on) =>
		values.StateVisible(Refii.Core.Engine_GameHash.Fnv1(name), on);
}
