using Interactables.Interobjects.DoorUtils;
using LabApi.Features.Wrappers;
using Mirror;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Mobile;
using UnityEngine;

namespace ProjectMER.Features.Serializable;

/// <summary>
/// A door of a map.
/// </summary>
/// <remarks>
/// Carl Mod has LCZ, HCZ and EZ breakable door prefabs; bulk doors and gates are skipped with a warning.
/// <see cref="RequiredPermissions"/> keeps its name but uses the fork's <see cref="KeycardPermissions"/> (ProjectMER's
/// <c>DoorPermissionFlags</c> names match, and <c>All</c> is read as every flag). Doors have no transform sync, so moving a
/// spawned door respawns it.
/// </remarks>
public class SerializableDoor : SerializableObject
{
	public DoorType DoorType { get; set; } = DoorType.Lcz;
	public bool IsOpen { get; set; } = false;
	public bool IsLocked { get; set; } = false;
	public KeycardPermissions RequiredPermissions { get; set; } = KeycardPermissions.None;
	public bool RequireAll { get; set; } = true;

	public override GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		DoorVariant doorVariant;
		Vector3 position = room.GetAbsolutePosition(Position);
		Quaternion rotation = room.GetAbsoluteRotation(Rotation);
		_prevIndex = Index;
		_prevType = DoorType;

		if (instance == null)
		{
			DoorVariant? prefab = DoorPrefab;
			if (prefab == null)
			{
				UnsupportedContent.Skip($"{DoorType} doors", "Carl Mod has no spawnable prefab for this door type");
				return null;
			}

			if (!Budget.CanCreate())
				return null;

			// DoorVariant.Start assigns byte ids from _serverDoorIdClock; past 255 they wrap and doors share ids.
			if (DoorVariant.AllDoors.Count >= byte.MaxValue)
				UnsupportedContent.Adapt("doors", "more than 255 doors exist, so door ids wrap and doors can share an id");

			doorVariant = UnityEngine.Object.Instantiate(prefab);
		}
		else
		{
			doorVariant = instance.GetComponent<DoorVariant>();
		}

		doorVariant.transform.SetPositionAndRotation(position, rotation);
		doorVariant.transform.localScale = Scale;

		SetupDoor(doorVariant);

		if (instance == null)
		{
			ToyFactory.Queue(ToyFactory.Track(doorVariant.gameObject, MerObjectKind.Door, Mobile.SpawnGroup.Current, false), SpawnPriority.Collidable);
		}
		else if (ToyFactory.IsSpawned(doorVariant.netIdentity))
		{
			// Doors have no transform sync: clients only read the transform when the door spawns.
			MerVisibility.Respawn(doorVariant.netIdentity, doorVariant.TryGetComponent(out Objects.MerBlockLink link) ? link.Group : null, MerObjectKind.Door);
		}

		return doorVariant.gameObject;
	}

	public void SetupDoor(DoorVariant doorVariant)
	{
		doorVariant.NetworkTargetState = IsOpen;
		doorVariant.ServerChangeLock(DoorLockReason.SpecialDoorFeature, IsLocked);
		doorVariant.RequiredPermissions = new DoorPermissions
		{
			RequiredPermissions = RequiredPermissions,
			RequireAll = RequireAll,
		};
	}

	private DoorVariant? DoorPrefab => DoorType switch
	{
		DoorType.Lcz => PrefabManager.DoorLcz,
		DoorType.Hcz => PrefabManager.DoorHcz,
		DoorType.Ez => PrefabManager.DoorEz,
		_ => null,
	};

	[YamlDotNet.Serialization.YamlIgnore]
	public override bool RequiresReloading => DoorType != _prevType || base.RequiresReloading;

	internal DoorType _prevType;
}
