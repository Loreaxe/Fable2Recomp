// THE GUI'S PREVIEW FIGURES, STATED. The preview scene `*(GUI + 8)` keeps one 24-byte record per
// figure a screen stands on a page: `+4` the entity's smart-pointer holder (`*holder` the entity),
// `+16` its ticket. Three figures exist, each made inside its own function:
//   - the clothing screen's mannequin: `lhGuiClothingScreen_OnEnter` → `sub_826D7650` (0x826d2c68,
//     disc 0x8257e6a0), which spawns a `CreatureHeroMannequin*` and files it;
//   - the co-op screen's player-one mannequin: its open `sub_82436548` → `sub_826D7650`
//     (0x82436a2c, disc `sub_82477768` 0x82477c4c);
//   - the co-op screen's henchman: `lhCoopScreen_SpawnPreviewMannequin`, which spawns it and files
//     the record itself (0x824389a0, disc 0x82479bc0).
// Each of the three names its figure for the filing it makes (SetPreviewFigureRole; the clothing
// screen's and the henchman's hooks are coop_hero_hooks.cpp and coop_player_hooks.cpp).
//
//   filed   `sub_826D81C8(records, record)` (disc `sub_823FDDB0`), the vector's push; its only
//           callers are the two above (0x826d7e18 / 0x824389a0, disc 0x823fda00 / 0x82479bc0).
//           States `@Preview.Add <role> <ticket> <entity>` from the record it is handed.
//   gone    `sub_826D80C8(scene, record)` (disc `sub_823FDCB0`) destroys a record's entity
//           (`lhEntity_Destroy` 0x826d8138): called by the ticket's remove `sub_826D7E90`
//           (0x826d7f14, disc 0x823fdafc) — every screen's exit and respawn — and by the scene's
//           destructor. States `@Preview.Remove <ticket>`.
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "core/gui_native_calls.h"
#include "hooks/ppc_externs.h"

namespace {

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
    const char* outer = refii::vfs::SetPreviewFigureRole("playerone");
    __imp__rex_lhCoopScreen_Open(ctx, base);
    refii::vfs::SetPreviewFigureRole(outer);
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
