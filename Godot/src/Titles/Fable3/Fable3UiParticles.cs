namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Ui;

/// <summary>
/// Fable 3's one-shot GUI particles at an element, as the guest states them: `@Ui.Particle(pres,
/// elem, path, name, scale, via)` from `sub_823CE408` TU3 / `sub_823ABEB0` disc
/// (stated_element_hooks.cpp, THE ONE-SHOTS AT AN ELEMENT) — the job press's success and fail bursts
/// on `Bar.Elements.pip.Icon.success` / `.fail`, the guild seal's follower tick. Each waits here for
/// its document's view, which plays it at the element's drawn place (Engine_UiParticles.Burst) on its
/// next frame (<see cref="Fable3HudDocument.Frame"/>).
/// </summary>
public static class Fable3UiParticles
{
	/// <summary>One stated one-shot: the document and path its element was named by, the effect and its size scale.</summary>
	public readonly record struct Shot(string Document, string Path, string Effect, float Scale, string Via,
		(float X, float Y, float Z)? Point = null);

	/// <summary>
	/// The document a stated POINT is played on: the position form's one shipped caller is the HUD's
	/// relationship bar change (TU3 `sub_82605A28` 0x82605D20 / disc `sub_825DAAC0` 0x825DADB8), and
	/// no shipped script calls `GUI.Create2dParticle`.
	/// </summary>
	public const string PointDocument = Fable3Elements.HudDocument;

	private static readonly List<Shot> Pending = new();

	/// <summary>More than a frame's worth waiting means no view drew the document: the oldest go.</summary>
	private const int MaxPending = 32;

	private static readonly HashSet<string> Said = new(System.StringComparer.Ordinal);

	/// <summary>The route for `@Ui.Particle` (Fable3Elements' table).</summary>
	public static void Stated(Engine_UiModel.GuiNativeCall call)
	{
		Add(new Shot(
			Fable3Elements.DocumentOfKey(call.Argument(0).Text), call.Argument(2).Text, call.Argument(3).Text,
			call.Argument(4).Number, call.Argument(5).Text));
	}

	/// <summary>
	/// The route for `@Ui.Particle2d(name, x, y, z, scale, via)`: the position form's GUI-world point
	/// (f1, f2, f3 = the canvas's x, −z, y — sub_82878420's own swap) as the whole canvas's point; Godot's
	/// canvas z is the file's negated (Engine_UiSceneBuilder), so f2 itself.
	/// </summary>
	public static void StatedAtPoint(Engine_UiModel.GuiNativeCall call)
	{
		float x = call.Argument(1).Number;
		float depth = call.Argument(2).Number;
		float y = call.Argument(3).Number;
		Add(new Shot(PointDocument, string.Empty, call.Argument(0).Text, call.Argument(4).Number, call.Argument(5).Text,
			(x, y, depth)));
	}

	private static void Add(Shot shot)
	{
		if (Pending.Count >= MaxPending)
		{
			Pending.RemoveAt(0);
		}

		Pending.Add(shot);
		if (Said.Add(shot.Document + ":" + shot.Effect))
		{
			string at = shot.Point is { } p ? $"canvas ({p.X:0.#}, {p.Y:0.#})" : $"'{shot.Path}'";
			GD.Print($"[fable3-glints] one-shot '{shot.Effect}' stated on {shot.Document} {at} scale {shot.Scale:0.###} (from {shot.Via})");
		}
	}

	/// <summary>Takes the one-shots stated on <paramref name="document"/>, oldest first.</summary>
	public static void Drain(string document, List<Shot> into)
	{
		for (int i = 0; i < Pending.Count; ++i)
		{
			if (Pending[i].Document == document)
			{
				into.Add(Pending[i]);
				Pending.RemoveAt(i--);
			}
		}
	}

	/// <summary>A one-shot whose element no drawn tree holds (or a point with no HUD view), said once per path.</summary>
	public static void Unplaced(Shot shot)
	{
		if (Said.Add("unplaced:" + shot.Document + ":" + shot.Path))
		{
			GD.Print($"[fable3-glints] one-shot '{shot.Effect}' on {shot.Document} '{shot.Path}': no drawn element there — not played");
		}
	}
}
