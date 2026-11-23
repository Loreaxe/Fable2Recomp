using Godot;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Refii.Core;
using Refii.Text;
using Refii.Ui;
using static Refii.Ui.Engine_UiModel;

namespace Refii.Titles.Fable2;

using Refii.Host;
using static Refii.Host.HostBridge;

/// <summary>
/// Fable 2's own part of the host bridge's ABI: the co-op hero and screen, the pub games, quests,
/// family, property and shops, the hero's inventory and looks, the loading screens, the front end
/// and pause, and the Fable 2 UI feeds. Bound from the same library (Fable 2's instance,
/// <c>bin/Fable2/</c>); the shell's <see cref="HostBridge"/> binds only the shared ABI.
/// </summary>
public static class Fable2Bridge
{
	static Fable2Bridge() => Refii.Core.Engine_NativeLibraries.Install();



	[ThreadStatic]
	private static uint[]? _playerTwoQueryWords;

	[ThreadStatic]
	private static byte[]? _playerTwoHudText;

	/// <summary>
	/// The next question a co-op script put to the partner (core/coop_consent.cpp): its ticket,
	/// kind (1 sleep, 2 sex), asking player (1 or 2) and detail (the sleep type); null when none.
	/// </summary>
	[ThreadStatic]
	private static byte[]? _consentText;

	[System.ThreadStatic]
	private static uint[]? _minimapWords;

	[ThreadStatic]
	private static uint[]? _realEstateWords;

	[ThreadStatic]
	private static byte[]? _realEstateOccupant;

	[DllImport("refii_hostbridge")]
	private static extern int RefiiHostGetLoadingScreenText(
		uint[] outWords, byte[] text, uint capacity);

	private static uint[]? _coopSessionWords;
	private static uint[]? _coopWords;
	private static readonly uint[] _dyeStateWords = new uint[160 * DyeStateWords];

	/// <summary>Words per entry of <c>RefiiHostGetDyeStates</c>: item, record, flags.</summary>
	private const int DyeStateWords = 3;
	private static readonly byte[] _estateIntent = new byte[128];
	private static float[]? _exitRequestAt;
	private static uint[]? _furnitureWords;
	private static readonly byte[] _properties = new byte[32768];
	private static byte[]? _shopFrontText;
	private static uint[]? _shopFrontWords;
	private static byte[]? _topBoxLiteral;

	public static (uint Generation, ulong Result) GetNewGameState()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp) return default;
		ulong result = RefiiHostGetNewGameState(out uint generation);
		return (generation, result);
	}

	public static uint GetGoldenCockInteraction() =>
		TryGetAbiVersion() is not null && IsGuestUp ? RefiiHostGetGoldenCockInteraction() : 0u;

	/// <summary>
	/// The layers the guest's own dye builder states for ONE inventory item INSTANCE (worn or
	/// not) — <paramref name="item"/> is its guest address, <see cref="InventoryRecord.Item"/> —
	/// in the same words a character's composites use, so the menu shows a dyed garment through
	/// the very compositor that dresses the hero. The dye is the instance's own record, so two
	/// copies of one record answer apart. Empty for an undyed item.
	/// </summary>
	public static GuestTextureComposite[] GetItemDyeLayers(uint item, out uint generation)
	{
		generation = 0;
		if (item == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return System.Array.Empty<GuestTextureComposite>();
		}

		uint[] words = _textureLayerWords;
		uint written;
		try
		{
			written = RefiiHostGetItemDyeLayers(item, words, (uint)words.Length, out generation);
		}
		catch (EntryPointNotFoundException)
		{
			return System.Array.Empty<GuestTextureComposite>();
		}

		return DecodeTextureLayers(words, written);
	}

	/// <summary>
	/// Which pause tabs the guest itself would offer — bit N set means tab N (2..8) is
	/// available. The guest's own prev/next skips an unavailable tab (`sub_826B83D0`) but its
	/// push does NOT re-check, so the owned menu must ghost these entries itself. Fails open
	/// to all-available; unavailability only ever comes from a completed read of the guest's
	/// own restriction flags (a childhood/cutscene gate on the hero entity).
	/// </summary>
	public static uint PauseTabsAvailable() =>
		TryGetAbiVersion() is null ? AllPauseTabs : RefiiHostPauseTabsAvailable();

	/// <summary>
	/// True while the abilities screen's spell-assignment blade is up: between the guest's own
	/// enter (`sub_826C2480`) and exit (`sub_826C25C0`) of that mode.
	/// </summary>
	public static bool AbilitiesAssignmentOpen() =>
		HostBridge.Stated(HostBridge.StatedValue.AbilitiesAssignMode) == 1u;

	/// <summary>
	/// The player who owns the open in-game GUI (1 or 2) — the guest's own owner of its pause and
	/// what opens from it, sampled every guest tick. Meaningful while a menu is open.
	/// </summary>
	public static int GetMenuOwner() => TryGetAbiVersion() is null ? 1 : RefiiHostGetMenuOwner();

	/// <summary>
	/// The player whose expression wheel is open (1 or 2), 0 for none — as the guest's wheel open and
	/// close state it (lhGuiManager OpenExpressionWheel TU1 0x8236F110 / disc 0x82337310, the record
	/// id it hands the wheel; CloseExpressionWheel 0x8236F340 / 0x82337540).
	/// </summary>
	public static int GetExpressionWheelPlayer() =>
		TryGetAbiVersion() is null ? 0 : RefiiHostGetExpressionWheelPlayer();

	/// <summary>
	/// The players a region exit refused since the last call because their partner was out of
	/// range (bit 0 player one, bit 1 player two) — the co-op travel rule, once per approach.
	/// Read-and-clear.
	/// </summary>
	public static int TakeExitRefused() => TryGetAbiVersion() is null ? 0 : RefiiHostTakeExitRefused();

	/// <summary>
	/// Online: this machine's player stands at a region exit waiting for the partner (the co-op
	/// travel rule) — restated to the peer, which tells its player.
	/// </summary>
	public static bool ExitWaiting() => TryGetAbiVersion() is not null && IsGuestUp && RefiiHostGetExitWaiting() != 0;

	/// <summary>
	/// The online joiner's newest region-exit use: a sequence (0 = none yet) and the exit's guest
	/// position. Restated to the host, which fires the exit standing there.
	/// </summary>
	public static (uint Sequence, float X, float Y, float Z) ExitRequest()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return (0u, 0f, 0f, 0f);
		}

		float[] at = _exitRequestAt ??= new float[3];
		uint sequence = RefiiHostGetExitRequest(at);
		return (sequence, at[0], at[1], at[2]);
	}

	/// <summary>
	/// The things whose use prompt answers player two this frame — the nearer player's, in co-op
	/// (coop_hero_hooks.cpp) — into <paramref name="into"/>; returns the count. A pooled world
	/// icon's <see cref="WorldSprite.Entity"/> is its prompted thing.
	/// </summary>
	public static int GetPlayerTwoPrompts(uint[] into) =>
		TryGetAbiVersion() is null || !IsGuestUp ? 0 : RefiiHostGetPlayerTwoPrompts(into, into.Length);

	/// <summary>The things whose use prompt only player two sees this frame (player one's half hides them).</summary>
	public static int GetPlayerOneHiddenPrompts(uint[] into) =>
		TryGetAbiVersion() is null || !IsGuestUp ? 0 : RefiiHostGetPlayerOneHiddenPrompts(into, into.Length);

	/// <summary>Player two's own hold-to-use meter: the fraction 0..1 and whether they hold A.</summary>
	public static (float Fraction, bool Pressed) GetPlayerTwoHold()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return (0f, false);
		}

		try
		{
			RefiiHostGetPlayerTwoHold(out float fraction, out int pressed);
			return (fraction, pressed != 0);
		}
		catch (EntryPointNotFoundException)
		{
			return (0f, false);
		}
	}

	/// <summary>A player's properties (1 or 2; 0 the player whose menus are open).</summary>
	public static List<Property> GetProperties(int player)
	{
		var list = new List<Property>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return list;
		}

		uint written;
		try
		{
			written = RefiiHostGetProperties(player, _properties, (uint)_properties.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return list;
		}

		if (written == 0 || written >= _properties.Length)
		{
			return list;
		}

		foreach (string line in Encoding.UTF8.GetString(_properties, 0, (int)written).Split('\n'))
		{
			string[] p = line.Split('\t');
			if (p.Length >= 7 &&
				ulong.TryParse(p[0], System.Globalization.NumberStyles.HexNumber, null, out ulong uid) &&
				uint.TryParse(p[2], out uint flags) && int.TryParse(p[3], out int income) &&
				int.TryParse(p[4], out int value) &&
				float.TryParse(p[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float condition) &&
				int.TryParse(p[6], out int cost))
			{
				int bought = p.Length >= 8 && int.TryParse(p[7], out int paid) ? paid : 0;
				list.Add(new Property(uid, p[1], flags, income, value, condition, cost, bought));
			}
		}

		return list;
	}

	/// <summary>
	/// The oldest property notice: kind 0 disrepair, 1 repaired, 2 not enough gold, 3 nothing to
	/// repair, 6 a purchase shared with the partner (the building's UID); 4 owed gold paid on return,
	/// 5 a sale's half paid to the partner (the gold in <c>Uid</c>).
	/// </summary>
	public static (int Kind, ulong Uid)? TakePropertyNotice()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		try
		{
			return RefiiHostTakePropertyNotice(out int kind, out ulong uid) != 0 ? (kind, uid) : null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>
	/// The partnership between the two heroes in the game (Fable 3's): 0 none, 1 business partners,
	/// 2 married; null when both heroes are not here with their identities known.
	/// </summary>
	public static int? GetPartnership()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		try
		{
			return RefiiHostGetPartnership(out int kind) != 0 ? kind : null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>
	/// The oldest household action for the co-op peer to run (<c>&lt;action&gt;:&lt;uid&gt;:&lt;value&gt;</c>):
	/// a joiner's estate action, gift, furniture level or bed, or the host's kept gift; null when none.
	/// </summary>
	public static string? TakeEstateIntent()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		try
		{
			uint written = RefiiHostTakeEstateIntent(_estateIntent, (uint)_estateIntent.Length);
			return written == 0 ? null : Encoding.UTF8.GetString(_estateIntent, 0, (int)written);
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>How many of an item (by its GDB record name) player <paramref name="player"/>'s hero carries; -1 without the hero.</summary>
	public static int GetItemCount(int player, string record)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return -1;
		}

		try
		{
			return RefiiHostGetItemCount(player, Fnv1(record));
		}
		catch (EntryPointNotFoundException)
		{
			return -1;
		}
	}

	/// <summary>Sets the partnership in both heroes' saved values (0 ends it). False when it could not.</summary>
	public static bool SetPartnership(int kind)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiHostSetPartnership(kind) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>
	/// A villager's opinion of player 1 or 2 by its network id: love, fear, attractiveness,
	/// morality and respect into <paramref name="axes"/> (five). False when it is not found here.
	/// </summary>
	public static bool GetOpinionOfUid(ulong uid, int player, float[] axes)
	{
		if (uid == 0ul || axes.Length < 5 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiHostGetOpinionOfUid(uid, player, axes) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>
	/// The 3D object the current in-game menu panel shows, as the guest states it. Null when
	/// no screen has one spawned.
	/// </summary>
	/// <remarks>
	/// `ScreenId` names which panel it belongs to (8 receive-items, 15 items/weapons, 22 shop,
	/// 25 gift, 26 selection, 27 decoration) — and therefore which anchor element to hang it
	/// on. The model hash is the same currency the model index keys on. `Item` is the inventory
	/// item INSTANCE the panel shows (<see cref="InventoryRecord.Item"/>), the key
	/// <see cref="GetItemDyeLayers"/> takes; 0 when the panel shows no owned item.
	/// </remarks>
	public readonly record struct UiPreviewObject(
		uint ScreenId, uint ModelHash, uint Entity,
		Vector3 Translation, Vector3 Rotation, float Scale, int RotationAxis,
		uint Item = 0);

	/// <summary>
	/// Which of the three ways the guest lays its loading art out. A normal level load packs the
	/// 1280x720 image into a square texture in two pieces with the right-hand strip stored
	/// ROTATED; a fixed-art screen is one plain full-bleed quad.
	/// </summary>
	/// <param name="FixedArt">The screen constructor's fixed-art argument — one quad, plain UVs (inset on 4:3).</param>
	/// <param name="QuitFade">Set by the art decision — the quit-without-save fade; one quad, plain UVs.</param>
	/// <param name="Narrow">The text-only constructor made the screen (`screen+320`).</param>
	public readonly record struct LoadingLayout(bool FixedArt, bool QuitFade, bool Narrow)
	{
		/// <summary>
		/// True when the art is the two-piece packing that has to be reassembled. A default
		/// rather than a case, matching `sub_8235FDC0`, which falls through to the two-quad
		/// path whenever neither flag is set.
		/// </summary>
		public bool IsSplit => !FixedArt && !QuitFade;
	}

	/// <summary>
	/// The loading screen's TEXT and its spinner clock.
	/// </summary>
	/// <param name="TipKey">
	/// The Babel key of the tip the guest last drew (the tag its DrawTip resolved), or 0 before
	/// it has drawn one. It is a KEY, not a string: the host resolves it through its own Babel
	/// database. The guest rotates the tip on its own ten-second clock.
	/// </param>
	/// <param name="TipIndex">Which tip, and <paramref name="TipCount"/> how many there are.</param>
	/// <param name="SpinnerFrame">
	/// The screen's frame counter, counted from its spinner draws. The spinner is `counter % 25`
	/// into a 5x5 grid — a timed flipbook, NOT load progress. The game has no progress bar at all.
	/// </param>
	/// <param name="TipsEnabled">On for every screen its constructor states.</param>
	/// <param name="BannerEnabled">On for the level screen, off for the text-only one.</param>
	/// <param name="LevelName">The level the banner names, the level screen's constructor argument.</param>
	/// <param name="Overlay">The text-only screen's text, or the manager's set text; usually empty.</param>
	public readonly record struct LoadingText(
		uint TipKey, int TipIndex, int TipCount, uint SpinnerFrame,
		bool TipsEnabled, bool BannerEnabled, string LevelName, string Overlay);

	/// <summary>The loading screen's text as its own calls state it, or null when there is no screen.</summary>
	public static LoadingText? GetLoadingScreenText()
	{
		uint[] words = new uint[6];
		byte[] buffer = new byte[512];
		try
		{
			if (RefiiHostGetLoadingScreenText(words, buffer, (uint)buffer.Length) == 0)
			{
				return null;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		string level = ReadUtf8(buffer, 0, out int next);
		string overlay = ReadUtf8(buffer, next, out _);

		return new LoadingText(words[2], (int)words[0], (int)words[1], words[3],
			words[4] != 0, words[5] != 0, level, overlay);
	}

	/// <summary>The loading screen's art layout, or null when no screen object exists.</summary>
	public static LoadingLayout? GetLoadingScreenLayout() =>
		HostBridge.GetLoadingScreen() is { Screen: not 0u }
			? new LoadingLayout(
				HostBridge.Stated(HostBridge.StatedValue.LoadingFixedArt) != 0u,
				HostBridge.Stated(HostBridge.StatedValue.LoadingQuitFade) != 0u,
				HostBridge.Stated(HostBridge.StatedValue.LoadingTextOnly) != 0u)
			: null;

	/// <summary>
	/// This player's role in a co-op session, as the network manager states it: 0 no session,
	/// 1 host, 2 joiner. SOLO READS AS HOST — a single-player game has <c>+52</c> and <c>+53</c>
	/// set — so 1 is "host or solo", not "co-op host". The manager is <c>g_XrnmNetContext</c>
	/// (<c>0x83497490</c>; <c>*(*(0x83496AB8)+12)+140</c> is its counted copy, the fallback);
	/// <c>+52</c> is "in a session", <c>+53</c> host and <c>+54</c> joiner, and the guest's own
	/// joiner-side front-end teardown is gated on exactly <c>+52 &amp;&amp; +54</c>.
	/// </summary>
	public static uint CoopRole => RefiiHostGetCoopRole();

	/// <summary>True when this player joined someone else's session.</summary>
	public static bool IsCoopJoiner => CoopRole == 2u;

	/// <summary>The co-op screen's two share sliders, 0..1. Both 0.5 until somebody moves one.</summary>
	public readonly record struct CoopShare(bool Known, float Gold, float Experience);

	/// <summary>
	/// The shares as the sliders state them: the globals at <c>0x833193D4</c> (gold) and
	/// <c>0x833193D8</c> (experience). THE GLOBALS ARE THE LIVE VALUE — the co-op manager's own
	/// copies at <c>+176</c>/<c>+172</c> sit at zero under a centred pip and read as 100/0.
	/// </summary>
	public static CoopShare CoopShares()
	{
		int known = RefiiHostGetCoopShares(out float gold, out float experience);
		return new CoopShare(known != 0, gold, experience);
	}

	/// <summary>The chosen co-op hero save's directory name, or null when none is chosen.</summary>
	public static string? CoopHeroSaveName()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		byte[] buffer = new byte[256];
		uint written = RefiiHostGetCoopHeroSaveName(buffer, (uint)buffer.Length);
		return written == 0 ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length - 1));
	}

	/// <summary>
	/// The co-op hero save the player chose on the co-op screen, as the guest read it: money,
	/// gender (2 = male), morality, purity, renown, fifteen ranks, four experience pools, four
	/// totals. Null until a save is chosen.
	/// </summary>
	public static float[]? CoopHeroSave()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		var values = new float[28];
		return RefiiHostGetCoopHeroSave(values, (uint)values.Length) != 0 ? values : null;
	}

	/// <summary>The hero's face record (component 17) and its tan, fat (0..1) and height (-1..1).</summary>
	public static (uint Face, float Fat, float Height, float Tan)? HeroLooks()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		return RefiiHostGetHeroLooks(out uint face, out float fat, out float height, out float tan) != 0
			? (face, fat, height, tan)
			: null;
	}

	/// <summary>
	/// The net ids of the item entities player 1 or 2 holds on THIS machine (ABI 135; the full
	/// 64-bit uid since 170) — what the host sends so a client attaches the replicas rather than
	/// making its own. Null when the player has no body here; zero for an empty slot.
	/// </summary>
	public static (ulong Melee, ulong Ranged)? CoopHeldUids(uint player)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		return RefiiHostGetCoopHeldUids(player, out ulong melee, out ulong ranged) != 0
			? (melee, ranged)
			: null;
	}

	/// <summary>
	/// The hero's equipped weapons as GDB record ids — the same id space the inventory records
	/// carry — or null when the hero has no weapon set. 0 in a slot means it is empty.
	/// </summary>
	public static (uint Melee, uint Ranged)? HeroWeapons()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		return RefiiHostGetHeroWeapons(out uint melee, out uint ranged) != 0 ? (melee, ranged) : null;
	}

	/// <summary>
	/// The weapon switches the menu has pending, as record ids — what the weapons list emphasises
	/// before the weapon in hand until the menu closes and the swap is applied. 0 for none. Stated
	/// by the menu's pick and clear (pending_weapons_stated.cpp).
	/// </summary>
	public static (uint Melee, uint Ranged)? PendingWeapons()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		return (HostBridge.Stated(HostBridge.StatedValue.PendingMelee), HostBridge.Stated(HostBridge.StatedValue.PendingRanged));
	}

	/// <summary>The co-op session's raw words off the authoritative network manager (ABI 134).</summary>
	public readonly record struct CoopSessionWords(
		bool ManagerPresent, bool Session, bool Host, bool Client, bool Lost, bool ClientJoined,
		uint PeerCount, uint PlayerOne, uint PlayerTwo, bool GameCopyPresent,
		uint GameMode, uint PlayerTwoPad)
	{
		/// <summary>`game+284 == 2` — the guest's own `IsLocalCoopGame` (`sub_82340300`).</summary>
		public bool LocalCoop => GameMode == 2u;
	}

	public static CoopSessionWords? CoopSessionRaw()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		_coopSessionWords ??= new uint[12];
		uint count = RefiiHostGetCoopSession(_coopSessionWords, (uint)_coopSessionWords.Length);
		if (count == 0)
		{
			return null;
		}

		uint[] w = _coopSessionWords;
		return new CoopSessionWords(
			w[0] != 0, w[1] != 0, w[2] != 0, w[3] != 0, w[4] != 0, w[5] != 0, w[6], w[7], w[8],
			w[9] != 0,
			count >= 12 ? w[10] : 0u, count >= 12 ? w[11] : 0xFFFFFFFFu);
	}

	/// <summary>
	/// The HeroState version the pump last APPLIED to a player's body (1 host, 2 joiner), 0 when
	/// none has been, or when the bridge is down. The receiver acks a version only once this
	/// reports it.
	/// </summary>
	public static uint HeroStateApplied(uint player)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0u;
		}

		try
		{
			return RefiiHostGetHeroStateApplied(player);
		}
		catch (DllNotFoundException)
		{
			return 0u;
		}
		catch (EntryPointNotFoundException)
		{
			return 0u;
		}
	}

	public static (uint Ticket, int Kind, int Asker, int Detail, string Text)? ConsentTake()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		try
		{
			byte[] text = _consentText ??= new byte[256];
			if (RefiiHostConsentTake(out uint ticket, out int kind, out int asker, out int detail, text, (uint)text.Length) == 0)
			{
				return null;
			}

			int length = Array.IndexOf(text, (byte)0);
			return (ticket, kind, asker, detail,
				System.Text.Encoding.UTF8.GetString(text, 0, length < 0 ? text.Length : length));
		}
		catch (DllNotFoundException)
		{
			return null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>The partner's answer to a question from <see cref="ConsentTake"/>.</summary>
	public static void ConsentGive(uint ticket, bool yes)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		try
		{
			RefiiHostConsentGive(ticket, yes ? 1 : 0);
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// The brightness the guest's camera holds, `G = 1.4 − 0.8·slider` (0.6..1.4, 1.0 at rest):
	/// the Video page writes it at `S+0x14` (0x829C08A4 commit, plus preview, revert and the
	/// profile restores), the camera publishes it every frame (0x821A6188) and the tonemap takes
	/// `pow(colour, G × 0.45454544)` as c78. Null when no camera is up.
	/// </summary>
	/// <remarks>
	/// Six guest sites write the field and none carries it as an argument, so it is read where
	/// the camera keeps it. Which camera follows `sub_823893C0`: the front end's
	/// (`dword_83496AC0`, `vt[5]` = `+0xC` → `+0x1C`) while `byte_83496AC4` is set, else the
	/// game's (`dword_83496AB8`, `vt[5]` = `+0xC` → `+0x6C`); either way `S = *(R+4)`.
	/// </remarks>
	public static float? GuestBrightness()
	{
		uint frontEnd = PickByBuild(0x83496AC0u, 0x83496928u);
		uint frontEndUp = PickByBuild(0x83496AC4u, 0x83496925u);
		uint game = PickByBuild(0x83496AB8u, 0x83496920u);

		static uint Word(uint address) => TryPeekWord(address, out uint value) ? value : 0u;
		static bool Plausible(uint address) => address >= 0x10000u && address < 0xC0000000u;

		uint aligned = frontEndUp & ~3u;
		int shift = 24 - (int)((frontEndUp & 3u) * 8u);
		bool useFrontEnd = Word(frontEnd) != 0 && ((Word(aligned) >> shift) & 0xFFu) != 0;

		uint root = Word(useFrontEnd ? frontEnd : game);
		if (!Plausible(root))
		{
			return null;
		}

		uint inner = Word(root + 0xC);
		uint wrapper = Plausible(inner) ? Word(inner + (useFrontEnd ? 0x1Cu : 0x6Cu)) : 0u;
		uint camera = Plausible(wrapper) ? Word(wrapper + 4) : 0u;
		if (!Plausible(camera))
		{
			return null;
		}

		float g = System.BitConverter.Int32BitsToSingle((int)Word(camera + 0x14));
		return float.IsFinite(g) && g is >= 0.5f and <= 1.5f ? g : null;
	}

	/// <summary>
	/// One record in the hero's inventory. NOT one item — see <see cref="Fable2Bridge.GetHeroInventory"/>.
	/// </summary>
	/// <param name="RecordId">The FNV-1 GDB key. NOT unique across records.</param>
	/// <param name="Count">How many of this thing the record holds.</param>
	/// <param name="Category">−1 when the item states none.</param>
	/// <param name="Viewed">The new-item marker the pause tabs decorate with.</param>
	/// <param name="Item">The item INSTANCE the inventory node holds (its guest address) — the
	/// key the dye reads take (<see cref="GetItemDyeLayers"/>); 0 when the snapshot stated none.
	/// Meaningful only until the next inventory sample.</param>
	public readonly record struct InventoryRecord(
		uint RecordId, int Count, int Category, bool Viewed, uint Item = 0);

	/// <summary>
	/// Names the region to sample. The pump answers one tick later — it cannot know which row
	/// is highlighted, and sampling calls eleven guest providers so it has to run over there.
	/// </summary>
	public static void SetWorldMapRegion(uint index)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		try
		{
			RefiiHostSetWorldMapRegion(index);
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// What the world map's minimap page plots from live state — see RefiiHostGetMinimapLive.
	/// <see cref="Known"/> is false before a level stands.
	/// </summary>
	public readonly record struct MinimapLive(
		uint LevelKey, int MinX, int MinY, int MaxX, int MaxY,
		uint Dog, bool HasTrailTarget, Vector3 TrailTarget, bool PrimaryQuestHere, uint Hero,
		uint[] Merchants)
	{
		public bool Known => LevelKey != 0u || MaxX != MinX;

		/// <summary>`max(maxX − minX, maxY − minY)` — the page's projection divisor.</summary>
		public float Extent => System.Math.Max(MaxX - MinX, MaxY - MinY);
	}

	public static MinimapLive GetMinimapLive()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		uint[] words = _minimapWords ??= new uint[13 + 32];
		try
		{
			if (RefiiHostGetMinimapLive(words, (uint)words.Length) == 0u)
			{
				return default;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		static float F(uint word) => System.BitConverter.Int32BitsToSingle(unchecked((int)word));
		int merchantCount = (int)System.Math.Min(words[12], 32u);
		var merchants = new uint[merchantCount];
		System.Array.Copy(words, 13, merchants, 0, merchantCount);
		return new MinimapLive(
			words[0], unchecked((int)words[1]), unchecked((int)words[2]),
			unchecked((int)words[3]), unchecked((int)words[4]),
			words[5], words[6] != 0u, new Vector3(F(words[7]), F(words[8]), F(words[9])),
			words[10] != 0u, words[11], merchants);
	}

	/// <summary>
	/// Screen 28's one building. The screen's left column is a list of COMMANDS, not of
	/// properties: there is a single building, resolved when it opens, and nothing to select.
	///
	/// Every number is the guest's own answer, because each walks something the host has no
	/// business reproducing — a prosperity term off a world component, a GDB adjustment array
	/// matched against <see cref="TypeMask"/>, an occupant-opinion term weighted over five
	/// personality axes. The PRESENTATION is entirely ours: the star bin, the gold prefix, the
	/// negative recolour, which Variable row stands and which legend Page 2 carries.
	///
	/// <paramref name="Record"/> is the GDB record the text hangs off — NOT the building
	/// entity's own record. `sub_821DA3B0` takes the entity's def handle, then a property named
	/// after the component's class, and the entry-ref that yields is this hash.
	///
	/// <paramref name="TitleRecord"/> is the hero's current title record (`heroComponent57+92`),
	/// which is how the guest names the owner of a building the hero owns and is not renting
	/// out: `HeroTitleComponent.TextTag` through Babel. <paramref name="Occupant"/> is the
	/// creature name of the tenant, or of the owner when that is not the hero — empty in the
	/// hero-owned case, because the hero has no creature name.
	/// </summary>
	public readonly record struct RealEstate(
		bool Known, uint Record, uint TypeMask, bool Owned, bool HasTenant,
		int Value, int Economy, int Decoration, int Income, int SalePrice, int SellingFees,
		int NotVacant, int OwnerKilled, int HeroOpinion, uint BonusesRecord, string Occupant,
		uint TitleRecord)
	{
		/// <summary>The guest's own "is a business" test — `mask &amp; 0xEFE` at 0x82999CF8.
		/// Every type but HOME and UNIQUE.</summary>
		public bool IsBusiness => (TypeMask & 0xEFEu) != 0u;

		/// <summary>Which BuildingType this is: the FIRST set bit, as `sub_8299A550` decodes it.
		/// 0 HOME, 1 DOCK, 2 FARM, 3 TOWNHALL, 4 WAREHOUSE, 5 SHOP, 6 BAR, 7 BORDELLO, 8 UNIQUE,
		/// 9 STALL, 10 HOUSE, 11 TEMPLE; -1 when nothing is set.</summary>
		public int Type
		{
			get
			{
				for (int bit = 0; bit < 12; ++bit)
				{
					if ((TypeMask & (1u << bit)) != 0u)
					{
						return bit;
					}
				}

				return -1;
			}
		}

		/// <summary>`sub_8299AF88`'s bins, over the building's current value.</summary>
		public int Stars => Value <= 0 ? 0
			: Value < 5000 ? 1
			: Value < 12000 ? 2
			: Value < 26000 ? 3
			: Value < 55000 ? 4
			: 5;
	}

	/// <summary>Names the property under the world map's cursor (0 for none); the pump samples it.</summary>
	public static void RequestPropertyEstate(ulong uid)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiHostRequestPropertyEstate(uid);
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// Screen 28's numbers for the property <paramref name="uid"/>, sampled while its building's
	/// region is loaded; not <see cref="RealEstate.Known"/> otherwise, or before the pump answers.
	/// </summary>
	public static RealEstate GetPropertyEstate(ulong uid)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp || uid == 0ul)
		{
			return default;
		}

		uint[] words = _realEstateWords ??= new uint[15];
		byte[] occupant = _realEstateOccupant ??= new byte[128];
		occupant[0] = 0;
		uint known;
		try
		{
			known = RefiiHostGetPropertyEstate(uid, words, (uint)words.Length, occupant, (uint)occupant.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		return known == 0u ? default : DecodeRealEstate(true, words, occupant);
	}

	/// <summary>Reads screen 28's building. See <see cref="RealEstate"/>.</summary>
	public static RealEstate GetRealEstate()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		uint[] words = _realEstateWords ??= new uint[15];
		byte[] occupant = _realEstateOccupant ??= new byte[128];
		occupant[0] = 0;
		uint known;
		try
		{
			known = RefiiHostGetRealEstate(words, (uint)words.Length,
				occupant, (uint)occupant.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		return DecodeRealEstate(known != 0u, words, occupant);
	}

	private static RealEstate DecodeRealEstate(bool known, uint[] words, byte[] occupant) =>
		new(
			known, words[0], words[1], words[2] != 0u, words[3] != 0u,
			(int)words[4], (int)words[5], (int)words[6], (int)words[7], (int)words[8],
			(int)words[9], (int)words[10], (int)words[11], (int)words[12], words[13],
			occupant[0] == 0 ? string.Empty : Utf8(occupant), words[14]);

	/// <summary>
	/// The hero's completed quests, UNSORTED. The page's order is descending by the GDB property
	/// `DisplayPriority`, which is a lookup for the caller to do.
	/// </summary>
	public static List<CompletedQuest> GetCompletedQuests()
	{
		var quests = new List<CompletedQuest>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return quests;
		}

		var conclusion = new byte[64];
		uint total;
		try
		{
			total = RefiiHostGetCompletedQuests(0u, out _, out _, out _, conclusion, 0u);
		}
		catch (EntryPointNotFoundException)
		{
			return quests;
		}

		for (uint index = 0; index < total; ++index)
		{
			RefiiHostGetCompletedQuests(
				index, out uint record, out int money, out int renown,
				conclusion, (uint)conclusion.Length);
			quests.Add(new CompletedQuest(record, money, renown, Utf8(conclusion)));
		}

		return quests;
	}

	/// <summary>
	/// The TopBox manager's front record — see the bridge export. <see cref="Present"/> is false
	/// with no record up; <see cref="Known"/> is false with no manager reachable.
	/// </summary>
	public readonly record struct TopBoxFront(
		bool Known, bool Present, uint TitleTag, bool ShowA, bool HoldA, bool ShowY, bool HoldY,
		int Channel, uint NameHash, int LifetimeMs);

	/// <summary>
	/// The front TopBox record's body provider, as data: <paramref name="Class"/> 0 Babel key,
	/// 1 key with %1/%2/%3 numbers, 2 key with %s/%1/%2 keys, 3 a literal; see the native.
	/// </summary>
	public readonly record struct TopBoxBody(
		bool Known, bool Present, int Class, uint Key, uint Second, uint Third, uint Fourth,
		uint TitleTag, uint NameHash, string Literal);



	/// <summary>
	/// PLAYER TWO'S targeted-citizen readout on split screen, in <see cref="TargetedQuery"/>'s
	/// shape: hero two's own focus target, the guest's gift reads, and the villager's opinion OF
	/// PLAYER TWO. Marry and divorce are hero one's questions in the guest and are never set.
	/// </summary>
	public static TargetedQuery GetPlayerTwoTargetedQuery()
	{
		uint[] words = _playerTwoQueryWords ??= new uint[12];
		uint count;
		try
		{
			count = RefiiHostGetPlayerTwoTargetedQuery(words, (uint)words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		if (count < 12u)
		{
			return default;
		}

		static float Bits(uint value) => BitConverter.Int32BitsToSingle(unchecked((int)value));
		return new TargetedQuery(
			true, words[0] != 0u, words[1], words[3] != 0u, Bits(words[4]), words[5] != 0u,
			unchecked((int)words[6]), words[7] != 0u, Bits(words[8]), words[9] != 0u,
			Bits(words[10]), Bits(words[11]));
	}

	/// <summary>Player two's split-screen HUD as the guest's own builders state it for hero two.</summary>
	/// <param name="Target">The entity the title names, 0 when none.</param>
	/// <param name="Title">The targeted readout's title (`sub_823C87B0`).</param>
	/// <param name="Icons">The d-pad's four icon paths: up, left, down, right; empty for none.</param>
	public readonly record struct PlayerTwoHud(uint Target, string Title, string[] Icons);

	public static PlayerTwoHud? GetPlayerTwoHud()
	{
		byte[] text = _playerTwoHudText ??= new byte[2048];
		uint target = 0u;
		try
		{
			if (RefiiHostGetPlayerTwoHud(ref target, text, (uint)text.Length) == 0u)
			{
				return null;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		int length = Array.IndexOf(text, (byte)0);
		string[] lines = System.Text.Encoding.UTF8.GetString(text, 0, length < 0 ? text.Length : length).Split('\n');
		if (lines.Length < 5)
		{
			return null;
		}

		return new PlayerTwoHud(target, lines[0], new[] { lines[1], lines[2], lines[3], lines[4] });
	}

	/// <summary>Reads the hero's derived state. See <see cref="HeroDerived"/>.</summary>
	public static HeroDerived GetHeroDerived()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		// Twelve words: the bridge fills slots 0..11 and clamps to the capacity given, so a
		// shorter buffer silently drops the trick count and the flags read below.
		var words = new uint[12];
		var values = new float[26];
		var title = new byte[64];
		var dog = new byte[64];
		var stats = new int[124];
		var tricks = new uint[32];
		int known;
		try
		{
			known = RefiiHostGetHeroDerived(
				words, (uint)words.Length, values, (uint)values.Length,
				title, (uint)title.Length, dog, (uint)dog.Length,
				stats, (uint)stats.Length, tricks, (uint)tricks.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		if (known == 0)
		{
			return default;
		}

		var attractiveness = new float[11];
		System.Array.Copy(values, 6, attractiveness, 0, 11);

		var spell = new float[6];
		System.Array.Copy(values, 20, spell, 0, 6);

		return new HeroDerived(
			true,
			values[0], values[1], values[2],
			values[3], values[4], values[5],
			unchecked((int)words[1]),
			Utf8(title),
			attractiveness, values[17],
			words[2] != 0u,
			unchecked((int)words[5]), unchecked((int)words[6]),
			unchecked((int)words[3]), Utf8(dog), words[9],
			Trim(tricks, words[7]),
			stats, words[8] != 0u,
			spell,
			words[10],
			words[11]);
	}

	/// <summary>Every live sale and shortage, in the guest's own order — sales first.</summary>
	public static List<Sale> GetSaleDetails()
	{
		var sales = new List<Sale>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return sales;
		}

		const int max = 32;
		const int stride = 5;
		uint[] words = new uint[max * stride];
		uint found;
		try
		{
			found = RefiiHostGetSales(words, (uint)words.Length);
		}
		catch (System.EntryPointNotFoundException)
		{
			return sales;
		}

		for (uint i = 0; i < found && i < max; ++i)
		{
			uint at = i * stride;
			sales.Add(new Sale(
				words[at], words[at + 1], words[at + 2], (int)words[at + 3],
				System.BitConverter.Int32BitsToSingle((int)words[at + 4])));
		}

		return sales;
	}

	public static List<(uint NameKey, bool IsShortage)> GetSales()
	{
		var sales = new List<(uint, bool)>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return sales;
		}

		const int max = 32;
		const int stride = 5;
		uint[] words = new uint[max * stride];
		uint found;
		try
		{
			found = RefiiHostGetSales(words, (uint)words.Length);
		}
		catch (System.DllNotFoundException)
		{
			return sales;
		}
		catch (System.EntryPointNotFoundException)
		{
			return sales;
		}

		for (uint i = 0; i < found && i < max; ++i)
		{
			float multiplier =
				System.BitConverter.Int32BitsToSingle((int)words[(i * stride) + 4]);
			sales.Add((words[i * stride], multiplier > 1f));
		}

		return sales;
	}

	public static List<(uint NameHash, uint Ordinal)> GetFastTravelRegions()
	{
		var regions = new List<(uint, uint)>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return regions;
		}

		const int max = 64;
		uint[] words = new uint[max * 2];
		uint found;
		try
		{
			found = RefiiHostGetFastTravelRegions(words, (uint)words.Length);
		}
		catch (System.DllNotFoundException)
		{
			return regions;
		}
		catch (System.EntryPointNotFoundException)
		{
			return regions;
		}

		for (uint i = 0; i < found && i < max; ++i)
		{
			regions.Add((words[i * 2], words[(i * 2) + 1]));
		}

		return regions;
	}

	/// <summary>One entry in the hero's quest log. `RecordId` is the quest DEFINITION's GDB key —
	/// the same currency the item lists use — and the flags are the guest's own state bytes.</summary>
	/// <summary>
	/// One quest as the world map's pages need it. Every number is a plain field on the quest
	/// object, named by the save serialiser `sub_825237C8`: `Rating` at +28 (the star strip's
	/// slide, written unclamped), `RewardRenown` +32, `RewardMoney` +36,
	/// `CurrentObjectiveParameter` +40 (the `%1` every task line substitutes) and
	/// `DaysRemaining` +44.
	///
	/// <paramref name="Primary"/> and <paramref name="Active"/> choose the banner the quest
	/// page opens with; <paramref name="TaskCount"/> is the length of the task vector at
	/// +104/+108, whose tags come across separately — see <see cref="GetQuestTasks"/>.
	/// </summary>
	public readonly record struct QuestEntry(
		uint RecordId, bool Unlocked, bool Completed, bool Failed, bool OnWorldMap, bool Viewed,
		bool Primary, bool Active,
		int Rating, int RewardRenown, int RewardMoney, int ObjectiveParameter,
		float DaysRemaining, int TaskCount,

		/// <summary>The entity the quest's objective weak-references (`quest+84`), or 0 — what
		/// the minimap plots as an "other quest" marker.</summary>
		uint ObjectiveEntity = 0);

	/// <summary>
	/// One job's journey — distance and travel time, already worded by the guest's own
	/// providers. <paramref name="Record"/> says which quest the answer is FOR: the pump replies
	/// a tick after the ask, so a page that has moved on must not draw the previous row's numbers.
	/// An empty <paramref name="Travel"/> is a real answer for a job under an hour away.
	/// </summary>
	public readonly record struct QuestJourney(
		bool Known, uint Record, string Distance, string Travel, string Region, string Prompt,
		string Days)
	{
		public bool IsFor(uint record) => Known && Record == record && record != 0u;
	}

	/// <summary>
	/// Whether the trail is following a sale or a family member instead of the primary quest —
	/// the quest log's TrailOverrideInfo at +40. While it is, the world map shows the primary
	/// quest as merely active (`sub_829A3260`). See RefiiHostGetQuestTrailOverride.
	/// </summary>
	public static bool QuestTrailOverridden()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiHostGetQuestTrailOverride() != 0u;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Names the quest whose journey the pump should answer for next.</summary>
	public static void SetQuestTravel(uint record)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiHostSetQuestTravel(record);
		}
		catch (System.EntryPointNotFoundException)
		{
		}
	}

	/// <summary>Reads back the journey — see <see cref="QuestJourney"/>.</summary>
	public static QuestJourney GetQuestTravel()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		var distance = new byte[64];
		var travel = new byte[64];
		var region = new byte[64];
		var prompt = new byte[64];
		var days = new byte[64];
		try
		{
			uint known = RefiiHostGetQuestTravel(
				out uint record, distance, (uint)distance.Length, travel, (uint)travel.Length,
				region, (uint)region.Length, prompt, (uint)prompt.Length, days, (uint)days.Length);
			return new QuestJourney(
				known != 0u, record, Utf8(distance), Utf8(travel), Utf8(region), Utf8(prompt),
			Utf8(days));
		}
		catch (System.EntryPointNotFoundException)
		{
			return default;
		}
	}

	/// <summary>
	/// One quest's task lines, as Babel TAGS in the guest's own order — the LAST is the current
	/// task and everything before it is completed. Position is what decides that; the
	/// `Succeeded` byte beside each tag is stored and never read by the page.
	/// </summary>
	public static List<string> GetQuestTasks(uint recordId)
	{
		var tasks = new List<string>();
		if (TryGetAbiVersion() is null || !IsGuestUp || recordId == 0u)
		{
			return tasks;
		}

		var packed = new byte[2048];
		try
		{
			if (RefiiHostGetQuestTasks(recordId, packed, (uint)packed.Length) == 0u)
			{
				return tasks;
			}
		}
		catch (System.EntryPointNotFoundException)
		{
			return tasks;
		}

		foreach (string tag in Utf8(packed).Split('\t'))
		{
			if (tag.Length != 0)
			{
				tasks.Add(tag);
			}
		}

		return tasks;
	}

	public static Furniture GetFurniture()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		uint[] words = _furnitureWords ??= new uint[15];
		try
		{
			if (RefiiHostGetFurniture(words, (uint)words.Length) == 0u)
			{
				return default;
			}
		}
		catch (System.EntryPointNotFoundException)
		{
			return default;
		}

		int count = (int)System.Math.Min(words[5], 8u);
		var values = new int[count];
		for (int i = 0; i < count; ++i)
		{
			values[i] = unchecked((int)words[7 + i]);
		}

		return new Furniture(
			true, words[1] != 0u, unchecked((int)words[2]), unchecked((int)words[3]),
			unchecked((int)words[4]), values,
			System.BitConverter.Int32BitsToSingle(unchecked((int)words[6])));
	}

	/// <summary>
	/// One quest's task lines' completed flags, index-parallel with <see cref="GetQuestTasks"/>
	/// (`quest+104` entries, byte +4). Empty when the quest has none or the guest is down.
	/// </summary>
	public static bool[] GetQuestTaskStates(uint recordId)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp || recordId == 0u)
		{
			return System.Array.Empty<bool>();
		}

		var completed = new byte[128];
		uint count;
		try
		{
			count = RefiiHostGetQuestTaskStates(recordId, completed, (uint)completed.Length);
		}
		catch (System.EntryPointNotFoundException)
		{
			return System.Array.Empty<bool>();
		}

		var states = new bool[System.Math.Min(count, (uint)completed.Length)];
		for (int i = 0; i < states.Length; ++i)
		{
			states[i] = completed[i] != 0;
		}

		return states;
	}

	/// <summary>
	/// The hero's quest log. The world map's Quests and Jobs are this one list filtered
	/// differently — the split is the definition's GDB `Type` (5 is a Job), which the caller
	/// resolves because the host has no GDB. Empty when the walk saw the container move.
	/// </summary>
	public static List<QuestEntry> GetQuestLog()
	{
		var quests = new List<QuestEntry>();
		GetQuestLog(quests);
		return quests;
	}

	/// <summary><see cref="GetQuestLog()"/> into a kept list, replacing what it held: a mod's minimap asks every frame.</summary>
	public static void GetQuestLog(List<QuestEntry> quests)
	{
		quests.Clear();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		const int max = 256;
		const int stride = 9;
		uint[] words = _questWords ??= new uint[max * stride];
		uint found;
		try
		{
			found = RefiiHostGetQuestLog(words, (uint)words.Length);
		}
		catch (System.DllNotFoundException)
		{
			return;
		}
		catch (System.EntryPointNotFoundException)
		{
			return;
		}

		for (uint i = 0; i < found && i < max; ++i)
		{
			uint at = i * stride;
			uint flags = words[at + 1];
			quests.Add(new QuestEntry(
				words[at],
				(flags & 1u) != 0u,
				(flags & 2u) != 0u,
				(flags & 4u) != 0u,
				(flags & 8u) != 0u,
				(flags & 16u) != 0u,
				(flags & 32u) != 0u,
				(flags & 64u) != 0u,
				(int)words[at + 2],
				(int)words[at + 3],
				(int)words[at + 4],
				(int)words[at + 5],
				System.BitConverter.Int32BitsToSingle((int)words[at + 6]),
				(int)words[at + 7],
				words[at + 8]));
		}
	}

	[System.ThreadStatic]
	private static uint[]? _questWords;

	/// <summary>
	/// The items the hero is WEARING, by GDB record id — what a clothing list must not offer
	/// again. Empty when nothing is worn, when the guest is down, and also when the walk saw the
	/// container move under it (a push reallocates); see the native side.
	/// </summary>
	/// <summary>
	/// One shop row's ASKING PRICE — what the shop charges, which is not the item's worth.
	/// </summary>
	/// <param name="Record">The item's GDB record — what the ticket names, read where the price reads it.</param>
	public readonly record struct ShopPrice(bool Known, uint Ticket, int Price, uint Record)
	{
		public bool IsFor(uint ticket) => Known && Ticket == ticket && ticket != 0u;
	}

	/// <summary>Names the row whose price the pump should answer for next.</summary>
	public static void SetShopPrice(uint ticket, bool sell)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiHostSetShopPrice(ticket, sell ? 1u : 0u);
		}
		catch (System.EntryPointNotFoundException)
		{
		}
	}

	/// <summary>Reads back the asking price — see <see cref="ShopPrice"/>.</summary>
	public static ShopPrice GetShopPrice()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		try
		{
			uint known = RefiiHostGetShopPrice(out uint ticket, out int price, out uint record);
			return new ShopPrice(known != 0u, ticket, price, record);
		}
		catch (System.EntryPointNotFoundException)
		{
			return default;
		}
	}

	/// <summary>
	/// ONE family member's page, in the guest's own field order.
	///
	/// Ten of the eleven are already-formatted text the collector wrote into the record —
	/// `sub_829B04E0` sets them with `sub_82425DD0`, which takes formatted text rather than a
	/// tag — so they are shown as they arrive. <see cref="Region"/> is the exception and IS a
	/// Babel tag, because the page sets it with `sub_82425F40`.
	/// </summary>
	public readonly record struct FamilyDetails(
		bool Known, string Name, string Children, string Region, string Distance, string Travel,
		string Note, string DailyUpkeep, string TotalUpkeep, string Happiness,
		string HomeHappiness, string UpkeepHappiness, string Prompt);

	/// <summary>
	/// The page for the family member at <paramref name="index"/> — the row's own id, which is
	/// the ordinal the guest's page indexes by.
	/// </summary>
	public static FamilyDetails GetFamilyDetails(uint index)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		const int fields = 12;
		const int stride = 192;
		byte[] block = new byte[fields * stride];
		uint found;
		try
		{
			found = RefiiHostGetFamilyDetails(index, block, stride, fields);
		}
		catch (System.DllNotFoundException)
		{
			return default;
		}
		catch (System.EntryPointNotFoundException)
		{
			return default;
		}

		if (found == 0u)
		{
			return default;
		}

		string Field(int at)
		{
			if (at >= found)
			{
				return string.Empty;
			}

			int start = at * stride;
			int end = System.Array.IndexOf(block, (byte)0, start, stride);
			int length = (end < 0 ? start + stride : end) - start;
			return length > 0 ? Encoding.UTF8.GetString(block, start, length) : string.Empty;
		}

		return new FamilyDetails(
			true, Field(0), Field(1), Field(2), Field(3), Field(4), Field(5), Field(6),
			Field(7), Field(8), Field(9), Field(10), Field(11));
	}

	/// <summary>
	/// The world map's family members, in the order the guest's own collector produced them.
	///
	/// Sampled by the pump while the world map is up (`sub_8246F020`, freed with
	/// `sub_826C9450`) rather than walked here — the read goes through an entity-UID map and a
	/// component-20 name fallback, and the guest already has the function for it.
	/// </summary>
	public static List<string> GetFamily()
	{
		var family = new List<string>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return family;
		}

		const int max = 32;
		const int stride = 64;
		byte[] names = new byte[max * stride];
		uint found;
		try
		{
			found = RefiiHostGetFamily(names, stride, max);
		}
		catch (System.DllNotFoundException)
		{
			return family;
		}
		catch (System.EntryPointNotFoundException)
		{
			return family;
		}

		for (uint i = 0; i < found && i < max; ++i)
		{
			int start = (int)i * stride;
			int end = System.Array.IndexOf(names, (byte)0, start, stride);
			int length = (end < 0 ? start + stride : end) - start;
			if (length > 0)
			{
				family.Add(Encoding.UTF8.GetString(names, start, length));
			}
		}

		return family;
	}

	/// <summary>
	/// One garment's dye state, by the guest's own test (<c>sub_826CAFA8</c>), and the item
	/// instance it was read off (<see cref="InventoryRecord.Item"/>).
	/// </summary>
	public readonly record struct DyeState(bool Slot1, bool Slot2, bool Worn, uint Item = 0);

	/// <summary>
	/// Which of the hero's garments are dyed, keyed by item record: for each record, the instance
	/// a dye press acts on — the worn one, else the first the inventory lists (the pump's
	/// `Inventory:dye` takes the worn target first, else `sub_824F1C90`'s find). The sampler
	/// reports per instance, worn first then the inventory in list order, so the first entry of
	/// a record is that one. Sampled on the guest's GUI tick, so it is current while a book is
	/// open; a record not listed has not been sampled.
	/// </summary>
	public static Dictionary<uint, DyeState> GetDyeStates()
	{
		var states = new Dictionary<uint, DyeState>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return states;
		}

		uint found;
		try
		{
			found = RefiiHostGetDyeStates(_dyeStateWords, (uint)_dyeStateWords.Length);
		}
		catch (System.EntryPointNotFoundException)
		{
			return states;
		}

		for (uint i = 0; i < found && (i * DyeStateWords) + 2 < _dyeStateWords.Length; ++i)
		{
			uint flags = _dyeStateWords[(i * DyeStateWords) + 2];
			states.TryAdd(_dyeStateWords[(i * DyeStateWords) + 1],
				new DyeState((flags & 1u) != 0, (flags & 2u) != 0, (flags & 4u) != 0,
					_dyeStateWords[i * DyeStateWords]));
		}

		return states;
	}

	/// <summary>
	/// The augment pairs socketed into one inventory record — (PrimaryType, SecondaryType) GDB
	/// record ids, secondary 0 when there is none — off the item's own instance record, which
	/// is what `sub_82469AE8` prints on the weapon page. Null when no record of that id carries
	/// one: the item was never seeded, and the definition's `InitialAugmentations` stand.
	/// </summary>
	public static (uint Primary, uint Secondary)[]? GetItemAugments(uint recordId)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp || recordId == 0u)
		{
			return null;
		}

		const int max = 16;
		uint[] words = new uint[max * 2];
		uint found;
		try
		{
			found = RefiiHostGetItemAugments(recordId, words, (uint)words.Length);
		}
		catch (System.DllNotFoundException)
		{
			return null;
		}
		catch (System.EntryPointNotFoundException)
		{
			return null;
		}

		if (found == uint.MaxValue)
		{
			return null;
		}

		var pairs = new (uint, uint)[System.Math.Min(found, max)];
		for (int i = 0; i < pairs.Length; ++i)
		{
			pairs[i] = (words[i * 2], words[(i * 2) + 1]);
		}

		return pairs;
	}

	public static List<InventoryRecord> GetHeroInventory()
	{
		var records = new List<InventoryRecord>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return records;
		}

		// Five words per record, the item instance last (RefiiHostGetHeroInventoryItems).
		const int stride = 5;
		uint count;
		try
		{
			count = RefiiHostGetHeroInventoryItems(null, 0);
		}
		catch (EntryPointNotFoundException)
		{
			return records;
		}

		if (count == 0)
		{
			return records;
		}

		// The snapshot is published whole on the guest's thread; headroom only for a publish
		// landing between the probe and the read.
		var words = new uint[(count + 32u) * stride];
		uint written = RefiiHostGetHeroInventoryItems(words, (uint)words.Length);

		int usable = (int)Math.Min(written, (uint)(words.Length / stride));
		for (int i = 0; i < usable; ++i)
		{
			int at = i * stride;
			records.Add(new InventoryRecord(
				words[at], (int)words[at + 1], (int)words[at + 2], words[at + 3] != 0u, words[at + 4]));
		}

		return records;
	}

	/// <summary>One worn item's dye slot: the colour the guest stores and the brightness.</summary>
	public readonly record struct SlotDye(byte Red, byte Green, byte Blue, float Brightness);

	/// <summary>
	/// One worn item's dye, off its own instance record (<c>RefiiHostGetEntityWornDyes</c>): the
	/// record hash and each slot the guest's own test (<c>sub_826CAFA8</c>) calls dyed; null for
	/// a slot that is not.
	/// </summary>
	public readonly record struct WornDye(uint Record, SlotDye? Primary, SlotDye? Secondary);

	/// <summary>What the entity wears, with each item's dye, in its worn order. Empty when unreadable.</summary>
	public static List<WornDye> GetEntityWornDyes(uint entity)
	{
		const int stride = 6;
		var result = new List<WornDye>();
		if (entity == 0u || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return result;
		}

		var words = new uint[64 * stride];
		uint count;
		try
		{
			count = RefiiHostGetEntityWornDyes(entity, words, (uint)words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return result;
		}

		// A colour word packs the guest's bytes from the low end: blue, green, red, alpha.
		static SlotDye Slot(uint colour, uint brightness) => new(
			(byte)(colour >> 16), (byte)(colour >> 8), (byte)colour,
			BitConverter.UInt32BitsToSingle(brightness));

		for (int i = 0; i < count && (i + 1) * stride <= words.Length; ++i)
		{
			int at = i * stride;
			uint flags = words[at + 1];
			result.Add(new WornDye(
				words[at],
				(flags & 1u) != 0u ? Slot(words[at + 2], words[at + 3]) : null,
				(flags & 2u) != 0u ? Slot(words[at + 4], words[at + 5]) : null));
		}

		return result;
	}

	/// <summary>One scar as <c>CECMorph</c> holds it: body-area mask, texture-morph record, permanence.</summary>
	public readonly record struct Scar(uint Area, uint Record, bool Permanent);

	/// <summary>The entity's scars (<c>RefiiHostGetEntityScars</c>), in the guest's order. Empty when it has none.</summary>
	public static List<Scar> GetEntityScars(uint entity)
	{
		var result = new List<Scar>();
		if (entity == 0u || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return result;
		}

		var words = new uint[64 * 3];
		uint count;
		try
		{
			count = RefiiHostGetEntityScars(entity, words, (uint)words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return result;
		}

		for (int i = 0; i < count && (i + 1) * 3 <= words.Length; ++i)
		{
			result.Add(new Scar(words[i * 3], words[(i * 3) + 1], words[(i * 3) + 2] != 0u));
		}

		return result;
	}

	/// <summary>
	/// The entity's own gender, as <c>CECGender</c> states it (<c>RefiiHostGetEntityGender</c>):
	/// true female, false male, null when the entity has no gender component.
	/// </summary>
	public static bool? GetEntityFemale(uint entity)
	{
		if (entity == 0u || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		try
		{
			return RefiiHostGetEntityGender(entity) switch
			{
				1u => true,
				2u => false,
				_ => null,
			};
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>
	/// Player 1's or 2's inventory, whoever's menus are open (<c>RefiiHostGetPlayerInventory</c>, the pump's
	/// per-player snapshot), with whether the hero wears each record. Empty for any other player.
	/// </summary>
	public static List<(InventoryRecord Record, bool Worn)> GetPlayerInventory(int player)
	{
		var records = new List<(InventoryRecord, bool)>();
		if (player is not (1 or 2) || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return records;
		}

		const int stride = 4;
		uint count;
		try
		{
			count = RefiiHostGetPlayerInventory(player, null, 0);
		}
		catch (EntryPointNotFoundException)
		{
			return records;
		}

		if (count == 0)
		{
			return records;
		}

		var words = new uint[(count + 32u) * stride];
		uint written = RefiiHostGetPlayerInventory(player, words, (uint)words.Length);
		int usable = (int)Math.Min(written, (uint)(words.Length / stride));
		for (int i = 0; i < usable; ++i)
		{
			int at = i * stride;
			records.Add((new InventoryRecord(words[at], (int)words[at + 1], (int)words[at + 2], (words[at + 3] & 1u) != 0u),
				(words[at + 3] & 2u) != 0u));
		}

		return records;
	}

	/// <summary>The shop's front page as its writer computes it — see <c>ShopFront</c> in hero_derived.h.</summary>
	public readonly record struct ShopFront(
		bool Known, bool Trader, bool Crier, bool HasKeeperName, bool Owner, bool SaleKnown,
		float Stars, float Wealth, float OwnerDiscount, float Love, float Hate, float Fear,
		float Special, float SaleFactor, int SaleDays,
		string NameKey, string AnecdoteKey, string KeeperName, string TitleKey);

	public static ShopFront GetShopFront()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		uint[] w = _shopFrontWords ??= new uint[11];
		byte[] text = _shopFrontText ??= new byte[512];
		System.Array.Clear(text);
		try
		{
			if (RefiiHostGetShopFront(w, (uint)w.Length, text, (uint)text.Length) == 0u)
			{
				return default;
			}
		}
		catch (System.EntryPointNotFoundException)
		{
			return default;
		}

		var pieces = new string[4];
		int at = 0;
		for (int i = 0; i < 4; ++i)
		{
			int end = System.Array.IndexOf(text, (byte)0, at);
			if (end < 0)
			{
				end = text.Length;
			}

			pieces[i] = Encoding.UTF8.GetString(text, at, end - at);
			at = System.Math.Min(end + 1, text.Length);
		}

		static float F(uint bits) => System.BitConverter.Int32BitsToSingle(unchecked((int)bits));
		uint flags = w[1];
		return new ShopFront(
			true, (flags & 1u) != 0u, (flags & 2u) != 0u, (flags & 4u) != 0u, (flags & 8u) != 0u,
			(flags & 16u) != 0u, F(w[2]), F(w[3]), F(w[4]), F(w[5]), F(w[6]), F(w[7]), F(w[8]),
			F(w[9]), unchecked((int)w[10]), pieces[0], pieces[1], pieces[2], pieces[3]);
	}

	/// <summary>The co-op screen's decision state (screen 24) — see <c>CoopScreen</c> in hero_derived.h.
	/// Words 11..13 (the pickboxes' indices) are not read: each caption is stated by its SetIndex
	/// (<see cref="Engine_UiPickBoxes"/>).</summary>
	public readonly record struct CoopPageState(
		bool Known, int Session, bool OnlineClient, bool RightSignedIn,
		bool PeerReady, bool Flag297, bool Flag298,
		bool Child, bool ChildLevel, int UserRightSave, bool SaveChosen,
		bool[] PickEnabled, int[] PickState, int ProfileLeft, int ProfileRight);

	public static CoopPageState GetCoopState()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		uint[] w = _coopWords ??= new uint[20];
		try
		{
			if (RefiiHostGetCoopState(w, (uint)w.Length) == 0u)
			{
				return default;
			}
		}
		catch (System.EntryPointNotFoundException)
		{
			return default;
		}

		return new CoopPageState(
			true, unchecked((int)w[1]), w[2] != 0u, w[3] != 0u, w[4] != 0u,
			w[5] != 0u, w[6] != 0u, w[7] != 0u, w[8] != 0u, unchecked((int)w[9]), w[10] != 0u,
			new[] { (w[14] & 1u) != 0u, (w[14] & 2u) != 0u, (w[14] & 4u) != 0u },
			new[] { unchecked((int)w[15]), unchecked((int)w[16]), unchecked((int)w[17]) },
			unchecked((int)w[18]), unchecked((int)w[19]));
	}

	/// <summary>
	/// What the guest's pub-game manager currently says. §the pub games are the guest's, not ours.
	/// </summary>
	/// <remarks>
	/// The guest drives the whole lifecycle — which game, which variation, which pad and
	/// language, seeding the bank from the hero's gold, and settling it afterwards. Godot claims
	/// the games (<see cref="ClaimPubGames"/>), which takes the Scaleform module out of the
	/// guest's pump (<c>Fable2/src/vfs/src/hooks/pubgame_pump.cpp</c>): the guest still walks 1 → 2 → 3,
	/// Godot's game plays while it sits in 3, and the game's own fscommands — posted through
	/// <see cref="PostPubGameCommand"/> — carry it to 6 and the settle at 9.
	/// </remarks>
	public readonly record struct PubGameState(
		uint Stage, uint Profile, long Gold, long Debt, string Game, string Variation)
	{
		/// <summary>The session is up and nobody is drawing it yet.</summary>
		public bool WantsHost => Stage == RunningStage && Profile != 0;

		/// <summary>The state the session plays in; the pump enters it from 2 once per session.</summary>
		public const uint RunningStage = 3;

		/// <summary>
		/// True while a session is live and the host should keep drawing.
		/// </summary>
		/// <remarks>
		/// 3 is play; 4 and 5 are the save a holdings update triggers (the tick saves in 4, the
		/// pump hands back EndSave in 5); 7 and 8 are the guest ending the session itself. 2 comes
		/// before the board exists and 6 and 9 after it is gone.
		/// </remarks>
		public bool IsLive => Stage is 3 or 4 or 5 or 7 or 8;
	}

	/// <summary>What Godot's game says, in the guest's fscommand handler's own terms.</summary>
	/// <remarks>Each is one branch of <c>sub_82EF2AC0</c>; see <c>pubgame_pump.cpp</c>.</remarks>
	public enum PubGameCommand : uint
	{
		PlaySound = 1,
		PauseSound = 2,
		Holdings = 3,
		Points = 4,
		Rank = 5,
		SoundVolume = 6,
		MusicVolume = 7,
		GameOver = 8,
	}

	/// <summary>What the pump would have told the movie.</summary>
	[System.Flags]
	public enum PubGameEvent : uint
	{
		None = 0,

		/// <summary>2 → 3: the session is up.</summary>
		Running = 1,

		/// <summary>State 5: the save a holdings update started has finished — <c>pubGame.EndSave</c>.</summary>
		SaveEnded = 2,

		/// <summary>A holdings update arrived outside state 3, so no save follows it.</summary>
		SaveSkipped = 4,

		/// <summary>State 7: the guest is ending the session — <c>pubGame.EndGame</c>.</summary>
		EndGame = 8,
	}

	/// <summary>
	/// What <c>sub_82EF18A0</c> hands the movie before <c>pubGame.Run</c>.
	/// </summary>
	/// <remarks>
	/// <see cref="Points"/> is the profile's three per-game slots; the game reads the one its
	/// settings name as <c>gameIndex</c>, and the total is the sum of all three
	/// (<c>sub_82EE8710</c>). <see cref="SoundVolume"/> and <see cref="MusicVolume"/> are 0..1 —
	/// the movie is handed <c>(int)(v * 10)</c>. <see cref="Items"/> bit N means
	/// <c>pubGame.TournamentItemWon(N)</c>.
	/// </remarks>
	public readonly record struct PubGameProfile(
		string Name, long Rank, long[] Points, uint Items, uint Pad,
		float SoundVolume, float MusicVolume, long StartingGold, long StartingDebt, string Language)
	{
		public int TotalPoints => (int)(Points[0] + Points[1] + Points[2]);
	}

	/// <summary>Reads the manager, or null when the guest is not up.</summary>
	[System.ThreadStatic]
	private static uint[]? _pubGameValues;

	[System.ThreadStatic]
	private static byte[]? _pubGameName, _pubGameVariation;

	public static PubGameState? GetPubGame()
	{
		if (!IsGuestUp)
		{
			return null;
		}

		// Polled every frame by the Android host: the buffers are kept per thread.
		uint[] values = _pubGameValues ??= new uint[6];
		byte[] name = _pubGameName ??= new byte[64];
		byte[] variation = _pubGameVariation ??= new byte[64];

		try
		{
			if (RefiiHostGetPubGame(
				values, name, (uint)name.Length, variation, (uint)variation.Length) == 0)
			{
				return null;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		// High word first: the guest is big endian and these are int64.
		long gold = ((long)values[2] << 32) | values[3];
		long debt = ((long)values[4] << 32) | values[5];

		return new PubGameState(
			values[0], values[1], gold, debt, HostBridge.Ascii(name), HostBridge.Ascii(variation));
	}

	/// <summary>Reads what the movie would be handed at start, or null when there is no profile.</summary>
	public static PubGameProfile? GetPubGameProfile()
	{
		if (!IsGuestUp)
		{
			return null;
		}

		var values = new uint[14];
		var name = new byte[256];
		var language = new byte[4];
		try
		{
			if (RefiiHostGetPubGameProfile(
				values, name, (uint)name.Length, language, (uint)language.Length) == 0)
			{
				return null;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		static long Wide(uint high, uint low) => ((long)high << 32) | low;

		int nameLength = System.Array.IndexOf(name, (byte)0);
		return new PubGameProfile(
			nameLength <= 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(name, 0, nameLength),
			Wide(values[0], values[1]),
			[Wide(values[2], values[3]), Wide(values[4], values[5]), Wide(values[6], values[7])],
			values[8],
			values[9],
			BitConverter.UInt32BitsToSingle(values[10]),
			BitConverter.UInt32BitsToSingle(values[11]),
			(int)values[12],
			(int)values[13],
			HostBridge.Ascii(language));
	}

	/// <summary>
	/// Tells the guest Godot plays the pub games, so its pump never creates the Scaleform movie.
	/// </summary>
	public static bool ClaimPubGames(bool claimed)
	{
		if (!IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiHostClaimPubGames(claimed ? 1 : 0) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Hands the guest one fscommand from Godot's game.</summary>
	public static void PostPubGameCommand(
		PubGameCommand kind, string? text = null,
		double n0 = 0, double n1 = 0, double n2 = 0, long i0 = 0, long i1 = 0)
	{
		if (!IsGuestUp)
		{
			return;
		}

		try
		{
			RefiiHostPostPubGameCommand((uint)kind, text ?? string.Empty, n0, n1, n2, i0, i1);
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>What the pump told the movie since the last call.</summary>
	public static PubGameEvent TakePubGameEvents()
	{
		if (!IsGuestUp)
		{
			return PubGameEvent.None;
		}

		try
		{
			return (PubGameEvent)RefiiHostTakePubGameEvents();
		}
		catch (EntryPointNotFoundException)
		{
			return PubGameEvent.None;
		}
	}

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern ulong RefiiHostGetNewGameState(out uint generation);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern uint RefiiHostGetGoldenCockInteraction();


	[DllImport(Library)]
	private static extern int RefiiHostGetMenuOwner();

	[DllImport(Library)]
	private static extern int RefiiHostGetExpressionWheelPlayer();

	[DllImport(Library)]
	private static extern int RefiiHostTakeExitRefused();

	[DllImport(Library)]
	private static extern int RefiiHostGetPlayerTwoPrompts(uint[] outThings, int capacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetPlayerOneHiddenPrompts(uint[] outThings, int capacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetPlayerTwoHold(out float fraction, out int pressed);

	[DllImport(Library)]
	private static extern uint RefiiHostGetProperties(int player, [Out] byte[] text, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostTakePropertyNotice(out int kind, out ulong uid);

	[DllImport(Library)]
	private static extern int RefiiHostGetPartnership(out int kind);

	[DllImport(Library)]
	private static extern int RefiiHostGetItemCount(int player, uint definition);

	[DllImport(Library)]
	private static extern uint RefiiHostTakeEstateIntent(byte[] outBuffer, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostSetPartnership(int kind);

	[DllImport(Library)]
	private static extern int RefiiHostGetOpinionOfUid(ulong uid, int player, [Out] float[] axes);

	[DllImport(Library)]
	private static extern int RefiiHostGetExitWaiting();

	[DllImport(Library)]
	private static extern uint RefiiHostGetExitRequest(float[] outXyz);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopRole();

	[DllImport(Library)]
	private static extern int RefiiHostGetCoopShares(out float gold, out float experience);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopHeroSaveName(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopSession(uint[] outWords, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopHeroSave([Out] float[] outValues, uint valueCount);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHeroLooks(out uint face, out float fat, out float height, out float tan);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHeroWeapons(out uint melee, out uint ranged);


	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopHeldUids(uint player, out ulong melee, out ulong ranged);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHeroStateApplied(uint player);



	[DllImport(Library)]
	private static extern uint RefiiHostGetPlayerTwoTargetedQuery([Out] uint[] words, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetPlayerTwoHud(ref uint target, [Out] byte[] text, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetDyeStates([Out] uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFamily([Out] byte[]? outNames, uint stride, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFamilyDetails(
		uint index, [Out] byte[] outFields, uint stride, uint count);

	[DllImport(Library)]
	private static extern void RefiiHostSetShopPrice(uint ticket, uint sell);

	[DllImport(Library)]
	private static extern uint RefiiHostGetShopPrice(out uint outTicket, out int outPrice, out uint outRecord);

	[DllImport(Library)]
	private static extern uint RefiiHostGetQuestLog(uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetQuestTrailOverride();

	[DllImport(Library)]
	private static extern uint RefiiHostGetFastTravelRegions([Out] uint[]? outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetSales(uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostPauseTabsAvailable();


	[DllImport(Library)]
	private static extern uint RefiiHostGetMinimapLive(uint[] outWords, uint capacity);

	[DllImport(Library)]
	private static extern void RefiiHostSetWorldMapRegion(uint index);

	[DllImport(Library)]
	private static extern uint RefiiHostGetQuestTasks(uint recordId, byte[] packed, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetQuestTaskStates(uint recordId, byte[] completed, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFurniture(uint[] words, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopState(uint[] words, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetShopFront(uint[] words, uint capacity, byte[] text, uint textCapacity);

	[DllImport(Library)]
	private static extern void RefiiHostSetQuestTravel(uint record);

	[DllImport(Library)]
	private static extern uint RefiiHostGetQuestTravel(
		out uint outRecord, byte[] distance, uint distanceCapacity, byte[] travel,
		uint travelCapacity, byte[] region, uint regionCapacity, byte[] prompt,
		uint promptCapacity, byte[] days, uint daysCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetRealEstate(
		uint[] words, uint wordCapacity, byte[] occupant, uint occupantCapacity);

	[DllImport(Library)]
	private static extern void RefiiHostRequestPropertyEstate(ulong uid);

	[DllImport(Library)]
	private static extern uint RefiiHostGetPropertyEstate(
		ulong uid, uint[] words, uint wordCapacity, byte[] occupant, uint occupantCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCompletedQuests(
		uint index, out uint outRecord, out int outMoney, out int outRenown,
		byte[] outConclusion, uint conclusionCapacity);

	[DllImport(Library)]
	private static extern int RefiiHostConsentTake(out uint ticket, out int kind, out int asker, out int detail,
		[Out] byte[] text, uint capacity);

	[DllImport(Library)]
	private static extern void RefiiHostConsentGive(uint ticket, int yes);

	[DllImport(Library)]
	private static extern int RefiiHostGetHeroDerived(
		uint[] words, uint wordCapacity, float[] values, uint valueCapacity,
		byte[] titleTag, uint titleCapacity, byte[] dogName, uint dogCapacity,
		int[] stats, uint statsCapacity, uint[] trickIds, uint trickCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHeroInventoryItems(uint[]? outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityWornDyes(uint entity, [Out] uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityScars(uint entity, [Out] uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityGender(uint entity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetPlayerInventory(int player, uint[]? outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetItemAugments(uint recordId, uint[] outWords, uint wordCapacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int RefiiHostGetPubGame(
		[Out] uint[] values,
		[Out] byte[] name,
		uint nameCapacity,
		[Out] byte[] variation,
		uint variationCapacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int RefiiHostGetPubGameProfile(
		[Out] uint[] values,
		[Out] byte[] name,
		uint nameCapacity,
		[Out] byte[] language,
		uint languageCapacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int RefiiHostClaimPubGames(int claimed);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int RefiiHostPostPubGameCommand(
		uint kind,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string text,
		double n0, double n1, double n2, long i0, long i1);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern uint RefiiHostTakePubGameEvents();


	[DllImport(Library)]
	private static extern uint RefiiHostGetItemDyeLayers(
		uint item, [Out] uint[] outWords, uint wordCapacity, out uint generation);

	/// <summary>
	/// The building entity the guest's own per-frame query finds the HERO standing in, 0 when it
	/// finds none. Null when the guest is not up, the hero is not built, or the guest did not
	/// ask this frame — hold what was last read.
	/// </summary>
	/// <remarks>
	/// The query is `sub_828330C8` (through `sub_821D8508`): a ray 10.5 units down from the
	/// entity, kept when it lands on an entity whose byte +144 has 0x40. `sub_821AA080` runs it
	/// on the hero to scale the light shafts by `flt_833191F8` indoors. Its answer is cached on
	/// the hero's component 128 — a weak reference at +12 (node pointer at +16) stamped at +20
	/// with the frame counter `dword_83319650` — and that cached answer is what is read here.
	/// Hero: `World = *(*(*(*(0x83496AB8)+12)+88)+4)`, node `*(*(World)+108)`, entity `*node`;
	/// component: entity+52 bit 0, slot byte at `*(entity+140)+128`, pointer at
	/// `*(entity+72) + slot*8 + 4` — all as `sub_821AA080` walks them.
	/// </remarks>
	public static uint? HeroBuilding()
	{
		static uint Word(uint at) => at >= 0x10000u && TryPeekWord(at, out uint w) ? w : 0u;

		uint root = Word(GuestAddress(0x83496AB8u));
		uint world = Word(Word(Word(root + 12u) + 88u) + 4u);
		uint hero = Word(Word(Word(world) + 108u));
		return root == 0 || world == 0 ? null : BuildingOf(hero);
	}

	/// <summary>
	/// <see cref="HeroBuilding"/> for player two: the same cached answer on player two's own
	/// component 128, which the guest fills for either player (`sub_821CC460`, gated by
	/// `sub_82232778` on P+104 and P+112, queries the owner's component 128 through
	/// `sub_821D8508`). Null without a player two or an answer this frame.
	/// </summary>
	public static uint? PlayerTwoBuilding()
	{
		(_, uint two) = CoopPlayerEntities();
		return two == 0 ? null : BuildingOf(two);
	}

	/// <summary>The building cached on <paramref name="hero"/>'s component 128 within the last two frames.</summary>
	private static uint? BuildingOf(uint hero)
	{
		static uint Word(uint at) => at >= 0x10000u && TryPeekWord(at, out uint w) ? w : 0u;

		if (hero == 0 || (Word(hero + 52u) & 1u) == 0)
		{
			return null;
		}

		uint slots = Word(hero + 140u);
		uint table = Word(hero + 72u);
		if (slots == 0 || table == 0)
		{
			return null;
		}

		uint component = Word(table + ((Word(slots + 128u) >> 24) * 8u) + 4u);
		if (component == 0)
		{
			return null;
		}

		Span<uint> cache = stackalloc uint[2];
		int cacheCount = Peek(component + 16u, cache);
		uint frame = Word(GuestAddress(0x83319650u));
		if (cacheCount != 2 || frame - cache[1] > 2u)
		{
			return null;
		}

		return cache[0] != 0 ? Word(cache[0]) : 0u;
	}

	/// <summary>
	/// The guest's stated blur width for the targeting highlight, in texels of its
	/// half-resolution highlight buffer. 0 when the guest is not up or the word is unreadable.
	/// </summary>
	/// <remarks>
	/// `dword_83318558` is the EXECUTE result of the thickness command and is already clamped:
	/// `sub_822BF690` truncates `flt_83319478 * flt_83496C70` with `fctiwz f13, f0`, then applies
	/// max(x, 1) (`subf r10, r7, r11`) and min(x, 16) (`add r11, r11, r10`). It has exactly two
	/// xrefs — that store at 0x822bf6c8 and the read at 0x82197c08 — so it is the whole of the
	/// guest's width contract, post-clamp. An absolute global, not a struct offset.
	///
	/// Peeked rather than exported through the ABI: one word at one fixed address. MAIN THREAD ONLY — the
	/// post chain runs on the render thread, so the layer samples this in `_Process` and
	/// publishes the result.
	/// </remarks>
	public static int HighlightWidthTexels()
	{
		return TryPeekWord(GuestAddress(0x83318558u), out uint word)
			? (int)Math.Clamp(word, 1u, 16u)
			: 0;
	}
}
