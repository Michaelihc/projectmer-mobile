using LabApi.Features.Wrappers;
using MEC;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.Serializable;
using ProjectMER.Features.ToolGun;
using UnityEngine;

namespace ProjectMER.Features.Objects;

public class MapEditorObject : MonoBehaviour
{
	public SerializableObject Base;

	public string MapName { get; protected set; }

	public string Id { get; protected set; }

	public Room Room { get; protected set; }

	public MapSchematic Map => MapUtils.LoadedMaps[MapName];

	public MapEditorObject Init(SerializableObject serializableObject, string mapName, string id, Room room)
	{
		Base = serializableObject;
		MapName = mapName;
		Id = id;
		Room = room;

		if (TryGetComponent(out MerBlockLink link))
			link.Owner = this;

		return this;
	}

	/// <summary>
	/// Applies edits of <see cref="Base"/> to every spawned copy of the object.
	/// </summary>
	/// <remarks>
	/// Copies are updated in place: static toys are resent with their new transform instead of being destroyed and spawned
	/// again. Changes that need a new object (room index, door/locker/target type, primitive flags that switch between
	/// networked and not) still respawn it.
	/// </remarks>
	public virtual void UpdateObjectAndCopies()
	{
		Map.IsDirty = true;

		if (Base.RequiresReloading)
		{
			Base._prevIndex = Base.Index;
			Player? player = ToolGunHandler.PlayerSelectedObjectDict.FirstOrDefault(x => x.Value != null && x.Value.Id == Id).Key;

			if (MapUtils.LoadedMaps.TryGetValue(MapName, out MapSchematic map))
			{
				map.DestroyObject(Id);
			}

			Timing.CallDelayed(0.1f, () =>
			{
				map.SpawnObject(Id, Base);

				if (player is not null)
					ToolGunHandler.SelectObject(player, Map.SpawnedObjects.Find(x => x.Id == Id));
			});

			return;
		}

		foreach (MapEditorObject copy in Map.SpawnedObjects.ToList())
		{
			if (copy.Id != Id)
				continue;

			copy.UpdateCopy();
		}
	}

	private void UpdateCopy()
	{
		IndicatorObject.TrySpawnOrUpdateIndicator(this);
		Base.SpawnOrUpdateObject(Room, gameObject);
	}

	/// <summary>
	/// Destroys the object. Networked objects are destroyed through the spawn queue (still-queued ones are dropped).
	/// </summary>
	public void Destroy()
	{
		IndicatorObject.TryDestroyIndicator(this);
		SpawnQueue.Destroy(gameObject);
	}
}
