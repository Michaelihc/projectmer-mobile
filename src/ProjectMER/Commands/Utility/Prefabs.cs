using CommandSystem;
using LabApi.Features.Permissions;
using ProjectMER.Features;

namespace ProjectMER.Commands.Utility;

/// <summary>
/// <c>mp prefabs</c>: name, asset id and components of every network prefab registered with Mirror.
/// </summary>
/// <remarks>The dump is also written to the server log (the file console does not return command output).</remarks>
public class Prefabs : ICommand
{
	public string Command => "prefabs";

	public string[] Aliases => ["pf"];

	public string Description => "Lists every network prefab (name, asset id, components).";

	public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
	{
		if (!sender.HasAnyPermission($"mpr.{Command}"))
		{
			response = $"You don't have permission to execute this command. Required permission: mpr.{Command}";
			return false;
		}

		response = PrefabManager.Dump();
		Logger.Info(response);
		return true;
	}
}
