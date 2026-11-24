# UI and HUD

Godot draws every screen, so a mod's UI is data the engine's own views read — the same views the
shipped UI runs through, with no second implementation.

## Contents

- [How mod UI composes](#how-mod-ui-composes)
- [The HUD: ui/hud.json](#the-hud-uihudjson)
  - [Moving the game's own pieces: layout](#moving-the-games-own-pieces-layout)
  - [Copies of the game's own pieces: clones](#copies-of-the-games-own-pieces-clones)
  - [Each player's from one record](#each-players-from-one-record)
  - [A mod's own pieces: meter, panel, picture](#a-mods-own-pieces-meter-panel-picture)
  - [Text pieces](#text-pieces)
  - [Moving pieces together: groups](#moving-pieces-together-groups)
- [Markers, fades and meters](#markers-fades-and-meters)
  - [World meters](#world-meters)
  - [The game's own world icons](#the-games-own-world-icons)
- [HUD overrides: icons, slides and holds](#hud-overrides-icons-slides-and-holds)
- [Textures from a Godot script: game://](#textures-from-a-godot-script-game)
- [The region map: minimap](#the-region-map-minimap)
- [A screen of your own](#a-screen-of-your-own)
- [Boxes: messages, notices and menu boxes](#boxes-messages-notices-and-menu-boxes)
- [Modals: who answers the game's boxes](#modals-who-answers-the-games-boxes)
- [A mod's own menus](#a-mods-own-menus)
  - [How a game screen stands: its file and Godot's statements](#how-a-game-screen-stands-its-file-and-godots-statements)
  - [Row actions on the game's screens](#row-actions-on-the-games-screens)
  - [Host lists a mod menu can source](#host-lists-a-mod-menu-can-source)
- [Lua on the title screen](#lua-on-the-title-screen)
  - [The front-end surface](#the-front-end-surface)
- [Mod abilities: ui/abilities.json](#mod-abilities-uiabilitiesjson)
- [Spell slot picks: ui/spellwheel.json](#spell-slot-picks-uispellwheeljson)
- [Button glyphs: glyph packs](#button-glyphs-glyph-packs)
- [Starters](#starters)

## How mod UI composes

`ui/hud.json` and `ui/templates.json` **compose across mods**: they are read from every enabled
package, lowest priority first, so a higher mod wins a shared piece and both mods' other pieces
land. A piece two mods state is recorded as a conflict and logged.

The Studio's **🖼️ UI** tab edits the HUD, menus, modals, presentations and the settings page
against the game's own HUD view and list view. Its **🖥 Screens** page shows any game screen as the
game draws it — the screen's book built by the game's own code from its file and template, its
slides playing — with the open replayable, slowed, paused or stepped and any component's slide
stated by hand; a presentation exported from the Presentations page is what it stands once the
preview reloads. Outside the editor, `ui_viewer.tscn` does the same per presentation (B book/file,
R replay, P pause, F step, Tab and `,` `.` to state a component's slide). Its editor calls are in
[Commands and queries](reference-commands.md#refiimodapi-in-the-editor).

## The HUD: ui/hud.json

### Moving the game's own pieces: layout

`"layout"` takes EXACTLY the entry shape of the player's own `user://hud/layout.json` (the HUD
editor's file), keyed by the shipped piece's id (`RefiiModApi.ListShippedHudPieces()`, or
`user://hud/layout.snapshot.json` after a run):

```json
{
  "layout": {
    "group.PlayerHealth.PlayerHealth": { "anchor": [0, 1], "offset": [64, -96] }
  }
}
```

- `anchor` is the fraction of the screen the piece hangs from ((0,0) top-left, (1,1)
  bottom-right); `offset` the distance from it in 720-high reference units, +X right, +Y down.
  Both are required.
- The fields the HUD editor writes beside them (`art`, `value`, `driven`, `placed`) are allowed
  and ignored. Moving a piece places it, including one the shipped layout leaves unplaced.
- **Precedence:** the shipped arrangement, then every mod's `layout` in load order, then the
  player's own file — a piece the player placed stays where they put it.

### Copies of the game's own pieces: clones

A copy is the game's own widget built again beside the original — it moves, fades and hides with
it — from the same art, with the differences the record states:

```json
{
  "clones": [
    { "id": "my_mod.stamina", "of": "group.PlayerHealth.PlayerHealth", "offset": [0, 34],
      "hue": 0.35, "hueElements": ["HealthBar"],
      "values": { "PlayerHealth": "player:my_mod.stamina/player:my_mod.stamina_max" },
      "bars": { "HealthBar": "PlayerHealth" } },
    { "id": "my_mod.boss", "of": "slidefader2.GenericMeter.GenericMeter", "offset": [0, 120],
      "values": { "GenericMeter": "mod:my_mod.boss/mod:my_mod.boss_max" },
      "slides": { "GenericMeter": 2 } }
  ]
}
```

| Field | Meaning |
|---|---|
| `id` | required: the copy's own id |
| `of` | required: the shipped piece copied (`<kind>.<Name>.<ElementName>`, or a `widget.` id — a widget copy draws exactly what the widget draws and takes none of the fields below) |
| `offset` | from the original, design units, +Y down |
| `hue`, `hueElements` | a hue rotation 0..1 of the named elements' art (red health to blue or green); the original keeps its colour |
| `icon` | `{ "element", "textures": [...] }`: replaces one element's picture with the first candidate that loads |
| `values` | meter name → `source` or `source/cap` (`mod:`, `player:`, `game:`, `hero:`), drawn as `source / cap × 100`; `"none"` holds a meter still; every meter not named stays the game's |
| `bars` | element → meter name: the elements a meter fills, redrawn at the meter's own value |
| `slides` | element name → slide number: the copy's own slide for that element |

**Slides.** A HUD piece's visibility is shared by the original and every copy, so raising a fader
by name raises the original too. A slide stated in `slides` is resolved for the copy alone, while
the original keeps the frame's state. That is how a piece the shipped game authors dark and never
lights — `GenericMeter` and `CrucibleMeter` are inactive on their master slide and lit by slide 2,
`On` — becomes a mod's own. Slides are numbered as the game counts them: 1 past the master slide.
The Studio's copy card lists the copied piece's slides in a picker ("Lit on slide"). A value that
is not a slide number is warned and ignored.

### Each player's from one record

A copy of player one's widget whose meters read `player:` values, or a piece that follows player
one's widget and reads one, is **each player's**: the engine builds it beside player two's widget
too, and every `player:` read is that player's. Player one's view, player two's half on split
screen and an online joiner's window all draw the right player from the one record. The twin's id
is the record's with `2` after it; a mod that states that id itself keeps its own. A record reading
only `mod:` values (one number for everyone) stays on the widget it names.

### A mod's own pieces: meter, panel, picture

`"pieces"` are a mod's own, built beside the game's from Godot primitives — no art needed — and
children of the HUD view, so they show and hide with it, scale with it and stand in 720-high
reference units:

```json
{
  "hide": ["group.PlayerHealth.PlayerHealth"],
  "pieces": [
    { "id": "my_mod.hp", "kind": "meter", "anchor": [0, 0], "offset": [96, 40], "size": [0, 10],
      "value": "player:my_mod.hp", "max": "player:my_mod.hp.max",
      "widthFrom": "player:my_mod.hp.max", "widthPerUnit": 0.6, "minWidth": 120, "maxWidth": 560,
      "fill": "#9e1b1b", "back": "#000000b0", "chip": "#d8c27a", "border": "#8a8478", "borderWidth": 1.5 },
    { "id": "my_mod.frame", "kind": "panel", "anchor": [0.5, 1], "offset": [-200, -120], "size": [400, 64],
      "back": "#00000080", "border": "#c8a85a", "borderWidth": 2, "corner": 6, "each": true },
    { "id": "my_mod.slot1", "kind": "picture", "anchor": [0.5, 1], "offset": [-190, -112], "size": [48, 48],
      "each": true },
    { "id": "my_mod.orb", "kind": "picture", "anchor": [0.5, 1], "offset": [-250, -120], "size": [64, 64],
      "texture": "art/gui/persistant/hud.tex", "region": [0.006, 0.164, 0.127, 0.125], "each": true }
  ]
}
```

| Field | Meaning |
|---|---|
| `id` | required: the piece's id (prefix it with the mod's name); a later mod's piece of the same id replaces it |
| `kind` | `meter` (back, a trailing chip and a fill; the default), `panel` (back and border only: a frame, a plate), `picture` (a texture) or `text` (below) |
| `anchor`, `offset`, `size` | placed as the layout places the game's pieces: the screen fraction it hangs from, the distance in reference units (+Y down), the size `[w, h]` |
| `follow`, `followElement` | hang beneath a shipped piece's element as it is drawn, every frame; `offset` is then from its bottom-left corner and a width of 0 takes the element's. A follower is that piece's player's |
| `each` | `true`: a piece with no `follow` is **each player's** — built in player two's half too (their view on split screen, an online joiner's window), reading player two's values and player two's `hud_icon` textures. Without it a free piece is the screen's and stays out of player two's half |
| `value`, `max` | a meter's fill: a number or a value source (`mod:`, `player:`, `game:`, `hero:`, `setting:`); a value not stated hides the piece — never drawn empty or full |
| `widthFrom`, `widthPerUnit`, `minWidth`, `maxWidth` | a bar whose LENGTH is a value (a pool's maximum) times `widthPerUnit`, clamped |
| `fill`, `back`, `chip`, `border`, `borderWidth`, `corner` | HTML colours (with alpha), the border's width and the corner radius; `chip` is the recently-lost part, held briefly then drained |
| `texture` | a picture's texture: a game texture (`art/gui/…/x.tex`, with or without `game://`), a PNG an enabled mod ships (`art/x.png`), or `res://`. Optional: a picture with none draws what `hud_icon` states for it |
| `textures` + `index` | a picture that shows ONE of a list, picked by a value source's integer part; hidden while it states nothing or an index outside the list |
| `region` | `[u, v, w, h]`, fractions of the texture from its top-left: the picture shows that part of it — a piece of an atlas, at any of the atlas's resolutions |
| `tint` | a picture's HTML colour (with alpha) its texture is multiplied by: a darkened orb, a dimmed icon (`"#ffffff59"`), a gold ring |
| `scale` (file level, id → factor) | a piece drawn larger about its own point |
| `group` | the id of a file-level `groups` record: the piece moves and sizes with it ([below](#moving-pieces-together-groups)) |
| `touch` | a picture's input action (`"slot4"`, scoped to the mod, or `"my_mod.slot4"`): on a touchscreen a finger on player one's copy, where it is drawn, presses it ([On a touchscreen](settings-and-input.md#on-a-touchscreen)). An action name, not text; ignored on a desktop |

Pieces draw in file order (the mods' files in load order): a later piece over an earlier one, so
an icon listed after its frame sits on it and a ring listed after the icon rims it.

**Filled by a call.** `hud_icon(p, "<piece id>", texture)` on any surface states the texture
player p's copy of a picture shows — any path `texture` takes — over its own `texture` and
`textures`; `""` clears. With `each`, a ten-slot bar is ten pictures that each player's
`hud_icon` calls fill, a `text` piece per count reading `player:` values, and nothing drawn by the
mod itself: the HUD's own visibility, scale and halves come with it.

### Text pieces

```json
{
  "pieces": [
    { "id": "my_mod.gold_text", "kind": "text", "anchor": [1, 0], "offset": [-48, 40],
      "text": "mod:my_mod.gold", "format": "{0:0} gold",
      "size": 18, "colour": "#d8b23a", "align": "right" }
  ]
}
```

| Field | Meaning |
|---|---|
| `text` | required: a literal, a Babel tag (drawn as written when the book has no such tag), or a value source — `mod:<key>` (a published value), `player:<key>` (the player store, read for the piece's player: player two's in their half), `game:<name>` (a text field the guest publishes for its HUD, drawn as written), `setting:<mod>.<name>` (a mod setting in force), `hero:health` / `hero:health_max`; an unstated value draws nothing, never a zero |
| `format` | for a number: a .NET composite (`"{0:0} gold"`) or a bare numeric format (`"0.0"`, `"N0"`); empty draws up to two decimals |
| `size` | the point size (default 18, the HUD's own) |
| `colour` | HTML hex; default the HUD text's gold |
| `align` | `left`, `centre` (or `center`) or `right` of the piece's point; the text hangs from its top edge |
| `outline`, `outlineWidth` | an HTML colour ringing every glyph, and the ring's width in reference units (default 1.5): the glyphs drawn again behind the text, offset all round |
| `shadow`, `shadowOffset` | an HTML colour for a drop shadow, and its `[x, y]` offset in reference units, +Y down (default `[1.5, 1.5]`) |
| placement | `anchor` + `offset`, or `follow` / `followElement` |

It is drawn by the HUD's own text path — the theme's fonts, inline icon tags included — and scales
with the HUD. Publish the value from any surface: `Refii.publish(key, n)`, `host.publish(key, v)`,
`refii::mods::Publish(key, v)`.

### Moving pieces together: groups

`"groups"` names a transform; every piece whose `group` names it is moved and sized by it, read
every frame, so a setting the player changes on Options › Mods moves the pieces at once:

```json
{
  "groups": {
    "my_mod.bar": { "anchor": [0.5, 1], "pivot": [0, -12],
                    "scale": "setting:my_mod.size", "offset": [0, "-setting:my_mod.lift"] }
  },
  "pieces": [
    { "id": "my_mod.frame", "kind": "panel", "anchor": [0.5, 1], "offset": [-200, -76], "size": [400, 64],
      "each": true, "group": "my_mod.bar" },
    { "id": "my_mod.count", "kind": "text", "anchor": [0.5, 1], "offset": [0, -60], "text": "player:my_mod.count",
      "outline": "#0a0805d9", "each": true, "group": "my_mod.bar" }
  ]
}
```

| Field | Meaning |
|---|---|
| `anchor`, `pivot` | the point the group scales about, placed as a piece's point is: the screen fraction plus reference units (+Y down) |
| `scale` | a number or a value source; every member's point moves that factor away from the pivot and the member is drawn that factor larger. Not stated, or not above 0, it is 1 |
| `offset` | `[x, y]` in reference units (+Y down), added after the scale; each a number or a value source, a leading `-` negating a source (`"-setting:my_mod.lift"` raises by the setting). Not stated, it is 0 |
| `touchScale` | a number the scale is multiplied by on a touchscreen device (a phone), where the screen is small and the pieces are tapped with a thumb. Not stated, or not above 0, it is 1; a desktop never applies it |

A member is placed first as it always is (anchor and offset, or `follow`), then moved by its group.
The values are read for the member's player: a group of `each` pieces in player two's half scales
about the pivot placed in that half and reads player two's `player:` values. `setting:<mod>.<name>`
reads the setting in force — the file's value, the player's choice over it, any live
`set_setting` — the value `Refii.setting` answers. A later mod's group of the same id replaces an
earlier one's.

## Markers, fades and meters

Three HUD calls on every surface, drawn by Godot for the player named — 1 or 2, or 0 for every
player this machine plays — on split screen in that player's half. A machine draws only the
players it plays; the mod's code runs on the other machine too.

| Surface | Calls |
|---|---|
| Native | `refii_mod_mark(player, entity, xyz, icon, text)` · `refii_mod_unmark(id)` · `refii_mod_world_meter(player, entity, xyz, value, max, style)` · `refii_mod_mark_meter(id, value, max, style)` · `refii_mod_fade(player, to_black, seconds)` · `refii_mod_meter(player, id, label, value, max, style)` · `refii_mod_meter_remove(id)` |
| Lua | `Refii.mark(target, icon, text [, p [, o]])` · `Refii.unmark(id)` · `Refii.world_meter(target, value, max [, style [, p]])` · `Refii.mark_meter(id, value, max [, style])` · `Refii.fade(p, dir [, seconds])` · `Refii.meter(id, label, value, max [, p [, style]])` · `Refii.meter_remove(id)` |
| Godot | `mark(target, icon, text, player)` · `unmark(id)` · `markers()` · `world_meter(target, value, max, style, player)` · `mark_meter(id, value, max, style)` · `fade(player, to_black, seconds)` · `fade_state(player)` · `meter(id, label, value, max, player, style)` · `meter_remove(id)` · `meters()` |

From GDScript, the trailing arguments may be left off, from the right: `mark`'s `text` ("") and
`player` (0), `fade`'s `seconds` (0), and `meter`'s `player` (0) and `style` ("").

```lua
local id = Refii.mark(e.victim, "art/gui/wheelicons/abilities/will_shock.tex", "Marked", 0)
Refii.mark({x, y, z}, "art/my_mod_flag.png", "Go here", 2)
Refii.fade(1, "to_black", 0.5)
Refii.meter("my_mod.boss", "Thag", hp, hp_max, 0, "boss")
Refii.meter_remove("my_mod.boss"); Refii.unmark(id); Refii.fade(1, "from_black", 0.5)
```

- **Markers** ride the HUD's world-icon channel — the layer the game's own property signs, quest
  markers and opinion splashes are drawn on, with the same projection and clipping. A marker on an
  entity rides the drawn body (lifted by its height) and goes five seconds after the entity does;
  a point marker stands a little above its point. It is sized by the game's own distance curve
  (full size within 10 m, 0.6 beyond 17 m). `icon` is a game texture path (`art/gui/.../x.tex`),
  one of the [world icon aliases](#the-games-own-world-icons), or a PNG an enabled mod ships
  under `art/`; an icon that loads nothing draws the text alone, warned once.
- **Fades** are the host's own black layer per player, drawn under the game's fade: to black
  (`"to_black"` / `"out"`) or back (`"from_black"` / `"in"`) from the alpha standing, over the
  seconds asked (0 or nil at once). They never touch the game's own fade, which level changes and
  save loads wait on. `fade_state` answers `alpha` (0 clear .. 1 black), `target`, `seconds`.
- **Meters** are stacked under the top centre in the order first shown. Styles: `bar` (gold, the
  default), `boss` (wide, red), `timer` (narrow, pale), and the game's own meter art, which the
  shipped game never shows: `generic` (the ornate bar with the red orbs), `crucible` (the plain
  red bar) and `price` (the bowl with the arrows and the A prompt; the value drawn as a whole
  number). The value and maximum are read live; the same id restates a meter in place.
- Ids: any surface's `unmark` takes any surface's id. Everything a session put up is cleared when
  the game goes down.

### World meters

A world meter is a marker's FILL: a bar under its icon and text, on the same channel and by the
same rules — each player's camera and half, lifted by the drawn height, sized by the game's
distance curve, gone with its entity, hidden by the world as the game's own icons are.

```lua
local bar = Refii.world_meter(enemy, hp, hp_max, "health numbers", 0)   -- a bar alone
Refii.mark_meter(bar, hp_now, hp_max)                                    -- the value, in place
Refii.mark(boss, "quest", "Thag", 0, { meter = { hp, hp_max, "health" } }) -- an icon with a fill
Refii.unmark(bar)
```

- `world_meter` makes a marker that is only the bar; `mark(…, { meter = { value, max, style } })`
  (Godot: `mark(target, icon, text, player, { "meter": { "value", "max", "style" } })`) puts a
  fill on a new marker; `mark_meter(id, value, max [, style])` puts one on any marker or restates
  it **in place** — no re-mark, no new id. `max` 0 takes the fill off (a world meter with nothing
  else goes); an empty style keeps the standing one.
- `style`: `bar` (or empty: the HUD's gold), `health` (red), or an HTML colour (`"#5fa8ff"`); add
  `numbers` (`"health numbers"`) for a "value / max" line under a marker with no text, and `x<n>`
  (`"health x1.5"`) to draw the marker n times the distance curve's size (0.25 .. 4).
- A drop leaves a pale trail that holds 0.45 s, then drains at 0.9 of a bar a second.
- `markers()` lists `meter_value` and `meter_max` beside each marker.

### The game's own world icons

`icon = "quest"` names the texture the game's own world-icon builder uses for that kind:

| Alias | Texture (under `art/gui/`) | The game draws it for |
|---|---|---|
| `quest` | `hud/inworld_reward_icon_quest` | the quest marker over a giver or objective |
| `reward`, `freebie` | `hud/inworld_reward_icon`, `hud/inworld_reward_icon_freebie` | a reward, a free one |
| `reward_quest` | `wheelicons/rewards/reward_quest` | the quest reward sign |
| `follow`, `wait` | `wheelicons/npcstatus/icon_npc_follow`, `…_wait` | a follower's status |
| `engaged`, `married`, `child` | `wheelicons/npcstatus/icon_npc_engaged`, `…_married`, `…_child` | a family member |
| `romance`, `love` | `wheelicons/icon_romantically_interested`, `wheelicons/opinions/opinion_love` | a love interest |
| `exit` | `wheelicons/icon_level_exit` | a region exit |
| `button_a` | `controller/icon_button_a` | an interaction prompt |
| `gold` | `wheelicons/icon_gold_coin` | gold on the ground, a sale |
| `player1`, `player2` | `wheelicons/splashopinions/icon_player1`, `…_player2` | the co-op partner's splash |
| `dig`, `chest`, `pet_quest` | `wheelicons/pet/icon_pet_point_dig`, `…_chest`, `…_quest` | the dog's finds |
| `talk` | `multiplayer/orb_icon_talk` | the co-op orb's talk prompt |
| `lock` | `icon_lock` | the default for a kind without a texture |
| `opinion_attractiveness`, `opinion_ridiculousness`, `opinion_love`, `opinion_morality`, `opinion_respect`, `opinion_ugly`, `opinion_fear`, `opinion_hate`, `opinion_evil`, `opinion_disrespect`, `opinion_renown`, and each with `_gold` (not renown) | `wheelicons/splashopinions/<alias>` | the opinion splashes over a villager |
| `morph_fat`, `morph_thin`, `morph_tall`, `morph_short`, `morph_strong`, `morph_weak`, `morph_attractive`, `morph_ugly`, `morph_good`, `morph_evil` | `wheelicons/splashopinions/<alias>` | the morph splashes |

An unknown name is a path as given.

## HUD overrides: icons, slides and holds

Three calls change what the game's own HUD elements show, per player, by element name — the way a
quickslot mod puts its own icons on the D-pad and keeps the D-pad on screen:

| Surface | Calls |
|---|---|
| Native | `refii_mod_hud_icon(player, element, texture)` · `refii_mod_hud_slide(player, element, slide)` · `refii_mod_hud_hold(player, piece, on)` |
| Lua | `Refii.hud_icon(p, element, texture)` · `Refii.hud_slide(p, element, slide)` · `Refii.hud_hold(p, piece, on)` |
| Godot | `hud_icon(player, element, texture)` · `hud_slide(player, element, slide)` · `hud_hold(player, piece, on)` · `hud_overrides(player)` |

```lua
Refii.hud_icon(1, "DPadUp", "Art\\GUI\\WheelIcons\\expressions.tex")  -- a game texture
Refii.hud_icon(1, "DPadLeft", "art/my_mod/potion.png")                  -- a PNG the mod ships
Refii.hud_slide(1, "DPadUp", 2)                                         -- the slot's "on" slide
Refii.hud_hold(1, "dpad", true)                                         -- keep the D-pad on screen
Refii.hud_icon(1, "DPadUp", ""); Refii.hud_slide(1, "DPadUp", 0); Refii.hud_hold(1, "dpad", false)
```

- **Player** is 1 or 2, or 0 for both. The shared HUD is the local player's (player two's on an
  online joiner); local player two's half on split screen takes player two's overrides.
- **Element** is a slide-toggle binding name — the name the guest's own `GUI.ToggleElement` states:
  `DPadUp`, `DPadDown`, `DPadLeft`, `DPadRight` — or an element path of `hud.bgf` matched by its
  trailing segments (`dpadmenu.group.up.up`). An icon lands on the element's quad, or the leaf
  quads beneath it.
- **Icons** are a game texture path or a PNG an enabled mod ships under `art/`, resolved as
  markers resolve theirs. Art that is not 128 × 128 is fitted the way the game's own slot icons
  are: 84% of the slot on its longer side, centred, transparent around. `""` clears.
- **Slides** are counted as the game counts them (1 the first; a toggle's off is 1, on is 2); 0
  clears and the guest's own stands.
- **Holds** name a HUD piece (`dpad`): every slide owner under it is kept on screen — one with an
  `in` slide is forced onto it, the others keep the slide the guest states — so a piece the guest
  tweens away stays up. The piece's trunk — its element and each lone child below it, down to the
  first element with several — fades the whole piece: a trunk element whose opacity no slide
  commands, which the guest fades by stating its opacity, is drawn at no less than its authored
  opacity. The health readout (`PlayerHealth`, player two's `CoopHealth`) fades out that way at
  full health (its `HealthAndMana.Health`), and held it stays up, its copies (`clones`) with it;
  elements past the first branch (the D-pad's sparkles and glow) keep the guest's opacity. Each view draws the piece of the player it shows, so
  `Refii.hud_hold(0, "CoopHealth", true)` keeps player two's readout up wherever it is drawn.
- Everything is cleared when the mod's Lua reloads and when the game goes down; state it again from
  `lua_ready` / `hero_ready`.
- **A mod's own picture piece** is an element too, named by its `ui/hud.json` id: `hud_icon(p,
  "my_mod.slot1", texture)` states the texture player p's copy of the piece shows, at the piece's
  size and `region` — any path, `game://` or not, a mod PNG or `res://`. See
  [A mod's own pieces](#a-mods-own-pieces-meter-panel-picture).

## Textures from a Godot script: game://

A Godot script loads the game's textures through Godot's own loader:

```gdscript
var icon: Texture2D = load("game://art/gui/wheelicons/abilities/will_shock.tex")
```

`game://<path>.tex` reads the game's banks with every enabled mod's overlays in place (a mod's
`art/x.tex.png` replaces the shipped texture at that path), decoded and cached as the HUD and the
menus decode theirs, upscaled when the player has upscaling on. A path is lower case with `/`; a
PNG a mod ships under its own path is read from the package as any file. A script rarely needs it:
a HUD picture names its texture by path and `hud_icon` fills it, a marker names its icon.

## The region map: minimap

The data the pause root's and the world map's minimaps plot from, for a mod that draws its own map,
for one player's hero.

| Surface | Call |
|---|---|
| Lua | `Refii.ask_host("minimap", fn, p)`, `Refii.ask_host("minimap_point", fn, x, y, z)`: `fn` gets the answer on a later tick, a place as a table of `x` and `y` |
| Godot | `host.minimap(p) -> Dictionary`, `host.minimap_point(x, y, z) -> Vector2` |

`p` is 1 or 2, 0 for this machine's hero (`local_hero`): pass the player the map is drawn for — the
one `player_here` says this machine plays.

| Key | Meaning |
|---|---|
| `known` | a level stands and the game's database has a map record for it; without it only `half_size` follows |
| `proper` | it has a real map, so the places below are stated; a place without one still states its art |
| `texture` | the map's art, a `Texture2D` (Godot only); absent when the record names none |
| `texture_path` | the same art's `game://` path, which Lua gets in its place |
| `north` | radians the image turns, as the game's own minimaps turn it |
| `half_size` | the image's half-size, in the units the places are in: read from the pause root's own file |
| `hero`, `facing` | the player's hero's place, and the radians their marker turns |
| `quests` | an Array of places: the objectives of the quests in the log other than the primary one, each where its objective entity stands, as the world map's minimap plots them |

The map record comes from the game's database, the extent the places are divided by from the
level's own heightfields (as the game computes its level bounds), the hero's place and turn from
the entity feed by the pause root's own rules. The quests come from the hero's quest log, placed by
the same rule; the log is that of the hero whose menus this machine shows (`local_hero`), whichever
`p` asks, which is the map's own player whenever a map is up in play. Other marks are the mod's to
place from stated calls: a dog's position from `player_dog` and `position`, an enemy's from `near`.
Places are `Vector2`s in the image's element units around its centre, Y up, so `half_size`
is the image's edge. `minimap_point` puts a guest world position on the same map. It
answers null with no proper map. Ask a few times a second rather than every frame.

## A screen of your own

A script can draw a whole screen of its own and have it stand where the pause book would, with the
world held behind it and the mouse free.

| Surface | Call |
|---|---|
| Lua | `Refii.ask_host("own_screen", fn, p, true)` |
| Godot | `host.own_screen(p, true, self)` |

`p` is the player the screen serves, 1 or 2 (0 the player whose menus are open here): the one whose
input opened it. The screen is your mod's: in Godot pass your script (`self`), and Lua's `ask_host`
names your mod by itself, so only your mod turns it off and another mod's `own_screen(p, false)` leaves
it standing. One screen stands at a time, and a later take by any mod takes it over; the engine releases
it when the mod set reloads (a mod enabled or disabled), the game VM's Lua reloads (a screen a Lua mod
took), the game is left or a level unloads, so a screen whose mod is gone never keeps the book hidden. While on, the pause book and the HUD draw nothing and the pad is held from the game, all but Start. It
does not pause by itself: hold Start with `press` to enter the pause, let go on the `paused` signal,
and do the same to leave it. Turn it off when `paused` says the pause has ended, because a pad's Start
still ends it. Escape is the menus' back while paused, so it is held too: a screen closes on it itself.
A key the screen uses is yours through a claim, as in [Claims](settings-and-input.md).

```gdscript
func open(p: int):
	player = p
	host.own_screen(player, true, self)
	host.press(player, "Start", true)

func _on_paused(e):
	host.press(player, "Start", false)
	if not e.get("paused", false):
		host.own_screen(player, false, self)
```

To show a model on the screen, draw it in a `SubViewport` of its own (`own_world_3d`) with its own
lights. The world's shaders read the level's lighting and draw nothing there, so give the
`MeshInstance3D` its mesh through `host.dress_model(node, mesh)`, which uses the lit material the
game's item previews use. `host.item_pose(item, yaw)` is how the game's item page holds an item of
`inventory_items(p)`: set it as the node's `basis` and grow `yaw` to spin it about the axis the item
states, with its front to a camera looking down -Z at 0.

## Boxes: messages, notices and menu boxes

| Surface | Calls | Answer |
|---|---|---|
| Godot | `message_box(title, text, buttons) -> id` | `message_box_answered(id, button)` |
| Lua (game and title) | `Refii.message_box(title, text [, accept [, cancel [, p]]])` · `Refii.notice(title, text [, seconds [, p]])` · `Refii.menu_box(title, text, options [, p [, cancelable]])` | the events `mod_message_box`, `mod_notice`, `mod_menu_box` |

From Godot, `notice(title, text, seconds = 0, player = 0) -> id` and
`menu_box(title, text, options, player = 0, cancelable = true) -> id` open the other two boxes and
answer with `notice_done(id, outcome)` and `menu_box_answered(id, option)`; `message_box` also
takes `player = 0`. An argument written `= value` may be left off, from the right.

- Every title, text, caption and option may name your text as `{KEY}`; it is drawn in the
  player's language (see [Text keys](gdb-and-text.md#text-keys)). `Refii.text` / `text` give
  you the string itself, with `%1` … filled in.
- A Godot `message_box` with `buttons` empty or one caption is a notice (one dismiss); two
  captions are a question (A and B). `title` sits above `text`. In game only.
- `button` is 0 for A (accept, or a notice's dismiss), 1 for B, -1 only when the box was
  withdrawn before it was answered. One question and one notice stand at a time; the rest queue
  in order — a second question is asked once the first is answered, a notice told under a notice
  follows it. A box is never answered -1 on another box's account.
- `mod_menu_box` answers `option` (-1 none); `mod_notice` answers `outcome` (0 shown, -1 never
  shown).

## Modals: who answers the game's boxes

`ui/templates.json` takes a `"modals"` block: the answer table for the game's query and message
boxes — which input codes A and B send back through the game's own router — one whole record per
box:

```json
{
  "modals": {
    "13":   { "kind": "query", "confirm": 39, "cancel": 37 },
    "20":   { "kind": "query", "confirm": 36 },
    "0@22": { "kind": "message", "confirm": 36 },
    "42":   null
  }
}
```

- The key is the game's modal id, or `<id>@<screen id>` for a row that holds over one screen only
  (a screen-specific row wins over the any-screen one).
- `kind` is `query` (A `confirm`, B `cancel`; no `cancel` means B does nothing) or `message` (one
  dismiss code). `null` removes the row: the box is still drawn, but keeps the game's own input.
- The table decides who ANSWERS a box, never whether it is drawn. The shipped rows, and every row
  as it ran, are in `user://ui/templates.snapshot.json` under `"modals"`.
- Editor: `RefiiModApi.SetModal(key, record)`, `ReleaseModal(key)`, `RemoveModal`, `GetModals`,
  `ListModals`, `ShippedModals`.

## A mod's own menus

A menu a mod defines in `ui/templates.json` opens as a list OVER the one it was chosen from — the
front end's list, the pause list or a screen's list — from a row whose `opens` names it or whose
command is `Opens:<menu>`. B collapses a fold first, then closes the menu and returns to exactly
where it was opened; menus nest. The list is built by the template's own construction, so a mod
menu folds, ghosts and sources rows as the shipped lists do. On the pause list a mod menu closes
with the root.

```json
{
  "menus": {
    "pause-root": {
      "patch": true,
      "entries": [ { "name": "my_mod.menu", "tag": "MY_MOD_MENU", "opens": "my_mod.menu" } ]
    },
    "my_mod.menu": {
      "title": "MY_MOD_MENU",
      "entries": [
        { "name": "greet", "tag": "MY_MOD_MENU_GREET", "command": "Mod:lua:my_mod_menu.greet" },
        { "name": "gold",  "tag": "MY_MOD_MENU_GOLD",  "command": "Mod:lua:my_mod_menu.give_gold:100" },
        { "name": "more",  "tag": "MY_MOD_MENU_MORE",  "command": "Opens:my_mod.more" }
      ]
    }
  }
}
```

- A row a mod adds to one of the game's lists (a patch entry, the rows of a source a mod's entry
  names, a script's `set_menu_rows` or `register_source` rows) is the mod's, and nothing in the file
  changes that: the game is never told such a row is highlighted, so its screen code goes on reading
  the game's own row, and the pause list's labels skip it. It needs no `"hostOnly"`. A patch entry
  that keeps the name and event of the row it replaces (a new tag, icon or `when`) is still the
  game's row.
- The shipped **options** and **decoration** screens carry
  `"commitsByHighlight": true` (a screens-record flag, default false): on such a screen a press
  puts the game's own menu cursor on the row and sends the game's A, instead of pushing the row's
  identity. A mod's row on one of these screens therefore needs a `command` (a host action), or a
  name the game's own list also carries.
- A screens record's `"rowAction"` is the verb a press on any of its rows runs, `{id}` replaced by
  the row's full id; a row's own `command` wins. The shipped **upgradefurniture** screen states
  `"Furniture:upgrade:{id}"`: its rows are levels, and `Furniture:upgrade:<level>` upgrades the
  targeted piece through the game's own transaction (the hero's unlock card is taken, the house
  value and the hero's properties restated, `furniture_upgraded` raised).
- `Mod:lua:<function>[:<arg>]` calls a global Lua function — a name or a dotted path
  (`my_mod_menu.give_gold`), looked up, never compiled — in the game VM on the next guest tick,
  protected, as `function(arg, e)`: `arg` is everything after the second colon (`nil` when there
  is none), `e` the row's `mod_menu_row` table. On the title it runs in `mod/frontend.lua`.
- Any other command on a mod menu's row runs as a host action (`setting:toggle:…`,
  `setting:set:…`, an options page) and never reaches the game.
- The game's own screen verbs can be named on any row: `Ability:learn:<1..14>` buys one level of
  that ability through the game's own purchase, `Ability:unlearn:<1..14>` gives one back through
  its own unlearn (without the confirm Y asks first); `Shop:buy:<ticket>` / `Shop:sell:<ticket>`
  is a shop row's transaction, pushed at the shop screen's own handler, whose own confirm follows.
  Shop rows carry it already.
- A Godot script opens a menu itself with `host.open_menu(name)` (false when no list is up, or no
  template has that name); Lua with `Refii.open_menu(name [, p])` on the title.

```lua
my_mod_menu = {}
function my_mod_menu.give_gold(arg, e)
  local hero = Refii.hero(e.player)     -- the player who chose the row
  if hero ~= nil then Refii.give_gold(hero, tonumber(arg) or 0) end
end
```

Every row of a mod's menu, every row a mod patched into a shipped menu, and every `Opens:` /
`Mod:lua:` row raises **`mod_menu_row`** on all three surfaces — `menu` (the mod menu's name, or
the list's own: `frontend-root`, `pause-root`, a screen's), `row`, `player` (whose menu it is) and
`mod` (the mod that stated the row or its menu):

```cpp
static void OnRow(refii::mods::ModMenuRowEvent& e) { /* e.menu, e.row, e.player, e.mod */ }
REFII_MOD_ON(ModMenuRow, OnRow);
```
```lua
Refii.on("mod_menu_row", function(e) Refii.log(e.menu .. "/" .. e.row) end)
```
```gdscript
Engine.get_singleton("RefiiHost").mod_menu_row.connect(func(e): print(e.menu, "/", e.row))
```

Godot raises it where the row is chosen, so a script sees it the same frame; native and Lua
handlers get it on the next guest tick. It is not cancellable.

Editor: `RefiiModApi.SetMenu(name, record)`, `RemoveMenu`, `GetMenu`, `ListModMenus`,
`SetMenuRow(menu, entry)`, `RemoveMenuRow`, `OpensCommand(menu)`, `LuaCommand(function, arg)`,
`ParseMenuCommand(command)`.

### How a game screen stands: its file and Godot's statements

Godot draws every book from its own `.bgf`, not from the game's hidden copy of it. A screen
record says how:

```json
{
  "screens": {
    "23": {
      "restFromFile": true,
      "restPose": { "Foreground.ExpandableMenu.KeyLight": [22] },
      "restOpacity": { "Foreground.Book.Pages.QuestsPage.Contents.Scrollbar": 0 }
    }
  }
}
```

- `"restFromFile": true` stands the book on the file's REST state: every component on its
  first slide (one whose first slide is its opening `PlayIntro` on the one after), every
  element switched on, and nothing read back from the game. Whatever the screen shows beyond
  that comes from Godot: the page provider's values, switches, slides, positions, textures and
  tints, and the game's GUI registries (below). The shipped books all carry it; a mod screen
  that sets it gets a picture that never drifts with the game's hidden cursor.
- **The slides play.** A stood book runs each component's slide over real time as the game
  does: the slide's animation tracks (position, scale, rotation, pivot, opacity) evaluate from
  0 to the slide's length, a slide plays its passes and holds its end (or loops / ping-pongs as
  its flags say), a go-next slide hands on to the next one, and an element's `timeoffset` /
  `endtime` window hides it outside that span. Changing a slide — by a provider's slide value,
  a registry write or a page turn — enters the new one from 0, so a mod screen's intros and
  transitions animate with no code. A `restPose` / `restOpacity` value or a provider's stated
  position or opacity is a later write and wins over a track.
- `"restPose"` puts an element where the game's own open leaves it, when that is not its file
  pose: a path to up to six numbers — position x, y, z, then scale x, y, z — with `null` for
  "keep the file's".
- `"restOpacity"` does the same for opacity, 0..100.
- `"flowWhenDrawn": true` limits a screen's measured reflow (`flowMeasured` / `flowBase` /
  `flowMoved`) to when the measured element is on screen — the pause legend moves under the quest
  objective only while there is an objective.
- **The GUI registries come for free.** An element the file binds by `TextName`,
  `SlideToggleName`, `SlideFaderName`, `SlideFader2Name`, `SlideSetName`, `MeterName` or
  `ImageName` draws what the game publishes under that name — the text, the slide, the meter
  (scrubbing the element's own timeline at percent × 10 ms) or the picture — on any book, a
  mod's included. A provider's own value still wins over a registry one.
- Elements with no authored name (a file's root) are addressed as `#<ordinal>` in any path a
  provider states, `#0` being the root.

### Row actions on the game's screens

A mod puts a button of its own on a shipped screen list with the `"rowActions"` block of
`ui/templates.json`, keyed by the list's menu template name — `ItemsMenu` and `WeaponsMenu` (the
inventory's two lists), `ExpressionsMenu` (the logbook's expressions), `AbilitiesMenu`, or a mod
menu's own name:

```json
{
  "rowActions": {
    "ItemsMenu":       [ { "button": "Y", "tag": "QUICKSLOTS_ASSIGN", "command": "Mod:lua:quickslots.assign" } ],
    "WeaponsMenu":     [ { "button": "Y", "tag": "QUICKSLOTS_ASSIGN", "command": "Mod:lua:quickslots.assign" } ],
    "ExpressionsMenu": [ { "button": "Y", "tag": "QUICKSLOTS_ASSIGN", "command": "Mod:lua:quickslots.assign" } ],
    "AbilitiesMenu":   [ { "button": "X", "tag": "QUICKSLOTS_ASSIGN", "command": "Mod:lua:quickslots.assign" } ]
  }
}
```

- While that list is up and a leaf row is highlighted, the prompt — the button's icon and the
  tag's text (a Babel tag, or a literal) — is drawn beside the page buttons where the game draws
  its own. The pad under a Godot-owned list is the host's: X, Y, LB and RB never reach the game
  while one stands (`X` is the equip hold, `Y` the flip hold: the two a screen list can take; the
  shipped prompt for the same button stands aside).
- On `AbilitiesMenu`, Y is the game's unlearn — its confirm, then one level back. A mod's Y row
  action on that list runs instead of it.
- On the press, or a click on the prompt, the row raises **`mod_menu_row`** on every surface with
  `menu` the list's template name, `row` the highlighted row's name, `player`, `mod`, and the two
  trailing fields `command` — the row's OWN command, `Inventory:use:1234`, `Inventory:equip:…`,
  `Ability:learn:9`, or on the expressions list the expression id — and `button`. Then the
  action's `command` runs exactly as a mod menu row's: `Mod:lua:<function>[:<arg>]` calls your
  Lua with `(arg, e)`, `e` carrying the same fields — and the row's TYPED SUBJECT, so no handler
  parses a verb: `subject_kind` (`item` for `Inventory:use`, `weapon` for `Inventory:equip`,
  `expression`, `will` for `Ability:learn` 7..14), `definition` (the record as `"0x%08x"`) and
  `spell` (a will's type 0..7; -1 for none).
- A later mod's list for the same menu replaces the earlier one's; `null` or `[]` removes it. The
  first action's prompt is drawn; every action's button is held and answered.

```lua
quickslots = {}
function quickslots.assign(arg, e)
  -- e.menu "ItemsMenu", e.row the item row, e.command "Inventory:use:1234", e.button "Y",
  -- e.subject_kind "item", e.definition "0x000004d2", e.spell -1
end
```

### Host lists a mod menu can source

A mod's menu need not build a catalog with `set_menu_rows`: an entry's `"source"` may name a list
the host builds from what the guest states.

| Source | Rows |
|---|---|
| `inventory:usable` / `inventory:usable:<p>` / `inventory:usable:<p>:<categories>` | what player p's hero can use from the inventory — `UsableFromInventory`, the game's own test — named `item:0x…`; `p` 0 is the menus' owner, as absent (the guest tick's answer to `inventory P`) |
| `expressions` / `expressions:<p>` | the expressions player p's hero knows (the guest tick's answer to `expressions P`), named `expression:0x…` and labelled as the logbook titles them |
| `spells_known` / `spells_known:<p>` | the wills player p's hero can assign (known and offered on some tier, the quick wheel's own test), named `will:<n>` |
| `abilities:<category>` | the abilities book's rows (Strength, Skill, Will, or a mod's category) |

- `p` absent is the player whose menus are open here. The guest-stated lists are asked again at most
  once a second while drawn; the rows follow the next answer.
- The rows carry no command: the menu's `"rowCommand"` (a `Mod:lua:` call) runs for each, and the
  `mod_menu_row` a choice raises carries `subject_kind`, `definition` and `spell` read off the row's
  name.

```json
"my_mod.choose": {
  "title": "MY_MOD_CHOOSE",
  "rowCommand": "Mod:lua:my_mod.chosen",
  "entries": [
    { "name": "items", "tag": "MY_MOD_ITEMS", "source": "inventory:usable" },
    { "name": "expressions", "tag": "MY_MOD_EXPRESSIONS", "source": "expressions" },
    { "name": "wills", "tag": "MY_MOD_WILLS", "source": "spells_known" }
  ]
}
```

A stated row's **label** is a Babel tag, a literal, or a literal with tags in braces —
`"{QUICKSLOTS_SLOT_1}: {OBJECT_HEALTH_POTION_NAME}"`: each `{TAG}` (letters, digits, `_`) is drawn
as the string book says, or as written when the book has no such tag. A mod's Lua has no text
lookup of its own; this is how a row names an item in the player's language.

`Refii.set_menu_rows` (any surface) carries up to 32768 bytes of rows; the native and Lua forms
cross the bus in as many 2 KB records as that takes and Godot applies them once all have arrived.
Past the limit the rows are cut at the last whole row and the Mods error list names the menu —
never a silent truncation.

## Lua on the title screen

There is no game on the title — no hero, no save, no world, no game VM — so a mod's Lua for the
front end is its own file, `mod/frontend.lua`, run in a Lua state of its own for the current
front-end presentation (the game's Lua 5.1 with float numbers: base, string, table and math) the first time a front-end chunk needs it (a row's `Mod:lua:` call, a box's answer, a
`start_game`); another presentation starts fresh. `Refii.frontend` is true there. A row on the
title is a patch on `frontend-root`:

```json
{ "menus": { "frontend-root": { "patch": true, "entries": [
  { "name": "my_mod.title", "tag": "MY_MOD_TITLE_ROW", "after": "Multiplayer", "command": "Mod:lua:my_mod_frontend.ask" } ] } } }
```
```lua
my_mod_frontend = {}
local asked = nil
function my_mod_frontend.ask(arg, e)
  asked = Refii.menu_box("My Mod", "Pick one.", { "Hello", "Goodbye" }, e.player)
end
Refii.on("mod_menu_box", function(e)
  if e.id == asked and e.option >= 0 then Refii.notice("My Mod", "You chose " .. (e.option + 1), 0, e.player) end
end)
```

The **Front end** starter writes exactly this; Spell Weaving's `mod/frontend.lua` puts a "Spell
Weaving" row on the title whose menu box explains the mod.

### The front-end surface

What `Refii` offers in `mod/frontend.lua`:

| In `Refii` on the front end | What it is |
|---|---|
| `Refii.on(name, fn)` for `start_game`, `mod_menu_row`, `mod_message_box`, `mod_notice`, `mod_menu_box` | the events raised without a world: the start target stated (`save`, `level`, `new_game`, `player`, `commit`), a row chosen, a box or notice answered. No other event reaches here — every other seam needs the world |
| `Refii.signed_in()` | the signed-in pad's user index, nil for none |
| `Refii.save_count()` | the signed-in profile's saved games on every device, nil with no front end — a content enumeration: ask once a screen (on a `mod_menu_row`), never per tick |
| `Refii.screen()` | the current presentation's name (`FrontendStartup`, `FrontendMainMenu`, …), nil for none |
| `Refii.hash(name)` | the game's FNV-1 of a name as a `"0x…"` string, as in a game |
| `Refii.setting(key, default)` | the mods' `settings.json` values, the values in force |
| `Refii.log(text)` | a line in the game log, `[mod lua, front end]` |
| `Refii.publish(key, n)` · `Refii.value(key)` | the shared value store every surface and the UI read; a published value outlives the presentation |
| `Refii.notice(title, text [, seconds [, p]])` · `Refii.message_box(title, text [, accept [, cancel [, p]]])` · `Refii.menu_box(title, text, options [, p [, cancelable]])` | the host's own notice, question and menu box; answers come as the events above |
| `Refii.open_menu(name [, p])` · `Refii.menu_player()` | a mod's menu over the front end's list; whose menus are open (1, or 2 for a joiner) |
| `Refii.frontend == true` | the mark a chunk tests |

**Not here, and why:** `hero`, `player_hero`, `local_hero`, health, position, spawn, `play_fx`,
`player_*`, `save_get_int` / `save_set_int` and `run_lua` (no world, no game VM, no loaded save);
`open_screen` (a host screen over the pause book — in game); `require` (one file,
`mod/frontend.lua`). A save's name and level reach a mod the moment they are chosen
(`start_game`); its mod values once it is loaded (`save_loaded`, in `init.lua`). A profile sign-in
change is not an event; ask `Refii.signed_in()` when you need it.

## Mod abilities: ui/abilities.json

An ability of the mod's own on the abilities book — a row after its category's own abilities, or
on a page only mods declare (a `ui/templates.json` patch on `AbilitiesMenu` with source
`abilities:<category>`). It is **declared**; the engine runs it, the same for every mod and both
players:

```json
{ "abilities": [ {
  "name": "SoulsCombatStamina", "category": "Strength",
  "nameTag": "SOULS_COMBAT_STAMINA_NAME", "descriptionTag": "SOULS_COMBAT_STAMINA_DESCRIPTION",
  "maxLevel": 5, "levelKey": "souls_combat.stamina_ability",
  "costLike": 3, "refund": "half",
  "stats": [100, 128, 185, 214, 242, 271],
  "legacySaveKey": "SoulsStamina",
  "image": "art/gui/abilitytarot/souls_combat_stamina.tex" } ] }
```

| Field | Meaning |
|---|---|
| `levelKey` | the SAVED player value each player's level lives in (it goes home with that hero's save). The mod reads it where it applies the effect: `refii_mod_player_value(p, key, 0)`, `Refii.player_value`, `player:<key>` |
| `costLike` | the ability type (1..14) whose experience table prices the levels, through the game's own cost call; `costLikeSetting` names a mod setting that overrides it |
| `category`, `disciplines` | the page, and the pools the price is drawn from: split evenly (the first named takes the odd point), each share from its own pool and then General |
| `refund` | `"half"`: Y unlearns a level through the game's own confirm and result boxes and half its price comes back into General; `"none"` (default): no unlearn |
| `stats` | the description's `%1` per level 0..maxLevel (the page shows the next level's); or `stat`, a value source read for the book's player |
| `legacySaveKey` | the `RefiiModSave` key an older save kept player one's level in; a hero with none saved takes it |

A on the row buys a level for the book's player: the engine checks the price against that hero's
pools, raises **`mod_ability`** — a handler may refuse it — spends through the game's own Spend
and saves the level one higher. Beside the level the engine states, per player,
`<levelKey>.cost` (the next level's price, -1 at the maximum), `.refund` and `.afford` (1 when A
can buy), which a HUD or menu can read as `player:` sources.

```cpp
static void OnAbility(refii::mods::ModAbilityEvent& e) {
  if (std::strcmp(e.ability, "MyAbility") == 0 && !e.refund && e.level == 5 && !Ready(e.player)) e.cancel = true;
}
REFII_MOD_ON(ModAbility, OnAbility);
```

Keep ability levels in the player store, never in the game's `HeroAbility` stats: the game's
loader has no bound on that index.

## Spell slot picks: ui/spellwheel.json

A pick per spell slot beside the slot's own spell — Spell Weaving's partner. The game's own pick
(the slot's spell) and a mod's are one data form; a mod states its picks:

```json
{ "picks": [ {
  "name": "spell_weaving.partner",
  "store": "spell_weaving.partner_{slot}",
  "inEffect": "player:spell_weaving.partner_active_{slot}",
  "unlocked": "player:spell_weaving.ability",
  "exclude": [2, 7], "empty": "art/gui/hud/spell_weaving_slot.tex", "scale": 0.55,
  "apply": "SPELL_WEAVING_SET_PARTNER", "remove": "SPELL_WEAVING_REMOVE_PARTNER" } ] }
```

- On the quick wheel and the abilities blade, X on a will picks it for the highlighted slot and Y
  clears it, for the selectors' player. The engine raises **`spell_pick`** (`spell` writable,
  cancel keeps the old pick) and saves the pick under `store` (`{slot}` is the slot's number,
  1..5; -1 none) in that player's store — nothing else writes it, so a mod reads its players'
  picks from the store.
- `inEffect` and `unlocked` are value sources read for the selectors' player: what the mod says is
  in effect (a stored pick that is not is drawn faint) and when the pick is offered at all.
- The game's own pick raises the same event — `pick` "spell", `mod` "" — before it writes: every
  step of the selectors' carousel, the scripts' spell-table natives and the host's own assignment.
  A changed `spell` is the one stored; a cancel leaves the slot as it was. The template's
  defaults, the other machine's pick arriving and a save load raise nothing.
- The selectors draw every enabled mod's pick under each slot, one small icon per pick in load
  order along the slot icon's lower edge, scaled so together they cover no more of the slot than
  one. X and Y act on each player's ACTIVE pick, drawn larger; a press with nothing to do on it
  steps to the next pick, and LB/RB step while the abilities blade is up. The blade's X/Y prompts
  read the active pick's `apply` and `remove`.
- A pick of the same `name` from a later mod replaces an earlier one, so the validator warns a
  `name` without the mod's prefix.
- **`exclude` is the engine's.** The selectors declare each pick to the engine, and a will the pick
  excludes is refused on every path: an X on the selectors, a `spell_pick` command posted by
  anyone, and a `spell_pick` handler that rewrites `spell` to one — the slot keeps what it had.
- **`legacySaveKey` / `legacySaveKeyTwo`** name the `RefiiModSave` keys a save from before kept
  player one's / player two's pick in, `{slot}` the slot's number (`"SpellWeavingPartner{slot}"`,
  `"SpellWeavingPartner2_{slot}"`): once after each Lua rebuild or save load, on the first tick that
  player's hero stands where it is played, a slot whose store is empty takes that value (0..7, not
  excluded) — as an ability's `legacySaveKey`.

## Button glyphs: glyph packs

Every button prompt the UI draws — inline `<a_img>` icons, the `.bgf` prompt quads, the HUD's
world prompts, the `art/gui/controller` art — follows the player's device: keycaps on the
keyboard, and on a controller the style `glyph_style` names (`auto` follows the controller in the
player's hands: PlayStation, Switch or Xbox, detected per pad; or `xbox`, `playstation`, `switch`).
The PlayStation and Switch glyphs are drawn by the game, and every prompt names the PHYSICAL
button bound to the game's button, after the player's remap.

A **glyph pack** replaces them with art of your own: a folder of PNGs named by button.

```
glyphs/<pack>/a.png  b.png  x.png  y.png      face buttons, by POSITION: a = bottom, b = right,
                                              x = left, y = top (Cross/Circle/Square/Triangle,
                                              B/A/Y/X on a Switch pad)
               lb.png rb.png lt.png rt.png    shoulders and triggers
               start.png back.png             the two system buttons
               ls.png rs.png                  the sticks (l3.png / r3.png also read)
               dpad.png dpad_up.png dpad_down.png dpad_left.png dpad_right.png
               key_e.png key_space.png ...    keyboard: key_ + the keycap's label, lowercase
                                              (key_lmb, key_esc, key_f1)
```

- The player's packs live under `glyphs/` in the game's user data folder (`user://glyphs/<pack>/`);
  a mod shares one by shipping `glyphs/<pack>/` in its package. The pack is content the game
  reads — no script runs. The player's folder wins over a mod's of the same name, file by file.
- `glyph_pack` (the launcher's Controls group, or Options › Controls › Controller and Keyboard ›
  Glyph pack) selects it by folder name; empty is the built-in glyphs.
- Any size; square art fills a prompt best. Each file is scaled into the prompt's cell, so the
  animated strips (`24x24/Anim*`) and the `abyx` atlas cells show it in every frame.
- A button the pack has no file for falls back to the selected style, so a pack can replace just
  the face buttons.
- A pack's files are read when it is selected and again whenever `refii.toml` changes; a file
  added while the game runs shows after the next settings change or restart.

## Starters

**New mod from template** offers, among others:

| Starter | Writes |
|---|---|
| **Mod menu** | a pause-list row opening a menu whose rows call the mod's Lua |
| **HUD text** | a top-right line showing the hero's gold from a published value |
| **HUD piece** | a gold meter under the health widget, fed by a value `init.lua` publishes every tick |
| **Front end** | a title-screen row whose `mod/frontend.lua` raises a menu box and answers it with a notice |

The game's own options pages are the same kind as a mod's settings page: the Dynamic Sky and
Weather page is a description in exactly the `settings.ui.json` shape, read by the same parser
over the game's config switches. A `choice` there may store text with `aliases`; a mod's choices
stay numbers, since a mod setting holds a number.
