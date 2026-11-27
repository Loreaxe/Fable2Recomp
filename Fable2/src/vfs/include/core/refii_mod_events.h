// refii — THE MOD EVENT API. Named, typed events on the game's own seams, for hard mods.
//
// WHY EVENTS. A hook on a game function is the loader-level primitive (core/refii_mod.h):
// exact, but a mod using it has to know the function, its registers and its structs, and two
// mods on one function share it by a chain. An EVENT is that seam with the knowledge done once:
// refii hooks the function, reads the fields, and hands every subscribed mod a struct it can
// read, change or cancel. Subscribing to an event never conflicts with another mod and never
// needs a game function's name. This is the Fabric/Forge shape: the events API is the surface,
// hooks are the mixins underneath for what the events do not cover yet.
//
//   #include "core/refii_mod_events.h"
//
//   static void OnHit(refii::mods::HitEvent& e) {
//     if (e.attacker_player != 0) e.damage *= 2.0f;      // either hero hits twice as hard
//     if (e.victim_player != 0 && e.spell_type == 2) e.cancel = true;   // heroes ignore slow-time hits
//   }
//   REFII_MOD_ON(Hit, OnHit);
//
// EVERY EVENT CARRIES `ctx` AND `base` (the guest context — a copy, legal for guest calls — and
// guest memory) and a `cancel` flag. Where the seam allows a refusal, the event says what
// `cancel` means; where it does not, `cancel` is ignored. Writable fields are named as such;
// everything else is a copy. A `player` field is 1 or 2 when the entity is that player's
// hero and 0 otherwise — both players are principals. Handlers run in priority order (higher first; ties in registration
// order, which is load order for mods, refii's own last), every handler sees every event, and
// the final state after the last handler is what the game gets. Seams are listed per event:
// the game function (named in versions/*/refii_config.toml) and what was read from the binary.
//
// EVENT HANDLERS RUN ON THE GUEST TICK THREAD, inside the game function they wrap. Keep them
// short; never block; a guest call is fine (CallGuest/CallVirtual/refii_mod_* helpers).
//
// VERSIONING. REFII_MOD_API_VERSION rises when an event's fields change meaning or an event is
// removed; new events and new trailing fields do not bump it. A mod may test it at compile time.
// Version 2: Lua and Godot receive entities and objects as HANDLES (refii_mod_handle) and
// hashes (definitions, text) as hex strings; native structs are unchanged.

#pragma once

#include <cstdint>

#include <rex/ppc/context.h>

#define REFII_MOD_API_VERSION 2

/// The numeric kinds. Stable within an API version; the names are what mods use.
enum refii_mod_event_kind : int32_t {
  REFII_EVENT_TICK = 0,
  REFII_EVENT_HERO_READY = 1,
  REFII_EVENT_HERO_GONE = 2,
  REFII_EVENT_LUA_READY = 3,
  REFII_EVENT_SAVE_LOADED = 4,
  REFII_EVENT_GAME_ACTION = 5,
  REFII_EVENT_ABILITY_REQUEST = 6,
  REFII_EVENT_MELEE_SWING = 7,
  REFII_EVENT_ROLL_CAN_START = 8,
  REFII_EVENT_ROLL_STARTED = 9,
  REFII_EVENT_BLOCK = 10,
  REFII_EVENT_ANIM_EVENT = 11,
  REFII_EVENT_HIT = 12,
  REFII_EVENT_HIT_LANDED = 13,
  REFII_EVENT_HIT_RESPONSE = 14,
  REFII_EVENT_SPELL_CHARGE = 15,
  REFII_EVENT_SPELL_CHARGE_LEVEL = 16,
  REFII_EVENT_SPELL_LAUNCH = 17,
  REFII_EVENT_SPELL_LAUNCHED = 18,
  REFII_EVENT_SPELL_CREATED = 19,
  REFII_EVENT_EXPERIENCE = 20,
  REFII_EVENT_ENTITY_DIED = 21,
  REFII_EVENT_HERO_DOWNED = 22,
  REFII_EVENT_DAMAGE = 23,
  REFII_EVENT_LEVEL_LOADED = 24,
  REFII_EVENT_LEVEL_UNLOADING = 25,
  REFII_EVENT_GAME_SAVING = 26,
  REFII_EVENT_ITEM_ADDED = 27,
  REFII_EVENT_ITEM_USED = 28,
  REFII_EVENT_HELD_ITEM = 29,
  REFII_EVENT_WEAPON_EQUIPPED = 30,
  REFII_EVENT_ABILITY_LEVEL = 31,
  REFII_EVENT_GOLD = 32,
  REFII_EVENT_RELATION = 33,
  REFII_EVENT_INPUT_ACTION = 34,
  REFII_EVENT_QUEST_STARTED = 35,
  REFII_EVENT_QUEST_ENDED = 36,
  REFII_EVENT_QUEST_OBJECTIVE = 37,
  REFII_EVENT_DIALOGUE = 38,
  REFII_EVENT_ENTITY_SPAWNED = 39,
  REFII_EVENT_ENTITY_REMOVED = 40,
  REFII_EVENT_EXPRESSION_STARTED = 41,
  REFII_EVENT_RENOWN = 42,
  REFII_EVENT_MORALITY = 43,
  REFII_EVENT_PURITY = 44,
  REFII_EVENT_MARRIAGE = 45,
  REFII_EVENT_DOG_TRICK = 46,
  REFII_EVENT_RANGED_SHOT = 47,
  REFII_EVENT_STATUS_EFFECT = 48,
  REFII_EVENT_TARGET_CHANGED = 49,
  REFII_EVENT_OPINION_DEED = 50,
  REFII_EVENT_OPINION_MODIFIED = 51,
  REFII_EVENT_TIME_OF_DAY = 52,
  REFII_EVENT_WEATHER_THEME = 53,
  REFII_EVENT_SLEEP_MENU = 54,
  REFII_EVENT_TRAVEL = 55,
  REFII_EVENT_FAST_TRAVEL = 56,
  REFII_EVENT_MENU_OPENED = 57,
  REFII_EVENT_MENU_CLOSED = 58,
  REFII_EVENT_MENU_SCREEN = 59,
  REFII_EVENT_WEATHER_REGION = 60,
  REFII_EVENT_SLEEP_CHOSEN = 61,
  REFII_EVENT_SLOW_TIME = 62,
  REFII_EVENT_SUMMONED = 63,
  REFII_EVENT_CRIME = 64,
  REFII_EVENT_ACHIEVEMENT = 65,
  REFII_EVENT_COLLECTABLE_TAKEN = 66,
  REFII_EVENT_PARTNER_CONSENT = 67,
  REFII_EVENT_PROPERTY_BOUGHT = 68,
  REFII_EVENT_PROPERTY_SOLD = 69,
  REFII_EVENT_RENT_PAID = 70,
  REFII_EVENT_PROPERTY_RENTED = 71,
  REFII_EVENT_PROPERTY_CONDITION = 72,
  REFII_EVENT_DIVORCE = 73,
  REFII_EVENT_WIDOWED = 74,
  REFII_EVENT_CHILD_BORN = 75,
  REFII_EVENT_GIFT_GIVEN = 76,
  REFII_EVENT_GIFT_ACCEPTED = 77,
  REFII_EVENT_ENGAGED = 78,
  REFII_EVENT_FOLLOW = 79,
  REFII_EVENT_FURNITURE_UPGRADED = 80,
  REFII_EVENT_REDECORATING = 81,
  REFII_EVENT_HOUSEHOLD_BUDGET = 82,
  REFII_EVENT_TRACKED_STAT = 83,
  REFII_EVENT_PARTNERSHIP = 84,
  REFII_EVENT_HOUSEHOLD_RESTORED = 85,
  REFII_EVENT_DYNAMIC_WEATHER = 86,
  REFII_EVENT_MOD_MENU_ROW = 87,
  REFII_EVENT_MOD_MESSAGE_BOX = 88,
  REFII_EVENT_MOD_NOTICE = 89,
  REFII_EVENT_MOD_MENU_BOX = 90,
  REFII_EVENT_MOD_ABILITY = 91,
  REFII_EVENT_SPELL_PICK = 92,
  REFII_EVENT_PARTNER_MESSAGE = 93,
  REFII_EVENT_LOOK_AT_BUTTON = 94,
  REFII_EVENT_VORTEX_STARTED = 95,
  REFII_EVENT_VORTEX_ENDED = 96,
  REFII_EVENT_FIREBALL_IMPACT = 97,
  REFII_EVENT_PAUSED = 98,
  REFII_EVENT_START_GAME = 99,
  REFII_EVENT_CUTSCENE = 100,
  REFII_EVENT_HEALTH_CHANGED = 101,
  REFII_EVENT_STATUS_APPLYING = 102,
  REFII_EVENT_SAVE_WRITTEN = 103,
  REFII_EVENT_LOCK_LOST = 104,
  REFII_EVENT_ATTACK_MODE_CHANGED = 105,
  REFII_EVENT_ITEM_REMOVED = 106,
  REFII_EVENT_EXPRESSION_WHEEL = 107,
  REFII_EVENT_SPELL_ENDED = 108,
  REFII_EVENT_SPELL_CHARGE_ENDED = 109,
  REFII_EVENT_MELEE_SWING_ENDED = 110,
  REFII_EVENT_ACTION_MOVE_INTERRUPT = 111,
  REFII_EVENT_CAMERA_CHANGED = 112,
  REFII_EVENT_VORTEX_CAUGHT = 113,
  REFII_EVENT_APPEARANCE_CHANGED = 114,
  REFII_EVENT_MORPH_CHANGED = 115,
  REFII_EVENT_GUILD_SEALS = 116,
  REFII_EVENT_TREASURY = 117,
  REFII_EVENT_KINGDOM_DECISION = 118,
  REFII_EVENT_CHEST_OPENED = 119,
  REFII_EVENT_ITEM_BOUGHT = 120,
  REFII_EVENT_ITEM_SOLD = 121,
  REFII_EVENT_SHOP_PRICE = 122,
  REFII_EVENT_DIG_SPOT_DUG = 123,
  REFII_EVENT_DEMON_DOOR = 124,
  REFII_EVENT_DRINK = 125,
  REFII_EVENT_SANCTUARY = 126,
  REFII_EVENT_SANCTUARY_REQUEST = 127,
  REFII_EVENT_WEAPON_LEVEL_UP = 128,
  REFII_EVENT_WEAPON_AUGMENT_UNLOCKED = 129,
  REFII_EVENT_WEAPON_UPGRADE_READY = 130,
  REFII_EVENT_HAND_HOLDING = 131,
  REFII_EVENT_HAND_HOLDING_REQUEST = 132,
  REFII_EVENT_INTERACTION_STARTED = 133,
  REFII_EVENT_INTERACTION_RESULT = 134,
  REFII_EVENT_INTERACTION_MODE = 135,
  REFII_EVENT_RELATIONSHIP_STAGE = 136,
  REFII_EVENT_DOG_DIG = 137,
  REFII_EVENT_DOG_LEADING = 138,
  REFII_EVENT_DOG_TRICK_LEARNED = 139,
  REFII_EVENT_DOG_LEARNED = 140,
  REFII_EVENT_DIVE_SPOT_USED = 141,
  REFII_EVENT_VOMIT = 142,
  REFII_EVENT_JOB_STARTED = 143,
  REFII_EVENT_JOB_LEVEL = 144,
  REFII_EVENT_JOB_EARNINGS = 145,
  REFII_EVENT_JOB_WORKED = 146,
  REFII_EVENT_JOB_MULTIPLIER = 147,
  REFII_EVENT_JOB_SUMMARY = 148,
  REFII_EVENT_GUILD_MESSAGE = 149,
  REFII_EVENT_TITLE_CHANGED = 150,
  REFII_EVENT_TITLE_UNLOCKED = 151,
  REFII_EVENT_CHAPTER_PROGRESS = 152,
  REFII_EVENT_SCAR_ADDED = 153,
  REFII_EVENT_SCAR_REMOVED = 154,
  REFII_EVENT_AGE_CHANGED = 155,
  REFII_EVENT_HERO_STAGE = 156,
  REFII_EVENT_CLOTHING_WORN = 157,
  REFII_EVENT_CLOTHING_REMOVED = 158,
  REFII_EVENT_CLOTHING_DYED = 159,
  REFII_EVENT_PUB_GAME_STARTED = 160,
  REFII_EVENT_PUB_GAME_FINISHED = 161,
  REFII_EVENT_VILLAGE_WEALTH = 162,
  REFII_EVENT_GARGOYLE_DESTROYED = 163,
  REFII_EVENT_CAMERA_CUT = 164,
  REFII_EVENT_CAMERA_BLEND = 165,
  REFII_EVENT_CAMERA_SCREEN_CHANGED = 166,
  REFII_EVENT_CAMERA_ZOOM_CHANGED = 167,
  REFII_EVENT_CAMERA_TICK = 168,
  REFII_EVENT_COUNT = 169,
};

/// What took a hero's camera and controls away from free play (CutsceneEvent's `kind`, and the
/// bits refii_mod_in_cutscene answers): each is one statement of the game's own, read as stated.
///   FULL        a full cutscene is the top game-flow state: `GUI.PlayFullCutscene` queued it,
///               `GUI.IsFullCutscenePlaying` answers it. Every player's at once.
///   INTERACTIVE a foreground interactive cutscene holds the hero: `Player.SetForegroundCutsceneInEffect`.
///   LOOK_AT     the game has given that hero's held LT a camera: `Player.SetOverrideCameraMode` with
///               the LOOK_AT scope (or ALWAYS) — a conversation's talking camera, a quest's look-at.
#define REFII_CUTSCENE_FULL 1
#define REFII_CUTSCENE_INTERACTIVE 2
#define REFII_CUTSCENE_LOOK_AT 4

/// The kind the host's query replies arrive under (RefiiHost in Godot): never a bus event.
#define REFII_MOD_HOST_REPLY_KIND 1000

/// The kind a play_fx / stop_fx request crosses to Godot under (refii_mod_play_fx in
/// core/refii_mod.h; Engine_FxPlayer draws it): never a bus event, posted whatever the mask says.
#define REFII_MOD_HOST_FX_KIND 1001

/// The kind an open_menu / message_box / notice / menu_box request crosses to Godot under
/// (refii_mod_open_menu, refii_mod_message_box, refii_mod_notice, refii_mod_menu_box in
/// core/refii_mod.h; Engine_UiModBoxes acts on it): never a bus event, posted whatever the mask says.
#define REFII_MOD_HOST_UI_KIND 1002

/// The kind a mod menu's rows and a host screen request cross to Godot under
/// (refii_mod_set_menu_rows, refii_mod_open_screen in core/refii_mod.h; Engine_UiModRows acts on
/// it): never a bus event, posted whatever the mask says.
#define REFII_MOD_HOST_MENU_KIND 1003

/// The kind a written save package crosses to Godot under (`player=N,name='X'`, posted by the
/// player-state hooks after the guest closed the package; Engine_ModSaveGate stamps the save's
/// sidecar with the mod set): never a bus event, posted whatever the mask says.
#define REFII_MOD_HOST_SAVE_KIND 1004

/// The kind an online JOINER's weapon pick crosses to Godot under (`player=N,op='weapon',
/// record='0x…'`, posted by refii_mod_equip_weapon's seam in the guest tick pump; CoopHeroKit puts it
/// in the kit the host equips from): never a bus event, posted whatever the mask says.
#define REFII_MOD_HOST_HERO_KIND 1005

/// A subscriber: `event` is the kind's struct (below), `user` is what was registered with it.
typedef void (*refii_mod_event_fn)(void* event, void (*user)(void));

/// Subscribes `fn` to `kind` at `priority` (higher runs first). `owner` is the mod's name, for
/// the log. A kind at or above REFII_EVENT_COUNT is a mod's own (raised with refii_mod_dispatch).
/// Returns the subscription's index, or -1 for a negative kind or no function.
extern "C" int32_t refii_mod_subscribe(int32_t kind, refii_mod_event_fn fn, void (*user)(void), int32_t priority,
                                       const char* owner);

/// The API version the running payload was built with.
extern "C" int32_t refii_mod_api_version(void);

/// Fires `event` of `kind` to every subscriber (native, then the mods' Lua handlers). What
/// refii's own seams call; a mod may raise its own kinds this way only above REFII_EVENT_COUNT.
extern "C" void refii_mod_dispatch(int32_t kind, void* event);

#ifndef REFII_MOD_NAME
/// The launcher defines this per mod (`-DREFII_MOD_NAME="<mod>"`); refii's own sources have none.
#define REFII_MOD_NAME "refii"
#endif

namespace refii::mods {

/// What every event carries.
struct Event {
  PPCContext* ctx;
  uint8_t* base;
  /// Set to refuse, where the event says a refusal exists; otherwise ignored.
  bool cancel;
};

/// Once per guest tick, menus included, after the game's own tick work. `seconds` is real time
/// since the last tick, capped at a quarter second across a hitch.
struct TickEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_TICK;
  float seconds;
};

/// A player's hero exists (HeroReady: a session or save started, a co-op partner joined) or
/// stopped existing (HeroGone). `player` is 1 or 2. Derived in the tick from the player
/// manager's handles (refii_mod_player_hero); fires on the first tick the change is seen.
struct HeroEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HERO_READY;
  int32_t player;
  uint32_t hero;
};

struct HeroGoneEvent : HeroEvent {
  static constexpr int32_t kKind = REFII_EVENT_HERO_GONE;
};

/// The game's Lua state was rebuilt and every mod's init.lua has run (LuaReady), or a save's
/// Lua table was loaded into it (SaveLoaded). Either steps `generation`
/// (refii_mod_lua_generation). A mod keeping state in RefiiModSave re-reads it here.
struct LuaEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_LUA_READY;
  uint32_t generation;
};

struct SaveLoadedEvent : LuaEvent {
  static constexpr int32_t kKind = REFII_EVENT_SAVE_LOADED;
};

/// A GameAction reached a player hero's action receiver: a button's press or release as the
/// control scheme maps it (49 TARGET_LOCK_SET and 50 _RELEASE for LT, 11 Interact ...; the
/// ids are the GDB's GameAction rows). `cancel` swallows it: the game never sees the action.
/// Seam: rex_CPlayerActionReceiver_ProcessAction (r3 receiver, r4 message whose first word is
/// the action; the receiver's entity is *(*(r3+0xC))).
struct GameActionEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_GAME_ACTION;
  uint32_t hero;
  int32_t player;
  uint32_t action;
  /// The 24-byte action message, for the fields past the id.
  uint32_t message;
};

/// A creature asked its ability set for an ability: 16 melee pressed, 17 melee released, 18
/// magic charge, and the rest of the game's AbilityType enum (the request's vtable slot 1).
/// `cancel` refuses it (the request matches nothing). A refused melee press refuses its release:
/// the bus raises that entity's next release already cancelled and refuses it whatever a handler
/// says. Seam: rex_CECAbilities_PerformRequest (r3 entity, r4 request).
struct AbilityRequestEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ABILITY_REQUEST;
  uint32_t entity;
  int32_t player;
  uint32_t request;
  uint32_t type;
};

/// A strike's swing has started (its animation is playing); not refusable here — refuse the
/// AbilityRequest instead. Seam: rex_CAbilityActionMelee_CommitSwing (r3 ability, r4 entity),
/// called by every strike's Perform after PlayAction succeeded.
struct MeleeSwingEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MELEE_SWING;
  uint32_t entity;
  int32_t player;
  uint32_t ability;
  /// The live strike action the entity's action manager (component 49) now runs, or 0.
  uint32_t action;
  /// The action's scriptable type (EScriptableAction, 0..88; -1 unknown) and its name
  /// (refii_mod_action_type / refii_mod_action_name; "" unknown).
  int32_t action_type;
  const char* action_name;
  /// The strike's kind as the game states it: the ability's EAbilityActionType (ability +0x10,
  /// which CommitSwing copies to the action's +0x7C): 1 strike, 2 flourish class, 24 release
  /// strike, 35 release strike confined, 36 advanced strike, 37 wall smash, 38 wall leap, 43 / 44
  /// lunge close / far, 45 sprint attack. Every strike action is cloned by the action manager, so
  /// action_type is -1 here; this is the type to read.
  int32_t strike;
  /// The ability's own GDB "Flourish" property: true for the hero's charged flourish (a release
  /// strike the charge let start), read through the game's own property lookup.
  bool flourish;
};

/// The roll asks whether it may start (RollCanStart: `cancel` refuses, asked once per matching
/// roll record, so it never spends) and reports it was queued (RollStarted: after the fact).
/// Seams: rex_CAbilityActionStrafeRoll_CanStart / _Start (r4 entity; Start returns 1 only when
/// a roll was queued). Rolls are already invulnerable for their whole length in the game.
struct RollEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ROLL_CAN_START;
  uint32_t entity;
  int32_t player;
};

struct RollStartedEvent : RollEvent {
  static constexpr int32_t kKind = REFII_EVENT_ROLL_STARTED;
};

/// A block is taking a hit. `cancel` BREAKS THE GUARD: the hit lands in full, as if there were
/// no block. Fired once per hit (two block classes each register one listener — pose and response;
/// when both are live on a victim the second takes the first's verdict). Refusing a response-class
/// block also drops a parry it would make (hit +0x1A). `unblockable` hits bypass blocks in the game already
/// and are reported with cancel ignored. Seams: rex_CActionBlock_OnHitBlockPose /
/// _OnHitBlockedResponse (r3 listener at action+0x54, r4 hit, r5 result word).
struct BlockEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_BLOCK;
  uint32_t blocker;
  int32_t player;
  uint32_t hit;
  float damage;
  bool unblockable;
};

/// An animation event reached an action: `name` is the event's text ("ALLOW_ACTION_INTERRUPT"
/// is the one that lets the next action in). `cancel` drops it. Seam: rex_CAction_OnAnimEvent
/// (r3 live action, r4 animation, r6 value object; the text is **r6).
struct AnimEventEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ANIM_EVENT;
  uint32_t action;
  const char* name;
  /// The action's owner, as the action states it (its weak reference at +4): any creature.
  uint32_t entity;
  /// 1 or 2 when the owner is a player hero, else 0.
  int32_t player;
  /// The action's scriptable type (EScriptableAction, 0..88; -1 unknown) and its name
  /// (refii_mod_action_type / refii_mod_action_name; "" unknown).
  int32_t action_type;
  const char* action_name;
};

/// A hit is arriving at its victim — melee, spell, physics, the Blades' stabs: the one receiver
/// every hit reaches. Fired BEFORE the game delivers it: `damage` is writable, `cancel` drops
/// the hit entirely (no damage, no response). HitLanded fires after, read-only. `spell_type` is
/// -1 for a non-spell hit (0 Lightning 1 Fireball 2 SlowTime 3 Swords 4 Vortex 5 Chaos 6
/// ForcePush 7 DeadRising), `spell_level` 0..4, `spell_serial` the cast's number. Seam:
/// rex_CECHitReceiver_OnHit (r3 victim's component 31, owner at +4; r4 hit: +0 attacker ref,
/// +0x38 damage, +0x44 serial, +0x48 level, +0x54 type).
struct HitEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HIT;
  uint32_t victim;
  int32_t victim_player;
  uint32_t attacker;
  int32_t attacker_player;
  uint32_t hit;
  /// Writable before delivery.
  float damage;
  int32_t spell_type;
  int32_t spell_level;
  uint32_t spell_serial;
  /// The hit asks for a knockdown (hit +0x18; force push sets it, as do heavy blows). Writable
  /// before delivery: the victim's hit response reads it to choose the knockdown.
  bool knockdown;
  /// The tag of the cast whose serial the hit carries (SpellLaunchEvent's), 0 none.
  int32_t tag;
};

struct HitLandedEvent : HitEvent {
  static constexpr int32_t kKind = REFII_EVENT_HIT_LANDED;
};

/// The victim's hit-response component is about to play the flinch or knockdown for a hit.
/// `cancel` absorbs it: no animation, no state left behind. The game's own early-outs (a
/// blocked or parried hit, a ranged hit, a knockdown request, a killing blow) are honoured
/// before this fires, so a lethal hit's fall always plays. `damage` is the game's own
/// arithmetic for the hit. Seam: rex_CECHitResponsePlayAction_OnHit (r3 listener, entity at
/// r3-8; r4 hit; r5 result word), priority 20 on the dispatch, before health's 30.
struct HitResponseEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HIT_RESPONSE;
  uint32_t victim;
  int32_t victim_player;
  uint32_t hit;
  float damage;
  float health;
  float max_health;
};

/// A player hero pressed B to charge a spell. `cancel` refuses the charge. Seam:
/// rex_CAbilityActionMagicCharge_CanStart (r4 entity).
struct SpellChargeEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SPELL_CHARGE;
  uint32_t entity;
  int32_t player;
};

/// The charge time became the top slot to cast; `level` (0..4, writable) is the slot the
/// release casts at full power — the cascade launches slots 0..level, each at its own power.
/// Seam: rex_CECSpellManager_ChargeLevelFromTime (r3 spell manager; r6 int* count of charged
/// slots, r7 int* first slot — the top is first + count - 1; a write moves the count, or the
/// first slot in the game's single-slot mode).
struct SpellChargeLevelEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SPELL_CHARGE_LEVEL;
  uint32_t caster;
  int32_t player;
  int32_t level;
};

/// One slot is about to launch. `type` (writable) is the spell the slot casts for this launch
/// only (the slot keeps its own assignment); `cancel` skips the launch. `cascading` says the
/// release cascade is running through the lower slots (slots_to_come > 0 at a lower slot).
/// Seam: rex_CECSpellManager_LaunchSlot (r3 manager = component 56, caster at +4; r4 targeted;
/// r5 slot; slot table int8[2][5] at +0x80; cascade state +0x70 == 5, +0x170 slots to come).
struct SpellLaunchEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SPELL_LAUNCH;
  uint32_t caster;
  int32_t player;
  uint32_t manager;
  bool targeted;
  int32_t slot;
  int32_t type;
  bool cascading;
  /// THE CAST'S TAG, writable (0 none): stamped on the spell this launch creates and carried by
  /// every later event of that cast — spell_created, vortex_started, hit, hit_landed,
  /// fireball_impact, status_applying — by its serial. The factory takes the serial from its
  /// registry's counter before it builds the spell (`lwz r19,0xC(r18)` 0x824b1934, `stw
  /// r19,0x44(r31)` 0x824b1ec4, then the class's Init and Start 0x824b1eec / 0x824b1f00), so a hit
  /// landed inside the factory carries it too.
  int32_t tag;
};

/// A spell was really created for a launch. Seam: rex_CECSpellManager_OnSpellLaunched (r3
/// manager, r4 slot), reached only behind the launch's own success test.
struct SpellLaunchedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SPELL_LAUNCHED;
  uint32_t caster;
  int32_t player;
  uint32_t manager;
  int32_t slot;
};

/// The spell factory made a spell object — every cast, the player's and any creature's, and
/// refii_mod_cast_spell's. Read-only. Seam: rex_CSpellRegistry_Create (r3 registry, r4 type,
/// r5 targeted, r6 target, r7 level, r8 caster; the object's serial at +0x44).
struct SpellCreatedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SPELL_CREATED;
  uint32_t spell;
  int32_t type;
  bool targeted;
  uint32_t target;
  int32_t level;
  uint32_t caster;
  int32_t caster_player;
  uint32_t serial;
  /// The launch slot the spell came from (spell_launch's `slot`), -1 for a cast no launch made
  /// (a creature's own cast, refii_mod_cast_spell).
  int32_t slot;
  /// The cast's tag: spell_launch's, or refii_mod_cast_spell_with's; 0 none. On the machine not
  /// deciding the caster online, the deciding machine's tag once the cast is paired.
  int32_t tag;
  /// This spell's serial on the online HOST (refii_mod_host_serial): `serial` itself on the host,
  /// alone and in local co-op; on the joiner the host's spell of the same cast once paired, 0
  /// before (a cast pairs by its replicated release, mod_events.cpp THE CAST PAIRS).
  uint32_t host_serial;
};

/// Experience is changing on an entity's experience component: `spend` false is a Modify
/// (earned or refunded; `amount` signed), true a Spend (`amount` the cost; a shortfall comes
/// out of General). Pools: 0 General, 1 Strength, 2 Skill, 3 Will. `amount` is writable,
/// `cancel` leaves the pools untouched. Seams: rex_CECExperience_Modify (r3 component 164, r4
/// pool, f1 amount, r6..r8 flags) / rex_CECExperience_Spend (r3 component, r4 pool, f1 cost).
/// NESTED: a Spend is done by one or two Modify calls (the pool, then General for a shortfall,
/// 0x8256e330/0x8256e348/0x8256e368), so one `spend` event is followed by the negative Modify
/// events that pay it; and a co-op share recurses on the partner's component (0x8256de64), a
/// second event for the other player. A handler summing amounts counts each change once only
/// by taking the Modify events and ignoring `spend`.
struct ExperienceEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_EXPERIENCE;
  uint32_t entity;
  int32_t player;
  uint32_t component;
  int32_t pool;
  float amount;
  bool spend;
  /// Raised inside another Experience event: a Spend's own payments, or the game's co-op share of
  /// a change already reported. A handler that scales `amount` acts only when this is false;
  /// a nested event cannot be cancelled (it would half-apply its outer change).
  bool nested;
};

/// A creature is dying, from damage (the health component at zero), a script kill, a fall, or a
/// death finished without the dying step (rex_CECCreature_FinishDeath: knockdown ends, scripts).
/// `cancel` keeps it alive only for a death by damage (health at zero): the death is not started
/// and health is set to 1 so the next hit asks again; other deaths cannot be refused — their
/// callers act on the death regardless. Heroes never come here from damage — see HeroDowned. Seam:
/// rex_CECCreature_Die (r3 creature component 20, owner at +4, alive byte +0x18; r4 killer or 0).
struct EntityDiedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ENTITY_DIED;
  uint32_t entity;
  int32_t player;
  uint32_t killer;
  int32_t killer_player;
};

/// A player's hero reached zero health: the knockdown, the scar, the resurrection potion and
/// the revive flow are what follows. `cancel` skips all of it and sets the hero's health to 1,
/// as the game itself does on its way in. Seam: rex_CECHealth_OnHeroDeath (r3 health component
/// 36, owner at +4; r4 killer or 0), reached from the zero-health dispatch for either hero.
struct HeroDownedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HERO_DOWNED;
  uint32_t hero;
  int32_t player;
  uint32_t killer;
  int32_t killer_player;
};

/// The victim's health is about to take a hit's damage: the health component's own listener
/// on the hit dispatch (priority 30, after responses and blocks). `damage` is the game's final
/// arithmetic for the hit (rex_CECHealth_CalculateDamage), read-only here — change it on the
/// Hit event, before delivery. `cancel` removes no health and starts no death; every other
/// listener on the hit (the flinch, the sound) has already run. Seam: rex_CECHealth_OnHit (r3
/// listener = health+0xC, entity at r3-8; r4 hit; r5 handled word, set when a block took it).
struct DamageEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DAMAGE;
  uint32_t victim;
  int32_t victim_player;
  uint32_t attacker;
  int32_t attacker_player;
  uint32_t hit;
  float damage;
  float health;
  float max_health;
  bool lethal;
  int32_t spell_type;
  int32_t spell_level;
  uint32_t spell_serial;
};

/// A level finished loading and the heroes stand in it, on a new game, a fast travel and a
/// save load alike; fired after the game's own Lua `AIManager:OnLevelLoad(fromSave)`. `level` is
/// the level's name (the world/level manager's current level, +0xAC). Region-internal level
/// changes (walking through a door) do not come here. Seam: rex_lhLuaScriptSystem_OnLevelLoad
/// (r3 script-system owner, r4 from-save byte), after it.
struct LevelLoadedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_LEVEL_LOADED;
  const char* level;
  bool from_save;
};

/// The current level is about to be unloaded (a travel, a load, the session's end): the last
/// moment its entities exist. Seam: rex_lhWorld_UnloadLevel (r3 world manager; +0x24 the level
/// holder), before it, only when a level is loaded.
struct LevelUnloadingEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_LEVEL_UNLOADING;
  const char* level;
};

/// A save file is about to be written (the main save, and a quest-fail save when one is made):
/// the moment to put state into RefiiModSave. Seam: rex_lhSaveManager_WriteSave (r3 save
/// manager, r4 file name string), before it.
struct GameSavingEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_GAME_SAVING;
  const char* file;
};

/// An item was added to an entity's inventory (component 13): a pickup, a purchase, a gift,
/// a quest reward, a script, the starting kit — by its definition's hash (the GDB record). One
/// more of an item already held raises its count. After the add, not cancellable: several callers
/// use the new item at once and cannot take a refusal (verified: two crash on it, one destroys the
/// picked-up entity regardless). Seam:
/// rex_CECInventory_AddOneOfRecord (r3 out, r4 inventory, owner at +4; r5 definition hash; r6
/// notify), the funnel every add goes through.
struct ItemAddedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ITEM_ADDED;
  uint32_t entity;
  int32_t player;
  uint32_t inventory;
  uint32_t definition;
  bool notify;
};

/// An entity is using an inventory item — a potion, food, a book, a gift: anything whose
/// definition has a carried-action script; the script does the consuming. `cancel` refuses the
/// use (the game answers "not used"). Seam: rex_lhEntity_UseInventoryItem (r3 entity, r4 the
/// item's shared pointer: item at *r4, definition at item+4, count at item+12; r5 script override).
struct ItemUsedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ITEM_USED;
  uint32_t entity;
  int32_t player;
  uint32_t item;
  uint32_t definition;
  int32_t count;
};

/// An item entity is being attached to, or released from, one of an entity's holding slots
/// (component 24): drawing and sheathing a weapon are a release from the holding slot and an
/// attach to the hand, a pick-up is an attach. `cancel` refuses it (a refused release makes the
/// attach that follows see nothing). Seams: rex_CECHeldItems_AttachItemToSlot (r3 held items,
/// owner at +4; r4 slot name; r5 item entity) / rex_CECHeldItems_ReleaseSlot (r3, r4 slot name;
/// returns the item released).
struct HeldItemEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HELD_ITEM;
  uint32_t entity;
  int32_t player;
  const char* slot;
  /// The item entity: known on an attach, 0 on a release until the game has done it. Only a
  /// release can be refused: an attach's callers record the drawn weapon regardless.
  uint32_t item;
  bool attached;
};

/// A weapon was equipped into an entity's weapon set (component 28): the melee or ranged
/// record changes — at equip time, not on a draw. Read-only. Seam:
/// rex_CECWeaponSet_RecordDrawnItem (r3 weapon set, owner at +4; r4 item entity, definition at
/// +0x1C), before it.
struct WeaponEquippedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_WEAPON_EQUIPPED;
  uint32_t entity;
  int32_t player;
  uint32_t item;
  uint32_t definition;
};

/// An ability's level is being set on an entity's stats (component 57): the abilities book's
/// purchase and unlearn, a script, a save load. `level` is writable; `cancel` leaves the level
/// as it was (the book has already spent the experience by then). Seam:
/// rex_CECStats_SetAbilityLevel (r3 stats, owner at +4; r4 type 1..14; r5 level; r6 force).
struct AbilityLevelEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ABILITY_LEVEL;
  uint32_t entity;
  int32_t player;
  int32_t type;
  int32_t level;
  int32_t old_level;
};

/// Gold is changing on an entity's purse (component 81): a purchase, a sale, a pickup, rent, a
/// gift, a reward. `delta` (signed) is writable; `cancel` changes nothing. `passive` marks
/// income (rent) that the HUD shows with its icon. Seam: rex_CECMoney_Modify (r3 money
/// component, owner at +4, amount at +12; r4 delta; r5 no-split; r6 co-op share; r7 passive).
/// NESTED: the co-op share recurses on the partner's purse (0x8242e938, 0x8242eb60), a second
/// event for the other player.
struct GoldEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_GOLD;
  uint32_t entity;
  int32_t player;
  int32_t gold;
  int32_t delta;
  bool passive;
  /// The game's co-op split of a change already reported (forwarded to player one, or the
  /// partner's share): a handler that scales `delta` acts only when this is false.
  bool share;
};

/// The game asked how `entity` stands to `other` — its faction rule, asked by the AI, the
/// targeting, the HUD's colours, many times a tick. `relation` (writable) is the answer: 1
/// neutral, 2 ally, 4 enemy; a handler makes two creatures friends or foes. Not cancellable. Seam:
/// rex_CECFaction_RelationTo (r3 faction component 133, owner at +4; r4 the other entity; returns
/// the relation), after it. refii_mod_relation calls the game's body directly and never raises this.
struct RelationEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_RELATION;
  uint32_t entity;
  int32_t player;
  uint32_t other;
  int32_t other_player;
  int32_t relation;
};

/// A mod's own input action changed state: `name` is "<mod>.<action>" from the mod's input.json,
/// `player` 1 or 2 (player 1 is the keyboard and the first pad, player 2 the second pad), `pressed`
/// true on the press and false on the release. Edges only, raised in the guest tick from the
/// host's queue (Godot reads the devices; RefiiHostPostModInput). Not cancellable.
struct InputActionEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_INPUT_ACTION;
  const char* name;
  int32_t player;
  bool pressed;
};

/// A quest is being activated on a hero's quest tracker (component, owner at +4): `quest` is the
/// quest's name (the GDB record's, the one the shipped scripts pass), `make_primary` whether it
/// becomes the tracked quest. `cancel` leaves it inactive. Seam: rex_CECQuestTracker_SetAsActive
/// (r3 tracker, r4 name, r5 flag, r6 make-primary; the record's +13 active and +14 activated bytes).
struct QuestStartedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_QUEST_STARTED;
  const char* quest;
  uint32_t hero;
  int32_t player;
  bool make_primary;
};

/// A quest ended: `completed` (the rewards were just given) or `failed`. After the fact, so not
/// cancellable. Seam: rex_CECQuestTracker_OnQuestEnded (r3 tracker, r4 record: +0 name LHString,
/// +21 completed, +22 failed), the one function both the completion and the failure reach.
struct QuestEndedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_QUEST_ENDED;
  const char* quest;
  uint32_t hero;
  int32_t player;
  bool completed;
  bool failed;
};

/// An objective is being added to a quest: `tag` is its text tag (the quest log's line). `cancel`
/// adds nothing. Seam: rex_CECQuestTracker_SetObjectiveTagSilently (r3 tracker, r4 quest name, r5
/// tag), the record write behind SetObjectiveTag, SetObjectiveTagFinal and a quest's default.
struct QuestObjectiveEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_QUEST_OBJECTIVE;
  const char* quest;
  const char* tag;
  uint32_t hero;
  int32_t player;
};

/// A creature is about to speak a line: `text` is the line's text hash (the TextManager key, the
/// final tag after the voice and variant suffixes), `listener` the entity it is said to or 0,
/// `subtitle` whether the subtitle is forced. `cancel` keeps the line unspoken (no audio, no
/// subtitle, no speech animation). Seam: rex_CECTalk_PlayLine (r3 talk component, owner at +4;
/// r4 text hash; r5 force subtitle; r6 listener), the one function every line reaches.
struct DialogueEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DIALOGUE;
  uint32_t speaker;
  int32_t speaker_player;
  uint32_t text;
  uint32_t listener;
  int32_t listener_player;
  bool subtitle;
};

/// An entity joined the world: created by a script or a generator, or loaded with a level or a
/// save. Raised on the first tick after the world's entity manager took it, when its components
/// exist (the manager's add comes before they are built), at most 64 a tick, oldest first, so a
/// level load's burst spreads over a few ticks. Not cancellable. Seam: rex_lhEntityManager_Add
/// (r3 the manager, r4 the entity: +0x14 its 64-bit id; the one add every constructor path reaches).
struct EntitySpawnedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ENTITY_SPAWNED;
  uint32_t entity;
  int32_t player;
  uint32_t definition;
};

/// An entity is leaving the world (killed and cleaned up, destroyed by a script, unloaded with its
/// level): the last moment it is whole. Not cancellable. Seam: rex_lhEntity_Destroy (r3 the entity),
/// before it — the one function that clears the in-world bit (+0x90 & 0x40).
struct EntityRemovedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ENTITY_REMOVED;
  uint32_t entity;
  int32_t player;
  uint32_t definition;
};

/// A creature starts an expression (the wheel, a script's PerformExpression, a trophy): `expression`
/// is the expression record's hash. `cancel` refuses it. Seam:
/// rex_CECExpressionPerformer_StartExpression (r3 performer component, owner at +4; r4 the record
/// pair {hash, entry}; returns 1 when started), before it.
/// (Raised before the game's own can-perform check, which may still refuse it.)
struct ExpressionStartedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_EXPRESSION_STARTED;
  uint32_t entity;
  int32_t player;
  uint32_t expression;
};

/// Renown is changing on an entity's stats (57; +28). `delta` is writable; `cancel` leaves it.
/// `share` marks the co-op partner's share (the game's own recursion, a second event). Seam:
/// rex_lhHeroStats_ModifyRenown (r3 stats, owner at +4; r4 delta; r5 share), before it.
struct RenownEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_RENOWN;
  uint32_t entity;
  int32_t player;
  int32_t delta;
  bool share;
};

/// Morality (good/evil, stats +16) or purity (pure/corrupt, stats +36) is changing: `delta`
/// writable, `cancel` leaves it; the game clamps afterwards. Seams: rex_lhHeroStats_ModifyMorality /
/// rex_lhHeroStats_ModifyPurity (r3 stats, owner at +4; f1 delta), before them.
struct MoralityEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MORALITY;
  uint32_t entity;
  int32_t player;
  float delta;
};

struct PurityEvent : MoralityEvent {
  static constexpr int32_t kKind = REFII_EVENT_PURITY;
};

/// A hero marries (the wedding ceremony or a script). `cancel` refuses it. Seam:
/// rex_CECPlayerFamily_Marry (r3 the hero's family component, owner at +4; r4 the spouse; r5 the
/// ceremony), before it.
struct MarriageEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MARRIAGE;
  uint32_t hero;
  int32_t player;
  uint32_t spouse;
};

/// The dog is told a trick (an expression's completion, a gift, an item, a script): `trick` is the
/// command record's hash. `cancel` refuses it. Seam: rex_CECDog_PerformCommand (r3 dog component,
/// owner at +4; r4 the record pair; r5 force), before it.
struct DogTrickEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DOG_TRICK;
  uint32_t dog;
  uint32_t trick;
  /// Whose dog: 1 or 2 by its master (the dog component's weak reference at +300, Dog.GetMaster),
  /// else by the players' dog records (refii's Refii.player_dog); 0 for a dog of neither.
  int32_t player;
};

/// A ranged weapon is about to fire (hero and AI alike): `target` the entity aimed at or 0, `aimed`
/// an aimed rather than a hip shot. `cancel` stops the shot. The game may still refuse it (no
/// ammunition). Seam: rex_CECFirearm_Fire (r3 firearm component, owner at +4; r4 target; r5 aimed).
struct RangedShotEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_RANGED_SHOT;
  /// The creature holding the weapon (the weapon's held-item component 83, its holder's weak
  /// reference at +12), or 0.
  uint32_t shooter;
  int32_t player;
  uint32_t target;
  bool aimed;
  /// The weapon's own item entity (the firearm component's owner).
  uint32_t weapon;
};

/// A status effect started or ended on an entity: `effect` 1 electrified (component 205),
/// 2 chaos (206), 3 burning (208), 4 caught in a vortex (209), 5 knocked down (every ragdoll fall:
/// a knockdown, a vortex throw, a kicked chicken, the co-op partner's replica), 6 mauled by the dog
/// (245), 7 unconscious (the hero downed, a script's SetAsUnconscious; ended by waking), 8 a hollow
/// man crumbled to bones (ended when it rises). After the fact. Seams: rex_lhEntity_AddComponent
/// (r3 entity; the new component's id from its own GetECType), rex_lhEntity_RemoveComponent (r3
/// entity, r4 id), rex_lhRagdoll_GoFloppy / _Recover (r3 entity), rex_CECCreature_SetUnconscious
/// (r3 creature component, r4 down), rex_HollowMan_SetCrumbled (r3 action, r4 crumbled).
struct StatusEffectEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_STATUS_EFFECT;
  uint32_t entity;
  int32_t player;
  int32_t effect;
  bool started;
  /// For a status started inside a spell's installer (StatusApplyingEvent's seams) or a vortex's
  /// capture (VortexCaughtEvent's): that cast's serial and its source (the caster). 0xFFFFFFFF and
  /// 0 otherwise (an end, a fall, a script's status).
  uint32_t serial;
  uint32_t source;
};

/// A player's target changed: `target` is the new one, 0 when it was cleared. After the fact.
/// Seams: rex_CECTargetingPlayer_SetTarget (r3 targeting component 77, owner at +4; r4 target) and
/// _ClearTarget (r3), the player targeting's vtable slots 19 and 18.
struct TargetChangedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_TARGET_CHANGED;
  uint32_t hero;
  int32_t player;
  uint32_t target;
};

/// A villager reacts to a deed, an expression or a gift it saw: `deed` the deed record's hash,
/// `kind` the message kind (0..4), `doer` who did it. `cancel` makes the villager ignore it (no
/// opinion change, no renown, morality or purity from it). Seam: rex_CECOpinionReaction_OnDeed
/// (r3 opinion component, owner at +4; r4 the deed message: +32 record, +52 kind, +72 doer weak ref).
struct OpinionDeedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_OPINION_DEED;
  uint32_t villager;
  uint32_t doer;
  int32_t doer_player;
  uint32_t deed;
  int32_t kind;
};

/// A villager's opinion axis is changed directly, not through a deed (0 love, 1 fear, 2 attractiveness,
/// 3 morality, 4 respect): `delta` writable, `cancel` leaves it. Seam:
/// rex_CECOpinionReaction_ModifyAxisValue (r3 component, owner at +4; r4 axis; f1 delta).
struct OpinionModifiedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_OPINION_MODIFIED;
  uint32_t villager;
  int32_t axis;
  float delta;
  /// Whose opinion set moves: 2 while the component holds player two's set (their opinion scope
  /// for this villager), else 1.
  int32_t player;
};

/// The world clock reached a new hour (`hour` 0..23) or a new day (`day`): natural time, a fast
/// forward, sleeping, travel time and a script's set all show here, a set one tick late. `time` is
/// the day fraction 0..1. Seam: rex_lhGameTimeManager_Update (r3 the time manager: +4 time,
/// +8 day count), after it.
struct TimeOfDayEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_TIME_OF_DAY;
  int32_t hour;
  int32_t day;
  float time;
  bool new_day;
};

/// An environment theme (the weather and the look of the sky) is blended to: `theme` its name,
/// `weight` and `seconds` writable. `cancel` skips the blend. Position-painted blending never comes
/// here. Seam: rex_lhEnvironmentThemeManager_BlendToTheme (r3 manager; r4 name; f1 weight; f2 seconds).
struct WeatherThemeEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_WEATHER_THEME;
  const char* theme;
  float weight;
  float seconds;
};

/// The bed's sleep menu is about to open (the time skip that follows is the shipped Lua's).
/// `cancel` keeps it closed. Seam: rex_SleepMenu_Show.
struct SleepMenuEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SLEEP_MENU;
  /// Always true: the event itself is the news.
  bool seen;
  /// The player who is sleeping: the user the game's use of the bed passed to its script
  /// (rex_CECOnActionUse_Use, r4; OnActionUseBed hands it to SleepFunction.Sleep); 0 when no
  /// player used it.
  int32_t player;
};

/// A travel started: a door, a level exit, a fast travel, a script. `full_load` is false for a
/// change inside the loaded level (a new entrance in the same region), which LevelLoaded never
/// reports. After the game decided. Seam: rex_CmdLevelChange_Start (r3 the command: +20 world,
/// +24 level, +28 entrance names, +36 travel type, +50 full load), after it.
struct TravelEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_TRAVEL;
  const char* world;
  const char* level;
  const char* entrance;
  int32_t type;
  bool full_load;
  /// The player who asked for it (both heroes travel): the one a level exit fired for (its user,
  /// the player its walk-in test tested, an online joiner's request) or the world map's owner for
  /// a fast travel; 0 for a script's travel, or one this machine did not see asked for.
  int32_t player;
};

/// The hero chose a fast-travel stop (the world map, a script): `level` and `stop`, `paid`.
/// `cancel` stays put (nothing is paid). Seam: rex_FastTravelManager_TravelToStop (r3 manager;
/// r4 level name; r5 stop, +4 its marker name; r6 paid), before it.
struct FastTravelEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_FAST_TRAVEL;
  const char* level;
  const char* stop;
  bool paid;
  /// The player who chose it: the world map's owner (rex_lhGuiWorldMap_FastTravel, the map's
  /// travel, around the call); 0 for a script's.
  int32_t player;
};

/// The game entered its menu mode (the world pauses behind it) or left it — real transitions only;
/// MenuScreen states each screen a menu changes to (0 pause, 2 spells, 14 abilities, 16 equipment,
/// 17 logbook, 18 options, 22 shop, 23 world map, 24 co-op, …; TU1 numbering on both builds).
/// Seams: rex_lhGuiManager_EnterGuiMode / _EnterGuiModeQuiet (when menu mode was off),
/// rex_lhGuiManager_ExitScreen (when it really leaves), rex_lhGuiManager_SetScreen (r4 the id).
/// `player` (all three) is whose menu it is: 2 on an online joiner, in local co-op while player
/// two owns the pause (the pause screen's +4, set by its vtable slot 8 before the screen and the
/// menu mode are entered) or their shop; 1 otherwise.
struct MenuOpenedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MENU_OPENED;
  /// Always true: the event itself is the news.
  bool seen;
  int32_t player;
};

struct MenuClosedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MENU_CLOSED;
  /// Always true: the event itself is the news.
  bool seen;
  int32_t player;
};

struct MenuScreenEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MENU_SCREEN;
  int32_t screen;
  int32_t player;
};

/// The hero walked into a different PAINTED weather region: `day_set` is the region's
/// environment day set (a GDB record hash; the day set picks the theme by time of day), 0 outside
/// every painted region; `previous` the one before. Read-only. A scripted blend is WeatherTheme.
/// Seam: rex_lhPaintMap_DaySetAt (r3 the paint map, v1 the hero's position; returns the day set
/// hash or -1), called once a frame by the painted-weight pass (sub_821ABFD8, its one caller) for
/// the hero in player one's slot (players+0x6C) — never once per split-screen view.
struct WeatherRegionEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_WEATHER_REGION;
  uint32_t day_set;
  uint32_t previous;
  /// The hero is inside a painted region (day_set is only meaningful then).
  bool inside;
  /// The player whose hero the pass tested (1 as shipped); `previous` is that player's own.
  int32_t player;
};

/// The player answered a bed's sleep menu: `choice` 0..3, `cancelled`, the `bed`. What each choice
/// does is the shipped SleepFunction.Sleep's (sleepfunctions.lua, reading Bed.GetSleepType = choice
/// + 1, 0 cancelled): 0 fast-forwards six hours, 1 twelve hours, 2 adds one day without moving the
/// clock, 3 adds seven days (its enum is named SLEEP_FORTY_EIGHT_HOURS). `choice` is writable and
/// `cancel` refuses the sleep; a length the menu does not offer is a Lua mod wrapping
/// SleepFunction.Sleep. Seam: rex_lhMenuBox_Close (r3 the
/// shared choice menu: +0 the selected index, +13 cancelled), while the sleep menu is the one open.
struct SleepChosenEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SLEEP_CHOSEN;
  uint32_t bed;
  int32_t choice;
  bool cancelled;
  /// The player who is sleeping, as SleepMenu stated it for this menu.
  int32_t player;
};

/// Slow Time changed the world's time scale: `scale` (1 is normal; writable before the game
/// applies it) and whether it is now `active` (scale < 1). Every creature but the caster slows —
/// the game slows the world and gives the caster (and the co-op partner) the inverse rate.
/// Seam: rex_SpellSlowTime_SetWorldScale (r3 the caster; f1 the scale; world +128 the scale).
struct SlowTimeEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SLOW_TIME;
  uint32_t caster;
  int32_t player;
  float scale;
  bool active;
};

/// A spell summoned a creature or a weapon: `kind` 1 a Raise Dead minion, 2 a Blades sword (an
/// entity of its own). The summon leaves the world through EntityRemoved. After the fact. Seams:
/// rex_SpellRaiseDead_BindSummoner (r3 summoner, r4 the summoned entity) and
/// rex_SpellDamoclesSword_Create (r3 the sword object, its entity's weak ref at +0; r5 the caster).
/// `serial` is the summoning spell's, as the game states it at each seam:
///   - the bind's r6: Raise Dead's update reads its spell's serial (`lwz r24,0x44(r3)` 0x82755804,
///     r3 = the spell) into `sub_82756168`'s r4, which binds with it (`mr r6,r24` 0x827567b8); the
///     bind hands r6 on as the serial wherever it reports the summon. The network receive's bind
///     carries the number the sender's message holds.
///   - the sword set's +0x10 (the create's r4): Blades' Start passes its spell's serial into the
///     set's init (`lwz r25,0x44(r31)` 0x825a21ec / `lwz r29,0x44(r31)` 0x825a297c, `mr r6`), which
///     stores it (`stw r27,0x10(r31)` 0x825a2f4c). Swords added to a live set by a re-cast keep the
///     set's — the spell they join.
struct SummonedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SUMMONED;
  uint32_t summoner;
  int32_t player;
  uint32_t summon;
  int32_t kind;
  /// The summoning spell's serial (a hit's spell_serial, spell_created's).
  uint32_t serial;
  /// The tag of the cast whose serial it carries (SpellLaunchEvent's), 0 none.
  int32_t tag;
};

/// A crime is recorded against someone: `criminal` committed crime `type` (the game's crime
/// enum: 2 murder, 3 assault, 6 trespass, 11 vandalism are among them) on `victim` (0 when none).
/// `cancel` drops it: no witness record, no bounty, no guards. The co-op partner's crimes are their
/// own. Seam: rex_CECCrime_Add (r3 the crime component, r4 the criminal, r5 the victim, r6 the
/// type, r7 a flag), which also records assault against hero one when a murder is reported.
struct CrimeEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CRIME;
  uint32_t criminal;
  int32_t player;
  uint32_t victim;
  int32_t victim_player;
  int32_t type;
};

/// An achievement unlocks (the game's ids 1..50; scripts and the stats code both unlock through
/// this). It is written for BOTH heroes. `source` is the entity whose stats asked. `cancel` keeps
/// it locked. Seam: rex_CECStats_UnlockAchievement (r3 the stats component, owner at +4; r4 the id).
struct AchievementEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ACHIEVEMENT;
  int32_t id;
  uint32_t source;
  int32_t player;
};

/// A player picks up a one-time collectable that is kept per player in co-op (a silver key):
/// `stays` is true when the entity stays in the world for the partner, who takes their own.
/// After the decision, before the pickup. Seam: rex_CECInventory_AddEntity (coop_world_hooks.cpp).
struct CollectableTakenEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_COLLECTABLE_TAKEN;
  int32_t player;
  uint32_t entity;
  uint32_t record;
  bool stays;
};

/// A co-op script asks the other player's consent (a bed's sleep or sex, or a mod's own question
/// from Refii.ask_partner): `kind` 1 sleep (`detail` the sleep type), 2 sex, 0 a mod's question;
/// `asker` the asking player. `answer` (writable) answers for the partner: -1 asks them (the
/// default), 0 refuses, 1 agrees. Seam: the consent ask (mod_lua_natives.cpp).
struct PartnerConsentEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PARTNER_CONSENT;
  uint32_t ticket;
  int32_t kind;
  int32_t asker;
  int32_t detail;
  int32_t answer;
};

// --- PROPERTY AND HOUSEHOLD (2026-10-20) ------------------------------------------------------
// Online, all of these fire on the host, where player two's household and property live and the
// economy is (household_persist.cpp); an online joiner's own actions are forwarded there and fire
// there. Each fires once per occurrence.

/// A hero buys a building (before the purchase). `price` (writable) is what it costs, condition
/// included. `cancel` keeps the building unbought and the gold where it is. Seam:
/// rex_CECPlayerProperties_Buy (r3 the buyer's properties comp69, r4 the building's entity).
struct PropertyBoughtEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PROPERTY_BOUGHT;
  uint32_t hero;
  int32_t player;
  uint32_t building;
  int32_t price;
};

/// A hero sells a building (before the sale). `price` (writable) is what they are paid; a shared
/// building's half then goes to the partner. `cancel` keeps it owned. Seam:
/// rex_CECPlayerProperties_Sell (r3 the seller's comp69, r4 the building's entity).
struct PropertySoldEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PROPERTY_SOLD;
  uint32_t hero;
  int32_t player;
  uint32_t building;
  int32_t price;
};

/// A hero's rent and shop profits were paid (after the payout): `gold` in all, for `drops` five-minute
/// drops; `away` when it is the catch-up for time spent out of the game (paid on the next screen).
/// Seam: rex_CECPlayerProperties_PayIncome (r3 comp69, r4 drops, r5 away; the total in r3 after).
struct RentPaidEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_RENT_PAID;
  uint32_t hero;
  int32_t player;
  int32_t gold;
  int32_t drops;
  bool away;
};

/// A building is rented out or its tenants evicted (before). `cancel` leaves it as it was. Seam:
/// rex_CECPlayerProperties_SetRentedOut (r3 comp69, r4 the building's entity, r5 rented).
struct PropertyRentedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PROPERTY_RENTED;
  uint32_t hero;
  int32_t player;
  uint32_t building;
  bool rented;
};

/// A building's condition changed (Fable 3's, 0..1): a rented home wore down at a rent drop, or it was
/// repaired. After the fact. `uid` is the building's UniqueID ("0x…"). Seam: property_hooks.cpp.
struct PropertyConditionEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PROPERTY_CONDITION;
  uint64_t uid;
  int32_t player;
  float condition;
  float previous;
  bool repaired;
};

/// A marriage ended in divorce (the spouse has left). After the fact. Seam:
/// rex_CECPlayerFamily_FinishDivorcing (r3 the hero's family comp136, r4 the spouse).
struct DivorceEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DIVORCE;
  uint32_t hero;
  int32_t player;
  uint32_t spouse;
};

/// A hero's spouse died or was removed: the household ends. After the fact. `uid` is the late
/// spouse's UniqueID. Seam: rex_CECPlayerFamily_Widow (r3 the family comp136, r4 &the spouse's UID).
struct WidowedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_WIDOWED;
  uint32_t hero;
  int32_t player;
  uint64_t uid;
};

/// A child is born to (or adopted by) a hero and their spouse. After the fact. Seam:
/// rex_CECPlayerFamily_AdoptWithSpouse (r3 the family comp136, r4 the spouse, r5 the child).
struct ChildBornEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CHILD_BORN;
  uint32_t hero;
  int32_t player;
  uint32_t spouse;
  uint32_t child;
};

/// A hero gives a gift (a ring among them) to the one they target (before). The item leaves their
/// inventory and comes back after fifteen seconds unless the villager keeps it (GiftAccepted).
/// `cancel` gives nothing. Seam: rex_lhGiftComponent_GiveGift (r3 the giver's comp33, r4 the item).
struct GiftGivenEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_GIFT_GIVEN;
  uint32_t hero;
  int32_t player;
  uint32_t target;
  uint32_t item;
};

/// A villager kept a hero's gift: it is theirs for good, and the gift's morality applies. After
/// the fact. Seam: rex_CECHeroGifts_RemovePermanently (r3 the giver's comp33, the item's record as
/// the Lua argument).
struct GiftAcceptedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_GIFT_ACCEPTED;
  uint32_t hero;
  int32_t player;
  uint32_t item;
};

/// A villager becomes engaged to a hero (or the engagement ends). `cancel` leaves it as it was.
/// Seam: rex_CECOpinionReaction_SetEngaged (r3 the villager's comp47, r4 engaged).
struct EngagedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ENGAGED;
  uint32_t villager;
  int32_t player;
  bool engaged;
};

/// A creature starts or stops following someone (a hero's follower, a fiancé, a partner for the
/// night). `cancel` on a start keeps it from following. Seams: rex_CECFollow_Start (r3 the
/// follower's comp94, r4 the leader) / rex_CECFollow_Stop (r3 comp94).
struct FollowEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_FOLLOW;
  uint32_t follower;
  uint32_t leader;
  int32_t player;
  bool following;
};

/// A piece of upgradeable furniture changes level (redecorating, with a furniture card). `level`
/// (writable) is the new level. `cancel` leaves it. Seam: rex_CECUpgradeableFurniture_SetLevel (r3
/// the piece's comp68, r4 the level).
struct FurnitureUpgradedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_FURNITURE_UPGRADED;
  uint32_t piece;
  int32_t player;
  int32_t level;
  int32_t previous;
};

/// Redecorating a building starts or ends (its furniture becomes usable for upgrades). Fires on the
/// machine of the player redecorating. Seam: rex_CECBuilding_SetRedecorating (r3 comp67, r4 on).
struct RedecoratingEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_REDECORATING;
  uint32_t building;
  int32_t player;
  bool on;
};

/// A hero sets a household's budget at its home (the upkeep it is given). `gold` (writable) is the
/// budget. `cancel` leaves it. Seam: rex_lhGuiRealEstateScreen_SetBudget (r3 the estate screen, r4
/// the gold).
struct HouseholdBudgetEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HOUSEHOLD_BUDGET;
  uint32_t hero;
  int32_t player;
  uint32_t building;
  int32_t gold;
};

/// A hero's tracked stat counts up (the game's ETrackedStatType: kills, sex, marriages, children, gold
/// spent and many more; achievements and titles are judged from them). `amount` (writable) is what
/// is added. `cancel` adds nothing. Seam: rex_CECStats_ModifyTrackedStat (r3 the hero's stats comp57,
/// r4 the stat, r5 the amount).
struct TrackedStatEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_TRACKED_STAT;
  uint32_t hero;
  int32_t player;
  int32_t stat;
  int32_t amount;
};

/// Fable 3's player-to-player partnership between the two heroes changed: `kind` 0 none, 1
/// business partners, 2 married (core/hero_partners.cpp). After the fact. Seam: the household tick.
struct PartnershipEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PARTNERSHIP;
  int32_t kind;
  int32_t previous;
};

/// Player two is back in the host's world and their household and property, kept with the world,
/// were restored onto their hero (household_persist.cpp). After the fact.
struct HouseholdRestoredEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HOUSEHOLD_RESTORED;
  uint32_t hero;
  int32_t player;
  int32_t spouses;
};

/// The dynamic weather in effect changed (Godot's dynamic sky decides it: the player's override,
/// else the level's rule — fixed, interior, the game's own theme, or a roll that drifts). One
/// weather per co-op session: the host decides it, an online joiner adopts the host's statement
/// for its level; each player's sky switch only changes whether it is drawn. Fires on both
/// machines: the host when it decides, the joiner when it adopts. After the fact. Seam: the guest
/// tick, from the host's "ModWeather:" pump action (Engine_DynamicWeatherChoice's change).
struct DynamicWeatherEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DYNAMIC_WEATHER;
  /// The level the weather is for, "<level>/<scenario>" lower-case, as the statement names it.
  const char* level;
  /// The weather: a preset's name (Clear, PartlyCloudy, Cloudy, Overcast, Rain, Storm, Snow, Fog,
  /// LightRain, LightSnow, Blizzard), "theme" (the game's own theme weather) or "interior".
  const char* weather;
  /// The weather before, the same way; empty for the first one of a session.
  const char* previous;
  /// How long the change blends over (0: at once).
  float seconds;
  /// An online joiner adopting the host's statement; false where this machine decided it.
  bool from_host;
};

/// A row of a mod's menu was chosen in Godot: a row of a menu a mod defines in ui/templates.json,
/// a row a mod patched into a shipped menu, or any row whose command is Opens:<menu> or
/// Mod:lua:<function>. `menu` is the menu the row is on (the mod menu's name, or the list's own:
/// frontend-root, pause-root, a screen's population script), `row` the row's name, `player` 1 or 2
/// (whose menu it is), `mod` the mod that stated the row or its menu ("" for a shipped row). The
/// menu is Godot's, so Godot scripts get it as it happens; native and Lua handlers get it on the
/// next guest tick, raised from the host's command queue (`menu_row`). The guest ticks on the
/// title and the front end too, so native handlers hear a front-end row; Lua handlers live in the
/// game VM, which exists only in a game. Not cancellable.
struct ModMenuRowEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MOD_MENU_ROW;
  const char* menu;
  const char* row;
  int32_t player;
  const char* mod;
  /// A ROW ACTION's trailing fields (ui/templates.json "rowActions", Engine_UiRowActions): the
  /// highlighted row's own command ("Inventory:use:1234", "Ability:learn:9", an expression id) and
  /// the button pressed ("X", "Y"); both "" for a plain row choice.
  const char* command;
  const char* button;
  /// The row's TYPED SUBJECT, read by Godot off the row's own command or a host list's row name
  /// (Refii.Mods.ModRowSubject), so a handler never parses a verb: `subject_kind` "item" (a use),
  /// "weapon" (an equip), "expression" or "will" ("" for none); `definition` the item's, weapon's or
  /// expression's record hash as "0x%08x" ("" for none); `spell` the will's type 0..7 (0 Lightning ..
  /// 7 Dead Rising), -1 for none. Trailing, so a mod built against the old struct reads the same.
  const char* subject_kind;
  const char* definition;
  int32_t spell;
};

/// A message box a mod raised (refii_mod_message_box, Refii.message_box, RefiiHost.message_box) was
/// answered. `id` is what the call returned (the payload's ids are 1 .. 2^23-1, Godot's 2^23 ..
/// 2^24-1), `button` 0 for A, 1 for B, -1 when another box replaced it or its player is played on
/// the other machine, `player` 1 or 2 (whose box it was). The box is Godot's, so Godot scripts get
/// it as it happens; native and Lua handlers on the next guest tick, from the host's command queue
/// (`box_answer`) — native on the front end too, Lua in a game. Not cancellable.
struct ModMessageBoxEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MOD_MESSAGE_BOX;
  uint32_t id;
  int32_t button;
  int32_t player;
};

/// A notice a mod posted (refii_mod_notice, Refii.notice, RefiiHost.notice) has gone. `id` is what
/// the call returned, `outcome` 0 once it was shown and its time ran out (or it was dismissed on the
/// front end), -1 when it never showed (its player is played on the other machine, or the queue
/// overflowed), `player` 1 or 2 (whose notice it was). The channel is Godot's, so Godot scripts get
/// it as it happens; native and Lua handlers on the next guest tick, from the host's command queue
/// (`notice_done`) — native on the front end too, Lua in a game. Not cancellable.
struct ModNoticeEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MOD_NOTICE;
  uint32_t id;
  int32_t outcome;
  int32_t player;
};

/// A menu box a mod raised (refii_mod_menu_box, Refii.menu_box, RefiiHost.menu_box) was answered.
/// `id` is what the call returned, `option` the chosen option's index in the list the mod gave
/// (paging is the box's own), -1 for B on a cancelable box, another box replacing it, or its player
/// played on the other machine; `player` 1 or 2 (whose box it was). Godot scripts get it as it
/// happens; native and Lua handlers on the next guest tick (`menu_box_answer`) — native on the
/// front end too, Lua in a game. Not cancellable.
struct ModMenuBoxEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MOD_MENU_BOX;
  uint32_t id;
  int32_t option;
  int32_t player;
};

/// A player is buying or giving back a level of a MOD ability (ui/abilities.json). The ENGINE runs
/// the purchase from what the file declares — the level is the player's SAVED value under
/// `levelKey`, the price the game's own table of the ability `costLike` names
/// (rex_CECStats_AbilityExperienceCost), split evenly across the declared disciplines' pools (the
/// first named takes the odd point), each share spent through the game's own Spend; an unlearn
/// (`"refund": "half"`) gives half the given-up level's price back into General through the game's
/// own Modify, as the shipped unlearn does. The book's A and Y post it for the player whose book it
/// is, and it runs on the guest tick of that player's machine. Raised BEFORE anything changes and
/// only for a purchase that can happen (affordable, below the maximum; a level to give up):
/// `cancel` refuses it — nothing spent, the level stands. `ability` is the declared name, `mod` the
/// mod that declared it, `refund` true for an unlearn, `previous` the level held, `level` the level
/// it will hold, `experience` the price to be spent (a buy) or what comes back (a refund), `player`
/// 1 or 2. A mod applies the level's effect by reading the level from the player store
/// (refii_mod_player_value(player, levelKey, 0)) where it is used; it needs no handler for that.
struct ModAbilityEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MOD_ABILITY;
  const char* ability;
  const char* mod;
  bool refund;
  int32_t previous;
  int32_t level;
  int32_t experience;
  int32_t player;
};

/// A player chose (or cleared) a pick on one of their spell slots on the spell selectors — the
/// quick wheel or the abilities blade — through a pick a mod states in ui/spellwheel.json (Spell
/// Weaving's partner). The engine keeps each player's pick per slot as a SAVED player value under
/// the pick's `store` key ({slot} the slot's number, 1..5), written after the handlers unless one
/// cancels. `pick` is the pick's name, `mod` the mod that stated it, `slot` 0..4, `spell` the will
/// chosen (0 Lightning .. 7 Dead Rising; -1 cleared) — writable: a handler may choose another, or
/// -1 — `previous` the will held before (-1 none), `player` 1 or 2 (whose selectors). Raised on the
/// guest tick of the machine whose player made the pick, from the host's command queue
/// (`spell_pick`); Godot scripts get it from the bus like any other event. `cancel` keeps the slot's
/// pick as it was.
/// THE GAME'S OWN PICK (the slot's spell) is raised too, with `pick` "spell" and `mod` "", by the
/// guest's slot writer whenever the game calls it as a choice — each step of the selectors'
/// carousel, the scripts' spell-table natives, the host's own assignment (mod_abilities.cpp
/// rex_CECSpellManager_SetSpellSlot) — before the slot is written: a changed `spell` is the one
/// stored and sent to the session, `cancel` leaves the slot as it was. The template's defaults, the
/// other machine's pick arriving and a save load are not picks and raise nothing.
struct SpellPickEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SPELL_PICK;
  const char* pick;
  const char* mod;
  int32_t slot;
  int32_t spell;
  int32_t previous;
  int32_t player;
};

/// A mod's message from THE OTHER MACHINE of an online session (refii_mod_send_to_partner,
/// Refii.send_to_partner, RefiiHost.send_to_partner there): `mod` the mod that sent it, `name` its
/// word, `payload` its text (at most REFII_MOD_PARTNER_PAYLOAD_MAX bytes; on the Lua and Godot
/// surfaces a quote, backslash or line break in it arrives as '_'), `from` the player that machine
/// plays (1 the host, 2 the joiner). Raised on the receiving machine's guest tick, once per message
/// and in the order sent — the route (core/event_relay.h) carries it — and never on the sender's.
/// Not a replica: this machine's own handling, so player-state writes in a handler count. Never
/// raised alone or in local co-op. Not cancellable.
struct PartnerMessageEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PARTNER_MESSAGE;
  const char* mod;
  const char* name;
  const char* payload;
  int32_t from;
};

/// A player hero's LOOK-AT flag is being written — the byte every lock camera script reads and
/// the one a held LT states. Seam: rex_CPlayer_SetLookAtButtonDown (r3 the player component 33,
/// its owner entity at +4 — `stw r4,4(r31)` in its constructor 0x823e0110 / disc 0x823beb20; r4
/// the value; `stb r29,0x20(r30)` 0x823e9f34 / disc 0x823c8964, after `lbz r10,0x20(r30); cmplw;
/// beq` returns unchanged on an equal value). Its callers are the camera's input event on LT
/// down (`sub_825ADD58`, `li r4,1` 0x825ae6e0), the hero's message handler on the release
/// (`sub_826E5B30` case 0x32, `li r4,0` 0x826e5fc0), and the API's own refii_mod_lock_target /
/// refii_mod_release_lock. `down` is WRITABLE: the value the game stores — a mod whose lock is
/// its own keeps the flag equal to it whoever asks. `cancel` leaves the flag as it was (the
/// writer never runs, so the camera mode is not re-picked either). Raised for player heroes only.
struct LookAtButtonEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_LOOK_AT_BUTTON;
  uint32_t hero;
  int32_t player;
  /// The value being written (writable).
  bool down;
  /// The player component (33) the flag lives on (native).
  uint32_t component;
};

/// A VORTEX is live: its component (CECVortex, 211, on the vortex entity) was initialised by the
/// vortex spell's post-init, inside the spell factory and BEFORE SpellCreated reports the spell
/// object — which marks itself finished at once, so no registry lookup ever returns a vortex.
/// Seam: rex_VortexComponent_Init (r3 the component, owner entity at +4 from its constructor
/// `stw r4,4(r31)` 0x82757720; r4 the caster entity, kept as a weak reference at +0x160/+0x164;
/// r5 the target; r6 the cast's serial -> +0x18 `stw r25,0x18(r31)` 0x827588f4; r8 the level ->
/// +0x14; the call site `sub_828B20D0` 0x828b23c8..0x828b2438 reads them from the spell's +0x40
/// and +0x44), after it. `x, y, z` is the vortex entity's position then. Online the cast runs on
/// both machines (refii_mod_event_replica tells the copy). `lifts` is writable.
///
/// WHETHER THE VORTEX LIFTS CREATURES. Every half second its update asks `sub_8275EB10(entity,
/// level, &kind, ...)` (rex_VortexComponent_Classify; TU1 0x8275aff4, disc `sub_827364F0`
/// 0x827329d4) how it takes each creature in reach: 1 lift, 2 one it cannot lift — the function's
/// own `li r10,2; stw r10,0(r22)` for a hero (0x8275ec00..08) and `li r11,2; stw r11,0(r22)` for a
/// creature whose CECCreature (component 20) +0x3C lacks 0x40 (0x8275ed38..50). The update then
/// shudders a 2 (`lwz r11,var_18C; cmpwi cr6,r11,2; bne` 0x8275b004..0c: one hit per cast per
/// creature, the cast's serial kept in the creature's CECVortexShudderHit ring, `sub_825FA078`
/// 0x8275b36c or `sub_825FA488` 0x8275b29c, each carrying `lwz r14,0x18(r22)` as the hit's serial)
/// and queues anything else for the capture (`bl sub_82B49750` 0x8275b374 -> the held test
/// 0x8275b914, the append 0x8275b964, rex_VortexComponent_Capture 0x8275b9cc). `lifts` false
/// answers 2 for every creature (an entity with component 20) the function answered 1 while this
/// vortex's update runs, so the vortex lifts none and shudders each once; a prop it lifts as before.
struct VortexStartedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_VORTEX_STARTED;
  /// The live component: refii_mod_spell_parts(serial) finds it again while it lives (native).
  uint32_t vortex;
  /// The vortex entity (the component's owner), the thing in the world the look rides.
  uint32_t entity;
  uint32_t caster;
  int32_t caster_player;
  uint32_t target;
  uint32_t serial;
  int32_t level;
  float x;
  float y;
  float z;
  /// The cast's tag (SpellLaunchEvent's), 0 none.
  int32_t tag;
  /// The damage of one of the vortex's hits on a creature it holds before that creature's own
  /// factor: component +0x38, Init's f3 (`fmr f31,f3` 0x8275883c, `stfs f31,0x38(r31)`
  /// 0x82758ebc), which the held hit multiplies by the creature's DamageFactor (`lfs f0,0x38(r25)`
  /// 0x8275d92c, `fmuls f30,f13,f0` 0x8275d938) and the lifting shudder by +0x30 and the factors
  /// (0x8275b304..20).
  float damage;
  /// Whether the vortex lifts creatures (writable; true as the game has it). False: it lifts none
  /// and shudders each creature it reaches once per cast instead (above).
  bool lifts;
};

/// The vortex is ending: its component's Shutdown, BEFORE it runs, with the twister's last point.
/// Seam: rex_VortexComponent_Shutdown (r3 the component; +0x140 is the point the update hands
/// every ground effect — `li r28,0x140; lvx128 v1,r31,r28` 0x82759210..38 — and writes back as the
/// twister moves, `stvx128 v127,r29,r25` 0x82759bdc, then places the entity there through its
/// placement, 0x82759be4..f4; Shutdown stores nothing to +4/+0x18/+0x140). Read-only.
struct VortexEndedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_VORTEX_ENDED;
  uint32_t vortex;
  uint32_t entity;
  uint32_t caster;
  int32_t caster_player;
  uint32_t serial;
  /// The twister's point (component +0x140).
  float x;
  float y;
  float z;
};

/// A targeted FIREBALL struck something: its Impact ran (the guard `lbz r11,0x48(r31); bne`
/// 0x82744234 passed — a fireball already detonated returns at once), after it. Seam:
/// rex_TargetedFireball_Impact (r3 the spell object, r4 what it struck — an entity, 0 for ground,
/// wall or water — v1 the impact position, v2 the impact normal, f1 a range, r6 the reached-target
/// flag). `detonated` is the exploded byte (spell+0x48) going 0 -> 1 inside the call: the blast
/// (`sub_827478D8`, every victim's hit delivered inside the call) and then `stb r24,0x48(r31)`
/// 0x827446e4 — a struck creature the fireball passes (no blast) leaves it clear and the fireball
/// flies on. `hits` counts the hits the receiver took inside the call carrying this cast's serial
/// (hit+0x44): 0 on a detonation means the blast found nothing alive. Online the cast runs on both
/// machines; the hits are decided where refii_mod_world_here holds. Read-only.
struct FireballImpactEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_FIREBALL_IMPACT;
  /// The spell object (native).
  uint32_t spell;
  uint32_t serial;
  int32_t level;
  uint32_t caster;
  int32_t caster_player;
  uint32_t struck;
  int32_t struck_player;
  /// The impact position.
  float x;
  float y;
  float z;
  /// The impact normal.
  float nx;
  float ny;
  float nz;
  bool detonated;
  int32_t hits;
  /// The cast's tag (SpellLaunchEvent's), 0 none.
  int32_t tag;
};

/// THE GAME'S PAUSE STATE flipped: a GameFlow mode command of the pause family became the mode in
/// force, or stopped being it. The game's own test is rex_lhGameFlow_IsGuiActive (`sub_821ECA30`:
/// the top of the mode stack at `*(*(*(game+0x80)+4)+4)`, its vtable slot 1 == 1), which the
/// gameplay loop asks every frame (`bl sub_821ECA30` 0x8227711c in `sub_82276D10`). The state is
/// SET by the mode command's apply, vtable slot +20 of `off_820AEC7C` = `sub_823D4D00` (disc
/// `sub_8245C2E0`, same 276 instructions: `bl sub_823D48E8` — both players' input categories —
/// then `li r4,1; bl sub_823D4A50` 0x823d4d14..1c), run by GameFlow_QueueCommand's push
/// (0x82318718: `(*cmd->vtable+20)(cmd)` as its last call) and again when a command above it
/// is removed (`sub_82318800` 0x82318800: re-applies the new top); and CLEARED by its remove,
/// slot +32 = `sub_823D5150` (disc `sub_8245C730`: `li r4,0; bl sub_823D4A50`), run by the
/// removal and by a push of another command over it. Mode 1 (slot 1 `li r3,1`, `sub_82EA63A8`)
/// is the GUI/pause command built by `sub_823D4C48` (input categories {12,10,9,11,1} on both
/// players): queued by rex_lhGuiManager_EnterGuiMode (0x8236ed3c, the pause menu and every
/// screen entered from play), _EnterGuiModeQuiet (0x82370100), the partner's relayed pause
/// (`sub_8232B2B0` "MULTI_PLAYER_OTHER_PLAYER_PAUSED_GAME", 0x8232b33c) and the camera's special
/// modes (`sub_825AD7E8`). Mode 2 (`off_820AECA0`, slot 1 `li r3,2`, the same apply/remove through
/// thunks `sub_823D53E0` / `sub_823D54F0`) is the level change (World_RequestLevelChange
/// 0x82350ca0..cc8, `sub_82313328`). Seams: rex_lhGameFlowGuiMode_Apply / _Remove (r3 the
/// command; its id by vtable slot 1), after each. Read-only.
struct PausedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PAUSED;
  /// True after the apply (the mode is in force), false after the remove.
  bool paused;
  /// The command's id: 1 the pause / menu mode, 2 a level change.
  int32_t mode;
  /// For mode 1 with a menu screen up: whose menu (MenuOwnerNow: 2 for an online joiner or local
  /// player two's pause book, else 1); 0 for a pause with no screen (the partner's relayed one, a
  /// camera mode) and for mode 2.
  int32_t player;
  /// The GUI manager's screen id then (TU1 numbering; 31 none, 0 the pause root, 29 co-op).
  int32_t screen;
};

/// THE FRONT END STARTS A GAME: the title's New Game, Load, Continue or a co-op join state the
/// target through rex_lhFrontEnd_SetStartGameTarget (`sub_82379838`, disc `sub_8237A2D8`, same 52
/// instructions: r3 the front-end object, r4 the save name LHString (empty for a new game —
/// `sub_822651A0(&g_PendingLoadSaveName, a2)`), r5 the start level LHString
/// (`sub_822651A0(&g_StartLevelName, a3)`); it then flags the game-state change
/// `*(*(0x83496AC0+12)+92) = 1` and creates the in-game state `sub_82319928`). Callers: the root
/// screen's action handler 0x826df880, GUI_FrontendRoot_MenuActionDispatch 0x826e0498, the
/// load-game page 0x826e0f34, and the host's own New Game pump action. Raised BEFORE the call,
/// read-only; on the title there is no game VM, so Lua hears it in mod/frontend.lua.
struct StartGameEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_START_GAME;
  /// The save's name ("" for a new game).
  const char* save;
  /// The level the game starts in.
  const char* level;
  bool new_game;
  /// The signed-in pad's player: 1.
  int32_t player;
  /// The host action the start was dispatched from — the guest-tick pump's name for it
  /// (`LoadSave:N`, `MenuEvent:…`, the root's bare row name) — or "" for a start the pump did
  /// not run. A refusing handler posts it again to make the same start once its question is
  /// answered (the host's save gate).
  const char* commit;
};

/// A CUTSCENE took a hero's camera and controls away from free play, or gave them back: one of the
/// three statements below flipped (raised per kind, on each edge, from the guest tick before the
/// Tick event; refii_mod_in_cutscene answers the same three live at any moment). All three are the
/// game's own and are read where its setter writes them — the same field offsets on both builds:
///   FULL (1): the top game-flow state's type is 7. `GUI.PlayFullCutscene` (`sub_82305FE8`) builds
///     that state (vtable `off_820AED30`, slot 1 `li r3,7`) and queues it (GameFlow_QueueCommand);
///     the game's own query `GUI.IsFullCutscenePlaying` (`sub_823060E0`, disc `sub_82907D08`: list
///     `*(*(*(*(*(0x83496AB8)+12)+128)+4)+4)`, node `*(list+4)`, `(*state->vtable+4)(state) == 7`,
///     `addi r9,r3,-7; cntlzw; extrwi` 0x82306130..38) is asked, after the empty-list test it traps on
///     (`cmplw r11,r10; bne; twui` 0x8230610C..14). Every player's: `player` 0.
///   INTERACTIVE (2): a foreground interactive cutscene holds the hero. The player component's
///     (33) +0x3B, written only by `Player.SetForegroundCutsceneInEffect` (`sub_823E72B8` / disc
///     `sub_823C5CE8`: `stb r4,0x3B(r3)` 0x823e72dc / 0x823c5d0c), which the shipped
///     InteractiveCutsceneGroupMind calls true when a non-background scene starts (with the
///     vignette, the cutscene microphone and EXPRESSION_CHOICES_ONLY) and false when the last one ends.
///   LOOK_AT (4): the game has given that hero's held LT a camera. The player component's +0xC8,
///     written by `Player.SetOverrideCameraMode(comp, mode, scope)` (`sub_823E31B8` / disc
///     `sub_823C1B80`: scope 2 LOOK_AT `stw r4,0xC8(r3)` 0x823e31cc, scope 0 ALWAYS both +0xC4 and
///     +0xC8 0x823e31dc..e0) — the native under CameraManager.SetCameraOverride — and read back by
///     `Player.GetOverrideCameraMode` (`sub_823E31E8`), which itself returns +0xC8 instead of +0xC4
///     while the look-at flag (+0x20) is down (`lbz r11,0x20(r3)` 0x823e31fc .. `lwz r3,0xC8(r3)` 0x823e3244). Set by a conversation's talking
///     camera (InteractiveCutsceneGroupMind:SetTalkingLookAtCamera: CAMERA_MODE_GAME on the speaker
///     with TargetLockInterestingPoint), a quest's QuestThreadBase:SetLookAtCamera and a scene's
///     own camera records; 0 is CAMERA_MODE_DEFAULT, none. While it holds, a held LT frames what the
///     game chose (CameraManager.GetActiveOverriddenData returns the LookAt data while
///     GetSafeLookAtButtonDown) — what LT does is the game's, not a lock's.
/// `player` is 1 or 2 (0 for FULL); both players are principals. Read-only.
struct CutsceneEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CUTSCENE;
  /// 1 or 2; 0 for a full cutscene (every player's).
  int32_t player;
  /// True on the rising edge, false on the falling one.
  bool active;
  /// The statement that flipped: REFII_CUTSCENE_FULL, _INTERACTIVE or _LOOK_AT.
  int32_t kind;
  /// Every statement holding for the player after this edge (for player 0, the full cutscene's).
  int32_t kinds;
  /// The player's hero (0 for player 0, or once the hero is gone).
  uint32_t hero;
};

/// An entity's health CHANGED through the health component's own Modify — a hit's damage, a
/// potion, a heal, refii_mod_modify_health. After the fact, read-only. Seam:
/// rex_CECHealth_Modify (TU1 `sub_824CBC68`, disc `sub_8268BD90`): r3 the health component (36,
/// owner at +4), f1 the delta, r5 the source entity (tested `lbz +0x90 & 0x40` at 0x824cbca0);
/// +0x14 current takes the delta clamped to 0..+0x1C max (0x824cbeb8..0x824cbf08) and the body
/// runs on only when +0x14 moved (`fcmpu` 0x824cbf14). Damage the game refuses moves nothing and
/// raises nothing: an in-world source `sub_824CDB38` rejects (0x824cbcc8), or a protected victim
/// (health +0x131, the hero-protection bytes 0x83496BF2 / BF3 / BFC, component 76's
/// `sub_82582160`; 0x824cbce8..0x824cbeac). `delta` is the change that landed, after the clamp, so
/// a heal at full health is not reported.
struct HealthChangedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HEALTH_CHANGED;
  uint32_t entity;
  int32_t player;
  /// The change that landed: positive a heal, negative damage.
  float delta;
  /// Health after it, and the maximum.
  float health;
  float max;
  /// The entity the change is attributed to (an attacker, a healer), 0 for none.
  uint32_t source;
};

/// A spell's status is about to be put on an entity, through the installer each spell calls —
/// `seconds` writable (> 0), `cancel` puts nothing on (and, for chaos, sends nothing to the co-op
/// partner). `effect` is the StatusEffect numbering: 1 electrified, 2 chaos, 3 burning. Seams, the
/// same registers on both builds (each prologue quoted at its address):
///   - rex_SpellLightning_Electrify (TU1 `sub_82742FA8`, disc `sub_827180A8`): r3 source, r4
///     victim, r5 serial, r6 level, f1 duration (`mr r29,r3; mr r28,r5; mr r27,r6; fmr f31,f1`
///     0x82742fbc..d4, handed to `sub_8294CE50`, which stores serial +0x20, level +0x24, duration
///     +0x28: 0x8294cef4 / 0x8294cef8 / 0x8294ce98). A victim already electrified is re-armed.
///   - rex_SpellChaos_ApplyToVictim (TU1 `sub_8274A230`, disc `sub_8271F330`): r3 source, r4
///     victim, r5 serial, f1 seconds, r7 is-creature, r8 replica (`mr r26,r5; mr r24,r7; mr r28,r8;
///     fmr f29,f1` 0x8274a25c..6c). Only the creature branch (r7) installs the status; the network
///     receive calls it with `li r8,1` (0x823991c0), and only a call with r8 = 0 sends it on
///     (`cmpwi r9,1` on the session 0x8274a490), carrying the seconds — so the event fires for r7
///     set and r8 clear, and a written `seconds` reaches the partner.
///   - rex_SpellFireball_Ignite (TU1 `sub_82747260`, disc `sub_8271C360`): r3 victim, r4 source,
///     r5 serial, r6 level, f3 burn time (`mr r27,r4; mr r26,r5; mr r25,r6; fmr f30,f3`
///     0x82747284..98; f30 is the burn time handed on at 0x82747418).
/// Before the installer; the installer may still refuse (an entity not in the world, the world's
/// spell-count gate). A cast's status carries its serial; refii_mod_apply_status passes -1.
struct StatusApplyingEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_STATUS_APPLYING;
  uint32_t entity;
  int32_t player;
  int32_t effect;
  /// The cast's serial (a hit's spell_serial), 0xFFFFFFFF for none.
  uint32_t serial;
  /// Who put it on (the caster), 0 for none.
  uint32_t source;
  /// The spell level the component records (0..4); chaos states none (-1).
  int32_t level;
  /// How long it lasts. Writable.
  float seconds;
  /// The tag of the cast whose serial it carries (SpellLaunchEvent's), 0 none.
  int32_t tag;
};

/// A save package was written — or the write failed. Seams: rex_lhSaveManager_WritePackage (the
/// full write: main save, hero save, chapter, morphs; TU1 `sub_824457E8`, disc `sub_824E8DE0`) and
/// rex_lhSaveManager_WriteHeroPackage (the hero save alone; `sub_82445A28` / `sub_824E9020`), both
/// r4 the package's name (an LHString*), r5 the PLAYER NUMBER whose hero they serialise (`lwz
/// r5,0x24(r27)` 0x82445514), returning their result in r3 after the package is closed (`bl
/// sub_82449BD8; mr r3,r31` 0x82445AA0). After the call, read-only.
struct SaveWrittenEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SAVE_WRITTEN;
  int32_t player;
  /// The package's name (its folder, "Hero000"), "" when the game named none.
  const char* save;
  /// The writer's own result: false for a failed write.
  bool ok;
};

/// A player's target LOCK ENDED: the game dropped player mode 4 TARGET_LOCK. Seam: the mode drop,
/// rex_CPlayer_ClearPlayerMode (TU1 `sub_82389CD0`, disc `sub_8238F4A0`): r3 the player's mode
/// handle (comp33+0x14, what every caller loads: `lwz r3,0x14(r31)`), r4 the mode, handed to the
/// player manager's remove (`lwz r4,0x24(r30); mr r5,r29; bl sub_823D8820` 0x82389d44..50). The
/// lock's ends call it with `li r4,4` — the targeting update when the force byte was released
/// (`+112 && !+20`, 0x82182290..0x821822a4), its range check when the target is beyond reach or
/// gone (`sub_82844378` 0x82844690, 0x8284493c), `sub_82976588` (0x82976d68) — and `sub_823B93F8`
/// and the network receive with the mode in a register. Raised after the call for r4 = 4 when the
/// game's own HasPlayerMode (`sub_821E8B90(handle, 4)`, disc `sub_821E8870`) answered yes before it
/// and no after: a real drop, never a drop of a mode not held. Read-only; both players are
/// principals; on the co-op machine not deciding the hero the run is flagged a replica.
struct LockLostEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_LOCK_LOST;
  int32_t player;
  uint32_t hero;
  /// The targeting component's own GetTarget (slot 17) after the drop, 0 for none.
  uint32_t target;
};

/// The keyboard's ATTACK MODE changed for a player: which pad button the attack key presses
/// (REFII_ATTACK_MODE_MELEE X, _RANGED Y, _MAGIC B), set by the mode keys (Fable III's 1, 2, 3). Host
/// state, no seam: Godot states the mode with the command `attack_mode P MODE` on each change (and
/// once when the guest comes up) and the next guest tick raises this where it differs from the last
/// statement. On the machine whose keyboard plays the player; refii_mod_attack_mode reads it.
/// Read-only.
struct AttackModeChangedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ATTACK_MODE_CHANGED;
  int32_t player;
  /// REFII_ATTACK_MODE_*; 0 when no keyboard plays the player here any more.
  int32_t mode;
};

/// One unit LEFT an inventory: the tail every removal body runs after it took the unit,
/// `sub_824F3FC0` (rex_CECInventory_RefreshRecordRefs; disc `sub_8261DCC8`), r3 the inventory
/// component (13; owner at +4), r4 the definition (item +4). Its callers: RemoveItem's body
/// `sub_824F12A0` (one unit: `--item+12` or `sub_82764E70(item, instance)`, then `bl sub_824F3FC0`
/// 0x824f13a0), the by-category loop `sub_824F1910` (0x824f1a20) and `sub_824F1650` (0x824f18ac),
/// each once per unit. After the call, read-only: the unit is gone.
struct ItemRemovedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ITEM_REMOVED;
  uint32_t entity;
  int32_t player;
  uint32_t inventory;
  uint32_t definition;
  /// Units this removal took: 1 (each removal body calls the tail once per unit).
  int32_t count;
  /// How many of `definition` the inventory still holds (refii_mod_item_count).
  int32_t remaining;
};

/// A player's EXPRESSION WHEEL opened, closed, or moved its highlight. Seams: the open
/// rex_lhGuiManager_OpenExpressionWheel (r3 the quick menu, r4 the player record; the wheel's
/// player is `*(r4 + 36)`, handed to `sub_826A9A60` after `stb 1` to the menu's +35) — after it,
/// when +35 went 0 -> 1; the close rex_lhGuiManager_CloseExpressionWheel (r3 the menu; +35 cleared,
/// `sub_826A9E58`) — before it, while +35 is set, for the wheel's player (+24). The highlight is
/// polled each guest tick while the wheel is up (refii_mod_selected_expression, the HUD's own
/// reader): one event with `open` true each time it names a different expression. Read-only.
struct ExpressionWheelEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_EXPRESSION_WHEEL;
  int32_t player;
  bool open;
  /// The expression record highlighted (refii_mod_selected_expression), 0 for none or closed.
  uint32_t highlighted;
};

/// A SPELL ENDED: the spell registry's update took it out of the registry. Seam: the registry's
/// update, rex_CSpellRegistry_Update (TU1 `sub_822A7F50`, disc `sub_822A7D10`), which for each
/// spell in its list (r31 = the spell, `lwz r31,8(r29)` 0x822a7fc4) runs its Update (vtable slot 2)
/// unless the spell is already done (`lbz r11,0x48(r31)`, `bne` 0x822a7fcc..d4), and for a spell
/// done before or after it (+0x48 again, 0x822a7fec) runs its Stop (slot 3, 0x822a8008..18) and
/// erases it from the list (`bl sub_82848008` 0x822a8028) — the only place the game removes a live
/// spell. The spell's own fields are read there, before its Stop (midasm hooks RefiiSpellRegistryUpdating
/// at 0x822a7fd8 and RefiiSpellRegistryEnding at 0x822a8008, both r31; disc 0x822a7d98 /
/// 0x822a7dc8): serial +0x44, type +0x28, level +0x40, caster the weak reference at +0x18 (the
/// base spell's constructor, `sub_828E5020`). Raised after the update returns. `reason`:
///   REFII_SPELL_END_FINISHED  the spell's own Update this pass marked it done (its time ran out —
///                             the base Update's `+0x50 >= +0x54` then `stb 1,0x48(r3)`, 0x828e5438..44 — or its own logic)
///   REFII_SPELL_END_STOPPED   it was marked done from outside before this pass (the registry's
///                             stop-a-caster's-spells `sub_824B20D0`, a re-cast replacing it)
/// A level unload that frees the registry raises nothing (level_unloading precedes it). Read-only;
/// on the co-op machine not deciding the caster the game's copy is flagged a replica.
#define REFII_SPELL_END_FINISHED 0
#define REFII_SPELL_END_STOPPED 1
struct SpellEndedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SPELL_ENDED;
  /// The registry's serial (spell_created's, a hit's spell_serial).
  uint32_t serial;
  /// The spell type (0 Lightning .. 7 Dead Rising).
  int32_t type;
  uint32_t caster;
  int32_t caster_player;
  /// The tag of the cast whose serial it carries (SpellLaunchEvent's), 0 none.
  int32_t tag;
  /// REFII_SPELL_END_*.
  int32_t reason;
  /// The spell's level (0..4).
  int32_t level;
};

/// A hero's MAGIC CHARGE ENDED: the charge action the magic charge ability pushed (the quick charge,
/// vtable TU1 0x820D2FB0 / disc 0x820CF820, or the full charge, 0x820D30A0 / 0x820CF910 — the only
/// two the charge ability's Start builds, `sub_8284B900` 0x8298a39c / `sub_829CED30`) ended through
/// the action's end, rex_CAction_End (TU1 `sub_822861A8`, disc `sub_82285D10`): r3 the action, run
/// once per started action (`lbz r11,0x48(r28)` 0x822861b8 gates its OnEnd, `stb 1,0x49(r28)`
/// 0x82286490 marks it ended). `released`: the charge ended inside the magic RELEASE ability's Start
/// (rex_CAbilityActionMagicCast_Start, TU1 `sub_8298A870`, disc `sub_829B9E30`, request type 19, r4
/// the entity), which builds the cast action and pushes it — the manager's push (vtable slot 16,
/// `sub_829619A0` -> `sub_822A4888`) ends the charge before the cast starts. False for any other
/// end: a hit's reaction, a roll, the charge refused. The cast that follows a release raises
/// spell_launch / spell_launched as usual. Raised only on the machine that plays the hero (the other
/// machine's clone ends through the network's own push). Read-only.
struct SpellChargeEndedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SPELL_CHARGE_ENDED;
  int32_t player;
  uint32_t caster;
  /// The magic button was released into a cast (the release ability took the charge over).
  bool released;
};

/// A STRIKE ENDED: the strike action rex_CAbilityActionMelee_CommitSwing committed (MeleeSwingEvent's
/// `action`) ended through rex_CAction_End (r3 the action; once per started action, as
/// SpellChargeEndedEvent states). `hits` counts the hit_landed deliveries whose attacker was the
/// striker while that strike was its current action — the machine's own hits and, online, the
/// copies relayed from the machine that decides hits (a copy arriving after the end is not counted).
/// 0 is a whiff. Read-only; relayed like melee_swing from the machine that plays the striker.
struct MeleeSwingEndedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MELEE_SWING_ENDED;
  uint32_t entity;
  int32_t player;
  /// The strike action (0 in a relayed copy).
  uint32_t action;
  /// The action's EScriptableAction, -1 unknown.
  int32_t action_type;
  int32_t hits;
  /// melee_swing's strike and flourish for this strike.
  int32_t strike;
  bool flourish;
};

/// MOVEMENT MAY INTERRUPT an entity's current action: the game's own test answered yes —
/// rex_CAction_MovementInterruptible (TU1 `sub_822BDEA0`, disc `sub_822BDBC0`; r3 the action
/// handle, its owner's weak reference first): `+0x4C` clear and `+0x4E` set on the action
/// (`lbz +76` / `lbz +78`), +0x4E being what an ALLOW_MOVEMENT_INTERRUPT anim event sets (a cast's
/// held until its PERFORM: `stb r26,0x4E(r29)` 0x82737e24 in `sub_82737C38`). The player's control
/// update asks it twice a frame (`sub_826BB078`): while it answers no, a moved stick is withheld
/// from the body (0x826bb690); when it answers yes and the stick moved, the action is stopped
/// through the manager's slot 18 (0x826bb710..30). `cancel` answers no: the action holds — the
/// body stays in it and the stick does not cancel it — for as long as a handler cancels. Asked
/// every frame of the window, so a handler is cheap or keyed.
struct ActionMoveInterruptEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ACTION_MOVE_INTERRUPT;
  uint32_t entity;
  int32_t player;
  /// The action (sub_82298738 resolves the handle, as the test itself does).
  uint32_t action;
  /// The action's EScriptableAction, -1 unknown.
  int32_t action_type;
};

/// The CAMERA the game has active for a player CHANGED: the camera manager's own pick. Every
/// player's manager binds the one `CameraManager` table and runs `CameraManager:FrameUpdate(player)`
/// (scripts/camera/cameramanager.lua), which walks `CameraSetList` {debug, override, multiplayer,
/// normal}, takes the first valid set's best camera and, when it differs from `CurrentCamera`,
/// calls `self.ActivateCamera(camera)` — `rex_lhCameraManager_ActivateCamera` (TU1 `sub_82481760`,
/// disc `sub_82422FA0`), whose argument is the camera's Lua table (a LuaObject: it builds the
/// wrapper `sub_825AD080`, which calls the table's own `Create`) and names no mode — then stores
/// `self.CurrentCamera = camera`. The mode is the key the game's override set lists that camera
/// under (`CameraModes = {[ECameraMode.CAMERA_FIRST_PERSON] = FirstPersonCamera, ...}`), so it is
/// stated from the game VM: the core camera chunk (Godot/src/Titles/Fable2/host/CoopHeroLua.cs),
/// after each `CameraManager:FrameUpdate(hero)`, reads the hero's `CurrentCamera` and states its
/// mode when it differs from the last. On the machine that runs that player's camera (each its own
/// online; both on one machine in local co-op). Read-only.
struct CameraChangedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CAMERA_CHANGED;
  /// 1 or 2.
  int32_t player;
  /// The ECameraMode now active (REFII_CAMERA_*): 2 GAME (the third-person SACCamera), 8 FIRST_PERSON
  /// ...; -1 for a camera the override set does not list.
  int32_t mode;
  /// The mode before, -1 for none stated yet.
  int32_t previous;
  /// The player's hero.
  uint32_t hero;
};

/// A vortex CAUGHT an entity (`caught` true) or LET ONE GO (false), whether or not it ever hit it.
/// The capture is the update's one call for every entity it takes, creatures and props alike: its
/// candidates are the creature query's lifts and the prop query's (`bl sub_82760500` 0x8275b7f0),
/// each capped by its kind (`bctrl` 0x8275b8a4, `cmpwi cr6,r29,2` creature against `lwz
/// r11,0x24(r22)` 0x8275b8ac..c4, `cmpwi cr6,r29,4` prop against the world's cap 0x8275b8b4).
/// Seams, both after the call:
///   - capture: rex_VortexComponent_Capture (TU1 `sub_8275C578` / disc `sub_82733F58`; r3 the
///     component, r4 the victim, r5 0). Its one caller is the vortex's own update (`sub_8275ABE8`
///     0x8275b9cc, disc `sub_827325C8`), after `sub_82759298` found the victim not yet held
///     (0x8275b914) and `sub_8236AC38(component+0x1B0, weak ref)` appended it to the held list
///     (0x8275b964). Capture adds CECInVortex (`addi r4,r11,"CECInVortex"; mr r3,r27; bl
///     sub_8238CD68` 0x8275c6b8..c8, which is rex_lhEntity_AddComponent by name, 0x8238cdac) and
///     hands it the caster (`addi r3,r23,0x160; bl sub_82173E38` 0x8275c92c, stored at +0x14
///     0x8275c958), so the StatusEffect kind 4 it raises carries this cast's serial and the caster
///     as `source`.
///   - release: rex_VortexComponent_Release (TU1 `sub_8275CB28` / disc `sub_82734508`; r3 the
///     component, r4 the victim, 0 for a reference already dead). It removes CECInVortex
///     (0x8275ccb4) and its three callers are every way out: the update dropping a victim before
///     erasing it from the list (0x8275be18), the component's own per-victim release (`sub_8275CA30`
///     0x8275ca78, vtable 0x82125590) and Shutdown releasing every held victim (0x8275904c).
/// `serial` is the component's +0x18 (Init `stw r25,0x18(r31)` 0x827588f4), the caster its weak
/// reference at +0x160/+0x164. Online the cast runs on both machines (refii_mod_event_replica tells
/// the copy, as for VortexStarted). Read-only.
struct VortexCaughtEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_VORTEX_CAUGHT;
  /// The cast's number (the component's +0x18), as its vortex_started and hits carry it.
  uint32_t serial;
  /// The cast's tag (SpellLaunchEvent's), 0 none.
  int32_t tag;
  /// The live CECVortex component (native) and its entity.
  uint32_t vortex;
  uint32_t entity;
  /// The creature caught or let go.
  uint32_t victim;
  /// The victim's player, 1 or 2 for a hero, else 0.
  int32_t player;
  uint32_t caster;
  int32_t caster_player;
  /// True on capture, false on release.
  bool caught;
};

/// The LOOK IN FORCE for a player's hero CHANGED (Murgo's Mirror, refii_mod_appearance): manual
/// came on or off, or a manual input moved. Each player's look is the player store's
/// `character_morphs.*` set (the Mirror and refii_mod_set_appearance write it; the machine that
/// plays the hero owns it, so it is the same on both co-op machines); Godot turns it into the
/// weights of the weight function `sub_822DA028` the morph hook answers for that hero, and this is
/// raised where the hook takes the new look (character_morph_hooks.cpp), on every machine. Not
/// raised for a look that stays the same, nor for automatic stats moving (that is the game's own
/// appearance, unchanged by refii). Read-only.
struct AppearanceChangedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_APPEARANCE_CHANGED;
  /// 1 or 2.
  int32_t player;
  /// True when the guest's own stats drive the look again; false when the stated manual inputs do.
  bool automatic;
  /// The player's hero, 0 while it has none.
  uint32_t hero;
};

/// A MOD MORPH'S WEIGHT CHANGED for a player (refii_mod_morph_weight): the player store's
/// `morph.<id>` moved — the Mirror's mod sliders, or a mod's morph_set on either machine — or the
/// registry the enabled mods declare changed what it answers. Raised where the morph hook takes the
/// new weight (character_morph_hooks.cpp's tick), on every machine; the hook then redraws that hero.
/// Read-only.
struct MorphChangedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_MORPH_CHANGED;
  /// 1 or 2.
  int32_t player;
  /// The morph's type, refii_mod_morph_id("<mod>.<name>").
  uint32_t morph;
  /// The weight the hook answers now (clamped, or the default when none is stored).
  float weight;
  /// The player's hero, 0 while it has none.
  uint32_t hero;
};

/// Fable 3: a hero's unspent guild seals changed — granted directly, spent on the Road to Rule, or
/// earned as renown passed a threshold (`source` 'granted' / 'spent' / 'earned'). After the fact.
/// Seams: Fable3/src/hooks/src/mod_events_hooks.cpp (the stats component's grant, spend and recompute).
struct GuildSealsEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_GUILD_SEALS;
  uint32_t entity;
  int32_t player;
  int32_t delta;
  int32_t seals;
  const char* source;
};

/// Fable 3: the kingdom's treasury changed through its writer (tax, transfers, the cost of a
/// decision). After the fact. Seam: Fable3/src/hooks/src/mod_events_hooks.cpp.
struct TreasuryEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_TREASURY;
  int32_t treasury;
  int32_t delta;
  int32_t type;
};

/// Fable 3: a town demand was decided, accepted or rejected, and its effects ran. After the fact.
/// Seam: Fable3/src/hooks/src/mod_events_hooks.cpp (the demand's accept and reject).
struct KingdomDecisionEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_KINGDOM_DECISION;
  uint32_t demand;
  uint32_t province;
  uint32_t level;
  bool accepted;
};

/// A chest opened (both titles): the chest, who opened it (0 for a script), the keys it needs; on
/// Fable 3 its guild-seal cost and whether it was the first time. Read-only.
struct ChestOpenedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CHEST_OPENED;
  uint32_t chest;
  uint32_t opener;
  int32_t player;
  int32_t keys;
  int32_t seals;
  bool first;
};

/// A hero bought an item from a shop. Fable 2 reports it; Fable 3 decides it.
struct ItemBoughtEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ITEM_BOUGHT;
  uint32_t shopkeeper;
  uint32_t buyer;
  int32_t player;
  uint32_t definition;
  int32_t price;
  bool stolen;
};

/// A hero sold an item to a shop. Fable 2 reports it; Fable 3 decides it.
struct ItemSoldEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_ITEM_SOLD;
  uint32_t shopkeeper;
  uint32_t seller;
  int32_t player;
  uint32_t definition;
  int32_t price;
  int32_t count;
};

/// A shop priced an item for a customer; `price` is writable.
struct ShopPriceEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SHOP_PRICE;
  uint32_t shopkeeper;
  uint32_t customer;
  int32_t player;
  uint32_t definition;
  const char* kind;  // "buy" or "sell"
  int32_t price;
};

/// A digging spot was dug out. Read-only.
struct DigSpotDugEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DIG_SPOT_DUG;
  uint32_t spot;
  uint32_t digger;
  int32_t player;
  bool by_dog;
  bool criminal;
};

/// A Demon Door changed: "face_out", "face_in" (Fable 3) or "opened". Read-only.
struct DemonDoorEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DEMON_DOOR;
  uint32_t door;
  const char* state;
  int32_t player;
};

/// A hero drank: `amount` is writable, cancel stops the drink counting.
struct DrinkEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DRINK;
  uint32_t hero;
  int32_t player;
  float amount;
  int32_t drinks;
};

/// Fable 3: the hero arrived in the Sanctuary or back in the world. After the fact. Seam:
/// Fable3/src/hooks/src/mod_events_hooks.cpp (the switch manager's arrival). Fable 2 has no Sanctuary.
struct SanctuaryEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SANCTUARY;
  uint32_t hero;
  int32_t player;
  bool entered;
  int32_t reason;
};

/// Fable 3: a switch into (or out of) the Sanctuary is asked for; `cancel` refuses an entry. Seam:
/// Fable3/src/hooks/src/mod_events_hooks.cpp (GameComponentSwitchManager.SwitchToNextComponent).
struct SanctuaryRequestEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SANCTUARY_REQUEST;
  uint32_t hero;
  int32_t player;
  int32_t reason;
  bool to_sanctuary;
};

/// Fable 3: a weapon changes level (its morph, or a new weapon brought to its hero's level); `level` is
/// writable, `cancel` keeps the level. Seam: Fable3/src/hooks/src/mod_events_hooks.cpp (SetLevel).
struct WeaponLevelUpEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_WEAPON_LEVEL_UP;
  uint32_t weapon;
  uint32_t owner;
  int32_t player;
  int32_t level_from;
  int32_t level;
  const char* cause;  // "level_up" or "sync"
};

/// Fable 3: a weapon gains an augment by play (a level's, or a met condition's); `cancel` refuses it.
/// Seam: Fable3/src/hooks/src/mod_events_hooks.cpp (AddAugment).
struct WeaponAugmentUnlockedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_WEAPON_AUGMENT_UNLOCKED;
  uint32_t weapon;
  uint32_t owner;
  int32_t player;
  uint32_t augment;
  const char* source;  // "level" or "condition"
  int32_t level;
};

/// Fable 3: a weapon's augment condition was met; it now waits to level up. After the fact. Seam:
/// Fable3/src/hooks/src/mod_events_hooks.cpp (the conditional-augment check).
struct WeaponUpgradeReadyEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_WEAPON_UPGRADE_READY;
  uint32_t weapon;
  uint32_t owner;
  int32_t player;
  int32_t added;
  int32_t pending;
  int32_t level;
};

/// Fable 3: a villager's hand was taken or let go. After the fact. Seam: Fable3/src/hooks/src/
/// mod_events_hooks.cpp (CECHandHolding's state setter). Fable 2 has no hand holding.
struct HandHoldingEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HAND_HOLDING;
  uint32_t villager;
  uint32_t leader;
  int32_t player;
  bool holding;
  bool dragged;
  bool forced;
};

/// Fable 3: something asks to take a villager's hand; `cancel` refuses it. Seam: Fable3/src/hooks/src/
/// mod_events_hooks.cpp (CECHandHolding's start). Fable 2 has no hand holding.
struct HandHoldingRequestEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HAND_HOLDING_REQUEST;
  uint32_t villager;
  uint32_t leader;
  int32_t player;
  bool forced;
};

/// Fable 3: a hero performs a social interaction (the ring's choice, a script's, a relayed one); a
/// cancel stops it. Seam: Fable3/src/hooks/src/mod_events_hooks.cpp (CECCharacterInteraction::Perform).
struct InteractionStartedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_INTERACTION_STARTED;
  uint32_t hero;
  int32_t player;
  uint32_t target;
  uint32_t interaction;
  bool scripted;
};

/// Fable 3: the interaction circle mini-game ended; `success` is writable. Seam: mod_events_hooks.cpp.
struct InteractionResultEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_INTERACTION_RESULT;
  uint32_t hero;
  int32_t player;
  uint32_t target;
  int32_t kind;
  bool success;
};

/// Fable 3: the GUI's interaction mode opened or closed. Seam: Fable3/src/hooks/src/interaction_hooks.cpp.
struct InteractionModeEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_INTERACTION_MODE;
  uint32_t hero;
  int32_t player;
  uint32_t target;
  int32_t mode;
  int32_t previous;
  bool open;
};

/// Fable 3: a villager's relationship with a hero changed stage (0 fear .. 4 love). After the fact.
/// Seam: Fable3/src/hooks/src/mod_events_hooks.cpp (the opinion record's SetStage and its four callers).
struct RelationshipStageEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_RELATIONSHIP_STAGE;
  uint32_t villager;
  uint32_t hero;
  int32_t player;
  int32_t stage;
  int32_t previous;
  const char* cause;
};

/// The dog starts digging at `spot` (the dog brain's Dog.SetEntityBeingDug); `first` the spot was not
/// yet marked dug by a dog. Read-only.
struct DogDigEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DOG_DIG;
  uint32_t dog;
  uint32_t spot;
  int32_t player;
  bool first;
};

/// The dog starts leading its hero to a find: `target` (a dig spot, a chest; 0 for a quest objective
/// or none), `quest` a quest objective. Read-only.
struct DogLeadingEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DOG_LEADING;
  uint32_t dog;
  int32_t player;
  uint32_t target;
  bool quest;
};

/// The dog learned a trick: `trick` the command record's hash (dog_trick's), `shown` the game
/// announced it, `tricks` how many it knows now. Read-only.
struct DogTrickLearnedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DOG_TRICK_LEARNED;
  uint32_t dog;
  int32_t player;
  uint32_t trick;
  bool shown;
  int32_t tricks;
};

/// The dog's training level on `track` changed (0 explore / sniffing, 1 combat, 2 charisma on Fable 3):
/// `level` the new one, `up` it rose, `xp` Fable 3's training XP on that track (0 on Fable 2). Read-only.
struct DogLearnedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DOG_LEARNED;
  uint32_t dog;
  int32_t player;
  int32_t track;
  int32_t level;
  bool up;
  int32_t xp;
};

/// A hero finished a dive at a dive spot (counted, the spot's script runs). Read-only.
struct DiveSpotUsedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_DIVE_SPOT_USED;
  uint32_t spot;
  uint32_t diver;
  int32_t player;
};

/// A drunk hero threw up: `vomits` the count now. Read-only.
struct VomitEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_VOMIT;
  uint32_t hero;
  int32_t player;
  int32_t vomits;
};

/// Fable 3: a hero started a job session at a job sign (`job` its JobName). Read-only.
struct JobStartedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_JOB_STARTED;
  uint32_t sign;
  uint32_t hero;
  int32_t player;
  const char* job;
};

/// Fable 3: a hero's job level is set; `level` writable, cancel keeps the record.
struct JobLevelEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_JOB_LEVEL;
  uint32_t hero;
  int32_t player;
  const char* job;
  int32_t level;
  int32_t previous;
};

/// Fable 3: a hero's total money earned at a job is set. Read-only.
struct JobEarningsEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_JOB_EARNINGS;
  uint32_t hero;
  int32_t player;
  const char* job;
  int32_t total;
  int32_t earned;
};

/// Fable 3: a hero's sessions worked at the job's current level is set. Read-only.
struct JobWorkedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_JOB_WORKED;
  uint32_t hero;
  int32_t player;
  const char* job;
  int32_t times;
  int32_t level;
};

/// A job script stated its combo multiplier to the HUD (`on` false: turned off). Read-only.
struct JobMultiplierEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_JOB_MULTIPLIER;
  int32_t level;
  bool on;
  int32_t player;
};

/// Fable 2: a job shift's summary screen: "title" (text, subtitle) or one "line" (text, amount).
struct JobSummaryEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_JOB_SUMMARY;
  const char* part;
  const char* text;
  const char* subtitle;
  int32_t amount;
};

/// The guild voice starts its next message: Fable 2 the text tag, Fable 3 the text key ("0x…").
struct GuildMessageEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_GUILD_MESSAGE;
  const char* message;
  const char* narrator;
  int32_t player;
};

/// A hero's title changed (the stats' title record moved). Read-only.
struct TitleChangedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_TITLE_CHANGED;
  uint32_t hero;
  int32_t player;
  const char* title;
};

/// A title was added to a hero's unlocked titles. Read-only.
struct TitleUnlockedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_TITLE_UNLOCKED;
  uint32_t hero;
  int32_t player;
  const char* title;
  int32_t titles;
};

/// The story's chapter progress rose. Read-only.
struct ChapterProgressEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CHAPTER_PROGRESS;
  uint32_t hero;
  int32_t player;
  float progress;
  float previous;
};

/// A scar is about to be added to a body area; cancel refuses it.
struct ScarAddedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SCAR_ADDED;
  uint32_t entity;
  int32_t player;
  uint32_t area;
  int32_t scars;
};

/// A random scar was healed. Read-only.
struct ScarRemovedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_SCAR_REMOVED;
  uint32_t entity;
  int32_t player;
  int32_t scars;
};

/// An entity's age is about to be set; `age` is writable, cancel keeps the old age.
struct AgeChangedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_AGE_CHANGED;
  uint32_t entity;
  int32_t player;
  float age;
  float previous;
  float limit;
};

/// The hero's story stage changed. Read-only.
struct HeroStageEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_HERO_STAGE;
  uint32_t hero;
  int32_t player;
  int32_t stage;
  int32_t previous;
};

/// An item of clothing is about to be worn; cancel leaves it unworn.
struct ClothingWornEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CLOTHING_WORN;
  uint32_t entity;
  int32_t player;
  uint32_t definition;
  int32_t category;
};

/// Worn clothing was taken off (one item, or every item of a category). Read-only.
struct ClothingRemovedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CLOTHING_REMOVED;
  uint32_t entity;
  int32_t player;
  uint32_t definition;
  int32_t category;
  int32_t count;
};

/// A worn item is dyed. A script's dye is decided (colour writable, cancel); the inventory's is
/// reported after.
struct ClothingDyedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CLOTHING_DYED;
  uint32_t entity;
  int32_t player;
  uint32_t definition;
  uint32_t dye;
  int32_t red;
  int32_t green;
  int32_t blue;
  float brightness;
  bool secondary;
};

/// A pub game is about to start; cancel refuses it (StartPubGame answers false).
struct PubGameStartedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PUB_GAME_STARTED;
  const char* game;
  const char* variation;
  uint32_t hero;
  int32_t player;
};

/// A pub game ended and is about to be settled. Read-only.
struct PubGameFinishedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_PUB_GAME_FINISHED;
  const char* game;
  const char* variation;
  uint32_t hero;
  int32_t player;
  int32_t gold;
  int32_t result;
};

/// A town's wealth is about to change; `delta` is writable, cancel keeps it.
struct VillageWealthEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_VILLAGE_WEALTH;
  uint32_t village;
  float delta;
  bool scaled;
  float wealth;
  float min;
  float max;
};

/// A gargoyle was destroyed and counted for the current region. Read-only.
struct GargoyleDestroyedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_GARGOYLE_DESTROYED;
  const char* level;
  int32_t player;
};

// THE CAMERA EVENTS (164..168). camera_cut, camera_blend and camera_tick are read off the game's own
// camera statements (src/runtime/src/core/stated_view.cpp): a cut where the block the manager's getter
// built (rex_lhCameraManager_Block) carries the cut byte set and its previous block had it clear, a blend
// where Camera.InterpolateFor (rex_Camera_InterpolateFor) was stated with ticks > 0, a tick per push
// (rex_lhCameraManager_Push, the end of one manager's update); each is the player of that push, raised
// from the guest tick (mod_events.cpp DispatchCameraEvents). camera_screen_changed is the shell's views
// (RefiiHostStateCameraViews) differing from the last raised; camera_zoom_changed the core camera chunk's
// statement after SACCamera:CalculateActiveZoomLevel (Refii._camera_zoom_stated). All read-only.

/// The game cut player `player`'s camera.
struct CameraCutEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CAMERA_CUT;
  int32_t player;
};

/// Player `player`'s camera started a blend of `ticks` guest ticks (Camera.InterpolateFor).
struct CameraBlendEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CAMERA_BLEND;
  int32_t player;
  int32_t ticks;
};

/// The co-op screen changed: mode REFII camera screen 0 shared, 1 split_vertical, 2 split_horizontal,
/// 3 hybrid; split two views drawn; reason 0 the player's choice or a join / leave, 1 hybrid's distance.
struct CameraScreenChangedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CAMERA_SCREEN_CHANGED;
  int32_t mode;
  bool split;
  int32_t previous;
  int32_t reason;
};

/// Player `player`'s exploring camera changed zoom set or entered / left the combat blend; the level in
/// force after the change (SACCamera's ScaledActiveZoomLevel).
struct CameraZoomChangedEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CAMERA_ZOOM_CHANGED;
  int32_t player;
  const char* set;
  const char* previous_set;
  int32_t level;
  float combat;
  float string_length;
  float cage_radius;
  float max_cage_radius;
  float height;
  float focal_offset;
  float fov;
  float max_coop_distance;
};

/// Once per guest tick per player: the block player `player`'s readers see after the camera's update.
struct CameraTickEvent : Event {
  static constexpr int32_t kKind = REFII_EVENT_CAMERA_TICK;
  int32_t player;
  float x;
  float y;
  float z;
  float fx;
  float fy;
  float fz;
  float ux;
  float uy;
  float uz;
  /// Degrees, horizontal.
  float fov;
  bool cut;
};

/// Subscribes a typed handler: `void fn(E&)`. Returns the subscription index.
template <typename E>
inline int32_t Subscribe(void (*fn)(E&), int32_t priority = 0, const char* owner = REFII_MOD_NAME) {
  return refii_mod_subscribe(
      E::kKind,
      [](void* event, void (*user)(void)) { reinterpret_cast<void (*)(E&)>(user)(*static_cast<E*>(event)); },
      reinterpret_cast<void (*)(void)>(fn), priority, owner);
}

/// Fires a typed event through every subscriber; the struct's state afterwards is the verdict.
template <typename E>
inline void Dispatch(E& event) {
  refii_mod_dispatch(E::kKind, &event);
}

}  // namespace refii::mods

/// Subscribes `fn` (a `void fn(refii::mods::<Kind>Event&)`) when the game module loads:
///   static void OnHit(refii::mods::HitEvent& e) { ... }
///   REFII_MOD_ON(Hit, OnHit);
/// REFII_MOD_ON_PRIORITY(Hit, OnHit, 10) runs before priority-0 handlers.
#define REFII_MOD_ON(Kind, fn) \
  static const int32_t refii_mod_on_##Kind##_##fn = refii::mods::Subscribe<refii::mods::Kind##Event>(&fn)
#define REFII_MOD_ON_PRIORITY(Kind, fn, priority) \
  static const int32_t refii_mod_on_##Kind##_##fn = refii::mods::Subscribe<refii::mods::Kind##Event>(&fn, (priority))
