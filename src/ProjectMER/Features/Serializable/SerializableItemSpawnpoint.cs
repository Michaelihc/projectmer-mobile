using InventorySystem.Items.Firearms.Attachments;
using LabApi.Features.Wrappers;
using ProjectMER.Events.Handlers.Internal;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Interfaces;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.Objects;
using UnityEngine;

namespace ProjectMER.Features.Serializable;

/// <summary>
/// An item spawnpoint of a map.
/// </summary>
/// <remarks>
/// The spawnpoint is a server-only GameObject; its pickups are separate root-level objects (Carl Mod clients have no
/// parenting) tracked by <see cref="SpawnedPickups"/>. ProjectMER's default item <c>Lantern</c> does not exist in Carl Mod
/// and reads as <see cref="ItemType.Flashlight"/>; other items missing from the fork are skipped with a warning.
/// </remarks>
public class SerializableItemSpawnpoint : SerializableObject, IIndicatorDefinition
{
	public ItemType ItemType { get; set; } = ItemType.Flashlight;
	public float Weight { get; set; } = -1;
	public string AttachmentsCode { get; set; } = "-1";
	public uint NumberOfItems { get; set; } = 1;
	public int NumberOfUses { get; set; } = 1;
	public bool UseGravity { get; set; } = true;
	public bool CanBePickedUp { get; set; } = true;

	public override GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		GameObject itemSpawnPoint = instance ?? new GameObject("ItemSpawnpoint");
		Vector3 position = room.GetAbsolutePosition(Position);
		Quaternion rotation = room.GetAbsoluteRotation(Rotation);
		_prevIndex = Index;

		itemSpawnPoint.transform.SetPositionAndRotation(position, rotation);
		if (!itemSpawnPoint.TryGetComponent(out SpawnedPickups spawned))
			spawned = itemSpawnPoint.AddComponent<SpawnedPickups>();

		spawned.DestroyAll();

		if (!Enum.IsDefined(typeof(ItemType), ItemType) || ItemType == ItemType.None)
		{
			UnsupportedContent.Skip("item spawnpoints", $"item \"{GameEnumConverter.GetUnknownName(ItemType) ?? ItemType.ToString()}\" does not exist in Carl Mod");
			return itemSpawnPoint;
		}

		for (int i = 0; i < NumberOfItems; i++)
		{
			if (!Budget.CanCreate())
				break;

			Pickup? pickup = Pickup.Create(ItemType, position, rotation, Scale, networkSpawn: false);
			if (pickup == null)
			{
				UnsupportedContent.Skip("item spawnpoints", $"item {ItemType} has no pickup");
				break;
			}

			if (Weight != -1)
				pickup.Weight = Weight;

			if (pickup.Rigidbody != null)
				pickup.Rigidbody.isKinematic = !UseGravity;

			pickup.IsLocked = !CanBePickedUp;
			PickupEventsHandler.PickupUsesLeft[pickup.Serial] = NumberOfUses;

			MerBlockLink link = ToyFactory.Track(pickup.GameObject, MerObjectKind.Pickup, Mobile.SpawnGroup.Current, false);
			spawned.Pickups.Add(pickup);

			Action<GameObject>? onSpawned = null;
			if (pickup is FirearmPickup firearmPickup)
			{
				string attachmentsCode = AttachmentsCode;
				onSpawned = _ =>
				{
					// The fork's firearm pickups have no modules: ammo and attachments live in FirearmStatus.
					firearmPickup.Base.OnDistributed();
					firearmPickup.AttachmentCode = uint.TryParse(attachmentsCode, out uint code) ? code : AttachmentsUtils.GetRandomAttachmentsCode(firearmPickup.Type);
				};
			}

			ToyFactory.Queue(link, SpawnPriority.Pickup, onSpawned: onSpawned);
		}

		return itemSpawnPoint.gameObject;
	}

	public GameObject SpawnOrUpdateIndicator(Room room, GameObject? instance = null)
	{
		Vector3 position = room.GetAbsolutePosition(Position);
		Quaternion rotation = room.GetAbsoluteRotation(Rotation);

		GameObject root = IndicatorObject.CreateRoot(instance, "Indicator", position, rotation);
		IndicatorObject.SetPart(root, 0, PrimitiveType.Cube, position, rotation, Vector3.one * 0.25f, new Color(0f, 1f, 0f, 0.9f));
		return root;
	}

	/// <summary>
	/// The pickups of a spawnpoint (no longer its children). Destroys the ones still on the ground with it.
	/// </summary>
	public sealed class SpawnedPickups : MonoBehaviour
	{
		public List<Pickup> Pickups { get; } = [];

		public void DestroyAll()
		{
			foreach (Pickup pickup in Pickups)
			{
				if (pickup.IsDestroyed)
					continue;

				PickupEventsHandler.PickupUsesLeft.Remove(pickup.Serial);
				SpawnQueue.Destroy(pickup.GameObject);
			}

			Pickups.Clear();
		}

		private void OnDestroy() => DestroyAll();
	}
}
