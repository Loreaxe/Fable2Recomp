namespace Refii.Entities;

/// <summary>
/// WHERE AN ENTITY'S BODY IS on its way from the guest's statement to a node of the entity feed,
/// for a consumer that shows one entity by address (the co-op screen's two figures). The feed
/// holds no node for an entity until its body is assembled: a composite body (component 17) is
/// assembled off-thread from the part list the guest states, so a figure the guest has just made
/// is walked and has parts before it has a node.
/// </summary>
public static class Engine_FigureBody
{
	public enum State
	{
		/// <summary>No walk of the feed has reached the entity.</summary>
		NotWalked,

		/// <summary>Walked; the guest states neither a model nor a body part for it.</summary>
		NoParts,

		/// <summary>Walked and stated; its body is being assembled or its node is not created yet.</summary>
		Building,

		/// <summary>The feed holds its node.</summary>
		Built,
	}

	/// <summary>
	/// The state from what the feed keeps for the address: whether a walk stamped it, how many
	/// composite parts the guest stated at the last read, whether it states a single model, and
	/// whether a node stands.
	/// </summary>
	public static State Of(bool walked, int statedParts, bool statesModel, bool hasNode) =>
		hasNode ? State.Built
		: !walked ? State.NotWalked
		: statedParts <= 0 && !statesModel ? State.NoParts
		: State.Building;

	/// <summary>The state as the log says it.</summary>
	public static string Describe(State state) => state switch
	{
		State.NotWalked => "not walked by the entity feed yet",
		State.NoParts => "walked by the entity feed, the guest states no body for it yet",
		State.Building => "walked by the entity feed, its body is still being built",
		_ => "built by the entity feed, not on the stage yet",
	};
}
