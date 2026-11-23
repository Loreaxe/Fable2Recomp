using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The guest's boxes as the title states them open, for the shared box surfaces
/// (<see cref="Engine_UiOwnedSurfaces"/>) and the source's answer address
/// (<see cref="Engine_UiSource.DisplayBoxAddress"/>). A title states its own through
/// <see cref="Refii.Core.Engine_TitleGame.StatedBoxes"/>; this base states none open.
/// </summary>
/// <remarks>Pure .NET: held by tests without a Godot type in the host.</remarks>
public class Engine_UiStatedBoxes
{
	/// <summary>A title that states no box.</summary>
	public static readonly Engine_UiStatedBoxes None = new();

	/// <summary>The active title's statements.</summary>
	public static Engine_UiStatedBoxes Current => Refii.Core.Engine_TitleGame.Active.StatedBoxes;

	/// <summary>The box the router hands input to, null when none is open.</summary>
	public virtual Box? Showing => null;

	/// <summary>The yes/no question box, when its queue shows one.</summary>
	public virtual Box? ShowingQuery => null;

	/// <summary>The numeric adjuster box, when its queue shows one.</summary>
	public virtual Box? ShowingAdjuster => null;

	/// <summary>The menu box stated open, null once its close is stated.</summary>
	public virtual MenuBox? Menu => null;

	/// <summary>One open box as its Open stated it.</summary>
	/// <param name="Address">The box object — what an answer is addressed to.</param>
	/// <param name="Kind">The class's type, as the title numbers its box classes.</param>
	/// <param name="Name">The identity the box carries, the name a removal matches.</param>
	/// <param name="Body">The resolved body, what the Open writes into the presentation.</param>
	/// <param name="Fields">The class's own fields, in the order its hook states them.</param>
	public sealed record Box(uint Address, int Kind, string Name, string Body, IReadOnlyList<string> Fields)
	{
		/// <summary>The adjuster's widget, for an adjuster: the host steps it by the widget's rule on the
		/// same codes it sends the guest.</summary>
		public Engine_UiGuiBoxes.AdjusterState? Adjuster { get; init; }

		public string Field(int index) => index >= 0 && index < Fields.Count ? Fields[index] : string.Empty;

		public int Number(int index) =>
			int.TryParse(Field(index), System.Globalization.NumberStyles.Integer,
				System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : 0;

		public bool Flag(int index) => Field(index) == "1";
	}

	/// <summary>One menu box as its populate stated it: the title, cancelable, the Orb's player card,
	/// and each row's label (resolved by the receiver) and enabled byte.</summary>
	public sealed record MenuBox(uint Address, Engine_UiGuiBoxes.MenuBoxModel Model, bool Card)
	{
		/// <summary>The player card's name as its build stated it, null until it does.</summary>
		public string? GamerName { get; init; }

		/// <summary>The row the box's cursor write last stated: 0 at the open, -1 for none.</summary>
		public int Selected { get; init; }
	}
}
