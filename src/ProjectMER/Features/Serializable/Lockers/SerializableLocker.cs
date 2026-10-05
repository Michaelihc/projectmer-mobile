using InventorySystem.Items.Pickups;
using MapGeneration.Distributors;
using MEC;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Mobile;
using UnityEngine;
using YamlDotNet.Serialization;
using LabApiLocker = LabApi.Features.Wrappers.Locker;
using LapApiLockerChamber = LabApi.Features.Wrappers.LockerChamber;
using Room = LabApi.Features.Wrappers.Room;

namespace ProjectMER.Features.Serializable.Lockers;

/// <summary>
/// A locker of a map.
/// </summary>
/// <remarks>
/// 12 of ProjectMER's 16 locker types exist in Carl Mod; SCP-1576, Anti-SCP-207 and SCP-1344 pedestals and the
/// experimental weapon locker are skipped with a warning (and kept in saved maps). Chamber permissions use
/// <c>KeycardPermissions</c>. Structures sync position and yaw only.
/// </remarks>
public class SerializableLocker : SerializableObject
{
	public LockerType LockerType { get; set; } = LockerType.PedestalScp500;

	public List<SerializableLockerLoot> Loot { get; set; } = [];

	public List<SerializableLockerChamber> Chambers { get; set; } = [];

	public override GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		Locker? prefab = LockerPrefab;
		if (instance == null && prefab == null)
		{
			UnsupportedContent.Skip($"{LockerType} lockers", "Carl Mod has no prefab for this locker type");
			return null;
		}

		if (instance == null && !Budget.CanCreate())
			return null;

		Locker locker = instance == null ? UnityEngine.Object.Instantiate(prefab) : instance.GetComponent<Locker>();
		Vector3 position = room.GetAbsolutePosition(Position);
		Quaternion rotation = room.GetAbsoluteRotation(Rotation);
		_prevIndex = Index;

		locker.transform.SetPositionAndRotation(position, rotation);
		locker.transform.localScale = Scale;
		ToyFactory.SyncStructure(locker.gameObject);

		LabApiLocker labApiLocker = LabApiLocker.Get(locker)!;
		if (LockerType != _prevType)
			SetDefaultSettings(labApiLocker);

		labApiLocker.ClearLockerLoot();
		foreach (SerializableLockerLoot loot in Loot)
		{
			labApiLocker.AddLockerLoot(loot.TargetItem, loot.RemainingUses, loot.ProbabilityPoints, loot.MinPerChamber, loot.MaxPerChamber);
		}

		int i = 0;
		labApiLocker.ClearAllChambers();
		foreach (LapApiLockerChamber chamber in labApiLocker.Chambers)
		{
			if (i > Chambers.Count - 1)
				break;

			chamber.AcceptableItems = Chambers[i].AcceptableItems.ToArray();
			chamber.RequiredPermissions = Chambers[i].RequiredPermissions;
			i++;
		}

		_prevType = LockerType;

		void AfterSpawn(GameObject _)
		{
			Timing.CallDelayed(0.25f, () =>
			{
				if (locker == null)
					return;

				foreach (ItemPickupBase itemPickupBase in locker.GetComponentsInChildren<ItemPickupBase>())
				{
					if (itemPickupBase.TryGetComponent(out Rigidbody rigidbody))
						rigidbody.isKinematic = false;
				}

				int i = 0;
				foreach (LapApiLockerChamber chamber in labApiLocker.Chambers)
				{
					if (i > Chambers.Count - 1)
						break;

					chamber.IsOpen = Chambers[i].IsOpen;
					i++;
				}
			});
		}

		if (instance == null)
		{
			ToyFactory.Queue(ToyFactory.Track(locker.gameObject, MerObjectKind.Structure, Mobile.SpawnGroup.Current, false), SpawnPriority.Collidable, onSpawned: AfterSpawn);
		}
		else if (ToyFactory.IsSpawned(locker.netIdentity))
		{
			ToyFactory.Resend(locker.netIdentity);
			AfterSpawn(locker.gameObject);
		}

		return locker.gameObject;
	}

	private void SetDefaultSettings(LabApiLocker labApiLocker)
	{
		Loot.Clear();
		Chambers.Clear();

		foreach (LockerLoot loot in labApiLocker.Loot)
		{
			Loot.Add(new SerializableLockerLoot(loot.TargetItem, loot.RemainingUses, loot.MaxPerChamber, loot.ProbabilityPoints, loot.MinPerChamber));
		}

		foreach (LapApiLockerChamber chamber in labApiLocker.Chambers)
		{
			Chambers.Add(new SerializableLockerChamber(chamber.AcceptableItems, chamber.IsOpen, chamber.RequiredPermissions));
		}
	}

	private Locker? LockerPrefab => LockerType switch
	{
		LockerType.PedestalScp500 => PrefabManager.PedestalScp500,
		LockerType.LargeGun => PrefabManager.LockerLargeGun,
		LockerType.RifleRack => PrefabManager.LockerRifleRack,
		LockerType.Misc => PrefabManager.LockerMisc,
		LockerType.Medkit => PrefabManager.LockerRegularMedkit,
		LockerType.Adrenaline => PrefabManager.LockerAdrenalineMedkit,
		LockerType.PedestalScp018 => PrefabManager.PedestalScp018,
		LockerType.PedestalScp207 => PrefabManager.PedstalScp207,
		LockerType.PedestalScp244 => PrefabManager.PedestalScp244,
		LockerType.PedestalScp268 => PrefabManager.PedestalScp268,
		LockerType.PedestalScp1853 => PrefabManager.PedstalScp1853,
		LockerType.PedestalScp2176 => PrefabManager.PedestalScp2176,
		_ => null,
	};

	[YamlIgnore]
	public override bool RequiresReloading => true;

	internal LockerType _prevType = LockerType.None;
}
