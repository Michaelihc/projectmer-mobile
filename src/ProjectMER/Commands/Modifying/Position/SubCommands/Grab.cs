using CommandSystem;
using LabApi.Features.Permissions;
using LabApi.Features.Wrappers;
using ProjectMER.Features.ToolGun;

namespace ProjectMER.Commands.Modifying.Position.SubCommands;

/// <summary>
/// Grabs the selected object, or drops the grabbed one.
/// </summary>
/// <remarks>
/// Carl Mod port: <see cref="GrabSession"/> moves small objects themselves (static toys and small schematics switch to
/// dynamic while grabbed and are resent in place at full precision on release) and moves only the player's selection box
/// for schematics above <see cref="GrabSession.ProxyThreshold"/> networked blocks, doors and server-only objects, which
/// then move once on release (docs/projectmer-port-plan.md §4).
/// </remarks>
public class Grab : ICommand
{
	public string Command => "grab";

	public string[] Aliases => ["g"];

	public string Description => "Grabs the selected object; run it again to drop it.";

	public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
	{
		if (!sender.HasAnyPermission($"mpr.position"))
		{
			response = $"You don't have permission to execute this command. Required permission: mpr.position";
			return false;
		}

		Player? player = Player.Get(sender);
		if (player is null || player.IsHost)
		{
			response = "This command can't be run from the server console.";
			return false;
		}

		return GrabSession.Toggle(player, out response);
	}
}
