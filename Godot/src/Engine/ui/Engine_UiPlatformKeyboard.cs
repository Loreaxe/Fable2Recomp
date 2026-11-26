namespace Refii.Ui;

using Godot;
using Refii.Core;
using Refii.Host;

/// <summary>
/// The platform's text entry: a guest asks the console for a string (`XamShowKeyboardUI`) and the host
/// is the console. A title's hook on the guest's keyboard thunk leaves the request pending and states
/// it as `@Keyboard.Open(user, title, description, default, max units)` (Fable 2
/// Fable2/src/vfs/src/hooks/keyboard_ui.cpp — the villager card's rename and the dog's naming; Fable 3
/// Fable3/src/hooks/src/keyboard_ui.cpp — the interaction mode's rename); the answer goes back as the
/// guest tick action `keyboard:&lt;text&gt;` or `keyboard-cancel`, which completes the guest's XOVERLAPPED on
/// its own thread. What the guest does with the text is its own. The entry is the shared
/// <see cref="Engine_UiTextEntry"/>; while it is open the title keeps the pad from the guest and from
/// its own menus (<see cref="IsOpen"/>), as the console's keyboard does.
/// </summary>
public partial class Engine_UiPlatformKeyboard : Control
{
	/// <summary>One open request, as the hook stated it.</summary>
	public readonly record struct Request(string Title, string Description, string Default, int MaxUnits);

	private static Request? _pending;
	private static ulong _generation;

	/// <summary>Whether the entry is up: the guest's pad is held neutral meanwhile.</summary>
	public static bool IsOpen { get; private set; }

	/// <summary>Routes the hook's statement; called with the title's other routes.</summary>
	public static void Register(Engine_UiGuiNatives router) =>
		router.Register("@Keyboard.Open", call =>
		{
			_pending = new Request(call.Argument(1).Text, call.Argument(2).Text, call.Argument(3).Text,
				System.Math.Max(1, (int)call.Argument(4).Number));
			++_generation;
		});

	private Engine_UiTextEntry _entry = null!;
	private ulong _shown;

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
		_entry = new Engine_UiTextEntry { Name = "Entry" };
		_entry.Accepted += text => Answer(text);
		_entry.Cancelled += () => Answer(null);
		AddChild(_entry);
	}

	public override void _Process(double delta)
	{
		if (_shown != _generation && _pending is { } request)
		{
			_shown = _generation;
			// The guest's buffer holds MaxUnits code units with the terminator.
			_entry.Open(request.Title, request.Description, request.Default, request.MaxUnits - 1);
			IsOpen = true;
			GD.Print($"[platform-keyboard] open '{request.Title}' (max {request.MaxUnits - 1} character(s))");
		}
	}

	private void Answer(string? text)
	{
		IsOpen = false;
		_pending = null;
		Engine_TitleGame.Active.Input.HoldUntilReleased();
		uint depth = HostBridge.PostFrontEndAction(text is null ? "keyboard-cancel" : "keyboard:" + text);
		GD.Print($"[platform-keyboard] answered {(text is null ? "cancel" : $"'{text}'")} (queue depth {depth})");
	}
}
