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
		if (map.TryAddElement(id, serializableObject))
			map.SpawnObject(id, serializableObject);

		foreach (MapEditorObject mapEditorObject in map.SpawnedObjects)
		{
			if (mapEditorObject.Id != id)
				continue;

			IndicatorObject.TrySpawnOrUpdateIndicator(mapEditorObject);
		}

		return map.SpawnedObjects.Count > before;
	}

	public static void DeleteObject(MapEditorObject mapEditorObject)
	{
		IndicatorObject.TryDestroyIndicator(mapEditorObject);

		MapSchematic map = MapUtils.LoadedMaps[mapEditorObject.MapName];
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

		if (hit.transform.TryGetComponentInParent(out MerBlockLink link))
			mapEditorObject = link.ResolveOwner()!;

		if (mapEditorObject == null && !hit.transform.TryGetComponentInParent(out mapEditorObject))
			return false;

		if (mapEditorObject is IndicatorObject indicatorObject)
			mapEditorObject = IndicatorObject.Dictionary[indicatorObject];

		return mapEditorObject != null;
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

	public static void SelectObject(Player player, MapEditorObject mapEditorObject)
	{
		if (!PlayerSelectedObjectDict.ContainsKey(player))
		{
			PlayerSelectedObjectDict.Add(player, mapEditorObject);
			return;
		}

		PlayerSelectedObjectDict[player] = mapEditorObject;
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

	public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit) => Physics.Raycast(origin, direction, out hit, 100f, ToolGunMask.Mask);

	private static readonly CachedLayerMask ToolGunMask = new("Default", "Door", "CCTV");

	private static Config Config => ProjectMER.Singleton.Config!;
}
