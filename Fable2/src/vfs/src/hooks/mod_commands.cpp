// refii — the host's helper calls and queries, run in the guest tick (core/payload_bridge.h:
// refii_bridge_mod_command_poll). A Godot script (RefiiHost) cannot call refii_mod_* itself:
// those need the guest tick thread and its context. So it posts a LINE and the tick runs it
// here with the real context; a query answers through the event queue under
// REFII_MOD_HOST_REPLY_KIND, carrying the id the script gave it.
//
// GRAMMAR (space-separated; numbers decimal; `run_lua` takes the rest of the line):
// Entities and objects are HANDLES (refii_mod_handle: what every event gave the script), never
// addresses; definitions and text hashes are "0x…" strings or decimal.
//   give_gold E D · modify_health E D ATTACKER · set_health E V · add_item E DEFINITION [COUNT]
//   give_experience E POOL AMOUNT · spend_experience E POOL AMOUNT
//   modify_renown E D · modify_morality E D · modify_purity E D   (refii_mod_modify_renown ..: the
//                                game's own Stats.Modify* bodies, so the events fire)
//   save_game P   (refii_mod_save_game: AutoSaveGame(true, true) where player P may save)
//   cast_spell CASTER TYPE TARGETED TARGET LEVEL [X Y Z [DX DY DZ]] · set_position E X Y Z
//   set_facing E X Y [Z]   (refii_mod_set_facing: Physics.SetFacingVector, a hero's Player.SetFacingVector)
//   cast_spell_with CASTER TYPE TARGETED TARGET LEVEL TAG SCALE [X Y Z [DX DY DZ]] [at E | at X Y Z]
//                                (refii_mod_cast_spell_with: the cast's tag and its hits' damage scale;
//                                `at` places the cast by its class's rule for that entity or point,
//                                X Y Z then the origin and DX DY DZ the facing on the place)
//                                   (the start position and facing; the caster's own when absent)
//   entity_tint E R G B A · entity_untint E   (refii_mod_entity_tint: the game's own tint setter)
//   entity_alpha E A · entity_scale E S · entity_saturation_mask E ON · entity_drawable E ON
//                                (the other component-3 setters, refii_mod_entity_alpha ..)
//   lock_target P E · release_lock P · switch_target P LEFT   (refii_mod_lock_target ..: P 1 or 2,
//                                E 0 for the game's own pick, LEFT 1 left / 0 right)
//   wheel_switches_target E ON   (refii_mod_wheel_switches_target, STICKY: stated again on every
//                                guest tick while ON is 1 and E's player holds a lock; cleared by
//                                ON 0 or the lock's release — a Godot script runs off the tick)
//   apply_status E KIND SECONDS [LEVEL [SOURCE]] · clear_status E KIND   (refii_mod_apply_status:
//                                1 electrified 2 chaos 3 burning)
//   knockback E X Y Z [FORCE] · stagger E   (refii_mod_knockback / refii_mod_stagger)
//   set_speed_multiplier P FACTOR · clear_speed_multiplier P   (refii_mod_set_speed_multiplier /
//                                refii_mod_clear_speed_multiplier: the player component's override)
//   allow_action_interrupt A   (refii_mod_allow_action_interrupt: A an action handle)
//   allow_action_interrupt_at A SECONDS   (refii_mod_allow_action_interrupt_at: granted once A's clock reaches SECONDS)
//   play_sound CUE E [TAG] · play_sound_at CUE X Y Z [TAG] · stop_sound E TAG [FADE_MS]
//                                (refii_mod_play_sound_on / _at / refii_mod_stop_sound: E 0 for the
//                                point emitters; an empty tag is "-")
//   play_music NAME · stop_music NAME   (refii_mod_play_music / refii_mod_stop_music)
//   play_animation E NAME [LOOPS [BLEND [SPEED]]] · stop_animation E
//                                (refii_mod_play_animation / refii_mod_stop_animation)
//   modify_opinion V H AXIS DELTA · add_deed V H DEED   (refii_mod_modify_opinion / refii_mod_add_deed)
//   npc_follow E T [RADIUS] · npc_go_to E X Y Z · npc_flee E FROM · npc_attack E T
//   npc_set_hostile E OTHER ON · npc_stop E   (refii_mod_npc_*: the quests' own orders, in the game VM)
//   set_time_of_day HOURS · set_weather THEME [WEIGHT [SECONDS]]   (refii_mod_set_time_of_day /
//                                refii_mod_set_weather: the clock's setter, the theme blend)
//   save_set KEY INT · run_lua CHUNK...
//   player_set P KEY V · player_add P KEY D [SAVED] · player_save P KEY V   (the player store
//     Refii.player_set / _add / _save write: refii_mod_player_*)
//   player_set_text P KEY B64 · player_save_text P KEY B64   (a typed text in the same store,
//     refii_mod_player_set_text / _save_text; base64 on the line, '-' for "")
//   mask_word event|verdict W HEX -> word W (2 and up: words 0 and 1 are the two mask exports) of
//                                   the host's event or verdict mask, its 64 bits in hex
//                                   (HostBridge.SetModEventMask / SetModVerdictMask post it)
//   publish KEY VALUE            -> refii::mods::Publish: the value every reader sees ("mod:<key>")
//   menu_row PLAYER MENU ROW MOD [CMD64 BUTTON [KIND DEF SPELL]]
//                                -> raises ModMenuRow for the native and Lua handlers (a row chosen
//                                   in Godot; a space in a name arrives as '_', an empty one as '-');
//                                   KIND DEF SPELL the row's typed subject (Refii.Mods.ModRowSubject)
//   box_answer ID BUTTON PLAYER  -> raises ModMessageBox for the native and Lua handlers (a mod's
//                                   message box answered in Godot: 0 A, 1 B, -1 replaced)
//   notice_done ID OUTCOME PLAYER -> raises ModNotice (a mod's notice gone: 0 shown, -1 never shown)
//   menu_box_answer ID OPTION PLAYER -> raises ModMenuBox (a mod's menu box answered: the option, -1 none)
//   attack_mode PLAYER MODE -> raises AttackModeChanged on a change (the keyboard's attack button mode
//                                   for the player it plays: 1 melee, 2 ranged, 3 magic; refii_mod_attack_mode)
//   camera_override P MODE [FOV [PITCH [BLEND [FLAGS]]]] -> refii_mod_camera_override: player P's hold
//                                   (MODE 8 first person, 2 the third-person camera, 0 lets go; 0 FOV or
//                                   PITCH the camera's own, BLEND below 0 its own; FLAGS 1 yields to the gun aim)
//   query ID health E            -> id,ok,current,max
//   query ID position E          -> id,ok,x,y,z
//   query ID facing E            -> id,ok,x,y,z   (refii_mod_facing: Physics.GetFacingVector)
//   query ID velocity E          -> id,ok,x,y,z   (refii_mod_velocity: Physics.GetVelocity)
//   query ID gold E              -> id,ok,gold
//   query ID item_count E DEF    -> id,ok,count
//   query ID experience E POOL   -> id,ok,amount
//   query ID ability_level E T   -> id,ok,level
//   query ID relation A B        -> id,ok,relation
//   query ID alive E             -> id,ok,alive
//   query ID locked_target P     -> id,ok,target  (refii_mod_locked_target: 0 when not locked)
//   query ID can_save P          -> id,ok,able    (refii_mod_can_save: Player.IsAbleToSaveGame)
//   query ID in_cutscene P       -> id,ok,kinds   (refii_mod_in_cutscene: REFII_CUTSCENE_* bits, 0 none;
//                                   P 0 either player)
//   query ID camera_mode P       -> id,ok,mode,fov (refii_mod_camera_mode: ECameraMode, -1 none;
//                                   refii_mod_camera_fov: degrees, 0 with no camera)
//   query ID has_status E KIND   -> id,ok,has     (refii_mod_has_status)
//   query ID speed_multiplier P  -> id,ok,overridden,factor  (refii_mod_speed_multiplier)
//   query ID current_action E    -> id,ok,action,elapsed,interruptible  (refii_mod_current_action,
//                                   _action_elapsed, _action_interruptible; action 0 for none)
//   query ID action A            -> id,ok,type,name='..',elapsed,interruptible  (refii_mod_action_type / _action_name:
//                                   EScriptableAction and its name; ok false for an unknown action)
//   query ID frontend            -> id,ok,signed_in,saves,screen='..'  (refii_mod_signed_in_user,
//                                   _save_count, _frontend_screen; ok false with no front end up)
//   query ID living E            -> id,ok,living  (refii_mod_entity_living)
//   query ID spell_in_slot P SLOT -> id,ok,type   (refii_mod_spell_in_slot: -1 none)
//   query ID spell_parts SERIAL  -> id,ok,spell,type,targeted,level,caster,caster_player,vortex,
//                                   anchor,sword_count,s1..s8,x,y,z,has_origin,exploded
//                                   (refii_mod_spell_parts; ok false for an unknown cast)
//   query ID vortex_held SERIAL  -> id,ok,count,v1..v16  (refii_mod_vortex_held: the handles the
//                                   cast's live vortex holds, its list order, 0 past count; ok
//                                   false when no live vortex carries SERIAL)
//   query ID spell_balance C T L -> id,ok,status_seconds,status_spread,duration,swords
//                                   (refii_mod_spell_balance: power L of spell T as its Targeted class's
//                                   data read loads it for caster C; -1 a field the class lacks; ok
//                                   false for another type or a caster with no spell manager)
//   query ID host_serial SERIAL  -> id,ok,serial  (refii_mod_host_serial: the online host's serial of
//                                   this machine's spell; ok false while its cast is not paired)
//   query ID local_serial SERIAL -> id,ok,serial  (refii_mod_local_serial: this machine's spell for
//                                   the host's serial; ok false while not paired)
//   query ID time_of_day         -> id,ok,hours   (refii_mod_time_of_day: the clock, 0..24)
//   query ID definition E        -> id,ok,definition
//   query ID name E              -> id,ok,name='..'
//   query ID level_name          -> id,ok,level='..'
//   query ID hero P              -> id,ok,hero
//   query ID player_of E         -> id,ok,player
//   query ID player_for E        -> id,ok,player  (refii_mod_player_for: who "the hero" is for E)
//   query ID world_here          -> id,ok,here    (refii_mod_world_here)
//   query ID combat_state P      -> id,ok,midst,close_to_combat,in_any_combat (Refii.combat_state)
//   query ID lua_int EXPR... (last two tokens LO HI) -> id,ok,value
//   query ID renown E -> renown · morality E / purity E -> value · physique E -> fat,height
//   query ID age E -> age · equipped E -> melee,ranged · inventory_stack E I -> definition,count
//   query ID inventory P [OFFSET] -> total,offset,next,count,d1..dk ('0x…'),n1..nk (count),c1..ck
//                        (category),v1..vk (viewed 0|1),u1..uk (usable 0|1, refii_mod_inventory_usable):
//                        player P's hero's stacks in the game's order,
//                        up to 40 a reply from stack OFFSET; ask again from next while next < total
//   use_item P DEF [FLAGS] · equip_weapon P DEF [FLAGS] · perform_expression P EXPRESSION [FLAGS] ·
//   assign_spell P SPELL [FLAGS]
//                        (refii_mod_quick_action: the quick menu's own actions for player P's hero,
//                        gated as the quick menu is unless FLAGS has REFII_QUICK_UNGATED (1); 2 holds
//                        the expression; a joiner's weapon pick goes through the co-op kit)
//   release_expression P (refii_mod_release_expression) · end_sprint P (refii_mod_end_sprint)
//   set_locomotion P KEY VALUE ("-" stops holding KEY) (refii_mod_set_locomotion)
//   set_parry P WINDOW WEDGE (negative leaves one) (refii_mod_set_parry)
//   query ID sprinting P -> sprinting · locomotion P KEY -> value · parry P -> window,wedge
//   query ID expressions P -> count,e1..eN (records '0x…'),u1..uN (uses)  (refii_mod_expression)
//   query ID spells_known P -> mask,count  (refii_mod_spells_known: bit 1 << type)
//   query ID selected_expression P -> expression='0x…'  (refii_mod_selected_expression; ok false with
//                        no wheel up for P)
//   query ID record_string DEF FIELD -> value='..'  (refii_mod_record_string: the inherited string, exact
//                        but for a quote or a line break)
//   query ID record_int DEF FIELD -> value=n  (refii_mod_record_int: the inherited int or enum)
//   query ID record_number DEF FIELD -> value=x · record_bool DEF FIELD -> value=true|false ·
//     record_exists DEF -> exists=true|false  (refii_mod_record_number / _bool / _exists)
//   query ID record_ref DEF FIELD -> value='0x…'  (refii_mod_record_ref: the record a type-6 property names)
//   charge_spell P TYPE HELD  (refii_mod_charge_spell: HELD 1 charges spell TYPE through P's own magic
//                        charge, its tiers lent with no spell_pick; 0 lets the button go)
//   query ID creature_tier E -> tier  (refii_mod_creature_tier: 1 elite | 2 lieutenant; ok false without a creature)
//   query ID near X Y Z R [CREATURES [OFFSET]] -> total,offset,count,e1..ecount (100 a page)
//   query ID line_of_sight X1 Y1 Z1 X2 Y2 Z2 -> clear
//   query ID spawn DEF X Y Z -> entity
//   query ID local_hero -> hero · coop_mode -> mode · menu_player -> player
//   query ID coop_state -> local_hero,coop_mode,menu_player,split_screen (the four at once)
//   query ID player_value P KEY [FALLBACK] -> value,has · player_here P -> here
//   query ID player_text P KEY -> text (quoted: a quote, backslash, comma or line break as '_'),
//     hash ('0x…' when the text reads as one), has
//   query ID player_dog P -> dog · focus P -> entity · opinion E P -> love,fear,attractiveness,
//     morality,respect · uid E -> uid='0x…'
//   query ID discovered_crime E P -> discovered · perceived_renown E P -> renown
//   query ID appearance P -> automatic,tan,fat,height,strength,skill,will,morality,purity,age
//     (refii_mod_appearance: the look in force for player P, Murgo's Mirror's or the game's own)
//   query ID morph P MORPH -> weight,registered   (refii_mod_morph_weight; MORPH "0x…" or "<mod>.<name>")
//   morph_set P MORPH W   (refii_mod_set_morph_weight)
//   appearance_set P NAME=V...   (refii_mod_set_appearance: manual, the named inputs of tan fat
//     height strength skill will morality purity age, the rest kept as they look)
//   appearance_automatic P ON    (refii_mod_set_appearance_automatic)
//   appearance_state P AUTOMATIC TAN FAT HEIGHT STRENGTH SKILL WILL MORALITY PURITY AGE
//     (GameCharacterMorphs' statement of a settings slot's look for player P; written only where P
//     is played: refii::vfs::AppearanceState)
//   forgive_crimes E P   (refii_mod_forgive_crimes: player P's crimes the villager E knows of)
//   fx_play ID HASH X Y Z ENTITY OX OY OZ SCALE INTENSITY LOOP [R G B]
//                        play_fx under the classic look (Engine_FxPlayer): the guest's own particle
//                        instance of effect HASH (its bank key), at X Y Z or riding handle ENTITY
//                        (0 for none) in its frame, offset by OX OY OZ along its axes and turned
//                        with it (guest_fx below); SCALE and INTENSITY the instance's own
//                        setters; LOOP 1 spawns it again each time it retires; R G B the tint, on
//                        the instance's own material copy (particle_dress.cpp TintFxInstance)
//   fx_stop ID           ends it: the guest's kill (a zero-frame fade) and our reference released
//   fx_reload            the guest's particle bank read again through its own loader after a
//                        remount (particle_dress.cpp ReloadParticleBank)
//   gdb_reload PATH      a remounted database (`globals/globals.gdb.tu`, `data/globals/globals.gdb`)
//                        read again by the guest's own loader, its records mapped and every older
//                        copy's values updated in place (gdb_hot_reload.cpp)
//   gdb_revert PATH HASH...  rows a patch states no more, mapped back to PATH's base records
//   frontend_lua CHUNK...  a chunk in the front end's mod Lua (mod_frontend_lua.cpp): the host's
//                        state for the current presentation, the mods' front-end chunk run in it first
//   ability_clear · ability_declare … · ability_buy P NAME · ability_refund P NAME
//                        the mod abilities' engine path (mod_abilities.cpp): the book declares each
//                        ui/abilities.json record, A and Y post the purchase and the unlearn
//   spell_pick P SLOT SPELL STORE PICK MOD
//                        a pick on a spell slot (ui/spellwheel.json): raises SpellPick, then saves
//                        the player value STORE unless a handler refused it (mod_abilities.cpp)
//   partner_send MOD NAME [BASE64]
//                        refii_mod_send_to_partner for a Godot script: the payload base64 on the
//                        line (no spaces), raised on the other machine as PartnerMessage
// A command that does nothing (no such entity, no component) is silent; a query always replies,
// with ok=false when it could not be answered.
// A Godot VERDICT handler reads a query verb that changes nothing (kReadOnlyVerbs) AT ONCE through
// RefiiHost.read: the same branch answers, on the guest thread inside the seam (ModVerdictRead).

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <algorithm>
#include <array>
#include <atomic>
#include <iterator>
#include <string>
#include <unordered_map>
#include <vector>

#include "core/guest_build.h"
#include "core/mod_values.h"
#include "core/refii_mod.h"

// guest_tick_pump.cpp: a player's dog (Refii.player_dog's answer), and local co-op drawn as halves.
extern "C" uint32_t RefiiCoopPlayerDog(uint8_t* base, int player);
extern "C" int RefiiCoopSplitScreen(void);

namespace refii::vfs {
// particle_dress.cpp
void TintFxInstance(const PPCContext& ctx, uint8_t* base, uint32_t instance, const float* rgb);
bool ReloadParticleBank(PPCContext& ctx, uint8_t* base);
// gdb_hot_reload.cpp
void GdbReload(PPCContext& ctx, uint8_t* base, const std::string& path);
void GdbRevert(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w);
void PumpParticleBank(PPCContext& ctx, uint8_t* base);
// mod_frontend_lua.cpp
bool GameLuaUp(uint8_t* base);
bool RunFrontEndLua(PPCContext& ctx, uint8_t* base, const std::string& chunk);
// mod_abilities.cpp
bool RunModAbilityCommand(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& words);
void PumpModAbilities(PPCContext& ctx, uint8_t* base);
// mod_api.cpp
void AppearanceState(int32_t player, bool automatic, const refii_mod_appearance_t& look);
}  // namespace refii::vfs

namespace {

std::vector<std::string> Split(const char* line, size_t keep_rest_from) {
  std::vector<std::string> words;
  const char* c = line;
  while (*c != 0) {
    while (*c == ' ') {
      ++c;
    }
    if (*c == 0) {
      break;
    }
    if (words.size() == keep_rest_from) {
      words.emplace_back(c);
      break;
    }
    const char* start = c;
    while (*c != 0 && *c != ' ') {
      ++c;
    }
    words.emplace_back(start, static_cast<size_t>(c - start));
  }
  return words;
}

/// A raw word: a hash as "0x…" or decimal.
uint32_t U(const std::vector<std::string>& w, size_t i) {
  return i < w.size() ? static_cast<uint32_t>(std::strtoul(w[i].c_str(), nullptr, 0)) : 0u;
}
/// An entity or object: the script holds a HANDLE (refii_mod_handle), never an address.
uint32_t E(const std::vector<std::string>& w, size_t i) { return refii_mod_handle_address(U(w, i)); }

/// Standard base64 back to text (a `partner_send` payload); false for a character outside the alphabet.
bool UnbaseCommand(const std::string& text, std::string& out) {
  uint32_t word = 0;
  int bits = 0;
  for (const char c : text) {
    if (c == '=') {
      break;
    }
    int value;
    if (c >= 'A' && c <= 'Z') {
      value = c - 'A';
    } else if (c >= 'a' && c <= 'z') {
      value = c - 'a' + 26;
    } else if (c >= '0' && c <= '9') {
      value = c - '0' + 52;
    } else if (c == '+') {
      value = 62;
    } else if (c == '/') {
      value = 63;
    } else {
      return false;
    }
    word = (word << 6) | static_cast<uint32_t>(value);
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      out += static_cast<char>((word >> bits) & 0xFFu);
    }
  }
  return true;
}
int32_t I(const std::vector<std::string>& w, size_t i) {
  return i < w.size() ? static_cast<int32_t>(std::strtol(w[i].c_str(), nullptr, 10)) : 0;
}
float F(const std::vector<std::string>& w, size_t i) {
  return i < w.size() ? std::strtof(w[i].c_str(), nullptr) : 0.0f;
}
/// A sound tag: absent or "-" is the empty tag (a line cannot carry an empty word).
const char* Tag(const std::vector<std::string>& w, size_t i) {
  return i < w.size() && w[i] != "-" ? w[i].c_str() : "";
}

void Reply(const std::string& id, bool ok, const char* fields) {
  char text[2048];
  std::snprintf(text, sizeof(text), "id=%s,ok=%s%s%s", id.c_str(), ok ? "true" : "false", fields[0] != 0 ? "," : "",
                fields);
  refii::mods::PostEvent(REFII_MOD_HOST_REPLY_KIND, text);
}

/// A text as a single-quoted Lua literal's body (quotes and newlines become underscores).
void Quote(const char* text, char* out, size_t size) {
  size_t n = 0;
  for (const char* c = text; c != nullptr && *c != 0 && n + 1 < size; ++c) {
    out[n++] = (*c == '\'' || *c == '\\' || *c == '\n' || *c == '\r' || *c == ',') ? '_' : *c;
  }
  out[n] = 0;
}

/// A reply's text kept exact for the host's field parser, which reads a quoted value to its closing
/// quote with no escapes: only a quote and a line break become underscores (a record's path keeps
/// its backslashes, a name its commas).
void QuoteText(const char* text, char* out, size_t size) {
  size_t n = 0;
  for (const char* c = text; c != nullptr && *c != 0 && n + 1 < size; ++c) {
    out[n++] = (*c == '\'' || *c == '\n' || *c == '\r') ? '_' : *c;
  }
  out[n] = 0;
}

constexpr size_t kAnswerBytes = 1900;

/// THE QUERY VERBS THAT CHANGE NOTHING: what a Godot verdict may read at once (RefiiHost.read,
/// ModVerdictRead below), as a Lua handler reads at the call. Every other verb is refused there:
/// spawn makes an entity, lua_int runs any game Lua expression. ModApi.VerdictReads is this list.
constexpr const char* kReadOnlyVerbs[] = {
    "health", "position", "facing", "velocity", "gold", "item_count", "experience", "ability_level", "relation",
    "alive", "locked_target", "in_cutscene", "camera_mode", "has_status", "speed_multiplier", "current_action",
    "action", "frontend", "living", "spell_in_slot", "spell_parts", "vortex_held", "spell_balance", "host_serial",
    "local_serial", "time_of_day", "definition", "name", "level_name", "hero", "player_of", "player_for", "world_here",
    "combat_state", "can_save", "renown", "morality", "purity", "physique", "age", "equipped", "inventory_stack",
    "inventory", "expressions", "spells_known", "sprinting", "locomotion", "parry", "selected_expression",
    "record_string", "record_int", "record_number", "record_bool", "record_exists", "record_ref", "creature_tier",
    "near", "line_of_sight", "local_hero", "coop_mode", "menu_player", "coop_state", "player_value", "player_text",
    "player_here", "player_dog", "focus", "opinion", "discovered_crime", "perceived_renown", "uid", "appearance",
    "morph",
};

/// A mod morph as a command names it: "0x…" its id, else "<mod>.<name>" (refii_mod_morph_id).
uint32_t MorphWord(const std::string& word) {
  if (word.size() > 2 && word[0] == '0' && (word[1] == 'x' || word[1] == 'X')) {
    return static_cast<uint32_t>(std::strtoul(word.c_str() + 2, nullptr, 16));
  }
  return refii_mod_morph_id(word.c_str());
}

/// refii_mod_appearance_t's inputs as the appearance_set / appearance_state lines name and order them.
constexpr float refii_mod_appearance_t::* kAppearanceFields[] = {
    &refii_mod_appearance_t::tan,      &refii_mod_appearance_t::fat,   &refii_mod_appearance_t::height,
    &refii_mod_appearance_t::strength, &refii_mod_appearance_t::skill, &refii_mod_appearance_t::will,
    &refii_mod_appearance_t::morality, &refii_mod_appearance_t::purity, &refii_mod_appearance_t::age,
};
constexpr const char* kAppearanceNames[] = {"tan", "fat", "height", "strength", "skill", "will", "morality", "purity", "age"};

bool ReadOnlyVerb(const std::string& verb) {
  return std::any_of(std::begin(kReadOnlyVerbs), std::end(kReadOnlyVerbs),
                     [&](const char* v) { return verb == v; });
}

/// A query's answer: `w` is "query ID VERB ARGS..."; the fields into `fields`, true when answered.
/// One implementation for the queued lane (RunQuery) and a verdict's read (ModVerdictRead).
bool Answer(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w, char (&fields)[kAnswerBytes]) {
  const std::string what = w.size() > 2 ? w[2] : "";
  bool ok = false;
  if (what == "health") {
    float current = 0.0f, max = 0.0f;
    ok = refii_mod_health(base, E(w, 3), &current, &max) != 0;
    std::snprintf(fields, sizeof(fields), "current=%g,max=%g", static_cast<double>(current), static_cast<double>(max));
  } else if (what == "position") {
    float xyz[3] = {0, 0, 0};
    ok = refii_mod_entity_position(&ctx, base, E(w, 3), xyz) != 0;
    std::snprintf(fields, sizeof(fields), "x=%g,y=%g,z=%g", static_cast<double>(xyz[0]), static_cast<double>(xyz[1]),
                  static_cast<double>(xyz[2]));
  } else if (what == "facing" || what == "velocity") {
    float xyz[3] = {0, 0, 0};
    ok = (what == "facing" ? refii_mod_facing(&ctx, base, E(w, 3), xyz) : refii_mod_velocity(&ctx, base, E(w, 3), xyz)) != 0;
    std::snprintf(fields, sizeof(fields), "x=%g,y=%g,z=%g", static_cast<double>(xyz[0]), static_cast<double>(xyz[1]),
                  static_cast<double>(xyz[2]));
  } else if (what == "gold") {
    int32_t gold = 0;
    ok = refii_mod_gold(base, E(w, 3), &gold) != 0;
    std::snprintf(fields, sizeof(fields), "gold=%d", gold);
  } else if (what == "item_count") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "count=%d", refii_mod_item_count(base, E(w, 3), U(w, 4)));
  } else if (what == "experience") {
    float amount = 0.0f;
    ok = refii_mod_experience(base, E(w, 3), I(w, 4), &amount) != 0;
    std::snprintf(fields, sizeof(fields), "amount=%g", static_cast<double>(amount));
  } else if (what == "ability_level") {
    const int32_t level = refii_mod_ability_level(base, E(w, 3), I(w, 4));
    ok = level >= 0;
    std::snprintf(fields, sizeof(fields), "level=%d", level);
  } else if (what == "relation") {
    const int32_t relation = refii_mod_relation(&ctx, base, E(w, 3), E(w, 4));
    ok = relation != 0;
    std::snprintf(fields, sizeof(fields), "relation=%d", relation);
  } else if (what == "alive") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "alive=%s", refii_mod_entity_alive(base, E(w, 3)) != 0 ? "true" : "false");
  } else if (what == "locked_target") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "target=%u", refii_mod_handle(refii_mod_locked_target(&ctx, base, I(w, 3))));
  } else if (what == "in_cutscene") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "kinds=%d", refii_mod_in_cutscene(&ctx, base, I(w, 3)));
  } else if (what == "camera_mode") {
    ok = I(w, 3) == 1 || I(w, 3) == 2;
    std::snprintf(fields, sizeof(fields), "mode=%d,fov=%.9g", refii_mod_camera_mode(I(w, 3)),
                  static_cast<double>(refii_mod_camera_fov(base, I(w, 3))));
  } else if (what == "has_status") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "has=%s", refii_mod_has_status(base, E(w, 3), I(w, 4)) != 0 ? "true" : "false");
  } else if (what == "speed_multiplier") {
    float factor = 1.0f;
    const uint32_t hero = refii_mod_player_hero(base, I(w, 3));
    ok = hero != 0 && refii_mod_component(base, hero, 33u) != 0;
    const bool overridden = refii_mod_speed_multiplier(base, I(w, 3), &factor) != 0;
    std::snprintf(fields, sizeof(fields), "overridden=%s,factor=%g", overridden ? "true" : "false", static_cast<double>(factor));
  } else if (what == "current_action") {
    ok = true;
    const uint32_t action = refii_mod_current_action(base, E(w, 3));
    float elapsed = 0.0f;
    if (action != 0) {
      refii_mod_action_elapsed(&ctx, base, action, &elapsed);
    }
    std::snprintf(fields, sizeof(fields), "action=%u,elapsed=%g,interruptible=%s", refii_mod_handle(action),
                  static_cast<double>(elapsed), action != 0 && refii_mod_action_interruptible(base, action) != 0 ? "true" : "false");
  } else if (what == "action") {
    const uint32_t action = E(w, 3);
    const int32_t type = refii_mod_action_type(base, action);
    ok = type >= 0;
    float elapsed = 0.0f;
    if (action != 0) {
      refii_mod_action_elapsed(&ctx, base, action, &elapsed);
    }
    std::snprintf(fields, sizeof(fields), "type=%d,name='%s',elapsed=%g,interruptible=%s", type,
                  refii_mod_scriptable_action_name(type), static_cast<double>(elapsed),
                  action != 0 && refii_mod_action_interruptible(base, action) != 0 ? "true" : "false");
  } else if (what == "frontend") {
    char screen[96];
    refii_mod_frontend_screen(&ctx, base, screen, sizeof(screen));
    const int32_t saves = refii_mod_save_count(&ctx, base);
    ok = saves >= 0;
    std::snprintf(fields, sizeof(fields), "signed_in=%d,saves=%d,screen='%s'", refii_mod_signed_in_user(base), saves, screen);
  } else if (what == "living") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "living=%s", refii_mod_entity_living(base, E(w, 3)) != 0 ? "true" : "false");
  } else if (what == "spell_in_slot") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "type=%d", refii_mod_spell_in_slot(base, I(w, 3), I(w, 4)));
  } else if (what == "spell_parts") {
    refii_mod_cast_parts parts{};
    ok = refii_mod_spell_parts(&ctx, base, U(w, 3), &parts) != 0;
    std::snprintf(fields, sizeof(fields),
                  "spell=%u,type=%d,targeted=%s,level=%d,caster=%u,caster_player=%d,vortex=%u,anchor=%u,sword_count=%d,"
                  "s1=%u,s2=%u,s3=%u,s4=%u,s5=%u,s6=%u,s7=%u,s8=%u,x=%g,y=%g,z=%g,has_origin=%s,exploded=%s",
                  refii_mod_handle(parts.spell), parts.type, parts.targeted != 0 ? "true" : "false", parts.level,
                  refii_mod_handle(parts.caster), parts.caster_player, refii_mod_handle(parts.vortex), refii_mod_handle(parts.anchor),
                  parts.sword_count, refii_mod_handle(parts.swords[0]), refii_mod_handle(parts.swords[1]),
                  refii_mod_handle(parts.swords[2]), refii_mod_handle(parts.swords[3]), refii_mod_handle(parts.swords[4]),
                  refii_mod_handle(parts.swords[5]), refii_mod_handle(parts.swords[6]), refii_mod_handle(parts.swords[7]),
                  static_cast<double>(parts.origin[0]), static_cast<double>(parts.origin[1]), static_cast<double>(parts.origin[2]),
                  parts.has_origin != 0 ? "true" : "false", parts.exploded != 0 ? "true" : "false");
  } else if (what == "vortex_held") {
    uint32_t held[16] = {0};
    const int32_t count = refii_mod_vortex_held(base, U(w, 3), held, 16);
    ok = count >= 0;
    int length = std::snprintf(fields, sizeof(fields), "count=%d", count < 0 ? 0 : count);
    for (int32_t i = 0; i < 16 && length > 0 && static_cast<size_t>(length) < sizeof(fields); ++i) {
      length += std::snprintf(fields + length, sizeof(fields) - static_cast<size_t>(length), ",v%d=%u", i + 1,
                              i < count ? refii_mod_handle(held[i]) : 0u);
    }
  } else if (what == "spell_balance") {
    refii_mod_cast_balance balance{};
    ok = refii_mod_spell_balance(&ctx, base, E(w, 3), I(w, 4), I(w, 5), &balance) != 0;
    std::snprintf(fields, sizeof(fields), "status_seconds=%g,status_spread=%g,duration=%g,swords=%d",
                  static_cast<double>(balance.status_seconds), static_cast<double>(balance.status_spread),
                  static_cast<double>(balance.duration), balance.swords);
  } else if (what == "host_serial" || what == "local_serial") {
    const uint32_t serial = what == "host_serial" ? refii_mod_host_serial(base, U(w, 3)) : refii_mod_local_serial(base, U(w, 3));
    ok = serial != 0;
    std::snprintf(fields, sizeof(fields), "serial=%u", serial);
  } else if (what == "time_of_day") {
    float hours = 0.0f;
    ok = refii_mod_time_of_day(base, &hours) != 0;
    std::snprintf(fields, sizeof(fields), "hours=%g", static_cast<double>(hours));
  } else if (what == "definition") {
    const uint32_t definition = refii_mod_entity_definition(base, E(w, 3));
    ok = definition != 0;
    std::snprintf(fields, sizeof(fields), "definition='0x%08x'", definition);
  } else if (what == "name") {
    char name[128] = {0};
    ok = refii_mod_entity_name(base, E(w, 3), name, sizeof(name)) != 0;
    char quoted[128];
    Quote(name, quoted, sizeof(quoted));
    std::snprintf(fields, sizeof(fields), "name='%s'", quoted);
  } else if (what == "level_name") {
    char level[128] = {0};
    ok = refii_mod_level_name(base, level, sizeof(level)) != 0;
    char quoted[128];
    Quote(level, quoted, sizeof(quoted));
    std::snprintf(fields, sizeof(fields), "level='%s'", quoted);
  } else if (what == "hero") {
    const uint32_t hero = refii_mod_player_hero(base, I(w, 3));
    ok = hero != 0;
    std::snprintf(fields, sizeof(fields), "hero=%u", refii_mod_handle(hero));
  } else if (what == "player_of") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "player=%d", refii_mod_player_of(base, E(w, 3)));
  } else if (what == "player_for") {
    // refii_mod_player_for: the player the entity deals with (a dog its master's, else the nearer hero).
    const int32_t player = refii_mod_player_for(&ctx, base, E(w, 3));
    ok = player != 0;
    std::snprintf(fields, sizeof(fields), "player=%d", player);
  } else if (what == "world_here") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "here=%s", refii_mod_world_here(base) != 0 ? "true" : "false");
  } else if (what == "combat_state") {
    // The core co-op Lua's Refii.combat_state(p) -> midst, close_to_combat, in_any_combat: asked of
    // the game VM, the one place the rule lives.
    const int32_t player = I(w, 3);
    int32_t state[3] = {0, 0, 0};
    ok = player == 1 || player == 2;
    for (int i = 0; ok && i < 3; ++i) {
      char expr[160];
      std::snprintf(expr, sizeof(expr),
                    "Refii and Refii.combat_state and ((select(%d, Refii.combat_state(%d))) and 1 or 0) or 0", i + 1,
                    player);
      ok = refii_mod_game_lua_int(&ctx, base, expr, 0, 1, &state[i]) != 0;
    }
    std::snprintf(fields, sizeof(fields), "midst=%s,close_to_combat=%s,in_any_combat=%s", state[0] != 0 ? "true" : "false",
                  state[1] != 0 ? "true" : "false", state[2] != 0 ? "true" : "false");
  } else if (what == "can_save") {
    // refii_mod_can_save: Player.IsAbleToSaveGame for player P's hero.
    ok = true;
    std::snprintf(fields, sizeof(fields), "able=%s", refii_mod_can_save(&ctx, base, I(w, 3)) != 0 ? "true" : "false");
  } else if (what == "renown") {
    int32_t renown = 0;
    ok = refii_mod_renown(&ctx, base, E(w, 3), &renown) != 0;
    std::snprintf(fields, sizeof(fields), "renown=%d", renown);
  } else if (what == "morality" || what == "purity") {
    float value = 0.0f;
    ok = (what == "morality" ? refii_mod_morality(&ctx, base, E(w, 3), &value)
                             : refii_mod_purity(&ctx, base, E(w, 3), &value)) != 0;
    std::snprintf(fields, sizeof(fields), "value=%g", static_cast<double>(value));
  } else if (what == "physique") {
    float fat = 0.0f, height = 0.0f;
    ok = refii_mod_physique(base, E(w, 3), &fat, &height) != 0;
    std::snprintf(fields, sizeof(fields), "fat=%g,height=%g", static_cast<double>(fat), static_cast<double>(height));
  } else if (what == "age") {
    float age = 0.0f;
    ok = refii_mod_age(base, E(w, 3), &age) != 0;
    std::snprintf(fields, sizeof(fields), "age=%g", static_cast<double>(age));
  } else if (what == "equipped") {
    uint32_t melee = 0, ranged = 0;
    ok = refii_mod_equipped(base, E(w, 3), &melee, &ranged) != 0;
    std::snprintf(fields, sizeof(fields), "melee='0x%08x',ranged='0x%08x'", melee, ranged);
  } else if (what == "inventory_stack") {
    uint32_t definition = 0;
    int32_t count = 0;
    ok = refii_mod_inventory_stack(base, E(w, 3), I(w, 4), &definition, &count) != 0;
    std::snprintf(fields, sizeof(fields), "definition='0x%08x',count=%d", definition, count);
  } else if (what == "inventory") {
    // query ID inventory P [OFFSET]: component 13's list (+0x10 the sentinel, node+8 the item) read
    // as the pump's SampleHeroInventory reads player one's — item +4 record, +0xC count, +0x10
    // category, +0x14 viewed — for either player's hero.
    using refii::mods::LoadU32;
    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
    constexpr int32_t kInventoryPage = 40;
    const int32_t player = I(w, 3);
    const uint32_t hero = player >= 1 && player <= 2 ? refii_mod_player_hero(base, player) : 0u;
    const uint32_t inventory = hero != 0u ? refii_mod_component(base, hero, 13) : 0u;
    const uint32_t sentinel = inventory != 0u ? LoadU32(base, inventory + 0x10u) : 0u;
    ok = plausible(sentinel);
    const int32_t offset = I(w, 4) < 0 ? 0 : I(w, 4);
    int32_t total = 0, next = -1, count = 0;
    char list[1700] = {0};
    int at = 0;
    for (uint32_t node = ok ? LoadU32(base, sentinel) : 0u; plausible(node) && node != sentinel && total < 4096;
         node = LoadU32(base, node), ++total) {
      if (total < offset || next >= 0) {
        continue;
      }
      if (count >= kInventoryPage || static_cast<size_t>(at) >= sizeof(list) - 64) {
        next = total;
        continue;
      }
      const uint32_t item = LoadU32(base, node + 8u);
      if (!plausible(item)) {
        continue;
      }
      ++count;
      // u: the item's own UsableFromInventory test, what Refii.inventory's `usable` answers.
      at += std::snprintf(list + at, sizeof(list) - static_cast<size_t>(at), ",d%d='0x%08x',n%d=%d,c%d=%d,v%d=%d,u%d=%d", count,
                          LoadU32(base, item + 0x04u), count, static_cast<int32_t>(LoadU32(base, item + 0x0Cu)), count,
                          static_cast<int32_t>(LoadU32(base, item + 0x10u)), count,
                          refii::mods::LoadU8(base, item + 0x14u) != 0u ? 1 : 0, count,
                          refii_mod_inventory_usable(&ctx, base, hero, total) != 0 ? 1 : 0);
    }
    std::snprintf(fields, sizeof(fields), "total=%d,offset=%d,next=%d,count=%d%s", total, offset, next >= 0 ? next : total,
                  count, list);
  } else if (what == "expressions") {
    // query ID expressions P -> count=n,e1='0x…',u1=n,...: the performer's known expressions, at
    // most 60 a reply (a hero knows a few dozen).
    const int32_t player = I(w, 3);
    const uint32_t hero = player >= 1 && player <= 2 ? refii_mod_player_hero(base, player) : 0u;
    ok = hero != 0 && refii_mod_component(base, hero, 154) != 0;
    uint32_t record = 0;
    int32_t uses = 0, count = 0;
    char list[1700] = {0};
    int at = 0;
    for (int32_t i = 0; ok && i < 60 && refii_mod_expression(base, player, i, &record, &uses) != 0; ++i) {
      at += std::snprintf(list + at, sizeof(list) - static_cast<size_t>(at), ",e%d='0x%08x',u%d=%d", i + 1, record, i + 1, uses);
      ++count;
      if (static_cast<size_t>(at) >= sizeof(list) - 32) {
        break;
      }
    }
    std::snprintf(fields, sizeof(fields), "count=%d%s", count, list);
  } else if (what == "spells_known") {
    uint32_t mask = 0;
    ok = refii_mod_spells_known(base, I(w, 3), &mask) != 0;
    std::snprintf(fields, sizeof(fields), "mask=%u,count=%d", mask, __builtin_popcount(mask));
  } else if (what == "sprinting") {
    // refii_mod_sprinting: the sprint component's flag (+0x24) for player P's hero.
    ok = refii_mod_player_hero(base, I(w, 3)) != 0;
    std::snprintf(fields, sizeof(fields), "sprinting=%s", refii_mod_sprinting(base, I(w, 3)) != 0 ? "true" : "false");
  } else if (what == "locomotion") {
    float value = 0.0f;
    ok = refii_mod_locomotion(&ctx, base, I(w, 3), w.size() > 4 ? w[4].c_str() : "", &value) != 0;
    std::snprintf(fields, sizeof(fields), "value=%.9g", value);
  } else if (what == "parry") {
    float window = 0.0f, wedge = 0.0f;
    ok = refii_mod_parry(&ctx, base, I(w, 3), &window, &wedge) != 0;
    std::snprintf(fields, sizeof(fields), "window=%.9g,wedge=%.9g", window, wedge);
  } else if (what == "selected_expression") {
    const uint32_t record = refii_mod_selected_expression(base, I(w, 3));
    ok = record != 0;
    std::snprintf(fields, sizeof(fields), "expression='0x%08x'", record);
  } else if (what == "record_string") {
    char text[256] = {0};
    ok = refii_mod_record_string(&ctx, base, U(w, 3), w.size() > 4 ? w[4].c_str() : "", text, sizeof(text)) != 0;
    char quoted[300];
    QuoteText(text, quoted, sizeof(quoted));
    std::snprintf(fields, sizeof(fields), "value='%s'", quoted);
  } else if (what == "record_int") {
    int32_t value = 0;
    ok = refii_mod_record_int(&ctx, base, U(w, 3), w.size() > 4 ? w[4].c_str() : "", &value) != 0;
    std::snprintf(fields, sizeof(fields), "value=%d", value);
  } else if (what == "record_number") {
    float value = 0.0f;
    ok = refii_mod_record_number(&ctx, base, U(w, 3), w.size() > 4 ? w[4].c_str() : "", &value) != 0;
    std::snprintf(fields, sizeof(fields), "value=%.9g", static_cast<double>(std::isfinite(value) ? value : 0.0f));
  } else if (what == "record_bool") {
    int32_t value = 0;
    ok = refii_mod_record_bool(&ctx, base, U(w, 3), w.size() > 4 ? w[4].c_str() : "", &value) != 0;
    std::snprintf(fields, sizeof(fields), "value=%s", value != 0 ? "true" : "false");
  } else if (what == "record_exists") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "exists=%s", refii_mod_record_exists(&ctx, base, U(w, 3)) != 0 ? "true" : "false");
  } else if (what == "record_ref") {
    uint32_t value = 0u;
    ok = refii_mod_record_ref(&ctx, base, U(w, 3), w.size() > 4 ? w[4].c_str() : "", &value) != 0;
    std::snprintf(fields, sizeof(fields), "value='0x%08x'", value);
  } else if (what == "creature_tier") {
    const int32_t tier = refii_mod_creature_tier(base, E(w, 3));
    ok = tier >= 0;
    std::snprintf(fields, sizeof(fields), "tier=%d", ok ? tier : 0);
  } else if (what == "near") {
    // query ID near X Y Z R [CREATURES [OFFSET]] -> total=n,offset=o,count=k,e1=h..ek=h: up to
    // 100 handles a reply, nearest first, starting at OFFSET; ask again from offset+count for more.
    const float xyz[3] = {F(w, 3), F(w, 4), F(w, 5)};
    constexpr int32_t kNearMax = 2048;
    constexpr int32_t kNearPage = 100;
    static uint32_t found[kNearMax];
    const int32_t total = refii_mod_entities_near(&ctx, base, xyz, F(w, 6), I(w, 7), found, kNearMax);
    int32_t offset = I(w, 8);
    offset = offset < 0 ? 0 : (offset > total ? total : offset);
    const int32_t count = total - offset < kNearPage ? total - offset : kNearPage;
    ok = true;
    int at = std::snprintf(fields, sizeof(fields), "total=%d,offset=%d,count=%d", total, offset, count);
    for (int32_t i = 0; i < count && at > 0 && static_cast<size_t>(at) < sizeof(fields) - 24; ++i) {
      at += std::snprintf(fields + at, sizeof(fields) - static_cast<size_t>(at), ",e%d=%u", i + 1,
                          refii_mod_handle(found[offset + i]));
    }
  } else if (what == "line_of_sight") {
    const float from[3] = {F(w, 3), F(w, 4), F(w, 5)};
    const float to[3] = {F(w, 6), F(w, 7), F(w, 8)};
    ok = true;
    std::snprintf(fields, sizeof(fields), "clear=%s", refii_mod_line_of_sight(&ctx, base, from, to) != 0 ? "true" : "false");
  } else if (what == "spawn") {
    // query ID spawn DEF X Y Z -> entity=h (a query, so the script learns the new entity's handle)
    const float xyz[3] = {F(w, 4), F(w, 5), F(w, 6)};
    const uint32_t entity = refii_mod_spawn(&ctx, base, U(w, 3), xyz);
    ok = entity != 0;
    std::snprintf(fields, sizeof(fields), "entity=%u", refii_mod_handle(entity));
  } else if (what == "local_hero") {
    const uint32_t hero = refii_mod_local_hero(base);
    ok = hero != 0;
    std::snprintf(fields, sizeof(fields), "hero=%u", refii_mod_handle(hero));
  } else if (what == "coop_mode") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "mode=%d", refii_mod_coop_mode(base));
  } else if (what == "menu_player") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "player=%d", refii_mod_menu_player(base));
  } else if (what == "coop_state") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "local_hero=%u,coop_mode=%d,menu_player=%d,split_screen=%s",
                  refii_mod_handle(refii_mod_local_hero(base)), refii_mod_coop_mode(base), refii_mod_menu_player(base),
                  RefiiCoopSplitScreen() != 0 ? "true" : "false");
  } else if (what == "player_value") {
    // Refii.player_value(p, key [, fallback]): the value, else the fallback, else nothing.
    float value = 0.0f;
    const bool has = w.size() > 4 && refii_mod_player_get(I(w, 3), w[4].c_str(), &value) != 0;
    const bool fallback = w.size() > 5;
    if (!has && fallback) {
      value = F(w, 5);
    }
    ok = has || fallback;
    std::snprintf(fields, sizeof(fields), "value=%.9g,has=%s", static_cast<double>(value), has ? "true" : "false");
  } else if (what == "player_text") {
    // Refii.player_value_text / _hash: the typed text, and the hash it reads as.
    char text[REFII_MOD_PLAYER_TEXT_MAX + 1] = {0};
    const bool has = w.size() > 4 && refii_mod_player_text(I(w, 3), w[4].c_str(), text, sizeof(text)) >= 0;
    uint32_t hash = 0;
    const bool hashed = has && refii_mod_player_hash(I(w, 3), w[4].c_str(), &hash) != 0;
    char quoted[REFII_MOD_PLAYER_TEXT_MAX + 1];
    Quote(text, quoted, sizeof(quoted));
    char hex[16] = {0};
    if (hashed) {
      std::snprintf(hex, sizeof(hex), "0x%08x", hash);
    }
    ok = has;
    std::snprintf(fields, sizeof(fields), "text='%s',hash='%s',has=%s", quoted, hex, has ? "true" : "false");
  } else if (what == "player_here") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "here=%s", refii_mod_player_here(I(w, 3)) != 0 ? "true" : "false");
  } else if (what == "player_dog") {
    const uint32_t dog = RefiiCoopPlayerDog(base, I(w, 3));
    ok = dog != 0;
    std::snprintf(fields, sizeof(fields), "dog=%u", refii_mod_handle(dog));
  } else if (what == "focus") {
    const uint32_t target = refii_mod_focus(base, I(w, 3));
    ok = target != 0;
    std::snprintf(fields, sizeof(fields), "entity=%u", refii_mod_handle(target));
  } else if (what == "opinion") {
    float axes[5] = {0, 0, 0, 0, 0};
    ok = refii_mod_opinion(base, E(w, 3), I(w, 4), axes) != 0;
    std::snprintf(fields, sizeof(fields), "love=%.9g,fear=%.9g,attractiveness=%.9g,morality=%.9g,respect=%.9g",
                  static_cast<double>(axes[0]), static_cast<double>(axes[1]), static_cast<double>(axes[2]),
                  static_cast<double>(axes[3]), static_cast<double>(axes[4]));
  } else if (what == "discovered_crime") {
    // Refii.discovered_crime(villager, p): the villager has discovered a crime of player p's hero.
    const uint32_t hero = refii_mod_player_hero(base, I(w, 4));
    ok = hero != 0;
    std::snprintf(fields, sizeof(fields), "discovered=%s",
                  ok && refii_mod_discovered_crime(&ctx, base, E(w, 3), hero) != 0 ? "true" : "false");
  } else if (what == "perceived_renown") {
    // Refii.perceived_renown(villager, p): the renown the villager credits player p's hero with.
    int32_t renown = 0;
    ok = refii_mod_perceived_renown(&ctx, base, E(w, 3), refii_mod_player_hero(base, I(w, 4)), &renown) != 0;
    std::snprintf(fields, sizeof(fields), "renown=%d", renown);
  } else if (what == "uid") {
    // Refii.uid(e): the entity's own 64-bit UniqueID (+0x14 high, +0x18 low).
    const uint32_t entity = E(w, 3);
    ok = entity != 0;
    std::snprintf(fields, sizeof(fields), "uid='0x%08x%08x'", ok ? refii::mods::LoadU32(base, entity + 0x14u) : 0u,
                  ok ? refii::mods::LoadU32(base, entity + 0x18u) : 0u);
  } else if (what == "appearance") {
    refii_mod_appearance_t look{};
    ok = refii_mod_appearance(&ctx, base, I(w, 3), &look) != 0;
    std::snprintf(fields, sizeof(fields),
                  "automatic=%s,tan=%.9g,fat=%.9g,height=%.9g,strength=%.9g,skill=%.9g,will=%.9g,morality=%.9g,purity=%.9g,age=%.9g",
                  look.automatic != 0 ? "true" : "false", static_cast<double>(look.tan), static_cast<double>(look.fat),
                  static_cast<double>(look.height), static_cast<double>(look.strength), static_cast<double>(look.skill),
                  static_cast<double>(look.will), static_cast<double>(look.morality), static_cast<double>(look.purity),
                  static_cast<double>(look.age));
  } else if (what == "morph") {
    float weight = 0.0f;
    const bool registered = w.size() > 4 && refii_mod_morph_weight(I(w, 3), MorphWord(w[4]), &weight) != 0;
    ok = registered;
    std::snprintf(fields, sizeof(fields), "weight=%.9g,registered=%s", static_cast<double>(weight), registered ? "true" : "false");
  } else if (what == "lua_int" && w.size() >= 6) {
    // query ID lua_int <expr words...> LO HI
    std::string expr;
    for (size_t i = 3; i + 2 < w.size(); ++i) {
      expr += (i > 3 ? " " : "") + w[i];
    }
    int32_t value = 0;
    ok = refii_mod_game_lua_int(&ctx, base, expr.c_str(), I(w, w.size() - 2), I(w, w.size() - 1), &value) != 0;
    std::snprintf(fields, sizeof(fields), "value=%d", value);
  }
  return ok;
}

void RunQuery(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  char fields[kAnswerBytes] = {0};
  const bool ok = Answer(ctx, base, w, fields);
  Reply(w.size() > 1 ? w[1] : "0", ok, fields);
}

/// A menu_row word back to the name Godot sent: '-' stands for an empty one.
const char* Name(const std::vector<std::string>& w, size_t i) {
  return i < w.size() && w[i] != "-" ? w[i].c_str() : "";
}

void RaiseMenuRow(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  refii::mods::ModMenuRowEvent e{};
  e.ctx = &ctx;
  e.base = base;
  e.player = I(w, 1);
  e.menu = Name(w, 2);
  e.row = Name(w, 3);
  e.mod = Name(w, 4);
  // A row action's trailing words: the row's own command (base64, '-' for none) and the button.
  std::string command;
  if (w.size() >= 6 && w[5] != "-" && !UnbaseCommand(w[5], command)) {
    command.clear();
  }
  e.command = command.c_str();
  e.button = Name(w, 6);
  // The typed subject Godot read off the row (ModRowSubject): kind, "0x…" definition, spell.
  e.subject_kind = Name(w, 7);
  e.definition = Name(w, 8);
  e.spell = w.size() > 9 ? I(w, 9) : -1;
  refii::mods::Dispatch(e);
  // No game VM (the title, the front end): the mods' front-end Lua hears it instead.
  if (!refii::vfs::GameLuaUp(base)) {
    char menu[160];
    char row[160];
    char mod[96];
    char cmd[256];
    char button[16];
    char kind[16];
    char definition[16];
    Quote(e.menu, menu, sizeof(menu));
    Quote(e.row, row, sizeof(row));
    Quote(e.mod, mod, sizeof(mod));
    Quote(e.command, cmd, sizeof(cmd));
    Quote(e.button, button, sizeof(button));
    Quote(e.subject_kind, kind, sizeof(kind));
    Quote(e.definition, definition, sizeof(definition));
    char chunk[1024];
    std::snprintf(chunk, sizeof(chunk),
                  "if Refii and Refii._fire then Refii._fire(%d, {player=%d,menu='%s',row='%s',mod='%s',command='%s',button='%s',"
                  "subject_kind='%s',definition='%s',spell=%d}) end",
                  REFII_EVENT_MOD_MENU_ROW, e.player, menu, row, mod, cmd, button, kind, definition, e.spell);
    refii::vfs::RunFrontEndLua(ctx, base, chunk);
  }
}

// ---------------------------------------------------------------------------------------------
// play_fx UNDER THE CLASSIC LOOK: the effect is spawned as the guest's own particle instance, so
// the guest simulates it and the classic renderer draws its groups like any other the game plays.
//
// THE SPAWN IS THE GAME'S OWN SEQUENCE, the one the particle component's start `sub_82569C28`
// runs (and fourteen other spawn sites with it), call for call:
//
//   82569c78  bl sub_8322FDC8   the effect's type collection from the registry (dword_8349E568),
//                               by the bank key: lhResourceName_ComputeHash of the lowered name
//   82569c80  lwz r11,var_30 / beq   an unknown key spawns nothing
//   82569c8c  li r3,0x48 / bl Render_AllocD3DObject   (operator new 0x8221F258)
//   82569c9c  bl sub_83233300   CParticleSystemEntityInstanceCollection(type): its entities, +44 = -1
//   82569cb0  bl sub_82273058   the owner's smart pointer takes it (a fresh count block)
//   82569ce8  lwarx/addi 1/stwcx.  a counted copy of that pointer ...
//   82569d00  bl sub_83234D78   ... handed to the manager, which lists it (dword_8349E480, 28-byte
//                               records), writes its slot at instance+44 and releases the copy
//   82569d18  bl sub_822B86C0   the world's own list takes a reference (*(*(dword_83496AB8+12)+88))
//   82569d28  bl sub_821B7B18   the type reference released
//
// The component also writes +40 (its interface) and +4 (the hash); +40 stays 0 here, the state the
// host bridge already reads as "spell-owned" (no emitter component).
//
// WHAT THE OPTIONS ARE ON THE INSTANCE: position `sub_832325A8(inst, v1, v2)` (v1's w is replaced by
// 1.0 at 0x83232624), scale `sub_83232290(inst, f1)` — every element's vtable slot 11, the setter
// behind ParticleAttacher.SetParticleScale (0x8276724c) — and intensity `sub_832323E0(inst, f1)`,
// instance+52 and every emitter's +408, behind SetParticleAlpha (0x8276701c). There is no colour
// on an instance (the constructor initialises +48, +52 and +68 as single floats; a particle's
// colour is its system's material keyframe table), so a TINT is the instance's emitters pointed at
// a tinted copy of that material (particle_dress.cpp TintFxInstance).
//
// END OF LIFE: `sub_83232048(inst, frames)` sets every element's +0xC0 = +0xC4 = frames; zero fails
// the liveness test at once and the retire pass `sub_83234338` drops the instance, writing -1 back
// to its +44 (0x832347c8). That word is how a one-shot is known to have run out.
//
// A PLAY ON AN ENTITY STANDS IN THE ENTITY'S FRAME, placed the way the guest places its own FX
// entity's instance (sub_825F8900 TU1 / sub_824B8938 disc, identical sequences):
//
//   825f8ac0  lwz r4,0x7C(r30)          the entity's placement component
//   825f8ad0  lwz r10,0x40(r11) / bctrl  its position (sret: buffer in r3, `this` in r4)
//   825f8ae8  lwz r8,0x44(r9) / bctrl    its orientation, the same way; r3 back = the quaternion
//   825f8b04  ld r5,0(r7) / ld r4,8(r7)  its two halves, re-stored and reloaded as r4 = bytes 0..7,
//   825f8b30  ld r4,.. / ld r5,..        r5 = bytes 8..15
//   825f8b2c  bl sub_832325A8           the position on the instance, then
//   825f8b3c  bl sub_83231C40           the orientation: each emitter's sub_83228D78 stores r4:r5
//                                       at emitter+0x10 and recomposes its matrix at +0x20 about
//                                       the position already at +0x50
//
// The per-tick emitter writers (sub_8322E4D0, sub_83230880) rewrite only the matrix's row 3
// (+0x50, `li r7,0x50 / stvx128` 0x8323091c), so the orientation stands until it is stated again.
// The quaternion is (x, y, z, w) and its rows are the entity's local axes (sub_8220C8F0, the form
// GameEntities.ToGodot draws the same entity with), so the offset is laid along them.
namespace guest_fx {

constexpr uint32_t kOperatorNew = 0x8221F258u;
constexpr uint32_t kOperatorDelete = 0x8221BD38u;
constexpr uint32_t kRefRelease = 0x821B7B18u;
constexpr uint32_t kRefReset = 0x82273058u;
constexpr uint32_t kEffectType = 0x8322FDC8u;
constexpr uint32_t kInstanceCtor = 0x83233300u;
constexpr uint32_t kManagerAdd = 0x83234D78u;
constexpr uint32_t kWorldHold = 0x822B86C0u;
constexpr uint32_t kSetPosition = 0x832325A8u;
constexpr uint32_t kSetOrientation = 0x83231C40u;
constexpr uint32_t kEntityPlacement = 0x7Cu;
constexpr uint32_t kPlacementOrientationSlot = 0x44u;
constexpr uint32_t kSetScale = 0x83232290u;
constexpr uint32_t kSetIntensity = 0x832323E0u;
constexpr uint32_t kFade = 0x83232048u;
constexpr uint32_t kGameRoot = 0x83496AB8u;
constexpr uint32_t kInstanceBytes = 0x48u;
constexpr uint32_t kManagerSlot = 44u;

struct Played {
  uint32_t holder = 0;  // our smart pointer {instance, count block}, 8 bytes of guest heap
  uint32_t hash = 0;
  uint32_t entity = 0;  // a handle, 0 for a world position
  float at[3] = {0.0f, 0.0f, 0.0f};
  float offset[3] = {0.0f, 0.0f, 0.0f};
  float scale = 1.0f;
  float intensity = 1.0f;
  float tint[3] = {1.0f, 1.0f, 1.0f};
  bool loop = true;
};

// The guest tick thread only.
std::unordered_map<uint32_t, Played> g_played;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

bool Call(PPCContext& work, uint8_t* base, uint32_t tu1) {
  return refii::mods::CallGuest(work, base, refii::guest::Address(tu1));
}

uint32_t Instance(uint8_t* base, uint32_t holder) {
  return holder != 0 ? refii::mods::LoadU32(base, holder) : 0u;
}

/// The count word of a guest smart pointer's block, raised the way the game raises it (lwarx/stwcx.).
void Retain(uint8_t* base, uint32_t block) {
  auto* word = reinterpret_cast<std::atomic<uint32_t>*>(base + block);
  uint32_t seen = word->load(std::memory_order_relaxed);
  while (!word->compare_exchange_weak(seen, __builtin_bswap32(__builtin_bswap32(seen) + 1u),
                                      std::memory_order_acq_rel)) {
  }
}

void Release(const PPCContext& frame, uint8_t* base, uint32_t ref) {
  PPCContext work = frame;
  work.r3.u32 = ref;
  Call(work, base, kRefRelease);
}

/// Our holder: released, then its 8 bytes returned to the guest heap.
void Drop(const PPCContext& ctx, uint8_t* base, uint32_t holder) {
  if (holder == 0) {
    return;
  }
  const PPCContext frame = refii::mods::Frame(ctx, 0x300u);
  Release(frame, base, holder);
  PPCContext work = frame;
  work.r3.u32 = holder;
  Call(work, base, kOperatorDelete);
}

/// The game's spawn sequence (above). Returns our holder, or 0 when the bank has no such effect.
uint32_t Spawn(const PPCContext& ctx, uint8_t* base, uint32_t hash) {
  using refii::mods::LoadU32;
  using refii::mods::StoreU32;
  const PPCContext frame = refii::mods::Frame(ctx, 0x300u);
  const uint32_t type_ref = (frame.r1.u32 + 0x100u) & ~0xFu;
  const uint32_t copy = type_ref + 16u;
  StoreU32(base, type_ref, 0u);
  StoreU32(base, type_ref + 4u, 0u);

  PPCContext work = frame;
  work.r3.u32 = type_ref;
  work.r4.u32 = hash;
  if (!Call(work, base, kEffectType) || LoadU32(base, type_ref) == 0u) {
    Release(frame, base, type_ref);
    return 0u;
  }

  work = frame;
  work.r3.u64 = 8u;
  const uint32_t holder = Call(work, base, kOperatorNew) ? work.r3.u32 : 0u;
  work = frame;
  work.r3.u64 = kInstanceBytes;
  const uint32_t memory = Plausible(holder) && Call(work, base, kOperatorNew) ? work.r3.u32 : 0u;
  if (!Plausible(holder) || !Plausible(memory)) {
    Release(frame, base, type_ref);
    if (Plausible(holder)) {
      work = frame;
      work.r3.u32 = holder;
      Call(work, base, kOperatorDelete);
    }
    return 0u;
  }
  StoreU32(base, holder, 0u);
  StoreU32(base, holder + 4u, 0u);

  work = frame;
  work.r3.u32 = memory;
  work.r4.u32 = type_ref;
  Call(work, base, kInstanceCtor);
  const uint32_t instance = work.r3.u32;

  work = frame;
  work.r3.u32 = holder;
  work.r4.u32 = instance;
  Call(work, base, kRefReset);

  const uint32_t block = LoadU32(base, holder + 4u);
  StoreU32(base, copy, LoadU32(base, holder));
  StoreU32(base, copy + 4u, block);
  if (block != 0u) {
    Retain(base, block);
  }
  work = frame;
  work.r3.u32 = copy;
  Call(work, base, kManagerAdd);

  const uint32_t root = LoadU32(base, refii::guest::Address(kGameRoot));
  const uint32_t game = Plausible(root) ? LoadU32(base, root + 12u) : 0u;
  const uint32_t world = Plausible(game) ? LoadU32(base, game + 88u) : 0u;
  if (Plausible(world)) {
    work = frame;
    work.r3.u32 = world;
    work.r4.u32 = holder;
    Call(work, base, kWorldHold);
  }

  Release(frame, base, type_ref);
  return holder;
}

void SetFloat(const PPCContext& ctx, uint8_t* base, uint32_t instance, uint32_t setter, float value) {
  PPCContext work = refii::mods::Frame(ctx, 0x300u);
  work.r3.u32 = instance;
  work.f1.f64 = value;
  Call(work, base, setter);
}

void Place(const PPCContext& ctx, uint8_t* base, uint32_t instance, const float* xyz) {
  PPCContext work = refii::mods::Frame(ctx, 0x300u);
  const float still[3] = {0.0f, 0.0f, 0.0f};
  work.r3.u32 = instance;
  refii::mods::SetVector(work, 1, xyz);
  refii::mods::SetVector(work, 2, still);
  Call(work, base, kSetPosition);
}

/// Where an instance stands: a point, and for a play on an entity that entity's orientation as
/// its placement states it (four raw words, x y z w), when it states one that is a rotation.
struct Stand {
  float xyz[3] = {0.0f, 0.0f, 0.0f};
  uint32_t quaternion[4] = {0u, 0u, 0u, 0u};
  bool turned = false;
};

/// The entity's orientation through its placement component's slot 0x44, as sub_825F8900 asks
/// for it (above). False when it has no placement or the answer is not a unit quaternion.
bool Orientation(const PPCContext& ctx, uint8_t* base, uint32_t entity, uint32_t* words, float* q) {
  using refii::mods::LoadU32;
  const uint32_t placement = Plausible(entity) ? LoadU32(base, entity + kEntityPlacement) : 0u;
  const uint32_t vtable = Plausible(placement) ? LoadU32(base, placement) : 0u;
  if (!Plausible(vtable)) {
    return false;
  }
  PPCContext work = refii::mods::Frame(ctx, 0x300u);
  const uint32_t out = (work.r1.u32 + 0x100u) & ~0xFu;
  work.r3.u32 = out;
  work.r4.u32 = placement;
  if (!refii::mods::CallGuest(work, base, LoadU32(base, vtable + kPlacementOrientationSlot))) {
    return false;
  }
  const uint32_t at = Plausible(work.r3.u32) ? work.r3.u32 : out;
  float length = 0.0f;
  for (uint32_t i = 0; i < 4u; ++i) {
    words[i] = LoadU32(base, at + 4u * i);
    std::memcpy(&q[i], &words[i], sizeof(float));
    length += q[i] * q[i];
  }
  return std::isfinite(length) && length > 0.98f && length < 1.02f;
}

void Place(const PPCContext& ctx, uint8_t* base, uint32_t instance, const Stand& at) {
  Place(ctx, base, instance, at.xyz);
  if (!at.turned) {
    return;
  }
  PPCContext work = refii::mods::Frame(ctx, 0x300u);
  work.r3.u32 = instance;
  work.r4.u64 = (static_cast<uint64_t>(at.quaternion[0]) << 32) | at.quaternion[1];
  work.r5.u64 = (static_cast<uint64_t>(at.quaternion[2]) << 32) | at.quaternion[3];
  Call(work, base, kSetOrientation);
}

void Kill(const PPCContext& ctx, uint8_t* base, uint32_t instance) {
  PPCContext work = refii::mods::Frame(ctx, 0x300u);
  work.r3.u32 = instance;
  work.r4.u32 = 0u;
  Call(work, base, kFade);
}

/// Where the effect stands this tick: in its entity's frame (the entity's position, the offset
/// along its axes, its orientation), or at its stated point. An entity whose placement states no
/// rotation keeps the offset in world axes. False when its entity is gone.
bool Where(PPCContext& ctx, uint8_t* base, const Played& p, Stand& at) {
  at = Stand{};
  float* xyz = at.xyz;
  if (p.entity == 0u) {
    xyz[0] = p.at[0];
    xyz[1] = p.at[1];
    xyz[2] = p.at[2];
    return true;
  }
  const uint32_t entity = refii_mod_handle_address(p.entity);
  if (entity == 0u || refii_mod_entity_position(&ctx, base, entity, xyz) == 0) {
    return false;
  }
  float q[4];
  at.turned = Orientation(ctx, base, entity, at.quaternion, q);
  if (!at.turned) {
    for (int i = 0; i < 3; ++i) {
      xyz[i] += p.offset[i];
    }
    return true;
  }
  // The rows of sub_8220C8F0's matrix (s = 2/|q|^2): the entity's local x, y and z.
  const float x = q[0], y = q[1], z = q[2], w = q[3];
  const float s = 2.0f / (x * x + y * y + z * z + w * w);
  const float rows[3][3] = {
      {1.0f - s * (y * y + z * z), s * (x * y + z * w), s * (x * z - y * w)},
      {s * (x * y - z * w), 1.0f - s * (x * x + z * z), s * (y * z + x * w)},
      {s * (x * z + y * w), s * (y * z - x * w), 1.0f - s * (x * x + y * y)},
  };
  for (int i = 0; i < 3; ++i) {
    xyz[i] += p.offset[0] * rows[0][i] + p.offset[1] * rows[1][i] + p.offset[2] * rows[2][i];
  }
  return true;
}

/// Spawns `p` where it stands and states its options; false when nothing was spawned.
bool Start(PPCContext& ctx, uint8_t* base, Played& p) {
  Stand xyz;
  if (!Where(ctx, base, p, xyz)) {
    return false;
  }
  p.holder = Spawn(ctx, base, p.hash);
  const uint32_t instance = Instance(base, p.holder);
  if (instance == 0u) {
    Drop(ctx, base, p.holder);
    p.holder = 0u;
    return false;
  }
  Place(ctx, base, instance, xyz);
  if (p.scale != 1.0f) {
    SetFloat(ctx, base, instance, kSetScale, p.scale);
  }
  SetFloat(ctx, base, instance, kSetIntensity, p.intensity);
  // On top of the effect's own tint, which the constructor hook has already put on its material.
  refii::vfs::TintFxInstance(ctx, base, instance, p.tint);
  return true;
}

void Stop(PPCContext& ctx, uint8_t* base, uint32_t id) {
  auto found = g_played.find(id);
  if (found == g_played.end()) {
    return;
  }
  const uint32_t instance = Instance(base, found->second.holder);
  if (instance != 0u && refii::mods::LoadU32(base, instance + kManagerSlot) != 0xFFFFFFFFu) {
    Kill(ctx, base, instance);
  }
  Drop(ctx, base, found->second.holder);
  g_played.erase(found);
}

void Play(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  if (w.size() < 13) {
    return;
  }
  const uint32_t id = U(w, 1);
  Stop(ctx, base, id);
  Played p;
  p.hash = U(w, 2);
  p.at[0] = F(w, 3);
  p.at[1] = F(w, 4);
  p.at[2] = F(w, 5);
  p.entity = U(w, 6);
  p.offset[0] = F(w, 7);
  p.offset[1] = F(w, 8);
  p.offset[2] = F(w, 9);
  p.scale = F(w, 10);
  p.intensity = F(w, 11);
  p.loop = I(w, 12) != 0;
  if (w.size() >= 16) {
    for (int c = 0; c < 3; ++c) {
      const float v = F(w, 13 + static_cast<size_t>(c));
      p.tint[c] = v >= 0.0f ? v : 0.0f;
    }
  }
  if (id == 0u || p.hash == 0u || !Start(ctx, base, p)) {
    return;
  }
  g_played.emplace(id, p);
}

/// Once a tick: an entity's effect follows it, a retired one-shot is let go, a retired loop spawns
/// again, and one whose entity is gone ends with it.
void Pump(PPCContext& ctx, uint8_t* base) {
  std::vector<uint32_t> done;
  for (auto& [id, p] : g_played) {
    const uint32_t instance = Instance(base, p.holder);
    const bool retired =
        instance == 0u || refii::mods::LoadU32(base, instance + kManagerSlot) == 0xFFFFFFFFu;
    Stand xyz;
    const bool placed = Where(ctx, base, p, xyz);
    if (!placed) {
      done.push_back(id);
      continue;
    }
    if (retired) {
      Drop(ctx, base, p.holder);
      p.holder = 0u;
      if (!p.loop || !Start(ctx, base, p)) {
        done.push_back(id);
      }
      continue;
    }
    if (p.entity != 0u) {
      Place(ctx, base, instance, xyz);
    }
  }
  for (uint32_t id : done) {
    Stop(ctx, base, id);
  }
}

/// The plays riding `entity`, placed where it stands now: called from inside the update in which
/// the guest moves that entity and its own effects (spell_fx_owner.cpp), so a ridden play stands
/// where the guest put them in the same tick rather than where the entity stood when Pump ran.
void PlaceRiding(PPCContext& ctx, uint8_t* base, uint32_t entity) {
  if (entity == 0u || g_played.empty()) {
    return;
  }
  for (auto& [id, p] : g_played) {
    if (p.entity == 0u || refii_mod_handle_address(p.entity) != entity) {
      continue;
    }
    const uint32_t instance = Instance(base, p.holder);
    if (instance == 0u || refii::mods::LoadU32(base, instance + kManagerSlot) == 0xFFFFFFFFu) {
      continue;
    }
    Stand xyz;
    if (Where(ctx, base, p, xyz)) {
      Place(ctx, base, instance, xyz);
    }
  }
}

}  // namespace guest_fx

void RaiseMessageBox(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  refii::mods::ModMessageBoxEvent e{};
  e.ctx = &ctx;
  e.base = base;
  e.id = U(w, 1);
  e.button = I(w, 2);
  e.player = I(w, 3);
  refii::mods::Dispatch(e);
  if (!refii::vfs::GameLuaUp(base)) {
    char chunk[256];
    std::snprintf(chunk, sizeof(chunk), "if Refii and Refii._fire then Refii._fire(%d, {id=%u,button=%d,player=%d}) end",
                  REFII_EVENT_MOD_MESSAGE_BOX, e.id, e.button, e.player);
    refii::vfs::RunFrontEndLua(ctx, base, chunk);
  }
}

void RaiseNotice(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  refii::mods::ModNoticeEvent e{};
  e.ctx = &ctx;
  e.base = base;
  e.id = U(w, 1);
  e.outcome = I(w, 2);
  e.player = I(w, 3);
  refii::mods::Dispatch(e);
  if (!refii::vfs::GameLuaUp(base)) {
    char chunk[256];
    std::snprintf(chunk, sizeof(chunk), "if Refii and Refii._fire then Refii._fire(%d, {id=%u,outcome=%d,player=%d}) end",
                  REFII_EVENT_MOD_NOTICE, e.id, e.outcome, e.player);
    refii::vfs::RunFrontEndLua(ctx, base, chunk);
  }
}

/// The keyboard's attack button mode per player as Godot last stated it (GameInput's 1 / 2 / 3), 0
/// for a player no keyboard plays on this machine.
std::atomic<int32_t> g_attack_mode[2] = {0, 0};

void StateAttackMode(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  const int32_t player = I(w, 1);
  const int32_t mode = I(w, 2);
  if (player < 1 || player > 2 || mode < 0 || mode > 3 || g_attack_mode[player - 1].exchange(mode) == mode) {
    return;
  }
  refii::mods::AttackModeChangedEvent e{};
  e.ctx = &ctx;
  e.base = base;
  e.player = player;
  e.mode = mode;
  refii::mods::Dispatch(e);
}

void RaiseMenuBox(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  refii::mods::ModMenuBoxEvent e{};
  e.ctx = &ctx;
  e.base = base;
  e.id = U(w, 1);
  e.option = I(w, 2);
  e.player = I(w, 3);
  refii::mods::Dispatch(e);
  if (!refii::vfs::GameLuaUp(base)) {
    char chunk[256];
    std::snprintf(chunk, sizeof(chunk), "if Refii and Refii._fire then Refii._fire(%d, {id=%u,option=%d,player=%d}) end",
                  REFII_EVENT_MOD_MENU_BOX, e.id, e.option, e.player);
    refii::vfs::RunFrontEndLua(ctx, base, chunk);
  }
}

/// THE STICKY WHEEL STATEMENT for a Godot script (`wheel_switches_target E ON`): a native or Lua
/// handler restates refii_mod_wheel_switches_target on every guest tick it holds its lock, and a
/// statement lapses two ticks after it was made (mouse_look.cpp kWheelStatementTicks). A Godot
/// script runs off the tick, so its ON is kept here, per hero, and restated by RunModCommands on
/// every tick until the script says OFF or the hero's player no longer holds a lock
/// (refii_mod_locked_target 0: the game's own release, or release_lock).
struct WheelSticky {
  uint32_t hero = 0u;
};

std::array<WheelSticky, 4> g_wheel_sticky{};

void StateWheelSwitches(PPCContext& ctx, uint8_t* base, uint32_t hero, bool on) {
  if (hero == 0u) {
    return;
  }
  WheelSticky* slot = nullptr;
  for (WheelSticky& sticky : g_wheel_sticky) {
    if (sticky.hero == hero) {
      slot = &sticky;
      break;
    }
    if (slot == nullptr && sticky.hero == 0u) {
      slot = &sticky;
    }
  }
  if (!on) {
    if (slot != nullptr && slot->hero == hero) {
      slot->hero = 0u;
    }
    return;
  }
  if (slot == nullptr) {
    return;  // four heroes already stated: more than any session has players
  }
  slot->hero = hero;
  refii_mod_wheel_switches_target(base, hero);
  (void)ctx;
}

/// Every tick: each sticky hero's statement made again while its player's lock holds.
void RestateWheelSwitches(PPCContext& ctx, uint8_t* base) {
  for (WheelSticky& sticky : g_wheel_sticky) {
    if (sticky.hero == 0u) {
      continue;
    }
    const int32_t player = refii_mod_player_of(base, sticky.hero);
    if (player == 0 || refii_mod_locked_target(&ctx, base, player) == 0u) {
      sticky.hero = 0u;
      continue;
    }
    refii_mod_wheel_switches_target(base, sticky.hero);
  }
}

void RunOne(PPCContext& ctx, uint8_t* base, const char* line) {
  const bool chunk = std::strncmp(line, "run_lua ", 8) == 0 || std::strncmp(line, "frontend_lua ", 13) == 0;
  const std::vector<std::string> w = Split(line, chunk ? 1 : 64);
  if (w.empty()) {
    return;
  }
  const std::string& op = w[0];
  if (refii::vfs::RunModAbilityCommand(ctx, base, w)) {
    return;
  }
  if (op == "query") {
    RunQuery(ctx, base, w);
  } else if (op == "give_gold") {
    refii_mod_give_gold(&ctx, base, E(w, 1), I(w, 2));
  } else if (op == "modify_health") {
    refii_mod_modify_health(&ctx, base, E(w, 1), F(w, 2), E(w, 3));
  } else if (op == "set_health") {
    refii_mod_set_health(base, E(w, 1), F(w, 2));
  } else if (op == "add_item") {
    refii_mod_add_items(&ctx, base, E(w, 1), U(w, 2), w.size() >= 4 ? I(w, 3) : 1, nullptr);
  } else if (op == "give_experience") {
    refii_mod_give_experience(&ctx, base, E(w, 1), I(w, 2), F(w, 3));
  } else if (op == "spend_experience") {
    refii_mod_spend_experience(&ctx, base, E(w, 1), I(w, 2), F(w, 3));
  } else if (op == "modify_renown") {
    refii_mod_modify_renown(&ctx, base, E(w, 1), I(w, 2));
  } else if (op == "modify_morality") {
    refii_mod_modify_morality(&ctx, base, E(w, 1), F(w, 2));
  } else if (op == "modify_purity") {
    refii_mod_modify_purity(&ctx, base, E(w, 1), F(w, 2));
  } else if (op == "save_game") {
    refii_mod_save_game(&ctx, base, I(w, 1));
  } else if (op == "cast_spell") {
    const float pos[3] = {F(w, 6), F(w, 7), F(w, 8)};
    const float dir[3] = {F(w, 9), F(w, 10), F(w, 11)};
    refii_mod_cast_spell(&ctx, base, E(w, 1), I(w, 2), I(w, 3), E(w, 4), I(w, 5), w.size() >= 9 ? pos : nullptr,
                         w.size() >= 12 ? dir : nullptr);
  } else if (op == "cast_spell_with" && w.size() >= 8) {
    // The positional words end at a trailing `at E` (REFII_MOD_CAST_AT_ENTITY) or `at X Y Z`
    // (REFII_MOD_CAST_AT_POINT).
    const size_t at = static_cast<size_t>(std::find(w.begin() + 8, w.end(), "at") - w.begin());
    const float pos[3] = {F(w, 8), F(w, 9), F(w, 10)};
    const float dir[3] = {F(w, 11), F(w, 12), F(w, 13)};
    refii_mod_cast_options options{};
    options.tag = I(w, 6);
    options.damage_scale = F(w, 7);
    if (at + 2 == w.size()) {
      options.at_kind = REFII_MOD_CAST_AT_ENTITY;
      options.at_entity = E(w, at + 1);
    } else if (at + 4 == w.size()) {
      options.at_kind = REFII_MOD_CAST_AT_POINT;
      options.at_point[0] = F(w, at + 1);
      options.at_point[1] = F(w, at + 2);
      options.at_point[2] = F(w, at + 3);
    }
    refii_mod_cast_spell_with(&ctx, base, E(w, 1), I(w, 2), I(w, 3), E(w, 4), I(w, 5), at >= 11 ? pos : nullptr,
                              at >= 14 ? dir : nullptr, &options);
  } else if (op == "set_position") {
    const float xyz[3] = {F(w, 2), F(w, 3), F(w, 4)};
    refii_mod_set_position(&ctx, base, E(w, 1), xyz);
  } else if (op == "set_facing") {
    const float xyz[3] = {F(w, 2), F(w, 3), w.size() >= 5 ? F(w, 4) : 0.0f};
    refii_mod_set_facing(&ctx, base, E(w, 1), xyz);
  } else if (op == "entity_tint" && w.size() >= 5) {
    refii_mod_entity_tint(&ctx, base, E(w, 1), F(w, 2), F(w, 3), F(w, 4), w.size() >= 6 ? F(w, 5) : 1.0f);
  } else if (op == "entity_untint") {
    refii_mod_entity_untint(&ctx, base, E(w, 1));
  } else if (op == "entity_alpha" && w.size() >= 3) {
    refii_mod_entity_alpha(&ctx, base, E(w, 1), F(w, 2));
  } else if (op == "entity_scale" && w.size() >= 3) {
    refii_mod_entity_scale(&ctx, base, E(w, 1), F(w, 2));
  } else if (op == "entity_saturation_mask" && w.size() >= 3) {
    refii_mod_entity_saturation_mask(&ctx, base, E(w, 1), I(w, 2));
  } else if (op == "entity_drawable" && w.size() >= 3) {
    refii_mod_entity_drawable(&ctx, base, E(w, 1), I(w, 2));
  } else if (op == "wheel_switches_target" && w.size() >= 3) {
    StateWheelSwitches(ctx, base, E(w, 1), I(w, 2) != 0);
  } else if (op == "lock_target" && w.size() >= 2) {
    refii_mod_lock_target(&ctx, base, I(w, 1), w.size() >= 3 ? E(w, 2) : 0u);
  } else if (op == "release_lock" && w.size() >= 2) {
    refii_mod_release_lock(&ctx, base, I(w, 1));
  } else if (op == "switch_target" && w.size() >= 3) {
    refii_mod_switch_target(&ctx, base, I(w, 1), I(w, 2));
  } else if (op == "use_item" && w.size() >= 3) {
    refii_mod_quick_action(&ctx, base, I(w, 1), 1, U(w, 2), U(w, 3) & REFII_QUICK_UNGATED);
  } else if (op == "equip_weapon" && w.size() >= 3) {
    refii_mod_quick_action(&ctx, base, I(w, 1), 3, U(w, 2), U(w, 3) & REFII_QUICK_UNGATED);
  } else if (op == "perform_expression" && w.size() >= 3) {
    refii_mod_quick_action(&ctx, base, I(w, 1), 2, U(w, 2), U(w, 3));
  } else if (op == "charge_spell" && w.size() >= 4) {
    refii_mod_charge_spell(&ctx, base, I(w, 1), I(w, 2), I(w, 3));
  } else if (op == "assign_spell" && w.size() >= 3) {
    if (I(w, 2) >= 0) {
      refii_mod_quick_action(&ctx, base, I(w, 1), 4, static_cast<uint32_t>(I(w, 2)), U(w, 3) & REFII_QUICK_UNGATED);
    }
  } else if (op == "release_expression" && w.size() >= 2) {
    refii_mod_release_expression(&ctx, base, I(w, 1));
  } else if (op == "end_sprint" && w.size() >= 2) {
    refii_mod_end_sprint(&ctx, base, I(w, 1));
  } else if (op == "set_locomotion" && w.size() >= 4) {
    refii_mod_set_locomotion(&ctx, base, I(w, 1), w[2].c_str(), w[3] == "-" ? NAN : F(w, 3));
  } else if (op == "set_parry" && w.size() >= 4) {
    refii_mod_set_parry(&ctx, base, I(w, 1), F(w, 2), F(w, 3));
  } else if (op == "apply_status" && w.size() >= 4) {
    refii_mod_apply_status(&ctx, base, E(w, 1), I(w, 2), F(w, 3), w.size() >= 5 ? I(w, 4) : 0,
                           w.size() >= 6 ? E(w, 5) : 0u);
  } else if (op == "clear_status" && w.size() >= 3) {
    refii_mod_clear_status(&ctx, base, E(w, 1), I(w, 2));
  } else if (op == "knockback" && w.size() >= 5) {
    const float dir[3] = {F(w, 2), F(w, 3), F(w, 4)};
    refii_mod_knockback(&ctx, base, E(w, 1), dir, w.size() >= 6 ? F(w, 5) : 1.0f);
  } else if (op == "stagger" && w.size() >= 2) {
    refii_mod_stagger(&ctx, base, E(w, 1));
  } else if (op == "set_speed_multiplier" && w.size() >= 3) {
    refii_mod_set_speed_multiplier(base, I(w, 1), F(w, 2));
  } else if (op == "clear_speed_multiplier" && w.size() >= 2) {
    refii_mod_clear_speed_multiplier(base, I(w, 1));
  } else if (op == "allow_action_interrupt" && w.size() >= 2) {
    refii_mod_allow_action_interrupt(base, E(w, 1));
  } else if (op == "allow_action_interrupt_at" && w.size() >= 3) {
    refii_mod_allow_action_interrupt_at(&ctx, base, E(w, 1), F(w, 2));
  } else if (op == "play_sound" && w.size() >= 3) {
    refii_mod_play_sound_on(&ctx, base, E(w, 2), w[1].c_str(), Tag(w, 3));
  } else if (op == "play_sound_at" && w.size() >= 5) {
    const float xyz[3] = {F(w, 2), F(w, 3), F(w, 4)};
    refii_mod_play_sound_at(&ctx, base, xyz, w[1].c_str(), Tag(w, 5));
  } else if (op == "stop_sound" && w.size() >= 3) {
    refii_mod_stop_sound(&ctx, base, E(w, 1), Tag(w, 2), w.size() >= 4 ? I(w, 3) : 0);
  } else if (op == "play_music" && w.size() >= 2) {
    refii_mod_play_music(&ctx, base, w[1].c_str());
  } else if (op == "stop_music" && w.size() >= 2) {
    refii_mod_stop_music(&ctx, base, w[1].c_str());
  } else if (op == "play_animation" && w.size() >= 3) {
    refii_mod_play_animation(&ctx, base, E(w, 1), w[2].c_str(), w.size() >= 4 ? I(w, 3) : 1,
                             w.size() >= 5 ? F(w, 4) : 0.1f, w.size() >= 6 ? F(w, 5) : 1.0f);
  } else if (op == "stop_animation" && w.size() >= 2) {
    refii_mod_stop_animation(&ctx, base, E(w, 1));
  } else if (op == "modify_opinion" && w.size() >= 5) {
    refii_mod_modify_opinion(&ctx, base, E(w, 1), E(w, 2), I(w, 3), F(w, 4));
  } else if (op == "add_deed" && w.size() >= 4) {
    refii_mod_add_deed(&ctx, base, E(w, 1), E(w, 2), U(w, 3));
  } else if (op == "npc_follow" && w.size() >= 3) {
    refii_mod_npc_follow(&ctx, base, E(w, 1), E(w, 2), w.size() >= 4 ? F(w, 3) : 2.0f);
  } else if (op == "npc_go_to" && w.size() >= 5) {
    const float xyz[3] = {F(w, 2), F(w, 3), F(w, 4)};
    refii_mod_npc_go_to(&ctx, base, E(w, 1), xyz);
  } else if (op == "npc_flee" && w.size() >= 3) {
    refii_mod_npc_flee(&ctx, base, E(w, 1), E(w, 2));
  } else if (op == "npc_attack" && w.size() >= 3) {
    refii_mod_npc_attack(&ctx, base, E(w, 1), E(w, 2));
  } else if (op == "npc_set_hostile" && w.size() >= 4) {
    refii_mod_npc_set_hostile(&ctx, base, E(w, 1), E(w, 2), I(w, 3) != 0 ? 1 : 0);
  } else if (op == "npc_stop" && w.size() >= 2) {
    refii_mod_npc_stop(&ctx, base, E(w, 1));
  } else if (op == "set_time_of_day" && w.size() >= 2) {
    refii_mod_set_time_of_day(&ctx, base, F(w, 1));
  } else if (op == "set_weather" && w.size() >= 2) {
    refii_mod_set_weather(&ctx, base, w[1].c_str(), w.size() >= 3 ? F(w, 2) : 1.0f, w.size() >= 4 ? F(w, 3) : 1.0f);
  } else if (op == "appearance_set" && w.size() >= 2) {
    refii_mod_appearance_t look{};
    for (float refii_mod_appearance_t::* field : kAppearanceFields) {
      look.*field = NAN;
    }
    for (size_t i = 2; i < w.size(); ++i) {
      const size_t equals = w[i].find('=');
      for (size_t f = 0; equals != std::string::npos && f < std::size(kAppearanceNames); ++f) {
        if (w[i].compare(0, equals, kAppearanceNames[f]) == 0) {
          look.*kAppearanceFields[f] = std::strtof(w[i].c_str() + equals + 1, nullptr);
        }
      }
    }
    refii_mod_set_appearance(&ctx, base, I(w, 1), &look);
  } else if (op == "morph_set" && w.size() >= 4) {
    refii_mod_set_morph_weight(I(w, 1), MorphWord(w[2]), F(w, 3));
  } else if (op == "appearance_automatic" && w.size() >= 3) {
    refii_mod_set_appearance_automatic(&ctx, base, I(w, 1), I(w, 2) != 0 ? 1 : 0);
  } else if (op == "appearance_state" && w.size() >= 3 + std::size(kAppearanceFields)) {
    refii_mod_appearance_t look{};
    for (size_t f = 0; f < std::size(kAppearanceFields); ++f) {
      look.*kAppearanceFields[f] = F(w, 3 + f);
    }
    refii::vfs::AppearanceState(I(w, 1), I(w, 2) != 0, look);
  } else if (op == "player_set" && w.size() >= 4) {
    refii_mod_player_set(I(w, 1), w[2].c_str(), F(w, 3));
  } else if (op == "player_add" && w.size() >= 4) {
    refii_mod_player_add(I(w, 1), w[2].c_str(), F(w, 3), I(w, 4) != 0 ? 1 : 0);
  } else if (op == "player_save" && w.size() >= 4) {
    refii_mod_player_save(I(w, 1), w[2].c_str(), F(w, 3));
  } else if ((op == "player_set_text" || op == "player_save_text") && w.size() >= 4) {
    std::string text;
    if (w[3] == "-" || UnbaseCommand(w[3], text)) {
      (op == "player_save_text" ? refii_mod_player_save_text : refii_mod_player_set_text)(I(w, 1), w[2].c_str(),
                                                                                          text.c_str());
    }
  } else if (op == "save_set" && w.size() >= 3) {
    refii_mod_save_set_int(&ctx, base, w[1].c_str(), I(w, 2));
  } else if (op == "forgive_crimes") {
    refii_mod_forgive_crimes(&ctx, base, E(w, 1), refii_mod_player_hero(base, I(w, 2)));
  } else if (op == "publish" && w.size() >= 3) {
    refii::mods::Publish(w[1], F(w, 2));
  } else if (op == "menu_row" && w.size() >= 4) {
    RaiseMenuRow(ctx, base, w);
  } else if (op == "box_answer" && w.size() >= 4) {
    RaiseMessageBox(ctx, base, w);
  } else if (op == "notice_done" && w.size() >= 4) {
    RaiseNotice(ctx, base, w);
  } else if (op == "menu_box_answer" && w.size() >= 4) {
    RaiseMenuBox(ctx, base, w);
  } else if (op == "attack_mode" && w.size() >= 3) {
    StateAttackMode(ctx, base, w);
  } else if (op == "camera_override" && w.size() >= 3) {
    refii_mod_camera_override(I(w, 1), I(w, 2), w.size() > 3 ? F(w, 3) : 0.0f, w.size() > 4 ? F(w, 4) : 0.0f,
                              w.size() > 5 ? F(w, 5) : -1.0f, I(w, 6));
  } else if (op == "run_lua" && w.size() >= 2) {
    refii_mod_run_game_lua(&ctx, base, w[1].c_str());
  } else if (op == "partner_send" && w.size() >= 3) {
    // partner_send MOD NAME [BASE64]: a Godot script's refii_mod_send_to_partner; the payload crosses
    // the command line base64 (no spaces) and is decoded here before the API encodes it for the route.
    std::string payload;
    if (w.size() >= 4 && !UnbaseCommand(w[3], payload)) {
      payload.clear();
    }
    refii_mod_send_to_partner(w[1].c_str(), w[2].c_str(), payload.c_str());
  } else if (op == "fx_play") {
    guest_fx::Play(ctx, base, w);
  } else if (op == "fx_stop") {
    guest_fx::Stop(ctx, base, U(w, 1));
  } else if (op == "fx_reload") {
    refii::vfs::ReloadParticleBank(ctx, base);
  } else if (op == "gdb_reload" && w.size() >= 2) {
    refii::vfs::GdbReload(ctx, base, w[1]);
  } else if (op == "gdb_revert" && w.size() >= 3) {
    refii::vfs::GdbRevert(ctx, base, w);
  } else if (op == "frontend_lua" && w.size() >= 2) {
    refii::vfs::RunFrontEndLua(ctx, base, w[1]);
  } else if (op == "mask_word" && w.size() >= 4) {
    // A word of the host's masks past the two the exports carry (core/mod_values.h kHostMaskWords).
    const int32_t word = I(w, 2);
    const uint64_t bits = std::strtoull(w[3].c_str(), nullptr, 16);
    if (w[1] == "event") {
      refii_bridge_mod_set_host_event_mask_word(word, bits);
    } else if (w[1] == "verdict") {
      refii_bridge_mod_set_verdict_mask_word(word, bits);
    }
  }
}

}  // namespace

namespace refii::vfs {

void RunModCommands(PPCContext& ctx, uint8_t* base) {
  char line[4096];
  // Bounded per tick so a flooding script cannot stall the simulation.
  for (int n = 0; n < 64 && refii_bridge_mod_command_poll(line, sizeof(line)) != 0; ++n) {
    PPCContext work = ctx;
    RunOne(work, base, line);
  }
  PPCContext work = ctx;
  RestateWheelSwitches(work, base);
  work = ctx;
  guest_fx::Pump(work, base);
  work = ctx;
  refii::vfs::PumpParticleBank(work, base);
  work = ctx;
  refii::vfs::PumpModAbilities(work, base);
}

/// RefiiHost.read inside a Godot verdict: `line` is "VERB ARGS..." (a query's words without its id),
/// answered at once by the query's own branch into `out` as "ok=true|false,<fields>"; the length, or
/// REFII_MOD_READ_REFUSED for a verb outside kReadOnlyVerbs. The caller holds the guest thread inside
/// the verdict (mod_events.cpp VerdictRead).
int32_t ModVerdictRead(PPCContext& ctx, uint8_t* base, const char* line, char* out, size_t capacity) {
  std::vector<std::string> w = Split(line, 64);
  if (w.empty() || !ReadOnlyVerb(w[0]) || out == nullptr || capacity == 0) {
    return REFII_MOD_READ_REFUSED;
  }
  w.insert(w.begin(), {std::string("query"), std::string("0")});
  char fields[kAnswerBytes] = {0};
  const bool ok = Answer(ctx, base, w, fields);
  const int length = std::snprintf(out, capacity, "ok=%s%s%s", ok ? "true" : "false", fields[0] != 0 ? "," : "", fields);
  return length < 0 ? 0 : static_cast<int32_t>(std::min(static_cast<size_t>(length), capacity - 1));
}

/// The classic plays riding `entity` placed at it now (guest_fx::PlaceRiding); guest thread only.
void PlaceFxRiding(PPCContext& ctx, uint8_t* base, uint32_t entity) {
  PPCContext work = ctx;
  guest_fx::PlaceRiding(work, base, entity);
}

}  // namespace refii::vfs

extern "C" int32_t refii_mod_attack_mode(int32_t player) {
  return player >= 1 && player <= 2 ? g_attack_mode[player - 1].load() : 0;
}

// ---------------------------------------------------------------------------------------------
// The camera per player (refii_mod.h refii_mod_camera_*). The game VM states the mode and applies
// the hold: the core camera chunk (Godot/src/Titles/Fable2/host/CoopHeroLua.cs) runs around every
// CameraManager:FrameUpdate(hero) and reaches this state through Refii._camera_stated /
// _camera_hold / _camera_held (mod_lua_natives.cpp).
// ---------------------------------------------------------------------------------------------

namespace {

struct CameraHold {
  int32_t mode = 0;
  float fov = 0.0f;
  float pitch = 0.0f;
  float blend = -1.0f;
  int32_t flags = 0;
  /// Bumped on every statement, so the chunk re-states a hold whose data changed.
  uint32_t revision = 0;
};

/// Guest thread only: written by the API and the command, read by the chunk, all in the tick.
std::array<CameraHold, 2> g_camera_hold{};
uint32_t g_camera_revision = 0;

/// The mode last stated per player, -1 for none.
std::atomic<int32_t> g_camera_mode[2] = {-1, -1};

/// Per player, the halves of the override record our hold wrote and still stand (1 Normal, 2
/// LookAt), as the chunk states them.
std::atomic<int32_t> g_camera_held[2] = {0, 0};

/// The camera manager of the player keyed `player` (1 or 2) in the guest's player map: game+0x9C
/// ({key, Player, _} from container+8 to +0xC), Player+4 the holder, holder+4 the manager — the
/// hops RefiiHostGetPlayerCamera and the split-screen tick take. 0 when there is none.
uint32_t CameraManagerOf(uint8_t* base, int32_t player) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t root = refii::mods::LoadU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = plausible(root) ? refii::mods::LoadU32(base, root + 0xCu) : 0u;
  const uint32_t map = plausible(game) ? refii::mods::LoadU32(base, game + 0x9Cu) : 0u;
  if (!plausible(map)) {
    return 0u;
  }
  const uint32_t begin = refii::mods::LoadU32(base, map + 0x8u);
  const uint32_t end = refii::mods::LoadU32(base, map + 0xCu);
  if (!plausible(begin) || end <= begin || end - begin > 0x1000u) {
    return 0u;
  }
  for (uint32_t entry = begin; entry + 12u <= end; entry += 12u) {
    if (refii::mods::LoadU32(base, entry) != static_cast<uint32_t>(player)) {
      continue;
    }
    const uint32_t record = refii::mods::LoadU32(base, entry + 4u);
    const uint32_t holder = plausible(record) ? refii::mods::LoadU32(base, record + 4u) : 0u;
    const uint32_t manager = plausible(holder) ? refii::mods::LoadU32(base, holder + 4u) : 0u;
    return plausible(manager) ? manager : 0u;
  }
  return 0u;
}

}  // namespace

extern "C" int32_t refii_mod_camera_mode(int32_t player) {
  return player >= 1 && player <= 2 ? g_camera_mode[player - 1].load() : -1;
}

extern "C" int32_t refii_mod_camera_override(int32_t player, int32_t mode, float fov, float pitch_limit, float blend,
                                             int32_t flags) {
  if (player < 1 || player > 2 || (mode != 0 && mode != REFII_CAMERA_GAME && mode != REFII_CAMERA_FIRST_PERSON) ||
      !std::isfinite(fov) || !std::isfinite(pitch_limit) || !std::isfinite(blend)) {
    return 0;
  }
  CameraHold& hold = g_camera_hold[player - 1];
  hold.mode = mode;
  hold.fov = fov > 0.0f ? (fov < 179.0f ? fov : 179.0f) : 0.0f;
  hold.pitch = pitch_limit > 0.0f ? (pitch_limit < 90.0f ? pitch_limit : 90.0f) : 0.0f;
  hold.blend = blend >= 0.0f ? blend : -1.0f;
  hold.flags = flags & REFII_CAMERA_HOLD_AIM;
  hold.revision = ++g_camera_revision;
  return 1;
}

extern "C" float refii_mod_camera_fov(uint8_t* base, int32_t player) {
  const uint32_t manager = base != nullptr && (player == 1 || player == 2) ? CameraManagerOf(base, player) : 0u;
  if (manager == 0u) {
    return 0.0f;
  }
  const float radians = refii::mods::LoadF32(base, manager + 0xC0u);
  return std::isfinite(radians) && radians > 0.0f ? radians * (180.0f / 3.14159265f) : 0.0f;
}

namespace refii::vfs {

/// Refii._camera_stated(hero, mode): the core chunk's statement of the mode hero's manager has
/// active; raises CameraChanged where it differs from the last.
void ModCameraStated(PPCContext& ctx, uint8_t* base, uint32_t hero, int32_t mode) {
  const int32_t player = refii_mod_player_of(base, hero);
  if (player != 1 && player != 2) {
    return;
  }
  const int32_t previous = g_camera_mode[player - 1].exchange(mode);
  if (previous == mode) {
    return;
  }
  refii::mods::CameraChangedEvent e{};
  e.ctx = &ctx;
  e.base = base;
  e.player = player;
  e.mode = mode;
  e.previous = previous;
  e.hero = hero;
  refii::mods::Dispatch(e);
}

/// Refii._camera_hold(hero): the hold stated for hero's player while it is in force — false with
/// none, and while a FULL cutscene holds (refii_mod_in_cutscene).
bool ModCameraHold(PPCContext& ctx, uint8_t* base, uint32_t hero, int32_t* mode, float* fov, float* pitch, float* blend,
                   int32_t* flags, uint32_t* revision) {
  const int32_t player = refii_mod_player_of(base, hero);
  if ((player != 1 && player != 2) || g_camera_hold[player - 1].mode == 0 ||
      (refii_mod_in_cutscene(&ctx, base, player) & REFII_CUTSCENE_FULL) != 0) {
    return false;
  }
  const CameraHold& hold = g_camera_hold[player - 1];
  *mode = hold.mode;
  *fov = hold.fov;
  *pitch = hold.pitch;
  *blend = hold.blend;
  *flags = hold.flags;
  *revision = hold.revision;
  return true;
}

/// Refii._camera_held(hero, normal, look_at): which halves of hero's override record our hold wrote
/// and still stand.
void ModCameraHeld(uint8_t* base, uint32_t hero, int32_t bits) {
  const int32_t player = refii_mod_player_of(base, hero);
  if (player == 1 || player == 2) {
    g_camera_held[player - 1].store(bits & 3);
  }
}

/// The halves `player`'s hold stands on (mod_api.cpp: a held LookAt half is not a cutscene).
int32_t ModCameraHeldBits(int32_t player) { return player >= 1 && player <= 2 ? g_camera_held[player - 1].load() : 0; }

/// Every hold let go: a session's startup scripts build a fresh VM (mod_lua_hooks.cpp).
void ModCameraClearHolds() {
  for (CameraHold& hold : g_camera_hold) {
    hold = CameraHold{};
  }
  for (auto& held : g_camera_held) {
    held.store(0);
  }
  for (auto& mode : g_camera_mode) {
    mode.store(-1);
  }
}

}  // namespace refii::vfs

extern "C" void refii_mod_wheel_switches_target_sticky(PPCContext* ctx, uint8_t* base, uint32_t hero, int32_t on) {
  if (ctx != nullptr) {
    StateWheelSwitches(*ctx, base, hero, on != 0);
  }
}
