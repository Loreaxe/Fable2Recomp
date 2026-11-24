namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using System.Globalization;
using Refii.Ui;

/// <summary>
/// Fable 3's UI surfaces as the calls that carry their values STATE them — the interaction mode's
/// buttons and list, the TopBox queue, the save menu, the Options screen's own query, the HUD
/// root, the HUD widgets (money, objectives, target names, crime timer, job multiplier) and the
/// box screens' records — routed from ui_stated_hooks.cpp, topbox_hooks.cpp and
/// screen_stated_hooks.cpp. Where the guest's writer names an element, the value lands on that
/// element of its document (<see cref="Fable3Elements.State"/>); the rest is held here for the
/// views and logged (<c>[fable3-stated]</c>) so a run compares it with the element writes the
/// guest still makes. Pure — no Godot type — so the tests drive it.
/// </summary>
public static class Fable3UiStatements
{
	/// <summary>One TopBox message as its post stated it (sub_824B5EE0's Msg).</summary>
	public readonly record struct TopBoxMessage(
		uint Id, int Channel, uint Title, uint MainVtable, uint MainTag, float Lifetime, uint LabelA, uint LabelY,
		bool ShowA, bool HoldA, bool ShowY, bool HoldY, int Player);

	/// <summary>
	/// THE TOPBOX QUEUE, by channel, in post order: a post pushes onto its channel
	/// (`*(M+76)[channel]`), a removal erases the id wherever it is (sub_824B5680), a clear empties
	/// every channel (sub_824B68D0). The message up is the back of the first non-empty channel
	/// (sub_82194270).
	/// </summary>
	public sealed class TopBoxQueue
	{
		private readonly SortedDictionary<int, List<TopBoxMessage>> _channels = new();

		public void Post(TopBoxMessage message)
		{
			if (!_channels.TryGetValue(message.Channel, out List<TopBoxMessage>? channel))
			{
				_channels[message.Channel] = channel = new List<TopBoxMessage>();
			}

			channel.Add(message);
		}

		public void Remove(uint id)
		{
			foreach (List<TopBoxMessage> channel in _channels.Values)
			{
				channel.RemoveAll(message => message.Id == id);
			}
		}

		public void Clear() => _channels.Clear();

		/// <summary>The message up, or null when every channel is empty.</summary>
		public TopBoxMessage? Current
		{
			get
			{
				foreach (List<TopBoxMessage> channel in _channels.Values)
				{
					if (channel.Count > 0)
					{
						return channel[^1];
					}
				}

				return null;
			}
		}

		public int Count
		{
			get
			{
				int count = 0;
				foreach (List<TopBoxMessage> channel in _channels.Values)
				{
					count += channel.Count;
				}

				return count;
			}
		}
	}

	public static TopBoxQueue TopBox { get; } = new();

	/// <summary>The last answer the TopBox got (1 A, 4 Y), or 0.</summary>
	public static int TopBoxAnswer { get; private set; }

	/// <summary>The save menu: up, whether saving is allowed, its row (1 Save, 2 Save and Quit, 3 Quit, 4 Character Management).</summary>
	public static bool SaveMenuOpen { get; private set; }

	public static bool SaveMenuAllowed { get; private set; }

	public static int SaveMenuRow { get; private set; }

	/// <summary>The Options screen's own query: the box that asked and the question's tag (FNV-1), or null.</summary>
	public static (uint Box, uint Tag)? OptionsQuery { get; private set; }

	/// <summary>The interaction list's cursor, count and last scroll (0 refresh, 1 down, 2 up).</summary>
	public static (int Cursor, int Count, int Scroll) InteractionList { get; private set; }

	/// <summary>The dye bar's colour, value and range as ShowDyingBar stated them.</summary>
	public static (float R, float G, float B, float Value, float Min, float Max)? DyeBar { get; private set; }

	/// <summary>The side panel's kind (1 weapon, 2 clothes, 3 augment, 0 none) as stated.</summary>
	public static int SidePanel { get; private set; }

	/// <summary>Per player (0, 1): the money alert's shown amount and whether it is the kingdom's.</summary>
	public static readonly (bool Shown, int Amount, bool Kingdom)[] Money = new (bool, int, bool)[2];

	/// <summary>Per player: the targeted entity, 0 when cleared.</summary>
	public static readonly uint[] Targets = new uint[2];

	public static int CrimeFine { get; private set; }

	public static int CrimeSeconds { get; private set; }

	/// <summary>The crime timer shown, as the crime manager's tick decided it.</summary>
	public static bool CrimeShown { get; private set; }

	/// <summary>The job multiplier's level and its caption's prefix.</summary>
	public static (int Level, string Prefix) Job { get; private set; } = (0, string.Empty);

	/// <summary>Bumped on every statement here.</summary>
	public static ulong Generation { get; private set; }

	private static System.Action<string> Log => Fable3Elements.Log;

	private static readonly Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>> Routes = BuildRoutes();

	/// <summary>Routes this model's statements from the natives router.</summary>
	public static void Register(Engine_UiGuiNatives router)
	{
		foreach (KeyValuePair<string, System.Action<Engine_UiModel.GuiNativeCall>> route in Routes)
		{
			System.Action<Engine_UiModel.GuiNativeCall> handler = route.Value;
			router.Register(route.Key, call => handler(call));
		}
	}

	/// <summary>Takes one statement as the router would (the tests' entry); false when it is not this model's.</summary>
	public static bool Take(Engine_UiModel.GuiNativeCall call)
	{
		if (!Routes.TryGetValue(call.Name, out System.Action<Engine_UiModel.GuiNativeCall>? handler))
		{
			return false;
		}

		handler(call);
		return true;
	}

	private static Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>> BuildRoutes()
	{
		var routes = new Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>>(System.StringComparer.Ordinal);
		void Add(string name, System.Action<Engine_UiModel.GuiNativeCall> handler) => routes[name] = handler;

		Add("@Hud.Root", call => Fable3Elements.StateHudRoot((int)call.Argument(0).Number, call.Argument(1).Text));

		// The cut presentation Lua's inputs: the Parameters stagers and the chunks the C++ ran.
		Add("@Lua.Param", Fable3LuaContracts.Param);
		Add("@Lua.Chunk", Fable3LuaContracts.Chunk);
		Add("@Interaction.Button", call => Button(
			Fable3Elements.DocumentOfKey(call.Argument(0).Text), call.Argument(1).Text,
			call.Argument(2).Text, call.Argument(3).Text, call.Argument(4).Boolean));
		Add("@Interaction.List", call =>
			List((int)call.Argument(0).Number, (int)call.Argument(1).Number, (int)call.Argument(2).Number));
		Add("@Interaction.DyeBar", call => Said(() => DyeBar = (call.Argument(0).Number, call.Argument(1).Number,
			call.Argument(2).Number, call.Argument(3).Number, call.Argument(4).Number, call.Argument(5).Number),
			$"dye bar {call.Argument(3).Number} in [{call.Argument(4).Number}, {call.Argument(5).Number}]"));
		Add("@Interaction.SidePanel", call => Said(() => SidePanel = (int)call.Argument(0).Number,
			$"side panel kind {(int)call.Argument(0).Number}"));

		Add("@Hud.Lines", Lines);
		Add("@TopBox.Post", call => TopBoxPost(ParseMessage(call)));
		Add("@TopBox.Remove", call => TopBoxRemove(Fable3Elements.Parse(call.Argument(0).Text)));
		Add("@TopBox.Clear", _ => TopBoxClear());
		Add("@TopBox.Answer", call => Said(() => TopBoxAnswer = (int)call.Argument(0).Number,
			$"TopBox answered {(int)call.Argument(0).Number}"));

		Add("@SaveMenu.Open", call => SaveMenuOpened(call.Argument(0).Boolean, (int)call.Argument(1).Number));
		Add("@SaveMenu.Row", call => Said(() => SaveMenuRow = (int)call.Argument(0).Number, null));
		Add("@SaveMenu.Close", _ => Said(() => SaveMenuOpen = false, "save menu closed"));
		Add("@Options.Query", call => Said(() => OptionsQuery =
			(Fable3Elements.Parse(call.Argument(0).Text), Fable3Elements.Parse(call.Argument(1).Text)),
			$"options query tag {call.Argument(1).Text}"));

		Add("@Hud.Money.Change", call => MoneyChanged(
			(int)call.Argument(0).Number, (int)call.Argument(1).Number, (int)call.Argument(2).Number,
			call.Argument(3).Boolean, call.Argument(4).Boolean));
		Add("@Hud.Money.Show", call => MoneyShown(
			(int)call.Argument(0).Number, (int)call.Argument(1).Number, call.Argument(2).Boolean));
		Add("@Hud.Money.Hide", call => MoneyHidden((int)call.Argument(0).Number));
		Add("@Hud.Target.Show", call => Target((int)call.Argument(0).Number, Fable3Elements.Parse(call.Argument(1).Text)));
		Add("@Hud.Target.Clear", call => Target((int)call.Argument(0).Number, 0u));
		Add("@Hud.Crime.Fine", call => Said(() => CrimeFine = (int)call.Argument(0).Number, null));
		Add("@Hud.Crime.Seconds", call => Said(() => CrimeSeconds = (int)call.Argument(0).Number, null));
		// THE CRIME TIMER'S SHOWN STATE, CT+0 (CT = `*(H+0x68)`), at every store that precedes the
		// root's SetState to the same value (screen_stated_hooks.cpp RefiiF3CrimeTimerShown): the
		// crime tick sub_828393F0, the HUD build sub_825E5158, the partner's packet 369
		// sub_825E5408, the manager's vf13, the criminal teardown sub_82837970, RemoveCriminal
		// sub_8283CF88, GUI.ShowMiscGUIElements and the region-change save sub_8232B870. The root's
		// hud.bgf slide 2 `show` / 1 `hide`, played by Godot.
		Add("@Hud.Crime.Shown", call => Said(() =>
		{
			CrimeShown = call.Argument(0).Boolean;
			Fable3Elements.StateSlide(Fable3Elements.HudDocument, "CrimeTimer", CrimeShown ? 2 : 1);
		}, $"crime timer {(call.Argument(0).Boolean ? "shown" : "hidden")}"));
		Add("@Hud.Job", call => Said(() => JobLevel((int)call.Argument(0).Number, call.Argument(1).Text),
			$"job multiplier {call.Argument(1).Text}{(int)call.Argument(0).Number}"));
		// The look-at alert's slide as sub_82B14CB8 chose it (0x82B14D30) or the HUD tick's hide 6
		// (0x8226A730), on `LookAt.LookAt.LookAt` (hud.bgf #510).
		Add("@Hud.LookAt", call => Said(() => Fable3Elements.StateSlide(Fable3Elements.HudDocument, "LookAt.LookAt.LookAt",
			(int)call.Argument(0).Number), null));
		Add("TurnJobMultiplierOff", call => Said(() => JobOff(call.Argument(0).Boolean),
			$"job multiplier off ({(call.Argument(0).Boolean ? "success" : "gone")})"));

		// A map table message class the table does not draw (map_table_hooks.cpp), named once.
		Add("@MapTable.Unstated", call => Said(null,
			$"map table {call.Argument(0).Text} adds message class {call.Argument(2).Text} (id {(int)call.Argument(1).Number}) that is not drawn"));

		return routes;
	}

	/// <summary>The job multiplier's root (`JobMultiplier`, `*dword_8355C0F8`, J = `*(H+68)+20`).</summary>
	public const string JobRoot = "JobMultiplier";

	/// <summary>
	/// `SetJobMultiplierLevel` (0x8251CD90 -> sub_82950B60(J, level, prefix)): the root plays
	/// hud.bgf's `LevelUpFromOff` (1) from level 0, `LevelUpFromOn` (2) for a higher level, else
	/// `LevelDownStillOn` (3) — the transitions the writer names (0x82950C28, 0x82950C64,
	/// 0x82950C9C); J+16 keeps the level (0x82950C38 / 0x82950C84 / 0x82950CA0).
	/// </summary>
	public static void JobLevel(int level, string prefix)
	{
		int was = Job.Level;
		Job = (level, prefix);
		int slide = was == 0 ? 1 : level > was ? 2 : 3;
		Fable3Elements.StateSlide(Fable3Elements.HudDocument, JobRoot, slide);
	}

	/// <summary>
	/// `TurnJobMultiplierOff(success)` (0x8251CDF0): nothing at level 0 (0x8251CE28 / 0x8251CE38),
	/// else `LevelDownToOffSu` (4) for true or `LevelDownToOffGr` (5), and the level is 0 (0x8251CE50).
	/// </summary>
	public static void JobOff(bool success)
	{
		if (Job.Level == 0)
		{
			return;
		}

		Job = (0, Job.Prefix);
		Fable3Elements.StateSlide(Fable3Elements.HudDocument, JobRoot, success ? 4 : 5);
	}

	/// <summary>Forgets everything (a new session; the tests).</summary>
	public static void Clear()
	{
		TopBox.Clear();
		TopBoxAnswer = 0;
		SaveMenuOpen = false;
		SaveMenuAllowed = false;
		SaveMenuRow = 0;
		OptionsQuery = null;
		InteractionList = (0, 0, 0);
		DyeBar = null;
		SidePanel = 0;
		System.Array.Clear(Money);
		System.Array.Clear(Targets);
		CrimeFine = 0;
		CrimeSeconds = 0;
		CrimeShown = false;
		Job = (0, string.Empty);
		++Generation;
	}

	/// <summary>
	/// `@Interaction.Button(pres, button, label, glyph, enabled)` — sub_82961340's writes in its
	/// own order: the button's opacity 100 enabled / 50 not (0x8296139C), the label on its `Title`
	/// (0x829613E4), the glyph on `Icon.Container.A_Text` (0x82961464) and the button shown
	/// (sub_82819AE0(button, pres, 1) at 0x82961494).
	/// </summary>
	public static void Button(string document, string button, string label, string glyph, bool enabled)
	{
		if (button.Length == 0)
		{
			return;
		}

		Fable3Elements.State(document, button, element =>
		{
			element.Opacity = enabled ? 100f : 50f;
			element.StatedOpacity = enabled ? 100f : 50f;
			element.Visible = null;
			element.MarkOpacity();
		});
		Fable3Elements.State(document, button + ".Title", element =>
			element.Stated = new Fable3Elements.StatedText(label, null, null, "82961340"));
		if (glyph.Length > 0)
		{
			Fable3Elements.State(document, button + ".Icon.Container.A_Text", element =>
			{
				element.Texture = glyph;
				element.StatedTexture = glyph;
			});
		}

		Fable3Elements.State(document, button, element =>
		{
			element.Visible = true;
			element.MarkOpacity();
		});
		++Generation;
	}

	public static void List(int cursor, int count, int scroll)
	{
		InteractionList = (cursor, count, scroll);
		++Generation;
	}

	/// <summary>
	/// `@Hud.Lines(box, n, hash, text, flag, …)`: a text box's lines as the caller passed them
	/// (sub_82920BC8 at entry), unwrapped — the text each line's TextName shows when the registry's
	/// own wrapped write has not said one (<see cref="Fable3HudRegistry.Line"/>).
	/// </summary>
	public static void Lines(Engine_UiModel.GuiNativeCall call)
	{
		uint box = Fable3Elements.Parse(call.Argument(0).Text);
		int count = (int)call.Argument(1).Number;
		var lines = new (uint Hash, string Text)[count];
		for (int i = 0; i < count; ++i)
		{
			int at = 2 + (i * 3);
			lines[i] = (Fable3Elements.Parse(call.Argument(at).Text), call.Argument(at + 1).Text);
			Fable3HudRegistry.StateLine(box, lines[i].Hash, lines[i].Text);
		}

		Fable3HudRegistry.StateBoxLines(box, lines);

		++Generation;
	}

	public static TopBoxMessage ParseMessage(Engine_UiModel.GuiNativeCall call) => new(
		Fable3Elements.Parse(call.Argument(0).Text), (int)call.Argument(1).Number,
		Fable3Elements.Parse(call.Argument(2).Text), Fable3Elements.Parse(call.Argument(3).Text),
		Fable3Elements.Parse(call.Argument(4).Text), call.Argument(5).Number,
		Fable3Elements.Parse(call.Argument(6).Text), Fable3Elements.Parse(call.Argument(7).Text),
		call.Argument(8).Boolean, call.Argument(9).Boolean, call.Argument(10).Boolean, call.Argument(11).Boolean,
		(int)call.Argument(12).Number);

	public static void TopBoxPost(TopBoxMessage message)
	{
		TopBox.Post(message);
		ReportTopBox("posted " + message.Id.ToString("X8", CultureInfo.InvariantCulture));
	}

	public static void TopBoxRemove(uint id)
	{
		TopBox.Remove(id);
		ReportTopBox("removed " + id.ToString("X8", CultureInfo.InvariantCulture));
	}

	public static void TopBoxClear()
	{
		TopBox.Clear();
		ReportTopBox("cleared");
	}

	private static void ReportTopBox(string what)
	{
		++Generation;
		TopBoxMessage? up = TopBox.Current;
		Log($"[fable3-stated] TopBox {what}: {TopBox.Count} queued, up " + (up is { } m
			? $"{m.Id:X8} ch {m.Channel} title {m.Title:X8} main {m.MainTag:X8} A {(m.ShowA ? (m.HoldA ? "hold " : string.Empty) + m.LabelA.ToString("X8", CultureInfo.InvariantCulture) : "-")} " +
				$"Y {(m.ShowY ? (m.HoldY ? "hold " : string.Empty) + m.LabelY.ToString("X8", CultureInfo.InvariantCulture) : "-")} life {m.Lifetime}"
			: "none"));
	}

	/// <summary>
	/// `@SaveMenu.Open(allowed, row)`. The guest writes the row as the menu root's state
	/// (sub_8281A428(&amp;dword_8355C4C0, pres, +12) at 0x8296C74C, again in the row writer at
	/// 0x8296CB74) and 5 on close (0x8296C88C) — behind the cached presentation; the screen reads
	/// the open flag and the row from here (<see cref="Fable3StatedScreen"/>).
	/// </summary>
	public static void SaveMenuOpened(bool allowed, int row)
	{
		SaveMenuOpen = true;
		SaveMenuAllowed = allowed;
		SaveMenuRow = row;
		++Generation;
		Log($"[fable3-stated] save menu open, saving {(allowed ? "allowed" : "not allowed")}, row {row}");
	}

	public static void MoneyChanged(int player, int old, int delta, bool kingdom, bool queued)
	{
		if (player is 0 or 1 && !queued)
		{
			// The tick counts +16 from old toward old + delta, clamped at 999999999 (0x8281E5EC..0x8281E624).
			long target = System.Math.Clamp((long)old + delta, 0L, 999999999L);
			Money[player] = (true, (int)target, kingdom);
		}

		Said(null, $"money P{player + 1} {old} {(delta >= 0 ? "+" : string.Empty)}{delta}{(kingdom ? " (kingdom)" : string.Empty)}{(queued ? " queued" : string.Empty)}");
	}

	public static void MoneyShown(int player, int amount, bool kingdom)
	{
		if (player is 0 or 1)
		{
			Money[player] = (true, amount, kingdom);
		}

		Said(null, $"money P{player + 1} shown {amount}{(kingdom ? " (kingdom)" : string.Empty)}");
	}

	public static void MoneyHidden(int player)
	{
		if (player is 0 or 1)
		{
			Money[player] = (false, Money[player].Amount, Money[player].Kingdom);
		}

		Said(null, $"money P{player + 1} hidden");
	}

	/// <summary>
	/// THE OBJECTIVE LIST IS DRAWN FROM ITS REDRAW'S OWN CALLS. sub_82953E68(O) writes, behind its
	/// `*H` gate (0x82953E90 / 0x82953E98), the list's slide — count + 1 (`addi r5,r11,1`,
	/// sub_8281A428 at 0x82953EAC), 1 when empty (0x82953FC0) — and per row i from 1 in list order
	/// the tag by name on `Bar&lt;i&gt;.Text` (node+8, sub_82819230 at 0x82953F10 -> sub_828191A8 at
	/// 0x8281926C) and the tick on slide done + 1 (`addi r5,r27,1`, sub_8281A428 at 0x82953F64);
	/// GUI.HideSubObjectives writes slide 1 (0x8251D89C). Those writers are stated at their entry
	/// (`@Ui.State`, `@Ui.TextName`, ui_stated_hooks.cpp),,
	/// so no model of the list is kept: rows past the count keep what was last written, as in the
	/// guest, whose redraw never touches them.
	/// </summary>
	/// <remarks>The list's component (`dword_8355C108`, "ObjectiveList" + ".ObjectiveList").</remarks>
	public const string ObjectiveListPath = "ObjectiveList.ObjectiveList";

	/// <summary>A row's text element: `dword_8355C10C` ("….Bar") + the row from 1 + `dword_8355C114` (".Text").</summary>
	public static string ObjectiveTextPath(int row) => ObjectiveListPath + ".Bar" + row.ToString(CultureInfo.InvariantCulture) + ".Text";

	/// <summary>A row's tick: `dword_8355C10C` + the row + `dword_8355C110` (".tickbox.tick").</summary>
	public static string ObjectiveTickPath(int row) => ObjectiveListPath + ".Bar" + row.ToString(CultureInfo.InvariantCulture) + ".tickbox.tick";

	public static void Target(int player, uint target)
	{
		if (player is 0 or 1)
		{
			Targets[player] = target;
		}

		++Generation;
	}

	private static void Said(System.Action? set, string? what)
	{
		set?.Invoke();
		++Generation;
		if (what is not null)
		{
			Log("[fable3-stated] " + what);
		}
	}
}
