using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The front end's modal warning — the box the guest puts up when an entry refuses.
///
/// WHAT THE GUEST DOES, so this can do the same thing. `GUI_FrontendRoot_MenuActionDispatch`
/// (`0x826DFD58`) never raises a message itself; every failing guard calls slot `+0x20` of its
/// own vtable (`0x820D0934`), which is `sub_826E1C50` → `sub_826E40E0`. That function drives
/// five named elements and nothing else:
///
///   dialog                     shown, and the screen's substate set to 1
///   dialog.Group.yousure       the BODY text  (r5)
///   dialog.Group.OK            the accept BUTTON label (r4)
///   dialog.Group.cancel        shown only when the two-button flag (r6) is set
///   dialog.Group.icon_button_y  `abyx.tex` when two-button, `blank.tex` when not
///
/// So the whole contract is: a body string, a button string, and one flag deciding whether a
/// second button exists. Every storage refusal on the New Game path passes `r6 = 0` — one
/// button — except the save-limit one, which passes `r6 = 1` and labels its accept button
/// `GUI_DEVICE_REMOVED_CONTINUE`. That is a generic "Continue", reused; it is NOT the front
/// end's Continue menu entry, because there is no such entry.
///
/// THE TEXT IS NOT INVENTED HERE. Those are Babel tags and the guest resolves them through
/// `sub_8230E0E8`, which hashes the name FNV-1 and retries with a `TEXT_` prefix before
/// falling back to the tag itself. <see cref="Engine_UiTheme.Resolve"/> falls back the same
/// way, so an unshipped tag announces itself on screen rather than leaving a blank box that
/// reads as a layout fault.
///
/// IT IS A PAGE, NOT A PRESENTATION. `dialog` is one of the pages under `frontendmainmenu`'s
/// `Layer` — it is already in the tree we build, alongside `choosecard` and `loadgame` — so
/// this clones it out of the same art the artist drew rather than loading anything.
/// </summary>
public partial class Engine_FrontEndWarning : Node3D
{
	/// <summary>The shipped group, as the guest names it in `dword_8349B67C`.</summary>
	public const string GroupName = "dialog";

	/// <summary>The body text element — `dword_8349B684`.</summary>
	public const string BodyName = "yousure";

	/// <summary>The accept button's label — `dword_8349B680`.</summary>
	public const string ButtonName = "OK";

	/// <summary>The second button, shown only in the two-button form — `dword_8349B688`.</summary>
	public const string CancelName = "cancel";

	/// <summary>The Y-button glyph, which the two-button form swaps for `abyx.tex`.</summary>
	public const string IconName = "icon_button_y";

	/// <summary>The accept button's glyph.</summary>
	public const string AcceptIconName = "icon_button_a";

	/// <summary>
	/// A third answer, Refii's: a copy of the accept button and its glyph, answered with Y and laid
	/// out as one more row of the buttons' column (<see cref="Show"/>). The guest's dialog has two buttons; a
	/// host question with three choices (New Game at the save limit) uses this one.
	/// </summary>
	public const string ThirdName = "third";

	public const string ThirdIconName = "icon_button_third";

	/// <summary>The third answer's button, and its column in the `abyx` atlas.</summary>
	public const string ThirdButton = "Y";

	private const int YColumn = 2;

	/// <summary>
	/// The glyph the one-button form stamps: `dword_8349B690` (disc `dword_8349B0E8`), the
	/// resource hash of `art\gui\frames\blank.tex` (static init 0x83264F00..0x83264F24).
	/// </summary>
	public static readonly uint BlankGlyph = Refii.Core.Engine_GameHash.ResourcePath(@"art\gui\frames\blank.tex");

	/// <summary>True when the group was found and cloned, rather than stood in for.</summary>
	public bool HasShippedArt { get; private set; }

	/// <summary>
	/// The frame's click on the box: +1 on the accept button, -1 on the cancel button or its
	/// glyph (two-button form only), 0 for none. Takes every click while the box is up, so one
	/// beside the buttons cannot fall through as A.
	/// </summary>
	public int Click()
	{
		if (!Visible || !Engine_UiPointer.TakeLeft() || Engine_UiPointer.Canvas is not { } at)
		{
			return 0;
		}

		// A click on the paper's scroll bar is the bar's, never an answer.
		if (Paper is not null && Paper.Press(at))
		{
			return 0;
		}

		if (On(ButtonName, at) || On(AcceptIconName, at))
		{
			return 1;
		}

		if (HasThird && (On(ThirdName, at) || On(ThirdIconName, at)))
		{
			return 2;
		}

		return AllowCancel && (On(CancelName, at) || On(IconName, at)) ? -1 : 0;
	}

	private bool On(string name, Vector2 at) =>
		Engine_UiTheme.FindByName(this, name) is Node3D node && node.IsVisibleInTree() &&
		Engine_UiPointer.CanvasBounds(node) is { } bounds && bounds.Grow(8f).HasPoint(at);

	/// <summary>
	/// The screen the ACCEPT button proceeds to, or null when accepting only dismisses.
	///
	/// THE BUTTON COUNT DOES NOT DECIDE THIS, which is the opposite of how it first read. Every
	/// New Game refusal — one-button or two — parks the same record on the screen: `+116 = 0`,
	/// `+120 = 2`. When the close transition fires UNFREEZE, `sub_826E1040` reads `+113` (was
	/// accept pressed), switches on `+116`, and for case 0 with `+120 == 2` runs
	/// `0x826E1158`..`0x826E11AC` — switch the presentation to `"choosecard"`, substate 2. That
	/// is instruction-for-instruction the SUCCESS branch at 0x826E01FC. So accepting any New
	/// Game warning goes on to the gender card; the one-button form simply has no other key.
	///
	/// Carried as a screen name for the same reason <see cref="Engine_UiExpandableMenu.Item.Opens"/>
	/// is — this class knows nothing about who draws what.
	/// </summary>
	public string? AcceptOpens { get; set; }

	/// <summary>
	/// Whether the CANCEL button exists — the guest's `this+206`, its two-button flag.
	///
	/// `sub_826DEE40`'s B arm for the dialog substate reads it first (`0x826DFC90 lbz r11,
	/// 0xCE(r31)`) and returns without even a sound when it is clear. So B on a one-button box
	/// does nothing at all, and that is safe to mirror precisely because accept always leads
	/// somewhere: there is no key combination that can strand the player here.
	/// </summary>
	public bool AllowCancel { get; private set; }

	/// <summary>Whether the third answer stands (<see cref="ThirdName"/>).</summary>
	public bool HasThird { get; private set; }

	/// <summary>What the last <see cref="Show"/> managed to set, for the log.</summary>
	public string LastReport { get; private set; } = "not shown yet";

	/// <summary>
	/// Clones the shipped `dialog` group.
	///
	/// THE AUTHORED TRANSFORM IS KEPT, unlike the gender card's two cards and for the opposite
	/// reason: no slide command moves this group. `sub_826E40E0` sets text and visibility and
	/// touches no position, so where the artist put the box IS where the box goes.
	/// </summary>
	public static Engine_FrontEndWarning FromPresentation(Node3D? group, string name = "Warning")
	{
		var panel = new Engine_FrontEndWarning
		{
			Name = name,
			Visible = false,
		};

		if (group is not null)
		{
			// Duplicated rather than reparented, for the reason Engine_UiTheme.Art gives: the
			// original stays live inside the screen it belongs to, and that screen is rebuilt
			// from the file on every guest state change.
			var clone = (Node3D)group.Duplicate();
			clone.Name = "Group";
			clone.Visible = true;
			panel.AddChild(clone);
			panel.HasShippedArt = true;

			Node3D? third = Copy(Engine_UiTheme.FindByName(clone, ButtonName) as Node3D, ThirdName);
			Node3D? thirdIcon = Copy(Engine_UiTheme.FindByName(clone, AcceptIconName) as Node3D, ThirdIconName);

			// The same `paperbox` nine-slice the in-game boxes draw, sized to the body by the
			// same rule; the buttons and their glyphs ride its bottom edge.
			panel.Paper = Engine_UiPaperBox.For(panel, clone,
				Engine_UiTheme.FindByName(clone, ButtonName) as Node3D,
				Engine_UiTheme.FindByName(clone, AcceptIconName) as Node3D,
				Engine_UiTheme.FindByName(clone, CancelName) as Node3D,
				Engine_UiTheme.FindByName(clone, IconName) as Node3D,
				third, thirdIcon);
		}

		return panel;
	}

	/// <summary>
	/// A hidden sibling copy of <paramref name="source"/> under <paramref name="name"/>, its quads on
	/// private materials so re-texturing the copy cannot reach the original.
	/// </summary>
	private static Node3D? Copy(Node3D? source, string name)
	{
		if (source?.GetParent() is not Node parent)
		{
			return null;
		}

		var copy = (Node3D)source.Duplicate();
		copy.Name = name;
		copy.Visible = false;
		OwnMaterials(copy);
		parent.AddChild(copy);
		return copy;
	}

	private static void OwnMaterials(Node node)
	{
		if (node is MeshInstance3D { MaterialOverride: ShaderMaterial shared } mesh)
		{
			ShaderMaterial own = Refii.Models.Engine_MaterialCopy.Shallow(shared);
			mesh.MaterialOverride = own;
			Engine_UiSceneBuilder.MarkOwnedMaterial(mesh, own);
		}

		foreach (Node child in node.GetChildren())
		{
			OwnMaterials(child);
		}
	}

	/// <summary>
	/// The paper sized to the body (<see cref="Engine_UiPaperBox"/>), shared with the in-game
	/// boxes: grown for a body longer than the authored window, scrolled past the window's
	/// maximum. Null without the shipped art.
	/// </summary>
	public Engine_UiPaperBox? Paper { get; private set; }

	/// <summary>One frame of the body's scroll: the stick, the d-pad, and the pointer's drag or wheel.</summary>
	public void Scroll(float stick, int direction, double delta)
	{
		if (Paper is null || !Visible)
		{
			return;
		}

		Paper.Pointer();
		Paper.Scroll(stick, direction, delta);
	}

	/// <summary>The box closed: the next body starts at the top of an authored-size paper.</summary>
	public void Release() => Paper?.Release();

	/// <summary>
	/// Puts the box up with the guest's own two strings.
	///
	/// <paramref name="twoButton"/> is the guest's `r6`. It is the only thing that decides
	/// whether `cancel` and the `abyx` glyph exist, so it is carried rather than inferred from
	/// which warning is being shown.
	///
	/// Reports what it actually found. A box that draws the wrong text and a box whose text
	/// element was never located look identical on screen, and only one of them is a layout
	/// problem — so this says which, instead of leaving the next run to guess.
	/// </summary>
	/// <param name="cancelTag">
	/// Label for the second button, or null to leave the file's own: the guest's
	/// `GUI_BUTTONS_CANCEL` (0x826e41ec), or a host-authored box's own.
	/// </param>
	/// <param name="glyph">
	/// Whether `icon_button_y` carries a glyph — the guest's stamp is `abyx.tex` or the blank
	/// (<see cref="BlankGlyph"/>); null follows <paramref name="twoButton"/>.
	/// </param>
	/// <param name="thirdTag">
	/// A third answer's label (Y), or null for the guest's own one- and two-button forms.
	/// </param>
	public void Show(
		Engine_UiTheme? theme, string bodyTag, string buttonTag, bool twoButton,
		string? cancelTag = null, bool? glyph = null, string? thirdTag = null)
	{
		Node3D? group = GetNodeOrNull<Node3D>("Group");

		string display = theme?.Resolve(bodyTag) ?? bodyTag;
		string body = SetText(theme, group, BodyName, display);
		if (Paper is not null && group is not null &&
			Engine_UiTheme.FindByName(group, BodyName) is MeshInstance3D bodyNode)
		{
			Paper.Layout(bodyNode, display);
			body += ", " + Paper.Report;
		}

		string button = SetText(theme, group, ButtonName, theme?.Resolve(buttonTag) ?? buttonTag);
		if (cancelTag is not null)
		{
			SetText(theme, group, CancelName, theme?.Resolve(cancelTag) ?? cancelTag);
		}

		// The second button and its glyph exist only in the two-button form. Hidden rather than
		// removed: the same clone is reused for every warning, and the next one may want them.
		SetShown(group, CancelName, twoButton);
		SetShown(group, IconName, glyph ?? twoButton);

		HasThird = thirdTag is not null && group is not null &&
			Engine_UiTheme.FindByName(group, ThirdName) is MeshInstance3D &&
			Engine_UiTheme.FindByName(group, ThirdIconName) is Node3D;
		if (HasThird)
		{
			SetText(theme, group, ThirdName, theme?.Resolve(thirdTag!) ?? thirdTag!);
			Reglyph(Engine_UiTheme.FindByName(group!, ThirdIconName), Engine_UiKeyGlyphs.KeycapFor(ThirdButton));
		}

		SetShown(group, ThirdName, HasThird);
		SetShown(group, ThirdIconName, HasThird);

		AllowCancel = twoButton;
		Visible = true;
		string row = HasThird ? ", " + LayRow(group!) : string.Empty;
		LastReport = $"body {body}, button {button}, {(HasThird ? "three" : twoButton ? "two" : "one")}-button{row}";
	}

	/// <summary>
	/// The dialog's glyphs are cells of the `abyx` atlas (columns A, B, Y, X), with the glyph the scene
	/// builder lays over each for the keys (`keycap_tex`, <see cref="Engine_UiKeyGlyphs.KeycapFor"/>).
	/// The copy of the A glyph is moved to the atlas's Y column and given the Y keycap.
	/// </summary>
	private static void Reglyph(Node? node, Texture2D glyph)
	{
		if (node is MeshInstance3D { MaterialOverride: ShaderMaterial material })
		{
			Vector2 offset = material.GetShaderParameter("uv1_offset").AsVector2();
			int column = Mathf.Clamp(Mathf.FloorToInt((offset.X * 4f) + 0.01f), 0, 3);
			material.SetShaderParameter("uv1_offset", new Vector2(offset.X + ((YColumn - column) * 0.25f), offset.Y));
			material.SetShaderParameter("keycap_tex", glyph);
			material.SetShaderParameter("has_keycap", true);
		}

		if (node is null)
		{
			return;
		}

		foreach (Node child in node.GetChildren())
		{
			Reglyph(child, glyph);
		}
	}

	/// <summary>
	/// Lays the third answer out as one more row of the dialog's button column: the file stacks
	/// cancel one row above accept, each label right-aligned against its glyph, so the third stands one
	/// such step above cancel, at cancel's x. The step is the authored one (cancel's place less
	/// accept's), taken after the paper has placed both (<see cref="Engine_UiPaperBox.Layout"/>), which
	/// lays the next box out from the authored places again.
	/// </summary>
	private static string LayRow(Node3D group)
	{
		if (Engine_UiTheme.FindByName(group, ButtonName) is not Node3D ok ||
			Engine_UiTheme.FindByName(group, CancelName) is not Node3D cancel ||
			Engine_UiTheme.FindByName(group, IconName) is not Node3D cancelIcon ||
			Engine_UiTheme.FindByName(group, ThirdName) is not Node3D third ||
			Engine_UiTheme.FindByName(group, ThirdIconName) is not Node3D thirdIcon ||
			Engine_UiTheme.FindByName(group, AcceptIconName) is not Node3D okIcon)
		{
			return "rows NOT laid";
		}

		float stepY = cancel.Position.Y - ok.Position.Y;
		float iconStepY = cancelIcon.Position.Y - okIcon.Position.Y;
		third.Position = cancel.Position + new Vector3(0f, stepY, 0f);
		thirdIcon.Position = cancelIcon.Position + new Vector3(0f, iconStepY, 0f);
		return $"third row at {third.Position.Y:0} above cancel {cancel.Position.Y:0}, accept {ok.Position.Y:0}";
	}

	/// <summary>
	/// Re-texts one element in place, keeping every piece of layout the file authored.
	///
	/// THE METADATA IS THE POINT. <see cref="Engine_UiSceneBuilder"/> stamps a text element's
	/// point size, alignment, wrap mode, box and colour onto the node as it builds it, so
	/// re-generating the glyphs from those values reproduces the artist's layout exactly. The
	/// alternative — choosing a size and an alignment here — is inventing a layout next to one
	/// that already exists, which is how a correct string ends up in the wrong place.
	///
	/// Returns a short description for the caller's report.
	/// </summary>
	private static string SetText(Engine_UiTheme? theme, Node3D? group, string name, string display)
	{
		if (theme is null)
		{
			return $"'{name}' has no theme";
		}

		return group is null ? $"'{name}' NOT FOUND" : theme.SetMeshText(group, name, display);
	}

	private static void SetShown(Node3D? group, string name, bool shown)
	{
		if (group is not null && Engine_UiTheme.FindByName(group, name) is Node3D node)
		{
			node.Visible = shown;
		}
	}
}
