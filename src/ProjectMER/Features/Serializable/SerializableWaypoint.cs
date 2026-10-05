using LabApi.Features.Wrappers;
using UnityEngine;

namespace ProjectMER.Features.Serializable;

/// <summary>
/// A waypoint of a map. Data only: the Carl Mod client has no WaypointToy, so it is skipped with a warning and kept in
/// saved maps.
/// </summary>
public class SerializableWaypoint : SerializableObject
{
	public const float ScaleMultiplier = 1 / 256f;

	public override GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		_prevIndex = Index;
		UnsupportedContent.Skip("waypoints", "WaypointToy is not available in the Carl Mod client");
		return null;
	}
}
