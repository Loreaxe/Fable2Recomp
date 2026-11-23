using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// Time Control's clock — the screen-sized dial that sweeps once round while the spell runs.
/// Not a Gameface element: `hud.bgf` has no such piece, and no registry carries its value. The
/// guest keeps it as a record in the hero's component 118 at +484 (two sprite segments, built
/// by `sub_8260B708`, paced by `sub_8260BBA0`, turned by `sub_8260BCB8` on the 15 Hz tick and
/// pushed as 2D sprites by `sub_8260BA60`), so the HUD rebuild has nothing to follow. Godot
/// draws it here from that draw's own statement of the record (`@SlowTime.Draw`,
/// slow_time_dial_stated.cpp) — the guest's angles, alpha, rectangles and textures, not a clock
/// of its own.
///
/// The record, base `dial = comp118 + 484` (`sub_822B5150` reaches it from the hero):
/// <code>
///   +4   active        set by sub_8260B638 while the caster's component 56 says +618 (spell up)
///   +12  alpha         chases +16 by 0.75/15 per tick; +16 is 0.8 on start, 0 at the end,
///                      and 0.24 is written straight into +12 on a recast
///   +32  +40·i  segment i (0, 1): prim handle (-1 = none), rect top/left/bottom/right in the
///                      guest's 1280x720 pixels, texture hash, angle (rad), velocity, accel
/// </code>
/// Geometry (`sub_8260B708`, 0x8260b740..0x8260b7f8): both segments are squares centred on the
/// backbuffer, half extent `(1.4 + 0.4·i) · height/2` — 504 and 648 px — textured
/// `art\gui\hud\slowtimetest01.tex` and `02`. Draw angle (`sub_8260BA60`, 0x8260ba60 loop) is
/// `base_i·π/180 + stated`, with `unk_8209CFD4` = {90, 0} degrees. Segment 0's velocity is
/// `2π / remaining seconds` (0x8260bc78) and it stops after exactly one turn (0x8260bdc0), so the
/// sweep IS the countdown; segment 1 eases in and out over the first and last `min(rem/2, 2)` s.
/// </summary>
public sealed partial class Engine_HudSlowTimeDial : Node3D
{
	private static readonly StringName Param_tint = "tint";
	private static readonly StringName Param_tex = "tex";

	private static readonly Shader MarkerShader =
		GD.Load<Shader>("res://src/Engine/ui/ui_world_marker.gdshader");

	/// <summary>
	/// One sprite segment as the guest states it: its centre and half extent in guest pixels
	/// (relative to the backbuffer centre, y down), its texture hash, stated angle and angular velocity.
	/// </summary>
	public readonly record struct Segment(Vector2 Centre, float HalfExtent, uint Texture, float Angle, float Velocity = 0f);

	/// <summary>The dial this frame: the shared alpha (0..0.8) and its two segments, either of
	/// which is null while its sprite is not built.</summary>
	public readonly record struct Dial(float Alpha, Segment? First, Segment? Second);

	/// <summary>Resolves a lowercase resource hash to a texture, or null.</summary>
	public Func<uint, Texture2D?>? ResolveTexture { get; set; }

	/// <summary>HUD canvas units per guest pixel (the guest's backbuffer is 720 tall).</summary>
	public float UnitsPerGuestPixel { get; set; } = 1f;

	private const int Segments = 2;

	/// <summary>`unk_8209CFD4`: the per-segment base angle, degrees — 90 for the sweep, 0 for
	/// the ring.</summary>
	private static readonly float[] BaseDegrees = { 90f, 0f };

	private Dial? _dial;
	private readonly MeshInstance3D[] _quads = new MeshInstance3D[Segments];
	private readonly ShaderMaterial[] _materials = new ShaderMaterial[Segments];
	private readonly Dictionary<uint, Texture2D?> _textures = new();
	private readonly float[] _drawnAngle = { float.NaN, float.NaN };
	private readonly float[] _lastStatedAngle = { float.NaN, float.NaN };

	public override void _Ready()
	{
		// After the camera, as the world sprites and the reticule are.
		ProcessPriority = 880;
		for (int i = 0; i < Segments; ++i)
		{
			var material = new ShaderMaterial { Shader = MarkerShader };
			var quad = new MeshInstance3D
			{
				Name = $"Segment{i}",
				Mesh = new QuadMesh { Size = Vector2.One },
				MaterialOverride = material,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				Visible = false,
			};
			AddChild(quad);
			_quads[i] = quad;
			_materials[i] = material;
		}
	}

	/// <summary>States the dial, null when the guest shows none.</summary>
	public void Feed(Dial? dial) => _dial = dial;

	private Texture2D? TextureFor(uint hash)
	{
		if (!_textures.TryGetValue(hash, out Texture2D? texture))
		{
			texture = ResolveTexture?.Invoke(hash);
			_textures[hash] = texture;
			if (texture is null)
			{
				GD.Print($"[hud-slowtime] texture {hash:X8} did not resolve — the dial draws untextured");
			}
		}

		return texture;
	}

	/// <summary>
	/// A mod's size for this widget (<c>ui/hud.json</c> <c>scale</c>, <see cref="Engine_HudWidgets"/>):
	/// each mark is drawn this much larger about its own point; 1 is the game's.
	/// </summary>
	public float PieceScale { get; set; } = 1f;

	public override void _Process(double delta)
	{
		if (_dial is not { } dial || dial.Alpha <= 0.004f)
		{
			_drawnAngle[0] = float.NaN;
			_drawnAngle[1] = float.NaN;
			HideSegments();
			return;
		}

		float scale = UnitsPerGuestPixel * PieceScale;
		for (int i = 0; i < Segments; ++i)
		{
			Segment? stated = i == 0 ? dial.First : dial.Second;
			MeshInstance3D quad = _quads[i];
			if (stated is not { } segment || segment.HalfExtent <= 0f)
			{
				_drawnAngle[i] = float.NaN;
				quad.Visible = false;
				continue;
			}

			if (float.IsNaN(_drawnAngle[i]) || segment.Angle != _lastStatedAngle[i])
			{
				_lastStatedAngle[i] = segment.Angle;
				if (float.IsNaN(_drawnAngle[i]) || Mathf.Abs(_drawnAngle[i] - segment.Angle) > 0.5f)
				{
					_drawnAngle[i] = segment.Angle;
				}
				else
				{
					_drawnAngle[i] = Mathf.Lerp(_drawnAngle[i], segment.Angle, 0.5f);
				}
			}
			else if (segment.Velocity != 0f)
			{
				_drawnAngle[i] += segment.Velocity * (float)delta;
			}

			// Guest screen space is y down; the canvas is y up, so y and the turn both flip.
			float angle = Mathf.DegToRad(BaseDegrees[i]) + _drawnAngle[i];
			quad.Position = new Vector3(segment.Centre.X * scale, -segment.Centre.Y * scale, 0f);
			quad.Rotation = new Vector3(0f, 0f, -angle);
			quad.Scale = new Vector3(2f * segment.HalfExtent * scale, 2f * segment.HalfExtent * scale, 1f);
			quad.Visible = true;

			ShaderMaterial material = _materials[i];
			material.SetShaderParameter(Param_tint, new Color(1f, 1f, 1f, dial.Alpha));
			Texture2D? texture = TextureFor(segment.Texture);
			if (texture is not null)
			{
				material.SetShaderParameter(Param_tex, texture);
			}
		}
	}

	private void HideSegments()
	{
		for (int i = 0; i < Segments; ++i)
		{
			_quads[i].Visible = false;
		}
	}
}
