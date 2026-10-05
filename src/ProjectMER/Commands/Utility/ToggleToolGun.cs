using CommandSystem;
using LabApi.Features.Permissions;
using LabApi.Features.Wrappers;
using ProjectMER.Features.Enums;
using ProjectMER.Features.ToolGun;

namespace ProjectMER.Commands;

/// <summary>
/// Gives or removes the tool gun, and picks what it creates.
/// </summary>
/// <remarks>
/// Carl Mod has no server-specific settings, so <c>mp tg schematic &lt;name|index&gt;</c> replaces ProjectMER's schematic
/// dropdown (indices follow <c>mp list</c>), <c>mp tg type &lt;type&gt;</c> picks the object type and
/// <c>mp tg mode &lt;create|delete&gt;</c> sets the mode. All three are per player and work without holding the tool gun.
/// </remarks>
public class ToggleToolGun : ICommand
{
	public string Command => "toolgun";

	public string[] Aliases => ["tg"];

	public string Description => "Gives or removes the tool gun. Subcommands: schematic <name|index>, type <type>, mode <create|delete>.";

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

		if (arguments.Count >= 1)
		{
			string value = arguments.Count >= 2 ? arguments.At(1) : string.Empty;
			switch (arguments.At(0).ToLowerInvariant())
			{
				case "schematic":
				case "s":
					return SetSchematic(player, value, out response);

				case "type":
				case "t":
					return SetType(player, value, out response);

				case "mode":
				case "m":
					return SetMode(player, value, out response);
			}
		}

		if (ToolGunItem.Remove(player))
		{
			response = "You no longer have a Tool Gun!";
			return true;
		}

		if (ToolGunItem.TryAdd(player))
		{
			response = $"You now have the Tool Gun! Fire: create/delete, aim + fire: select, inspect or light: switch create/delete, reload/drop: previous/next type.";
			return true;
		}

		response = "You have a full inventory!";
		return false;
	}

	private static bool SetSchematic(Player player, string value, out string response)
	{
		ToolGunItem.RefreshSchematicNames(force: true);
		string[] names = ToolGunItem.SchematicNames;
		string? name = null;
		if (int.TryParse(value, out int index) && index >= 1 && index <= names.Length)
		{
			name = names[index - 1];
		}
		else
		{
			foreach (string candidate in names)
			{
				if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
				{
					name = candidate;
					break;
				}
			}
		}

		if (name == null)
		{
			response = $"No schematic named or numbered \"{value}\". See mp list.";
			return false;
		}

		ToolGunState state = ToolGunState.Get(player);
		state.SchematicName = name;
		state.ObjectType = ToolGunObjectType.Schematic;
		state.Mode = ToolGunMode.Create;
		SyncToolGun(player, state);
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

		ToolGunState state = ToolGunState.Get(player);
		state.ObjectType = objectType;
		state.Mode = ToolGunMode.Create;
		SyncToolGun(player, state);
		response = objectType == ToolGunObjectType.Schematic && string.IsNullOrEmpty(state.SchematicName)
			? "Tool gun creates schematics; pick one with mp tg schematic <name|index>."
			: $"Tool gun creates {objectType}.";
		return true;
	}

	private static bool SetMode(Player player, string value, out string response)
	{
		ToolGunState state = ToolGunState.Get(player);
		switch (value.ToLowerInvariant())
		{
			case "create":
			case "c":
				state.Mode = ToolGunMode.Create;
				break;

			case "delete":
			case "d":
				state.Mode = ToolGunMode.Delete;
				break;

			default:
				response = "Usage: mp tg mode <create|delete>. Aim + fire selects in either mode.";
				return false;
		}

		SyncToolGun(player, state);
		response = $"Tool gun mode: {state.Mode}.";
		return true;
	}

	private static void SyncToolGun(Player player, ToolGunState state)
	{
		foreach (KeyValuePair<ushort, ToolGunItem> pair in ToolGunItem.ItemDictionary)
		{
			if (pair.Value.Firearm != null && pair.Value.Firearm.Owner == player.ReferenceHub)
				pair.Value.ApplyStatus(state.Mode == ToolGunMode.Create);
		}

		ToolGunHud.Refresh(state);
	}
}
