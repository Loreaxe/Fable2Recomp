// refii — THE NAME THE GAME SHOWS FOR A PLAYER, by RexNet's rule (rexnet-design-spec §17.3.7–
// §17.3.8): a friend's own display name, anyone else's pseudonym ("Curious Cat"), never a name a
// non-friend sent. The game keys its players by XUID — the co-op member records (+0x78) and the
// orb presence records — and every RexNet XUID is derived from the player's peer id
// (`RexNet::XuidFromPeer`), so a XUID names its peer. Where the game prints a name straight from
// a message with no XUID beside it (gifts, the version-mismatch boxes), the name is matched to a
// player instead: every machine's game sends its player's pseudonym (the kernel's profile name),
// so a name that is a known player's pseudonym names that player. Read by the payload's name
// hooks (coop_ui_hooks.cpp); here, in refii_runtime, because it links RexNet.

#include <cstdint>
#include <cstdio>
#include <string>

#include "core/payload_bridge.h"

#if defined(REXGLUE_ENABLE_REXNET)
#include <rex/net/pseudonym.h>
#include <rex/net/rexnet.h>
#endif

extern "C" int32_t refii_bridge_safe_name_for_xuid(uint64_t xuid, char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
  rex::net::RexNet* net = rex::net::RexNet::shared();
  if (net == nullptr || xuid == 0u || out == nullptr || capacity == 0u) {
    return 0;
  }

  std::string name;
  bool found = false;
  const RexNetPeerId self = net->local_peer_id();
  if (rex::net::RexNet::XuidFromPeer(self) == xuid) {
    // This machine's own player, shown to themselves.
    name = net->display_name();
    if (name.empty()) {
      name = rex::net::Pseudonym(self);
    }
    found = true;
  }
  if (!found) {
    for (const rex::net::RexNet::FriendInfo& friend_info : net->SnapshotFriends()) {
      if (rex::net::RexNet::XuidFromPeer(friend_info.peer) == xuid) {
        name = friend_info.display_name.empty() ? rex::net::Pseudonym(friend_info.peer) : friend_info.display_name;
        found = true;
        break;
      }
    }
  }
  if (!found) {
    // Already resolved by RexNet: a friend's name or a pseudonym, never a stranger's own.
    for (const rex::net::RexNet::Connection& connection : net->SnapshotConnections()) {
      if (!connection.is_relay && rex::net::RexNet::XuidFromPeer(connection.peer) == xuid) {
        name = connection.display_name.empty() ? rex::net::Pseudonym(connection.peer) : connection.display_name;
        found = true;
        break;
      }
    }
  }
  if (!found) {
    for (const rex::net::RexNet::ShardMember& member : net->SnapshotShardMembers()) {
      if (rex::net::RexNet::XuidFromPeer(member.peer) == xuid) {
        name = member.display_name.empty() ? rex::net::Pseudonym(member.peer) : member.display_name;
        found = true;
        break;
      }
    }
  }
  if (!found || name.empty()) {
    return 0;
  }
  std::snprintf(out, capacity, "%s", name.c_str());
  return 1;
#else
  (void)xuid;
  (void)out;
  (void)capacity;
  return 0;
#endif
}

// A name another machine's game sent: 1 and the name to show when it is a known player's
// pseudonym (a friend's display name for a friend, the pseudonym otherwise) or this player's own;
// 0 when it names no known player — an asserted name, never shown as sent.
extern "C" int32_t refii_bridge_safe_name_for_sent(const char* sent, char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
  rex::net::RexNet* net = rex::net::RexNet::shared();
  if (net == nullptr || sent == nullptr || sent[0] == '\0' || out == nullptr || capacity == 0u) {
    return 0;
  }
  const std::string wanted(sent);
  const auto put = [&](const std::string& name) {
    std::snprintf(out, capacity, "%s", name.c_str());
    return 1;
  };

  const RexNetPeerId self = net->local_peer_id();
  if (rex::net::Pseudonym(self) == wanted) {
    const std::string own = net->display_name();
    return put(own.empty() ? wanted : own);
  }
  // Friends first: a pseudonym two players share (§17.3.7 collisions) names the friend.
  for (const rex::net::RexNet::FriendInfo& friend_info : net->SnapshotFriends()) {
    if (rex::net::Pseudonym(friend_info.peer) == wanted) {
      return put(friend_info.display_name.empty() ? wanted : friend_info.display_name);
    }
  }
  for (const rex::net::RexNet::Connection& connection : net->SnapshotConnections()) {
    if (!connection.is_relay && rex::net::Pseudonym(connection.peer) == wanted) {
      return put(wanted);
    }
  }
  for (const rex::net::RexNet::ShardMember& member : net->SnapshotShardMembers()) {
    if (rex::net::Pseudonym(member.peer) == wanted) {
      return put(wanted);
    }
  }
  return 0;
#else
  (void)sent;
  (void)out;
  (void)capacity;
  return 0;
#endif
}

// This machine's own player's pseudonym — the name the game's profile carries and sends (its XamUserGetName),
// which another machine shows by RexNet's rule (refii_bridge_safe_name_for_sent). 1 when RexNet is up.
extern "C" int32_t refii_bridge_own_pseudonym(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
  rex::net::RexNet* net = rex::net::RexNet::shared();
  if (net == nullptr || out == nullptr || capacity == 0u) {
    return 0;
  }
  std::snprintf(out, capacity, "%s", rex::net::Pseudonym(net->local_peer_id()).c_str());
  return 1;
#else
  (void)out;
  (void)capacity;
  return 0;
#endif
}

// This machine's own player's name, as they are shown it: their display name, else their
// pseudonym. The game's profile name is the pseudonym (it travels); this is for the game's own
// screens that name the local player. 1 when RexNet is up.
extern "C" int32_t refii_bridge_own_name(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
  rex::net::RexNet* net = rex::net::RexNet::shared();
  if (net == nullptr || out == nullptr || capacity == 0u) {
    return 0;
  }
  std::string name = net->display_name();
  if (name.empty()) {
    name = rex::net::Pseudonym(net->local_peer_id());
  }
  std::snprintf(out, capacity, "%s", name.c_str());
  return 1;
#else
  (void)out;
  (void)capacity;
  return 0;
#endif
}
