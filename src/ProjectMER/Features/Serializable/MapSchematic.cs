using LabApi.Features.Wrappers;
using NorthwoodLib.Pools;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.Objects;
using ProjectMER.Features.Serializable.Lockers;
using ProjectMER.Features.Serializable.Schematics;
using UnityEngine;
using Utils.NonAllocLINQ;

namespace ProjectMER.Features.Serializable;

/// <summary>
/// A map: every object dictionary of ProjectMER's format, including the types the Carl Mod client cannot show, so a
/// load followed by a save keeps them.
/// </summary>
public class MapSchematic
{
	public MapSchematic() { }

	public MapSchematic(string mapName)
	{
		Name = mapName;
	}

	public string Name;

	public bool IsDirty;

	public Dictionary<string, SerializablePrimitive> Primitives { get; set; } = [];

	public Dictionary<string, SerializableTriangle> Triangles { get; set; } = [];

	public Dictionary<string, SerializableLight> Lights { get; set; } = [];

	public Dictionary<string, SerializableDoor> Doors { get; set; } = [];

	public Dictionary<string, SerializableWorkstation> Workstations { get; set; } = [];

	public Dictionary<string, SerializableItemSpawnpoint> ItemSpawnpoints { get; set; } = [];

	public Dictionary<string, SerializablePlayerSpawnpoint> PlayerSpawnpoints { get; set; } = [];

	public Dictionary<string, SerializableCapybara> Capybaras { get; set; } = [];

	public Dictionary<string, SerializableText> Texts { get; set; } = [];

	public Dictionary<string, SerializableInteractable> Interactables { get; set; } = [];

	public Dictionary<string, SerializableScp079Camera> Scp079Cameras { get; set; } = [];

	public Dictionary<string, SerializableShootingTarget> ShootingTargets { get; set; } = [];

	public Dictionary<string, SerializableSchematic> Schematics { get; set; } = [];

	public Dictionary<string, SerializableTeleport> Teleports { get; set; } = [];

	public Dictionary<string, SerializableLocker> Lockers { get; set; } = [];

	public Dictionary<string, SerializableWaypoint> Waypoints { get; set; } = [];

	public List<MapEditorObject> SpawnedObjects = [];

	/// <summary>
	/// The spawn group of the current load. Cancelled on unload and reload.
	/// </summary>
	public SpawnGroup? SpawnGroup;

	public MapSchematic Merge(MapSchematic other)
	{
		Primitives.AddRange(other.Primitives);
		Triangles.AddRange(other.Triangles);
		Lights.AddRange(other.Lights);
		Doors.AddRange(other.Doors);
		Workstations.AddRange(other.Workstations);
		ItemSpawnpoints.AddRange(other.ItemSpawnpoints);
		PlayerSpawnpoints.AddRange(other.PlayerSpawnpoints);
		Capybaras.AddRange(other.Capybaras);
		Texts.AddRange(other.Texts);
		Interactables.AddRange(other.Interactables);
		Schematics.AddRange(other.Schematics);
		Scp079Cameras.AddRange(other.Scp079Cameras);
		ShootingTargets.AddRange(other.ShootingTargets);
		Teleports.AddRange(other.Teleports);
		Lockers.AddRange(other.Lockers);
		Waypoints.AddRange(other.Waypoints);

		return this;
	}

	/// <summary>
	/// Destroys every spawned object and cancels the objects still waiting in the spawn queue.
	/// </summary>
	public void DestroyAll()
	{
		SpawnGroup?.Cancel();
		SpawnGroup = null;

		foreach (MapEditorObject mapEditorObject in SpawnedObjects)
		{
			if (mapEditorObject != null)
				mapEditorObject.Destroy();
		}

		SpawnedObjects.Clear();
	}

	public void Reload()
	{
		DestroyAll();
		SpawnGroup = new SpawnGroup($"map {Name}");

		using (UnsupportedContent.Begin($"Map \"{Name}\""))
		{
			HashSet<string> admittedLights = AdmitLights();

			Primitives.ForEach(kVP => SpawnObject(kVP.Key, kVP.Value));
			Triangles.ForEach(kVP => SpawnObject(kVP.Key, kVP.Value));
			Lights.ForEach(kVP =>
			{
				if (admittedLights.Contains(kVP.Key))
					SpawnObject(kVP.Key, kVP.Value);
			});
			Doors.ForEach(kVP =>
			{
				Door? vanillaDoor = Door.Get(kVP.Key);
				if (vanillaDoor != null)
				{
					kVP.Value.SetupDoor(vanillaDoor.Base);
					return;
				}

				SpawnObject(kVP.Key, kVP.Value);
			});
			Workstations.ForEach(kVP => SpawnObject(kVP.Key, kVP.Value));
			ItemSpawnpoints.ForEach(kVP => SpawnObject(kVP.Key, kVP.Value));
			PlayerSpawnpoints.ForEach(kVP => SpawnObject(kVP.Key, kVP.Value));
			Capybaras.ForEach(kVP => SpawnObject(kVP.Key, kVP.Value));
			Texts.ForEach(kVP => SpawnObject(kVP.Key, kVP.Value));
			Interactables.ForEach(kVP => SpawnObject(kVP.Key, kVP.Value));
			Schematics.ForEach(kVP => SpawnObject(kVP.Key, kVP.Value));
			Scp079Cameras.ForEach(kVP => SpawnObject(kVP.Key, kVP.Value));
			ShootingTargets.ForEach(kVP => SpawnObject(kVP.Key, kVP.Value));
			Teleports.ForEach(kVP => SpawnObject(kVP.Key, kVP.Value));
			Lockers.ForEach(kVP =>
			{
				kVP.Value._prevType = kVP.Value.LockerType;
				SpawnObject(kVP.Key, kVP.Value);
			});
			Waypoints.ForEach(kVP => SpawnObject(kVP.Key, kVP.Value));
		}

		int transparent = 0;
		foreach (MapEditorObject mapEditorObject in SpawnedObjects)
		{
			if (mapEditorObject != null && mapEditorObject.TryGetComponent(out MerBlockLink link) && link.IsTransparent)
				transparent++;
		}

		if (transparent > SchematicObject.TransparentWarnThreshold)
			Logger.Warn($"Map \"{Name}\" has {transparent} transparent primitives (alpha below 1 or invisible colliders). Each one still renders with blending, which costs fill rate on phones.");

		SpawnGroup.CheckCompleted();
	}

	public void SpawnObject<T>(string id, T serializableObject) where T : SerializableObject
	{
		SpawnGroup ??= new SpawnGroup($"map {Name}");
		SpawnGroup? previous = SpawnGroup.Current;
		SpawnGroup.Current = SpawnGroup;

		List<Room> rooms = serializableObject.GetRooms();
		try
		{
			foreach (Room room in rooms)
			{
				if (serializableObject.Index < 0 || serializableObject.Index == room.GetRoomIndex())
				{
					GameObject? gameObject = serializableObject.SpawnOrUpdateObject(room);
					if (gameObject == null)
						continue;

					MapEditorObject mapEditorObject = gameObject.AddComponent<MapEditorObject>().Init(serializableObject, Name, id, room);
					SpawnedObjects.Add(mapEditorObject);
				}
			}
		}
		finally
		{
			SpawnGroup.Current = previous;
			ListPool<Room>.Shared.Return(rooms);
		}
	}

	public void DestroyObject(string id)
	{
		foreach (MapEditorObject mapEditorObject in SpawnedObjects.ToList())
		{
			if (mapEditorObject.Id != id)
				continue;

			SpawnedObjects.Remove(mapEditorObject);
			mapEditorObject.Destroy();
		}
	}

	public bool TryAddElement<T>(string id, T serializableObject) where T : SerializableObject
	{
		bool dirtyPrevValue = IsDirty;
		IsDirty = true;

		if (Primitives.TryAdd(id, serializableObject))
			return true;

		if (Triangles.TryAdd(id, serializableObject))
			return true;

		if (Lights.TryAdd(id, serializableObject))
			return true;

		if (Doors.TryAdd(id, serializableObject))
			return true;

		if (Workstations.TryAdd(id, serializableObject))
			return true;

		if (ItemSpawnpoints.TryAdd(id, serializableObject))
			return true;

		if (PlayerSpawnpoints.TryAdd(id, serializableObject))
			return true;

		if (Capybaras.TryAdd(id, serializableObject))
			return true;

		if (Texts.TryAdd(id, serializableObject))
			return true;

		if (Interactables.TryAdd(id, serializableObject))
			return true;

		if (Schematics.TryAdd(id, serializableObject))
			return true;

		if (Scp079Cameras.TryAdd(id, serializableObject))
			return true;

		if (ShootingTargets.TryAdd(id, serializableObject))
			return true;

		if (Teleports.TryAdd(id, serializableObject))
			return true;

		if (Lockers.TryAdd(id, serializableObject))
			return true;

		if (Waypoints.TryAdd(id, serializableObject))
			return true;

		IsDirty = dirtyPrevValue;
		return false;
	}

	public bool TryRemoveElement(string id)
	{
		bool dirtyPrevValue = IsDirty;
		IsDirty = true;

		if (Primitives.Remove(id))
			return true;

		if (Triangles.Remove(id))
			return true;

		if (Lights.Remove(id))
			return true;

		if (Doors.Remove(id))
			return true;

		if (Workstations.Remove(id))
			return true;

		if (ItemSpawnpoints.Remove(id))
			return true;

		if (PlayerSpawnpoints.Remove(id))
			return true;

		if (Capybaras.Remove(id))
			return true;

		if (Texts.Remove(id))
			return true;

		if (Interactables.Remove(id))
			return true;

		if (Schematics.Remove(id))
			return true;

		if (Scp079Cameras.Remove(id))
			return true;

		if (ShootingTargets.Remove(id))
			return true;

		if (Teleports.Remove(id))
			return true;

		if (Lockers.Remove(id))
			return true;

		if (Waypoints.Remove(id))
			return true;

		IsDirty = dirtyPrevValue;
		return false;
	}

	/// <summary>
	/// Applies <c>max_lights</c> to this map's lights, strongest (intensity × range) first.
	/// </summary>
	private HashSet<string> AdmitLights()
	{
		List<string> ids = [];
		List<float> strengths = [];
		foreach (KeyValuePair<string, SerializableLight> pair in Lights)
		{
			ids.Add(pair.Key);
			strengths.Add(pair.Value.Intensity * pair.Value.Range);
		}

		bool[] admitted = Budget.AdmitLights(strengths);
		HashSet<string> result = [];
		for (int i = 0; i < ids.Count; i++)
		{
			if (admitted[i])
				result.Add(ids[i]);
		}

		return result;
	}
}
