namespace Refii.Titles.Fable2;

using Godot;
using Refii.Ui;

/// <summary>
/// THE HUD'S METERS AND TEXTS AS THE GUEST'S WRITERS STATE THEM (hooks/gui_registry_hooks.cpp,
/// Engine_UiHudRegistryWrites): `@Gui.Meter registry hash op a b c` from the meter registry's four
/// writers and `@Gui.Text registry hash text` from the text registry's, posted at each writer's
/// entry from its arguments.
/// </summary>
public partial class GameUi
{
	private bool _hudRegistryRoutesReady;

	private void EnsureHudRegistryRoutes()
	{
		if (_hudRegistryRoutesReady)
		{
			return;
		}

		_hudRegistryRoutesReady = true;
		_guiNatives.Register("@Gui.Meter", call =>
		{
			EnsureRebuiltHud();
			if (_hud is { } hud && int.TryParse(call.Argument(0).Text, out int registry) &&
				uint.TryParse(call.Argument(1).Text, out uint hash))
			{
				Engine_UiHudRegistryWrites.StateMeter(hud.Values, registry, hash,
					call.Argument(2).Text ?? string.Empty,
					Engine_UiHudRegistryWrites.Number(call.Argument(3).Text),
					Engine_UiHudRegistryWrites.Number(call.Argument(4).Text),
					Engine_UiHudRegistryWrites.Number(call.Argument(5).Text));
			}
		});

		// The guest destroyed or rebuilt the HUD's registries (gui_registry_hooks.cpp).
		_guiNatives.Register("@Gui.RegistryReset", _ =>
		{
			_hud?.Values.ForgetMeters();
			Engine_UiHudPulsers.Reset();
			Refii.Host.HostBridge.ForgetSpellWidgets();
		});

		// A spell assignment widget as its own methods left it (spell_widget_stated.cpp): op,
		// widget, player, the show's mode argument (1 the blade; empty on the other ops), then row,
		// highest row, 5 counts, 5 cells, 5x8 lists.
		_guiNatives.Register("@Spell.Widget", call =>
		{
			string op = call.Argument(0).Text ?? string.Empty;
			if (!uint.TryParse(call.Argument(1).Text, out uint widget) ||
				!int.TryParse(call.Argument(2).Text, out int player))
			{
				return;
			}

			bool? blade = int.TryParse(call.Argument(3).Text, out int mode) ? mode == 1 : null;

			uint[]? words = null;
			if (op != "close")
			{
				words = new uint[53];
				words[0] = widget;
				for (int i = 1; i < words.Length; ++i)
				{
					// The highest row is signed (-1: nothing to assign).
					if (!long.TryParse(call.Argument(3 + i).Text, out long word))
					{
						return;
					}

					words[i] = unchecked((uint)word);
				}
			}

			Refii.Host.HostBridge.StateSpellWidget(op, widget, player, blade, words);
		});

		// The timing meter's fields as its own methods left them (hud_expression_meter_stated.cpp):
		// eleven words, position and speed (5, 6) as float bits.
		_guiNatives.Register("@Hud.ExpressionMeter", call =>
		{
			var values = new float[11];
			for (int i = 0; i < values.Length; ++i)
			{
				if (!uint.TryParse(call.Argument(i).Text, out uint word))
				{
					return;
				}

				values[i] = i is 5 or 6 ? System.BitConverter.UInt32BitsToSingle(word) : (int)word;
			}

			Refii.Host.HostBridge.StateExpressionMeter(values);
		});

		// The pulser registry's start and stop (gui_registry_hooks.cpp): registry, hash, op, rate.
		_guiNatives.Register("@Gui.Pulser", call =>
		{
			if (!uint.TryParse(call.Argument(1).Text, out uint hash))
			{
				return;
			}

			if (call.Argument(2).Text == "start")
			{
				Engine_UiHudPulsers.Start(hash, Engine_UiHudRegistryWrites.Number(call.Argument(3).Text));
			}
			else
			{
				Engine_UiHudPulsers.Stop(hash);
			}
		});

		_guiNatives.Register("@Gui.Text", call =>
		{
			EnsureRebuiltHud();
			if (_hud is { } hud && int.TryParse(call.Argument(0).Text, out int registry) &&
				registry == Engine_UiHudRegistryWrites.TextRegistry &&
				uint.TryParse(call.Argument(1).Text, out uint hash) &&
				Engine_UiHudRegistryWrites.TakesText(hash))
			{
				hud.Values.StateText(hash, call.Argument(2).Text ?? string.Empty);
			}
		});
	}
}
