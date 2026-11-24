#include <atomic>
#include <string>
#include <utility>

#include "vfs_hooks_private.h"
#include "gui/gui_menu_chunks.h"

// Open → OpenInternal → FindAndCreateStream → Submit each have silent fail paths ending in a
// NULL manifest; these hooks log each stage's verdict.

REX_EXTERN(rex_lhVFS_RequestManager_SubmitRequest) {
  REX_FUNC_PROLOGUE();
  const uint32_t mgr = ctx.r3.u32;
  const uint32_t item = ctx.r4.u32;
  const uint32_t shutdown =
      mgr != 0 ? (refii::vfs::LoadGuestU32(base, mgr + 152u) >> 24) : 0xFFu;
  __imp__rex_lhVFS_RequestManager_SubmitRequest(ctx, base);
  static std::atomic<uint32_t> logged{0};
  if (logged.fetch_add(1, std::memory_order_relaxed) < 8u) {
    REXLOG_INFO("Refii RequestManager Submit: mgr=0x{:08X} shutdown_byte={} item=0x{:08X} "
                "item_vtbl=0x{:08X} -> result=0x{:08X}",
                mgr, shutdown, item,
                item != 0 ? refii::vfs::LoadGuestU32(base, item) : 0u,
                ctx.r3.u32);
  }
}

REX_EXTERN(rex_lhTextureStreamRequest_OpenStream) {
  REX_FUNC_PROLOGUE();

  // Replicates REFII_VFS_NATIVE_PASSTHROUGH but keeps the outcome log, which the macro's early
  // return would skip.
  {
    const uint32_t native_item = ctx.r3.u32;
    __imp__rex_lhTextureStreamRequest_OpenStream(ctx, base);
    const uint32_t success_byte =
        refii::vfs::LoadGuestU32(base, native_item + 116u) >> 24;
    static std::atomic<uint32_t> logged_native_pool_execs{0};
    const uint32_t exec_count =
        logged_native_pool_execs.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (exec_count <= 32u) {
      REXLOG_INFO(
          "Refii pool stream-in exec #{} item=0x{:08X} success={} "
          "hash=0x{:08X} off8=0x{:08X} sz12=0x{:08X} sz16=0x{:08X} "
          "off20=0x{:08X} flags28=0x{:08X} mode104={} sel112={} "
          "off48=0x{:08X}",
          exec_count, native_item, success_byte,
          refii::vfs::LoadGuestU32(base, native_item + 4u),
          refii::vfs::LoadGuestU32(base, native_item + 8u),
          refii::vfs::LoadGuestU32(base, native_item + 12u),
          refii::vfs::LoadGuestU32(base, native_item + 16u),
          refii::vfs::LoadGuestU32(base, native_item + 20u),
          refii::vfs::LoadGuestU32(base, native_item + 28u),
          refii::vfs::LoadGuestU32(base, native_item + 104u),
          refii::vfs::LoadGuestU32(base, native_item + 112u),
          refii::vfs::LoadGuestU32(base, native_item + 48u));
    }
    return;
  }
}

REX_EXTERN(rex_lhResourceManager_OpenInternal) {
  REX_FUNC_PROLOGUE();

  // Passthrough with the verdict stated — see the chain note above FindAndCreateStream.
  {
    const PPCContext entry_ctx = ctx;
    __imp__rex_lhResourceManager_OpenInternal(ctx, base);
    static std::atomic<uint32_t> chain_logged{0};
    if (chain_logged.fetch_add(1, std::memory_order_relaxed) < 8u) {
      const uint32_t out_object =
          entry_ctx.r6.u32 != 0 ? refii::vfs::LoadGuestU32(base, entry_ctx.r6.u32) : 0u;
      // The proxy's inner is what every slot forwards to.
      const uint32_t inner =
          out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object + 12u) : 0u;
      REXLOG_INFO("Refii OpenInternal: manifest=0x{:08X} size_arg=0x{:08X} out=0x{:08X} "
                  "result={} object=0x{:08X} vtable=0x{:08X} words=[{:08X} {:08X} {:08X}] "
                  "inner=0x{:08X} inner_vtbl=0x{:08X} inner[+24]={} inner[+124]=0x{:08X} "
                  "inner[+128]={} inner[+200]={}",
                  entry_ctx.r3.u32, entry_ctx.r5.u32, entry_ctx.r6.u32,
                  ctx.r3.u32, out_object,
                  out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object) : 0u,
                  out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object + 4u) : 0u,
                  out_object != 0 ? refii::vfs::LoadGuestU32(base, out_object + 8u) : 0u,
                  inner,
                  inner,
                  inner != 0 ? refii::vfs::LoadGuestU32(base, inner) : 0u,
                  inner != 0 ? refii::vfs::LoadGuestU32(base, inner + 24u) : 0u,
                  inner != 0 ? refii::vfs::LoadGuestU32(base, inner + 124u) : 0u,
                  inner != 0 ? refii::vfs::LoadGuestU32(base, inner + 128u) : 0u,
                  inner != 0 ? refii::vfs::LoadGuestU32(base, inner + 200u) : 0u);
    }
    return;
  }

  const PPCContext original_ctx = ctx;
  if (refii::vfs::ShouldLogVfsTraceOperation(
          "ResourceManager_OpenInternal")) {
    REXLOG_INFO("VFS trace ResourceManager_OpenInternal enter lr=0x{:08X} "
                "manager={} r4=0x{:08X} r5=0x{:08X} out=0x{:08X} "
                "r7=0x{:08X}({}) r8=0x{:08X} manager_ascii_32={}",
                original_ctx.lr,
                refii::vfs::DescribeGuestObject(
                    base, original_ctx.r3.u32, 16, 12),
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                original_ctx.r6.u32,
                original_ctx.r7.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r7.u32),
                original_ctx.r8.u32,
                refii::vfs::FormatGuestAsciiBytes(
                    base, original_ctx.r3.u32 + 32, 160));
  }

  __imp__rex_lhResourceManager_OpenInternal(ctx, base);

  if (refii::vfs::ShouldLogVfsTraceOperation(
          "ResourceManager_OpenInternal")) {
    const uint32_t out_stream =
        refii::vfs::TryReadOutputPointer(base, original_ctx.r6.u32);
    REXLOG_INFO("VFS trace ResourceManager_OpenInternal exit lr=0x{:08X} "
                "result=0x{:08X} out=0x{:08X} out_stream={}",
                original_ctx.lr,
                ctx.r3.u32,
                original_ctx.r6.u32,
                refii::vfs::DescribeGuestObject(base, out_stream, 12, 12));
  }
}

/// THE RUNNER, `sub_82426470(chunk, presentation)` (TU1, disc `sub_823F8908`) — the one door
/// every C++→Lua UI chunk passes: 127 `bl` sites per build, and its core `sub_82372500` (disc
/// `sub_82306718`) has exactly two callers, this function (0x824264C8 / 0x823F8960) and the
/// deferred drain `sub_821738A0` (0x821739A0; disc `sub_82173718` 0x82173818). The original runs
/// the chunk while the GUI's Lua window `GUI+0x64` is set, else queues it in the
/// `std::map<presentation, list<LHString>>` at GUI+0x58, whose only writer is this function
/// (`sub_823726C8`, one caller at 0x824264E8 / disc `sub_823068E0` at 0x823F8980).
///
/// THE GUEST'S UI LUA IS CUT HERE. The chunk is recorded for Godot and never run, never queued —
/// so the drain finds the map empty, and the core is swallowed besides
/// (`rex_lhGuiPresentation_RunLuaString`, gui_menu_hooks.cpp). That includes the presentation
/// init `GUI:StoreScriptsDirectoryAndLoadAppropriateScripts()` (0x823DCD28 / 0x823FEA40), the
/// only `dofile` of `GUISetup.lua` in a shipped build (`uicontract.lua`), so no presentation
/// carries `g_Menu`, the event queue or the `OnPressA`/`OnPressB` handlers
/// (`expandablemenuinput.lua`) the UIContract fires (`sub_826D58F0` 0x826D597C, `sub_826D59C8`
/// 0x826D5A54, `sub_82405190` 0x82405384, the front-end root 0x826DF4A4 / 0x826DFC64) reach.
/// Those fires' C++ consequences — the root test, the sounds, the screen exits — are outside
/// the Lua and run unchanged.
///
/// Every read-back of a Lua result is answered natively: `GetHighlightedMenuItemData`
/// (`sub_826C1B50`), `IsHighlightedItemAtRoot` (`sub_826C1D98`, `sub_826DED60`) are replaced
/// whole; the event pumps' `EventQueueSize` (0x823704D0, 0x82379410) and `CountSpawnedItems`'s
/// `Float1` (0x82998F24) are answered by the getter hook. The rest are Lua→C++→Lua round trips
/// with no C++ state behind them (`GetMenuItemDataByName` 0x823DF7E4 → `RefreshButtonText`,
/// `GetLastItemIndex` 0x826CE75C → `SetItemEmphasisByIndex`) or dead (the front end's
/// `GetHighlightedMenuItemData`, `sub_826E4AE8`: its out slots are never loaded after
/// 0x826DED20).
///
/// Recorded, in submission order, with what the stagers staged: every `g_Menu:` /
/// `g_MenuFormatter:` statement and each presentation's script init. A `...Selected...`
/// statement and `PushMenuState` carry Godot's stated highlighted row (`@Highlighted`), the row
/// the script's `CurrentHighlightIdx` would have meant.
REX_EXTERN(rex_lhGui_RunLuaString) {
  REX_FUNC_PROLOGUE();

  // r4 == 0 is a chunk the original discards outright (`beq cr6` at 0x82426488).
  const uint32_t presentation = ctx.r4.u32;
  if (presentation == 0) {
    return;
  }

  // r3 is an LHString handle — `char***`, two dereferences, as `sub_82372500` reads it
  // (`lwz r11, 0(r30)` / `lwz r6, 0(r11)`).
  std::string chunk = refii::vfs::ReadGuestLhString(base, ctx.r3.u32);
  if (chunk.empty()) {
    chunk = refii::vfs::ReadMaybeGuestString(base, ctx.r3.u32, 512);
  }

  if (chunk.find("g_Menu") != std::string::npos ||
      chunk.find(refii::vfs::kPresentationInitChunk) != std::string::npos) {
    const bool names_selected =
        chunk.find("g_Menu:SetSelectedItemGhosting") != std::string::npos ||
        chunk.find("g_Menu:SetSelectedItemEmphasis") != std::string::npos ||
        chunk.find("g_Menu:RefreshSelectedButtonText") != std::string::npos ||
        chunk.find("g_Menu:RenameSelectedButton") != std::string::npos ||
        chunk.find("g_Menu:PushMenuState") != std::string::npos;
    std::string highlighted;
    int32_t highlighted_id = 0;
    if (names_selected && refii::vfs::StatedHighlightName(base, &highlighted, &highlighted_id)) {
      refii::vfs::StageMenuAttribute(refii::vfs::kHighlightedAttribute, highlighted, 0.0f,
                                     /*is_number=*/false);
      refii::vfs::StageMenuAttribute(refii::vfs::kHighlightedIdAttribute,
                                     std::to_string(static_cast<uint32_t>(highlighted_id)), 0.0f,
                                     /*is_number=*/false);
    }

    refii::vfs::RunMenuChunk(chunk, presentation);

    // The capture's list boundary, keyed by the screen the guest states. `sub_826CE110` is the
    // class-level clear the pause and inventory paths reach; the logbook submits its
    // repopulation inline (0x826D8E34), so the boundary is taken from the chunk text.
    if (chunk.find("g_Menu:DeleteAllSpawnedMenuItems") != std::string::npos) {
      uint32_t screen_id = 0;
      refii_bridge_menu_spawn_begin_for(
          refii::vfs::StatedInGameScreen(&screen_id) ? screen_id : 0xFFFFFFFFu);
    }

    // `g_Menu:CountSpawnedItems()` — the real-estate populate `sub_82998300` (disc
    // `sub_828ECC10`) runs it at 0x82998ECC (disc 0x828ED7DC) and reads `Float1` at 0x82998F24
    // (disc 0x828ED834), adding `GUI_BACK` (`li r8, 0x78` at 0x82998F78) below 1.0
    // (`fcmpu` at 0x82998F40). The answer is the root rows that populate stated, handed to that
    // one read.
    // `g_Menu:GetLastItemIndex()` — the spawn helper `sub_826CE230` (disc `sub_82407D20`) runs
    // it at 0x826ce708 (disc 0x824081f8) and reads `Float1` at 0x826ce75c (disc 0x8240824c) into
    // `sub_826D0890`, which states the row's worn/equipped emphasis by that index
    // (`SetItemEmphasisByIndex`, 0x826d0be4). The script answers `NumberOfItems - 1`, the index
    // of the row just spawned; the answer is that row's place in this presentation's populate.
    if (chunk.find("g_Menu:GetLastItemIndex") != std::string::npos) {
      refii::vfs::AnswerParamNumber(
          presentation, "Float1", static_cast<float>(refii::vfs::MenuLastSpawnOrdinal(presentation)));
    }

    // `g_Menu:GetMenuItemDataByName()` — `sub_823DF660` (disc `sub_8283F948`) runs it at
    // 0x823df7ac (disc 0x8283fa94) and reads `String2` at 0x823df7e4 (disc 0x8283facc): the named
    // row's text tag, which the recorder answered from this presentation's populate; a name it has
    // not got gets the script's own miss, "No Text Tag" (`expandablemenu.lua`).
    if (chunk.find("g_Menu:GetMenuItemDataByName") != std::string::npos) {
      std::string text_tag;
      if (!refii::vfs::MenuByNameTextTag(presentation, &text_tag)) {
        static std::atomic<uint64_t> missed{0};
        const uint64_t m = missed.fetch_add(1, std::memory_order_relaxed) + 1u;
        if (m <= 16u || (m & (m - 1u)) == 0u) {
          REXLOG_INFO("Refii menu: GetMenuItemDataByName on presentation 0x{:08X} names no row — "
                      "String2 'No Text Tag' (#{})",
                      presentation, m);
        }
        refii::vfs::AnswerParamText(presentation, "String2", "No Text Tag");
      } else {
        static std::atomic<uint64_t> answered{0};
        const uint64_t a = answered.fetch_add(1, std::memory_order_relaxed) + 1u;
        if (a <= 16u || (a & (a - 1u)) == 0u) {
          REXLOG_INFO("Refii menu: GetMenuItemDataByName on presentation 0x{:08X} answered "
                      "natively — text tag '{}' (#{})",
                      presentation, text_tag, a);
        }
        refii::vfs::AnswerParamText(presentation, "String2", text_tag);
      }
    }

    if (chunk.find("g_Menu:CountSpawnedItems") != std::string::npos) {
      const uint32_t rows = refii::vfs::MenuRootRows(presentation);
      refii::vfs::AnswerParamNumber(presentation, "Float1", static_cast<float>(rows));
      REXLOG_INFO("Refii menu: CountSpawnedItems on presentation 0x{:08X} answered natively — "
                  "{} root row(s)",
                  presentation, rows);
    }
  }

  static std::atomic<uint64_t> cut{0};
  const uint64_t n = cut.fetch_add(1, std::memory_order_relaxed) + 1u;
  if (n <= 16u || (n & (n - 1u)) == 0u) {
    REXLOG_INFO("Refii menu: the guest's UI Lua is cut — chunk #{} '{}' on 0x{:08X} not run",
                n, chunk.substr(0, 96), presentation);
  }
}
