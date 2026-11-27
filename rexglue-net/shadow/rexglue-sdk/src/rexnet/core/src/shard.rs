// @file        rexnet/core/src/shard.rs
// @brief       Ambient title shards.
//
// @copyright   Copyright (c) 2026 Ryan Fisher <ryanfisher099@gmail.com>
//              All rights reserved.
//
// @license     BSD 3-Clause License
//              See LICENSE file in the project root for full license text.

//! Ambient title shards — design spec §17.3.
//!
//! A shard is the "Connected" tier: up to 255 peers playing the same title,
//! joined automatically, carrying passive presence (orbs, lobby population).
//! It is **not** the game's own session — see [`crate::protocol::shard_key`]
//! for why the two must never share a DHT namespace.
//!
//! Placement and convergence live here as pure functions over a snapshot of
//! candidates, so the interesting behaviour (friend affinity, tie-breaking,
//! split-brain merge) is testable without a swarm.

use serde::{Deserialize, Serialize};

/// Spec default; a title may lower it via config (§17.5).
pub const DEFAULT_SHARD_CAP: u16 = 255;

/// Descriptor published under `rexnet/v1/title/<id>/shards` (§17.3.1) and
/// returned by the `/rexnet/shard/1.0.0` query.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ShardDesc {
    pub schema: u8, // = 1
    pub shard_id: [u8; 16],
    pub title_id: u32,
    /// Creator's wall clock, unix seconds. Advisory: used to pick the oldest
    /// shard, with `shard_id` as the tie-break so a wrong or hostile clock
    /// cannot stop peers agreeing.
    pub created_at: u64,
    pub cap: u16,
    /// Last known population. Advisory only — the authoritative count is the
    /// number of live gossipsub topic peers (§17.3.4).
    pub members_hint: u16,
    /// Creator. May have left; never treated as an authority.
    pub anchor: Vec<u8>,
}

impl ShardDesc {
    pub fn has_capacity(&self) -> bool {
        self.members_hint < self.cap
    }
}

/// A candidate shard plus what the local node knows about it.
#[derive(Debug, Clone)]
pub struct Candidate {
    pub desc: ShardDesc,
    /// Friends observed in this shard. Drives the primary placement rule.
    pub friends_present: u16,
}

/// Total order over candidates: most friends, then oldest, then lowest id.
///
/// Every peer must derive the same answer from the same inputs, so the
/// comparison is total and never falls through to iteration order — hence the
/// `shard_id` tiebreak (§17.3.2).
fn better_than(a: &Candidate, b: &Candidate) -> bool {
    if a.friends_present != b.friends_present {
        return a.friends_present > b.friends_present;
    }
    if a.desc.created_at != b.desc.created_at {
        return a.desc.created_at < b.desc.created_at;
    }
    a.desc.shard_id < b.desc.shard_id
}

/// What placement decided (§17.3.2).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Placement {
    /// Join this existing shard.
    Join([u8; 16]),
    /// Nothing suitable — create one.
    Create,
}

/// Pick a shard for `title_id`: most friends wins, else oldest, ties by
/// lowest id; create if nothing has room.
pub fn choose(title_id: u32, candidates: &[Candidate]) -> Placement {
    let mut best: Option<&Candidate> = None;
    for candidate in candidates {
        // Wrong-title records share the DHT with us only by accident; a peer
        // that publishes one must not be able to pull us out of our title.
        if candidate.desc.title_id != title_id || !candidate.desc.has_capacity() {
            continue;
        }
        match best {
            Some(current) if !better_than(candidate, current) => {}
            _ => best = Some(candidate),
        }
    }
    match best {
        Some(candidate) => Placement::Join(candidate.desc.shard_id),
        None => Placement::Create,
    }
}

/// Why a migration was or was not taken. Returned rather than a bare bool so
/// the caller can log the reason — shard churn is otherwise hard to explain.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Migration {
    Stay,
    MoveTo([u8; 16]),
}

/// Convergence (§17.3.3). Concurrent creation is the *normal* case: two peers
/// that both find nothing will both create a shard, and without this the
/// population fragments permanently.
///
/// Everyone walks toward the **canonical** shard: the one sorting first by
/// `(created_at, shard_id)` among those with room. That order is total and
/// fixed, so it has a unique minimum and migration always terminates there.
///
/// An earlier draft also required "our shard is the smaller of the two",
/// meaning to damp churn. It does not converge: when the older shard is the
/// *smaller* one, direction (toward older) and the size guard (toward bigger)
/// disagree, neither side yields, and the two populations never merge. Size
/// must not influence direction -- a stampede is a question of *rate*, which
/// belongs in the caller's rate limiting, not of *which way* to walk.
///
/// A GAME IN PROGRESS DOES NOT STOP THE WALK. This used to answer Stay for a
/// node in a session, and a host whose start-up search came back empty created
/// a young shard, opened its game and stayed there for as long as it hosted
/// while its joiner walked to the old one: the pair never shared a shard, and
/// a join that needs the host's shard beat found none (2026-11-27, twice). The
/// engine now moves a node mid-game without re-addressing anyone (its own
/// address and every peer's stay pinned until the game ends), so the direction
/// of travel is the same for everybody.
///
/// FRIENDS ARE WEIGHED AMONG THE OLDER SHARDS, never against age. Each step is
/// still strictly toward an older shard, so the walk terminates at the
/// canonical one; a friend only decides which older shard is the next step, so
/// friends travel together instead of one hop apart. Weighing friends against
/// age instead cannot converge: two friends in two shards each see the other's
/// shard as the friendlier one and swap forever, and a pair in a young shard
/// holds each other there for good.
pub fn should_migrate(title_id: u32, current: &ShardDesc, candidates: &[Candidate]) -> Migration {
    let mut best: Option<&Candidate> = None;
    for candidate in candidates {
        let other = &candidate.desc;
        if other.title_id != title_id || other.shard_id == current.shard_id {
            continue;
        }
        if !other.has_capacity() {
            continue;
        }
        // Strictly older, ties by lower id -- the same total order placement
        // uses, so both sides of a split agree on who moves.
        let older = (other.created_at, other.shard_id) < (current.created_at, current.shard_id);
        if !older {
            continue;
        }
        // Among the older ones: most friends, then oldest, then lowest id --
        // placement's own order (`better_than`), applied only where age
        // already says move.
        match best {
            Some(current_best) if !better_than(candidate, current_best) => {}
            _ => best = Some(candidate),
        }
    }
    match best {
        Some(candidate) => Migration::MoveTo(candidate.desc.shard_id),
        None => Migration::Stay,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn desc(id: u8, created_at: u64, members: u16) -> ShardDesc {
        ShardDesc {
            schema: 1,
            shard_id: [id; 16],
            title_id: 0x4D53_0910,
            created_at,
            cap: DEFAULT_SHARD_CAP,
            members_hint: members,
            anchor: vec![],
        }
    }

    fn cand(id: u8, created_at: u64, members: u16, friends: u16) -> Candidate {
        Candidate { desc: desc(id, created_at, members), friends_present: friends }
    }

    const TITLE: u32 = 0x4D53_0910;

    #[test]
    fn creates_when_nothing_exists() {
        assert_eq!(choose(TITLE, &[]), Placement::Create);
    }

    #[test]
    fn joins_the_oldest_when_no_friends() {
        let candidates = [cand(2, 500, 10, 0), cand(1, 100, 10, 0), cand(3, 900, 10, 0)];
        assert_eq!(choose(TITLE, &candidates), Placement::Join([1; 16]));
    }

    #[test]
    fn friends_beat_age() {
        // Younger shard, but a friend is in it.
        let candidates = [cand(1, 100, 10, 0), cand(2, 900, 10, 1)];
        assert_eq!(choose(TITLE, &candidates), Placement::Join([2; 16]));
    }

    #[test]
    fn more_friends_wins() {
        let candidates = [cand(1, 100, 10, 1), cand(2, 900, 10, 3)];
        assert_eq!(choose(TITLE, &candidates), Placement::Join([2; 16]));
    }

    #[test]
    fn full_shards_are_skipped_even_with_friends() {
        let full = cand(2, 900, DEFAULT_SHARD_CAP, 5);
        let open = cand(1, 100, 10, 0);
        assert_eq!(choose(TITLE, &[full, open]), Placement::Join([1; 16]));
    }

    #[test]
    fn creates_when_everything_is_full() {
        let candidates = [cand(1, 100, DEFAULT_SHARD_CAP, 0), cand(2, 200, DEFAULT_SHARD_CAP, 9)];
        assert_eq!(choose(TITLE, &candidates), Placement::Create);
    }

    #[test]
    fn other_titles_are_ignored() {
        let mut foreign = cand(1, 1, 0, 9);
        foreign.desc.title_id = 0xDEAD_BEEF;
        assert_eq!(choose(TITLE, &[foreign]), Placement::Create);
    }

    #[test]
    fn ties_break_deterministically_regardless_of_order() {
        // Same age and friend count: the lower id must win from either
        // direction, or two peers reading the DHT in different orders would
        // land in different shards.
        let a = cand(1, 100, 10, 0);
        let b = cand(2, 100, 10, 0);
        assert_eq!(choose(TITLE, &[a.clone(), b.clone()]), Placement::Join([1; 16]));
        assert_eq!(choose(TITLE, &[b, a]), Placement::Join([1; 16]));
    }

    #[test]
    fn a_session_no_longer_holds_a_node_in_a_young_shard() {
        // Was Stay: the hosting node never left the shard it created. The
        // engine now moves it without re-addressing, so it walks like anyone.
        let current = desc(9, 900, 2);
        let older = [cand(1, 100, 50, 0)];
        assert_eq!(should_migrate(TITLE, &current, &older), Migration::MoveTo([1; 16]));
    }

    #[test]
    fn smaller_shard_migrates_into_the_older_one() {
        let current = desc(9, 900, 2);
        let older = [cand(1, 100, 50, 0)];
        assert_eq!(
            should_migrate(TITLE, &current, &older),
            Migration::MoveTo([1; 16])
        );
    }

    #[test]
    fn population_does_not_change_the_direction_of_travel() {
        // Even a large shard walks toward the older one. Tempting to hold
        // ground here, but making size a condition is exactly what breaks
        // convergence (see older_but_smaller_still_converges). Churn is the
        // caller's problem, via rate limiting.
        let current = desc(9, 900, 50);
        let older = [cand(1, 100, 2, 0)];
        assert_eq!(
            should_migrate(TITLE, &current, &older),
            Migration::MoveTo([1; 16])
        );
    }

    #[test]
    fn older_but_smaller_still_converges() {
        // Regression: with a size guard neither side moved and the split was
        // permanent. The younger shard must yield regardless of being bigger.
        let older_smaller = desc(1, 100, 1);
        let younger_bigger = desc(2, 900, 5);
        let a_sees = [Candidate { desc: younger_bigger.clone(), friends_present: 0 }];
        let b_sees = [Candidate { desc: older_smaller.clone(), friends_present: 0 }];

        assert_eq!(should_migrate(TITLE, &older_smaller, &a_sees), Migration::Stay);
        assert_eq!(
            should_migrate(TITLE, &younger_bigger, &b_sees),
            Migration::MoveTo([1; 16])
        );
    }

    #[test]
    fn migration_reaches_a_fixed_point() {
        // Walking the rule repeatedly must terminate, not cycle.
        let shards = [desc(3, 300, 4), desc(1, 100, 9), desc(2, 200, 1)];
        for start in &shards {
            let mut at = start.clone();
            let mut hops = 0;
            loop {
                let others: Vec<Candidate> = shards
                    .iter()
                    .filter(|d| d.shard_id != at.shard_id)
                    .map(|d| Candidate { desc: d.clone(), friends_present: 0 })
                    .collect();
                match should_migrate(TITLE, &at, &others) {
                    Migration::Stay => break,
                    Migration::MoveTo(id) => {
                        at = shards.iter().find(|d| d.shard_id == id).unwrap().clone();
                        hops += 1;
                        assert!(hops < 8, "migration cycled instead of settling");
                    }
                }
            }
            // Everyone lands on the oldest.
            assert_eq!(at.shard_id, [1; 16]);
        }
    }

    #[test]
    fn split_brain_converges_one_way_only() {
        // The real scenario: two peers each created a shard at nearly the
        // same time. Exactly one side must move, or they swap forever.
        let a = desc(1, 100, 1);
        let b = desc(2, 100, 1);
        let a_sees = [Candidate { desc: b.clone(), friends_present: 0 }];
        let b_sees = [Candidate { desc: a.clone(), friends_present: 0 }];

        let a_move = should_migrate(TITLE, &a, &a_sees);
        let b_move = should_migrate(TITLE, &b, &b_sees);

        // Equal age and size, so the id tiebreak decides: b joins a.
        assert_eq!(a_move, Migration::Stay);
        assert_eq!(b_move, Migration::MoveTo([1; 16]));
    }

    #[test]
    fn never_migrates_into_a_full_shard() {
        let current = desc(9, 900, 1);
        let older_full = [cand(1, 100, DEFAULT_SHARD_CAP, 0)];
        assert_eq!(
            should_migrate(TITLE, &current, &older_full),
            Migration::Stay
        );
    }

    #[test]
    fn never_migrates_to_a_younger_shard() {
        let current = desc(1, 100, 1);
        let younger = [cand(9, 900, 1, 0)];
        assert_eq!(
            should_migrate(TITLE, &current, &younger),
            Migration::Stay
        );
    }

    #[test]
    fn migration_picks_the_oldest_of_several() {
        let current = desc(9, 900, 1);
        let others = [cand(3, 300, 5, 0), cand(1, 100, 5, 0), cand(2, 200, 5, 0)];
        assert_eq!(
            should_migrate(TITLE, &current, &others),
            Migration::MoveTo([1; 16])
        );
    }

    #[test]
    fn friends_pick_the_next_older_step() {
        // Two older shards; the friend's is the step taken, though not the oldest.
        let current = desc(9, 900, 1);
        let others = [cand(1, 100, 5, 0), cand(2, 200, 5, 1)];
        assert_eq!(should_migrate(TITLE, &current, &others), Migration::MoveTo([2; 16]));
    }

    #[test]
    fn friends_never_pull_toward_a_younger_shard() {
        // Friend-first against age is the swap that never ends; age decides direction.
        let current = desc(1, 100, 1);
        let younger_with_friends = [cand(9, 900, 1, 3)];
        assert_eq!(should_migrate(TITLE, &current, &younger_with_friends), Migration::Stay);
    }

    #[test]
    fn two_friends_in_two_shards_meet_and_stay_met() {
        // A in the older shard, B in the younger; each sees the other as a friend.
        let older = desc(1, 100, 1);
        let younger = desc(2, 200, 1);
        let a_sees = [Candidate { desc: younger.clone(), friends_present: 1 }];
        let b_sees = [Candidate { desc: older.clone(), friends_present: 1 }];
        assert_eq!(should_migrate(TITLE, &older, &a_sees), Migration::Stay);
        assert_eq!(should_migrate(TITLE, &younger, &b_sees), Migration::MoveTo([1; 16]));
        // Together in the older one, nobody moves again.
        let both_there = [Candidate { desc: younger, friends_present: 0 }];
        assert_eq!(should_migrate(TITLE, &older, &both_there), Migration::Stay);
    }

    #[test]
    fn friend_weighting_still_reaches_the_oldest() {
        // A friend in the middle shard changes the path, never the end.
        let shards = [desc(3, 300, 4), desc(1, 100, 9), desc(2, 200, 1)];
        let friends_in = |d: &ShardDesc| -> u16 { if d.shard_id == [2; 16] { 1 } else { 0 } };
        for start in &shards {
            let mut at = start.clone();
            let mut hops = 0;
            loop {
                let others: Vec<Candidate> = shards
                    .iter()
                    .filter(|d| d.shard_id != at.shard_id)
                    .map(|d| Candidate { desc: d.clone(), friends_present: friends_in(d) })
                    .collect();
                match should_migrate(TITLE, &at, &others) {
                    Migration::Stay => break,
                    Migration::MoveTo(id) => {
                        at = shards.iter().find(|d| d.shard_id == id).unwrap().clone();
                        hops += 1;
                        assert!(hops < 8, "migration cycled instead of settling");
                    }
                }
            }
            assert_eq!(at.shard_id, [1; 16]);
        }
    }

    fn real(first: u8, created_at: u64) -> ShardDesc {
        let mut d = desc(0, created_at, 1);
        d.shard_id[0] = first;
        d
    }

    #[test]
    fn the_hosts_that_created_a_shard_on_2026_11_27_walk_to_their_joiners() {
        // Logged created_at values: df1707… 1795756872, 3f1f4c… 1795611246,
        // 86f92b… 1795776284 (host, 21:44), 88792f… 1795777273 (host, 22:01).
        let df17 = real(0xdf, 1_795_756_872);
        let f3f1 = real(0x3f, 1_795_611_246);
        let h2144 = real(0x86, 1_795_776_284);
        let h2201 = real(0x88, 1_795_777_273);
        let c = |d: &ShardDesc| Candidate { desc: d.clone(), friends_present: 0 };

        // 21:44: the joiner went to df17…; the host, still in its own shard, follows.
        assert_eq!(should_migrate(TITLE, &h2144, &[c(&df17)]), Migration::MoveTo(df17.shard_id));
        assert_eq!(should_migrate(TITLE, &df17, &[c(&h2144)]), Migration::Stay);

        // 22:01: the joiner went df17… → 3f1f…; the host takes the oldest it hears of.
        assert_eq!(
            should_migrate(TITLE, &h2201, &[c(&df17), c(&f3f1)]),
            Migration::MoveTo(f3f1.shard_id)
        );
        assert_eq!(should_migrate(TITLE, &f3f1, &[c(&h2201), c(&df17)]), Migration::Stay);
    }
}
