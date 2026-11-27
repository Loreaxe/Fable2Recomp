// @file        rexnet/core/tests/shard_hosting_converges.rs
// @brief       A node hosting a game still reaches its shard, and keeps its address.
//
// @copyright   Copyright (c) 2026 Ryan Fisher <ryanfisher099@gmail.com>
//              All rights reserved.
//
// @license     BSD 3-Clause License
//              See LICENSE file in the project root for full license text.

//! A game in progress pins ADDRESSES, not the shard.
//!
//! On 2026-11-27 a host whose start-up search found nothing created its own
//! shard, opened its game, and stayed there for as long as it hosted, while its
//! joiner walked to the older shard: placement and migration both waited for
//! the game to end. The engine now enters and leaves a shard mid-game without
//! re-deriving any address. These run the real swarm path for both halves: a
//! first placement made while hosting, and a split between two hosting nodes.
//!
//! Loopback, no bootstrap (kad in server mode), one title id per test so the
//! suite's nodes, which find each other over mDNS, stay apart.

use std::time::Duration;

use libp2p::Multiaddr;
use rexnet_core::engine::{self, Command, EngineConfig, EngineHandles, Event};
use tokio::sync::{mpsc, oneshot};

const TITLE_FIRST_PLACEMENT: u32 = 0x4D53_0920;
const TITLE_TWO_HOSTS: u32 = 0x4D53_0921;
const SETTLE_TIMEOUT: Duration = Duration::from_secs(30);

async fn spawn_node(dir: &std::path::Path, title_id: u32) -> EngineHandles {
    let keypair = libp2p::identity::Keypair::generate_ed25519();
    engine::spawn(
        keypair,
        EngineConfig {
            data_dir: dir.to_path_buf(),
            title_id,
            display_name: "test".into(),
            bootstrap: vec![],
            listen_port: 0,
            game_port: 0,
            relays: Vec::new(),
            force_tunnel: false,
        },
    )
    .await
    .expect("engine start")
}

async fn wire(from: &EngineHandles, to: &EngineHandles) {
    let (tx, rx) = oneshot::channel();
    let _ = to.cmd_tx.send(Command::LocalAddrs { reply: tx });
    let addrs: Vec<Multiaddr> = rx.await.unwrap_or_default();
    assert!(!addrs.is_empty(), "a node never reported a listen address");
    for addr in addrs {
        let with_peer = addr.with(libp2p::multiaddr::Protocol::P2p(to.peer_id));
        let _ = from.cmd_tx.send(Command::ConnectManual { multiaddr: with_peer.to_string() });
    }
}

async fn shard_of(cmd_tx: &mpsc::UnboundedSender<Command>) -> Option<[u8; 16]> {
    let (tx, rx) = oneshot::channel();
    let _ = cmd_tx.send(Command::ShardStatus { reply: tx });
    rx.await.unwrap_or(None)
}

async fn await_shard(cmd_tx: &mpsc::UnboundedSender<Command>) -> Option<[u8; 16]> {
    tokio::time::timeout(SETTLE_TIMEOUT, async {
        loop {
            if let Some(id) = shard_of(cmd_tx).await {
                return id;
            }
            tokio::time::sleep(Duration::from_millis(200)).await;
        }
    })
    .await
    .ok()
}

/// Every own address the node has announced so far, oldest first.
fn own_addresses(node: &mut EngineHandles, seen: &mut Vec<u32>) {
    while let Ok(event) = node.evt_rx.try_recv() {
        if let Event::LocalAddress { virtual_ip } = event {
            seen.push(virtual_ip);
        }
    }
}

fn host_a_game(node: &EngineHandles, id: u8) {
    let _ = node.cmd_tx.send(Command::SessionCreate {
        id: [id; 16],
        slots_total: 2,
        slots_open: 1,
        is_public: true,
    });
}

#[tokio::test(flavor = "multi_thread")]
async fn a_hosting_node_enters_the_existing_shard_and_keeps_its_address() {
    let dir_a = tempdir();
    let dir_b = tempdir();
    let mut host = spawn_node(&dir_a, TITLE_FIRST_PLACEMENT).await;
    let other = spawn_node(&dir_b, TITLE_FIRST_PLACEMENT).await;
    tokio::time::sleep(Duration::from_millis(500)).await;
    wire(&other, &host).await;
    tokio::time::sleep(Duration::from_secs(2)).await;

    // The other node founds the shard first.
    let _ = other.cmd_tx.send(Command::ShardEnable { cap: 0 });
    let shard = await_shard(&other.cmd_tx).await.expect("the other node never settled");

    // The host opens its game BEFORE it looks for a shard: placement used to be
    // deferred for as long as the game lasted.
    host_a_game(&host, 0xA1);
    tokio::time::sleep(Duration::from_millis(500)).await;
    let mut seen = Vec::new();
    own_addresses(&mut host, &mut seen);
    let before = *seen.last().expect("the host never announced its own address");

    let _ = host.cmd_tx.send(Command::ShardEnable { cap: 0 });
    let placed = await_shard(&host.cmd_tx).await.expect("a hosting node was kept out of every shard");
    assert_eq!(placed, shard, "the hosting node founded its own shard instead of joining");

    tokio::time::sleep(Duration::from_secs(2)).await;
    own_addresses(&mut host, &mut seen);
    assert!(
        seen.iter().all(|vip| *vip == before),
        "the hosting node's address moved under its game: {seen:08x?}"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn two_hosting_nodes_in_two_shards_still_converge() {
    let dir_a = tempdir();
    let dir_b = tempdir();
    let mut a = spawn_node(&dir_a, TITLE_TWO_HOSTS).await;
    let mut b = spawn_node(&dir_b, TITLE_TWO_HOSTS).await;
    tokio::time::sleep(Duration::from_millis(500)).await;
    wire(&b, &a).await;
    tokio::time::sleep(Duration::from_secs(2)).await;

    // Both host, then both look at once: the split-brain case with both pinned.
    host_a_game(&a, 0xA2);
    host_a_game(&b, 0xB2);
    tokio::time::sleep(Duration::from_millis(500)).await;
    let (mut seen_a, mut seen_b) = (Vec::new(), Vec::new());
    own_addresses(&mut a, &mut seen_a);
    own_addresses(&mut b, &mut seen_b);
    let (a_before, b_before) = (*seen_a.last().expect("A's address"), *seen_b.last().expect("B's address"));

    let _ = a.cmd_tx.send(Command::ShardEnable { cap: 0 });
    let _ = b.cmd_tx.send(Command::ShardEnable { cap: 0 });
    await_shard(&a.cmd_tx).await.expect("A never settled");
    await_shard(&b.cmd_tx).await.expect("B never settled");

    // A split is legal; it must heal while both still host (rescan is 20 s).
    let deadline = tokio::time::Instant::now() + Duration::from_secs(120);
    loop {
        let (now_a, now_b) = (shard_of(&a.cmd_tx).await, shard_of(&b.cmd_tx).await);
        if now_a.is_some() && now_a == now_b {
            break;
        }
        assert!(
            tokio::time::Instant::now() < deadline,
            "two hosting nodes stayed split: A={now_a:02x?} B={now_b:02x?}"
        );
        tokio::time::sleep(Duration::from_secs(2)).await;
    }

    own_addresses(&mut a, &mut seen_a);
    own_addresses(&mut b, &mut seen_b);
    assert!(seen_a.iter().all(|vip| *vip == a_before), "A's address moved under its game: {seen_a:08x?}");
    assert!(seen_b.iter().all(|vip| *vip == b_before), "B's address moved under its game: {seen_b:08x?}");
}

/// Scratch dir under the platform temp dir (TMPDIR on Unix).
fn tempdir() -> std::path::PathBuf {
    let mut path = std::env::temp_dir();
    path.push(format!(
        "rexnet-shard-hosting-test-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ));
    std::fs::create_dir_all(&path).expect("temp dir");
    path
}
