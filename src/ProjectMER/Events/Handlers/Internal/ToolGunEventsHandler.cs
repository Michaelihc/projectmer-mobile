using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;
using LabApi.Features.Wrappers;
using MEC;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Objects;
using ProjectMER.Features.ToolGun;

namespace ProjectMER.Events.Handlers.Internal;

/// <summary>
/// Tool gun input and HUD.
/// </summary>
/// <remarks>
/// Phase A port: ProjectMER's mapping (dry fire runs the mode, reload and drop cycle the type), and the hint refreshed every
/// <c>hud_interval</c> instead of every 0.1 s. The mobile input mapping and change-only HUD of
/// docs/projectmer-port-plan.md §4 replace this.
/// </remarks>
public class ToolGunEventsHandler : CustomEventsHandler
{
	private static CoroutineHandle _toolGunCoroutine;

	public override void OnServerRoundStarted()
	{
		Timing.KillCoroutines(_toolGunCoroutine);
		_toolGunCoroutine = Timing.RunCoroutine(ToolGunGUI());
	}

	private static IEnumerator<float> ToolGunGUI()
	{
		while (true)
		{
			float interval = Math.Max(0.1f, ProjectMER.Singleton?.Config?.HudInterval ?? 0.5f);
			yield return Timing.WaitForSeconds(interval);

			if (ToolGunItem.ItemDictionary.Count == 0 && ToolGunHandler.PlayerSelectedObjectDict.Count == 0)
				continue;

			foreach (Player player in Player.List)
			{
				if (!player.CurrentItem.IsToolGun(out ToolGunItem _) && !ToolGunHandler.TryGetSelectedMapObject(player, out MapEditorObject _))
					continue;

				string hud;
				try
				{
					hud = ToolGunUI.GetHintHUD(player);
				}
				catch (Exception e)
				{
					Logger.Error(e);
					hud = "ERROR: Check server console";
				}

				player.SendHint(hud, interval + 0.3f);
			}
		}
	}

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
		toolGun.SelectedObjectToSpawn--;
	}

	public override void OnPlayerDroppingItem(PlayerDroppingItemEventArgs ev)
	{
		if (!ev.Item.IsToolGun(out ToolGunItem toolGun))
			return;

		ev.IsAllowed = false;
		toolGun.SelectedObjectToSpawn++;
	}
}
