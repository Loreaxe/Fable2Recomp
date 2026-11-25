using System;
using System.IO;
using Godot;
using Refii.Launcher.Core;
using Refii.Launcher.Native;

namespace Refii.Launcher;

[Tool]
public partial class InstallDialog : Control
{
	private const int PopupWidth = 700;

	private readonly Refii.Core.Engine_Title _title;
	private readonly Font? _font;
	private readonly int _fontSize;
	private readonly SmartStyle _style;

	private OptionButton _versionOption = null!;
	private Label _versionSummary = null!;
	private LineEdit _isoEdit = null!;
	private LineEdit _xexpEdit = null!;
	private CheckBox _updateCheck = null!;
	private CheckBox _classicShadersCheck = null!;
	private Label _xexpLabel = null!;
	private Label _errorLabel = null!;
	private Button _startButton = null!;
	private PanelContainer _frame = null!;

	private string _dialogError = string.Empty;

	/// <summary>The target title this install wizard configures.</summary>
	public Refii.Core.Engine_Title TargetTitle => _title;

	/// <summary>The ISO, the title update (or empty), and whether to build the classic shaders.</summary>
	public event Action<string, string, bool>? InstallRequested;

	public InstallDialog(Font? font, int fontSize, SmartStyle style)
		: this(Refii.Core.Engine_Title.Active, font, fontSize, style)
	{
	}

	/// <summary>Creates an install wizard scoped explicitly to <paramref name="title"/>.</summary>
	public InstallDialog(Refii.Core.Engine_Title title, Font? font, int fontSize, SmartStyle style)
	{
		_title = title;
		_font = font;
		_fontSize = fontSize;
		_style = style;
	}

	public override void _Ready()
	{
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Stop;

		var dim = new ColorRect { Color = new Color(0.08f, 0.06f, 0.04f, 0.65f) };
		dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		AddChild(dim);

		var centre = new CenterContainer();
		centre.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		AddChild(centre);

		_frame = new PanelContainer { CustomMinimumSize = new Vector2(PopupWidth, 0) };
		var box = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentGlassStrong,
			BorderColor = LauncherTokens.Gold,
			ShadowColor = new Color(0, 0, 0, 0.55f),
			ShadowSize = 24,
			ShadowOffset = new Vector2(0, 8),
		};
		box.SetBorderWidthAll(1);
		box.SetCornerRadiusAll(LauncherTokens.RadiusMd);
		box.ContentMarginLeft = 24;
		box.ContentMarginRight = 24;
		box.ContentMarginTop = 18;
		box.ContentMarginBottom = 20;
		_frame.AddThemeStyleboxOverride("panel", box);
		centre.AddChild(_frame);

		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 10);
		_frame.AddChild(column);

		Label heading = MakeLabel("Install " + _title.Name);
		var headingBar = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) };
		headingBar.BorderColor = LauncherTokens.Gold;
		headingBar.BorderWidthBottom = 2;
		headingBar.ContentMarginBottom = 6;
		heading.AddThemeStyleboxOverride("normal", headingBar);
		heading.AddThemeFontSizeOverride("font_size", _fontSize + 4);
		heading.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		column.AddChild(heading);

		Label intro = MakeLabel(
			"Select your game version and its ISO. To install with the title update, tick the " +
			"box and select either the default.xexp itself or the package it came in, under any " +
			"name. Other pressings are not supported. " + SupportedGame.EasingNote);
		intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		column.AddChild(intro);

		// THE PRESSING IS THE PLAYER'S CHOICE. The dropdown names each supported disc once; the
		// disc and the update are then checked against that one, so a wrong pick is reported as
		// a wrong pick and the list grows without the text above growing with it.
		column.AddChild(MakeLabel("Game version"));
		_versionOption = MakeDropdown();
		foreach (SupportedGame.Pressing pressing in SupportedGame.PressingsFor(_title))
		{
			_versionOption.AddItem(pressing.DisplayName);
		}

		_versionOption.Selected = 0;
		_versionOption.ItemSelected += _ =>
		{
			_dialogError = string.Empty;
			RefreshState();
		};
		column.AddChild(_versionOption);

		_versionSummary = MakeLabel(string.Empty);
		_versionSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_versionSummary.AddThemeColorOverride("font_color", LauncherTokens.Sepia);
		column.AddChild(_versionSummary);

		column.AddChild(MakeLabel("Game ISO"));
		_isoEdit = AddPathRow(column, () => BrowseForFile(
			"Select the " + _title.Name + " ISO", new[] { "*.iso ; Disc image" }, _isoEdit));

		// The tick decides which build of the game is installed: with the title update the
		// patch is copied beside the executable and the TU1 config is used; without it the
		// disc executable is recompiled as it is, from its own config. Unticked by default:
		// the player opts in with the update they have.
		_updateCheck = new CheckBox
		{
			Text = "Install with the title update",
			ButtonPressed = false,
		};
		_updateCheck.AddThemeFontSizeOverride("font_size", _fontSize);
		_updateCheck.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		_updateCheck.AddThemeColorOverride("font_pressed_color", LauncherTokens.Walnut);
		_updateCheck.AddThemeColorOverride("font_hover_color", LauncherTokens.Walnut);
		_updateCheck.AddThemeColorOverride("font_hover_pressed_color", LauncherTokens.Walnut);
		_updateCheck.AddThemeColorOverride("font_disabled_color", LauncherTokens.Sepia);
		// White box whenever the update is available: the theme's own icons are grey, so the
		// enabled states get drawn ones. The disabled grey is left to the theme.
		_updateCheck.AddThemeIconOverride("unchecked", MakeCheckIcon(ticked: false));
		_updateCheck.AddThemeIconOverride("checked", MakeCheckIcon(ticked: true));

		_updateCheck.Toggled += _ => RefreshState();
		column.AddChild(_updateCheck);

		// The classic renderer draws with the game's own shaders, translated from the installed
		// shader bank; the remaster does not need them. A player who skips them here can build
		// them later from the Reinstall tab's Compile Shaders.
		_classicShadersCheck = new CheckBox
		{
			Text = "Build the classic shaders (for the original look; can be done later)",
			ButtonPressed = false,
		};
		_classicShadersCheck.AddThemeFontSizeOverride("font_size", _fontSize);
		_classicShadersCheck.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		_classicShadersCheck.AddThemeColorOverride("font_pressed_color", LauncherTokens.Walnut);
		_classicShadersCheck.AddThemeColorOverride("font_hover_color", LauncherTokens.Walnut);
		_classicShadersCheck.AddThemeColorOverride("font_hover_pressed_color", LauncherTokens.Walnut);
		_classicShadersCheck.AddThemeIconOverride("unchecked", MakeCheckIcon(ticked: false));
		_classicShadersCheck.AddThemeIconOverride("checked", MakeCheckIcon(ticked: true));
		column.AddChild(_classicShadersCheck);

		_xexpLabel = MakeLabel("Title update (.xexp or package)");
		column.AddChild(_xexpLabel);
		_xexpEdit = AddPathRow(column, () => BrowseForFile(
			"Select the title update",
			new[] { "*.xexp ; Title update patch", "* ; Title update package" }, _xexpEdit));

		_errorLabel = MakeLabel(string.Empty);
		_errorLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_errorLabel.Visible = false;
		column.AddChild(_errorLabel);

		column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });

		var buttonRow = new HBoxContainer();
		buttonRow.AddThemeConstantOverride("separation", 8);
		column.AddChild(buttonRow);

		_startButton = MakeButton("Start Install", 160, isPrimary: true);
		_startButton.Disabled = true;
		_startButton.Pressed += OnStartPressed;
		buttonRow.AddChild(_startButton);

		Button cancel = MakeButton("Cancel", 110);
		cancel.Pressed += Dismiss;
		buttonRow.AddChild(cancel);

		RefreshState();
	}

	private void Dismiss()
	{
		_dialogError = string.Empty;
		QueueFree();
	}

	private Button MakeButton(string text, int minimumWidth, bool isPrimary = false)
	{
		var button = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(minimumWidth, 38),
			MouseDefaultCursorShape = CursorShape.PointingHand,
		};
		Apply(button);

		if (isPrimary)
		{
			var btnNormal = new StyleBoxFlat
			{
				BgColor = LauncherTokens.Emerald,
				BorderColor = LauncherTokens.Gold,
				ShadowColor = new Color(LauncherTokens.Gold, 0.25f),
				ShadowSize = 8,
			};
			btnNormal.SetBorderWidthAll(1);
			btnNormal.SetCornerRadiusAll(LauncherTokens.RadiusSm);
			btnNormal.ContentMarginLeft = 14;
			btnNormal.ContentMarginRight = 14;

			var btnHover = new StyleBoxFlat
			{
				BgColor = LauncherTokens.EmeraldLit,
				BorderColor = LauncherTokens.GoldBright,
				ShadowColor = new Color(LauncherTokens.Gold, 0.4f),
				ShadowSize = 12,
			};
			btnHover.SetBorderWidthAll(1);
			btnHover.SetCornerRadiusAll(LauncherTokens.RadiusSm);
			btnHover.ContentMarginLeft = 14;
			btnHover.ContentMarginRight = 14;

			button.AddThemeStyleboxOverride("normal", btnNormal);
			button.AddThemeStyleboxOverride("hover", btnHover);
			button.AddThemeStyleboxOverride("pressed", btnNormal);
			button.AddThemeStyleboxOverride("disabled", Fill(new Color(0.2f, 0.2f, 0.2f, 0.35f)));
			button.AddThemeColorOverride("font_color", LauncherTokens.Cream);
			button.AddThemeColorOverride("font_disabled_color", LauncherTokens.Sepia);
		}
		else
		{
			var btnNormal = new StyleBoxFlat
			{
				BgColor = new Color(0, 0, 0, 0),
				BorderColor = LauncherTokens.Sepia,
			};
			btnNormal.SetBorderWidthAll(1);
			btnNormal.SetCornerRadiusAll(LauncherTokens.RadiusSm);
			btnNormal.ContentMarginLeft = 14;
			btnNormal.ContentMarginRight = 14;

			var btnHover = new StyleBoxFlat
			{
				BgColor = new Color(LauncherTokens.Gold, 0.15f),
				BorderColor = LauncherTokens.Gold,
			};
			btnHover.SetBorderWidthAll(1);
			btnHover.SetCornerRadiusAll(LauncherTokens.RadiusSm);
			btnHover.ContentMarginLeft = 14;
			btnHover.ContentMarginRight = 14;

			button.AddThemeStyleboxOverride("normal", btnNormal);
			button.AddThemeStyleboxOverride("hover", btnHover);
			button.AddThemeStyleboxOverride("pressed", btnHover);
			button.AddThemeStyleboxOverride("disabled", Fill(new Color(0.3f, 0.3f, 0.3f, 0.35f)));
			button.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
			button.AddThemeColorOverride("font_hover_color", LauncherTokens.GoldInk);
			button.AddThemeColorOverride("font_disabled_color", LauncherTokens.Sepia);
		}
		return button;
	}

	private static StyleBoxFlat Fill(Color color)
	{
		var box = new StyleBoxFlat { BgColor = color };
		box.SetCornerRadiusAll(LauncherTokens.RadiusSm);
		box.ContentMarginLeft = 10;
		box.ContentMarginRight = 10;
		box.ContentMarginTop = 6;
		box.ContentMarginBottom = 6;
		return box;
	}

	private LineEdit AddPathRow(VBoxContainer column, Action onBrowse)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 8);
		column.AddChild(row);

		var edit = new LineEdit
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0, 36),
		};
		Apply(edit);
		var editBox = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentLit,
			BorderColor = new Color(LauncherTokens.Gold, 0.4f),
		};
		editBox.SetBorderWidthAll(1);
		editBox.SetCornerRadiusAll(LauncherTokens.RadiusSm);
		editBox.ContentMarginLeft = 10;
		editBox.ContentMarginRight = 10;

		edit.AddThemeStyleboxOverride("normal", editBox);
		edit.AddThemeStyleboxOverride("focus", editBox);
		edit.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		edit.TextChanged += _ => RefreshState();
		row.AddChild(edit);

		Button browse = MakeButton("Browse...", 110);
		browse.Pressed += onBrowse;
		row.AddChild(browse);

		return edit;
	}

	private void BrowseForFile(string title, string[] filters, LineEdit target)
	{
		string pathHint = !string.IsNullOrWhiteSpace(target.Text) ? target.Text : _isoEdit.Text;
		NativeFilePicker.OpenFile(
			title,
			filters,
			pathHint,
			"LastInstallBrowseDir",
			path =>
			{
				if (!IsInstanceValid(this) || !IsInstanceValid(target))
				{
					return;
				}

				target.Text = path;
				_dialogError = string.Empty;
				RefreshState();
			});
	}

	private SupportedGame.Pressing[] Pressings => SupportedGame.PressingsFor(_title);

	private SupportedGame.Pressing SelectedPressing =>
		Pressings[Math.Clamp(_versionOption.Selected, 0, Pressings.Length - 1)];

	private void OnStartPressed()
	{
		string isoPath = _isoEdit.Text;
		bool withUpdate = _updateCheck.ButtonPressed && SelectedPressing.HasTitleUpdate;
		string xexpPath = withUpdate ? _xexpEdit.Text : string.Empty;
		_dialogError = string.Empty;

		if (!File.Exists(isoPath))
		{
			_dialogError = "The selected ISO path does not exist.";
		}
		else if (!withUpdate)
		{
			if (DiscIsSupported(_title, isoPath, SelectedPressing, out _, out string unsupported))
			{
				InstallRequested?.Invoke(isoPath, string.Empty, _classicShadersCheck.ButtonPressed);
				QueueFree();
				return;
			}

			_dialogError = unsupported;
		}
		else if (!File.Exists(xexpPath))
		{
			_dialogError = "The selected title update path does not exist.";
		}
		else if (TitleUpdateFitsDisc(_title, isoPath, xexpPath, SelectedPressing, out string mismatch))
		{
			InstallRequested?.Invoke(isoPath, xexpPath, _classicShadersCheck.ButtonPressed);
			QueueFree();
			return;
		}
		else
		{
			_dialogError = mismatch;
		}

		RefreshState();
	}

	/// <summary>
	/// The one check that can save the player an hour: the disc must be the supported
	/// pressing and the patch the supported title update. Both identities come from file
	/// headers, so this costs a few reads, not an extraction.
	/// </summary>
	/// <remarks>
	/// The disc is judged first: a wrong pressing is the answer the player needs, whatever
	/// update they picked to go with it.
	/// </remarks>
	private static bool TitleUpdateFitsDisc(
		Refii.Core.Engine_Title title,
		string isoPath, string updatePath, SupportedGame.Pressing pressing, out string error)
	{
		if (!DiscIsSupported(title, isoPath, pressing, out XexIdentity executable, out error))
		{
			return false;
		}

		if (!TitleUpdate.TryLoad(updatePath, title, out TitleUpdate update, out error))
		{
			return false;
		}

		return SupportedGame.IsTitleUpdate(title, update.Identity, pressing, out error) &&
			update.AppliesTo(executable, out error);
	}

	private static bool DiscIsSupported(
		Refii.Core.Engine_Title title,
		string isoPath, SupportedGame.Pressing pressing, out XexIdentity executable, out string error)
	{
		executable = new XexIdentity();

		if (!Xiso.XisoExtractor.TryReadRootFileHead(
				isoPath, "default.xex", 64 * 1024, out byte[] head, out string isoError))
		{
			error = "The ISO's game executable could not be read: " + isoError;
			return false;
		}

		if (!TitleUpdate.TryReadExecutable(head, out executable, out string xexError))
		{
			error = "The ISO's game executable could not be identified: " + xexError;
			return false;
		}

		return SupportedGame.IsExecutable(title, executable, pressing, out error);
	}

	private void RefreshState()
	{
		SupportedGame.Pressing pressing = SelectedPressing;
		_versionSummary.Text = pressing.Summary;

		// A pressing without a confirmed update has nothing to tick: the box is greyed, not
		// hidden, so the player sees the update exists and is simply not available here.
		_updateCheck.Disabled = !pressing.HasTitleUpdate;
		if (!pressing.HasTitleUpdate)
		{
			_updateCheck.SetPressedNoSignal(false);
		}

		_updateCheck.Text = pressing.HasTitleUpdate
			? "Install with the title update (" + pressing.TitleUpdateName + ")"
			: "Install with the title update (none confirmed for this version)";

		bool withUpdate = _updateCheck.ButtonPressed && pressing.HasTitleUpdate;
		_xexpLabel.Visible = withUpdate;
		if (_xexpEdit.GetParent() is Control updateRow)
		{
			updateRow.Visible = withUpdate;
		}

		_startButton.Disabled = _isoEdit.Text.Length == 0 ||
			(withUpdate && _xexpEdit.Text.Length == 0);

		_errorLabel.Visible = _dialogError.Length != 0;
		_errorLabel.Text = _dialogError.Length == 0
			? string.Empty
			: _dialogError;
	}

	private void Apply(Control control)
	{
		if (_font is not null)
		{
			control.AddThemeFontOverride("font", _font);
		}

		control.AddThemeFontSizeOverride("font_size", _fontSize);
	}

	private Label MakeLabel(string text)
	{
		var label = new Label { Text = text };
		Apply(label);
		label.AddThemeColorOverride("font_color", _style.Text);
		return label;
	}

	/// <summary>
	/// A white box with a gold rule, with or without a walnut tick, at the size the theme draws
	/// its own check icons.
	/// </summary>
	private static ImageTexture MakeCheckIcon(bool ticked)
	{
		const int size = 20;
		var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		Color border = LauncherTokens.Gold;
		Color face = Colors.White;
		Color ink = LauncherTokens.Walnut;

		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				// Rounded corners: the four corner pixels stay clear.
				bool corner = (x == 0 || x == size - 1) && (y == 0 || y == size - 1);
				if (corner)
				{
					continue;
				}

				bool edge = x == 0 || y == 0 || x == size - 1 || y == size - 1;
				image.SetPixel(x, y, edge ? border : face);
			}
		}

		if (ticked)
		{
			// Two strokes: down to the base, then up to the far corner.
			DrawStroke(image, new Vector2(4.5f, 10f), new Vector2(8.5f, 14f), 1.3f, ink);
			DrawStroke(image, new Vector2(8.5f, 14f), new Vector2(15.5f, 5.5f), 1.3f, ink);
		}

		return ImageTexture.CreateFromImage(image);
	}

	private static void DrawStroke(Image image, Vector2 from, Vector2 to, float radius, Color ink)
	{
		Vector2 direction = to - from;
		float lengthSquared = direction.LengthSquared();
		for (int y = 0; y < image.GetHeight(); y++)
		{
			for (int x = 0; x < image.GetWidth(); x++)
			{
				var point = new Vector2(x + 0.5f, y + 0.5f);
				float t = lengthSquared == 0 ? 0 : Mathf.Clamp((point - from).Dot(direction) / lengthSquared, 0, 1);
				float distance = point.DistanceTo(from + direction * t);
				if (distance <= radius)
				{
					image.SetPixel(x, y, ink);
				}
				else if (distance <= radius + 1)
				{
					// One pixel of coverage so the stroke is not jagged.
					Color under = image.GetPixel(x, y);
					image.SetPixel(x, y, under.Lerp(ink, radius + 1 - distance));
				}
			}
		}
	}

	/// <summary>A dropdown dressed like the path fields: parchment face, gold rule, walnut ink.</summary>
	private OptionButton MakeDropdown()
	{
		var option = new OptionButton
		{
			CustomMinimumSize = new Vector2(0, 36),
			FitToLongestItem = false,
			TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
			MouseDefaultCursorShape = CursorShape.PointingHand,
		};
		Apply(option);

		var face = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentLit,
			BorderColor = new Color(LauncherTokens.Gold, 0.4f),
		};
		face.SetBorderWidthAll(1);
		face.SetCornerRadiusAll(LauncherTokens.RadiusSm);
		face.ContentMarginLeft = 10;
		face.ContentMarginRight = 10;

		var faceHover = new StyleBoxFlat
		{
			BgColor = LauncherTokens.ParchmentLit,
			BorderColor = LauncherTokens.Gold,
		};
		faceHover.SetBorderWidthAll(1);
		faceHover.SetCornerRadiusAll(LauncherTokens.RadiusSm);
		faceHover.ContentMarginLeft = 10;
		faceHover.ContentMarginRight = 10;

		option.AddThemeStyleboxOverride("normal", face);
		option.AddThemeStyleboxOverride("hover", faceHover);
		option.AddThemeStyleboxOverride("pressed", faceHover);
		option.AddThemeStyleboxOverride("focus", faceHover);
		option.AddThemeColorOverride("font_color", LauncherTokens.Walnut);
		option.AddThemeColorOverride("font_hover_color", LauncherTokens.Walnut);
		option.AddThemeColorOverride("font_pressed_color", LauncherTokens.Walnut);
		option.AddThemeColorOverride("font_focus_color", LauncherTokens.Walnut);

		// The popup is a Window, not a Control: it takes the same font by its own overrides.
		PopupMenu popup = option.GetPopup();
		if (_font is not null)
		{
			popup.AddThemeFontOverride("font", _font);
		}

		popup.AddThemeFontSizeOverride("font_size", _fontSize);
		return option;
	}
}
