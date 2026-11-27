namespace Refii.Titles.Fable3;

using System;
using System.Collections.Generic;
using System.Globalization;
using Refii.Ui;

/// <summary>
/// THE SANCTUARY'S LIST (mode 3, CInteractionModeGUIWorld) as the mode keeps it, from what the guest
/// states when it has filled the mode (`@Interaction.World.Parsed(player, cursor, holds, rows)`,
/// Fable3/src/hooks/src/interaction_guiworld.cpp): each kept row as it was pushed, the cursor as stored,
/// each slot's hold flag as stored. Nothing is parsed from the scripts' table here — the guest's own
/// parser drops the rows without a TextTag or an Icon and clamps CurrentListIndex, and Godot sees only
/// its result. Pure — no Godot type.
/// </summary>
/// <remarks>
/// <para>A PRESS. Godot hands every A / B / X / Y to the guest's own HandleInput, which takes a label, the
/// slot's enable and the GUI's state into account itself (TU3 0x82969DC4, 0x8296A198, 0x8296A0CC,
/// 0x8296A000). The one press Godot holds back is a hold slot's (`P_Fill`): the hold step ahead of
/// HandleInput (sub_82970A98 in sub_82970900) takes it from the guest's pad, which keeps it.</para>
/// <para>THE BARS. The scripts' `TriggersVisible` shows the bar at IM+32, which is the BUMPER class (LB / RB
/// released post 129 / 130), and `BumpersVisible` the one at IM+36, the TRIGGER class (LT / RT released,
/// 131 / 132): the levers name the physical buttons.</para>
/// </remarks>
public sealed class Fable3GuiWorldModel
{
	public readonly record struct Row(string TextTag, string Icon, string Message, string SelectedTag, string SelectedHeldTag);

	/// <summary>The face buttons and START, by the lever verb they post.</summary>
	public enum Button
	{
		A = 0,
		B = 1,
		X = 2,
		Y = 3,
		Start = 4,
	}

	public IReadOnlyList<Row> Rows { get; private init; } = Array.Empty<Row>();

	/// <summary>The list's cursor as the guest stored it (0 when it stored none: a table with no list).</summary>
	public int Cursor { get; private init; }

	private readonly bool[] _holds = new bool[4];

	/// <summary>Whether slot <paramref name="button"/> is a hold button (`P_Fill`).</summary>
	public bool Holds(Button button) => button != Button.Start && _holds[(int)button];

	/// <summary>
	/// The model the statement states: <paramref name="rows"/> holds `&lt;n&gt;.TextTag`, `.Icon`, `.Message`,
	/// `.SelectedTag`, `.SelectedHeldTag` for n = 1.. in push order, <paramref name="holds"/> bit k slot k's
	/// P_Fill (A B X Y). A cursor outside the rows (the guest leaves one unclamped with no rows) reads as 0.
	/// </summary>
	public static Fable3GuiWorldModel Stated(int cursor, int holds, Engine_UiModel.GuiNativeArgument rows)
	{
		var kept = new List<Row>();
		IReadOnlyDictionary<string, Engine_UiModel.GuiNativeArgument> fields =
			rows.Fields ?? new Dictionary<string, Engine_UiModel.GuiNativeArgument>();
		for (int n = 1; fields.ContainsKey(Key(n, "TextTag")); ++n)
		{
			kept.Add(new Row(rows.FieldText(Key(n, "TextTag"), string.Empty), rows.FieldText(Key(n, "Icon"), string.Empty),
				rows.FieldText(Key(n, "Message"), string.Empty), rows.FieldText(Key(n, "SelectedTag"), string.Empty),
				rows.FieldText(Key(n, "SelectedHeldTag"), string.Empty)));
		}

		var model = new Fable3GuiWorldModel
		{
			Rows = kept.ToArray(),
			Cursor = cursor >= 0 && cursor < kept.Count ? cursor : 0,
		};
		for (int slot = 0; slot < model._holds.Length; ++slot)
		{
			model._holds[slot] = (holds & (1 << slot)) != 0;
		}

		return model;
	}

	private static string Key(int n, string field) => n.ToString(CultureInfo.InvariantCulture) + "." + field;

	/// <summary>The verb a press of <paramref name="button"/> posts, or null for a hold slot (the guest's pad keeps it).</summary>
	public string? Verb(Button button) => button switch
	{
		Button.Start => "start",
		_ when Holds(button) => null,
		Button.A => "a",
		Button.B => "b",
		Button.X => "x",
		_ => "y",
	};

	/// <summary>
	/// Whether a press of <paramref name="presser"/>'s pad is the Sanctuary's, as the guest's owner gate has it: the
	/// base input (TU3 sub_82970900 / disc sub_82961CA8) answers 0 unless sub_829704A0 / sub_82961848 passes, and
	/// that passes for the hero the mode was sent by — `GetEntitySentBy(IM)`'s player component (+20 → +36) equal to
	/// the pressing player (TU3 0x829708D0..0x829708F4, disc 0x82961C78..0x82961C9C) — which is the owner the
	/// parser states (<see cref="Fable3GuiWorld.Player"/>). In a couch session (`*(*(dword_835979F8+16)+128) == 1`
	/// TU3 0x829705F4 / disc `+124` 0x8296199C) B (action 75) also passes for the hero the mode was sent to
	/// (`GetEntitySentTo`, sub_82289B08 / sub_824ABBF8, 0x82970620..0x82970760 / 0x829619C8..0x82961B08), which is
	/// not stated: another player's B is posted and the guest's gate decides it. Every other press of another
	/// player's pad the guest answers 0, so it is not posted.
	/// </summary>
	public static bool TakesPress(int presser, int owner, Button button) => presser == owner || button == Button.B;

	/// <summary>`guiworld:&lt;p&gt;:&lt;verb&gt;`.</summary>
	public static string Lever(int player, string verb) => "guiworld:" + Fable3ActingPlayer.Field(player) + ":" + verb;

	/// <summary>`guiworld:&lt;p&gt;:cursor:&lt;row&gt;`.</summary>
	public static string CursorLever(int player, int row) =>
		Lever(player, "cursor:" + row.ToString(CultureInfo.InvariantCulture));

	/// <summary>`guiworld:&lt;p&gt;:dye-value:&lt;value&gt;`.</summary>
	public static string DyeValueLever(int player, float value) =>
		Lever(player, "dye-value:" + value.ToString("R", CultureInfo.InvariantCulture));

	/// <summary>A stick's direction as the guest's sub_82B74918 reads it: x first, then y (up positive), past ±0.6.</summary>
	public static string StickDirection(float x, float y)
	{
		const float threshold = 0.6f;
		if (x > threshold)
		{
			return "right";
		}

		if (x < -threshold)
		{
			return "left";
		}

		if (y < -threshold)
		{
			return "down";
		}

		return y > threshold ? "up" : "none";
	}

	/// <summary>
	/// THE LIST'S STEP REPEAT on a held stick: a step at once, then one per <see cref="RepeatEvery"/>
	/// after <see cref="RepeatAfter"/>. Pure; the caller hands it the frame's time.
	/// </summary>
	public sealed class StepRepeat
	{
		public const double RepeatAfter = 0.35;
		public const double RepeatEvery = 0.12;

		private int _held;
		private double _next;

		/// <summary>The step this frame (-1 up, +1 down, 0 none) for a stick held at <paramref name="direction"/> (-1, 0, +1).</summary>
		public int Step(int direction, double now)
		{
			if (direction == 0)
			{
				_held = 0;
				return 0;
			}

			if (direction != _held)
			{
				_held = direction;
				_next = now + RepeatAfter;
				return direction;
			}

			if (now < _next)
			{
				return 0;
			}

			_next = now + RepeatEvery;
			return direction;
		}
	}
}
