using CommandSystem;
using LabApi.Features.Permissions;
using LabApi.Features.Wrappers;
using ProjectMER.Features;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.ToolGun;

namespace ProjectMER.Commands;

/// <summary>
/// Gives or removes the tool gun, and picks what it creates.
/// </summary>
/// <remarks>
/// Carl Mod has no server-specific settings, so <c>mp tg schematic &lt;name|index&gt;</c> replaces ProjectMER's schematic
/// dropdown (indices follow <c>mp list</c>), and <c>mp tg type &lt;type&gt;</c> picks the object type.
/// </remarks>
public class ToggleToolGun : ICommand
{
	public string Command => "toolgun";

	public string[] Aliases => ["tg"];

	public string Description => "Tool gun for spawning and editing objects. Subcommands: schematic <name|index>, type <type>.";

	public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
	{
		if (!sender.HasAnyPermission($"mpr.{Command}"))
		{
			response = $"You don't have permission to execute this command. Required permission: mpr.{Command}";
			return false;
		}

		Player? player = Player.Get(sender);
		if (player is null)
		{
			response = "This command can't be run from the server console.";
			return false;
		}

		if (arguments.Count >= 2)
		{
			switch (arguments.At(0).ToLowerInvariant())
			{
				case "schematic":
				case "s":
					return SetSchematic(player, arguments.At(1), out response);

				case "type":
				case "t":
					return SetType(player, arguments.At(1), out response);
			}
		}

		if (ToolGunItem.Remove(player))
		{
			response = "You no longer have a Tool Gun!";
			return true;
		}

		if (ToolGunItem.TryAdd(player))
		{
			response = "You now have the Tool Gun!";
			return true;
		}

		response = "You have a full inventory!";
		return false;
	}

	private static bool SetSchematic(Player player, string value, out string response)
	{
		string[] names = MapUtils.GetAvailableSchematicNames();
		string? name = null;
		if (int.TryParse(value, out int index) && index >= 1 && index <= names.Length)
			name = names[index - 1];
		else
			name = names.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));

		if (name == null)
		{
			response = $"No schematic named or numbered \"{value}\". See mp list.";
			return false;
		}

		ToolGunItem.SelectedSchematics[player] = name;
		if (player.CurrentItem.IsToolGun(out ToolGunItem toolGun))
			toolGun.SelectedObjectToSpawn = ToolGunObjectType.Schematic;

		response = $"Tool gun creates schematic {name}.";
		return true;
	}

	private static bool SetType(Player player, string value, out string response)
	{
		if (!Enum.TryParse(value, true, out ToolGunObjectType objectType) || !Enum.IsDefined(typeof(ToolGunObjectType), objectType))
		{
			response = $"Unknown type \"{value}\". Types: {string.Join(", ", ToolGunItem.TypesDictionary.Keys)}.";
			return false;
		}

		string? reason = ToolGunItem.GetUnsupportedReason(objectType);
		if (reason != null)
		{
			response = $"{objectType} cannot be created in Carl Mod: {reason}.";
			return false;
		}

		if (!player.CurrentItem.IsToolGun(out ToolGunItem toolGun))
		{
			response = "Hold the tool gun first.";
			return false;
		}

		toolGun.SelectedObjectToSpawn = objectType;
		response = $"Tool gun creates {objectType}.";
		return true;
	}
}
