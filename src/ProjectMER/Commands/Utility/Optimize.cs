using System.Globalization;
using System.Text;
using CommandSystem;
using LabApi.Features.Permissions;
using ProjectMER.Configs;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.Serialization;

namespace ProjectMER.Commands.Utility;

/// <summary>
/// <c>mp optimize &lt;schematic&gt; [map|api]</c>: what the schematic optimizer does to a schematic (docs/projectmer-port-plan.md
/// §3.9), with networked objects and estimated spawn bytes after each step.
/// </summary>
/// <remarks>
/// The plan is the one schematics are built from: computed on a worker thread and cached until the file changes, so a
/// second call answers from the cache. Source files are never rewritten. The report is also written to the server log
/// (the file console does not return command output).
/// </remarks>
public class Optimize : ICommand
{
	public string Command => "optimize";

	public string[] Aliases => ["opt"];

	public string Description => "Shows what the schematic optimizer removes and merges: networked objects and spawn bytes after each step. Usage: mp optimize <schematic> [map|api]";

	public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
	{
		if (!sender.HasAnyPermission($"mpr.{Command}"))
		{
			response = $"You don't have permission to execute this command. Required permission: mpr.{Command}";
			return false;
		}

		if (arguments.Count == 0)
		{
			response = "Usage: mp optimize <schematic> [map|api]. \"map\" (default) shows the plan of a schematic placed by a map, \"api\" of one spawned by a plugin.";
			return false;
		}

		string name = arguments.At(0);
		bool inMap = arguments.Count < 2 || !arguments.At(1).Equals("api", StringComparison.OrdinalIgnoreCase);
		if (!SchematicLoader.TryResolve(name, out string directory, out string jsonPath, out string error))
		{
			response = error;
			return false;
		}

		SchematicLoadJob job = SchematicLoader.LoadFile(name, directory, jsonPath, OptimizerSettings.FromConfig(inMap));
		job.Wait();
		job.Publish();
		if (job.Plan == null)
		{
			response = job.Error ?? $"Schematic {name} could not be planned.";
			return false;
		}

		response = Build(name, jsonPath, job, inMap);
		Logger.Info(response);
		return true;
	}

	/// <summary>
	/// Builds the report for a planned schematic.
	/// </summary>
	public static string Build(string name, string jsonPath, SchematicLoadJob job, bool inMap)
	{
		SchematicBuildPlan plan = job.Plan!;
		Config config = ProjectMER.Singleton.Config!;
		StringBuilder sb = new();
		long fileBytes = File.Exists(jsonPath) ? new FileInfo(jsonPath).Length : 0;
		sb.Append($"Schematic \"{name}\" ({fileBytes / 1024.0:F1} KB, {plan.Data.Blocks.Count} blocks, {plan.ReachableBlocks} reachable), planned as {(inMap ? "placed by a map" : "spawned by a plugin")}. ");
		sb.Append(job.WasCached ? "Cached plan (kept until the file changes)." : $"Parsed in {plan.ParseMilliseconds:F1} ms and planned in {plan.PlanMilliseconds:F1} ms on a worker thread; cached until the file changes.");

		OptimizerStep previous = default;
		for (int i = 0; i < plan.Steps.Count; i++)
		{
			OptimizerStep step = plan.Steps[i];
			sb.Append('\n').Append(i == 0 ? "ProjectMER" : $"{i}. {step.Name}").Append(": ");
			sb.Append(step.Networked.ToString(CultureInfo.InvariantCulture)).Append(" networked");
			if (i > 0)
				sb.Append(" (").Append((step.Networked - previous.Networked).ToString("+0;-0;0", CultureInfo.InvariantCulture)).Append(')');

			sb.Append($" = {step.Primitives} primitives, {step.Lights} lights, {step.Pickups + step.Workstations} pickups/workstations; ~{step.EstimatedSpawnBytes / 1024.0:F1} KB of spawn messages per player");
			previous = step;
		}

		sb.Append($"\nNot networked (server-side anchors only): {plan.Empties} empty, {plan.DroppedInvisible} invisible primitives, {plan.DroppedZeroScale} zero-scale");
		if (plan.SkippedInvisibleColliders > 0)
			sb.Append($", {plan.SkippedInvisibleColliders} invisible colliders (invisible_collider_mode: Skip)");

		foreach (KeyValuePair<Features.Enums.BlockType, int> pair in plan.Unsupported)
			sb.Append($", {pair.Value} {pair.Key} (unsupported)");

		sb.Append($"\nLights: {plan.Lights} in the file, {plan.LightsCapped} dropped by max_lights_per_schematic ({config.MaxLightsPerSchematic}); max_lights ({config.MaxLights}) still applies to all loaded content.");
		if (plan.Settings.MergeBlocks)
		{
			sb.Append($"\nDuplicates removed: {plan.Duplicates}. Merge: {plan.MergeCandidates} static cubes and quads considered ({plan.MergeSkippedTranslucent} translucent and {plan.MergeSkippedSheared} sheared or mirrored ones left alone); {plan.MergedSources} merged into {plan.MergedBlocks}.");
		}
		else
		{
			sb.Append($"\nDuplicate removal and merging are off for this schematic (merge_blocks: {config.MergeBlocks}{(config.MergeBlocks == BlockMergeMode.Maps ? ", schematics spawned by plugins keep every block" : string.Empty)}).");
		}

		sb.Append($"\nResult: {plan.Networked} networked ({plan.DynamicBlocks} in animated or physics subtrees, {plan.Transparent} transparent), {plan.Anchors} server-side anchors.");
		if (plan.Networked > config.PrimitiveWarnPerSchematic)
			sb.Append($" Above primitive_warn_per_schematic ({config.PrimitiveWarnPerSchematic}): phones render every networked block.");

		return sb.ToString();
	}
}
