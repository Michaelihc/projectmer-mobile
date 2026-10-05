using CommandSystem;
using LabApi.Features.Permissions;
using NorthwoodLib.Pools;
using ProjectMER.Features;
using ProjectMER.Features.Serializable;

namespace ProjectMER.Commands.Utility;

public class Merge : ICommand
{
	/// <inheritdoc/>
	public string Command => "merge";

	/// <inheritdoc/>
	public string[] Aliases { get; } = [];

	/// <inheritdoc/>
	public string Description => "Merges two or more maps into one.";

	public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
	{
		if (!sender.HasAnyPermission($"mpr.{Command}"))
		{
			response = $"You don't have permission to execute this command. Required permission: mpr.{Command}";
			return false;
		}

		if (arguments.Count < 3)
		{
			response = "\nUsage:\n" +
				"mp merge outputMapName inputMap1 inputMap2 [inputMap3 ...]";

			return false;
		}

		string mapName = arguments.At(0);
		if (!MapUtils.IsValidName(mapName, out string invalid))
		{
			response = invalid;
			return false;
		}

		List<MapSchematic> maps = ListPool<MapSchematic>.Shared.Rent();
		try
		{
			for (int i = 1; i < arguments.Count; i++)
			{
				if (!MapUtils.TryGetMapData(arguments.At(i), out MapSchematic map))
				{
					response = $"Map named {arguments.At(i)} does not exist or is invalid!";
					return false;
				}

				maps.Add(map);
			}

			MapSchematic outputMap = new(mapName);
			foreach (MapSchematic map in maps)
			{
				outputMap.Merge(map);
			}

			File.WriteAllText(MapUtils.GetMapPath(mapName), YamlParser.Serializer.Serialize(outputMap));
		}
		finally
		{
			ListPool<MapSchematic>.Shared.Return(maps);
		}

		response = $"You've successfully merged {arguments.Count - 1} maps into one!";
		return true;
	}
}
