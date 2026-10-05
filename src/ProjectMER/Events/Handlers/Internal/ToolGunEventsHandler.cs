using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.Scp914Events;
using LabApi.Events.CustomHandlers;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.ToolGun;

namespace ProjectMER.Events.Handlers.Internal;

/// <summary>
/// Maps the mobile firearm buttons to tool gun actions (docs/projectmer-port-plan.md §4).
/// </summary>
/// <remarks>
/// <para>
/// Every input is a request the stock Carl Mod firearm handler already processes, raised by the LabAPI port from
/// <c>FirearmBasicMessagesHandler.ServerRequestReceived</c> (and <c>Inventory.CmdDropItem</c> for drop):
/// </para>
/// <list type="table">
/// <item><term>Attack (dry fire)</term><description><see cref="OnPlayerDryFiringWeapon"/>: Create or Delete at the crosshair, or
/// Select while aiming (aim is a toggle on mobile). The request is cancelled.</description></item>
/// <item><term>Inspect</term><description><see cref="OnPlayerInspectingItem"/>: switches Create/Delete and the flashlight with it.</description></item>
/// <item><term>Light toggle</term><description><see cref="OnPlayerTogglingWeaponFlashlight"/>: the same switch; the light shows
/// the mode (on: Create), as in ProjectMER.</description></item>
/// <item><term>Reload</term><description><see cref="OnPlayerReloadingWeapon"/>: previous schematic or object type; cancelled.</description></item>
/// <item><term>Throw away (drop)</term><description><see cref="OnPlayerDroppingItem"/>: next schematic or object type; cancelled.</description></item>
/// </list>
/// <para>
/// Aiming is read from the firearm's <c>AdsModule.ServerAds</c> when the attack arrives; <c>AimingWeapon</c> does not exist
/// in LabAPI, and subscribing to <c>AimedWeapon</c> would make every player's aim requests allocate event arguments, so the
/// HUD picks the aim state up on its next check instead. Unload requests are cancelled (the tool gun stays empty), and so
/// is SCP-914's processing of a tool gun in an inventory.
/// </para>
/// </remarks>
public class ToolGunEventsHandler : CustomEventsHandler
{
	// Only on round restart: a WaitingForPlayers reset would also drop tool guns given by plugins handling that event first.
	public override void OnServerRoundRestarted() => ResetRound();

	public override void OnPlayerDryFiringWeapon(PlayerDryFiringWeaponEventArgs ev)
	{
		if (!ev.FirearmItem.IsToolGun(out ToolGunItem toolGun))
			return;

		ev.IsAllowed = false;
		toolGun.Shot(ev.Player);
	}

	public override void OnPlayerReloadingWeapon(PlayerReloadingWeaponEventArgs ev)
	{
		if (!ev.FirearmItem.IsToolGun(out ToolGunItem toolGun))
			return;

		ev.IsAllowed = false;
		toolGun.Cycle(ev.Player, -1);
	}

	public override void OnPlayerUnloadingWeapon(PlayerUnloadingWeaponEventArgs ev)
	{
		if (ev.FirearmItem.IsToolGun(out ToolGunItem _))
			ev.IsAllowed = false;
	}

	public override void OnPlayerDroppingItem(PlayerDroppingItemEventArgs ev)
	{
		if (!ev.Item.IsToolGun(out ToolGunItem toolGun))
			return;

		ev.IsAllowed = false;
		toolGun.Cycle(ev.Player, 1);
	}

	public override void OnPlayerInspectingItem(PlayerInspectingItemEventArgs ev)
	{
		if (!ev.Item.IsToolGun(out ToolGunItem toolGun))
			return;

		toolGun.ToggleMode(ev.Player);
	}

	public override void OnPlayerTogglingWeaponFlashlight(PlayerTogglingWeaponFlashlightEventArgs ev)
	{
		if (!ev.FirearmItem.IsToolGun(out ToolGunItem toolGun))
			return;

		// The request applies NewState itself.
		ev.NewState = toolGun.ToggleMode(ev.Player, syncFlashlight: false);
	}

	public override void OnPlayerChangedItem(PlayerChangedItemEventArgs ev)
	{
		if (!ev.NewItem.IsToolGun(out ToolGunItem _) && !ev.OldItem.IsToolGun(out ToolGunItem _))
			return;

		// Show the HUD at once, or hide it.
		ToolGunHud.Refresh(ToolGunState.Get(ev.Player));
	}

	public override void OnScp914ProcessingInventoryItem(Scp914ProcessingInventoryItemEventArgs ev)
	{
		// The firearm upgrade removes the item and adds a new, loaded firearm that is no tool gun.
		if (ev.Item.IsToolGun(out ToolGunItem _))
			ev.IsAllowed = false;
	}

	public override void OnPlayerLeft(PlayerLeftEventArgs ev)
	{
		ToolGunState.Remove(ev.Player);
		ToolGunItem.ForgetOrphans(ev.Player.ReferenceHub);
		ToolGunHandler.PlayerSelectedObjectDict.Remove(ev.Player);
	}

	private static void ResetRound()
	{
		// The scene change destroyed every tool gun, box and trigger; drop the references.
		ToolGunLoop.Stop();
		ToolGunState.Reset();
		EditingColliders.Reset();
		ToolGunItem.ItemDictionary.Clear();
		ToolGunHandler.PlayerSelectedObjectDict.Clear();
	}
}
