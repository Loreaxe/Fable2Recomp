// refii - Fable 3's mod Lua, as the tick pump and the debug channel reach it (mod_lua_hooks.cpp).
#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include <rex/ppc.h>

namespace refii::fable3::modlua {

/// The once-per-HF-step upkeep on the game VM's own thread: the host's mod commands and input,
/// the Lua subscription masks, the heroes stated to Lua, the tick event. Nothing on another thread
/// or with no game VM.
void Tick(PPCContext& ctx, uint8_t* base);

/// Runs `chunk` in the game VM. False, with the Lua error in `error`, when it did not compile or
/// raised; false with a reason when there is no game VM or this is not its thread.
bool Run(PPCContext& ctx, uint8_t* base, std::string_view chunk, const char* name, std::string* error);

/// Whether Lua's handlers or Godot's mask want `kind`: a seam builds its fields only then.
bool Wants(int32_t kind);

/// What the mods decided about an event a seam asked them about: cancelled, and the catalog's
/// writable fields they wrote (by name).
struct Verdict {
  bool cancelled = false;
  std::vector<std::pair<std::string, double>> writes;

  void Write(const std::string& field, double value);
  bool Read(const char* field, double& value) const;
};

/// Asks the mods about `kind` NOW, inside the seam: Lua's handlers (their cancel and their writes
/// through Refii._write), then a Godot verdict, then the host's event with the decided fields. Off
/// the VM's thread, or inside another decision, the event is queued as a report and nothing is
/// decided.
Verdict Decide(PPCContext& ctx, uint8_t* base, int32_t kind, const std::string& fields);

/// Decide, also where there is no game VM to run Lua in (the front end: Fable 3 has no front-end Lua):
/// on the VM's thread with a VM it IS Decide; otherwise the native handlers and the Godot verdict
/// decide it and the host's event carries the decided fields; Lua does not hear it. Inside another
/// decision it is queued as a report, as Decide does.
Verdict DecideAnywhere(PPCContext& ctx, uint8_t* base, int32_t kind, const std::string& fields);

/// `kind` with its table text (`name=value,...`, Lua literals), raised to Lua and Godot from the
/// next tick on the VM's thread. Safe from any seam on any thread.
void QueueEvent(int32_t kind, std::string fields);

/// Player 1's or 2's hero entity (the world's hero holders, keyed by PlayerID), or 0.
uint32_t Hero(const uint8_t* base, int32_t player);

/// 1 or 2 when `entity` is that player's hero, else 0.
int32_t PlayerOf(const uint8_t* base, uint32_t entity);
/// This machine joined another's game online (the game's own Network.IsClient, as of the last tick).
bool OnlineClient();

/// A text as a single-quoted Lua literal's body.
std::string Quoted(std::string_view text);

/// The entity's live action (its action manager's, not stopped), or 0 (mod_combat_hooks.cpp).
uint32_t CurrentAction(const uint8_t* base, uint32_t entity);

/// The action's clock in seconds (its slot 7); false when there is no action.
bool ActionElapsed(const PPCContext& ctx, uint8_t* base, uint32_t action, float& seconds);

/// Whether the next action may interrupt it now: no veto (+0x48) and allowed (+0x49).
bool ActionInterruptible(const uint8_t* base, uint32_t action);

/// The anim handler's ALLOW_ACTION_INTERRUPT grant, now; false under the action's veto.
bool AllowActionInterrupt(uint8_t* base, uint32_t action);

/// The grant once the action's clock reaches `seconds` (now when it has); false when it cannot be.
bool AllowActionInterruptAt(const PPCContext& ctx, uint8_t* base, uint32_t action, float seconds);

/// The waiting grants, once per tick.
void PumpActionInterrupts(const PPCContext& ctx, uint8_t* base);

/// The cutscene statements holding for player 1 or 2 (0: either), as REFII_CUTSCENE_* bits.
int32_t InCutscene(const PPCContext& ctx, uint8_t* base, int32_t player);

/// The player's locked target (the force byte or the game's TARGET_LOCK mode, then its target), or 0.
uint32_t LockedTarget(const PPCContext& ctx, uint8_t* base, int32_t player);

/// Locks the player's targeting on `target` (0: on what it holds) through the force byte.
bool LockTarget(const PPCContext& ctx, uint8_t* base, int32_t player, uint32_t target);

/// Releases the force byte; the game's targeting pass drops the lock mode.
bool ReleaseLock(uint8_t* base, int32_t player);

/// Refii.CREATURE_ELITE (1) | CREATURE_LIEUTENANT (2) of the entity, 0 for neither.
int32_t CreatureTier(const uint8_t* base, uint32_t entity);

/// A live entity whose health (if it has one) is above zero.
bool Living(const uint8_t* base, uint32_t entity);

/// The GUI manager's screen word now (32: none).
int32_t MenuScreen(const uint8_t* base);

/// After the GUI manager changed its screen from `before`: menu_screen, and menu_closed when none is left.
void NoteMenuScreen(const uint8_t* base, int32_t before);

/// A hero's charge started (the quick charge's CanStart answered yes).
void NoteChargeStarted(const uint8_t* base, uint32_t entity);

/// CAction_End of a live charge action of a hero: true when it is one (raise with RaiseChargeEnd after).
bool NoteChargeEnd(const uint8_t* base, uint32_t action);
void RaiseChargeEnd(uint8_t* base);

/// A hit delivered with a cast's serial: counted for the fireball impact in progress.
void NoteImpactHit(uint32_t serial);

/// What a cast put in the world, by the serial its hits and events carry (Refii.spell_parts):
/// the spell object while it carries the serial, the live vortex component and its entity, the
/// fireball's impact point and whether it detonated, the sword set's live swords. Kept by the
/// spell seams (mod_combat_hooks.cpp) for the last 32 casts.
struct CastParts {
  uint32_t spell = 0;
  int32_t type = -1;
  bool targeted = false;
  int32_t level = 0;
  uint32_t caster = 0;
  uint32_t vortex = 0;
  uint32_t anchor = 0;
  bool exploded = false;
  bool has_origin = false;
  float origin[3] = {0.0f, 0.0f, 0.0f};
  uint32_t swords[8] = {};
  int32_t sword_count = 0;
};

/// False for a serial the factory never made (or one 32 casts ago).
bool SpellParts(const uint8_t* base, uint32_t serial, CastParts& out);

/// The serial of a spell object the factory made while it still carries it, 0 otherwise.
uint32_t SpellSerial(const uint8_t* base, uint32_t spell);

/// Who the live vortex of `serial` holds now (its held vector, its order; up to `max` written):
/// the count, -1 when no live vortex carries the serial.
int32_t VortexHeld(const uint8_t* base, uint32_t serial, uint32_t* out, int32_t max);

/// The lent charge (Refii.charge_spell, mod_lua_hooks.cpp): player 1's or 2's charge action ended
/// (released: by its cast's Start), and a real slot pick on `manager` (the spell_pick seam).
void NoteLendChargeEnd(int32_t player, bool released);
void NoteLendPick(const uint8_t* base, uint32_t manager, int32_t slot);

/// Once per tick before the queued events: the cutscene and lock edges, the interrupt grants.
void PollCombat(const PPCContext& ctx, uint8_t* base);

/// level_loaded (after the level manager's post-load pass) / level_unloading (before its unload),
/// from world_hooks.cpp with the manager (mod_events_hooks.cpp).
void NoteLevel(const uint8_t* base, uint32_t manager, bool loaded);

/// A value crossing to or from the game VM for the native C API (mod_api.cpp): an entity crosses as
/// its handle (a number), a record as its ID word (a light userdata, as the game's glue takes it).
struct LuaValue {
  enum class Kind : uint8_t { Nil, Number, Boolean, String, Record };
  Kind kind = Kind::Nil;
  double number = 0.0;
  bool boolean = false;
  std::string text;
  uint32_t raw = 0u;
  static LuaValue Num(double v) { LuaValue x; x.kind = Kind::Number; x.number = v; return x; }
  static LuaValue Bool(bool v) { LuaValue x; x.kind = Kind::Boolean; x.boolean = v; return x; }
  static LuaValue Str(std::string_view v) { LuaValue x; x.kind = Kind::String; x.text = std::string(v); return x; }
  static LuaValue Rec(uint32_t id) { LuaValue x; x.kind = Kind::Record; x.raw = id; return x; }
};
/// Refii.<function>(args...) in the game VM (Fable3CoreLua's helpers, the same a Lua mod calls), every
/// result in `results`. VM thread only; false without a VM, when the helper is missing or raised.
bool CallRefii(PPCContext& ctx, uint8_t* base, const char* function, const std::vector<LuaValue>& args,
               std::vector<LuaValue>& results);
/// The VM-thread context a native mod's callback runs inside (a tick, an event), for the C API's
/// calls that take none; null outside one.
PPCContext* CurrentContext();
class ContextScope {
 public:
  explicit ContextScope(PPCContext& ctx);
  ~ContextScope();
  ContextScope(const ContextScope&) = delete;
  ContextScope& operator=(const ContextScope&) = delete;

 private:
  PPCContext* previous_;
};

}  // namespace refii::fable3::modlua

namespace refii::fable3::modlua {
/// The halves of player's camera override record the mods' camera hold stands on (1 Normal, 2 LookAt).
void NoteCameraHeld(int32_t player, int32_t bits);
/// The two override-camera words of hero's Player component (+0xC0 Normal, +0xC4 LookAt, both builds)
/// stored as given: what the mods' camera hold puts back after each of its own
/// CameraManager.SetCameraOverride calls, so the pair the component saves is never the hold's.
void RestoreCameraModes(uint8_t* base, uint32_t hero, uint32_t normal, uint32_t look_at);
}  // namespace refii::fable3::modlua
namespace refii::fable3::mouselook {
/// A mod's wheel statement for `hero`: 0 off (sticky cleared), 1 this frame, 2 sticky.
void StateWheel(const uint8_t* base, uint32_t hero, int32_t mode);
}  // namespace refii::fable3::mouselook

namespace refii::fable3::splash {
/// The "Press START" splash stands (splash_screen.cpp).
bool Up();
}  // namespace refii::fable3::splash

namespace refii::fable3::view {
/// The horizontal FOV at 4:3, in radians, of the camera the shell draws the presented game with (the
/// rebuilt block's +0x30, stated back by RefiiHostStateCamera); 0 before the first.
float StatedFovRadians();
}  // namespace refii::fable3::view
