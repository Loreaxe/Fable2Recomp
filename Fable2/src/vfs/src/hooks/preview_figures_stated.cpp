// THE GUI'S PREVIEW FIGURES, STATED. The preview scene `*(GUI + 8)` keeps one 24-byte record per
// figure a screen stands on a page: `+4` the entity's smart-pointer holder (`*holder` the entity),
// `+16` its ticket. Three figures exist, each made inside its own function:
//   - the clothing screen's mannequin: its ported OnEnter (gui_screen_stub_clothing.cpp) keeps the
//     guest's `sub_826D7650` (0x826d2c68, disc 0x8257e6a0), which spawns a `CreatureHeroMannequin*`
//     and files it;
//   - the co-op screen's player-one mannequin: its open `sub_82436548` → `sub_826D7650`
//     (0x82436a2c, disc `sub_82477768` 0x82477c4c);
//   - the co-op screen's henchman: `lhCoopScreen_SpawnPreviewMannequin`, which spawns it and files
//     the record itself (0x824389a0, disc 0x82479bc0).
// Each of the three names its figure for the filing it makes (SetPreviewFigureRole; the clothing
// screen's is its ported OnEnter, the henchman's hook is coop_player_hooks.cpp).
//
//   filed   `sub_826D81C8(records, record)` (disc `sub_823FDDB0`), the vector's push; its only
//           callers are the two above (0x826d7e18 / 0x824389a0, disc 0x823fda00 / 0x82479bc0).
//           States `@Preview.Add <role> <ticket> <entity>` from the record it is handed.
//   gone    `sub_826D80C8(scene, record)` (disc `sub_823FDCB0`) destroys a record's entity
//           (`lhEntity_Destroy` 0x826d8138): called by the ticket's remove `sub_826D7E90`
//           (0x826d7f14, disc 0x823fdafc) — every screen's exit and respawn — and by the scene's
//           destructor. States `@Preview.Remove <ticket>`.
//
// PLAYER ONE'S FIGURE IS BUILT FROM A HERO THE GUEST DOES NOT TEST. The screen's update
// `sub_82436548` (disc `sub_82477768`), once `screen+492` is set and the root element's state
// (`+48`) is 500 or more, passes `sub_8222C1B8(EM)` — 0 on an empty or dead hero-one handle
// (`EM+108`) — straight to `sub_82438438` (0x82436a10, disc `sub_82479658` 0x82477c30), whose
// first use is `lwz r5, 0x30(r31)` (0x8243845c, disc 0x8247967c) with no null test. The update's
// hook states, once per open, whether hero one stood while that build was pending and when it ran.
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "core/gui_native_calls.h"
#include "hooks/ppc_externs.h"
#include "core/guest_build.h"

namespace {

constexpr refii::guest::Addr kGameRoot{0x83496AB8u};
// The co-op screen's "figures not built yet" byte: set by its enter (0x82435ac0, disc
// 0x82476ce0), read and cleared by its update (0x82436964 / 0x82436a98, disc 0x82477b84 /
// 0x82477cb8).
constexpr uint32_t kScreenFiguresPending = 492u;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

// Hero one as the update's own `sub_8222C1B8(**(*(*(root+12)+88)+4))` is about to read it on this
// thread: the handle's object at `EM+108`, one more load; 0 on an empty or dead handle.
uint32_t HeroOneAsTheUpdateReadsIt(uint8_t* base) {
    uint32_t at = refii::vfs::LoadGuestU32(base, kGameRoot);
    for (uint32_t offset : {12u, 88u, 4u, 0u, 108u, 0u}) {
        if (!Plausible(at)) return 0u;
        at = refii::vfs::LoadGuestU32(base, at + offset);
    }
    return Plausible(at) ? at : 0u;
}

// The open whose pending build was last reported with no hero one, so the line is one per open.
uint32_t g_reported_no_hero_screen = 0u;

constexpr uint32_t kRecordHolder = 4u;
constexpr uint32_t kRecordTicket = 16u;

thread_local const char* t_role = nullptr;

// The figures filed and not yet destroyed. The scene holds three at most; a filing past the
// table's end is stated to Godot and not kept.
struct Figure {
    uint32_t ticket = 0u;
    uint32_t entity = 0u;
    const char* role = "";
};
std::mutex g_figures_lock;
Figure g_figures[8];
uint32_t g_figure_count = 0u;

void Add(refii::vfs::GuiNativeCall& call, std::string value) {
    refii::vfs::GuiNativeArgument argument;
    argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    argument.text = std::move(value);
    call.arguments.push_back(std::move(argument));
}

std::string Hex(uint32_t value) {
    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", value);
    return hex;
}

}  // namespace

namespace refii::vfs {
inline namespace detail {

uint32_t StatedPreviewFigure(const char* role) {
    std::lock_guard<std::mutex> lock(g_figures_lock);
    for (uint32_t i = g_figure_count; i-- > 0u;) {
        if (std::strcmp(g_figures[i].role, role) == 0) return g_figures[i].entity;
    }
    return 0u;
}

uint32_t StatedPreviewFigures(uint32_t* out_entities, const char** out_roles, uint32_t capacity) {
    std::lock_guard<std::mutex> lock(g_figures_lock);
    uint32_t count = 0u;
    for (uint32_t i = 0u; i < g_figure_count && count < capacity; ++i) {
        out_entities[count] = g_figures[i].entity;
        out_roles[count] = g_figures[i].role;
        ++count;
    }
    return count;
}

uint32_t StatedClothingMannequin() { return StatedPreviewFigure("clothing"); }

const char* SetPreviewFigureRole(const char* role) {
    const char* outer = t_role;
    t_role = role;
    return outer;
}

}  // namespace detail
}  // namespace refii::vfs

REX_EXTERN(rex_lhCoopScreen_Open) {
    REX_FUNC_PROLOGUE();
    const uint32_t screen = ctx.r3.u32;
    const bool pending =
        Plausible(screen) && refii::vfs::LoadGuestU8(base, screen + kScreenFiguresPending) != 0u;
    const uint32_t hero = pending ? HeroOneAsTheUpdateReadsIt(base) : 0u;
    if (pending && hero == 0u && g_reported_no_hero_screen != screen) {
        g_reported_no_hero_screen = screen;
        REXLOG_WARN("Refii preview figure: the co-op screen {:08X} has its figures pending and hero "
                    "one's handle is EMPTY — the build reads it untested once the root element "
                    "reaches state 500",
                    screen);
    }

    const char* outer = refii::vfs::SetPreviewFigureRole("playerone");
    __imp__rex_lhCoopScreen_Open(ctx, base);
    refii::vfs::SetPreviewFigureRole(outer);

    if (pending && refii::vfs::LoadGuestU8(base, screen + kScreenFiguresPending) == 0u) {
        g_reported_no_hero_screen = 0u;
        REXLOG_INFO("Refii preview figure: the co-op screen {:08X} built its figures from hero one "
                    "{:08X}",
                    screen, hero);
    }
}

REX_EXTERN(rex_lhGuiPreviewScene_FileRecord) {
    REX_FUNC_PROLOGUE();
    const uint32_t record = ctx.r4.u32;
    const uint32_t holder = refii::vfs::LoadGuestU32(base, record + kRecordHolder);
    const uint32_t entity = holder != 0u ? refii::vfs::LoadGuestU32(base, holder) : 0u;
    const uint32_t ticket = refii::vfs::LoadGuestU32(base, record + kRecordTicket);
    const char* const filed_as = t_role != nullptr ? t_role : "";  // a string literal
    const std::string role = filed_as;
    __imp__rex_lhGuiPreviewScene_FileRecord(ctx, base);

    {
        std::lock_guard<std::mutex> lock(g_figures_lock);
        if (g_figure_count < 8u) {
            g_figures[g_figure_count++] = Figure{ticket, entity, filed_as};
        }
    }
    REXLOG_INFO("Refii preview figure: '{}' ticket {} entity {:08X} filed", role, ticket, entity);
    refii::vfs::GuiNativeCall call;
    call.name = "@Preview.Add";
    Add(call, role);
    Add(call, std::to_string(ticket));
    Add(call, Hex(entity));
    refii::vfs::PostGuiNativeCall(std::move(call));
}

REX_EXTERN(rex_lhGuiPreviewScene_DestroyRecordEntity) {
    REX_FUNC_PROLOGUE();
    const uint32_t ticket = refii::vfs::LoadGuestU32(base, ctx.r4.u32 + kRecordTicket);
    __imp__rex_lhGuiPreviewScene_DestroyRecordEntity(ctx, base);

    {
        std::lock_guard<std::mutex> lock(g_figures_lock);
        uint32_t kept = 0u;
        for (uint32_t i = 0u; i < g_figure_count; ++i) {
            if (g_figures[i].ticket != ticket) g_figures[kept++] = g_figures[i];
        }
        g_figure_count = kept;
    }
    REXLOG_INFO("Refii preview figure: ticket {} gone", ticket);
    refii::vfs::GuiNativeCall call;
    call.name = "@Preview.Remove";
    Add(call, std::to_string(ticket));
    refii::vfs::PostGuiNativeCall(std::move(call));
}
