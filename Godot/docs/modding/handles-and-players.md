# Handles and players

How a mod names game objects, and how it serves both co-op players without a co-op channel of
its own.

## Contents

- [Lua and Godot have the same access](#lua-and-godot-have-the-same-access)
- [Handles](#handles)
- [Hashes](#hashes)
- [Both players, always](#both-players-always)
  - [Which hero?](#which-hero)
- [The player store](#the-player-store)
  - [Typed values: texts and hashes](#typed-values-texts-and-hashes)
- [Replicas and relayed events](#replicas-and-relayed-events)
- [Whose book](#whose-book)
- [Each villager's opinion of each hero](#each-villagers-opinion-of-each-hero)
- [Player two's screen](#player-twos-screen)
- [Asking the partner](#asking-the-partner)
- [Partner messages](#partner-messages)
- [Lua to Godot on one machine](#lua-to-godot-on-one-machine)

## Lua and Godot have the same access

A mod may be written in Lua, in Godot scripts, or both, and neither is the lesser surface: every
Lua call has a Godot form and every Godot call a Lua form, matched by meaning in
[the side-by-side table](reference-calls.md#lua-and-godot-side-by-side). A test fails the build
when an entry of either surface has no twin, so the rule holds as the API grows.

- **Where a surface reads at the call, the other asks.** Lua runs on the guest tick and reads the
  game at once (`Refii.health(e)`); a Godot script asks the guest — `ask("health", [e], callback)`,
  or `call_lua("Refii.health(" + str(e) + ")", callback)` for any `Refii.*` call — and the answer
  comes a frame later. Godot reads what the host holds at once (`fade_state(p)`, `lights()`,
  `input_claims(p)`); a Lua mod asks the host — `Refii.ask_host("lights", fn)` — and `fn` runs a
  tick later.
- **Inside an event, both read at once.** A Lua handler reads at the call; a Godot verdict runs on
  the game's thread inside the same seam, so `read("sprinting", [p])` answers there and then — the
  same query, the same fields, no tick late (see [Reading inside a verdict](events.md#reading-inside-a-verdict)).
  It takes the query verbs that change nothing, and only inside a verdict handler.
- **One implementation, two doors.** A twin shares state, never copies it: Godot's `pool` IS the
  Lua runtime's pool in the player store; `Refii.get_text` reads the texts the host keeps; the
  host's clock counts the same `tick` and `paused` statements Lua's does (each from its own start,
  so durations agree).
- **The other surface, both ways.** `Refii.signal` raises Godot's `mod_signal`; `signal_lua`
  raises Lua's `mod_signal`. Godot's `value_changed` and Lua's `value_changed` hear the same
  changes. See [Lua to Godot on one machine](#lua-to-godot-on-one-machine).
- **Events alike.** Every event reaches a Lua handler and a Godot signal; both refuse it (Lua
  `return true` or `e.cancel = true`, a Godot verdict's `return true` or `e.cancel = true`) and
  write the same catalog fields (see [Lua and Godot alike](events.md#lua-and-godot-alike)).
- **What one side genuinely cannot have** is listed with its reason in the table: Godot's scene
  nodes (`node_for`) and synchronous list sources, Lua's `require` and guest addresses.

## Handles

The game's Lua number is a 32-bit float, so an entity or object reaches Lua and Godot scripts as
a **handle**: a small integer that stands for that object for the session, the same in every
event and every call.

- Compare handles (`e.victim == Refii.hero(1)`), key tables by them, pass them back. Never treat
  one as an address.
- Natively, objects are addresses; `refii_mod_handle(address)` and
  `refii_mod_handle_address(handle)` convert.
- **Dead handles.** When an entity is destroyed its handle resolves to nothing from then on:
  `Refii.alive(h)` is false and helper calls do nothing. An object that later takes the same
  memory gets a new handle.
- `Refii.entity(handle)` is the game's own Entity userdata for a handle — what the shipped scripts
  and the game's natives take — and `Refii.handle(entity)` goes back.
- A handle is neither stable across saves nor the same on both co-op machines. For that, use
  `Refii.uid(e)`: the entity's own 64-bit UniqueID as `"0x…"`, the same on both machines and
  across saves.

## Hashes

A hash — an item definition, a text tag, a deed — is a `"0x…"` string, exact in a string.
`Refii.hash("OBJECT_HEALTH_POTION")` makes one from a name (the game's FNV-1);
`refii_mod_fnv1(text)` natively.

## Both players, always

The API is not a side door for mods: it is the shared layer the base game's own co-op fixes and
the mods are both written on. Events carry the player they are about (1 or 2) and every helper
takes a player or an entity. The "which hero" questions have one answer each — the one the core
co-op layer itself uses — and a mod asks it the same way.

An event decided on one machine reaches the other when its effect belongs there (see
[relayed events](#replicas-and-relayed-events)): player two's swings reach the host's AI, the
host's hits reach the joiner's looks. No mod keeps a co-op channel of its own.

### Which hero?

| Question | Native | Lua | Godot |
|---|---|---|---|
| is this a hero, and whose | `refii_mod_player_of` | `Refii.player_of(e)` | `query player_of E` |
| who is "the hero" for this NPC or dog | `refii_mod_player_for` | `Refii.player_for(e)` | `query player_for E` |
| who "the hero" means right now (`GetPlayerHero`) | — | `Refii.acting_player()` | via `run_lua` |
| run a shipped script as player p | — | `Refii.run_as_player(p, fn, ...)` | via `run_lua` |
| is player p in combat | — | `Refii.combat_state(p)` | `query combat_state P` |
| where are player p's own effects made | `refii_mod_player_here` | `Refii.player_here(p)` | `player_here(p)` |
| where are world effects made | `refii_mod_world_here` | `Refii.world_here()` | `world_here()` |
| is this event the other machine's | `refii_mod_event_replica` | `Refii.event_replica()` | `e.replica` |
| send the other machine a message | `refii_mod_send_to_partner` | `Refii.send_to_partner(mod, name, payload)` | `send_to_partner(mod, name, payload)` |
| show something to player p (a box, a question, a tutorial, a fade, a camera change) | — | `Refii.show_to_player(p, name, ...)` | via `run_lua` |
| a villager's crimes and renown for player p | `refii_mod_forgive_crimes` · `refii_mod_discovered_crime` · `refii_mod_perceived_renown` | `Refii.forgive_crimes(v, p)` · `Refii.discovered_crime(v, p)` · `Refii.perceived_renown(v, p)` | `forgive_crimes E P` · `query discovered_crime E P` · `query perceived_renown E P` |

- `player_for`: a hero is its own player; a player's dog is its master's (`Refii.player_dog`);
  anything else answers to the nearer living hero, keeping its last answer until the other hero is
  more than 3 m nearer, so an NPC between the two does not flicker. One hero standing is
  everyone's.
- `acting_player`, `run_as_player` and `combat_state` live in the game VM (the core co-op Lua
  defines them on `Refii`): they are the state the shipped scripts read, so native code has no
  copy of them.
- `world_here()` is true where the shared world runs — the AI, every hit's outcome, the NPCs:
  alone, in local co-op, and on an online host. Make world effects only there.
- `Refii.coop_mode()`: 0 alone, 1 local co-op (split or shared screen), 2 online hosting,
  3 online joined. `Refii.split_screen()` is true on split screen.
- `Refii.local_hero()` is this machine's hero: player two's on an online joiner, player one's on
  a host or alone, and in local co-op the hero of whoever owns the open menus. Name a player
  (`Refii.player_hero(p)`) when the answer must not follow the menus. In Godot, `hero(1)` is the
  HOST's hero on both machines; this machine's hero is `local_hero()`.
- Use `Refii.player_dog(p)`, never the game's `GetDog()`: inside player two's dog's own AI and
  item use, `GetDog()` answers that dog.

## The player store

Anything that belongs to one player — a pool, a level, a cooldown, a choice — goes in the player
store, never in a mod's own array. One store serves all three surfaces:

| Native | Lua | Godot |
|---|---|---|
| `refii_mod_player_value` / `_set` / `_add` / `_save` / `_here` | `Refii.player_value(p, key [, fallback])` · `Refii.player_set(p, key, v)` · `Refii.player_add(p, key, d [, saved])` · `Refii.player_save(p, key, v)` · `Refii.player_here(p)` | `player_set(p, key, v)` · `player_add(p, key, d, saved)` · `player_save(p, key, v)` · `player_value(p, key)` · `player_here(p)` · `query player_value P KEY [FALLBACK]` |

`player` is 1 or 2; `key` is `"<mod>.<name>"`. A HUD piece or menu reads a value as
`player:<key>`.

- **The same on both machines.** Online, each player's values are owned by the machine that plays
  that hero (the host for player one, the joiner for player two). A write on either machine applies
  there at once and goes to the owner; an add merges with the owner's own changes, so a spend on
  the host and a refill on the joiner both count. The owner restates its values when they change
  and every half second.
- **Saved values go home with the hero.** `player_save` (or `player_add` with `saved`) marks a key
  as the hero's. It is written beside the save the game has just written that hero into — player
  one's main save, a local player two's own save, an online joiner's own save on the joiner's
  machine — and read back when the game loads the hero. A value nobody saved is a session value.
- **Where per-player work runs.** A refill, a timer or anything a mod does every tick for a player
  runs only where `player_here(player)` is true, so it runs once per session. Event handlers need
  no test: they read and write the store wherever the event fires.
- Key per-thing values with `Refii.uid(e)`:
  `Refii.player_save(p, "mymod.opened." .. Refii.uid(e), 1)`.
- Godot reads a value NOW with `host.player_value(player, key)` (NaN when unset; a third argument
  is the fallback) — the same store a `player:` source reads, no query round trip.

### Typed values: texts and hashes

A number in the store is a float, which cannot hold a 32-bit record hash (Lua's number is a float
too). Never split a hash over two numbers: store it TYPED. A text (at most 256 bytes) is kept
exactly, under the same keys, owners and save as the numbers; a hash is kept as its `"0x%08x"`
text, so either reader reads it.

| Native | Lua | Godot |
|---|---|---|
| `refii_mod_player_text(player, key, out, capacity)` (the length, -1 for none) · `refii_mod_player_set_text` / `refii_mod_player_save_text(player, key, text)` · `refii_mod_player_hash(player, key, &hash)` · `refii_mod_player_save_hash(player, key, hash)` | `Refii.player_value_text(p, key)` · `Refii.player_set_text(p, key, text)` · `Refii.player_save_text(p, key, text)` · `Refii.player_value_hash(p, key)` → `"0x…"` · `Refii.player_save_hash(p, key, "0x…")` | `player_text(p, key)` (`""` for none) · `player_value_hash(p, key)` → `"0x…"` (`""` for none), both synchronous · `player_set_text(p, key, text)` · `player_save_text(p, key, text)` · `player_save_hash(p, key, hash)` · `query player_text P KEY` → `text`, `hash`, `has` |

- A key holds one type. A number written over a text replaces it and the other way round; the
  number readers answer nil (or the fallback) for a text key, and the text readers nil for a number.
- Saved texts go home with the hero like saved numbers (`refii_player.txt`, `key=t:<base64>`), and
  reach the other machine of an online session the same way.

```lua
Refii.player_save_hash(p, "my_mod.slot1", "0xce6b8e0f")
local record = Refii.player_value_hash(p, "my_mod.slot1")   -- "0xce6b8e0f", or nil
```

## A player's achievements

Achievements are per character: each hero earns its own, and every unlock also counts on the profile's
total. The machine that plays a hero states its unlocks on the co-op channel (the player store, key
`refii.achievements`, a session text the core writes — read it through these calls, never write it), so
both machines answer the same for either player, the online partner's hero included.

| Lua | Godot |
|---|---|
| `Refii.achievement_unlocked(p, id)` → bool · `Refii.achievements(p)` → `"id,id,…"` (ascending, `""` for none) · `Refii.character_id(p)` → `"…"` (`""` before the game bound one) | `achievement_unlocked(p, id)` → bool · `achievements(p)` → `int[]` · `character_id(p)` → `String` |

```lua
if Refii.achievement_unlocked(2, 23) then   -- player two's hero is a Hero of Many Faces
  ...
end
```

## Replicas and relayed events

**Events the game runs on both machines.** Online, the game runs some seams twice for one
occurrence: `anim_event`, `spell_charge_level`, `spell_launch`, `spell_launched`, `spell_created`
(a cast is rebuilt on the other machine), `hit_response` (the host's run and the joiner's
predicted run) and `experience` (the receiver applies the delta again). The run on the machine
that does not decide is a **copy** — `Refii.event_replica()`, `refii_mod_event_replica()`, Godot's
`e.replica`.

- A handler runs for both, and its verdict applies on both from the same player values.
- Player-state writes made during a copy are dropped — the deciding machine makes them, so a
  spend counts once.
- Only a handler with an effect on the body itself (holding the hero in place) needs to skip the
  copy.

Every other event fires on one machine: an ability request, a swing, a roll or a game action where
the hero is played; a hit, a hit landing and a block on the host.

**Relayed events.** Of those one-machine events, the ones whose effect belongs on the other
machine are relayed: Refii sends a copy after the deciding machine dispatched it, and the other
machine dispatches it as a replica.

| Event | Decided | Goes |
|---|---|---|
| `melee_swing`, `melee_swing_ended`, `roll_started`, `item_used` | where the hero is played | both ways: the host's AI sees player two's, the joiner sees player one's |
| `hit_landed` | where the world runs | host to joiner, when a hero dealt or took the hit |

In a relayed copy: entities are the receiving machine's own copies (heroes are matched by
UniqueID); the deciding machine's objects (a hit record, an ability, an action, an item) are 0;
`spell_serial` is the deciding machine's number; cancel and writes do nothing (it already
happened); it arrives a network moment later. Handlers need no co-op code: record what the event
says for `e.player` wherever it arrives, and skip a world or body effect in a copy.

## Whose book

`Refii.menu_player()` / `refii_mod_menu_player(base)` is the player whose menus are open on this
machine (1 or 2). A mod ability's row shows their level and price, the engine's purchase buys for
them, and the spell selectors pick for them.

## Each villager's opinion of each hero

A villager keeps one set of feelings per hero: player two's deeds move how it sees player two and
leave player one's alone. `Refii.opinion(villager, p)` / `refii_mod_opinion` read either; player
two's starts as the villager's own base attitudes. See [Opinion and deeds](gameplay.md#opinion-and-deeds).

## Player two's screen

What the game shows follows the player it is for. A box, question, tutorial, received money or
item banner, fade, d-pad or HUD-suggestion mode, scripted hero camera, cutscene camera, hero look
or camera point of interest that code puts up **while acting as a player** —
`Refii.run_as_player(p, …)`, AI dealing with p, a cutscene held by p — and any `Player.*` call
naming player two's hero appears on that player's screen and acts on that player's hero.

- Online it is sent to the machine that plays them and run there through the same function, so
  the box is the game's own.
- A question (`GUI.AskYesNoQuestion`, `AskSetQuestPrimaryQuestion`, `AskForAmount`,
  `ShowMenuBox`) waits for the answer that player gave and returns it.
- `Player.Is*/Has*/Get*` about player two are answered from what their machine states each frame.
- On split screen the box opens in their half, on their pad.
- `Refii.show_to_player(p, name, ...)` asks for any function outright: `name` is a global path
  (`"GUI.DisplayMessageBox"`, `"GUI.AskYesNoQuestion"`, `"TutorialManager.DisplayTutorial"`). A
  question waits — call it from a coroutine — and returns p's answer.
- An argument that cannot travel (a table, a vector) keeps the call where it was made. Movies,
  the crime screen and the d-pad/HUD of a shared local screen stay where they are.

## Asking the partner

`Refii.ask_partner(asker, question)` → ticket puts a mod's own yes/no question to the other
player — on their half in local co-op, on the peer's screen online — and
`Refii.partner_answer(ticket)` is nil until they answer, then true or false. Thirty seconds
unanswered is a no; with no partner in the game the answer is yes at once. Ask from the machine
where the asking player's action runs, and poll from a tick.

The game's own bed consent is the same mechanism. A `partner_consent` handler sees every question
first and may answer it for the partner (`e.answer = 1`): a "never ask, always agree" mod is one
handler.

## Partner messages

A mod's own message to the other machine of an online session:

| Native | Lua | Godot |
|---|---|---|
| `refii_mod_send_to_partner(mod, name, payload)` | `Refii.send_to_partner(mod, name, payload)` | `host.send_to_partner(mod, name, payload)` |

- `mod` and `name` are words (letters, digits, `_`, `.`, `-`; at most 32); `payload` is any text
  up to 256 bytes (empty is fine).
- It arrives on the other machine as the `partner_message` event — `mod`, `name`, `payload`,
  `from` (1 the host, 2 the joiner) — on every surface there, **once and in the order sent**. It
  waits out a load or a pause; a sender whose peer is gone for 20 s loses it.
- It is not a replica: player-state writes in its handler count. Either machine may send.
- Nothing is sent alone or in local co-op (the call answers false); on one machine a mod talks to
  itself through the player store.
- On the Lua and Godot surfaces a quote, backslash or line break in the payload arrives as `_`;
  native handlers get the bytes as sent.
- The sender does not get its own message back. What both machines must agree on still belongs
  in the player store.

```lua
Refii.on("partner_message", function(e)
  if e.mod == "my_mod" and e.name == "ping" then Refii.log("ping from player " .. e.from) end
end)
Refii.send_to_partner("my_mod", "ping", "hello")
```

## Lua to Godot on one machine

The same-machine twin of a partner message: a mod's Lua or native code tells its own Godot script
something, and a Godot script tells its own Lua, with no polling bus of published numbers.

| Native | Lua | Godot |
|---|---|---|
| `refii_mod_signal(name, payload)` | `Refii.signal(name, payload)` | signal `mod_signal(mod, name, payload)` (and `mod_event("mod_signal", e)`) |
| — | `Refii.on("mod_signal", fn)`: `{ mod, name, payload }` | `signal_lua(name, payload)` |
| `refii_mod_publish_text(key, text)` | `Refii.publish_text(key, text)` · `Refii.get_text(key)` · `Refii.has_text(key)` | `get_text(key)` · `has_text(key)` · `publish_text(key, text)` |
| `refii::mods::Publish(key, v)` | `Refii.publish(key, v)` · `Refii.value(key)` · `Refii.on("value_changed", fn)` | `get_value(key)` · signal `value_changed(key, value)` |

- `name` and `key` are scoped `"<mod>.<name>"` (letters, digits, `_`, `.`, `-`; at most 96). The
  signal's `mod` is the part before the first dot, `name` the rest.
- A payload or text is any text up to 1024 bytes, carried exactly (quotes, line breaks and all).
  Longer is cut to 1024 and the Mods error list says so.
- The signal reaches Godot on its next frame, in the order sent. It never leaves the machine — for
  the other machine of an online session use `send_to_partner`.
- `value_changed(key, value)` fires once per CHANGE of a published number (any surface's
  `publish`, a hook's `refii::mods::Publish`; posted while a Godot script is loaded) or text —
  `value` a float or a String — so a script connects once instead of polling `get_value`.
- Published texts are kept for the process, like published numbers (at most 4096 keys).
- A player's own values still belong in the player store; Godot reads them now with
  `player_value(player, key)`.
- The other way: `signal_lua(name, payload)` raises Lua's `mod_signal` in the game VM on the next
  guest tick (`mod/frontend.lua` does not hear it). `Refii.get_text` answers every surface's texts as
  the host states them (a Lua mod's own at once, a new game VM all of them again), and Lua's
  `value_changed { key, value }` hears the changes Godot's does, a tick later — texts always,
  numbers once a `value_changed` handler is subscribed.

```lua
Refii.signal("my_mod.saved", "slot 3")
```

```gdscript
func _ready():
    var host = Engine.get_singleton("RefiiHost")
    host.mod_signal.connect(func(mod, name, payload):
        if mod == "my_mod" and name == "saved": print("saved ", payload))
    host.value_changed.connect(func(key, value): if key == "my_mod.charge": _redraw(value))
```
