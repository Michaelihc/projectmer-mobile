using Mirror;
using ProjectMER.Features.Enums;
using UnityEngine;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// Visibility state of one networked MER object whose observers <see cref="MerVisibility"/> chooses.
/// </summary>
/// <remarks>
/// Added when the object is first spawned through <see cref="MerVisibility.Spawn(NetworkIdentity, SpawnGroup?, MerObjectKind, bool)"/>
/// and kept across respawns. Mirror's <c>identity.observers</c> stays the record of who currently receives the object;
/// this component only holds what decides it (group, zone, audience, per-player overrides).
/// </remarks>
[DisallowMultipleComponent]
public sealed class VisibilityEntry : MonoBehaviour
{
	/// <summary>
	/// Gets the object's identity.
	/// </summary>
	public NetworkIdentity Identity { get; internal set; }

	/// <summary>
	/// Gets the spawn group the object was spawned with.
	/// </summary>
	public SpawnGroup? Group { get; internal set; }

	/// <summary>
	/// Gets the kind of object.
	/// </summary>
	public MerObjectKind Kind { get; internal set; }

	/// <summary>
	/// Gets whether only its <see cref="Audience"/> (or players it was shown to explicitly) see the object.
	/// </summary>
	public bool IsAdminOnly { get; internal set; }

	/// <summary>
	/// Gets the audience of an admin-only object, if any.
	/// </summary>
	public VisibilityAudience? Audience { get; internal set; }

	/// <summary>
	/// Gets the culling zone index (see <see cref="MerVisibility.GetZoneName"/>), or -1 when the object is never zone culled.
	/// </summary>
	public int Zone { get; internal set; } = -1;

	/// <summary>
	/// Gets whether the object is currently zone culled (its group reached <c>zone_culling_min_objects</c>).
	/// </summary>
	public bool IsCulled => Root != null && Root.Active && Zone >= 0;

	/// <summary>
	/// Gets whether the object is a relative-positioning waypoint (doors carry a <c>NetIdWaypoint</c>).
	/// </summary>
	/// <remarks>
	/// Clients number these waypoints by sorting the ones they have by netId, and players' and pickups' positions are sent
	/// relative to a waypoint number. A client missing a MER door would number the later doors differently and place
	/// objects near them wrongly, so waypoints are never zone culled and a joining player gets them all at once.
	/// </remarks>
	public bool IsWaypoint { get; internal set; }

	/// <summary>
	/// Gets the streaming priority (collidable first).
	/// </summary>
	internal SpawnPriority Priority { get; set; }

	/// <summary>
	/// Gets the culling root the object is counted in.
	/// </summary>
	internal VisibilityRoot? Root { get; set; }

	/// <summary>
	/// Gets the index in the registry list, or -1 when not registered.
	/// </summary>
	internal int RegistryIndex { get; set; } = -1;

	/// <summary>
	/// Gets the index in <c>Root.Cells[Zone]</c>, or -1.
	/// </summary>
	internal int CellIndex { get; set; } = -1;

	/// <summary>
	/// Gets the index in <c>Audience.Entries</c>, or -1.
	/// </summary>
	internal int AudienceIndex { get; set; } = -1;

	/// <summary>
	/// Gets explicit per-connection decisions made through <see cref="MerVisibility.ShowTo(NetworkIdentity, LabApi.Features.Wrappers.Player)"/>
	/// and <see cref="MerVisibility.HideFrom(NetworkIdentity, LabApi.Features.Wrappers.Player)"/>, by connection id.
	/// </summary>
	internal Dictionary<int, bool>? Overrides { get; set; }

	private void OnDestroy() => MerVisibility.Unregister(this);
}
