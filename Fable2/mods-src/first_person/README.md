# First Person

Play Fable II from the hero's eyes. Walk, fight and look around with the mouse or the right stick.
Your body stays in view when you look down; the head (with its hair and hat) is hidden so it never
fills the screen. **H** (**R3** on a controller) switches between first and third person (rebind it in
Options › Controls). R3 replaces the game's Camera mode; L3 + R3 still opens Photo mode.

Each player has their own: in split screen and in an online game whoever presses H switches their
own view, and each holds first person or not on their own. On one shared screen the camera frames
both heroes, so it stays the game's own there.

The whole mod is one Godot script (`godot/first_person.gd`): it holds the game's first-person camera
through `camera_override` and states how the held view behaves through `camera_tune`. It has no Lua
of its own; the engine applies the stated values inside the game's camera.

On a phone the **1st Person** pill in the top row of the touch controls is H.

## How it works

| Piece | File | What it does |
|---|---|---|
| The switch | `godot/first_person.gd`, `input.json` | `first_person.toggle` (H) flips first person for the player who pressed it (`input_action`'s `player`) and states it as that player's camera hold: `camera_override(p, CAMERA_FIRST_PERSON, {"fov": …, "pitch_limit": …, "aim": …})`, or `camera_override(p, 0)` to let it go. The hold is the game's own per-player camera override, the one its quests use for their first-person shots, so the game's own camera manager does the rest: a scene or quest that sets its own camera replaces the hold and it comes back after, a full cutscene and the canned actions (chests, doors, ladders, finishers) take over, the gun aim takes over with the third-person-aim setting on, and the hold is never written into a save. Holds are cleared each time the mods' Lua loads, so the script states them again on `lua_ready` (and starts each player's toggle as `start_on` says, as every new game VM does); it states them again when a hold setting changes (`setting_changed`), when an interactive scene starts or ends (the `cutscene` signal, the scenes setting) and when a hero arrives or leaves (`hero_ready`, `hero_gone`: a co-op partner makes a shared screen or not). No hold on a shared screen (`coop_mode()` 1 without `split_screen()`). |
| The view | `godot/first_person.gd` | Beside each hold the script states the held view with `camera_tune(p, {...}, "first_person")`, for each player this machine plays (`player_here`): `fp_view` makes the game's first-person camera (`camera/firstpersoncamera.lua`, `FirstPersonCamera`) the free-look view on a camera whose player holds first person and that the game's own look button did not open — only the look input turns it (the shipped look mode also turned with the left stick, which here walks) and the shipped body fade is left out; `fp_fov` and `fp_pitch_limit` are its field of view and pitch range; `fp_eye_forward` and `fp_eye_height` put the eye a little in front of and above the head; `fp_face` turns the hero to the view as `face_camera` says; `fp_scenes` (the scenes setting) shows a scene's look-at and framed-shot overrides (`SimpleCamera`, `SACCamera` in the manager's override set) through this camera, which faces what they frame (the speaker's mouth) until you look away. The values are stated again when a setting behind one changes, and let go with the hold. Each camera is its own player's, so both players are served alike. |
| The head | the core | Refii hides a hero's head, hair and hat in the drawing while that hero's player has the first-person camera (`camera_mode` 8: the game's own look button and this mod's hold alike). The body and the shadow keep theirs. |

The game's own first-person button (Z / middle mouse) keeps its shipped behaviour.

## Settings (Options › Mods › First Person)

| Setting | Default | Meaning |
|---|---|---|
| `start_on` | 1 | each player starts a session in first person |
| `fov` | 80 | field of view, 50–120 degrees (the shipped first-person view is 75); the hold's `fov` |
| `eye_height`, `eye_forward` | 0.05, 0.12 | metres above and in front of the head's collision point |
| `pitch_limit` | 75 | how far up or down you can look; the hold's `pitch_limit` |
| `face_camera` | 1 | turn the hero to the view: 0 never, 1 while standing, 2 always |
| `third_person_aim` | 1 | gun aim uses the game's over-the-shoulder camera; the hold's `aim` |
| `scenes` | 1 | stay in first person in conversations and interactive scenes, facing the speaker |

## Limits

- Both halves of a split screen draw the same hero, so a head hidden for its player's first person
  is hidden in the partner's half too.
- A shared screen (local co-op without split screen) has one camera for both heroes: no first
  person there.
- The eye offset, the left-stick fix, the facing and the scene conversion are the engine's
  `camera_tune` keys (`fp_*`), which Fable 2 answers; nothing here wraps the game's camera scripts.

## Iterating

The launcher packs this folder into `assets/mods/first_person.f2mod` on start. With the game
running, the Studio's Live Game tab (or the debug op `mods-remount` with `lua=true`) reloads the
Lua; the script states every hold and its view again on `lua_ready`.
