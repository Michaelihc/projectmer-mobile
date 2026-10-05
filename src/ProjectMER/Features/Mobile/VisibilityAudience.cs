using LabApi.Features.Wrappers;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// A set of players who see a set of admin-only MER objects (indicators, a selection box, a grab proxy).
/// </summary>
/// <remarks>
/// <para>
/// Objects are bound to an audience when they are spawned with
/// <see cref="MerVisibility.Spawn(Mirror.NetworkIdentity, SpawnGroup?, Enums.MerObjectKind, bool, VisibilityAudience?)"/>.
/// Nobody else ever receives a spawn, hide, destroy or state message for them: they are spawned hidden and only members are
/// observers. Adding a member shows the bound objects to that player (paced like any stream) and removing one hides them.
/// </para>
/// <para>
/// Members who disconnect are removed. <see cref="Emptied"/> is raised when the last member leaves, so the owner can
/// destroy objects nobody sees (indicators do).
/// </para>
/// </remarks>
public sealed class VisibilityAudience
{
	private readonly HashSet<ReferenceHub> _members = [];

	/// <summary>
	/// Initializes a new instance of the <see cref="VisibilityAudience"/> class.
	/// </summary>
	/// <param name="name">A name for logs.</param>
	public VisibilityAudience(string name)
	{
		Name = name;
	}

	/// <summary>
	/// Raised when the last member left (removed or disconnected). Not raised by a round reset.
	/// </summary>
	public event Action<VisibilityAudience>? Emptied;

	/// <summary>
	/// Gets the name used in logs.
	/// </summary>
	public string Name { get; }

	/// <summary>
	/// Gets the number of members.
	/// </summary>
	public int Count => _members.Count;

	/// <summary>
	/// Gets the members.
	/// </summary>
	public IReadOnlyCollection<ReferenceHub> Members => _members;

	/// <summary>
	/// Gets the objects bound to this audience.
	/// </summary>
	internal List<VisibilityEntry> Entries { get; } = [];

	/// <summary>
	/// Gets or sets whether the audience is in <see cref="MerVisibility"/>'s list of audiences with members.
	/// </summary>
	internal bool Registered { get; set; }

	/// <summary>
	/// Gets whether a player is a member.
	/// </summary>
	public bool Contains(Player? player) => player != null && _members.Contains(player.ReferenceHub);

	/// <summary>
	/// Gets whether a player is a member.
	/// </summary>
	public bool Contains(ReferenceHub? hub) => hub != null && _members.Contains(hub);

	/// <summary>
	/// Adds a member and shows the bound objects to them.
	/// </summary>
	/// <returns><see langword="false"/> if the player was already a member or has no connection.</returns>
	public bool Add(Player player)
	{
		ReferenceHub hub = player.ReferenceHub;
		if (hub == null || hub.connectionToClient == null || !_members.Add(hub))
			return false;

		MerVisibility.OnAudienceMemberAdded(this, hub);
		return true;
	}

	/// <summary>
	/// Removes a member and hides the bound objects from them.
	/// </summary>
	/// <returns><see langword="false"/> if the player was not a member.</returns>
	public bool Remove(Player player) => Remove(player.ReferenceHub, true);

	/// <summary>
	/// Removes every member.
	/// </summary>
	public void Clear()
	{
		if (_members.Count == 0)
			return;

		List<ReferenceHub> members = NorthwoodLib.Pools.ListPool<ReferenceHub>.Shared.Rent(_members);
		foreach (ReferenceHub hub in members)
			Remove(hub, false);

		NorthwoodLib.Pools.ListPool<ReferenceHub>.Shared.Return(members);
		Emptied?.Invoke(this);
	}

	/// <inheritdoc />
	public override string ToString() => Name;

	/// <summary>
	/// Removes a member.
	/// </summary>
	/// <param name="hub">The member.</param>
	/// <param name="raiseEmptied">Whether to raise <see cref="Emptied"/> when the audience becomes empty.</param>
	/// <returns><see langword="false"/> if the player was not a member.</returns>
	internal bool Remove(ReferenceHub hub, bool raiseEmptied)
	{
		if (hub == null || !_members.Remove(hub))
			return false;

		MerVisibility.OnAudienceMemberRemoved(this, hub);
		if (raiseEmptied && _members.Count == 0)
			Emptied?.Invoke(this);

		return true;
	}

	/// <summary>
	/// Forgets every member without sending anything (round reset: the objects are gone).
	/// </summary>
	internal void ResetMembers()
	{
		_members.Clear();
		Entries.Clear();
		Registered = false;
	}
}
