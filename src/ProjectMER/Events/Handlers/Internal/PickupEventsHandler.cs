using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;
using LabApi.Features.Wrappers;
using ProjectMER.Features.Objects;
using FirearmPickup = LabApi.Features.Wrappers.FirearmPickup;

namespace ProjectMER.Events.Handlers.Internal;

/// <summary>
/// Schematic "button" pickups and item spawnpoint use counts.
/// </summary>
/// <remarks>
/// MER pickups are no longer children of their spawnpoint or schematic; they carry a <see cref="MerBlockLink"/>.
/// The firearm refill uses Carl Mod's <c>FirearmStatus</c> through the LabAPI firearm wrapper (no magazine modules).
/// </remarks>
public class PickupEventsHandler : CustomEventsHandler
{
	internal static readonly Dictionary<ushort, SchematicObject> ButtonPickups = [];
	internal static readonly Dictionary<ushort, int> PickupUsesLeft = [];

	public override void OnPlayerSearchingPickup(PlayerSearchingPickupEventArgs ev)
	{
		if (!ButtonPickups.TryGetValue(ev.Pickup.Serial, out SchematicObject schematic))
			return;

		ev.IsAllowed = false;
		Schematic.OnButtonInteracted(new(ev.Pickup, ev.Player, schematic));
	}

	public override void OnPlayerPickingUpItem(PlayerPickingUpItemEventArgs ev)
	{
		if (!ev.Pickup.GameObject.TryGetComponent(out MerBlockLink _))
			return;

		if (!PickupUsesLeft.ContainsKey(ev.Pickup.Serial))
			return;

		if (--PickupUsesLeft[ev.Pickup.Serial] == 0)
		{
			PickupUsesLeft.Remove(ev.Pickup.Serial);
			return;
		}

		ev.IsAllowed = false;
		ev.Pickup.IsInUse = false;

		Item? item = ev.Player.AddItem(ev.Pickup.Type);
		if (ev.Pickup is not FirearmPickup firearmPickup || item is not FirearmItem firearmItem)
			return;

		firearmItem.AttachmentsCode = firearmPickup.AttachmentCode;
		firearmItem.StoredAmmo = firearmItem.MaxAmmo;
	}

	public override void OnPlayerPickingUpAmmo(PlayerPickingUpAmmoEventArgs ev)
	{
		if (!ev.AmmoPickup.GameObject.TryGetComponent(out MerBlockLink _))
			return;

		if (!PickupUsesLeft.ContainsKey(ev.AmmoPickup.Serial))
			return;

		if (--PickupUsesLeft[ev.AmmoPickup.Serial] == 0)
		{
			PickupUsesLeft.Remove(ev.AmmoPickup.Serial);
			return;
		}

		ev.IsAllowed = false;
		ev.AmmoPickup.IsInUse = false;
		ev.Player.AddAmmo(ev.AmmoType, ev.AmmoAmount);
	}
}
