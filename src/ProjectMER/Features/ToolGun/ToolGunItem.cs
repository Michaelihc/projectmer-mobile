using InventorySystem.Items;
using InventorySystem.Items.Firearms;
using LabApi.Features.Wrappers;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Objects;
using ProjectMER.Features.Serializable;
using ProjectMER.Features.Serializable.Lockers;
using ProjectMER.Features.Serializable.Schematics;
using Firearm = InventorySystem.Items.Firearms.Firearm;

namespace ProjectMER.Features.ToolGun;

/// <summary>
/// The tool gun: a COM-18 with an empty magazine whose dry fire runs the current mode.
/// </summary>
/// <remarks>
/// <para>
/// Phase A port (minimal): modes follow ProjectMER (flashlight on: create, off: delete; aiming: select), the schematic to
/// create is chosen with <c>mp tg schematic</c> instead of a server-specific settings dropdown (Carl Mod has none), and
/// object types cycle through the types the Carl Mod client supports.
/// </para>
/// <para>
/// Seam for the mobile rewrite (docs/projectmer-port-plan.md §4): input mapping (inspect toggles create/delete, reload and
/// drop cycle types), the fork attachment code with a flashlight, and the HUD belong in this file,
/// <see cref="ToolGunUI"/>, <see cref="ToolGunHandler"/> and <c>ToolGunEventsHandler</c>.
/// </para>
/// </remarks>
public class ToolGunItem
{
	public static Dictionary<ushort, ToolGunItem> ItemDictionary { get; private set; } = [];

	/// <summary>
	/// Gets the object types the tool gun and <c>mp create</c> can create in Carl Mod.
	/// </summary>
	public static Dictionary<ToolGunObjectType, Type> TypesDictionary { get; private set; } = new()
	{
		{ ToolGunObjectType.Primitive, typeof(SerializablePrimitive) },
		{ ToolGunObjectType.Light, typeof(SerializableLight) },
		{ ToolGunObjectType.Door, typeof(SerializableDoor) },
		{ ToolGunObjectType.Workstation, typeof(SerializableWorkstation) },
		{ ToolGunObjectType.ItemSpawnpoint, typeof(SerializableItemSpawnpoint) },
		{ ToolGunObjectType.PlayerSpawnpoint, typeof(SerializablePlayerSpawnpoint) },
		{ ToolGunObjectType.Schematic, typeof(SerializableSchematic) },
		{ ToolGunObjectType.ShootingTarget, typeof(SerializableShootingTarget) },
		{ ToolGunObjectType.Locker, typeof(SerializableLocker) },
		{ ToolGunObjectType.Teleport, typeof(SerializableTeleport) },
		{ ToolGunObjectType.Quad, typeof(SerializablePrimitive) },
	};

	/// <summary>
	/// Gets the reason a ProjectMER object type cannot be created in Carl Mod, or <see langword="null"/>.
	/// </summary>
	public static string? GetUnsupportedReason(ToolGunObjectType objectType) => objectType switch
	{
		ToolGunObjectType.Capybara => "CapybaraToy is not available in the Carl Mod client",
		ToolGunObjectType.Text => "TextToy is not available in the Carl Mod client",
		ToolGunObjectType.Scp079Camera => "Scp079CameraToy is not available in the Carl Mod client",
		ToolGunObjectType.Interactable => "InvisibleInteractableToy is not available in the Carl Mod client",
		ToolGunObjectType.Waypoint => "WaypointToy is not available in the Carl Mod client",
		ToolGunObjectType.Triangle => "triangles need sheared (parented) quads, which Carl Mod clients cannot show",
		_ => null,
	};

	/// <summary>
	/// Gets the schematic each player creates with the tool gun.
	/// </summary>
	public static Dictionary<Player, string> SelectedSchematics { get; } = [];

	private static readonly ToolGunObjectType[] CycleOrder = TypesDictionary.Keys.OrderBy(x => (int)x).ToArray();

	private int _typeIndex = Array.IndexOf(CycleOrder, ToolGunObjectType.Primitive);

	public ToolGunObjectType SelectedObjectToSpawn
	{
		get => CycleOrder[_typeIndex];
		set
		{
			int index = Array.IndexOf(CycleOrder, value);
			if (index >= 0)
			{
				_typeIndex = index;
				return;
			}

			// ProjectMER's ++/-- enum arithmetic: step from the current type over the unsupported ones.
			int step = (int)value > (int)CycleOrder[_typeIndex] ? 1 : -1;
			_typeIndex = (_typeIndex + step + CycleOrder.Length) % CycleOrder.Length;
		}
	}

	public static bool TryAdd(Player player)
	{
		Item? item = player.AddItem(ItemType.GunCOM18);
		if (item is not FirearmItem firearmItem)
			return false;

		Firearm toolgun = firearmItem.Base;
		FirearmStatus status = toolgun.Status;
		toolgun.Status = new FirearmStatus(0, FirearmStatusFlags.Cocked | FirearmStatusFlags.MagazineInserted, status.Attachments);

		player.AddAmmo(ItemType.Ammo9x19, 1);

		ItemDictionary.Add(toolgun.ItemSerial, new ToolGunItem(toolgun));
		return true;
	}

	public static bool Remove(Player player)
	{
		foreach (ItemBase itemBase in player.Inventory.UserInventory.Items.Values)
		{
			if (ItemDictionary.ContainsKey(itemBase.ItemSerial))
			{
				ItemDictionary.Remove(itemBase.ItemSerial);
				player.RemoveItem(itemBase);
				return true;
			}
		}

		return false;
	}

	public bool CreateMode => Firearm.IsEmittingLight && !Firearm.AdsModule.ServerAds;
	public bool DeleteMode => !Firearm.IsEmittingLight && !Firearm.AdsModule.ServerAds;
	public bool SelectMode => Firearm.AdsModule.ServerAds;

	public void Shot(Player player)
	{
		if (CreateMode)
		{
			SelectedSchematics.TryGetValue(player, out string? schematicName);
			ToolGunHandler.CreateObject(player, SelectedObjectToSpawn, schematicName ?? string.Empty);
			return;
		}

		if (ToolGunHandler.TryGetMapObject(player, out MapEditorObject mapEditorObject) && DeleteMode)
		{
			ToolGunHandler.DeleteObject(mapEditorObject);
			return;
		}

		if (SelectMode)
			ToolGunHandler.SelectObject(player, mapEditorObject);
	}

	private ToolGunItem(Firearm firearm)
	{
		Firearm = firearm;
	}

	private readonly Firearm Firearm;
}
