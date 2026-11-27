// The host's answer to a held-items ATTACH (type 1) the joiner sends for player two's body.
//
// Player two's body is the joiner's to control (CECNetwork+0x68 set on the host), so the guest's
// authority test (`sub_82214AF8`, disc `sub_82214C28`) is false for it on the host and true on
// the joiner. Every attach to it is therefore made on the joiner and arrives here as a message.
//
// A HENCHMAN is armed by the guest itself (`sub_82315AD0`, disc `sub_82351E30`): the host
// creates "DefaultWeaponHenchman" and "DefaultRangedWeaponHenchman" and replicates them, and
// only the authoritative side puts them in the sheaths (gate 0x8231614C / 0x823524AC, attach
// 0x8231626C / 0x823525CC) — the joiner, whose attach is this message. Refusing it leaves both
// on the host's floor.
//
// A HERO is armed by record on the host (the pump's EquipRecord); an attach of anything else
// may name a joiner-local id that resolves to an unrelated host entity, and is refused.
//
// An attach of what the slot already holds is already true: re-attaching it destroys it
// (`sub_824FDB70` on the host).
#pragma once

namespace refii::coop {

enum class JoinerAttach { kRun, kAlreadyTrue, kRefuse };

constexpr JoinerAttach JoinerAttachToPlayerTwo(bool already_held, bool own_hero, bool host_armed) {
  if (already_held) return JoinerAttach::kAlreadyTrue;
  if (own_hero && !host_armed) return JoinerAttach::kRefuse;
  return JoinerAttach::kRun;
}

/// A henchman weapon the host made (`sub_82315AD0`) is LOOSE when it is alive and no live
/// carrier holds it: the joiner never attached it (it left or was killed first), or a later arm
/// replaced it. A loose one is the host's to destroy; a held one goes with its holder
/// (`sub_824F8E68` → `sub_824F99D8`).
constexpr bool HenchmanItemIsLoose(bool alive, bool carrier_alive, bool carrier_holds_it) {
  return alive && !(carrier_alive && carrier_holds_it);
}

}  // namespace refii::coop
