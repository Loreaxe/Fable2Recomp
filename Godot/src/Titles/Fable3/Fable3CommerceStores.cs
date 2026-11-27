namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Refii.Ui;

/// <summary>
/// THE SHOP'S AND THE PAWN SHOP'S FIELDS that no call states, as their store instructions state
/// them (commerce_stated_stores.cpp): the shop mode's state and "stealing allowed", the pawn-shop
/// mode's state and "stealing allowed", the pawn shop's display page and category, each keyed by
/// the object the guest stored it into.
/// </summary>
/// <remarks>
/// Shop states: 0 at the plinth, 1 browsing, 2 stealing or haggling, 3 just bought, 4 the weapon
/// page. Pawn-shop states: 0 talking, 1 the list, 2 expression. A statement comes only when the
/// stored value changed for that object; the shop mode's close (vf2, `@Shop.Close`) forgets its two.
/// </remarks>
public static class Fable3CommerceStores
{
	public enum Field
	{
		ShopState,
		ShopStealAllowed,
		PawnState,
		PawnStealAllowed,
		PawnPage,
		PawnCategory,
	}

	private static readonly Dictionary<string, Field> Names = new(System.StringComparer.Ordinal)
	{
		["@Shop.State"] = Field.ShopState,
		["@Shop.StealAllowed"] = Field.ShopStealAllowed,
		["@Pawn.State"] = Field.PawnState,
		["@Pawn.StealAllowed"] = Field.PawnStealAllowed,
		["@Pawn.Page"] = Field.PawnPage,
		["@Pawn.Category"] = Field.PawnCategory,
	};

	private static readonly Dictionary<(Field, uint), int> Values = new();

	/// <summary>The shop mode up: the last one a state was stored for, until its close (`@Shop.Close`); 0 for none.</summary>
	public static uint OpenShop { get; private set; }

	/// <summary>Bumped on every statement here.</summary>
	public static ulong Generation { get; private set; }

	/// <summary>The value last stored into <paramref name="field"/> of <paramref name="owner"/>; null before any.</summary>
	public static int? Of(Field field, uint owner) => Values.TryGetValue((field, owner), out int value) ? value : null;

	public static void Clear()
	{
		Values.Clear();
		OpenShop = 0u;
		Generation++;
	}

	public static void Register(Engine_UiGuiNatives router)
	{
		foreach (string name in Names.Keys)
		{
			router.Register(name, call => Take(call));
		}

		router.Register("@Shop.Close", call => Take(call));
	}

	/// <summary>Takes one statement (the router's and the tests' entry); false when it is not this model's.</summary>
	public static bool Take(Engine_UiModel.GuiNativeCall call)
	{
		if (call.Name == "@Shop.Close")
		{
			uint closed = Fable3Elements.Parse(call.Argument(0).Text);
			Values.Remove((Field.ShopState, closed));
			Values.Remove((Field.ShopStealAllowed, closed));
			if (OpenShop == closed)
			{
				OpenShop = 0u;
			}

			Generation++;
			Fable3Elements.Log($"[fable3-commerce] shop {closed:X8} closed");
			return true;
		}

		if (!Names.TryGetValue(call.Name, out Field field))
		{
			return false;
		}

		uint owner = Fable3Elements.Parse(call.Argument(0).Text);
		bool flag = field is Field.ShopStealAllowed or Field.PawnStealAllowed;
		int value = flag ? (call.Argument(1).Boolean ? 1 : 0) : (int)call.Argument(1).Number;
		Values[(field, owner)] = value;
		if (field == Field.ShopState)
		{
			OpenShop = owner;
		}

		Generation++;
		Fable3Elements.Log($"[fable3-commerce] {call.Name} {owner:X8} = {value}");
		return true;
	}
}
