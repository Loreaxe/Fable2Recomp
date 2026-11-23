// THE ROW TICKETS, STATED. Every list screen keys its item rows by a population TICKET on one
// registry, `*(*(GUI+0x1C)+0x3C) + 4` (each caller builds it so, e.g. 0x82430914..0x82430924):
//
//   allocate `sub_82880AC8(registry, handle)` (disc `sub_827BE0F0`): `++*a1` (0x82880b00) is the
//            ticket, filed in the map at `a1 + 4` against a copy of the item handle `a2`
//            (`sub_82880D48(…, a1 + 1, &ticket)` 0x82880b74) and returned (0x82880ba8). The
//            handle's first word is the item, whose `+4` is its record hash.
//   clear    `sub_82880CE0(registry)` (disc `sub_827BE308`): `*a1 = 0` (0x82880d00) and every
//            node erased (0x82880d0c) — the populates' first act (e.g. 0x829b0c18, 0x829b10d0).
//
// The table here is ticket -> record as those two calls leave it; nothing walks the guest's map.
// A ticket at or below the last one allocated means the counter restarted (a clear, or a new
// registry, whose counter starts at 0), and drops the table the same way.
#include <cstdint>
#include <map>
#include <mutex>

#include "vfs_hooks_private.h"
#include "hooks/ppc_externs.h"

namespace {

std::mutex g_lock;
uint32_t g_registry = 0u;
uint32_t g_last = 0u;
std::map<uint32_t, uint32_t> g_records;  // ticket -> record hash

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

}  // namespace

namespace refii::vfs {

uint32_t MenuTicketForRecord(uint32_t record) {
    if (record == 0u) {
        return 0u;
    }
    std::lock_guard<std::mutex> lock(g_lock);
    for (const auto& [ticket, held] : g_records) {
        if (held == record) {
            return ticket;  // the lowest: the first row the populate made for it
        }
    }
    return 0u;
}

uint32_t MenuTicketRegistry() {
    std::lock_guard<std::mutex> lock(g_lock);
    return g_registry;
}

}  // namespace refii::vfs

REX_EXTERN(rex_lhGuiRowTickets_Allocate) {
    REX_FUNC_PROLOGUE();
    const uint32_t registry = ctx.r3.u32;
    const uint32_t handle = ctx.r4.u32;
    const uint32_t item = Plausible(handle) ? refii::vfs::LoadGuestU32(base, handle) : 0u;
    const uint32_t record = Plausible(item) ? refii::vfs::LoadGuestU32(base, item + 4u) : 0u;
    __imp__rex_lhGuiRowTickets_Allocate(ctx, base);
    const uint32_t ticket = ctx.r3.u32;
    std::lock_guard<std::mutex> lock(g_lock);
    if (registry != g_registry || ticket <= g_last) {
        g_records.clear();
        g_registry = registry;
    }
    g_last = ticket;
    g_records[ticket] = record;
}

REX_EXTERN(rex_lhGuiRowTickets_Clear) {
    REX_FUNC_PROLOGUE();
    const uint32_t registry = ctx.r3.u32;
    __imp__rex_lhGuiRowTickets_Clear(ctx, base);
    std::lock_guard<std::mutex> lock(g_lock);
    g_records.clear();
    g_registry = registry;
    g_last = 0u;
}
