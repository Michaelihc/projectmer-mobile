using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.CustomHandlers;
using MEC;
using ProjectMER.Features;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.Objects;
using ProjectMER.Features.Serializable;

namespace ProjectMER.Events.Handlers.Internal;

public class GenericEventsHandler : CustomEventsHandler
{
	/// <summary>
	/// Registers the new scene's prefabs. Round state is not reset here: the round restart already did, and plugins whose
	/// handlers run before this one may already have spawned MER content for the new round.
	/// </summary>
	public override void OnServerWaitingForPlayers() => PrefabManager.RegisterPrefabs();

	/// <summary>
	/// The scene change of a round restart destroys every MER object; drop everything that still refers to them. Every
	/// scene change of a server goes through the round restart (<c>RoundRestart.InitiateRoundRestart</c>).
	/// </summary>
	public override void OnServerRoundRestarted() => ResetRoundState();

	public override void OnPlayerSpawning(PlayerSpawningEventArgs ev)
	{
		if (!ev.UseSpawnPoint)
			return;

		List<MapEditorObject> list = [];
		foreach (MapSchematic map in MapUtils.LoadedMaps.Values)
		{
			foreach (KeyValuePair<string, SerializablePlayerSpawnpoint> spawnpoint in map.PlayerSpawnpoints)
			{
				if (!spawnpoint.Value.Roles.Contains(ev.Role.RoleTypeId))
					continue;

				list.AddRange(map.SpawnedObjects.Where(x => x != null && x.Id == spawnpoint.Key));
			}
		}

		if (list.Count == 0)
			return;

		MapEditorObject randomElement = list[UnityEngine.Random.Range(0, list.Count)];

		// A teleport: show the zone of the spawnpoint (its MER floor first) before the player is moved there.
		MerVisibility.Prefetch(ev.Player, randomElement.transform.position);
		ev.SpawnLocation = randomElement.transform.position;
		Timing.CallDelayed(0.05f, () =>
		{
			try
			{
				ev.Player.LookRotation = randomElement.transform.eulerAngles;
			}
			catch (Exception e)
			{
				Logger.Error(e);
			}
		});
	}

	public override void OnPlayerInteractingShootingTarget(PlayerInteractingShootingTargetEventArgs ev)
	{
		// Button 5 destroys the target.
		if (ev.ShootingTarget.GameObject.TryGetComponent(out MapEditorObject _))
			ev.IsAllowed = false;
	}

	private static void ResetRoundState()
	{
		int pending = SpawnQueue.PendingSpawns;
		int maps = MapUtils.LoadedMaps.Count;
		int networked = Budget.Networked;
		MapUtils.LoadedMaps.Clear();
		SpawnQueue.Clear();
		MerVisibility.Reset();
		MerWaypoints.Reset();
		Budget.Reset();
		AnimationController.Dictionary.Clear();
		IndicatorObject.ResetState();
		PickupEventsHandler.ButtonPickups.Clear();
		PickupEventsHandler.PickupUsesLeft.Clear();

		if (maps > 0 || pending > 0 || networked > 0)
			Logger.Info($"Round reset: forgot {maps} loaded maps and {networked} networked MER objects, dropped {pending} queued spawns.");
	}
}
