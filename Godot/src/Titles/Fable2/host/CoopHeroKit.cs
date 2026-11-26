using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Host;

/// <summary>
/// Player two's own hero, as a thing that can cross the wire.
/// </summary>
/// <remarks>
/// THE LIVE HERO IS THE SOURCE, NOT THE SAVE FILE. The guest reads a save through its own
/// content path and hands us nothing on the way past; every field below is already exported off
/// the hero's live components, so capturing from the running character costs no save parsing and
/// no compatibility risk. `docs/coop_native_hero_plan.md` weighs both routes and lands here.
///
/// CAPTURE IS CONTINUOUS AND CACHED, because the moment that matters has already gone by the
/// time it is needed: joining tears the joiner's own world down, so their hero is not there to
/// read when the co-op screen asks for it. <see cref="Sample"/> keeps the last good capture
/// instead — whatever they were last playing.
///
/// The wire form and the pump's form are both the HeroState record: <see cref="CoopHeroState"/>
/// serializes a <see cref="Kit"/>, versions it, fragments it and acknowledges it.
/// </remarks>
public static class CoopHeroKit
{
	/// <summary>Whether a kit carries a body — a save-picked hero states none (strength −1).</summary>
	public static bool HasLooks(in Kit kit) => kit.Known && kit.Strength >= 0f;

	/// <summary>Ability ids 1..14, the range <c>HeroVitals.AbilityLevels</c> carries.</summary>
	private const int AbilityCount = 15;

	/// <summary>
	/// Everything about a hero that we can both read and re-apply. Weapons are GDB record ids.
	/// <see cref="WornDyes"/> is the dye on each worn item that holds one — the dye is the item
	/// instance's own record, which a worn def hash alone does not carry — and <see cref="Scars"/>
	/// the hero's `CECMorph` scars; both cross as the HeroState's looks extension.
	/// </summary>
	public readonly record struct Kit(
		bool Known,
		bool Female,
		float Morality,
		float Purity,
		int Renown,
		float Strength,
		float Skill,
		float Will,
		int[] AbilityLevels,
		uint[] Worn,
		(uint Record, int Count)[] Inventory,
		string Name,
		int Money,
		float[] Experience,
		float[] TotalExperience,
		uint Face,
		float Fat,
		float Height,
		float Tan,
		uint MeleeId = 0u,
		uint RangedId = 0u,
		uint DogDefinition = 0u,
		int DogSearching = 0,
		int DogCombat = 0,
		string[]? DogTricks = null,
		uint[]? DogWorn = null,
		string? DogName = null,
		Fable2Bridge.WornDye[]? WornDyes = null,
		Fable2Bridge.Scar[]? Scars = null)
	{
		/// <summary>The creature this hero's body is, in the guest's own naming.</summary>
		public string Creature => Female ? "CreatureHeroFemale" : "CreatureHero";
	}

	/// <summary>
	/// GDB record id → def name, supplied by the shell, for the logs. Null until the shell sets
	/// it, or when the instance has no GDB view (a joiner on the front end).
	/// </summary>
	public static Func<uint, string>? RecordName { get; set; }

	/// <summary>
	/// This instance's display name, off the node — independent of whether a hero was ever
	/// captured. A joiner that comes in from the title has a name and no hero. With no display
	/// name set it is the pseudonym, by RexNet's rule as every other name on screen; the friend
	/// code it fell back to titled each player's own co-op page with ~70 characters of REXN-….
	/// </summary>
	public static string LocalName =>
		NetBridge.Self() is { } self
			? (self.DisplayName.Length != 0 ? self.DisplayName
				: NetBridge.PeerName(self.PeerId) is { Length: > 0 } pseudonym ? pseudonym
				: "…" + self.PeerId[^Math.Min(8, self.PeerId.Length)..])
			: string.Empty;

	/// <summary>The last hero seen alive on this instance, or an unknown kit.</summary>
	public static Kit Local { get; private set; }

	/// <summary>What the peer last stated about theirs — set by <see cref="CoopHeroState.Receive"/>.</summary>
	public static Kit Remote { get; internal set; }

	/// <summary>
	/// Takes a capture if a hero is up. Cheap enough to call on a timer; does nothing and keeps
	/// the previous capture when no hero is loaded, which is every moment in the front end.
	/// </summary>
	/// <param name="appearanceOnly">
	/// Keep the standing kit's stats (money, experience, ranks, morality…) and take only what the
	/// body shows and carries — outfit, weapons, inventory, looks. The joiner's copy of its own
	/// hero is not the authority for its stats once the host's world owns it, and its money read
	/// there came back as 32768. The weapons and the inventory ARE taken live: a change of sword
	/// or coat on the joiner is stated here or nowhere, and the host's copy only ever re-equips
	/// from this kit. An empty weapon slot is never taken — the joiner's own guest drops its
	/// weapons on a client until the host's replicas are attached, and a kit sampled in that gap
	/// would unarm player two on the host.
	/// </param>
	public static void Sample(bool appearanceOnly = false)
	{
		uint entity = HostBridge.HeroEntityAddress();
		if (entity == 0u)
		{
			return;
		}

		Kit before = Local;

		HostBridge.HeroVitals vitals = HostBridge.GetHeroVitals();
		if (!vitals.Known)
		{
			return;
		}

		List<HostBridge.GuestWornItem> worn = HostBridge.GetEntityWornItems(entity);

		// The hero's OWN gender, `CECGender` +12 — not the worn walk's per-item flag, which a hero
		// wearing nothing does not have. The flag is the same read, so it stands in only for a
		// bridge without the gender export.
		bool female = Fable2Bridge.GetEntityFemale(entity) ?? (worn.Count > 0 && worn[0].Female);

		// The dye on each worn item, off the instance it lives on, and the scars.
		Fable2Bridge.WornDye[] dyes = Fable2Bridge.GetEntityWornDyes(entity)
			.FindAll(dye => dye.Primary is not null || dye.Secondary is not null).ToArray();
		Fable2Bridge.Scar[] scars = Fable2Bridge.GetEntityScars(entity).ToArray();

		var hashes = new uint[worn.Count];
		for (int i = 0; i < worn.Count; ++i)
		{
			hashes[i] = worn[i].Hash;
		}

		var abilities = new int[AbilityCount];
		int[]? levels = vitals.AbilityLevels;
		if (levels is not null)
		{
			for (int id = 0; id < AbilityCount && id < levels.Length; ++id)
			{
				abilities[id] = levels[id];
			}
		}

		// THE WEAPONS BY RECORD ID, off the two equipped slots.
		uint meleeId = 0u;
		uint rangedId = 0u;
		if (Fable2Bridge.HeroWeapons() is { } slots)
		{
			meleeId = slots.Melee;
			rangedId = slots.Ranged;

			// A PENDING PICK OUTRANKS THE LIVE SLOT until the slot reports it: the live slot
			// keeps the old weapon until the host's equip and the replica land, and a sample in
			// that gap would restate the old record and undo the pick.
			if (_pendingMelee != 0u)
			{
				if (meleeId == _pendingMelee) _pendingMelee = 0u;
				else meleeId = _pendingMelee;
			}

			if (_pendingRanged != 0u)
			{
				if (rangedId == _pendingRanged) _pendingRanged = 0u;
				else rangedId = _pendingRanged;
			}
		}

		// THE WHOLE INVENTORY, as record id and count — the currency the giver keys on. A
		// record id repeats when the first still holds instance data, so counts are summed.
		var counts = new Dictionary<uint, int>();
		foreach (Fable2Bridge.InventoryRecord record in Fable2Bridge.GetHeroInventory())
		{
			counts[record.RecordId] = counts.GetValueOrDefault(record.RecordId) + Math.Max(1, record.Count);
		}

		var inventory = new (uint, int)[counts.Count];
		int at = 0;
		foreach (KeyValuePair<uint, int> pair in counts)
		{
			inventory[at++] = (pair.Key, pair.Value);
		}

		// THE REST OF THE HERO. Money and the two experience pools are already in the vitals;
		// the face record and the three body scalars come off the looks export.
		float[] pools = new float[4];
		float[] totals = new float[4];
		for (int i = 0; i < 4; ++i)
		{
			pools[i] = vitals.ExperienceAvailable is { Length: > 0 } avail && i < avail.Length ? avail[i] : 0f;
			totals[i] = vitals.ExperienceEarned is { Length: > 0 } earned && i < earned.Length ? earned[i] : 0f;
		}

		(uint Face, float Fat, float Height, float Tan) looks = Fable2Bridge.HeroLooks() ?? (0u, 0f, 0f, 0f);

		bool keep = appearanceOnly && before.Known;

		// THE DOG, as Fable 3 carries the visitor's own into the host's world: its breed (the
		// definition), its two levels and its tricks BY NAME — the trick store holds record ids,
		// and only this instance's GDB view names them. Taken with the stats: once the joiner is
		// in the host's world, the dog it reads is the host's.
		HostBridge.HeroDerived derived = Fable2Bridge.GetHeroDerived();
		uint dogDefinition = 0u;
		int dogSearching = 0, dogCombat = 0;
		string[]? dogTricks = null;
		uint[]? dogWorn = null;
		string? dogName = null;
		if (derived.Known && derived.DogKnown)
		{
			dogDefinition = derived.DogDefinition;
			// Its collar (the worn key item) and the name the player gave it, if any.
			List<HostBridge.GuestWornItem> dogItems = HostBridge.GetEntityWornItems(derived.DogEntity);
			dogWorn = new uint[dogItems.Count];
			for (int i = 0; i < dogItems.Count; ++i)
			{
				dogWorn[i] = dogItems[i].Hash;
			}

			dogName = derived.DogName;
			dogSearching = derived.DogSearching;
			dogCombat = derived.DogCombat;
			var names = new List<string>();
			foreach (uint id in derived.DogTrickIds ?? Array.Empty<uint>())
			{
				if (RecordName?.Invoke(id) is { Length: > 0 } trick)
				{
					names.Add(trick);
				}
			}

			dogTricks = names.ToArray();
		}

		// An empty worn walk or inventory read on a standing hero is a read that missed (the
		// body mid-rebuild, the component not yet populated), not a hero that owns nothing:
		// the previous capture stands until a non-empty one replaces it.
		if (keep && hashes.Length == 0 && before.Worn is { Length: > 0 })
		{
			hashes = before.Worn;
		}

		if (keep && inventory.Length == 0 && before.Inventory is { Length: > 0 })
		{
			inventory = before.Inventory;
		}

		Local = new Kit(
			Known: true,
			Female: keep ? before.Female : female,
			Morality: keep ? before.Morality : vitals.MoralityBase,
			Purity: keep ? before.Purity : vitals.PurityBase,
			Renown: keep ? before.Renown : vitals.RenownBase,
			Strength: keep ? before.Strength : vitals.Strength,
			Skill: keep ? before.Skill : vitals.Skill,
			Will: keep ? before.Will : vitals.Will,
			AbilityLevels: keep ? before.AbilityLevels : abilities,
			Worn: hashes,
			Inventory: inventory,
			// NO NAME TRAVELS: the kit crosses the co-op channel to whoever the partner is, and a
			// display name goes only to friends (RexNet §17.3.8). The partner names us from its own
			// RexNet view (NetBridge.PartnerName): our name if we are friends, our pseudonym if not.
			Name: string.Empty,
			Money: keep ? before.Money : vitals.Gold,
			Experience: keep ? before.Experience : pools,
			TotalExperience: keep ? before.TotalExperience : totals,
			Face: keep && looks.Face == 0u ? before.Face : looks.Face,
			Fat: keep ? before.Fat : looks.Fat,
			Height: keep ? before.Height : looks.Height,
			Tan: keep ? before.Tan : looks.Tan,
			// Appearance is the outfit, the weapons and the inventory; an empty slot keeps the
			// record's weapon (see the parameter note).
			MeleeId: keep && meleeId == 0u ? before.MeleeId : meleeId,
			RangedId: keep && rangedId == 0u ? before.RangedId : rangedId,
			DogDefinition: keep ? before.DogDefinition : dogDefinition,
			DogSearching: keep ? before.DogSearching : dogSearching,
			DogCombat: keep ? before.DogCombat : dogCombat,
			DogTricks: keep ? before.DogTricks : dogTricks,
			DogWorn: keep ? before.DogWorn : dogWorn,
			DogName: keep ? before.DogName : dogName,
			// What the body shows: taken live, as the outfit is. A torn worn walk reads no dyes,
			// so the previous dyes stand exactly when the previous outfit does; an empty scar read
			// on a standing hero is the same kind of miss (a scar is never restated away).
			WornDyes: keep && worn.Count == 0 ? before.WornDyes : dyes,
			Scars: keep && scars.Length == 0 && before.Scars is { Length: > 0 } ? before.Scars : scars);
	}

	/// <summary>
	/// The save profile the game runs from — `user_data_root`, set by the shell. Null until it is.
	/// </summary>
	public static string? UserDataRoot { get; set; }

	/// <summary>The save name a full kit was last read for, so a pick is read once.</summary>
	private static string _sampledSave = string.Empty;

	/// <summary>
	/// Whether <see cref="Local"/> is the save the player chose on the co-op screen. A live
	/// capture from before the join is a hero too, but not the one X offers: that comes from
	/// the chosen save or nowhere.
	/// </summary>
	public static bool ChosenSaveRead { get; private set; }

	/// <summary>
	/// Builds <see cref="Local"/> from the save the player chose on the co-op screen — read off
	/// that save's OWN FILES by <see cref="Engine_SaveHero"/>, never by loading its
	/// world: body, clothes, weapons, inventory, stats, experience, money, face. A different pick
	/// replaces it; the same pick is not re-read.
	/// </summary>
	public static void SampleChosenSave()
	{
		string? name = Fable2Bridge.CoopHeroSaveName();
		if (name is null || name == _sampledSave || UserDataRoot is null)
		{
			return;
		}

		_sampledSave = name;
		_pendingMelee = 0u;
		_pendingRanged = 0u;
		if (KitFromSave(name, string.Empty) is not { } kit)
		{
			ChosenSaveRead = false;
			return;
		}

		ChosenSaveRead = true;
		Local = kit;
	}

	/// <summary>
	/// LOCAL CO-OP: player two's kit from the save they picked on the co-op screen (the guest
	/// holds its name where it saves player two's hero, SaveMgr+4), into <see cref="Remote"/> —
	/// the kit that describes player two on a host, as a joiner's does online. Fable 3's couch
	/// rule: a player two with a save plays as their hero, one without as the henchman. True when
	/// the kit changed (a new pick read).
	/// </summary>
	public static bool SampleLocalPlayerTwo()
	{
		string? name = Fable2Bridge.CoopHeroSaveName();
		if (name is null || name == _sampledLocalTwo || UserDataRoot is null)
		{
			return false;
		}

		_sampledLocalTwo = name;
		if (KitFromSave(name, "Player 2") is not { } kit)
		{
			return false;
		}

		Remote = kit;
		return true;
	}

	/// <summary>The save local player two's kit was last read from, or empty.</summary>
	private static string _sampledLocalTwo = string.Empty;

	/// <summary>
	/// A kit read off a save's OWN FILES by <see cref="Engine_SaveHero"/>, never by
	/// loading its world: body, clothes, weapons, inventory, stats, experience, money, face, dog.
	/// Null when the save cannot be read.
	/// </summary>
	private static Kit? KitFromSave(string name, string heroName)
	{
		string? directory = Engine_SaveHero.FindSaveDirectory(UserDataRoot!, name);
		Engine_SaveHero.Hero? hero = directory is null ? null : Engine_SaveHero.Read(directory);
		if (hero is null)
		{
			Godot.GD.Print($"[coop-kit] chosen save '{name}' could not be read " +
				$"({(directory is null ? "no directory under " + UserDataRoot : directory)})");
			return null;
		}

		// A record of a mod the save was stamped with that is not enabled now never reaches the
		// guest: the kit is filtered the way the main save is on load (ModSaveGate).
		int filtered = Fable2KitFilter.FilterKit(directory!, hero);
		if (filtered != 0)
		{
			Godot.GD.Print($"[coop-kit] chosen save '{name}': {filtered} entr{(filtered == 1 ? "y" : "ies")} of missing mods left out of the kit");
		}

		var kit = new Kit(
			Known: true,
			Female: hero.Female,
			Morality: hero.Morality,
			Purity: hero.Purity,
			Renown: hero.Renown,
			Strength: hero.Strength, Skill: hero.Skill, Will: hero.Will,
			AbilityLevels: hero.AbilityLevels,
			Worn: hero.Worn.ToArray(),
			Inventory: hero.Inventory.ToArray(),
			Name: heroName,
			Money: hero.Money,
			Experience: hero.Experience,
			TotalExperience: hero.TotalExperience,
			Face: hero.Face, Fat: hero.Fat, Height: hero.Height, Tan: hero.Tan,
			MeleeId: hero.Melee, RangedId: hero.Ranged,
			// The save's own dog: its breed, levels, tricks (named through this instance's GDB
			// view), collar and name.
			DogDefinition: hero.DogRecord,
			DogSearching: hero.DogSniffing,
			DogCombat: hero.DogCombat,
			DogTricks: hero.DogTricks.ConvertAll(id => RecordName?.Invoke(id) ?? string.Empty)
				.FindAll(trick => trick.Length != 0).ToArray(),
			DogWorn: hero.DogWorn.ToArray(),
			DogName: hero.DogName,
			WornDyes: hero.WornDyes.ConvertAll(dye => new Fable2Bridge.WornDye(
				dye.Record, SlotOf(dye.Primary), SlotOf(dye.Secondary))).ToArray(),
			Scars: hero.Scars.ConvertAll(scar => new Fable2Bridge.Scar(scar.Area, scar.Record, scar.Permanent)).ToArray());

		Godot.GD.Print($"[coop-kit] chosen save '{name}' read: {hero.Worn.Count} worn, " +
			$"{hero.Inventory.Count} inventory record(s), melee '{Name(hero.Melee)}' (0x{hero.Melee:X8}), " +
			$"ranged '{Name(hero.Ranged)}' (0x{hero.Ranged:X8}), " +
			$"money {hero.Money}, renown {hero.Renown}, " +
			(hero.Face == 0u ? "no character-creation face (the save's CharacterUID is 0)" : $"face 0x{hero.Face:X8}"));
		return kit;
	}

	/// <summary>A saved dye slot as the kit carries it.</summary>
	private static Fable2Bridge.SlotDye? SlotOf(Engine_SaveHero.Dye? dye) =>
		dye is null ? null : new Fable2Bridge.SlotDye(dye.Red, dye.Green, dye.Blue, dye.Brightness);

	/// <summary>A record's def name for a log line; empty without a GDB view.</summary>
	private static string Name(uint record) =>
		record != 0u && RecordName is { } resolve ? resolve(record) ?? string.Empty : string.Empty;

	/// <summary>
	/// REFII_MOD_HOST_HERO_KIND (core/refii_mod_events.h): an online joiner's weapon pick a native
	/// or Lua mod made (<c>refii_mod_equip_weapon</c> / <c>Refii.equip_weapon</c>), as the guest
	/// tick posts it — <c>player=N,op='weapon',record='0x…'</c>. Never an event; RefiiHost hands it
	/// to <see cref="HandlePick"/>.
	/// </summary>
	public const int PickKind = 1005;

	/// <summary>
	/// A <see cref="PickKind"/> line into <see cref="SetLocalWeapon"/>, with the record's own weapon
	/// class (<see cref="Engine_UiInventorySource.WeaponClassOf"/>) — the same route the
	/// joiner's own menu pick takes. False for a line that is not a weapon pick.
	/// </summary>
	public static bool HandlePick(string fields)
	{
		string op = string.Empty;
		uint record = 0u;
		foreach (KeyValuePair<string, object> pair in Refii.Mods.RefiiHost.ParseFieldsPlain(fields))
		{
			if (pair.Key == "op")
			{
				op = pair.Value as string ?? string.Empty;
			}
			else if (pair.Key == "record")
			{
				string text = pair.Value is string s ? s : System.Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
				record = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
					? (uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint hex) ? hex : 0u)
					: (uint.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out uint dec) ? dec : 0u);
			}
		}

		if (op != "weapon" || record == 0u)
		{
			return false;
		}

		return SetLocalWeapon(record, Engine_UiInventorySource.WeaponClassOf(record));
	}

	/// <summary>
	/// A joiner's weapon pick goes into the record: the host equips it and the replica comes
	/// back (`CoopHeroHeld`). <paramref name="weaponClass"/> is 1 melee, 2 ranged. The changed
	/// kit reaches the peer on the next HeroState tick, as a new version.
	/// </summary>
	public static bool SetLocalWeapon(uint record, int weaponClass)
	{
		if (!Local.Known || record == 0u || (weaponClass != 1 && weaponClass != 2))
		{
			return false;
		}

		if (weaponClass == 1)
		{
			_pendingMelee = record;
			Local = Local with { MeleeId = record };
		}
		else
		{
			_pendingRanged = record;
			Local = Local with { RangedId = record };
		}

		Godot.GD.Print($"[coop-kit] local hero's {(weaponClass == 1 ? "melee" : "ranged")} weapon is " +
			$"now 0x{record:X8} '{Name(record)}' — the host equips it from the record");
		return true;
	}

	/// <summary>
	/// A pick the live weapon slot has not reported yet. <see cref="Sample"/> keeps it over the
	/// slot's record until the slot holds it, then drops it. 0 when nothing is pending.
	/// </summary>
	private static uint _pendingMelee;

	private static uint _pendingRanged;

	/// <summary>Drops everything learned from the peer — on a lost connection, so a later join starts clean.</summary>
	public static void ForgetRemote() => Remote = default;

	/// <summary>
	/// Drops every piece of session state: the peer's kit, the chosen save and the kit read from
	/// it, pending picks. A live capture of this instance's own hero survives — it belongs to
	/// the player, not the session. The protocol's own state is <see cref="CoopHeroState.Reset"/>'s.
	/// </summary>
	public static void ResetSession()
	{
		ForgetRemote();
		if (ChosenSaveRead)
		{
			Local = default;
		}

		_sampledSave = string.Empty;
		_sampledLocalTwo = string.Empty;
		ChosenSaveRead = false;
		_pendingMelee = 0u;
		_pendingRanged = 0u;
	}
}
