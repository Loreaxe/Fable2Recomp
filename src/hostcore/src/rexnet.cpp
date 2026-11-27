// refii — RexNet for the shell: identity, friends, invites, sessions, Discord, the co-op
// channel and the sync stream, over the node the runtime owns.

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <csignal>
#ifndef _WIN32
#include <dlfcn.h>
#include <execinfo.h>
#include <fcntl.h>
#include <sys/syscall.h>
#include <sys/resource.h>
#include <setjmp.h>
#include <ucontext.h>
#include <unistd.h>
#include <ctime>
#endif
#include <cstring>
#include <span>
#include <exception>
#include <filesystem>
#include <fstream>
#include <set>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <vector>


#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <dbghelp.h>
#endif

#include <achievement/achievement_runtime.h>
#include <rex/chrono/clock.h>
#include <rex/cvar.h>
#include <rex/logging.h>

namespace rex {
/// Defined in the runtime's logging.cpp and not declared by its header; the crash reporters
/// need it, since a record left in spdlog's queue when the process dies is never written.
void FlushLogging();
}  // namespace rex
#include <rex/system/kernel_state.h>

#include "core/host_asset_reader.h"
#include "core/precise_sleep.h"
#include "core/payload_bridge.h"
#include "core/rex_payload_bridge.h"
#include <rex/memory/utils.h>
#include <rex/system/mmio_handler.h>
#include <rex/system/xmemory.h>
#include <rex/rex_app.h>
#include <rex/ui/windowed_app.h>

#include <rex/filesystem/entry.h>
#include <rex/filesystem/vfs.h>
#include <rex/perf/counter.h>
#include <rex/runtime.h>
#include <rex/system/thread_state.h>
#include <rex/system/xsocket.h>
#include <rex/system/xthread.h>

#include <array>
#include <set>
#include <utility>
#ifdef REXGLUE_ENABLE_PROFILING
#include <tracy/Tracy.hpp>
#include <tracy/TracyC.h>
#endif

#include "host_input.h"
#include "refii_host.h"

#include "host_abi.h"
#include "host_core.h"
#include "host_tracy.h"
#include "hang_watchdog.h"
#include "core/traced_mutex.h"
#include "core/audio_state.h"
#include "core/mod_values.h"
#include "core/event_relay.h"
#include "core/player_state.h"

using namespace refii::hostcore;

#if defined(REXGLUE_ENABLE_REXNET)
#include <rex/net/rexnet.h>
#include <rex/net/rexnet_ffi.h>
#include <rex/net/pseudonym.h>

#if defined(__GNUC__) || defined(__clang__)
#pragma GCC visibility push(hidden)
#endif
namespace refii::hostcore {
/// Appends one field, stripped of anything that could forge a delimiter.
void Field(std::string& row, const std::string& value, char terminator) {

    for (unsigned char c : value) {
        row.push_back(c < 0x20 || c == 0x7F ? ' ' : static_cast<char>(c));
    }
    row.push_back(terminator);
}

void FieldNum(std::string& row, uint64_t value, char terminator) {
    Field(row, std::to_string(value), terminator);
}

std::string PeerText(const RexNetPeerId& peer) {
    char buffer[256] = {0};
    const uint32_t written = rexnet_peer_id_string(&peer, buffer, sizeof(buffer));
    return written == 0 ? std::string() : std::string(buffer, written);
}

std::string HexOf(const std::array<uint8_t, 16>& id) {
    static const char* kDigits = "0123456789abcdef";
    std::string out;
    out.reserve(32);
    for (uint8_t b : id) {
        out.push_back(kDigits[b >> 4]);
        out.push_back(kDigits[b & 0xF]);
    }
    return out;
}

/// Are these the same peer? `RexNetPeerId` is a length-prefixed multihash.
bool SamePeerId(const RexNetPeerId& a, const RexNetPeerId& b) {
    return a.len == b.len && std::memcmp(a.bytes, b.bytes, a.len) == 0;
}

/// The name to show for a PLAYER behind a connection (§17.2): what RexNet's snapshot names them
/// (a friend's display name, anyone else's pseudonym), except for the one case where the
/// snapshot names a role instead of a person. A non-friend this node also relays through is
/// "relay" there, as infrastructure; as a player they are their pseudonym like any other
/// non-friend. The relay role never decides WHETHER a peer is a player: the nearest reachable
/// peer is the one a node relays through, and on one LAN that is the co-op partner.
std::string PlayerName(const rex::net::RexNet::Connection& connection) {
    if (connection.is_relay && !connection.is_friend) {
        return rex::net::Pseudonym(connection.peer);
    }
    return connection.display_name.empty() ? rex::net::Pseudonym(connection.peer)
                                           : connection.display_name;
}

/// The live node, or null when RexNet is off, not built in, or not up yet.
///
/// A RAW STATIC, NOT A REFERENCE. `RexNet::shared()` is `shared_instance_`, deleted by the
/// guest thread's last XNetCleanup (`DestroyShared`) with no refcount and no lock the SDK
/// exposes, so the bridge cannot hold the node alive across a call. What it can do: take the
/// pointer once at entry, null-check it, and re-check it (`NetStillUp`) between calls into the
/// node, so a teardown between two calls is noticed instead of dereferenced. A teardown DURING
/// a node call is the SDK's to serialise; the shell never calls here after it has stopped the
/// guest.
rex::net::RexNet* Net() { return rex::net::RexNet::shared(); }

/// The node taken at entry is still the shared instance.
bool NetStillUp(const rex::net::RexNet* net) { return net != nullptr && Net() == net; }

/// The standard guest-address bound for the net exports.
bool NetPlausible(uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; }

/// 32 hex digits to a 16-byte id; false on any other input.
bool ParseHex16(const char* text, std::array<uint8_t, 16>& out) {
    if (text == nullptr || std::strlen(text) != 32) {
        return false;
    }
    for (size_t i = 0; i < 16; ++i) {
        unsigned value = 0;
        for (size_t nibble = 0; nibble < 2; ++nibble) {
            const char c = text[i * 2 + nibble];
            unsigned digit;
            if (c >= '0' && c <= '9') digit = c - '0';
            else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
            else return false;
            value = (value << 4) | digit;
        }
        out[i] = static_cast<uint8_t>(value);
    }
    return true;
}

/// The guest is already a joiner in `host`'s `session_id`: the arm that resolved names that
/// host and session, and the guest's own bytes say it is in a session as a joiner (the
/// title's statement, TitleCallbacks::guest_joined_session). Re-arming here made the guest run
/// JoinSessionFromInvite inside the session it was in (2026-10-07, 17:08:57), so both join
/// routes refuse on it.
bool GuestAlreadyJoined(rex::net::RexNet* net, const RexNetPeerId& host,
                        const std::array<uint8_t, 16>& session_id) {
    std::optional<rex::net::RexNet::AcceptedInvite> joined = net->accepted_invite();
    if (!joined.has_value() || !SamePeerId(joined->host, host) || joined->session_id != session_id) {
        return false;
    }
    const auto guest_joined = Title().guest_joined_session;
    if (guest_joined == nullptr) {
        return false;
    }
    rex::memory::Memory* memory = SafeKernelMemory();
    const uint8_t* base = memory == nullptr ? nullptr : memory->virtual_membase();
    return guest_joined(base);
}

/// Resolves a peer id the shell named. Accepts the canonical peer string; a friend code is
/// parsed by the caller, which needs its embedded name anyway.
bool ParsePeer(const char* text, RexNetPeerId& out) {
    return text != nullptr && text[0] != '\0' && rexnet_peer_id_parse(text, &out);
}

uint32_t Emit(const std::string& text, char* out, uint32_t capacity) {
    if (out == nullptr || capacity == 0 || text.size() >= capacity) {
        return 0;
    }

    std::memcpy(out, text.data(), text.size());
    out[text.size()] = '\0';
    return static_cast<uint32_t>(text.size());
}

/// The bridge-side carry for a DRAINING reader. The node's Take* calls empty their queue on
/// read, so a record the caller's buffer could not hold would be lost; it is kept here and
/// emitted on the next call instead. Whole records only: the text handed out always ends on a
/// record boundary.
struct DrainCarry {
    std::mutex mutex;
    std::string pending;
    uint64_t dropped = 0;

    /// Bounded: past this the oldest whole records are dropped, counted, rather than growing
    /// without a reader.
    static constexpr size_t kMaxPending = 1u << 20;
};

/// Appends `fresh` to the carry and emits as many whole records as fit in `capacity - 1`
/// bytes; the rest waits. A single record longer than the buffer can never be emitted and is
/// dropped, counted, so it does not block every record behind it. 0 when nothing fits.
uint32_t EmitRecords(DrainCarry& carry, const std::string& fresh, char* out, uint32_t capacity) {
    std::lock_guard<std::mutex> lock(carry.mutex);
    carry.pending += fresh;
    while (carry.pending.size() > DrainCarry::kMaxPending) {
        const size_t cut = carry.pending.find('\n');
        if (cut == std::string::npos) {
            carry.pending.clear();
            ++carry.dropped;
            break;
        }
        carry.pending.erase(0, cut + 1);
        ++carry.dropped;
    }
    if (out == nullptr || capacity == 0 || carry.pending.empty()) {
        return 0;
    }

    const size_t room = capacity - 1;
    size_t take = 0;
    if (carry.pending.size() <= room) {
        take = carry.pending.size();
    } else {
        const size_t cut = carry.pending.rfind('\n', room - 1);
        if (cut == std::string::npos) {
            // The first record alone overflows the buffer: drop it rather than hold the rest.
            const size_t first = carry.pending.find('\n');
            carry.pending.erase(0, first == std::string::npos ? carry.pending.size() : first + 1);
            ++carry.dropped;
            REXLOG_INFO("RefiiNet drain: record longer than the caller's buffer ({} bytes) dropped; {} so far",
                        capacity, carry.dropped);
            return 0;
        }
        take = cut + 1;
    }

    std::memcpy(out, carry.pending.data(), take);
    out[take] = '\0';
    carry.pending.erase(0, take);
    return static_cast<uint32_t>(take);
}
}  // namespace refii::hostcore
#if defined(__GNUC__) || defined(__clang__)
#pragma GCC visibility pop
#endif
#endif

extern "C" {
// ---------------------------------------------------------------------------
// RexNet — friends, invites and sessions, for the shell's own network screen.
//
// OVER `RexNet::shared()`, NEVER `rexnet_init`. The FFI in <rex/net/rexnet_ffi.h> is the
// layer BELOW this one: `RexNet::InitializeShared` calls `rexnet_init` and owns the handle,
// the pump thread, the friend list and the presence cache. A second `rexnet_init` from the
// shell would stand up a second libp2p node with its own identity — a different player.
//
// TEXT, NOT STRUCTS. Every reader returns LF-separated records of TAB-separated fields,
// UTF-8, and the return value is the byte count (0 when RexNet is absent or down). Display
// names are peer-supplied, so `Field` strips every control character on the way out: a name
// carrying a tab or a newline would otherwise forge a record boundary. The field order is
// stated at each export and mirrored by Refii.Host.NetBridge.

/// Is RexNet up? Writes one record: `displayName \t peerId \t friendCode`.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetIdentity(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    const RexNetPeerId self = net->local_peer_id();
    if (!NetStillUp(net)) {
        return 0;
    }

    const std::string name = net->display_name();
    char code[192] = {0};
    rexnet_friend_code(&self, name.empty() ? nullptr : name.c_str(), code, sizeof(code));

    std::string row;
    Field(row, name, '\t');
    Field(row, PeerText(self), '\t');
    Field(row, code, '\n');
    return Emit(row, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif

}

/// One record per friend: `peerId \t name \t online \t titleId \t titleName \t hasSession
/// \t sessionId \t presenceState`.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetFriends(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    std::string text;
    for (const rex::net::RexNet::FriendInfo& friend_info : net->SnapshotFriends()) {
        Field(text, PeerText(friend_info.peer), '\t');
        Field(text, friend_info.display_name, '\t');
        FieldNum(text, friend_info.online ? 1u : 0u, '\t');
        FieldNum(text, friend_info.title_id, '\t');
        Field(text, friend_info.title_name, '\t');
        FieldNum(text, friend_info.has_session ? 1u : 0u, '\t');
        Field(text, friend_info.has_session ? HexOf(friend_info.session_id) : std::string(), '\t');
        FieldNum(text, friend_info.presence_state, '\t');
        Field(text, friend_info.status, '\t');
        FieldNum(text, friend_info.signal, '\n');
    }

    return Emit(text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// The node's view of the network, one record per line, the first field naming the record:
///   `self \t peerId \t shardId \t virtualIp \t titleId` — shardId is empty outside a shard;
///   `shard \t peerId \t name \t friend \t state \t hasSession \t sessionId` — a member of our
///       shard heard within the liveness window (state: 0 offline, 1 online, 2 in game,
///       3 joinable);
///   `link \t peerId \t name \t virtualIp \t status \t degraded \t control \t friend \t relay
///       \t rttMs` — a peer with a virtual address (status: 0 idle, 1 pending, 2 connected,
///       3 lost; rttMs 0 when unmeasured).
/// Names are RexNet's (§17.2): a friend's own, anyone else's pseudonym.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetNetwork(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    std::string text;
    Field(text, "self", '\t');
    Field(text, PeerText(net->local_peer_id()), '\t');
    const std::optional<std::array<uint8_t, 16>> shard = net->current_shard_id();
    Field(text, shard.has_value() ? HexOf(*shard) : std::string(), '\t');
    FieldNum(text, net->local_vip(), '\t');
    const rex::system::KernelState* state = rex::system::kernel_state();
    FieldNum(text, state != nullptr ? state->title_id() : 0u, '\n');

    if (!NetStillUp(net)) {
        return 0;
    }
    for (const rex::net::RexNet::ShardMember& member : net->SnapshotShardMembers()) {
        Field(text, "shard", '\t');
        Field(text, PeerText(member.peer), '\t');
        Field(text, member.display_name, '\t');
        FieldNum(text, member.is_friend ? 1u : 0u, '\t');
        FieldNum(text, member.state, '\t');
        FieldNum(text, member.has_session ? 1u : 0u, '\t');
        Field(text, member.has_session ? HexOf(member.session_id) : std::string(), '\n');
    }

    if (!NetStillUp(net)) {
        return 0;
    }
    for (const rex::net::RexNet::Connection& connection : net->SnapshotConnections()) {
        Field(text, "link", '\t');
        Field(text, PeerText(connection.peer), '\t');
        Field(text, connection.display_name, '\t');
        FieldNum(text, connection.virtual_ip, '\t');
        FieldNum(text, static_cast<uint32_t>(connection.status), '\t');
        FieldNum(text, connection.degraded ? 1u : 0u, '\t');
        FieldNum(text, connection.control_linked ? 1u : 0u, '\t');
        FieldNum(text, connection.is_friend ? 1u : 0u, '\t');
        FieldNum(text, connection.is_relay ? 1u : 0u, '\t');
        FieldNum(text, net->PeerRttMs(connection.virtual_ip), '\n');
    }

    return Emit(text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// One record per inbound friend request: `peerId \t name \t note`.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetRequests(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    std::string text;
    for (const rex::net::RexNet::FriendRequestInfo& request : net->PendingFriendRequests()) {
        Field(text, PeerText(request.peer), '\t');
        Field(text, request.display_name, '\t');
        Field(text, request.note, '\n');
    }

    return Emit(text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// One record per notice the node raised since the last call: `kind\tpeerId\tmessage`, kind
/// 0 error, 1 invite failed, 2 join timed out (`RexNet::Notice::Kind`). Drained on read.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetNotices(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    static DrainCarry carry;
    std::string text;
    for (const rex::net::RexNet::Notice& notice : net->TakeNotices()) {
        Field(text, std::to_string(static_cast<uint32_t>(notice.kind)), '\t');
        Field(text, PeerText(notice.peer), '\t');
        Field(text, notice.message, '\n');
    }

    return EmitRecords(carry, text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// One record per peer that dismissed a request we sent since the last call: `peerId`.
/// Drained on read; the node keeps nothing once it is reported.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetDeclined(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    static DrainCarry carry;
    std::string text;
    for (const RexNetPeerId& peer : net->TakeDeclinedFriendRequests()) {
        Field(text, PeerText(peer), '\n');
    }

    return EmitRecords(carry, text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// One record per peer a request we sent REACHED since the last call: `peerId`. Drained on
/// read. "Sent" is the bridge handing the request to the node; this is the other side's ack.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetDelivered(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    static DrainCarry carry;
    std::string text;
    for (const RexNetPeerId& peer : net->TakeDeliveredFriendRequests()) {
        Field(text, PeerText(peer), '\n');
    }

    return EmitRecords(carry, text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// One record per pending invite: `peerId \t titleId \t sessionId`.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetInvites(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    std::string text;
    for (const rex::net::RexNet::InviteInfo& invite : net->PendingInvites()) {
        Field(text, PeerText(invite.peer), '\t');
        FieldNum(text, invite.title_id, '\t');
        Field(text, HexOf(invite.session_id), '\n');
    }

    return Emit(text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// Drains the accumulated session search: `hostPeerId \t sessionId \t total \t open \t invite`.
///
/// DRAINING, so the shell must keep what it reads — `TakeSessionResults` empties the queue and a
/// second call returns nothing. `RefiiNetSessionSearch` starts a fresh sweep. Records the
/// caller's buffer could not hold are carried to the next call, never lost.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetSessionResults(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    static DrainCarry carry;
    std::string text;
    for (const rex::net::RexNet::SessionResult& result : net->TakeSessionResults()) {
        Field(text, PeerText(result.host), '\t');
        Field(text, HexOf(result.session_id), '\t');
        FieldNum(text, result.slots_total, '\t');
        FieldNum(text, result.slots_open, '\t');
        FieldNum(text, result.requires_invite ? 1u : 0u, '\n');
    }

    return EmitRecords(carry, text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

REFII_HOST_ABI_EXPORT int RefiiNetSessionSearch(void) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    net->SessionSearch();
    return 1;
#else
    return 0;
#endif
}

/// States this player's status to everyone who sees their presence (empty clears it).
REFII_HOST_ABI_EXPORT int RefiiNetSetPlayerStatus(const char* status) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }
    std::string text = status != nullptr ? status : "";
    for (char& c : text) {
        if (c == '\t' || c == '\n' || c == '\r') {
            c = ' ';
        }
    }
    net->SetPlayerStatus(text);
    return 1;
#else
    (void)status;
    return 0;
#endif
}

/// Sends a signal (a greeting, by id 1..255) to everyone who sees this player's presence.
REFII_HOST_ABI_EXPORT int RefiiNetSendPlayerSignal(int id) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr || id <= 0 || id > 255) {
        return 0;
    }
    net->SendPlayerSignal(static_cast<uint8_t>(id));
    return 1;
#else
    (void)id;
    return 0;
#endif
}


/// Reads a `REXN-…` code without acting on it: `name \t peerId`. 0 when it is not one.
///
/// A PURE PARSE, so it answers with the node down: the code carries the peer id and the name
/// inside it, which is what lets the shell say who it is about to add before it sends anything.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetPreviewCode(const char* code, char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    if (code == nullptr || code[0] == '\0') {
        return 0;
    }

    RexNetPeerId peer{};
    char name[128] = {0};
    if (!rexnet_friend_code_parse(code, &peer, name, sizeof(name))) {
        return 0;
    }

    std::string row;
    Field(row, name, '\t');
    Field(row, PeerText(peer), '\n');
    return Emit(row, out, capacity);
#else
    (void)code;
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// THE NAME TO SHOW FOR A PEER (RexNet §17.3.7–§17.3.8): a friend's own display name; anyone else
/// is shown the pseudonym every observer derives from their peer id ("Curious Cat"), never a name
/// they asserted. `name` in one record, 0 when the id is not one.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetPeerName(const char* peer_text, char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    RexNetPeerId peer{};
    if (!ParsePeer(peer_text, peer)) {
        return 0;
    }

    std::string name;
    rex::net::RexNet* net = Net();
    if (net != nullptr && net->IsFriend(peer)) {
        for (const rex::net::RexNet::FriendInfo& friend_info : net->SnapshotFriends()) {
            if (SamePeerId(friend_info.peer, peer)) {
                name = friend_info.display_name;
                break;
            }
        }
    }
    if (name.empty()) {
        name = rex::net::Pseudonym(peer);
    }

    std::string row;
    Field(row, name, '\n');
    return Emit(row, out, capacity);
#else
    (void)peer_text;
    (void)out;
    (void)capacity;
    return 0;
#endif
}

extern "C" uint32_t RefiiCoopChannelPeer(void);

/// The online co-op partner's name to show, by the same rule: the connection the co-op channel
/// talks to (`RefiiCoopChannelPeer`), whatever else that peer is to this node, named as a player
/// (PlayerName). `name` in one record, 0 when no partner is known.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetPartnerName(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    const uint32_t partner = RefiiCoopChannelPeer();
    if (net == nullptr || partner == 0u) {
        return 0;
    }

    for (const rex::net::RexNet::Connection& connection : net->SnapshotConnections()) {
        if (connection.virtual_ip == partner) {
            std::string row;
            Field(row, PlayerName(connection), '\n');
            return Emit(row, out, capacity);
        }
    }
    return 0;
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// States this player's achievements to everyone who sees their presence (the rich KV
/// `kRichKeyPlayerAchievements`, shadow rexnet.h): the bytes are Godot's (Engine_AchievementWire), stated
/// again only when they change. Empty clears them.
REFII_HOST_ABI_EXPORT int RefiiNetSetPlayerAchievements(const uint8_t* bytes, uint32_t len) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }
    net->SetPlayerAchievements(bytes, len);
    return 1;
#else
    (void)bytes;
    (void)len;
    return 0;
#endif
}

#if defined(REXGLUE_ENABLE_REXNET)
extern "C++" {
namespace {
/// The value of rich KV `key` in a presence blob (`[count u8]` then per KV `[key u16 LE][type][len]
/// [bytes]`, RexNet::PushRichLocked), as lowercase hex; empty when it is not there.
std::string RichHex(const std::vector<uint8_t>& rich, uint16_t key) {
    static const char* kDigits = "0123456789abcdef";
    if (rich.empty()) {
        return {};
    }
    const uint8_t* p = rich.data();
    const uint8_t* end = p + rich.size();
    uint8_t count = *p++;
    while (count-- && p + 4 <= end) {
        const uint16_t at = static_cast<uint16_t>(p[0] | (p[1] << 8));
        const uint8_t len = p[3];
        p += 4;
        if (p + len > end) {
            break;
        }
        if (at == key) {
            std::string hex;
            for (uint8_t i = 0; i < len; ++i) {
                hex.push_back(kDigits[p[i] >> 4]);
                hex.push_back(kDigits[p[i] & 0xF]);
            }
            return hex;
        }
        p += len;
    }
    return {};
}
}  // namespace
}  // extern "C++"
#endif

/// THE ACHIEVEMENTS A PLAYER'S MACHINE STATES (RefiiNetSetPlayerAchievements on theirs), for viewing
/// their orb or their hero: one record, `hex`, the bytes as they were sent; 0 when they stated none or
/// this node has not heard them. `peer_text` names the player; empty names the co-op partner (the
/// connection the co-op channel talks to). Read from their presence (a friend's), else from the
/// shard beat their orb rides on.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetPeerAchievements(const char* peer_text, char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    RexNetPeerId peer{};
    if (peer_text == nullptr || peer_text[0] == '\0') {
        const uint32_t partner = RefiiCoopChannelPeer();
        bool found = false;
        for (const rex::net::RexNet::Connection& connection : net->SnapshotConnections()) {
            if (partner != 0u && connection.virtual_ip == partner) {
                peer = connection.peer;
                found = true;
                break;
            }
        }
        if (!found) {
            return 0;
        }
    } else if (!ParsePeer(peer_text, peer)) {
        return 0;
    }

    std::string hex;
    if (std::optional<rex::net::RexNet::PresenceInfo> presence = net->GetPresence(peer)) {
        hex = RichHex(presence->rich, rex::net::kRichKeyPlayerAchievements);
    }
    if (hex.empty()) {
        for (const rex::net::RexNet::ShardMember& member : net->SnapshotShardMembers()) {
            if (SamePeerId(member.peer, peer)) {
                hex = RichHex(member.rich, rex::net::kRichKeyPlayerAchievements);
                break;
            }
        }
    }
    if (hex.empty()) {
        return 0;
    }
    std::string row;
    Field(row, hex, '\n');
    return Emit(row, out, capacity);
#else
    (void)peer_text;
    (void)out;
    (void)capacity;
    return 0;
#endif
}

extern "C" uint32_t RefiiGamerCardRequest(uint64_t* xuid);

/// THE GAMER CARD AN ORB ASKED FOR (`XamShowGamerCardUIForXUID`, the orb box's Interact), as
/// one record: `request \t peer \t name \t flags \t xuid` (the XUID in hex, for the log). `request`
/// counts the asks this run, so the shell acts on one it has not answered; 0 when the guest has
/// asked for none.
///
/// The XUID names its peer — every RexNet XUID is `XuidFromPeer` of one — so it is matched
/// against the players this node knows: friends, then the shard's members (the orbs of players
/// who show to everyone), then live connections. `peer` is empty when none matches. The name is
/// RexNet's (§17.2): a friend's own, anyone else's pseudonym.
///
/// flags: 1 a friend, 2 hosting a session we could join (a friend's presence, else the shard
/// beat's session id), 4 already our co-op partner.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetGamerCard(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    uint64_t xuid = 0u;
    const uint32_t request = RefiiGamerCardRequest(&xuid);
    rex::net::RexNet* net = Net();
    if (request == 0u || net == nullptr) {
        return 0;
    }

    std::optional<RexNetPeerId> found;
    std::string name;
    uint32_t flags = 0u;
    for (const rex::net::RexNet::FriendInfo& friend_info : net->SnapshotFriends()) {
        if (rex::net::RexNet::XuidFromPeer(friend_info.peer) == xuid) {
            found = friend_info.peer;
            name = friend_info.display_name;
            flags |= 1u | (friend_info.has_session ? 2u : 0u);
            break;
        }
    }
    for (const rex::net::RexNet::ShardMember& member : net->SnapshotShardMembers()) {
        if (rex::net::RexNet::XuidFromPeer(member.peer) == xuid) {
            if (!found.has_value()) {
                found = member.peer;
                name = member.display_name;
            }
            flags |= member.has_session ? 2u : 0u;
            break;
        }
    }

    const uint32_t partner = RefiiCoopChannelPeer();
    for (const rex::net::RexNet::Connection& connection : net->SnapshotConnections()) {
        if (rex::net::RexNet::XuidFromPeer(connection.peer) != xuid) {
            continue;
        }
        if (!found.has_value()) {
            found = connection.peer;
            name = PlayerName(connection);
        }
        flags |= partner != 0u && connection.virtual_ip == partner ? 4u : 0u;
        break;
    }

    if (found.has_value() && name.empty()) {
        name = rex::net::Pseudonym(*found);
    }

    std::string row;
    FieldNum(row, request, '\t');
    Field(row, found.has_value() ? PeerText(*found) : std::string(), '\t');
    Field(row, name, '\t');
    FieldNum(row, flags, '\t');
    char xuid_text[17];
    std::snprintf(xuid_text, sizeof(xuid_text), "%016llX", static_cast<unsigned long long>(xuid));
    Field(row, xuid_text, '\n');
    return Emit(row, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// Sends a friend request to a `REXN-…` code. The code carries the peer's own display name, so
/// the shell can show who it is about to add before committing.
REFII_HOST_ABI_EXPORT int RefiiNetAddFriendByCode(const char* code, const char* note) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr || code == nullptr) {
        return 0;
    }

    RexNetPeerId peer{};
    if (!rexnet_friend_code_parse(code, &peer, nullptr, 0)) {
        return 0;
    }

    // 2 = that is OUR OWN code. Two installs made by copying one share identity.key, so
    // the other machine's code is ours and nothing could ever connect; said on the page
    // rather than left as a request that waits forever.
    if (std::memcmp(&peer, &net->local_peer_id(), sizeof(peer)) == 0) {
        return 2;
    }
    if (!NetStillUp(net)) {
        return 0;
    }

    net->FriendRequest(peer, note == nullptr ? std::string() : std::string(note));
    return 1;
#else
    (void)code;
    (void)note;
    return 0;
#endif
}

namespace {

const char* PeerActionName(int action) {
    switch (action) {
        case 0: return "accept request";
        case 1: return "dismiss request";
        case 2: return "remove friend";
        case 3: return "accept invite";
        case 4: return "decline invite";
        case 5: return "invite";
        case 6: return "join";
        default: return "unknown action";
    }
}

/// RefiiNetPeerAction's work; `why` names the outcome for its one log line.
int PeerActionRun(const char* peer_text, int action, const char*& why) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    RexNetPeerId peer{};
    if (net == nullptr) {
        why = "RexNet is not up";
        return 0;
    }
    if (!ParsePeer(peer_text, peer)) {
        why = "not a peer id";
        return 0;
    }

    switch (action) {
        case 0: net->AcceptFriendRequest(peer); return 1;
        case 1: net->DismissFriendRequest(peer); return 1;
        case 2: net->FriendRemove(peer); return 1;
        case 4: net->DeclineInvite(peer); return 1;
        case 5: net->SendInvite(peer); return 1;

        case 3: {
            // The pending invite carries the session id the arm needs, so it is looked up
            // rather than rebuilt: an accept for an invite that is no longer offered must fail.
            for (const rex::net::RexNet::InviteInfo& invite : net->PendingInvites()) {
                if (SamePeerId(invite.peer, peer)) {
                    if (!NetStillUp(net)) {
                        why = "RexNet went down";
                        return 0;
                    }
                    net->AcceptInvite(invite);
                    why = "armed on the invite's session";
                    return 1;
                }
            }

            why = "no invite from that peer is pending";
            return 0;
        }

        case 6: {
            // AN UNINVITED JOIN IS AN INVITE WE RAISE FOR OURSELVES. The material is the same —
            // the host peer and the 16-byte session id — and presence already carries both for
            // a friend who is hosting, so nothing new has to be resolved.
            std::optional<rex::net::RexNet::PresenceInfo> presence = net->GetPresence(peer);
            const bool from_presence = presence.has_value() && presence->has_session;
            if (!from_presence) {
                // NOT A FRIEND: an orb of a player who shows to everyone. The shard's beat
                // carries their session id, and the shard is this title's, so the title is ours.
                presence.reset();
                for (const rex::net::RexNet::ShardMember& member : net->SnapshotShardMembers()) {
                    if (SamePeerId(member.peer, peer) && member.has_session) {
                        rex::system::KernelState* state = rex::system::kernel_state();
                        rex::net::RexNet::PresenceInfo shard;
                        shard.title_id = state != nullptr ? state->title_id() : 0u;
                        shard.has_session = true;
                        shard.session_id = member.session_id;
                        presence = shard;
                        break;
                    }
                }
            }
            if (!NetStillUp(net)) {
                why = "RexNet went down";
                return 0;
            }

            // NO SESSION HEARD OF: ASK THE HOST. Presence reaches only a mutual friend, and the
            // shard only a peer placed in the host's shard — a host that hosts is pinned to the
            // shard it was in, which a joiner may never enter. The host serves its descriptor to
            // whoever asks (a public session), so the accept is armed for that host's session,
            // whichever it is, and the answer resolves it; no answer is the accept's timeout.
            if (!presence.has_value() || !presence->has_session) {
                if (std::optional<rex::net::RexNet::AcceptedInvite> joined = net->accepted_invite();
                    joined.has_value() && GuestAlreadyJoined(net, peer, joined->session_id)) {
                    why = "the guest is already in that session; not re-armed";
                    return 1;
                }

                rex::system::KernelState* state = rex::system::kernel_state();
                rex::net::RexNet::InviteInfo ask;
                ask.peer = peer;
                ask.title_id = state != nullptr ? state->title_id() : 0u;
                ask.session_id = {};
                net->AcceptInvite(ask);
                net->SessionQuery(peer);
                why = "no session known (neither presence nor the shard beat); asked the host for it";
                return 1;
            }

            // ALREADY IN THAT GAME: the press is answered as the join it already is.
            if (GuestAlreadyJoined(net, peer, presence->session_id)) {
                why = "the guest is already in that session; not re-armed";
                return 1;
            }
            if (!NetStillUp(net)) {
                why = "RexNet went down";
                return 0;
            }

            rex::net::RexNet::InviteInfo self_invite;
            self_invite.peer = peer;
            self_invite.title_id = presence->title_id;
            self_invite.session_id = presence->session_id;
            net->AcceptInvite(self_invite);

            // The arm resolves on the HOST'S DESCRIPTOR, which arrives as a search result — so
            // the search is part of the join, not a separate step the player has to take.
            if (!NetStillUp(net)) {
                why = "RexNet went down after the arm";
                return 0;
            }
            net->SessionSearch();
            why = from_presence ? "armed on the session their presence states; searching"
                                : "armed on the session the shard beat states; searching";
            return 1;
        }

        default:
            why = "no such action";
            return 0;
    }
#else
    (void)peer_text;
    (void)action;
    why = "RexNet is not built in";
    return 0;
#endif
}

}  // namespace

/// `action`: 0 accept request, 1 dismiss request, 2 remove friend, 3 accept invite,
/// 4 decline invite, 5 invite this peer to our session, 6 join this peer's session (a friend's
/// from their presence, anyone else's from the shard beat their orb rides on).
///
/// 3 AND 6 GO THROUGH `AcceptInvite`, NOT `InviteReply`. Only `AcceptInvite` arms
/// `pending_accept_`; `InviteReply` just answers the sender. Without the arm, the session
/// descriptor that arrives next never fills `accepted_invite_`, no `kXNotifyLiveInviteAccepted`
/// is raised, and the guest's `XInviteGetAcceptedInfo` fails — the reply is sent and nothing
/// joins.
///
/// Every call is logged once with its outcome: each is a player's press on a friend, request or invite.
REFII_HOST_ABI_EXPORT int RefiiNetPeerAction(const char* peer_text, int action) {
    const char* why = "";
    const int done = PeerActionRun(peer_text, action, why);
    REXLOG_INFO("RefiiNetPeerAction: {} {} — {}{}{}", PeerActionName(action),
                peer_text != nullptr && peer_text[0] != '\0' ? peer_text : "(no peer)", done != 0 ? "done" : "refused",
                why[0] != '\0' ? ": " : "", why);
    return done;
}

/// Our session's 16-byte id as 32 hex digits, or 0 when we are not hosting one. With the peer
/// id, it is the whole of what a joiner needs — the material an invite carries.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetOwnSession(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    std::optional<std::array<uint8_t, 16>> session = net->current_session_id();
    if (!session.has_value()) {
        return 0;
    }

    std::string hex;
    for (uint8_t byte : *session) {
        char pair[3];
        std::snprintf(pair, sizeof(pair), "%02x", byte);
        hex += pair;
    }
    return Emit(hex, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// Joins a session from the material itself — host peer, title id and the session id as
/// `RefiiNetOwnSession` writes it — rather than from a friend's presence. The uninvited join
/// above (action 6) with the lookup removed: a Discord invite carries all three, and the host
/// need not be a friend yet. 0 when the node is down or the material does not parse.
///
/// A JOIN THROUGH DISCORD MAKES A FRIEND. Unless they already are one, the host is sent a
/// friend request whose note is `discord:<session>`: the session id is the proof that the
/// request came with the secret, and the host's shell accepts a request so marked while that
/// session is its own. Mutual consent then completes as for any request.
REFII_HOST_ABI_EXPORT int RefiiNetJoinSession(const char* peer_text, uint32_t title_id,
                                              const char* session_hex) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    RexNetPeerId peer{};
    std::array<uint8_t, 16> session_id{};
    if (net == nullptr || !ParsePeer(peer_text, peer) || !ParseHex16(session_hex, session_id)) {
        return 0;
    }

    // ALREADY IN THAT GAME: the same refusal as action 6, so neither route re-arms a join the
    // guest is in.
    if (GuestAlreadyJoined(net, peer, session_id)) {
        REXLOG_INFO("RefiiNetJoinSession: join to the session the guest is already in; not re-armed");
        return 1;
    }
    if (!NetStillUp(net)) {
        return 0;
    }

    // ONE JOIN, THE NODE'S. The three fields are the first cut's secret; the node reads that
    // form as well as its own, and does everything a join needs (dial, arm, search, befriend,
    // shard) in one place for every host, Discord's included.
    char material[256];
    std::snprintf(material, sizeof(material), "%s|%08X|%s", peer_text, title_id, session_hex);
    return net->JoinFromSecret(material) ? 1 : 0;
#else
    (void)peer_text;
    (void)title_id;
    (void)session_hex;
    return 0;
#endif
}

/// What Discord shows for us: the two lines, and whether a joiner is admitted right now
/// (-1 = whenever a local session exists). The node owns the Discord connection, the party and
/// the join secret; this only states the text.
REFII_HOST_ABI_EXPORT void RefiiNetDiscordActivity(const char* details, const char* state,
                                                   int joinable) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return;
    }
    net->SetDiscordActivity(details == nullptr ? std::string() : std::string(details),
                            state == nullptr ? std::string() : std::string(state),
                            joinable < 0 ? std::nullopt : std::optional<bool>(joinable != 0));
#else
    (void)details;
    (void)state;
    (void)joinable;
#endif
}

/// Rich Presence on or off, the player's setting. Off clears it at Discord.
REFII_HOST_ABI_EXPORT void RefiiNetDiscordEnabled(int enabled) {
#if defined(REXGLUE_ENABLE_REXNET)
    if (rex::net::RexNet* net = Net()) {
        net->SetDiscordEnabled(enabled != 0);
    }
#else
    (void)enabled;
#endif
}

/// One record per Discord "ask to join" the node is holding for an answer: `userId \t userName`.
/// Drained on read; a record the buffer could not hold is carried to the next call.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetJoinRequests(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr) {
        return 0;
    }

    static DrainCarry carry;
    std::string text;
    for (const rex::net::RexNet::JoinRequest& request : net->TakeJoinRequests()) {
        Field(text, request.user_id, '\t');
        Field(text, request.user_name, '\n');
    }

    return EmitRecords(carry, text, out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// Answers one join request by the Discord user id it named. 0 when the node is down.
REFII_HOST_ABI_EXPORT int RefiiNetJoinRequestReply(const char* user_id, int accept) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    if (net == nullptr || user_id == nullptr || user_id[0] == '\0') {
        return 0;
    }

    net->ReplyJoinRequest(std::string(user_id), accept != 0);
    return 1;
#else
    (void)user_id;
    (void)accept;
    return 0;
#endif
}

// --- Refii's own co-op channel ------------------------------------------
//
// Two Refii instances, one co-op session, and state that is OURS: which body player two is in,
// and whatever the native-hero payload grows into. It rides port 0xEF11 rather than any packet
// the title would recognise, so the guest wire is untouched.
//
// Delivery is deliberately unreliable and the messages are deliberately IDEMPOTENT STATE, not
// events — "player two's body is X", restated while the screen is up. A lost datagram costs one
// restatement, which is why this needs no acking, no sequencing and no reliability flag.
//
// The receive half lives in the shadowed XAM net layer, where the datagram sink already sees
// every inbound packet; see `rexglue-net/shadow/rexglue-sdk/src/kernel/xam/xam_net.cpp`.
extern "C" uint16_t RefiiCoopChannelPort(void);
extern "C" uint32_t RefiiCoopChannelPeer(void);
extern "C" uint32_t RefiiCoopChannelTake(char* out, uint32_t capacity);


/// Sends one message to the co-op peer. Returns 0 when RexNet is down or no peer has been seen.
REFII_HOST_ABI_EXPORT int RefiiNetCoopSend(const char* text) {
#if defined(REXGLUE_ENABLE_REXNET)
    rex::net::RexNet* net = Net();
    const uint32_t peer_vip = RefiiCoopChannelPeer();
    static std::atomic<int> last_outcome{-1};  // -1 never, 0 refused, 1 sent; logged on change
    if (net == nullptr || peer_vip == 0 || text == nullptr || text[0] == '\0') {
        if (last_outcome.exchange(0) != 0) {
            REXLOG_INFO("Refii co-op channel: send refused — net {}, peer vip {:08X}",
                        net == nullptr ? "MISSING" : "up", peer_vip);
        }
        return 0;
    }

    const uint16_t port = RefiiCoopChannelPort();
    net->SendDatagram(peer_vip, port, port, text, static_cast<uint32_t>(std::strlen(text)));
    if (last_outcome.exchange(1) != 1) {
        REXLOG_INFO("Refii co-op channel: sending to 10.77.{}.{} port {}", (peer_vip >> 8) & 0xFF,
                    peer_vip & 0xFF, port);
    }
    return 1;
#else
    (void)text;
    return 0;
#endif
}

/// Takes the oldest queued message. Returns the byte count, 0 when nothing is waiting.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetCoopReceive(char* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    return RefiiCoopChannelTake(out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

// Refii's mod sync stream (ABI 217): the guest TCP shim's reliable, ordered rexnet stream on a
// port of our own, between the two co-op machines. Bulk bytes — a joiner downloading the host's
// mod packages — which the datagram channel above is the wrong shape for. The framing, the
// chunk window and the hash check are Godot's (Engine/mods/Engine_ModSync.cs); this is the pipe.
// The port and the stream live in the shadowed XAM net layer beside the co-op channel.
#if defined(REXGLUE_ENABLE_REXNET)
extern "C" int RefiiSyncChannelConnect(void);
extern "C" int RefiiSyncChannelState(void);
extern "C" int RefiiSyncChannelSend(const uint8_t* data, uint32_t len);
extern "C" uint32_t RefiiSyncChannelReceive(uint8_t* out, uint32_t capacity);
extern "C" void RefiiSyncChannelClose(void);
#endif

/// Opens the stream to the co-op peer. 1 when requested (or already up), 0 when RexNet is down or
/// no peer has been seen. Poll RefiiNetSyncState for the outcome.
REFII_HOST_ABI_EXPORT int RefiiNetSyncConnect(void) {
#if defined(REXGLUE_ENABLE_REXNET)
    return RefiiSyncChannelConnect();
#else
    return 0;
#endif
}

/// 0 idle, 1 connecting, 2 open, 3 closed by the peer, 4 connect failed, 5 inbox overflowed.
REFII_HOST_ABI_EXPORT int RefiiNetSyncState(void) {
#if defined(REXGLUE_ENABLE_REXNET)
    return RefiiSyncChannelState();
#else
    return 0;
#endif
}

/// Writes `len` bytes to the open stream. 0 when none is open.
REFII_HOST_ABI_EXPORT int RefiiNetSyncSend(const uint8_t* data, uint32_t len) {
#if defined(REXGLUE_ENABLE_REXNET)
    return RefiiSyncChannelSend(data, len);
#else
    (void)data;
    (void)len;
    return 0;
#endif
}

/// Takes up to `capacity` received bytes, in order. Returns the byte count.
REFII_HOST_ABI_EXPORT uint32_t RefiiNetSyncReceive(uint8_t* out, uint32_t capacity) {
#if defined(REXGLUE_ENABLE_REXNET)
    return RefiiSyncChannelReceive(out, capacity);
#else
    (void)out;
    (void)capacity;
    return 0;
#endif
}

/// Closes the stream if one is up; the state goes back to idle.
REFII_HOST_ABI_EXPORT void RefiiNetSyncClose(void) {
#if defined(REXGLUE_ENABLE_REXNET)
    RefiiSyncChannelClose();
#endif
}
}  // extern "C"
