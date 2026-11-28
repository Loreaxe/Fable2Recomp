// refii — THE ONLINE JOINER'S SHOP, AS THE HOST STATES IT (Fable 2 co-op). Pure: no guest memory, no I/O.
//
// The host owns the keeper (components 26, 27) and the shop entity's stock (124); a client builds none of
// them (hooks/coop_shop_for_joiner.cpp). So the host runs the shop screen's own routines for hero two — the
// population, the buy and sell trees, their answers — with what they spawn and the boxes they open taken
// here instead of shown, and states them as this blob of player one's store. The joiner's own screen 22 shows
// them. This header is the wire: the statement, its encoding, and the two rules the joiner's screen keeps.
//
// The encoding is text: records split by RS (0x1E), fields by US (0x1F); no field holds either (Clean). The
// first record is the head `S <session> <keeper uid hex> <population> <flags>`; then per row
// `R name parent label icon id type rating emphasis flags record count category`, per price `P ticket text`,
// per box `M serial kind id text`.
#ifndef REFII_VFS_CORE_JOINER_SHOP_STATEMENT_H
#define REFII_VFS_CORE_JOINER_SHOP_STATEMENT_H

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace refii::coop {

/// One spawned row, as the capture holds it (guest_tick_pump_state.cpp MenuRow), less the host's item address.
struct ShopRow {
  std::string name;
  std::string parent;
  std::string label;
  std::string icon;
  uint32_t id = 0u;  // the host's ticket for an item row (the menu's ticket table, `sub_82880AC8`)
  int32_t event_type = 0;
  int32_t rating = 0;
  int32_t emphasis = 0;
  uint32_t item_flags = 0u;
  uint32_t record = 0u;
  uint32_t count = 0u;
  uint32_t category = 0u;
};

/// A box the host's run opened for hero two (`lhGuiManager_OpenModal`'s kind, id and text).
struct ShopModal {
  uint32_t serial = 0u;
  uint32_t kind = 0u;
  uint32_t id = 0u;
  std::string text;
};

enum ShopFlags : uint32_t {
  kShopTitles = 1u,        // a titles shop: the keeper's comp26 +156 == 31 (the OnEnter header)
  kShopRepopulate = 2u,    // a transaction set the screen's "repopulate pending" (h+441)
};

struct ShopStatement {
  uint32_t session = 0u;     // one per opening on the host
  uint64_t keeper = 0u;      // the keeper's UID
  uint32_t population = 0u;  // one per population the host ran
  uint32_t flags = 0u;
  std::vector<ShopRow> rows;
  std::vector<std::pair<uint32_t, std::string>> prices;  // ticket -> the asking price text
  std::vector<ShopModal> modals;                          // newest last
};

constexpr char kRecord = '\x1E';
constexpr char kField = '\x1F';

/// `text` with the two separators dropped.
inline std::string Clean(std::string_view text) {
  std::string out;
  out.reserve(text.size());
  for (const char c : text) {
    if (c != kRecord && c != kField) {
      out.push_back(c);
    }
  }
  return out;
}

namespace shop_detail {

inline void Put(std::string& out, std::string_view text) {
  out.push_back(kField);
  out += Clean(text);
}

inline void Put(std::string& out, uint64_t value) { Put(out, std::to_string(value)); }
inline void PutSigned(std::string& out, int64_t value) { Put(out, std::to_string(value)); }

inline std::vector<std::string_view> Split(std::string_view text, char by) {
  std::vector<std::string_view> parts;
  size_t at = 0u;
  while (at <= text.size()) {
    const size_t next = text.find(by, at);
    if (next == std::string_view::npos) {
      parts.push_back(text.substr(at));
      break;
    }
    parts.push_back(text.substr(at, next - at));
    at = next + 1u;
  }
  return parts;
}

inline bool Number(std::string_view text, uint64_t* out, int base = 10) {
  if (text.empty() || text.size() > 20u) {
    return false;
  }
  const std::string copy(text);
  char* end = nullptr;
  const unsigned long long value = std::strtoull(copy.c_str(), &end, base);
  if (end == nullptr || *end != '\0') {
    return false;
  }
  *out = value;
  return true;
}

inline bool Signed(std::string_view text, int32_t* out) {
  if (text.empty() || text.size() > 12u) {
    return false;
  }
  const std::string copy(text);
  char* end = nullptr;
  const long value = std::strtol(copy.c_str(), &end, 10);
  if (end == nullptr || *end != '\0') {
    return false;
  }
  *out = static_cast<int32_t>(value);
  return true;
}

}  // namespace shop_detail

inline std::string Encode(const ShopStatement& s) {
  using namespace shop_detail;
  std::string out = "S";
  Put(out, s.session);
  char keeper[24];
  std::snprintf(keeper, sizeof(keeper), "%016llX", static_cast<unsigned long long>(s.keeper));
  Put(out, keeper);
  Put(out, s.population);
  Put(out, s.flags);
  for (const ShopRow& row : s.rows) {
    out.push_back(kRecord);
    out.push_back('R');
    Put(out, row.name);
    Put(out, row.parent);
    Put(out, row.label);
    Put(out, row.icon);
    Put(out, row.id);
    PutSigned(out, row.event_type);
    PutSigned(out, row.rating);
    PutSigned(out, row.emphasis);
    Put(out, row.item_flags);
    Put(out, row.record);
    Put(out, row.count);
    Put(out, row.category);
  }
  for (const auto& [ticket, text] : s.prices) {
    out.push_back(kRecord);
    out.push_back('P');
    Put(out, ticket);
    Put(out, text);
  }
  for (const ShopModal& modal : s.modals) {
    out.push_back(kRecord);
    out.push_back('M');
    Put(out, modal.serial);
    Put(out, modal.kind);
    Put(out, modal.id);
    Put(out, modal.text);
  }
  return out;
}

/// The statement in `text`, or nothing when its head does not parse. A record that does not parse is
/// skipped.
inline std::optional<ShopStatement> Decode(std::string_view text) {
  using namespace shop_detail;
  const std::vector<std::string_view> records = Split(text, kRecord);
  if (records.empty()) {
    return std::nullopt;
  }
  const std::vector<std::string_view> head = Split(records[0], kField);
  ShopStatement s;
  uint64_t session = 0u;
  uint64_t population = 0u;
  uint64_t flags = 0u;
  if (head.size() != 5u || head[0] != "S" || !Number(head[1], &session) || !Number(head[2], &s.keeper, 16) ||
      !Number(head[3], &population) || !Number(head[4], &flags)) {
    return std::nullopt;
  }
  s.session = static_cast<uint32_t>(session);
  s.population = static_cast<uint32_t>(population);
  s.flags = static_cast<uint32_t>(flags);
  for (size_t i = 1u; i < records.size(); ++i) {
    const std::vector<std::string_view> f = Split(records[i], kField);
    if (f.empty()) {
      continue;
    }
    if (f[0] == "R" && f.size() == 13u) {
      ShopRow row;
      uint64_t id = 0u, item_flags = 0u, record = 0u, count = 0u, category = 0u;
      if (!Number(f[5], &id) || !Signed(f[6], &row.event_type) || !Signed(f[7], &row.rating) ||
          !Signed(f[8], &row.emphasis) || !Number(f[9], &item_flags) || !Number(f[10], &record) ||
          !Number(f[11], &count) || !Number(f[12], &category)) {
        continue;
      }
      row.name = std::string(f[1]);
      row.parent = std::string(f[2]);
      row.label = std::string(f[3]);
      row.icon = std::string(f[4]);
      row.id = static_cast<uint32_t>(id);
      row.item_flags = static_cast<uint32_t>(item_flags);
      row.record = static_cast<uint32_t>(record);
      row.count = static_cast<uint32_t>(count);
      row.category = static_cast<uint32_t>(category);
      s.rows.push_back(std::move(row));
    } else if (f[0] == "P" && f.size() == 3u) {
      uint64_t ticket = 0u;
      if (Number(f[1], &ticket)) {
        s.prices.emplace_back(static_cast<uint32_t>(ticket), std::string(f[2]));
      }
    } else if (f[0] == "M" && f.size() == 5u) {
      ShopModal modal;
      uint64_t serial = 0u, kind = 0u, id = 0u;
      if (Number(f[1], &serial) && Number(f[2], &kind) && Number(f[3], &id)) {
        modal.serial = static_cast<uint32_t>(serial);
        modal.kind = static_cast<uint32_t>(kind);
        modal.id = static_cast<uint32_t>(id);
        modal.text = std::string(f[4]);
        s.modals.push_back(std::move(modal));
      }
    }
  }
  return s;
}

/// A question's answer as one intent value: the box id over the answer code.
constexpr int32_t AnswerValue(uint32_t box, uint32_t code) {
  return static_cast<int32_t>(((box & 0x7FFFu) << 16) | (code & 0xFFFFu));
}
constexpr uint32_t AnswerBox(int32_t value) { return (static_cast<uint32_t>(value) >> 16) & 0x7FFFu; }
constexpr uint32_t AnswerCode(int32_t value) { return static_cast<uint32_t>(value) & 0xFFFFu; }

/// What closes the joiner's own question once its answer has gone to the host, as the shop's answer helpers
/// close theirs on the machine that showed it: 1 `sub_8236FEC8(QM, 3)`, 2 `sub_8236FE38(QM, 3)`, 0 none.
///   box 17 (tattoo removal, `sub_826AF430`) and 19 (the worn item's sale, `sub_826AFB98`): code 0x2E yes →
///   the work, then `li r4,3` / `bl sub_8236FEC8` (0x826AFCA4 / 0x826AFCAC; 0x826AF840 / 0x826AF848); code
///   0x2F no → `li r4,3` / `bl sub_8236FE38` (0x826AFBBC / 0x826AFBC8; 0x826AF454 / 0x826AF460); any other
///   code returns (`bne loc_826AFCE4` 0x826AFBB4).
///   box 20 (buy and wear, `sub_826AF888`): code 36 (buy) or 75 (buy and wear) → the buy, then `li r4,3` /
///   `bl sub_8236FEC8` (0x826AFB24 / 0x826AFB30); any other code does nothing.
constexpr int QuestionCloser(uint32_t box, uint32_t code) {
  if (box == 17u || box == 19u) {
    return code == 0x2Eu ? 1 : code == 0x2Fu ? 2 : 0;
  }
  if (box == 20u) {
    return code == 36u || code == 75u ? 1 : 0;
  }
  return 0;
}

/// The joiner installs a newer population where the shop's own Update repopulates (`sub_826AE270`): the
/// highlight on a header (type 301) and no box up (`QM+40 == 1`, 0x826AE520) — so the list never changes
/// under a box or the cursor.
constexpr bool InstallsPopulation(bool newer, uint32_t highlighted_type, uint32_t box_mode) {
  return newer && highlighted_type == 301u && box_mode == 1u;
}
/// And the list that follows this joiner's own buy, sale or answer: the host makes it at once after the guest's
/// tree states one pending (h+441), and the joiner takes it as soon as no box is up — the box the transaction opened
/// answered, as the shipped screen refreshes the transacted row then (`sub_826B0998`).
constexpr bool InstallsAfterOwn(bool awaiting, uint32_t box_mode) { return awaiting && box_mode == 1u; }

}  // namespace refii::coop

#endif  // REFII_VFS_CORE_JOINER_SHOP_STATEMENT_H
