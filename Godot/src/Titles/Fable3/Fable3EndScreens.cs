namespace Refii.Titles.Fable3;

using System.Globalization;
using Refii.Ui;

/// <summary>
/// Fable 3's epilogue (questcompletion.bgf) and credits (creditsscreen.bgf), drawn on the SHARED
/// HUD view as two more <see cref="Fable3HudDocument"/>s. Both are screen-manager screens, not
/// box-manager boxes, so no `@Box.Show` names them: end_screen_hooks.cpp states when each is up
/// and which document it is; everything on them is an element statement. Their pad is Godot's
/// (<see cref="Answer"/>).
/// </summary>
/// <remarks>
/// THE EPILOGUE. The show (sub_8276B460) writes the title, description, reward caption, renown
/// and button texts through the HUD text registry sub_82733F30 (`@Hud.Text`) — all behind its
/// presentation (0x8276B4BC), so the title and description are taken from the show's own arguments
/// (`@Epilogue.Open`, <see cref="Opened"/>); the reward rows
/// (AddItemRewardToEpilogueScreen -> sub_8276B978) toggle `EpilogueItem1/2` through the
/// SlideToggleName registry (sub_82921200 at 0x8276B9CC, `@Hud.Slide`) and write their texts the
/// same way; the show hides the reward caption with sub_828196E0 (0x8276B61C, `@Ui.Active`); the gold
/// (SetGoldRewardInEpilogueScreen -> sub_8276B848) writes `EpilogueRenown` / `EpilogueRenownAmt`.
/// The image is not an element write — the show stores it into the `ScreenShot` texture
/// (0x8276B7F8) — so it arrives only as `@Epilogue.Open`'s second argument. INTERACT (13, the pad's
/// A) closes it through its own input (vt+20, TU3 0x827A2480 / disc 0x8276B410), which Godot runs with
/// the player who pressed: player one always, another player only on a machine that is not the
/// session's server (an online client: the guest's Network.IsServer, N+0x1C and N+0x1D).
///
/// THE CREDITS. The roll (sub_82B3F980, then its tick sub_82B3FD70) writes the `GUI_CREDITS` lines
/// into `Text.Text0..23` (sub_82B45268 at 0x82B3FC0C / 0x82B3FE64 / 0x82B3FEA4, `@Ui.Text` on their
/// guards) and scrolls them and `credits_logos` with SetPosition (0x82B3FC50, 0x82B3FED0, 0x82B3FCE8,
/// 0x82B3FF1C, `@Ui.Position`). Both documents are drawn from those statements alone (stated-only).
/// It ends itself. A and B hand GUI_SELECT (72) to its own input (TU3 sub_82B75188 / disc
/// sub_82B3FF60), which ends the roll with a local partner seated, or on the session's server (a
/// solo game is its own server: A skips it alone, as on the 360), and never on an online client;
/// Godot adds no skip of its own.
/// </remarks>
public static class Fable3EndScreens
{
	/// <summary>
	/// The epilogue's widgets: the quest card (`elements`) and its button row. `questcompletion.bgf`'s card
	/// `Layer.elements` is a plain node (its leaf, bullet points, title, description, reward texts, `Image` and
	/// `GuildSealIcon` beneath), so it is cut as any node; `Layer.Buttons` is a component.
	/// </summary>
	public static readonly Engine_HudLayout.GroupSubtree[] EpilogueGroups =
	{
		new("elements", "Epilogue", null, 0f, true, AnyNode: true),
		new("Buttons", "EpilogueButtons", null, 0f, true),

		// The epilogue's glint (`Layer.FX.fx2`, CHUDParticle `fxgui_glint_hero_status_01`), a childless
		// component cut alone so the view's glints (Engine_UiParticles) stand on it.
		new("fx2", "EpilogueGlint", Engine_HudLayout.Anchor.Centre, 0f, false, "FX", AnyNode: true, Every: true),
	};

	/// <summary>
	/// The credits' widgets: the backdrop, the rolling lines, the logo sheet and the edge fades. In
	/// `creditsscreen.bgf` each is a plain node under `Layer`, not a component (`Layer.Background`,
	/// `Layer.Text` over `Text0..23`, `Layer.credits_logos`, `Layer.Fade`), so each is cut as any node
	/// (<see cref="Engine_HudLayout.GroupSubtree.AnyNode"/>): cut as components they matched nothing, the
	/// document built no piece and the stated roll (`@Ui.Text` / `@Ui.Position` on `Text.Text&lt;i&gt;` and
	/// `credits_logos`) had nothing to land on.
	/// </summary>
	public static readonly Engine_HudLayout.GroupSubtree[] CreditsGroups =
	{
		new("Background", "CreditsBackground", null, -0.5f, true, AnyNode: true),
		new("Text", "CreditsText", null, 0f, true, AnyNode: true),
		new("credits_logos", "CreditsLogos", null, 0f, true, AnyNode: true),
		new("Fade", "CreditsFade", null, 0.5f, true, AnyNode: true),
	};

	/// <summary>The epilogue is up (between `@Epilogue.Open` and `@Epilogue.Close`).</summary>
	public static bool EpilogueUp { get; private set; }

	/// <summary>The credits roll (between `@Credits.Open` and `@Credits.Close`).</summary>
	public static bool CreditsUp { get; private set; }

	/// <summary>
	/// The tick action for a press while an end screen is up, or null: A on the epilogue is
	/// `epilogue:&lt;p&gt;:close`; A or B on the credits is `credits:&lt;p&gt;:skip`. The epilogue stands
	/// in front when both are up (it is the screen the guest's input reaches, the open screen object).
	/// </summary>
	public static string? Answer(bool a, bool b, int player = Fable3ActingPlayer.One) =>
		Lever(EpilogueUp, CreditsUp, a, b, player);

	/// <summary><see cref="Answer"/> for stated screens (pure).</summary>
	public static string? Lever(bool epilogueUp, bool creditsUp, bool a, bool b, int player)
	{
		if (epilogueUp)
		{
			return a ? "epilogue:" + Fable3ActingPlayer.Field(player) + ":close" : null;
		}

		return creditsUp && (a || b) ? "credits:" + Fable3ActingPlayer.Field(player) + ":skip" : null;
	}

	/// <summary>The two documents, for <see cref="Fable3Hud"/> to stand beside `hud` and `interactionmode`.</summary>
	public static Fable3HudDocument[] Documents() => new[]
	{
		new Fable3HudDocument("questcompletion", EpilogueGroups, layouts: false),
		new Fable3HudDocument("creditsscreen", CreditsGroups, layouts: false),
	};

	/// <summary>
	/// Shows an end screen's view only while its screen is up (the HUD view keeps its last feed
	/// otherwise). Held through <see cref="Engine_HudView.Suppressed"/>: the view's walk restates
	/// its own visibility every guest frame, so a bare hide would not hold. Other documents are left alone.
	/// </summary>
	public static void Gate(Fable3HudDocument document)
	{
		if (document.View is not { } view)
		{
			return;
		}

		bool? up = document.Document switch
		{
			"questcompletion" => EpilogueUp,
			"creditsscreen" => CreditsUp,
			_ => null,
		};
		if (up is { } shown && view.Suppressed == shown)
		{
			view.Suppressed = !shown;
		}
	}

	public static void Register(Engine_UiGuiNatives router)
	{
		router.Register("@Epilogue.Open", call =>
		{
			EpilogueUp = true;
			Opened(call.Argument(2).Text, call.Argument(3).Text, (int)call.Argument(4).Number,
				Parse(call.Argument(5).Text), Parse(call.Argument(6).Text));
		});
		// The screenshot's texture as the show computed it (sub_826EFBD8 at 0x8276B788), for the
		// elements filed under `*dword_8355AC44` in the ImageName registry.
		router.Register("@Epilogue.Image", call =>
		{
			uint name = Parse(call.Argument(0).Text);
			uint texture = Parse(call.Argument(1).Text);
			Fable3HudRegistry.StateImage(name, texture);
			Fable3Elements.Log($"[fable3-stated] epilogue screenshot '{call.Argument(2).Text}' texture {texture:X8} under image name {name:X8}");
		});
		router.Register("@Epilogue.Close", _ => EpilogueUp = false);
		router.Register("@Credits.Open", _ => CreditsUp = true);
		router.Register("@Credits.Close", _ => CreditsUp = false);
	}

	/// <summary>
	/// The show's own arguments (end_screen_hooks.cpp): the title and description land on the
	/// elements questcompletion.bgf binds under the text names the show writes them to
	/// (`*dword_8355AC24` / `*dword_8355AC28`, 0x8276B500 / 0x8276B524) — the writes the show makes
	/// only behind its presentation (0x8276B4BC).
	/// </summary>
	public static void Opened(string title, string description, int renown, uint titleName, uint descriptionName)
	{
		if (titleName != 0u)
		{
			Fable3HudRegistry.StateText(titleName, title);
		}

		if (descriptionName != 0u)
		{
			Fable3HudRegistry.StateText(descriptionName, description);
		}
	}

	private static uint Parse(string hex) =>
		uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value) ? value : 0u;
}
