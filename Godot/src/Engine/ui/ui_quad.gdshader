shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix;

// The one shader every Gameface quad draws through — the native form of the title's own
// `0098_pshader_guiquad`:
//
//     rgb = primary.rgb ; alpha = primary.a
//     if (detail) { rgb = detail.rgb ; alpha *= detail.a }     // blend factor is 1.0
//     rgb -= (1 - tint) * tintAmount                           // SUBTRACTIVE, weighted
//     alpha *= accumulatedOpacity
//
// The blend factor is stored unconditionally as 1.0 (`stfs f0, 0x1A4(r30)` at 0x8286E614), so
// a detail-mapped quad takes its RGB entirely from the detail map and the primary contributes
// only ALPHA — which is why the primary may be the exporter's magenta placeholder. `g_Tinted`
// is likewise set unconditionally (`|= 0x40` at 0x8286E51C): the tint is gated by its WEIGHT,
// not by a flag.
//
// The gamma ramp is deliberately not reproduced — our texture decode already lands in the
// calibrated space, and a second transfer would double-count it.

uniform sampler2D diffuse_tex : source_color, filter_linear_mipmap, repeat_disable, hint_default_white;

// Repeat-enabled: the detail maps tile, and the derived uv set runs outside 0..1.
uniform sampler2D detail_tex : source_color, filter_linear_mipmap, repeat_enable, hint_default_white;

// False on untextured quads (such as solid-tinted backdrops), so the shader skips sampling diffuse_tex.
uniform bool has_diffuse = true;

// The atlas rectangle, already inset to texel centres on the CPU side.
uniform vec2 uv1_offset = vec2(0.0);
uniform vec2 uv1_scale = vec2(1.0);

uniform bool detail_mapped = false;

// The guest seeds this from two randoms per quad so neighbouring frame pieces do not tile in
// lockstep; ours is a hash of the element ordinal, which keeps a screenshot reproducible.
uniform vec2 detail_origin = vec2(0.0);

// `uv1 = |corner_i - corner_j| * 0.0025`, from sub_8286F830 (flt_833210C8 = 0x3B23D70A).
// UNPROVEN which space those corners are in; this assumes the 960x720 virtual space, so a
// 400-unit quad tiles once. Calibrate here if the grain reads wrong.
uniform float detail_scale = 0.0025;

// Lets the detail tiling come off the model matrix rather than an accumulated scale.
uniform float quad_base_size = 100.0;

// .fac field 14 (colour) and field 13 (weight). The weight is 0 on ~85% of records — the
// runtime raises it through the `TintingAmount` attribute.
uniform vec3 tint_rgb = vec3(1.0);
uniform float tint_amount = 0.0;

// A hue rotation in turns, 0 = none. Lets a mod recolour shipped art — the health bar's red as
// a mana bar's blue — without a second texture.
uniform float hue_shift = 0.0;

// The ACCUMULATED opacity down the element chain, not this element's own.
uniform float opacity = 1.0;

// Clip rect in the laid-out UI space: min.xy, max.xy. An element carrying `MaskName` — and,
// via the parent walk in `sub_82427188`, its subtree — is clipped by the named element.
//
// The guest does this as a render-to-texture, not a rect. A discard is exact for all 14
// shipped masks (axis-aligned, zero pivot, single-texel textures) and nothing more.
uniform vec4 clip_rect = vec4(-1e9, -1e9, 1e9, 1e9);

// A controller button cut from a shared atlas (`abyx`) carries the keycap of the key bound to
// it; while the keyboard and mouse are the active device the quad draws that instead of its
// atlas cell. One global flips every glyph at once — no material is walked on a switch.
uniform sampler2D keycap_tex : source_color, filter_linear_mipmap, repeat_disable, hint_default_transparent;
uniform bool has_keycap = false;
global uniform bool ui_keyboard_mode;
// A quad on a surface that is player two's alone (their half of a split screen) follows player two's
// own pad: its keycap is player two's and the switch is player two's global (Engine_UiKeyGlyphs.PlayerTwo).
uniform bool glyph_player_two = false;
global uniform bool ui_keyboard_mode_two;

// A menu seated in one player's half on split screen (Engine_UiPresentationHost.MenuLayer):
// scale in xy, offset in zw. The clip compares in the menu's own laid-out space, so the seat
// comes back out of the world position. Identity everywhere else. A MATERIAL uniform, not an
// `instance uniform`: with the separate render thread, an instance uniform's property list is
// fetched through the render command queue into the caller's stack, and Node.duplicate (the
// UI's template clones) crashed in InstanceUniforms::get_property_list (2026-10-20).
uniform vec4 clip_seat = vec4(1.0, 1.0, 0.0, 0.0);

varying vec2 clip_world;

// The seat a HUD view is drawn in on split screen (Engine_HudView.KeepToSeat): min.xy, max.xy in
// WORLD space, the real canvas the camera draws. A half is a screen of its own, so what a seated
// view places outside its half is off its screen, as it would be past a window edge. Unbounded
// everywhere else, so nothing that is not seated is affected.
uniform vec4 seat_rect = vec4(-1e9, -1e9, 1e9, 1e9);
varying vec2 seat_world;

void vertex() {
	clip_world = ((MODEL_MATRIX * vec4(VERTEX, 1.0)).xy - clip_seat.zw) / clip_seat.xy;
	seat_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xy;
}

void fragment() {
	bool clipped = clip_world.x < clip_rect.x || clip_world.x > clip_rect.z ||
		clip_world.y < clip_rect.y || clip_world.y > clip_rect.w;
	clipped = clipped || seat_world.x < seat_rect.x || seat_world.x > seat_rect.z ||
		seat_world.y < seat_rect.y || seat_world.y > seat_rect.w;

	vec2 uv = UV * uv1_scale + uv1_offset;
	// Precompute gradients in uniform control flow so D3D12/DXIL mipmap selection stays
	// valid and anti-aliased inside the per-quad sampler branches below.
	vec2 uv_dx = dFdx(uv);
	vec2 uv_dy = dFdy(uv);
	vec2 raw_uv_dx = dFdx(UV);
	vec2 raw_uv_dy = dFdy(UV);

	// Laid-out size in UI units, off the model matrix so ancestor scale is included.
	vec2 size = vec2(length(MODEL_MATRIX[0].xyz), length(MODEL_MATRIX[1].xyz))
		* quad_base_size;
	// V is negated exactly as the guest writes it (`uv1.y = -|dy| * 0.0025 + rand`).
	vec2 detail_uv = vec2(UV.x, -UV.y) * size * detail_scale + detail_origin;
	vec2 detail_dx = dFdx(detail_uv);
	vec2 detail_dy = dFdy(detail_uv);

	vec4 primary = vec4(1.0);
	vec3 rgb = vec3(0.0);
	float alpha = 1.0;
	bool should_discard = clipped;
	ALBEDO = vec3(0.0);
	ALPHA = 0.0;

	if (!should_discard) {
		if (has_keycap && (glyph_player_two ? ui_keyboard_mode_two : ui_keyboard_mode)) {
			primary = textureGrad(keycap_tex, UV, raw_uv_dx, raw_uv_dy);
		} else if (has_diffuse) {
			primary = textureGrad(diffuse_tex, uv, uv_dx, uv_dy);
		}

		rgb = primary.rgb;
		alpha = primary.a;

		if (detail_mapped) {
			vec4 detail = textureGrad(detail_tex, detail_uv, detail_dx, detail_dy);

			// REPLACE, not blend — the stored factor is 1.0.
			rgb = detail.rgb;
			alpha *= detail.a;
		}

		float final_alpha = alpha * opacity;
		should_discard = (final_alpha <= 0.001);

		if (!should_discard) {
			if (hue_shift != 0.0) {
				// RGB -> HSV, rotate, back (the branchless form).
				vec4 k = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
				vec4 p = mix(vec4(rgb.bg, k.wz), vec4(rgb.gb, k.xy), step(rgb.b, rgb.g));
				vec4 q = mix(vec4(p.xyw, rgb.r), vec4(rgb.r, p.yzx), step(p.x, rgb.r));
				float d = q.x - min(q.w, q.y);
				vec3 hsv = vec3(abs(q.z + (q.w - q.y) / (6.0 * d + 1e-10)), d / (q.x + 1e-10), q.x);
				hsv.x = fract(hsv.x + hue_shift);
				vec3 c = clamp(abs(fract(hsv.xxx + vec3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0, 0.0, 1.0);
				rgb = hsv.z * mix(vec3(1.0), c, hsv.y);
			}

			// SUBTRACTIVE and weighted; weight 0 leaves the colour alone.
			rgb -= (vec3(1.0) - tint_rgb) * tint_amount;

			ALBEDO = rgb;
			ALPHA = final_alpha;
		}
	}
}
