using System.Text;
using CommandSystem;
using LabApi.Features.Permissions;
using LabApi.Features.Wrappers;
using Mirror;
using ProjectMER.Configs;
using ProjectMER.Features;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.Objects;
using ProjectMER.Features.Serializable;

namespace ProjectMER.Commands.Utility;

/// <summary>
/// <c>mp stats</c>: networked MER objects, budgets, the spawn queue and what each player observes.
/// </summary>
/// <remarks>The report is also written to the server log (the file console does not return command output).</remarks>
public class Stats : ICommand
{
	public string Command => "stats";

	public string[] Aliases => ["st"];

	public string Description => "Shows networked MER object counts, budgets, the spawn queue and per-player observed objects.";

	public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
	{
		if (!sender.HasAnyPermission($"mpr.{Command}"))
		{
			response = $"You don't have permission to execute this command. Required permission: mpr.{Command}";
			return false;
		}

		response = Build();
		Logger.Info(response);
		return true;
	}

	/// <summary>
	/// Builds the report.
	/// </summary>
	public static string Build()
	{
		Config config = ProjectMER.Singleton.Config!;
		StringBuilder sb = new();
		sb.Append("MER stats:");
		int toys = Budget.Count(MerObjectKind.Primitive) + Budget.Lights + Budget.Count(MerObjectKind.ShootingTarget) + Budget.Count(MerObjectKind.Indicator);
		sb.Append($"\nNetworked: {Budget.Networked} (warn {config.NetworkedWarnTotal}, cap {config.NetworkedHardCap}); toys static {Budget.Static}, dynamic {toys - Budget.Static}; transparent {Budget.Transparent}; refused {Budget.Refused}");
		sb.Append($"\nBy kind: primitives {Budget.Count(MerObjectKind.Primitive)}, lights {Budget.Lights} (max {config.MaxLights}), pickups {Budget.Count(MerObjectKind.Pickup)}, structures {Budget.Count(MerObjectKind.Structure)}, doors {Budget.Count(MerObjectKind.Door)}, shooting targets {Budget.Count(MerObjectKind.ShootingTarget)}, indicators {Budget.Count(MerObjectKind.Indicator)}");
		sb.Append($"\nEstimated spawn bytes for a joining player: {Budget.EstimatedSpawnBytes / 1024.0:F1} KB");
		sb.Append($"\nSpawn queue: {SpawnQueue.PendingSpawns} spawns and {SpawnQueue.PendingDestroys} destroys pending; limits {config.SpawnMaxPerFrame}/frame, {config.SpawnTimeBudgetMs} ms/frame");
		sb.Append($"\nLast drain: {SpawnQueue.LastSessionSummary}");

		int disabled = 0, enabled = 0;
		foreach (MerBlockLink link in UnityEngine.Object.FindObjectsByType<MerBlockLink>(UnityEngine.FindObjectsSortMode.None))
		{
			if (link.TryGetComponent(out AdminToys.AdminToyBase toy) && link.Kind != MerObjectKind.ShootingTarget)
			{
				if (toy.enabled)
					enabled++;
				else
					disabled++;
			}
		}

		sb.Append($"\nToy server behaviours: {disabled} disabled, {enabled} enabled");

		sb.Append($"\nLoaded maps: {MapUtils.LoadedMaps.Count}");
		foreach (MapSchematic map in MapUtils.LoadedMaps.Values)
			sb.Append($"\n- {map.Name}: {map.SpawnedObjects.Count} objects{(map.SpawnGroup != null ? $", {map.SpawnGroup.Pending} queued" : string.Empty)}");

		foreach (SchematicObject schematic in UnityEngine.Object.FindObjectsByType<SchematicObject>(UnityEngine.FindObjectsSortMode.None))
			sb.Append($"\n- schematic {schematic.Name}: {schematic.NetworkedCount} networked, {(schematic.IsStatic ? "static" : "dynamic")}, {(schematic.IsBuilt ? "built" : $"{schematic.SpawnGroup.Pending} queued")}");

		sb.Append($"\nPlayers (observed MER objects / all observed):");
		foreach (Player player in Player.List)
		{
			if (player.IsHost || player.ConnectionToClient == null)
				continue;

			sb.Append($"\n- {player.Nickname}: {MerVisibility.CountObserved(player)} / {player.ConnectionToClient.observing.Count}");
		}

		return sb.ToString();
	}
}
