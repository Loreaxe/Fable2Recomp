#!/usr/bin/env python3
"""Fable 2's STORY ITEMS: the records a shipped quest script asks the hero's inventory for that are the
quest's own, not common goods.

Reads the player's own install: a decompiled copy of the game's scripts and the game's global database
(<data>/Globals/globals.gdb). Nothing of either is kept here. Writes
Fable2/src/vfs/include/core/coop_story_items.inc, the table coop_item_rule.h reads: in co-op such an item goes
to player one's hero, whoever finds it, because the quest asks hero one's bag (CoopHeroLua.cs).
The game's quest-item category (26) is read at run time from the item itself; this table adds the rest.

The rule, exactly:
  * CHECKED: `Inventory.GetNumberOfItemsOfType(<hero>, "<Name>")` in a script under quests/ or
    miscellaneous/saveload/ (less files named *debug* / *test*), <hero> one of QuestManager.HeroEntity,
    GetPlayerHero(), player_hero; `"<Prefix>" ..n` is a FAMILY (every record named Prefix + a number). The
    silver key is out (per player already). A quest thread polls its checks in its update loop, so a check
    is what the quest waits on.
  * the script's name resolves to a record the way the game resolves it (FNV-1 of the name, case kept,
    through the database's alias table); a name with no record is dropped;
  * KEPT when the record is the quest's own: its name carries a quest id (QC / QO / QD + digits), or its
    display key (InventoryItemComponent NameTag) is a quest key (TEXT_QUEST_..., TEXT_DLC2_QUEST_...), or it is
    in the quest-item category (26); OR when it is no common good: no shop stocks it (the record is the
    Item of no entry of any StockItems list in the database). DROPPED otherwise: a good the shops sell,
    which player one can always buy.

  tools/fable2_story_items.py <scripts dir> <data dir>            write the table
  tools/fable2_story_items.py <scripts dir> <data dir> --check    exit 1 when the table in the tree differs
  tools/fable2_story_items.py <scripts dir> <data dir> --why      every checked name: kept or dropped, and why
"""
import os
import re
import struct
import sys

HEROES = ("QuestManager.HeroEntity", "GetPlayerHero()", "player_hero")
FOLDERS = ("quests", os.path.join("miscellaneous", "saveload"))
SILVER_KEY = "ObjectInventorySilverKey"
QUEST_ID = re.compile(r"^Q[CDO]\d")
QUEST_KEYS = ("TEXT_QUEST_", "TEXT_DLC2_QUEST_")
TABLE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Fable2", "src", "vfs", "include", "core",
                     "coop_story_items.inc")
CHECK = re.compile(r'GetNumberOfItemsOfType\(\s*([^,()]+(?:\(\))?)\s*,\s*("([A-Za-z0-9_]+)"\s*(\.\.)?|[A-Za-z_][\w.]*)')


def fnv1(text):
    h = 0x811C9DC5
    for c in text.encode("latin-1"):
        h = ((h * 0x01000193) & 0xFFFFFFFF) ^ c
    return h


def u32(b, o):
    return struct.unpack_from(">I", b, o)[0]


class Gdb:
    """The game's database file: records (a schema of field hashes and types, a slot per field), its
    record-name aliases and its string dictionary."""

    def __init__(self, data):
        if data[:4] != b"GDB\0":
            raise ValueError("not a GDB")
        self.b = data
        self.count, size_a, size_b, aliases = u32(data, 4), u32(data, 8), u32(data, 12), u32(data, 16)
        c = self.count
        payload = size_a + size_b + 2 * (3 * c + (c & 1) + 4 * aliases)
        self.schema_base = 0x18 + size_a
        self.hash_base = self.schema_base + size_b
        offset_base = self.hash_base + c * 4
        self.offsets, cur = [], 0x18
        for _ in range(c):
            self.offsets.append(cur)
            cur += 4 + self._schema(cur)[1] * 4
        self.index = {u32(data, self.hash_base + i * 4): i for i in range(c)}
        alias_base = 0x18 + payload - 8 * aliases
        self.alias = {u32(data, alias_base + i * 8): u32(data, alias_base + i * 8 + 4) for i in range(aliases)}
        self.names = {}
        name_base = (offset_base + c * 2 + 3) & ~3
        dict_base = name_base + aliases * 8
        if dict_base + 12 <= len(data) and u32(data, dict_base) == 0x00010000:
            end, off = dict_base + 12 + u32(data, dict_base + 4), dict_base + 12
            for _ in range(u32(data, dict_base + 8)):
                if off + 5 > end:
                    break
                h, off = u32(data, off), off + 4
                t = data.index(b"\0", off)
                self.names[h] = data[off:t].decode("utf-8", "replace")
                off = t + 1

    def _schema(self, record):
        so = self.schema_base + u32(self.b, record)
        cnt = u32(self.b, so) >> 8
        if cnt > 256 and so + 4 + cnt * 8 > self.hash_base:
            cnt = (self.b[so] | (self.b[so + 1] << 8)) + self.b[so + 2]
        return so, cnt

    def fields(self, record):
        i = self.index.get(record)
        if i is None:
            return []
        rec = self.offsets[i]
        so, fc = self._schema(rec)
        out = []
        for k in range(fc):
            slot = rec + 4 + k * 4
            if slot + 4 > self.schema_base:
                break
            out.append((self.names.get(u32(self.b, so + 4 + k * 4), ""), (u32(self.b, so + 4 + fc * 4 + k * 4) >> 24) & 0xFF,
                        u32(self.b, slot)))
        return out

    def inherited(self, record, field, kind):
        seen = set()
        while record and record not in seen:
            seen.add(record)
            parent = 0
            for name, t, raw in self.fields(record):
                if name == field and t == kind:
                    return raw
                if name == "parent":
                    parent = raw
            record = parent
        return None

    def record(self, name):
        h = fnv1(name)
        return self.alias.get(h, h if h in self.index else None)

    def component(self, record, name):
        return self.inherited(record, name, 6)

    def stocked(self):
        items = set()
        for record in self.index:
            for name, t, raw in self.fields(record):
                if name == "StockItems" and t == 6:
                    for entry_name, t2, entry in self.fields(raw):
                        if t2 == 6 and entry_name != "parent":
                            item = self.inherited(entry, "Item", 7)
                            if item:
                                items.add(item)
        return items


def scan(root):
    names, families = set(), set()
    for folder in FOLDERS:
        for here, _, files in os.walk(os.path.join(root, folder)):
            for fn in sorted(files):
                low = fn.lower()
                if not low.endswith(".lua") or "debug" in low or "test" in low:
                    continue
                with open(os.path.join(here, fn), encoding="latin-1") as f:
                    for line in f:
                        for m in CHECK.finditer(line):
                            if m.group(1).strip() not in HEROES or m.group(3) is None:
                                continue
                            if m.group(4):
                                families.add(m.group(3))
                            elif m.group(3) != SILVER_KEY:
                                names.add(m.group(3))
    return names, families


def decide(gdb, stock, name):
    record = gdb.record(name)
    if record is None:
        return False, "no record of that name"
    item = gdb.component(record, "InventoryItemComponent")
    tag_hash = gdb.inherited(item, "NameTag", 4) if item else None
    tag = gdb.names.get(tag_hash, "") if tag_hash is not None else ""
    category = gdb.inherited(item, "Category", 5) if item else None
    if QUEST_ID.match(name):
        return True, "quest id in its name"
    if tag.startswith(QUEST_KEYS):
        return True, "quest display key " + tag
    if category == 26:
        return True, "quest-item category"
    if record not in stock:
        return True, "no shop stocks it"
    return False, "a shop stocks it (common good)"


def build(scripts, data):
    names, families = scan(scripts)
    with open(os.path.join(data, "Globals", "globals.gdb"), "rb") as f:
        gdb = Gdb(f.read())
    stock = gdb.stocked()
    why, kept, kept_families = [], [], []
    for name in sorted(names):
        keep, reason = decide(gdb, stock, name)
        why.append((name, keep, reason))
        if keep:
            kept.append(name)
    for prefix in sorted(families):
        members = [prefix + str(n) for n in range(100) if gdb.record(prefix + str(n)) is not None]
        verdicts = [decide(gdb, stock, m) for m in members]
        keep = bool(members) and all(k for k, _ in verdicts)
        reason = ("%d member(s): %s" % (len(members), "; ".join(sorted(set(r for _, r in verdicts))))) if members else "no member"
        why.append((prefix + "<n>", keep, reason))
        if keep:
            kept_families.append(prefix)
    return kept, kept_families, why


def render(names, families):
    out = ["// GENERATED by tools/fable2_story_items.py from the game's own quest scripts and database -- do not edit.",
           "// The records a shipped quest script asks the hero's inventory for that are the quest's own (a quest id",
           "// in the name, a quest display key, the quest-item category) or that no shop stocks; then the families",
           "// the scripts name by a prefix and an appended number. A name is the script's own: the game resolves it",
           "// to its record (NGDB_CRecord_Get_int) when the table is first read.",
           "// REFII_STORY_ITEM(name)  REFII_STORY_FAMILY(prefix)"]
    out += ['REFII_STORY_ITEM("%s")' % n for n in names]
    out += ['REFII_STORY_FAMILY("%s")' % p for p in families]
    return "\n".join(out) + "\n"


def main(argv):
    if len(argv) < 3 or not os.path.isdir(argv[1]) or not os.path.isdir(argv[2]):
        print(__doc__)
        return 2
    names, families, why = build(argv[1], argv[2])
    text = render(names, families)
    if "--why" in argv:
        for name, keep, reason in why:
            print("%s\t%s\t%s" % ("KEEP" if keep else "drop", name, reason))
        return 0
    if "--check" in argv:
        have = open(TABLE, encoding="utf-8").read() if os.path.exists(TABLE) else ""
        same = have == text
        print("table %s the scripts and the database (%d records, %d families)" %
              ("matches" if same else "DIFFERS from", len(names), len(families)))
        return 0 if same else 1
    with open(TABLE, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote %s: %d records, %d families" % (os.path.normpath(TABLE), len(names), len(families)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
