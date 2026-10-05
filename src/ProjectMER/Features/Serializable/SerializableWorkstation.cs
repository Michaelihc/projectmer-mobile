using InventorySystem.Items.Firearms.Attachments;
using LabApi.Features.Wrappers;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Mobile;
using UnityEngine;

namespace ProjectMER.Features.Serializable;

/// <summary>
/// A workstation of a map. Carl Mod syncs structures as position plus yaw quantized to 5.625°.
/// </summary>
public class SerializableWorkstation : SerializableObject
{
	/// <summary>
	/// Gets or sets a value indicating whether the player can interact with the workstation.
	/// </summary>
	public bool IsInteractable { get; set; } = true;

	public override GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		if (instance == null && (PrefabManager.Workstation == null || !Budget.CanCreate()))
			return null;

		WorkstationController workstation = instance == null ? UnityEngine.Object.Instantiate(PrefabManager.Workstation) : instance.GetComponent<WorkstationController>();
		Vector3 position = room.GetAbsolutePosition(Position);
		Quaternion rotation = room.GetAbsoluteRotation(Rotation);
		_prevIndex = Index;

		workstation.transform.SetPositionAndRotation(position, rotation);
		workstation.transform.localScale = Scale;

		workstation.NetworkStatus = (byte)(IsInteractable ? 0 : 4);
		ToyFactory.SyncStructure(workstation.gameObject);

		if (instance == null)
			ToyFactory.Queue(ToyFactory.Track(workstation.gameObject, MerObjectKind.Structure, Mobile.SpawnGroup.Current, false), SpawnPriority.Collidable);
		else if (ToyFactory.IsSpawned(workstation.netIdentity))
			ToyFactory.Resend(workstation.netIdentity);

		return workstation.gameObject;
	}
}
