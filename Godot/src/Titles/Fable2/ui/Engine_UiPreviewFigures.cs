using System.Collections.Generic;
using System.Globalization;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE GUI'S PREVIEW FIGURES, as the guest's preview scene files and destroys them
/// (preview_figures_stated.cpp): the clothing screen's mannequin, and the co-op screen's
/// player-one mannequin and henchman. A figure stands from its `@Preview.Add` until the
/// `@Preview.Remove` of its ticket.
/// </summary>
public static class Engine_UiPreviewFigures
{
	public const string Clothing = "clothing";
	public const string PlayerOne = "playerone";
	public const string Henchman = "henchman";

	private static readonly Dictionary<uint, (string Role, uint Entity)> Standing = new();

	/// <summary>`@Preview.Add`: role, ticket, entity (hex).</summary>
	public static void Add(Engine_UiModel.GuiNativeCall call)
	{
		if (call.Argument(0).Text is { } role &&
			uint.TryParse(call.Argument(1).Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ticket) &&
			uint.TryParse(call.Argument(2).Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint entity))
		{
			Standing[ticket] = (role, entity);
		}
	}

	/// <summary>`@Preview.Remove`: ticket.</summary>
	public static void Remove(Engine_UiModel.GuiNativeCall call)
	{
		if (uint.TryParse(call.Argument(0).Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ticket))
		{
			Standing.Remove(ticket);
		}
	}

	/// <summary>The guest is gone: so is every figure.</summary>
	public static void Reset() => Standing.Clear();

	/// <summary>The entity standing as <paramref name="role"/> — the latest filed — or 0.</summary>
	public static uint Entity(string role)
	{
		uint entity = 0u;
		uint latest = 0u;
		foreach ((uint ticket, (string held, uint body)) in Standing)
		{
			if (held == role && ticket >= latest)
			{
				latest = ticket;
				entity = body;
			}
		}

		return entity;
	}
}
