using InventorySystem;
using InventorySystem.Items;
using InventorySystem.Items.Firearms;
using InventorySystem.Items.Firearms.Attachments;
using InventorySystem.Items.Firearms.Attachments.Components;
using InventorySystem.Items.Pickups;
using LabApi.Features.Wrappers;
using MEC;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Objects;
using ProjectMER.Features.Serializable;
using ProjectMER.Features.Serializable.Lockers;
using ProjectMER.Features.Serializable.Schematics;
using Firearm = InventorySystem.Items.Firearms.Firearm;

namespace ProjectMER.Features.ToolGun;

/// <summary>
/// The tool gun: an automatic firearm with an empty magazine whose dry fire runs the current mode.
/// </summary>
/// <remarks>
/// <para>
/// Carl Mod runs the pre-14.0 firearm code. Its <c>AutomaticAction</c> never dry-fires a semi-automatic weapon with an
/// empty magazine on the client (the COM-15 and COM-18 just reset their hammer), so the tool gun is an FSP-9: automatic,
/// with a flashlight attachment for the mobile light-toggle button. Its status is fixed at 0 rounds with
/// <c>Cocked | Chambered | MagazineInserted</c> (the FSP-9 has a bolt lock, and an unchambered bolt-lock gun only clicks
/// without sending the dry-fire request). It cannot shoot: the server refuses shots without ammo, reloads and unloads are
/// cancelled, the acquisition refill that a firearm added without a pickup would get is disabled, and a tool gun that
/// leaves the inventory by any path other than <c>mp tg</c> (death, cuffing, escaping, plugins) has its pickup locked and
/// destroyed: the escape code gives the pickups of the old inventory back at once unless they are locked. SCP-914 leaves
/// a tool gun in an inventory unchanged (its firearm upgrade would hand out a loaded gun).
/// </para>
/// <para>
/// Inputs (all are requests the stock firearm handler already processes, see docs/projectmer-port-plan.md §4): attack
/// (dry fire) runs Create or Delete, or Select while aiming; inspect and the light toggle switch Create/Delete; reload and
/// drop step backwards and forwards through the object types and the schematics. Mode, type and schematic are per player
/// (<see cref="ToolGunState"/>).
/// </para>
/// </remarks>
public class ToolGunItem
{
	/// <summary>
	/// The firearm used as the tool gun.
	/// </summary>
	public const ItemType ToolGunItemType = ItemType.GunFSP9;

	private const FirearmStatusFlags BaseFlags = FirearmStatusFlags.Cocked | FirearmStatusFlags.Chambered | FirearmStatusFlags.MagazineInserted;

	private const float SchematicListLifetime = 5f;

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
	/// The object types reload and drop cycle through after the schematics, in ProjectMER's enum order.
	/// </summary>
	internal static readonly ToolGunObjectType[] TypeCycle = BuildTypeCycle();

	private static string[] _schematicNames = [];

	private static float _schematicNamesTime = float.NegativeInfinity;

	private static uint _attachmentsCode;

	private static bool _attachmentsCodeKnown;

	private static bool _subscribed;

	private ToolGunItem(Firearm firearm)
	{
		Firearm = firearm;
		Serial = firearm.ItemSerial;
	}

	/// <summary>
	/// Gets the firearm.
	/// </summary>
	public Firearm Firearm { get; }

	/// <summary>
	/// Gets the item serial.
	/// </summary>
	public ushort Serial { get; }

	/// <summary>
	/// Gets the schematic names reload and drop cycle through, as last read from disk (inputs and <c>mp tg</c> re-read
	/// the folder at most every few seconds).
	/// </summary>
	public static string[] SchematicNames
	{
		get
		{
			if (float.IsNegativeInfinity(_schematicNamesTime))
				RefreshSchematicNames();

			return _schematicNames;
		}
	}

	/// <summary>
	/// Gets or sets the object type the owner creates.
	/// </summary>
	/// <remarks>Kept for ProjectMER compatibility; the type is stored per player in <see cref="ToolGunState"/>.</remarks>
	public ToolGunObjectType SelectedObjectToSpawn
	{
		get => OwnerState?.ObjectType ?? ToolGunObjectType.Primitive;
		set
		{
			ToolGunState? state = OwnerState;
			if (state != null && GetUnsupportedReason(value) == null && TypesDictionary.ContainsKey(value))
				state.ObjectType = value;
		}
	}

	/// <summary>
	/// Gets whether the owner is aiming (aim + attack selects).
	/// </summary>
	public bool Aiming => Firearm != null && Firearm.AdsModule != null && Firearm.AdsModule.ServerAds;

	public bool CreateMode => !Aiming && (OwnerState?.Mode ?? ToolGunMode.Create) == ToolGunMode.Create;

	public bool DeleteMode => !Aiming && OwnerState?.Mode == ToolGunMode.Delete;

	public bool SelectMode => Aiming;

	/// <summary>
	/// Gets whether the firearm has its flashlight attachment (the mobile light-toggle button is shown only then).
	/// </summary>
	public bool HasFlashlight => Firearm != null && Firearm.HasAdvantageFlag(AttachmentDescriptiveAdvantages.Flashlight);

	private ToolGunState? OwnerState
	{
		get
		{
			if (Firearm == null || Firearm.Owner == null)
				return null;

			Player? owner = Player.Get(Firearm.Owner);
			return owner is null ? null : ToolGunState.Get(owner);
		}
	}

	/// <summary>
	/// Re-reads the schematic list from disk unless it was read in the last few seconds.
	/// </summary>
	/// <param name="force">Whether to read it regardless.</param>
	public static void RefreshSchematicNames(bool force = false)
	{
		if (!force && UnityEngine.Time.realtimeSinceStartup - _schematicNamesTime < SchematicListLifetime)
			return;

		try
		{
			_schematicNames = MapUtils.GetAvailableSchematicNames();
		}
		catch (Exception e)
		{
			Logger.Warn($"Tool gun: could not list schematics: {e.Message}");
			_schematicNames = [];
		}

		_schematicNamesTime = UnityEngine.Time.realtimeSinceStartup;
	}

	public static bool TryAdd(Player player)
	{
		Item? item = player.AddItem(ToolGunItemType);
		if (item is not FirearmItem firearmItem)
		{
			if (item != null)
				player.RemoveItem(item);

			return false;
		}

		Firearm firearm = firearmItem.Base;

		// A firearm added without a pickup refills to a full magazine when the client confirms the acquisition
		// (Firearm.ServerConfirmAcqusition). The tool gun must stay empty.
		firearm._refillAmmo = false;
		firearm.AcquisitionAlreadyReceived = true;

		ToolGunItem toolGun = new(firearm);
		ItemDictionary[toolGun.Serial] = toolGun;
		EnsureSubscribed();

		ToolGunState state = ToolGunState.Get(player);
		toolGun.ApplyStatus(state.Mode == ToolGunMode.Create);

		EditingColliders.Acquire(toolGun);
		ToolGunLoop.EnsureRunning();
		return true;
	}

	public static bool Remove(Player player)
	{
		foreach (ItemBase itemBase in player.Inventory.UserInventory.Items.Values)
		{
			if (!ItemDictionary.ContainsKey(itemBase.ItemSerial))
				continue;

			// InventoryExtensions.OnItemRemoved forgets the item.
			player.RemoveItem(itemBase);
			Forget(itemBase.ItemSerial);
			return true;
		}

		return false;
	}

	/// <summary>
	/// Runs the action of the current mode at the crosshair.
	/// </summary>
	public void Shot(Player player)
	{
		ToolGunState state = ToolGunState.Get(player);
		if (Aiming)
		{
			// Aiming at nothing deselects, as in ProjectMER.
			ToolGunHandler.TryGetMapObject(player, out MapEditorObject target);
			ToolGunHandler.SelectObject(player, target);
		}
		else if (state.Mode == ToolGunMode.Create)
		{
			ToolGunHandler.CreateObject(player, state.ObjectType, state.SchematicName ?? string.Empty);
		}
		else if (ToolGunHandler.TryGetMapObject(player, out MapEditorObject target))
		{
			ToolGunHandler.DeleteObject(target);
		}

		ToolGunHud.Refresh(state);
	}

	/// <summary>
	/// Switches between Create and Delete.
	/// </summary>
	/// <param name="player">The owner.</param>
	/// <param name="syncFlashlight">Whether to switch the flashlight to match (on: Create); the light-toggle request applies it itself.</param>
	/// <returns>Whether the new mode is Create.</returns>
	public bool ToggleMode(Player player, bool syncFlashlight = true)
	{
		ToolGunState state = ToolGunState.Get(player);
		state.Mode = state.Mode == ToolGunMode.Create ? ToolGunMode.Delete : ToolGunMode.Create;
		bool create = state.Mode == ToolGunMode.Create;
		if (syncFlashlight)
			ApplyStatus(create);

		ToolGunHud.Refresh(state);
		return create;
	}

	/// <summary>
	/// Steps through the schematics and object types (drop: +1, reload: -1).
	/// </summary>
	public void Cycle(Player player, int step)
	{
		ToolGunState state = ToolGunState.Get(player);
		Step(state, step);
		ToolGunHud.Refresh(state);
	}

	/// <summary>
	/// Steps a player's selection through the cycle: every schematic (ProjectMER's type 0), then the other object types.
	/// </summary>
	internal static void Step(ToolGunState state, int step)
	{
		RefreshSchematicNames();
		string[] schematics = _schematicNames;
		int count = schematics.Length + TypeCycle.Length;
		int index = GetCycleIndex(state, schematics);
		index = ((index + step) % count + count) % count;

		if (index < schematics.Length)
		{
			state.ObjectType = ToolGunObjectType.Schematic;
			state.SchematicName = schematics[index];
		}
		else
		{
			state.ObjectType = TypeCycle[index - schematics.Length];
		}
	}

	/// <summary>
	/// Gets the position of a player's selection in the cycle (for the HUD), and the cycle length.
	/// </summary>
	internal static int GetCycleIndex(ToolGunState state, string[] schematics)
	{
		if (state.ObjectType == ToolGunObjectType.Schematic)
		{
			for (int i = 0; i < schematics.Length; i++)
			{
				if (string.Equals(schematics[i], state.SchematicName, StringComparison.OrdinalIgnoreCase))
					return i;
			}

			return 0;
		}

		int typeIndex = Array.IndexOf(TypeCycle, state.ObjectType);
		return schematics.Length + Math.Max(0, typeIndex);
	}

	/// <summary>
	/// Writes the fixed tool gun status: empty, cocked and chambered (so the client dry-fires), the flashlight on in
	/// Create mode.
	/// </summary>
	/// <param name="flashlight">Whether the flashlight is on.</param>
	internal void ApplyStatus(bool flashlight)
	{
		if (Firearm == null)
			return;

		uint code = GetAttachmentsCode(Firearm);
		FirearmStatusFlags flags = BaseFlags;

		// The attachment code has to be applied before the flashlight flag means anything.
		if (Firearm.Status.Attachments != code)
			Firearm.Status = new FirearmStatus(0, flags, code);

		if (flashlight && HasFlashlight)
			flags |= FirearmStatusFlags.FlashlightEnabled;

		// Firearm.Status sends a status message only when the value changes.
		Firearm.Status = new FirearmStatus(0, flags, code);
	}

	/// <summary>
	/// Gets the attachment code: the first attachment of every slot, except the flashlight where a slot has one.
	/// </summary>
	private static uint GetAttachmentsCode(Firearm firearm)
	{
		if (_attachmentsCodeKnown)
			return _attachmentsCode;

		Attachment[] attachments = firearm.Attachments;
		uint code = 0;
		for (int i = 0; i < attachments.Length; i++)
		{
			Attachment attachment = attachments[i];
			int chosen = -1;
			for (int j = 0; j < attachments.Length; j++)
			{
				if (attachments[j].Slot != attachment.Slot)
					continue;

				if ((attachments[j].DescriptivePros & AttachmentDescriptiveAdvantages.Flashlight) != 0)
				{
					chosen = j;
					break;
				}

				if (chosen < 0)
					chosen = j;
			}

			if (chosen == i)
				code |= 1u << i;
		}

		_attachmentsCode = firearm.ValidateAttachmentsCode(code);
		_attachmentsCodeKnown = true;
		return _attachmentsCode;
	}

	private static ToolGunObjectType[] BuildTypeCycle()
	{
		List<ToolGunObjectType> types = [];
		foreach (ToolGunObjectType type in TypesDictionary.Keys)
		{
			if (type != ToolGunObjectType.Schematic)
				types.Add(type);
		}

		types.Sort(static (a, b) => ((int)a).CompareTo((int)b));
		return types.ToArray();
	}

	private static void EnsureSubscribed()
	{
		if (_subscribed)
			return;

		_subscribed = true;
		InventoryExtensions.OnItemRemoved += OnItemRemoved;
	}

	/// <summary>
	/// Forgets a tool gun that left an inventory. A dropped tool gun (death, cuffing, escaping, a plugin) would be an
	/// ordinary FSP-9 pickup, so the pickup is locked at once and destroyed on the next frame (callers of
	/// <c>ServerDropItem</c> still use it this frame). The lock keeps <c>InventoryItemProvider.SpawnPreviousInventoryPickups</c>
	/// (escape) from putting the same serial straight back into the inventory as a plain FSP-9.
	/// </summary>
	private static void OnItemRemoved(ReferenceHub hub, ItemBase item, ItemPickupBase pickup)
	{
		if (item == null || !Forget(item.ItemSerial) || pickup == null)
			return;

		PickupSyncInfo info = pickup.Info;
		info.Locked = true;
		pickup.NetworkInfo = info;

		Timing.CallDelayed(0f, () =>
		{
			if (pickup != null)
				pickup.DestroySelf();
		});
	}

	/// <summary>
	/// Forgets a tool gun item.
	/// </summary>
	/// <returns>Whether it was a tool gun.</returns>
	internal static bool Forget(ushort serial)
	{
		if (!ItemDictionary.TryGetValue(serial, out ToolGunItem toolGun))
			return false;

		ItemDictionary.Remove(serial);
		EditingColliders.Release(toolGun);
		return true;
	}

	/// <summary>
	/// Forgets every tool gun whose firearm no longer exists or belongs to a player that left.
	/// </summary>
	internal static void ForgetOrphans(ReferenceHub? leaving)
	{
		List<ushort> stale = NorthwoodLib.Pools.ListPool<ushort>.Shared.Rent();
		foreach (KeyValuePair<ushort, ToolGunItem> pair in ItemDictionary)
		{
			Firearm firearm = pair.Value.Firearm;
			if (firearm == null || firearm.Owner == null || firearm.Owner == leaving)
				stale.Add(pair.Key);
		}

		foreach (ushort serial in stale)
			Forget(serial);

		NorthwoodLib.Pools.ListPool<ushort>.Shared.Return(stale);
	}
}
