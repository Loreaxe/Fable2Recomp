// The hero's derived state, sampled on the guest's own thread; shared by the pump, the state
// file and the host bridge.

#ifndef REFII_VFS_CORE_HERO_DERIVED_H_
#define REFII_VFS_CORE_HERO_DERIVED_H_

#include <cstdint>

#include "core/payload_bridge_fable2.h"

namespace refii::vfs {


/// The world map's minimap keys for one region (SampleWorldMapRegion): what the minimap fill
/// `sub_829ABFC0` keys its `MinimapInfo` record and art on. The region info page is its own fill's
/// statement (page_fills_stated.cpp).
struct WorldMapRegion {
    bool known = false;

    /// Which region this sample is of — the guest's region walk `sub_82518330`'s index, the row's id
    /// less 999; the pump answers a tick late.
    uint32_t index = 0xFFFFFFFFu;

    /// The region record's name, the narrow key `sub_82519DF8` matches regions by.
    char name[64] = {};

    /// How many regions the walk holds, so a host can bound its cursor.
    uint32_t count = 0;

    /// The region's FIRST LEVEL (`**(region+16)`), what the world map's minimap page keys the
    /// `MinimapInfo` record on: `regionmap_<level>_<scenario>` (`sub_829ABFC0`).
    char level_name[64] = {};

    /// `World_ComputeWorldLevelFnv1Key(World, "Albion", &level)` — the key the page compares to
    /// the current level's `L+180` before it plots the hero.
    uint32_t level_key = 0;

    /// `World_LookupLevelNameByKey(World, level_key)`, empty when the world names no scenario
    /// for it (the page then uses "DefaultScenario").
    char scenario[64] = {};
};

/// One completed quest for the logbook page. Row key is `index + 1000` into the list
/// `sub_82470220` rebuilds: sorted descending by GDB `DisplayPriority`, then filtered
/// order-preserving.
struct QuestEntry {
    /// GDB record hash from `quest+4`; name, description and DisplayPriority hang off it.
    uint32_t record = 0;

    /// `quest+36` and `quest+32`, signed ints, gated on `> 0` by the page.
    int32_t money = 0;
    int32_t renown = 0;

    /// Babel tag of the conclusion, LHString at `quest+0x50`; empty when none.
    char conclusion[64] = {};
};

/// The hero's derived state: accessor returns, not fields. Morality is `sub_8223D320`, renown
/// `sub_821C7D30`. Personality bands test at 0.375 and 0.625. `known` is false until sampled.
struct HeroDerived {
    bool known = false;

    /// Displayed morality and purity, and the range each is normalised against.
    float morality = 0.0f;
    float morality_min = 0.0f;
    float morality_max = 0.0f;
    float purity = 0.0f;
    float purity_min = 0.0f;
    float purity_max = 0.0f;

    /// Displayed renown: the field plus modifier 10, clamped at zero.
    int32_t renown = 0;

    /// The hero's title as the Babel tag the guest resolves; empty when unresolved.
    char title_tag[64] = {};

    /// The eleven attractiveness contributions in page order, then the slider's own position.
    ///
    /// `attractiveness_fraction` is 0..1 as `sub_829BA860` states it to the meter, not the sum
    /// of the eleven rows: the guest's sum also carries preference axes 0 and 2, which the page
    /// never displays, and the "BalancingAugmentations" GDB bonus. It clamps to [-100, 100] and
    /// scales by `(total + 100) * 0.0049999999` before the store, so the curve is already applied.
    static constexpr uint32_t kAttractivenessCount = 11;
    float attractiveness[kAttractivenessCount] = {};
    float attractiveness_fraction = 0.0f;

    /// The dog, when one exists: `dog_known` gates the whole page. The local player's: hero
    /// one's, or on a joiner player two's own.
    bool dog_known = false;
    uint32_t dog_entity = 0;

    /// Levels 1..5, int32 not float: the constructor stores `1` with `stw` (`0x82808130`) and
    /// `sub_829D75F0` uses combat as a one-based index into a five-float table.
    int32_t dog_combat = 0;
    int32_t dog_searching = 0;
    int32_t dog_state = 0;
    char dog_name[64] = {};

    /// The Babel key the guest resolves when no custom name is set: `c20+48`, 0xD1E14CE2 ('Dog')
    /// on an unnamed dog. `dog_name` carries the custom LHString at `c20+52` when it has a length.
    uint32_t dog_name_key = 0;

    /// The dog's definition (entity+0x1C): its breed, `CreatureDogHero` or one of the three
    /// breed defs `OnCarriedActionUseBase:ApplyDogChange` swaps in.
    uint32_t dog_definition = 0;

    /// GDB record ids of learned tricks, not name hashes: `sub_8219C5E8` maps `FNV1(name)`
    /// through a hash -> record-id table before storing.
    uint32_t dog_trick_ids[32] = {};
    uint32_t dog_trick_count = 0;

    /// Logbook stat counters: flat int32 array at `c57 + 348 + 4*id`; ids up to 123.
    static constexpr uint32_t kStatCount = 124;
    int32_t stats[kStatCount] = {};
    bool stats_known = false;

    /// Hero-side spell damage multiplier indexed by level 1..5; slot 0 unused.
    float spell_damage[6] = {};
};

/// Published by the pump on the guest thread; read by the host bridge on Godot's.
inline void PublishHeroDerived(const HeroDerived& sampled) {
  refii_bridge_publish_hero_derived(&sampled);
}

inline HeroDerived HeroDerivedSnapshot() {
  HeroDerived out;
  refii_bridge_hero_derived_snapshot(&out);
  return out;
}

/// The completed-quest list, same contract; the store is fixed-size.
constexpr uint32_t kMaxCompletedQuests = 256;

inline void PublishQuests(const QuestEntry* entries, uint32_t count) {
  refii_bridge_publish_quests(entries, count);
}

uint32_t QuestSnapshot(QuestEntry* out, uint32_t capacity);

/// ONE BUILDING'S REAL-ESTATE NUMBERS, for the world map's property row (SamplePropertyEstate):
/// the remaster's map properties have no guest page, so the guest's providers answer for the
/// building the host names. Screen 28 itself is stated by its page fills (page_fills_stated.cpp).
///
/// WHAT CROSSES IS DATA, NOT PRESENTATION. Every number here is the guest's own answer —
/// `sub_8282AEB0` (value), `sub_8282C1C0` (economy), `sub_8282C110` (income), `sub_8282BCA0`
/// (sale floor), `sub_8282BA48` (purchase) and the three not-owned deltas — because each of
/// them averages or walks something the host has no business reproducing: prosperity off a
/// world component, a GDB adjustment array matched against the building-type mask, an
/// occupant-opinion term weighted over five personality axes. The FORMATTING is Godot's: the
/// star bin, the `<gold_img>` prefix, the negative recolour, which of the four Variable rows
/// stands, and the HOUSE-versus-SHOP legend are all decided from these fields.
struct RealEstate {
    bool known = false;

    /// The GDB record the page's text hangs off — `BuildingName`, `BuildingAddress`,
    /// `BuildingAnecdotes`, `BasicSalePrice`, `CanSellOncePurchased`. It is NOT the building
    /// entity's own record: `sub_821DA3B0` takes the entity's def handle at `BE+28`, then a
    /// property named after the component's class, and the entry-ref THAT yields is this hash.
    uint32_t record = 0;

    /// `*(u32*)(CECBuilding+28)` — the BuildingType bitmask. Bit 0 HOME, then DOCK, FARM,
    /// TOWNHALL, WAREHOUSE, SHOP, BAR, BORDELLO, UNIQUE, STALL, HOUSE, TEMPLE.
    /// `mask & 0xEFE` is "is a business", the guest's own test at 0x82999CF8.
    uint32_t type_mask = 0;

    bool owned = false;
    bool has_tenant = false;

    int32_t value = 0;
    int32_t economy = 0;
    int32_t decoration = 0;
    int32_t income = 0;
    int32_t sale_price = 0;

    /// Owned only: `sub_8282BCA0 - sub_8282AEB0`, the negative selling fee.
    int32_t selling_fees = 0;

    /// Not owned only: the three price deltas the page lists as Variable1..3.
    int32_t not_vacant = 0;
    int32_t owner_killed = 0;
    int32_t hero_opinion = 0;

    /// `sub_823C8510` on the TENANT when there is one, else on the OWNER when the hero is not
    /// the owner. Empty when the entity is gone (the page's "OTHER" case) and empty when the
    /// hero owns it — the guest never names the hero this way; see `title_record`.
    char occupant[128] = {};

    /// A HERO-OWNED PROPERTY IS NAMED AFTER THE HERO'S TITLE, not through the creature-name
    /// path. `sub_82998FB0` branches on ownership first (0x8299959c) and, when the hero owns
    /// the building, calls `sub_824D8A10(&text, heroComponent57)`: the lazy GDB ref at
    /// `comp57+92` (`{hash, cache}`, resolved by `sub_821B24F8`), its `HeroTitleComponent`
    /// reference, that entry's `TextTag`, through Babel. This is that hash; 0 when the hero
    /// holds no title, which the guest draws as an empty name. `sub_823C8510` cannot name the
    /// hero: it carries no component 20.
    uint32_t title_record = 0;

    /// The GDB record carrying the `Bonuses` array. NOT `record`: `sub_82917F60` runs the same
    /// `sub_821DA3B0` indirection over component **184**, so the array hangs off that
    /// component's own sub-entry. The host reads the array and each element's
    /// `ShortNameTextTag` from its own GDB; no string crosses.
    uint32_t bonuses_record = 0;
};

/// THE CO-OP SCREEN'S STATE (screen 24, ctor `sub_82434DD8`) — what its writers decide from,
/// read off the screen object and the session as plain data. The host composes every caption,
/// slide and opacity from these by the writers' own rules (`sub_82439988` buttons,
/// `sub_8281F010` profile publish, the pickbox widget `sub_82881F58`, `sub_82437810` connecting).
struct CoopScreen {
    bool known = false;
    int32_t session = 0;            // *(game+284): 2 local co-op, 1 online
    bool online_client = false;     // *(game+0x8C) +0x34 && +0x36
    bool right_signed_in = false;   // XamUserGetSigninState(scr+300) != 0, scr+300 < 4
    bool peer_ready = false;        // scr+296
    bool flag297 = false;           // scr+297
    bool flag298 = false;           // scr+298
    bool child = false;             // scr+270
    bool child_level = false;       // scr+271
    int32_t user_right_save = -1;   // scr+292
    bool save_chosen = false;       // *(*(game+0x94)+4) non-empty: player two's chosen hero save
    int32_t pick_selected[3] = {};  // pickbox widget +36
    bool pick_enabled[3] = {};      // pickbox widget +25
    int32_t pick_state[3] = {};     // pickbox widget +28: frame slide 1 normal, 2 focused, 3 off
    int32_t profile_left = 0;       // scr+264 widget +56 (4 = published)
    int32_t profile_right = 0;      // scr+260 widget +56
};

inline void PublishCoopScreen(const CoopScreen& coop) {
  refii_bridge_publish_coop_screen(&coop);
}

CoopScreen CoopScreenSnapshot();

/// THE WORLD MAP'S PROPERTY ROW. The host names the UID under its cursor; the pump answers with
/// screen 28's numbers for that building while its region is loaded, unknown otherwise.
void RequestPropertyEstate(uint64_t uid);

inline uint64_t RequestedPropertyEstate() { return refii_bridge_requested_property_estate(); }

inline void PublishPropertyEstate(uint64_t uid, const RealEstate& estate) {
  refii_bridge_publish_property_estate(uid, &estate);
}

/// The last answer and the UID it is for.
RealEstate PropertyEstateSnapshot(uint64_t* uid);

/// Which region the host wants sampled; the pump answers for that one only.
void RequestWorldMapRegion(uint32_t index);

inline uint32_t RequestedWorldMapRegion() {
  return refii_bridge_requested_world_map_region();
}

inline void PublishWorldMapRegion(const WorldMapRegion& region) {
  refii_bridge_publish_world_map_region(&region);
}

WorldMapRegion WorldMapRegionSnapshot();

}  // namespace refii::vfs

#endif  // REFII_VFS_CORE_HERO_DERIVED_H_
