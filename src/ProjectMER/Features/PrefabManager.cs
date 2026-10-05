using System.Text;
using AdminToys;
using Interactables.Interobjects.DoorUtils;
using InventorySystem.Items.Firearms.Attachments;
using MapGeneration.Distributors;
using Mirror;
using UnityEngine;
using LightSourceToy = AdminToys.LightSourceToy;
using PrimitiveObjectToy = AdminToys.PrimitiveObjectToy;

namespace ProjectMER.Features;

/// <summary>
/// Finds the network prefabs MER spawns, by component and Carl Mod prefab name (docs/projectmer-port-plan.md §1.3).
/// </summary>
/// <remarks>
/// Carl Mod (SL 13.1-13.2 game code) has no bulk door, spawnable gate, capybara, text, interactable, camera, waypoint or
/// culling-parent prefabs, and no SCP-1576/Anti-SCP-207/SCP-1344 pedestals or experimental weapon locker. Those
/// ProjectMER properties do not exist here; their map objects are skipped with a warning.
/// </remarks>
public static class PrefabManager
{
	public static PrimitiveObjectToy PrimitiveObject { get; private set; }

	public static LightSourceToy LightSource { get; private set; }

	public static DoorVariant DoorLcz { get; private set; }
	public static DoorVariant DoorHcz { get; private set; }
	public static DoorVariant DoorEz { get; private set; }

	public static WorkstationController Workstation { get; private set; }

	public static ShootingTarget ShootingTargetSport { get; private set; }
	public static ShootingTarget ShootingTargetDBoy { get; private set; }
	public static ShootingTarget ShootingTargetBinary { get; private set; }

	public static Locker PedestalScp018 { get; private set; }
	public static Locker PedstalScp207 { get; private set; }
	public static Locker PedestalScp244 { get; private set; }
	public static Locker PedestalScp268 { get; private set; }
	public static Locker LockerLargeGun { get; private set; }
	public static Locker LockerRifleRack { get; private set; }
	public static Locker LockerMisc { get; private set; }
	public static Locker LockerRegularMedkit { get; private set; }
	public static Locker LockerAdrenalineMedkit { get; private set; }
	public static Locker PedestalScp500 { get; private set; }
	public static Locker PedstalScp1853 { get; private set; }
	public static Locker PedestalScp2176 { get; private set; }

	/// <summary>
	/// Gets every prefab registered with Mirror (<c>NetworkClient.prefabs</c>), for <c>mp prefabs</c>.
	/// </summary>
	public static IEnumerable<GameObject> AllNetworkPrefabs => NetworkClient.prefabs.Values;

	public static void RegisterPrefabs()
	{
		foreach (GameObject gameObject in NetworkClient.prefabs.Values)
		{
			if (gameObject.TryGetComponent(out PrimitiveObjectToy primitiveObjectToy))
			{
				PrimitiveObject = primitiveObjectToy;
				continue;
			}

			if (gameObject.TryGetComponent(out LightSourceToy lightSourceToy))
			{
				LightSource = lightSourceToy;
				continue;
			}

			if (gameObject.TryGetComponent(out DoorVariant doorVariant))
			{
				switch (gameObject.name)
				{
					case "LCZ BreakableDoor":
						DoorLcz = doorVariant;
						continue;
					case "HCZ BreakableDoor":
						DoorHcz = doorVariant;
						continue;
					case "EZ BreakableDoor":
						DoorEz = doorVariant;
						continue;
				}
			}

			if (gameObject.TryGetComponent(out ShootingTarget shootingTarget))
			{
				switch (gameObject.name)
				{
					case "sportTargetPrefab":
						ShootingTargetSport = shootingTarget;
						continue;
					case "dboyTargetPrefab":
						ShootingTargetDBoy = shootingTarget;
						continue;
					case "binaryTargetPrefab":
						ShootingTargetBinary = shootingTarget;
						continue;
				}
			}

			if (gameObject.TryGetComponent(out WorkstationController workstationController))
			{
				Workstation = workstationController;
				continue;
			}

			if (gameObject.TryGetComponent(out Locker locker))
			{
				switch (gameObject.name)
				{
					case "Scp018PedestalStructure Variant":
						PedestalScp018 = locker;
						continue;
					case "Scp207PedestalStructure Variant":
						PedstalScp207 = locker;
						continue;
					case "Scp244PedestalStructure Variant":
						PedestalScp244 = locker;
						continue;
					case "Scp268PedestalStructure Variant":
						PedestalScp268 = locker;
						continue;
					case "LargeGunLockerStructure":
						LockerLargeGun = locker;
						continue;
					case "RifleRackStructure":
						LockerRifleRack = locker;
						continue;
					case "MiscLocker":
						LockerMisc = locker;
						continue;
					case "RegularMedkitStructure":
						LockerRegularMedkit = locker;
						continue;
					case "AdrenalineMedkitStructure":
						LockerAdrenalineMedkit = locker;
						continue;
					case "Scp500PedestalStructure Variant":
						PedestalScp500 = locker;
						continue;
					case "Scp1853PedestalStructure Variant":
						PedstalScp1853 = locker;
						continue;
					case "Scp2176PedestalStructure Variant":
						PedestalScp2176 = locker;
						continue;
				}
			}
		}

		LogMissing();
	}

	/// <summary>
	/// Builds the <c>mp prefabs</c> dump: name, asset id and components of every registered network prefab.
	/// </summary>
	public static string Dump()
	{
		StringBuilder sb = new();
		int count = 0;
		foreach (KeyValuePair<uint, GameObject> pair in NetworkClient.prefabs.OrderBy(x => x.Value != null ? x.Value.name : string.Empty, StringComparer.Ordinal))
		{
			GameObject prefab = pair.Value;
			if (prefab == null)
				continue;

			count++;
			sb.Append("\n- ").Append(prefab.name).Append(" (assetId ").Append(pair.Key).Append("): ");
			Component[] components = prefab.GetComponents<Component>();
			for (int i = 0; i < components.Length; i++)
			{
				if (components[i] == null || components[i] is Transform)
					continue;

				sb.Append(components[i].GetType().FullName);
				if (i < components.Length - 1)
					sb.Append(", ");
			}
		}

		sb.Insert(0, $"{count} network prefabs:");
		return sb.ToString();
	}

	private static void LogMissing()
	{
		List<string> missing = [];
		Check(PrimitiveObject, "PrimitiveObjectToy", missing);
		Check(LightSource, "LightSourceToy", missing);
		Check(DoorLcz, "LCZ BreakableDoor", missing);
		Check(DoorHcz, "HCZ BreakableDoor", missing);
		Check(DoorEz, "EZ BreakableDoor", missing);
		Check(Workstation, "Spawnable Work Station Structure", missing);
		Check(ShootingTargetSport, "sportTargetPrefab", missing);
		Check(ShootingTargetDBoy, "dboyTargetPrefab", missing);
		Check(ShootingTargetBinary, "binaryTargetPrefab", missing);
		Check(LockerLargeGun, "LargeGunLockerStructure", missing);
		Check(LockerRifleRack, "RifleRackStructure", missing);
		Check(LockerMisc, "MiscLocker", missing);
		Check(LockerRegularMedkit, "RegularMedkitStructure", missing);
		Check(LockerAdrenalineMedkit, "AdrenalineMedkitStructure", missing);
		Check(PedestalScp500, "Scp500PedestalStructure Variant", missing);
		Check(PedestalScp018, "Scp018PedestalStructure Variant", missing);
		Check(PedstalScp207, "Scp207PedestalStructure Variant", missing);
		Check(PedestalScp244, "Scp244PedestalStructure Variant", missing);
		Check(PedestalScp268, "Scp268PedestalStructure Variant", missing);
		Check(PedstalScp1853, "Scp1853PedestalStructure Variant", missing);
		Check(PedestalScp2176, "Scp2176PedestalStructure Variant", missing);

		if (missing.Count == 0)
			Logger.Info($"Registered all {21} MER network prefabs.");
		else
			Logger.Warn($"MER network prefabs not found: {string.Join(", ", missing)}. Objects that need them are skipped.");
	}

	private static void Check(UnityEngine.Object prefab, string name, List<string> missing)
	{
		if (prefab == null)
			missing.Add(name);
	}
}
