using ProjectMER.Features.Objects;
using ProjectMER.Features.Serializable;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// A zone culling unit: everything loaded under one top-level <see cref="SpawnGroup"/> (a map load, or a schematic that
/// is not part of a map), split into cells by zone.
/// </summary>
/// <remarks>
/// <para>
/// The unit is culled when it holds at least <c>zone_culling_min_objects</c> networked objects. A unit that is known to be
/// that large when its first object spawns is culled for good. Any other unit is culled provisionally: its objects only
/// reach players in their own zone until the load has finished (nothing queued, every schematic built, no new object for
/// a poll interval). A unit that is still small then is shown in every zone, which only adds objects for the other
/// zones' players; nobody receives objects that are hidden again.
/// </para>
/// <para>
/// A settled unit stays culled until it is empty. A settled small unit that grows past the threshold (tool-gun edits)
/// becomes culled, the only case that hides objects players already have.
/// </para>
/// </remarks>
internal sealed class VisibilityRoot
{
	public VisibilityRoot(SpawnGroup group, int expected)
	{
		Group = group;
		Expected = expected;
	}

	/// <summary>
	/// Gets the top-level spawn group.
	/// </summary>
	public SpawnGroup Group { get; }

	/// <summary>
	/// Gets the number of networked objects the load had created when its first object spawned.
	/// </summary>
	public int Expected { get; }

	/// <summary>
	/// Gets or sets the map the group belongs to, if any.
	/// </summary>
	public MapSchematic? Map { get; set; }

	/// <summary>
	/// Gets or sets the schematic the group belongs to when it is not part of a map.
	/// </summary>
	public SchematicObject? Schematic { get; set; }

	/// <summary>
	/// Gets or sets the number of registered objects.
	/// </summary>
	public int Count { get; set; }

	/// <summary>
	/// Gets or sets when the last object was registered (<see cref="UnityEngine.Time.unscaledTime"/>).
	/// </summary>
	public float LastAdded { get; set; }

	/// <summary>
	/// Gets or sets whether the unit is zone culled (provisionally until <see cref="Settled"/>).
	/// </summary>
	public bool Active { get; set; }

	/// <summary>
	/// Gets or sets whether the culling decision is final.
	/// </summary>
	public bool Settled { get; set; }

	/// <summary>
	/// Gets the registered objects by zone index (lists are created on demand).
	/// </summary>
	public List<VisibilityEntry>?[] Cells { get; } = new List<VisibilityEntry>?[MerVisibility.ZoneCount];
}
