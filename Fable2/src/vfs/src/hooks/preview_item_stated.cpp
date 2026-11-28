// THE ITEM A PANEL PREVIEWS, STATED. A screen's 3D item preview is a plain struct inside its handler.
//   set    `sub_828C3B30(preview r3, recordRef r4)` (TU1; disc `sub_828348F8`): `*a1 = *a2`
//          (0x828c3b54) takes the item's GDB record hash from the `{hash, cache}` ref it is handed,
//          then builds the model and offsets from that record. Callers: the screens' page fills
//          (shop `sub_82992998` 0x82992c5c, the item books, gift, furniture), which run for the row
//          the highlight reader answers — Godot's (gui_menu_hooks.cpp) — and the receive-items card.
//   hide   `sub_828C3FD8(preview r3)` (disc `sub_82834DA0`): clears the spawned byte +64.
// The screen is the one whose handler — the guest's own lookup — holds the struct at that screen's
// offset: 8 at +96, 22 at +160 (the shop's pages +128, preview +32: 0x826b1408, 0x82992c5c; disc
// 0x82595908, 0x828e4624), 15/16/25/26/27 at +128. States `@PreviewItem.Set <screen> <record hex>`
// and `@PreviewItem.Hide <screen>`; Godot resolves the model and placement from the record.
#include <cstdint>
#include <cstdio>
#include <string>

#include "vfs_hooks_private.h"
#include "core/gui_native_calls.h"
#include "hooks/page_fills_stated.h"
#include "hooks/ppc_externs.h"

namespace {

struct PreviewSlot {
    uint32_t screen;
    uint32_t offset;
};

constexpr PreviewSlot kSlots[] = {
    {8u, 96u}, {22u, 160u}, {15u, 128u}, {16u, 128u}, {25u, 128u}, {26u, 128u}, {27u, 128u},
};

uint32_t ScreenOfPreview(PPCContext& ctx, uint8_t* base, uint32_t preview) {
    for (const PreviewSlot& slot : kSlots) {
        const uint32_t handler = refii::vfs::InGameScreenHandler(ctx, base, slot.screen);
        if (handler != 0u && handler + slot.offset == preview) {
            return slot.screen;
        }
    }
    return 0u;
}

// What each screen's preview was last stated as, so a hide the items screen repeats every frame
// its highlight is a category (0x826d17c0) is stated once. Guest thread only.
struct Said {
    bool shown = false;
    uint32_t record = 0u;
    bool known = false;
};
Said g_said[32];

void Post(const char* name, uint32_t screen, const std::string* record) {
    refii::vfs::GuiNativeCall call;
    call.name = name;
    refii::vfs::GuiNativeArgument argument;
    argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    argument.text = std::to_string(screen);
    call.arguments.push_back(argument);
    if (record != nullptr) {
        argument.text = *record;
        call.arguments.push_back(argument);
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

REX_EXTERN(rex_lhGuiPreviewObject_Set) {
    REX_FUNC_PROLOGUE();
    const uint32_t preview = ctx.r3.u32;
    const uint32_t ref = ctx.r4.u32;
    const uint32_t record = ref >= 0x10000u && ref < 0xC0000000u ? refii::vfs::LoadGuestU32(base, ref) : 0u;
    const uint32_t screen = ScreenOfPreview(ctx, base, preview);
    // A relay (page_fills_stated.cpp: the online joiner's shop item pane, run on the host): the record is the
    // statement; this machine builds no preview model and keeps no "said" for it — the joiner shows it.
    if (refii::vfs::PageFillRelaying()) {
        refii::vfs::NotePageFillRecord("preview", record);
        if (screen != 0u) {
            char hex[9];
            std::snprintf(hex, sizeof hex, "%08X", record);
            const std::string text = hex;
            Post("@PreviewItem.Set", screen, &text);
        }
        return;
    }
    __imp__rex_lhGuiPreviewObject_Set(ctx, base);
    refii::vfs::NotePageFillRecord("preview", record);

    if (screen == 0u) {
        static uint32_t s_unplaced = 0u;
        if (s_unplaced != preview) {
            s_unplaced = preview;
            REXLOG_INFO("Refii preview item: set on {:08X}, in no known screen's handler", preview);
        }
        return;
    }

    Said& said = g_said[screen];
    if (said.known && said.shown && said.record == record) {
        return;
    }
    said = Said{true, record, true};

    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", record);
    const std::string text = hex;
    Post("@PreviewItem.Set", screen, &text);
}

REX_EXTERN(rex_lhGuiPreviewObject_Hide) {
    REX_FUNC_PROLOGUE();
    const uint32_t screen = ScreenOfPreview(ctx, base, ctx.r3.u32);
    if (refii::vfs::PageFillRelaying()) {
        if (screen != 0u) {
            Post("@PreviewItem.Hide", screen, nullptr);
        }
        return;
    }
    __imp__rex_lhGuiPreviewObject_Hide(ctx, base);
    if (screen == 0u) {
        return;
    }
    Said& said = g_said[screen];
    if (said.known && !said.shown) {
        return;
    }
    said = Said{false, 0u, true};
    Post("@PreviewItem.Hide", screen, nullptr);
}
