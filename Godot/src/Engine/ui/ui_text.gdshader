shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;

// The material every piece of UI text draws through. Exists so text can be CLIPPED — the guest
// masks text by the same mechanism it masks quads, at a different base (the type-5 text object
// resolves its mask into +148 and caches the handle at +152).
//
// Unshaded because a glyph atlas is emissive art, and an alpha SCISSOR rather than blending
// because the atlas is padded with transparent black — without the cut, the bilinear tail of
// the neighbouring cell haloes every character.

// Linear UNORM view (no `source_color`): `atlas.a` carries shipped glyph coverage and `atlas.g`
// carries the precomputed signed distance field (0.5 on the half-coverage contour), keeping both
// channels linear and halving per-material D3D12 shader-visible sampler descriptors and font VRAM.
uniform sampler2D atlas : filter_linear_mipmap, repeat_disable, hint_default_transparent;
uniform bool use_distance_field = false;

// Texture pixels per SHIPPED atlas texel — the atlas is supersampled at load.
uniform float supersample = 1.0;

uniform vec4 colour = vec4(1.0);

// Same contract as ui_quad.gdshader: min.xy, max.xy in the laid-out UI space.
uniform vec4 clip_rect = vec4(-1e9, -1e9, 1e9, 1e9);

// The element's RESOLVED opacity, written by the clone binding exactly as it writes the quads'.
//
// TEXT IGNORED THE RESOLVE UNTIL THIS EXISTED. The binding sets `opacity` on every bound
// mesh's material, but this shader had no such uniform, so the write was a silent no-op and a
// text element at accumulated opacity 0 rendered fully opaque — four button prompts drawn over
// each other with only one icon visible (the icons are quads, which do honour it), and hidden
// pages' text bleeding through wherever a book selects pages by opacity.
uniform float opacity = 1.0;

// The title's draw floor (Engine_UiTextContract.AlphaFloor): at or below this element alpha the
// text draws nothing. Negative draws at any alpha.
uniform float alpha_floor = -1.0;

// A menu seated in one player's half on split screen (Engine_UiPresentationHost.MenuLayer):
// scale in xy, offset in zw. The clip compares in the menu's own laid-out space, so the seat
// comes back out of the world position. Identity everywhere else. A MATERIAL uniform, not an
// `instance uniform`: with the separate render thread, an instance uniform's property list is
// fetched through the render command queue into the caller's stack, and Node.duplicate (the
// UI's template clones) crashed in InstanceUniforms::get_property_list (2026-10-20).
uniform vec4 clip_seat = vec4(1.0, 1.0, 0.0, 0.0);

// The title's text contract (Engine_UiTextContract): SnapGlyphs, ContrastFloor, CoverageGamma,
// plus whether the mesh carries UV2 run-mode attributes (ColourRunsReplace / Outlines) and outlines.
// The neutral values (false, 1.0, 1.0, false, false) draw the shipped coverage as the shader did before them.
uniform bool snap_glyphs = false;
uniform float contrast_floor = 1.0;
uniform float coverage_gamma = 1.0;
uniform bool has_uv2_modes = false;
uniform bool outline = false;

varying vec2 clip_world;

// The seat a HUD view is drawn in on split screen (Engine_HudView.KeepToSeat): min.xy, max.xy in
// WORLD space, the real canvas the camera draws. A half is a screen of its own, so what a seated
// view places outside its half is off its screen, as it would be past a window edge. Unbounded
// everywhere else, so nothing that is not seated is affected.
uniform vec4 seat_rect = vec4(-1e9, -1e9, 1e9, 1e9);
varying vec2 seat_world;

#include "res://src/Engine/ui/ui_text_outline.gdshaderinc"

void vertex() {
	clip_world = ((MODEL_MATRIX * vec4(VERTEX, 1.0)).xy - clip_seat.zw) / clip_seat.xy;
	seat_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xy;

	// SNAPPED, GLYPHS SIT ON WHOLE PIXELS, as the 360 drew them: a 2-texel stem is two solid pixels.
	// The canvas camera frames 712.1 units in a 720-pixel viewport and elements sit at fractional
	// positions, so a glyph quad lands between pixels and every stem smears over three or four
	// partly covered ones, which read heavier than solid ones once blended in linear light. Each
	// corner goes to the nearest pixel edge, so a quad DrawWidth texels wide covers a whole number
	// of pixels. Orthographic cameras only: a mirrored screen's perspective text is left alone.
	//
	// POSITION IS WRITTEN ON EVERY PATH. Any write of it anywhere in vertex() makes Godot define
	// OVERRIDE_POSITION, which starts `position` at vec4(1.0) and outputs it as gl_Position
	// (4.7.1 shader_compiler.cpp usage_defines, scene_forward_clustered.glsl `vec4 position =
	// vec4(1.0)` / `gl_Position = position`), so a path that skipped the write put every vertex on
	// one point and drew no text at all: every Fable 3 label (snap off), and Fable 2's under a
	// perspective camera or the smooth text style. Unsnapped it is Godot's own transform,
	// `projection_matrix * (modelview * vertex)`.
	vec4 clip = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
	if (snap_glyphs && PROJECTION_MATRIX[3][3] == 1.0) {
		vec2 pixel = round((clip.xy / clip.w * 0.5 + 0.5) * VIEWPORT_SIZE);
		clip.xy = (pixel / VIEWPORT_SIZE * 2.0 - 1.0) * clip.w;
	}
	POSITION = clip;
}

void fragment() {
	bool clipped = clip_world.x < clip_rect.x || clip_world.x > clip_rect.z ||
		clip_world.y < clip_rect.y || clip_world.y > clip_rect.w;
	clipped = clipped || seat_world.x < seat_rect.x || seat_world.x > seat_rect.z ||
		seat_world.y < seat_rect.y || seat_world.y > seat_rect.w;

	vec4 texel = texture(atlas, UV);
	vec2 uv_dx = dFdx(UV);
	vec2 uv_dy = dFdy(UV);

	// Screen pixels per SHIPPED atlas texel, plus UV step per shipped texel (computed once in uniform control flow).
	vec2 atlas_size = max(vec2(textureSize(atlas, 0)), vec2(1.0));
	float safe_supersample = max(supersample, 1.0);
	vec2 texel_step = safe_supersample / atlas_size;
	vec2 shipped_fwidth = (abs(uv_dx) + abs(uv_dy)) / texel_step;
	float magnified = 1.0 / max(max(shipped_fwidth.x, shipped_fwidth.y), 1e-5);

	// Read the packed signed distance channel (`.g`). Avoid `fwidth(distance)` on the sampled
	// texel: dependent-sample derivatives trigger D3D12 GPU hangs (LiveKernelEvent 141) on Intel
	// Arc GPUs. `Engine_BitmapFontTexture` / `Engine_SdfField` encode `.g` as `0.5 - s / (2 * spread)`
	// with `spread = 2 * supersample`, so a 1-screen-pixel step changes `.g` by exact `0.25 / magnified`.
	float distance = texel.g;
	float df_width = clamp(0.25 / max(magnified, 1e-4), 1e-4, 0.5);

	// THE FIELD ONLY WHERE THE TEXT IS MAGNIFIED. At or near the shipped size a screen pixel spans
	// a whole shipped texel, the field is read from its mips, and the mip average erodes every
	// stroke thinner than a pixel below the 0.5 contour — Fable 3's list rows lost the bar of 'H'
	// and the bowls of 'B' at 1280x720. The shipped coverage is exact there.
	bool from_field = use_distance_field && magnified > 1.25;
	float field_width = 1e-4;
	float contrast = 0.0;
	if (from_field) {
		// One screen pixel of anti-aliasing across the 0.5 contour: the field's own change per
		// pixel, so the edge is as sharp at 3x as at 1.25x and never wider than a pixel.
		field_width = df_width;
		texel.a = clamp((distance - 0.5) / field_width + 0.5, 0.0, 1.0);
	} else if (magnified > 1.0 || (contrast_floor > 1.0 && magnified > 0.75)) {
		// LEGIBLE AT ANY SIZE. The font is a 720p coverage mask whose edge is about one shipped
		// texel wide; magnified, the coverage is re-contrasted about its half point by that factor
		// so the edge is one SCREEN pixel wide.
		//
		// A contrast floor above 1.0 holds the factor there from 0.75x up: the mip read near 1:1 is
		// the cubic upsample, whose edge fringe (about 0.11 one texel out, 0.89 inside) shows on dark
		// leather at a third of the text's brightness. At 1.0, at or below 1:1 the coverage is the
		// shipped coverage untouched.
		contrast = max(min(magnified, 8.0), contrast_floor);
		texel.a = clamp((texel.a - 0.5) * contrast + 0.5, 0.0, 1.0);
	}

	// PER-RUN TINT RIDES THE VERTEX COLOUR, as a multiplier over the element's own. Inline
	// `<b>`/`<s1>`..`<s4>` colour the run they enclose, and they can appear several times in
	// one label — so the tint cannot be a uniform without splitting the mesh per colour. An
	// unmarked run is white and multiplies to nothing, which is what leaves `colour` as the
	// live channel Engine_UiTheme.Retint writes.
	vec4 tint = colour * COLOR;

	// A RUN THAT REPLACES (Engine_UiTextContract.RunReplaces, UV2 x = RGB, y = alpha): Fable 3's
	// colour tags overwrite the element's colour and opacity with their own vector instead of
	// multiplying. An OUTLINED run (Engine_UiTextContract.Outlined) adds 2 to x. Gated behind
	// uniform booleans so Fable 2 glyph meshes (which omit ArrayType.TexUV2) never read UV2 or
	// evaluate the outline loop on D3D12.
	bool outlined = false;
	float run_alpha = tint.a * opacity;
	if (has_uv2_modes) {
		outlined = outline && UV2.x > 1.5;
		if (UV2.x - (outlined ? 2.0 : 0.0) > 0.5) {
			tint.rgb = COLOR.rgb;
		}
		if (UV2.y > 0.5) {
			run_alpha = COLOR.a;
		}
	}

	// THE COVERAGE CURVE. Godot mixes in linear light, where a half-covered pixel of light text on a
	// dark page shows at about three quarters of the text's brightness, and dark text on parchment
	// fades the other way. Bright text takes its coverage to `coverage_gamma` and dark text the
	// mirror of that. Solid and empty pixels are unchanged, and the distance field's one-pixel edge
	// is left as it is drawn. 1.0 is no curve.
	if (!from_field && coverage_gamma != 1.0) {
		float bright = clamp(dot(tint.rgb, vec3(0.2126, 0.7152, 0.0722)) * 2.0, 0.0, 1.0);
		texel.a = mix(1.0 - pow(1.0 - texel.a, coverage_gamma), pow(texel.a, coverage_gamma), bright);
	}

	// THE OUTLINE (ui_text_outline.gdshaderinc), gated by uniform `outline` so non-outlined titles skip it statically.
	float cover = texel.a;
	if (outline && outlined) {
		if (!clipped && run_alpha >= 0.01) {
			cover = ui_text_outline_cover(atlas, UV, texel.a, uv_dx, uv_dy, from_field, field_width, contrast, texel_step);
		}
	}

	// Zero-discard `depth_draw_never` blend path: output `ALPHA = 0.0` outside the glyph/clip
	// so `blend_mix` leaves underlying UI untouched without DXIL `discard;` hazards.
	ALBEDO = vec3(0.0);
	ALPHA = 0.0;
	bool should_discard = (clipped || colour.a * opacity <= alpha_floor || cover * run_alpha < 0.01);
	if (!should_discard) {
		ALBEDO = outlined ? tint.rgb * texel.a : tint.rgb;
		ALPHA = cover * run_alpha;
	}
}
