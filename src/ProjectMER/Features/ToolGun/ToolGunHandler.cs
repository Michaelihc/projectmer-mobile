using AdminToys;
using LabApi.Features.Wrappers;
using MapGeneration;
using ProjectMER.Configs;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Objects;
using ProjectMER.Features.Serializable;
using ProjectMER.Features.Serializable.Schematics;
using UnityEngine;

namespace ProjectMER.Features.ToolGun;

public static class ToolGunHandler
{
	private const float MaxDistance = 100f;

	private static readonly RaycastHit[] Hits = new RaycastHit[32];

	public static Dictionary<Player, MapEditorObject> PlayerSelectedObjectDict { get; private set; } = [];

	public static void CreateObject(Player player, ToolGunObjectType objectType, string schematicName = "")
	{
		if (!Raycast(player, out RaycastHit hit))
			return;

		if (!CreateObject(hit.point, objectType, schematicName))
			return;

		if (Config.AutoSelect)
			SelectObject(player, MapUtils.UntitledMap.SpawnedObjects.LastOrDefault());
	}

	/// <summary>
	/// Creates an object in the Untitled map.
	/// </summary>
	/// <returns><see langword="false"/> when the type is not available in Carl Mod or nothing was spawned.</returns>
	public static bool CreateObject(Vector3 position, ToolGunObjectType objectType, string schematicName = "")
	{
		if (!ToolGunItem.TypesDictionary.TryGetValue(objectType, out Type serializableType))
		{
			Logger.Warn($"{objectType} objects cannot be created: {ToolGunItem.GetUnsupportedReason(objectType) ?? "unknown type"}.");
			return false;
		}

		if (objectType == ToolGunObjectType.Schematic && string.IsNullOrEmpty(schematicName))
		{
			Logger.Warn("No schematic chosen to create; pick one with mp tg schematic <name|index>.");
			return false;
		}

		Room room = RoomExtensions.GetRoomAtPosition(position);

		position = room.Name == RoomName.Outside ? position : room.Transform.InverseTransformPoint(position);
		string roomId = room.GetRoomStringId();

		MapSchematic map = MapUtils.UntitledMap;
		string id = Guid.NewGuid().ToString("N").Substring(0, 8);

		SerializableObject serializableObject = (SerializableObject)Activator.CreateInstance(serializableType);
		serializableObject.Room = roomId;
		serializableObject.Index = room.GetRoomIndex();

		switch (serializableObject)
		{
			case SerializablePrimitive serializablePrimitive when objectType == ToolGunObjectType.Quad:
				{
					serializableObject.Position = position;
					serializablePrimitive.PrimitiveType = PrimitiveType.Quad;
					serializablePrimitive.PrimitiveFlags = PrimitiveFlags.Visible;
					break;
				}

			case SerializablePlayerSpawnpoint _:
				{
					serializableObject.Position = position + Vector3.up * 0.01f;
					break;
				}

			case SerializableTeleport _:
				{
					serializableObject.Position = position + Vector3.up;
					break;
				}

			case SerializableSchematic serializableSchematic:
				{
					serializableObject.Position = position;
					serializableSchematic.SchematicName = schematicName!;
					break;
				}

			default:
				serializableObject.Position = position;
				break;
		}

		int before = map.SpawnedObjects.Count;
		bool wasDirty = map.IsDirty;
		if (!map.TryAddElement(id, serializableObject))
			return false;

		map.SpawnObject(id, serializableObject);
		if (map.SpawnedObjects.Count == before)
		{
			// Nothing spawned (unknown schematic, budget or door limit, no matching room): keep no entry that a save would
			// write to the map file.
			map.TryRemoveElement(id);
			map.IsDirty = wasDirty;
			return false;
		}

		foreach (MapEditorObject mapEditorObject in map.SpawnedObjects)
		{
			if (mapEditorObject.Id != id)
				continue;

			IndicatorObject.TrySpawnOrUpdateIndicator(mapEditorObject);
		}

		// Give a new non-collidable primitive its editing trigger soon.
		EditingColliders.MarkDirty();
		return true;
	}

	public static void DeleteObject(MapEditorObject mapEditorObject)
	{
		// End grabs of the object and clear selections that point at it, so no box or grab outlives it.
		foreach (ToolGunState state in ToolGunState.Values)
		{
			if (state.Grab != null && ReferenceEquals(state.Grab.Target, mapEditorObject))
				state.Grab.End(commit: false);
		}

		foreach (KeyValuePair<Player, MapEditorObject> pair in PlayerSelectedObjectDict)
		{
			if (ReferenceEquals(pair.Value, mapEditorObject) && ToolGunState.TryGet(pair.Key, out ToolGunState state))
				state.Box.Destroy();
		}

		IndicatorObject.TryDestroyIndicator(mapEditorObject);

		if (!MapUtils.LoadedMaps.TryGetValue(mapEditorObject.MapName, out MapSchematic map))
		{
			// Its map is no longer loaded; there is no entry to remove.
			mapEditorObject.Destroy();
			return;
		}

		if (map.TryRemoveElement(mapEditorObject.Id))
			map.DestroyObject(mapEditorObject.Id);
	}

	/// <summary>
	/// Gets the map object the player is looking at.
	/// </summary>
	/// <remarks>
	/// Networked blocks are not children of their schematic, so the hit is resolved through <see cref="MerBlockLink"/>
	/// before falling back to ProjectMER's parent search (server-only objects such as indicators and teleports).
	/// </remarks>
	public static bool TryGetMapObject(Player player, out MapEditorObject mapEditorObject)
	{
		mapEditorObject = null!;
		if (!Raycast(player, out RaycastHit hit))
			return false;

		return TryResolve(hit.transform, out mapEditorObject);
	}

	/// <summary>
	/// Resolves a hit transform to the map object that owns it.
	/// </summary>
	public static bool TryResolve(Transform transform, out MapEditorObject mapEditorObject)
	{
		mapEditorObject = null!;
		if (transform == null)
			return false;

		if (transform.TryGetComponentInParent(out MerBlockLink link))
			mapEditorObject = link.ResolveOwner()!;

		if (mapEditorObject == null && !transform.TryGetComponentInParent(out mapEditorObject))
			return false;

		if (mapEditorObject is IndicatorObject indicatorObject && IndicatorObject.Dictionary.TryGetValue(indicatorObject, out MapEditorObject owner))
			mapEditorObject = owner;

		return mapEditorObject != null && mapEditorObject is not IndicatorObject;
	}

	public static bool TryGetSelectedMapObject(Player player, out MapEditorObject mapEditorObject)
	{
		if (!PlayerSelectedObjectDict.ContainsKey(player))
		{
			mapEditorObject = null!;
			return false;
		}

		return PlayerSelectedObjectDict.TryGetValue(player, out mapEditorObject) && mapEditorObject != null;
	}

	/// <summary>
	/// Selects an object for a player (<see langword="null"/> deselects) and updates the player's selection box.
	/// </summary>
	public static void SelectObject(Player player, MapEditorObject mapEditorObject)
	{
		PlayerSelectedObjectDict[player] = mapEditorObject;

		if (player is null || player.IsHost)
			return;

		ToolGunState state = ToolGunState.Get(player);
		if (state.Grab != null && !ReferenceEquals(state.Grab.Target, mapEditorObject))
			state.Grab.End(commit: true);

		state.Box.Sync(state);
		ToolGunHud.Refresh(state);
	}

	public static bool TryGetObjectById(string id, out MapEditorObject mapEditorObject)
	{
		foreach (MapSchematic map in MapUtils.LoadedMaps.Values)
		{
			foreach (MapEditorObject meo in map.SpawnedObjects)
			{
				if (meo != null && meo.Id == id)
				{
					mapEditorObject = meo;
					return true;
				}
			}
		}

		mapEditorObject = null!;
		return false;
	}

	public static bool Raycast(Player player, out RaycastHit hit)
	{
		Transform camera = player.Camera;
		if (camera == null)
		{
			// The host and players without a camera (no role yet) cannot aim.
			hit = default;
			return false;
		}

		return Raycast(camera.position, camera.forward, out hit);
	}

	/// <summary>
	/// Casts the tool gun ray: the nearest solid surface, or a nearer MER trigger (indicators, teleports and the editing
	/// triggers of non-collidable primitives, which are on <see cref="EditingColliders.Layer"/>). Other triggers on the tool
	/// gun layers are ignored.
	/// </summary>
	public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit)
	{
		int mask = ToolGunMask.Mask;
		bool found = Physics.Raycast(origin, direction, out hit, MaxDistance, mask, QueryTriggerInteraction.Ignore);
		float limit = found ? hit.distance : MaxDistance;

		int count = Physics.RaycastNonAlloc(origin, direction, Hits, limit, mask | (1 << EditingColliders.Layer), QueryTriggerInteraction.Collide);
		for (int i = 0; i < count; i++)
		{
			RaycastHit candidate = Hits[i];
			Collider collider = candidate.collider;
			if (collider == null || !collider.isTrigger || (found && candidate.distance >= hit.distance) || !IsMerTrigger(collider))
				continue;

			hit = candidate;
			found = true;
		}

		return found;
	}

	private static bool IsMerTrigger(Collider collider)
	{
		Transform transform = collider.transform;
		return transform.TryGetComponentInParent(out MerBlockLink _) || transform.TryGetComponentInParent(out MapEditorObject _);
	}

	private static readonly CachedLayerMask ToolGunMask = new("Default", "Door", "CCTV");

	private static Config Config => ProjectMER.Singleton.Config!;
}
