namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Refii.Ui;

/// <summary>
/// THE BOX SCREENS' OWN ART. Each box-manager screen is drawn in its own .bgf's frame — the black
/// box, its glow, the top and bottom lines and the description band (`BlackBox`, `TopLine`,
/// `BottomLine`, `Desription` under a `Small1` / `Medium1` / `Large1` … node) — on the shared HUD
/// view, behind the list <see cref="Fable3StatedScreen"/> draws its rows on. Only the frame is cut
/// (<see cref="Fable3HudDocument"/> groups-only): the texts, rows and prompts are the template's.
/// </summary>
/// <remarks>
/// <para>WHICH FILE. The box's type (vtable slot 7) picks the presentation through sub_8291F4C8 and
/// the file through sub_82B3EC40's table (<see cref="Fable3BoxScreenModel"/>'s class list); the
/// stems are the documents <see cref="Fable3Elements"/> keys their statements by.</para>
/// <para>WHEN IT SHOWS. A frame stands in the scene root's slide 1 and its screen root's slide — the
/// file's own gating (each frame is an entry of both). Both are stated here while the screen is up,
/// from what the screen's own calls stated (<see cref="RootSlide"/>), so the frame follows the page:
/// the divorce box's Large1 on its asking and told pages, Medium1 on the confirmed one; the
/// treasury's Large4 with the balances, Medium1 for the transfer.</para>
/// </remarks>
public static class Fable3ScreenFrames
{
	/// <summary>
	/// One screen's frame: its document, the element root its rows are written under (the
	/// <see cref="Fable3ScreenBoxes.ScreenRoot"/>), the component whose slides gate the frame, the
	/// frame nodes' parent, the frame nodes, and the rows its file authors on that frame
	/// (<see cref="Column"/>; null keeps the list template's own column).
	/// </summary>
	public readonly record struct Frame(string Document, string Root, string SlideOwner, string Parent, string[] Art, Column? Rows = null);

	/// <summary>
	/// The rows a frame's file authors, by element path in its document: the first row's text (its
	/// place and alignment are the list's column), the second row's (the two rows' distance is the
	/// pitch a page without a cursor stands its rows at) and the element where the rows' values begin
	/// (the marks' column). <see cref="PanelOf"/> reads them from the standing document.
	/// </summary>
	public readonly record struct Column(string First, string? Second = null, string? Values = null);

	/// <summary>
	/// Every box screen's frame, read from its .bgf (the art-only nodes under the screen root that
	/// the root's slides switch on).
	/// </summary>
	/// <remarks>
	/// The rows, from each file's text elements (chain x, y; the .fac's halign): hero status
	/// `Section1.Titles.Strength` (5, 160) halign 0, `.Skill` 30 below, the meters' zero
	/// `StatusMeter_Pointer` x 257; the schedule `Tasks.Task1` (−140, 189) halign 0, `Task2` 102 below,
	/// `TickBox1.Icon_Task` x −190; the choice boxes' `SelectionTitles.*` halign 1 (join and friends x
	/// 78, visibility 68); the rest halign 0 — chat `SelectionTitles.General` −280, treasury `Personal
	/// Balance` (−334, 180) with `Treasury.Treasury Balance` 30 below (its rows' pitch: 2 balances and up
	/// to 9 transactions stand inside Large4, whose bottom line is at −253 + 50), bed `Sleep.Title` −330, food `Current.Health` −253, wedding `Group.Title` −335,
	/// partners `GamerTag` −294, proposal `GamerInfo.Gold2` −279, divorce
	/// `JointAssetsBreakdown.ActionDescription` −334, quest summary `Text.QuestName` −308; promises'
	/// `Promise.Promise` halign 1 at 0. Stats states none: its pages stand their rows in different
	/// columns (categories halign 1 at 0, statistics halign 0 at −390), and the template's own
	/// column is the statistics page's.
	/// </remarks>
	public static readonly Frame[] Frames =
	{
		new("herostatus", Fable3ScreenModel.HeroStatus, "HeroStatus", "HeroStatus", new[] { "HeroStatus" },
			new("HeroStatus.Section1.Titles.Strength", "HeroStatus.Section1.Titles.Skill",
				"HeroStatus.Section2.Values.Morality.StatusMeter.StatusMeter_Pointer")),
		new("statsscreen", Fable3ScreenModel.Stats, "StatsBox", "StatsBox", new[] { "Large2" }),
		new("promises", Fable3ScreenModel.Promises, "Promises", "Promises", new[] { "Background2", "Promises" },
			new("Promises.Promise.Promise")),
		new("questsummary", Fable3ScreenModel.Quests, "QuestSummary", "QuestSummary", new[] { "Large1" },
			new("QuestSummary.Details.QuestSummaryDetails0.Text.QuestName")),
		new("kingsschedule", Fable3ScreenModel.Schedule, "KingsSchedule", "KingsSchedule", new[] { "KingsAgenda" },
			new("KingsSchedule.Tasks.Task1", "KingsSchedule.Tasks.Task2", "KingsSchedule.TickBoxes.TickBox1.Icon_Task")),
		new("joinoptions", Fable3BoxScreenModel.JoinOptions, "OrbEmote", "OrbEmote", new[] { "Medium1" },
			new("OrbEmote.SelectionTitles.Any1")),
		new("visability", Fable3BoxScreenModel.Visibility, "Visibilities", "Visibilities", new[] { "Medium1" },
			new("Visibilities.SelectionTitles.Everyone")),
		new("friendschannel", Fable3BoxScreenModel.FriendsChannel, "FriendsChannel", "FriendsChannel", new[] { "Medium1" },
			new("FriendsChannel.SelectionTitles.Friends")),
		new("chatchannel", Fable3BoxScreenModel.ChatChannel, "ChatChannel", "ChatChannel", new[] { "Small1" },
			new("ChatChannel.SelectionTitles.General")),
		new("treasureaccounts", Fable3BoxScreenModel.Treasury, "TreasureAccounts", "TreasureAccounts", new[] { "Large4", "Medium1" },
			new("TreasureAccounts.Personal.Personal Balance", "TreasureAccounts.Treasury.Treasury Balance")),
		new("bedmenubox", Fable3BoxScreenModel.BedMenu, "BedMenu", "BedMenu", new[] { "Medium2" },
			new("BedMenu.Sleep.Title")),
		new("foodquerybox", Fable3BoxScreenModel.FoodQuery, "InfoBox", "InfoBox", new[] { "Medium1" },
			new("InfoBox.Current.Health")),
		new("compweddingpicker", Fable3BoxScreenModel.WeddingPicker, "WeddingPicker", "WeddingPicker", new[] { "Medium1" },
			new("WeddingPicker.Details.WeddingDetails0.Group.Title")),
		new("cooppartnersscreen", Fable3BoxScreenModel.Partners, "CoopPartnersInfoBox", "CoopPartnersInfoBox", new[] { "Large1" },
			new("CoopPartnersInfoBox.Details.PartnerDetails0.GamerTag")),
		new("proposalquerybox", Fable3BoxScreenModel.Proposal, "InfoBox", "MarriageProposal", new[] { "Large1", "Medium1" },
			new("InfoBox.MarriageProposal.GamerInfo.Gold2")),
		new("divorcemessagebox", Fable3BoxScreenModel.Divorce, "DivorceMessageBox", "DivorceMessageBox", new[] { "Large1", "Medium1" },
			new("DivorceMessageBox.Foreground.JointAssetsBreakdown.ActionDescription")),
	};

	/// <summary>
	/// The panel the screen whose element root is <paramref name="root"/> stands its rows on, read
	/// from its frame's standing document (<see cref="Fable3HudDocument.Authored"/>): the frame art's
	/// x as the centre line, the first row's place and alignment, the pitch to the second row and the
	/// values' x, each where the frame states it. Null for a screen with no frame or no stated rows, or
	/// before its document stands — the template's own column.
	/// </summary>
	public static Fable3ListPanel? PanelOf(string? root) => PanelOf(root, PanelSlide(root));

	/// <summary>
	/// <see cref="PanelOf(string?)"/> as the screen root's slide <paramref name="slide"/> stands the frame: every
	/// element read where that slide's position writes put it (<see cref="Fable3HudDocument.Authored"/>), so a
	/// page whose slide moves its rows — the treasury's TransferFounds (balances −90), the proposal's Show
	/// (`GamerInfo` to (−56, 88)), the chat channel's `General` (−179) — stands them on that page's place.
	/// </summary>
	public static Fable3ListPanel? PanelOf(string? root, int slide)
	{
		if (FrameOf(root) is not { Rows: { } rows } frame || Fable3Hud.Standing(frame.Document) is not { } document ||
			document.Authored(rows.First, frame.SlideOwner, slide) is not { } first)
		{
			return null;
		}

		float centre = document.Authored(frame.Root + "." + frame.Art[0], frame.SlideOwner, slide) is { } art ? art.X : 0f;
		float? pitch = rows.Second is { } second && document.Authored(second, frame.SlideOwner, slide) is { } next ? first.Y - next.Y : null;
		float? values = rows.Values is { } path && document.Authored(path, frame.SlideOwner, slide) is { } value ? value.X : null;
		return new Fable3ListPanel(centre, first.X, first.HorzAlign, first.Y, pitch, values);
	}

	/// <summary>The screen root's slide its page stands on now (<see cref="RootSlide"/>, from the front box's statements); 1 for no frame.</summary>
	public static int PanelSlide(string? root) =>
		root is null ? 1 : RootSlide(root, Fable3ScreenStatements.Of(Fable3ScreenBoxes.ScreenBox));

	/// <summary>
	/// The frame of the screen whose element root is <paramref name="root"/>, or null: an unknown root,
	/// or the save menu, whose `SaveMenu.bgf` (sub_82B3EC40 type 11, 0x82B3EE9C) ships in no bank, so
	/// the guest never loads it (sub_825C7A90 0x825C7AF4) and draws no frame.
	/// </summary>
	public static Frame? FrameOf(string? root)
	{
		foreach (Frame frame in Frames)
		{
			if (frame.Root == root)
			{
				return frame;
			}
		}

		return null;
	}

	/// <summary>One frame document per box screen, for <see cref="Fable3Hud"/> to stand beside the HUD.</summary>
	public static IEnumerable<Fable3HudDocument> Documents()
	{
		foreach (Frame frame in Frames)
		{
			var groups = new Engine_HudLayout.GroupSubtree[frame.Art.Length];
			for (int i = 0; i < frame.Art.Length; ++i)
			{
				groups[i] = new Engine_HudLayout.GroupSubtree(frame.Art[i], "Frame_" + frame.Document,
					Engine_HudLayout.Anchor.Centre, 0f, false, frame.Parent, AnyNode: true);
			}

			yield return new Fable3HudDocument(frame.Document, groups, layouts: false, groupsOnly: true);
		}
	}

	/// <summary>
	/// The screen root's slide that its page stands on, from the screen's own statements: the stats
	/// page (`@Screen.StatsPage`, the Show's 2 at 0x8296D54C; 2 categories, 3 statistics, 4 friends),
	/// the treasury page (2 both balances, 3 personal only, 4 transfer, 5 query) as its own root write
	/// stated it (the Show and the Update slide the root to `+28`, 0x82B5A134 / 0x82B5A388), the divorce
	/// page by the ctor's stated mode (2 asking, 3 told, 4 confirmed: sub_82928C90 / sub_82928F78 /
	/// sub_82929378 at 0x82928CB4 / 0x82928F9C / 0x8292939C), and Godot's cursor on the bed (slides
	/// Sleep 1 / Sex 2), join / visibility and the friends channel (one slide per row), the food and proposal queries' Show (2); every
	/// other screen has the one slide.
	/// </summary>
	public static int RootSlide(string root, Fable3ScreenState screen) => root switch
	{
		Fable3ScreenModel.Stats => screen.StatsPage is >= 2 and <= 5 ? screen.StatsPage.Value : 2,
		Fable3BoxScreenModel.Treasury => screen.RootState is >= 2 and <= 5 ? screen.RootState.Value : 3,
		Fable3BoxScreenModel.Divorce => Fable3BoxScreenModel.DivorcePageOf(screen.Divorce?.Mode ?? 0) switch
		{
			Fable3BoxScreenModel.DivorceKind.Told => 3,
			Fable3BoxScreenModel.DivorceKind.Confirmed => 4,
			_ => 2,
		},
		Fable3BoxScreenModel.BedMenu => Fable3SessionBoxAnswers.CursorOf(root, screen) == 1 ? 2 : 1,
		Fable3BoxScreenModel.JoinOptions or Fable3BoxScreenModel.Visibility =>
			System.Math.Clamp(Fable3SessionBoxAnswers.CursorOf(root, screen) + 1, 1, 3),
		Fable3BoxScreenModel.FriendsChannel => System.Math.Clamp(Fable3SessionBoxAnswers.CursorOf(root, screen) + 1, 1, 4),
		Fable3BoxScreenModel.FoodQuery or Fable3BoxScreenModel.Proposal => 2,
		_ => 1,
	};

	private static string? _statedDocument;
	private static int _statedSlide;
	private static ulong _stamp;

	/// <summary>The scene's and the screen root's slides for <paramref name="document"/> while its screen is up.</summary>
	public static IEnumerable<Fable3HudStatements.Statement> StatementsFor(string document)
	{
		if (FrameOf(Fable3ScreenBoxes.ScreenRoot) is not { } frame || frame.Document != document)
		{
			yield break;
		}

		int slide = RootSlide(frame.Root, Fable3ScreenStatements.Of(Fable3ScreenBoxes.ScreenBox));
		if (_statedDocument != document || _statedSlide != slide)
		{
			// Entered once per page: a slide is re-entered only by a newer stamp.
			_statedDocument = document;
			_statedSlide = slide;
			_stamp = Fable3Elements.Generation + 1;
		}

		yield return new Fable3HudStatements.Statement(Fable3HudStatements.RootPath, 1, null, null, null, _stamp);
		yield return new Fable3HudStatements.Statement(frame.SlideOwner, slide, null, null, null, _stamp);
	}

	/// <summary>Shows a frame document's view only while its screen is up (<see cref="Engine_HudView.Suppressed"/>).</summary>
	public static void Gate(Fable3HudDocument document)
	{
		if (document.View is not { } view)
		{
			return;
		}

		bool framed = false;
		foreach (Frame frame in Frames)
		{
			framed |= frame.Document == document.Document;
		}

		if (!framed)
		{
			return;
		}

		bool shown = FrameOf(Fable3ScreenBoxes.ScreenRoot) is { } up && up.Document == document.Document;
		if (view.Suppressed == shown)
		{
			view.Suppressed = !shown;
		}

		if (!shown && _statedDocument == document.Document)
		{
			_statedDocument = null;
		}
	}
}
