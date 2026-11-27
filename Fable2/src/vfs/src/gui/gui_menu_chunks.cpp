#include "gui/gui_menu_chunks.h"
#include "core/queue_put_back.h"

#include <algorithm>
#include <atomic>
#include <charconv>
#include <cstring>
#include <mutex>
#include <string>
#include <system_error>
#include <unordered_map>
#include <utility>

namespace refii::vfs {

/// The spawn capture's in-place restate (core/guest_tick_pump_state.cpp).
bool RestateCapturedMenuRow(const std::string& name, const std::string* parent, const uint32_t* id,
                            const std::string* new_name, const std::string* new_label);

namespace {

/// One mutex covers both the staged bag and the completed-call queue: a stage and a run are
/// two halves of one transaction, and a host drain must not land between them.
std::mutex& Mutex() {
    static std::mutex mutex;
    return mutex;
}

std::vector<MenuChunkAttribute>& Staged() {
    static std::vector<MenuChunkAttribute> staged;
    return staged;
}

std::vector<MenuChunkCall>& Pending() {
    static std::vector<MenuChunkCall> pending;
    return pending;
}

std::atomic<uint64_t> g_recorded{0};
std::atomic<uint64_t> g_dropped{0};

/// Ceiling: a full repopulation is a few hundred chunks and nothing guarantees the host is
/// draining. The oldest is dropped — the newest statements are the list's current state — and
/// drops are counted, since a truncated population is a menu silently missing rows.
constexpr std::size_t kMaxPending = 2048u;

/// A bag this large means a chunk ran without consuming; bounded so a leak cannot grow.
constexpr std::size_t kMaxStaged = 64u;

/// One row a populate spawned: its `String1` name, its `String2` text tag (`CreateMenuItem`'s
/// `TextTag`, rewritten by the refresh statements) and whether `String4` put it on the root.
struct SpawnedRow {
    std::string name;
    std::string text_tag;
    bool root = false;
};

/// `GetMenuItemDataByName`'s `String2` as the shipped script writes it (`expandablemenu.lua`):
/// the named row's `TextTag`, else "No Text Tag". Kept per presentation for the read that
/// follows that statement.
std::unordered_map<uint32_t, std::string>& ByNameTextTags() {
    static std::unordered_map<uint32_t, std::string> answers;
    return answers;
}

constexpr const char kNoTextTag[] = "No Text Tag";

/// THE ROWS EACH PRESENTATION'S CURRENT POPULATE STATED, kept beside the queue so the guest's
/// own read-back is answered at the runner, in the call, without the Lua. Same boundaries the
/// host model applies (Engine_UiMenuModel): `DeleteAllSpawnedMenuItems` and
/// `RepopulateMenuFromPopulationScript` empty it, `SpawnButton` adds or restates a row by name,
/// `PushMenuState` saves it and starts empty, `PopMenuState` restores it — the shipped
/// `expandablemenu.lua` keeps `MenuRoot` exactly so (`PushMenuState` / `PopMenuState`).
struct Populate {
    std::vector<SpawnedRow> rows;
    std::vector<SpawnedRow> pushed;
    bool has_pushed = false;
    /// The 1-based place in `rows` of the row the last `SpawnButton` named; 0 none since the
    /// last boundary.
    uint32_t last = 0;
    uint32_t pushed_last = 0;
};

std::unordered_map<uint32_t, Populate>& Populates() {
    static std::unordered_map<uint32_t, Populate> populates;
    return populates;
}

const MenuChunkAttribute* StagedNamed(const std::vector<MenuChunkAttribute>& staged,
                                      const char* name) {
    for (const MenuChunkAttribute& attribute : staged) {
        if (attribute.name == name) {
            return &attribute;
        }
    }
    return nullptr;
}

/// Applies one statement to its presentation's populate. Called under the mutex.
void NotePopulate(const MenuChunkCall& call) {
    const std::string& chunk = call.chunk;
    const auto is = [&](const char* method) { return chunk.find(method) != std::string::npos; };

    // A presentation's script state is born here: `sub_823DCC70` (disc `sub_823FE988`) runs this
    // chunk first on every presentation it initialises (0x823dcca8 / 0x823fe9c0), so its menu
    // starts empty whatever the address last held.
    if (is(kPresentationInitChunk)) {
        Populates().erase(call.presentation);
        return;
    }

    if (is("g_Menu:DeleteAllSpawnedMenuItems") || is("g_Menu:RepopulateMenuFromPopulationScript")) {
        Populate& populate = Populates()[call.presentation];
        populate.rows.clear();
        populate.last = 0;
        return;
    }

    if (is("g_Menu:PushMenuState")) {
        Populate& populate = Populates()[call.presentation];
        populate.pushed.swap(populate.rows);
        populate.rows.clear();
        populate.pushed_last = populate.last;
        populate.last = 0;
        populate.has_pushed = true;
        return;
    }

    if (is("g_Menu:PopMenuState")) {
        Populate& populate = Populates()[call.presentation];
        if (populate.has_pushed) {
            populate.rows.swap(populate.pushed);
            populate.pushed.clear();
            populate.last = populate.pushed_last;
            populate.pushed_last = 0;
            populate.has_pushed = false;
        }
        return;
    }

    const MenuChunkAttribute* name = StagedNamed(call.staged, "String1");
    const MenuChunkAttribute* text_tag = StagedNamed(call.staged, "String2");
    const auto row_named = [&](const std::string& wanted) -> SpawnedRow* {
        for (SpawnedRow& row : Populates()[call.presentation].rows) {
            if (row.name == wanted) {
                return &row;
            }
        }
        return nullptr;
    };

    // The `TextTag` writers (`expandablemenuspawning.lua`): `RefreshButton`, `RefreshButtonText`
    // and `RefreshChildsButtonText` on the row `String1` names, `RefreshSelectedButtonText` only
    // when the highlighted row is that row, and `RenameSelectedButton` renames the highlighted row
    // to `String1`.
    // The selected row's id, where the runner stated one beside its name.
    const MenuChunkAttribute* highlighted_id = StagedNamed(call.staged, kHighlightedIdAttribute);
    uint32_t selected_id = 0;
    const bool has_selected_id =
        highlighted_id != nullptr && !highlighted_id->text.empty() &&
        std::from_chars(highlighted_id->text.data(),
                        highlighted_id->text.data() + highlighted_id->text.size(), selected_id)
                .ec == std::errc();

    if (is("g_Menu:RefreshButton(") || is("g_Menu:RefreshButtonText(") ||
        is("g_Menu:RefreshChildsButtonText(") || is("g_Menu:RefreshSelectedButtonText(")) {
        const MenuChunkAttribute* highlighted = StagedNamed(call.staged, kHighlightedAttribute);
        const bool selected_only = is("g_Menu:RefreshSelectedButtonText(");
        if (name != nullptr && text_tag != nullptr &&
            (!selected_only || (highlighted != nullptr && highlighted->text == name->text))) {
            if (SpawnedRow* row = row_named(name->text)) {
                row->text_tag = text_tag->text;
            }

            // The capture Godot's list is sourced from takes the same caption, on the same row.
            // Never an empty one: the Buy rows' restate after a sale reaches the stager with no
            // `String2` text, and writing that through blanked every Buy row's caption.
            const MenuChunkAttribute* parent = StagedNamed(call.staged, "String4");
            if (text_tag->text.empty()) {
                return;
            }
            if (selected_only && has_selected_id) {
                RestateCapturedMenuRow(name->text, nullptr, &selected_id, nullptr, &text_tag->text);
            } else if (is("g_Menu:RefreshChildsButtonText(") && parent != nullptr &&
                       !parent->text.empty()) {
                RestateCapturedMenuRow(name->text, &parent->text, nullptr, nullptr, &text_tag->text);
            } else if (is("g_Menu:RefreshButtonText(") || is("g_Menu:RefreshButton(")) {
                // By name, the first row so named, as the script's `GetMenuItem(String1)`: the item books'
                // page fills take the new mark off the seen row this way (`sub_829B5160` 0x829B55AC,
                // `sub_829B6310` 0x829B67C8).
                RestateCapturedMenuRow(name->text, nullptr, nullptr, nullptr, &text_tag->text);
            }
        }
        return;
    }

    if (is("g_Menu:RenameSelectedButton(")) {
        const MenuChunkAttribute* highlighted = StagedNamed(call.staged, kHighlightedAttribute);
        if (name != nullptr && highlighted != nullptr && has_selected_id) {
            RestateCapturedMenuRow(highlighted->text, nullptr, &selected_id, &name->text, nullptr);
        }
        if (name != nullptr && highlighted != nullptr) {
            if (SpawnedRow* row = row_named(highlighted->text)) {
                row->name = name->text;
            }
        }
        return;
    }

    // `GetMenuItemDataByName` — the answer its reader takes next (refii_bridge_menu_by_name_text_tag).
    if (is("g_Menu:GetMenuItemDataByName(")) {
        const SpawnedRow* row = name != nullptr ? row_named(name->text) : nullptr;
        ByNameTextTags()[call.presentation] = row != nullptr ? row->text_tag : std::string(kNoTextTag);
        return;
    }

    if (!is("g_Menu:SpawnButton")) {
        return;
    }

    if (name == nullptr || name->text.empty()) {
        return;
    }

    // `SpawnButton` maps `String4 == "Root"` to a nil parent, which `AddChild` hangs on
    // `MenuRoot`; an unstaged parent is nil too.
    const MenuChunkAttribute* parent = StagedNamed(call.staged, "String4");
    const bool root = parent == nullptr || parent->text.empty() || parent->text == "Root";

    Populate& populate = Populates()[call.presentation];
    std::vector<SpawnedRow>& rows = populate.rows;
    for (std::size_t i = 0; i < rows.size(); ++i) {
        if (rows[i].name == name->text) {
            rows[i].root = root;
            rows[i].text_tag = text_tag != nullptr ? text_tag->text : std::string();
            populate.last = static_cast<uint32_t>(i + 1u);
            return;
        }
    }
    rows.push_back(SpawnedRow{name->text, text_tag != nullptr ? text_tag->text : std::string(), root});
    populate.last = static_cast<uint32_t>(rows.size());
}

}  // namespace

extern "C" void refii_bridge_stage_menu_attribute(const char* name_data, size_t name_length,
                                                  const char* text_data, size_t text_length,
                                                  float number, int32_t is_number_flag) {
    std::string name = name_data != nullptr && name_length != 0u
                           ? std::string(name_data, name_length)
                           : std::string();
    std::string text = text_data != nullptr && text_length != 0u
                           ? std::string(text_data, text_length)
                           : std::string();
    const bool is_number = is_number_flag != 0;

    if (name.empty()) {
        return;
    }

    std::lock_guard<std::mutex> lock(Mutex());
    if (Staged().size() >= kMaxStaged) {
        return;
    }

    // Last write wins per name: the guest re-stages rather than clearing, and two `String1`s
    // in one bag would leave the host picking.
    for (MenuChunkAttribute& existing : Staged()) {
        if (existing.name == name) {
            existing.text = std::move(text);
            existing.number = number;
            existing.is_number = is_number;
            return;
        }
    }

    MenuChunkAttribute attribute;
    attribute.name = std::move(name);
    attribute.text = std::move(text);
    attribute.number = number;
    attribute.is_number = is_number;
    Staged().push_back(std::move(attribute));
}

extern "C" void refii_bridge_run_menu_chunk(const char* chunk, size_t length,
                                            uint32_t presentation) {
    if (chunk == nullptr || length == 0) {
        return;
    }

    std::lock_guard<std::mutex> lock(Mutex());

    MenuChunkCall call;
    call.chunk.assign(chunk, length);
    call.presentation = presentation;
    call.staged.swap(Staged());
    call.sequence = g_recorded.fetch_add(1, std::memory_order_relaxed) + 1u;

    if (Pending().size() >= kMaxPending) {
        Pending().erase(Pending().begin());
        g_dropped.fetch_add(1, std::memory_order_relaxed);
    }

    NotePopulate(call);
    Pending().push_back(std::move(call));
}

extern "C" uint32_t refii_bridge_menu_root_rows(uint32_t presentation) {
    std::lock_guard<std::mutex> lock(Mutex());
    const auto found = Populates().find(presentation);
    if (found == Populates().end()) {
        return 0u;
    }
    uint32_t count = 0;
    for (const SpawnedRow& row : found->second.rows) {
        count += row.root ? 1u : 0u;
    }
    return count;
}

extern "C" bool refii_bridge_menu_by_name_text_tag(uint32_t presentation, char* out, size_t capacity) {
    std::lock_guard<std::mutex> lock(Mutex());
    const auto found = ByNameTextTags().find(presentation);
    if (found == ByNameTextTags().end() || out == nullptr || capacity == 0u) {
        return false;
    }
    const std::size_t length = std::min(found->second.size(), capacity - 1u);
    std::memcpy(out, found->second.data(), length);
    out[length] = '\0';
    ByNameTextTags().erase(found);
    return true;
}

extern "C" uint32_t refii_bridge_menu_last_spawn_ordinal(uint32_t presentation) {
    std::lock_guard<std::mutex> lock(Mutex());
    const auto found = Populates().find(presentation);
    return found == Populates().end() ? 0u : found->second.last;
}

extern "C" int32_t refii_bridge_staged_menu_attribute(const char* name, char* text, uint32_t text_capacity,
                                                      float* number) {
    std::lock_guard<std::mutex> lock(Mutex());
    const MenuChunkAttribute* staged = StagedNamed(Staged(), name);
    if (staged == nullptr) {
        return 0;
    }
    if (text != nullptr && text_capacity != 0u) {
        const size_t take = std::min<size_t>(staged->text.size(), text_capacity - 1u);
        std::memcpy(text, staged->text.data(), take);
        text[take] = '\0';
    }
    if (number != nullptr) {
        *number = staged->number;
    }
    return 1;
}

std::vector<MenuChunkCall> TakeMenuChunkCalls() {
    std::vector<MenuChunkCall> taken;
    std::lock_guard<std::mutex> lock(Mutex());
    taken.swap(Pending());
    return taken;
}

void PutBackMenuChunkCalls(std::vector<MenuChunkCall>& taken, std::size_t from) {
    std::lock_guard<std::mutex> lock(Mutex());
    PutBackFront(Pending(), taken, from);
}

void MenuChunkCounters(uint64_t* out_recorded, uint32_t* out_pending, uint64_t* out_dropped) {
    if (out_recorded != nullptr) {
        *out_recorded = g_recorded.load(std::memory_order_relaxed);
    }
    if (out_dropped != nullptr) {
        *out_dropped = g_dropped.load(std::memory_order_relaxed);
    }
    if (out_pending != nullptr) {
        std::lock_guard<std::mutex> lock(Mutex());
        *out_pending = static_cast<uint32_t>(Pending().size());
    }
}

}  // namespace refii::vfs
