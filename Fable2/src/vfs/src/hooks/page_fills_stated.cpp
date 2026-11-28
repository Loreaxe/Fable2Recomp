// A PAGE FILL'S ELEMENT WRITES, STATED — see hooks/page_fills_stated.h.
//
// THE NAME. Every book page fill finds its elements by name through one function,
//   `sub_82426798(&name r3, presentation r4)` → the element (TU1; disc `sub_823F8C30`, 23
//   instructions, identical): a copy of the name (`sub_821F0240` 0x824267bc), the resolve
//   `GuiElement_Resolve_p(copy, 0, presentation)` (0x824267cc), the name destroyed (0x824267d4).
// The name wrappers call it first: text `sub_8246B888(pres, &name, &text)` (0x8246b8b8), slide
// `sub_82426D18(&name, pres, slide)` (0x82426d4c), shown `sub_82426328(&name, pres, on)` (0x8242635c),
// opacity `sub_82426140(&name, pres, f1)` (0x82426174). The name is read at the entry, before the copy
// is consumed, and kept for the element it answers while a fill is open.
//
// THE VALUES, by the calls that carry them:
//   text     SetText (TU1 0x82425DD0 / disc 0x823F8268: r3 element, r4 presentation, r5 the text) and
//            SetTextFromTag (0x82425F40 / 0x823F83D8: r3 element, r4 the tag, through the guest's own
//            lookup) — noted where the headless element is told (gui_headless_presentation.cpp);
//   slide    `lhGuiElement_GoToSlide(element r3, pres r4, slide r5)` (0x82426BA8 / 0x823F9040, 38
//            instructions, identical) — at its entry, on the element the fill named (the call hands
//            the animation manager `sub_82BB9AB0`'s answer, 0x82426c00, which can be another element);
//            `sub_82426D18`'s restart `sub_82426CA8` enters through it (0x82426cc4);
//   shown    `sub_82426260(element r3, pres r4, on r5)` (0x82426260 / 0x823F86F8, 50 instructions,
//            identical): the opacity re-scaled by 1024 for off (0x824262cc / 0x82426310) and written
//            through GuiElement_SetOpacity_p (0x82426300 / 0x8242631c) — stated as the flag it is
//            handed; its own opacity write is not a second statement;
//   opacity  `GuiElement_SetOpacity_p(element r3, pres r4, f1)` (0x82426078 / 0x823F8510, 50
//            instructions, identical), 0..100 (the furniture pages hand it 50.0 at 0x8209E0E0 and
//            100.0 at 0x8209BC68, 0x8299bc5c / 0x8299bc64);
//   record   the preview's Set `sub_828C3B30(preview, &ref)` (preview_item_stated.cpp) as "preview",
//            and upgrade furniture's card `sub_825CC7A8(piece)` (TU1; disc 0x825623C8, 86
//            instructions, identical): the `<Type>Level<n>` / `ObjectInventoryCarbonatedBed` entity
//            the page fills take the card from, whose def ref they read at +0x1C (0x8299BA80,
//            0x8299CA80; disc 0x828F15A8) — stated as "card", that ref's hash;
//   colour   `sub_82425E58(element r3, pres r4, &colour r5)` (0x82425E58 / 0x823F82F0, 57 instructions,
//            identical): four bytes `lbz 0(r5) / 1(r5) / 2(r5)` (0x82425e98 / 0x82425e90 / 0x82425e88)
//            and alpha — blue, green, red, alpha in memory (the furniture value's green is
//            `stb 0xA0, +1`, its red `stb 0xC0, +2`: 0x8299be64..0x8299be78, 0x8299bed4..0x8299bee8).
//   tint     `sub_82425C58(element r3, &colour r4)` (0x82425C58 / disc 0x823F80F0, 26 instructions,
//            identical): the quad's colour, four bytes `lbz 2(r10) / 1(r10) / 0(r10) / 3(r10)` (0x82425C88..
//            0x82425C98) — blue, green, red, alpha in memory (the item page's dye swatch `stb r31,0 ; stb r29,1 ;
//            stb r30,2 ; li 0xFF ; stb 3` 0x829B5400..0x829B5414, r30 the dye's Red) — noted at the entry, before
//            its headless early return (`lwz r11,4(r3) ; cmplwi ; beq` 0x82425C70..0x82425C78), as `RRGGBBAA`;
//   texture  `sub_82425C10(&hash r3, element r4)` (TU1; disc 0x823F80A8, 18 instructions, identical): the quad's
//            texture `lwz r10,0(r3) ; ... stw r9,0x200(r11)` (0x82425C28 / 0x82425C44) behind the headless early
//            return `lwz r11,4(r4) ; cmplwi ; beq` (0x82425C10..0x82425C18) — noted at the entry, the hash `*r3`
//            in hex; and the minimap fill's inline stores of the same field (minimap_page_stated.cpp);
//   rotation `sub_824254C8(element r3, presentation r4, f1, f2, f3)` (TU1; disc 0x823F7960, 68 instructions,
//            identical): the three attribute writes `(*(*svc+0x48))(svc, element, attr, bits)` for
//            `dword_834BE528 / 52C / 530` with f1 / f2 / f3 (0x82425560 / 0x82425588 / 0x824255B0) — stated
//            as the three radians it is handed;
//   marks    what a fill makes that is no element write (the minimap fill's projection `map` and its icons
//            `icon`, minimap_page_stated.cpp), in call order;
//
// THE STATEMENT: `@PageFill <screen> <scope> <key>` then per write, in the fill's order, its kind
// (`text`, `slide`, `shown`, `opacity`, `colour`, `tint`, `texture`, `rotation`), the path the fill named
// and the value (UTF-8 text; a decimal slide; 1 / 0; the opacity 0..100 as written; `RRGGBBAA` hex; the
// texture hash `%08X`; `x y z` radians), then the records, then the marks (`<kind> <label> <value>`).
// Godot holds the last one per screen and scope (Engine_UiPageFills).
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <iterator>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include <rex/logging.h>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "core/gui_native_calls.h"
#include "hooks/page_fills_stated.h"
#include "hooks/ppc_externs.h"

namespace {

struct Written {
    const char* kind;
    uint32_t element;
    std::string value;
};

struct Fill {
    bool open = false;
    uint32_t screen = 0u;
    std::string scope;
    std::unordered_map<uint32_t, std::string> names;
    std::vector<Written> writes;
    std::vector<std::pair<std::string, std::string>> records;
    std::vector<Written> marks;
    std::vector<std::string> mark_labels;
    uint32_t unnamed = 0u;
};

thread_local Fill t_fill;

/// Opens on this thread; only the outermost close states.
thread_local uint32_t t_depth = 0u;

/// A restated scope's last statement, by "screen/scope" (guest thread only).
std::unordered_map<std::string, std::vector<std::string>> g_restated;

/// Each outermost open's number on this thread (PageFillSerial).
thread_local uint32_t t_serial = 0u;

/// Inside `sub_82426260`: its opacity write states the flag, not a second value.
thread_local bool t_in_shown = false;

/// What the last outermost close did (LastPageFillClosed).
thread_local refii::vfs::PageFillClosed t_closed;

/// "kind n, kind n, …" over the stated triples, by kind in first-seen order.
std::string CountKinds(const std::vector<std::string>& said) {
    std::vector<std::pair<std::string, uint32_t>> counts;
    for (size_t i = 0; i + 2 < said.size(); i += 3) {
        auto it = counts.begin();
        while (it != counts.end() && it->first != said[i]) {
            ++it;
        }
        if (it == counts.end()) {
            counts.emplace_back(said[i], 1u);
        } else {
            ++it->second;
        }
    }
    std::string out;
    for (const auto& [kind, n] : counts) {
        out += (out.empty() ? "" : ", ") + kind + " " + std::to_string(n);
    }
    return out;
}

std::string Utf8(const std::vector<uint16_t>& units) {
    std::string out;
    out.reserve(units.size());
    for (const uint16_t unit : units) {
        if (unit == 0u) {
            break;
        }
        if (unit < 0x80u) {
            out.push_back(static_cast<char>(unit));
        } else if (unit < 0x800u) {
            out.push_back(static_cast<char>(0xC0u | (unit >> 6)));
            out.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
        } else {
            out.push_back(static_cast<char>(0xE0u | (unit >> 12)));
            out.push_back(static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu)));
            out.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
        }
    }
    return out;
}

void Note(const char* kind, uint32_t element, std::string value) {
    if (!t_fill.open || element == 0u) {
        return;
    }
    t_fill.writes.push_back(Written{kind, element, std::move(value)});
}

void Add(refii::vfs::GuiNativeCall& call, std::string text) {
    refii::vfs::GuiNativeArgument argument;
    argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    argument.text = std::move(text);
    call.arguments.push_back(std::move(argument));
}

}  // namespace

namespace refii::vfs {

void OpenPageFill(uint32_t screen, const char* scope) {
    if (t_depth++ != 0u) {
        return;
    }
    ++t_serial;
    t_closed = PageFillClosed{};
    t_fill = Fill{};
    t_fill.open = true;
    t_fill.screen = screen;
    t_fill.scope = scope;
}

bool PageFillOpen() { return t_fill.open; }

uint32_t PageFillSerial() { return t_serial; }

void ForgetRestatedPageFills(uint32_t screen) {
    const std::string prefix = std::to_string(screen) + "/";
    for (auto it = g_restated.begin(); it != g_restated.end();) {
        it = it->first.rfind(prefix, 0) == 0 ? g_restated.erase(it) : std::next(it);
    }
}

const PageFillClosed& LastPageFillClosed() { return t_closed; }

void ClosePageFill(uint32_t key, bool restated) {
    if (t_depth == 0u || --t_depth != 0u || !t_fill.open) {
        return;
    }
    t_closed = PageFillClosed{};
    Fill fill = std::move(t_fill);
    t_fill = Fill{};

    std::vector<std::string> said;
    said.reserve(fill.writes.size() * 3u);
    for (const Written& write : fill.writes) {
        const auto name = fill.names.find(write.element);
        if (name == fill.names.end()) {
            ++fill.unnamed;
            continue;
        }
        said.emplace_back(write.kind);
        said.push_back(name->second);
        said.push_back(write.value);
    }
    for (const auto& [what, hex] : fill.records) {
        said.emplace_back("record");
        said.push_back(what);
        said.push_back(hex);
    }
    for (size_t i = 0; i < fill.marks.size(); ++i) {
        said.emplace_back(fill.marks[i].kind);
        said.push_back(fill.mark_labels[i]);
        said.push_back(fill.marks[i].value);
    }
    t_closed.unnamed = fill.unnamed;
    if (fill.unnamed != 0u) {
        static uint32_t s_reported = 0u;
        if (s_reported < 8u) {
            ++s_reported;
            REXLOG_INFO("Refii page fill: screen {} '{}' wrote {} element(s) it did not find by name — not stated",
                        fill.screen, fill.scope, fill.unnamed);
        }
    }
    if (said.empty()) {
        return;
    }
    const std::string prefix = std::to_string(fill.screen) + "/";
    if (restated) {
        std::vector<std::string>& last = g_restated[prefix + fill.scope];
        said.insert(said.begin(), std::to_string(key));
        if (last == said) {
            t_closed.unchanged = true;
            return;
        }
        last = said;
        said.erase(said.begin());
    } else {
        ForgetRestatedPageFills(fill.screen);
    }

    t_closed.posted = true;
    t_closed.counts = CountKinds(said);
    for (size_t i = 0; i + 2 < said.size(); i += 3) {
        if (said[i] == "map") {
            t_closed.map = said[i + 2];
            break;
        }
    }
    GuiNativeCall call;
    call.name = "@PageFill";
    Add(call, std::to_string(fill.screen));
    Add(call, fill.scope);
    Add(call, std::to_string(key));
    for (std::string& text : said) {
        Add(call, std::move(text));
    }
    PostGuiNativeCall(std::move(call));
}

void NotePageFillText(uint32_t element, const std::vector<uint16_t>& units) { Note("text", element, Utf8(units)); }

void NotePageFillTexture(uint32_t element, uint32_t hash) {
    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", hash);
    Note("texture", element, hex);
}

void NotePageFillMark(const char* kind, std::string label, std::string value) {
    if (!t_fill.open || label.empty()) {
        return;
    }
    t_fill.marks.push_back(Written{kind, 0u, std::move(value)});
    t_fill.mark_labels.push_back(std::move(label));
}

void NotePageFillRecord(const char* what, uint32_t record) {
    if (!t_fill.open || record == 0u) {
        return;
    }
    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", record);
    t_fill.records.emplace_back(what, hex);
}

}  // namespace refii::vfs

// `sub_82426798(&name r3, presentation r4)` (TU1; disc 0x823F8C30): the name every fill finds its
// elements by. Read at the entry — the call consumes its copy (0x824267d4).
REX_EXTERN(rex_lhGuiElement_FindByName) {
    REX_FUNC_PROLOGUE();
    if (!t_fill.open) {
        __imp__rex_lhGuiElement_FindByName(ctx, base);
        return;
    }
    std::string name = refii::vfs::ReadGuestLhString(base, ctx.r3.u32);
    __imp__rex_lhGuiElement_FindByName(ctx, base);
    if (ctx.r3.u32 != 0u && !name.empty()) {
        t_fill.names[ctx.r3.u32] = std::move(name);
    }
}

// `sub_82426260(element r3, presentation r4, on r5)` (TU1; disc 0x823F86F8): shown or not.
REX_EXTERN(rex_lhGuiElement_SetShown) {
    REX_FUNC_PROLOGUE();
    if (!t_fill.open) {
        __imp__rex_lhGuiElement_SetShown(ctx, base);
        return;
    }
    if (ctx.r4.u32 != 0u) {
        Note("shown", ctx.r3.u32, (ctx.r5.u32 & 0xFFu) != 0u ? "1" : "0");
    }
    const bool outer = t_in_shown;
    t_in_shown = true;
    __imp__rex_lhGuiElement_SetShown(ctx, base);
    t_in_shown = outer;
}

// `GuiElement_SetOpacity_p(element r3, presentation r4, f1)` (TU1 0x82426078; disc 0x823F8510).
REX_EXTERN(rex_lhGuiElement_SetOpacity) {
    REX_FUNC_PROLOGUE();
    if (t_fill.open && !t_in_shown && ctx.r4.u32 != 0u) {
        char value[32];
        std::snprintf(value, sizeof value, "%g", static_cast<float>(ctx.f1.f64));
        Note("opacity", ctx.r3.u32, value);
    }
    __imp__rex_lhGuiElement_SetOpacity(ctx, base);
}

// `sub_82425E58(element r3, presentation r4, &colour r5)` (TU1; disc 0x823F82F0): blue, green, red,
// alpha bytes, stated as RRGGBBAA.
REX_EXTERN(rex_lhGuiElement_SetTextColour) {
    REX_FUNC_PROLOGUE();
    if (t_fill.open && ctx.r4.u32 != 0u && ctx.r5.u32 >= 0x10000u && ctx.r5.u32 < 0xC0000000u) {
        const uint32_t at = ctx.r5.u32;
        char value[16];
        std::snprintf(value, sizeof value, "%02X%02X%02X%02X", refii::vfs::LoadGuestU8(base, at + 2u),
                      refii::vfs::LoadGuestU8(base, at + 1u), refii::vfs::LoadGuestU8(base, at),
                      refii::vfs::LoadGuestU8(base, at + 3u));
        Note("colour", ctx.r3.u32, value);
    }
    __imp__rex_lhGuiElement_SetTextColour(ctx, base);
}

// `sub_82425C58(element r3, &colour r4)` (TU1; disc 0x823F80F0): the quad's colour, blue, green, red, alpha
// bytes, stated as RRGGBBAA. Its callers: the item books' dye swatch (`sub_829B5160` 0x829B5418, `sub_829B6310`
// 0x829B69D0), the shop's item page (`sub_82992998` 0x82992D58), the co-op screen (`sub_8243B520` 0x8243B60C) and
// `sub_82804CC0` (0x82804D48) — noted only inside a fill.
REX_EXTERN(rex_lhGuiElement_SetQuadColour) {
    REX_FUNC_PROLOGUE();
    if (t_fill.open && ctx.r4.u32 >= 0x10000u && ctx.r4.u32 < 0xC0000000u) {
        const uint32_t at = ctx.r4.u32;
        char value[16];
        std::snprintf(value, sizeof value, "%02X%02X%02X%02X", refii::vfs::LoadGuestU8(base, at + 2u),
                      refii::vfs::LoadGuestU8(base, at + 1u), refii::vfs::LoadGuestU8(base, at),
                      refii::vfs::LoadGuestU8(base, at + 3u));
        Note("tint", ctx.r3.u32, value);
    }
    __imp__rex_lhGuiElement_SetQuadColour(ctx, base);
}

// `sub_82425C10(&hash r3, element r4)` (TU1; disc 0x823F80A8): the quad's texture, `*r3`. Its only callers are
// the minimap fill's four legend rows for a region the hero is not in (`sub_829ABFC0` 0x829ADD94 / 0x829ADE4C /
// 0x829ADF04 / 0x829ADFBC; disc `sub_828FE480` 0x82900254 / 0x8290030C / 0x829003C4 / 0x8290047C) — noted only
// inside a fill.
REX_EXTERN(rex_lhGuiElement_SetTextureHash) {
    REX_FUNC_PROLOGUE();
    if (t_fill.open && ctx.r3.u32 >= 0x10000u && ctx.r3.u32 < 0xC0000000u) {
        refii::vfs::NotePageFillTexture(ctx.r4.u32, refii::vfs::LoadGuestU32(base, ctx.r3.u32));
    }
    __imp__rex_lhGuiElement_SetTextureHash(ctx, base);
}

// `sub_824254C8(element r3, presentation r4, f1, f2, f3)` (TU1; disc 0x823F7960): the element's rotation, the
// three radians as handed (`fmr f31,f1 ; fmr f30,f2 ; fmr f29,f3` 0x824254EC..0x824254F4). Eleven callers; noted
// only inside a fill (the minimap's map image, 0x829AC7C4 / disc 0x828FEC84).
REX_EXTERN(rex_lhGuiElement_SetRotation) {
    REX_FUNC_PROLOGUE();
    if (t_fill.open && ctx.r4.u32 != 0u) {
        char value[64];
        std::snprintf(value, sizeof value, "%g %g %g", static_cast<float>(ctx.f1.f64), static_cast<float>(ctx.f2.f64),
                      static_cast<float>(ctx.f3.f64));
        Note("rotation", ctx.r3.u32, value);
    }
    __imp__rex_lhGuiElement_SetRotation(ctx, base);
}

// `lhGuiElement_GoToSlide(element r3, presentation r4, slide r5)` (TU1 0x82426BA8; disc 0x823F9040).
REX_EXTERN(rex_lhGuiElement_GoToSlide) {
    REX_FUNC_PROLOGUE();
    if (t_fill.open && ctx.r3.u32 != 0u && ctx.r4.u32 != 0u) {
        Note("slide", ctx.r3.u32, std::to_string(static_cast<int32_t>(ctx.r5.u32)));
    }
    __imp__rex_lhGuiElement_GoToSlide(ctx, base);
}

// THE SHOP'S FRONT PAGE (22). Its fill `sub_82991800(page)` (TU1; disc 0x828E31B0, 1002 instructions,
// identical) is the front page's vf +4 (`b sub_82991800` at 0x829917F8; vtable 0x820EA670 / disc
// 0x820E5150, +4 0x828E31A8 `b sub_828E31B0`) and runs once, from the shop's OnEnter (0x826ADDC0 /
// 0x82592340): the shop's name or the trader's title, its anecdote, the star slide, the wealth line,
// the keeper's name, the owner's or the opinions group, the sale or shortage (element names
// 0x8349CE74..0x8349CEC4, initialised from "Foreground.FrontPage.Contents.…"). Stated as scope "front".
REX_EXTERN(rex_lhGuiShopFrontPage_Fill) {
    REX_FUNC_PROLOGUE();
    refii::vfs::OpenPageFill(22u, "front");
    __imp__rex_lhGuiShopFrontPage_Fill(ctx, base);
    refii::vfs::ClosePageFill(0u);
}

// Upgrade furniture's card `sub_825CC7A8(piece r3)` (TU1; disc 0x825623C8): the entity it returns,
// taken by the fills only with `card+0x90 & 0x40` set (`lbz r11,0x90(r3)` / `rlwinm r10,r11,0,25,25`,
// 0x8299BA5C / disc 0x828F1584), its def ref at card+0x1C (0x8299BA80 / 0x8299CA80; disc 0x828F15A8).
REX_EXTERN(rex_lhUpgradeableFurniture_Card) {
    REX_FUNC_PROLOGUE();
    __imp__rex_lhUpgradeableFurniture_Card(ctx, base);
    const uint32_t card = ctx.r3.u32;
    if (t_fill.open && card >= 0x10000u && card < 0xC0000000u &&
        (refii::vfs::LoadGuestU8(base, card + 0x90u) & 0x40u) != 0u) {
        refii::vfs::NotePageFillRecord("card", refii::vfs::LoadGuestU32(base, card + 0x1Cu));
    }
}

// THE REAL-ESTATE PAGES (28), filled once at the open by the book's registration (`sub_82990610`, each
// page's vf +4): page 1 `sub_82998FB0` (TU1; disc 0x828ED8C0, 1313 lines, identical; vtable 0x820EB80C
// / disc 0x820E6420, +4) — the title, type, star slide (`sub_8299AF88`), owner caption and name, base
// value, decoration, economy, sale price, the four Variable rows shown or hidden, the bonuses — and
// page 2 `sub_8299B068` (disc 0x828EF980, 369 lines, identical; vtable 0x820EB948 / 0x820E655C, +4) —
// the title, address and history. Their texts go through `sub_8299A438` / `sub_8299A4B0` (name lookup
// 0x8299A460 / 0x8299A4DC, SetText 0x8299A474 / 0x8299A4F4, colour 0x8299A510). Scopes "page1" and
// "page2". Page 1's vf +12 `sub_8299A728` (disc 0x828EF040) restates the rent / profit row each tick:
// gui_screen_stub_real_estate.cpp, scope "tick".
REX_EXTERN(rex_lhGuiRealEstatePage1_Fill) {
    REX_FUNC_PROLOGUE();
    refii::vfs::OpenPageFill(28u, "page1");
    __imp__rex_lhGuiRealEstatePage1_Fill(ctx, base);
    refii::vfs::ClosePageFill(0u);
}

REX_EXTERN(rex_lhGuiRealEstatePage2_Fill) {
    REX_FUNC_PROLOGUE();
    refii::vfs::OpenPageFill(28u, "page2");
    __imp__rex_lhGuiRealEstatePage2_Fill(ctx, base);
    refii::vfs::ClosePageFill(0u);
}
