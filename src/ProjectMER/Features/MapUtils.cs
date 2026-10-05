using Newtonsoft.Json;
using ProjectMER.Features.Objects;
using ProjectMER.Features.Serializable;
using ProjectMER.Features.Serializable.Schematics;
using ProjectMER.Features.Serialization;
using YamlDotNet.Core;
using FileStamp = ProjectMER.Features.Serialization.SchematicJson.FileStamp;

namespace ProjectMER.Features;

public static class MapUtils
{
	public const string UntitledMapName = "Untitled";

	private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).Distinct().ToArray();

	private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"CON", "PRN", "AUX", "NUL",
		"COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
		"LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
	};

	// Stamps of map files when the server last read them (main thread only).
	private static readonly Dictionary<string, FileStamp> ReadStamps = [];

	public static MapSchematic UntitledMap => LoadedMaps.GetOrAdd(UntitledMapName, () => new(UntitledMapName));

	public static Dictionary<string, MapSchematic> LoadedMaps { get; private set; } = [];

	/// <summary>
	/// Saves a map with the objects created since (the Untitled map), and loads it again.
	/// </summary>
	/// <param name="mapName">The map name, a plain file name (<see cref="IsValidName"/>).</param>
	/// <exception cref="ArgumentException">The name is not a plain file name.</exception>
	/// <remarks>When the map is not loaded and its file exists but cannot be read (YAML errors, locked), the read error is
	/// thrown and nothing is written; ProjectMER replaced such a file with the Untitled objects alone.</remarks>
	public static void SaveMap(string mapName)
	{
		if (mapName == UntitledMapName)
			throw new InvalidOperationException("This map name is reserved for internal use!");

		string path = GetMapPath(mapName);
		if (LoadedMaps.TryGetValue(mapName, out MapSchematic map)) // Map is already loaded
		{
			map.Merge(UntitledMap);
		}
		else if (File.Exists(path)) // Map isn't loaded but map file exists; a read error aborts the save
		{
			map = GetMapData(mapName);
			map.Merge(UntitledMap);
		}
		else // Map isn't loaded and map file doesn't exist
		{
			map = new MapSchematic(mapName).Merge(UntitledMap);
		}

		File.WriteAllText(path, YamlParser.Serializer.Serialize(map));
		map.IsDirty = false;

		UnloadMap(UntitledMapName);
		LoadMap(mapName);
	}

	public static void LoadMap(string mapName)
	{
		MapSchematic map = GetMapData(mapName);
		UnloadMap(mapName);
		map.Reload();

		LoadedMaps.Add(mapName, map);
	}

	public static bool UnloadMap(string mapName)
	{
		if (!LoadedMaps.TryGetValue(mapName, out MapSchematic map))
			return false;

		map.DestroyAll();

		LoadedMaps.Remove(mapName);
		return true;
	}

	public static bool TryGetMapData(string mapName, out MapSchematic mapSchematic)
	{
		try
		{
			mapSchematic = GetMapData(mapName);
			return true;
		}
		catch (Exception)
		{
			mapSchematic = null!;
			return false;
		}
	}

	public static MapSchematic GetMapData(string mapName)
	{
		MapSchematic map;

		string path = GetMapPath(mapName);
		if (!File.Exists(path))
		{
			string error = $"Failed to load map data: File {mapName}.yml does not exist!";
			throw new FileNotFoundException(error);
		}

		try
		{
			// The stamp before the read: a write after it still makes the file watcher reload the map.
			FileStamp stamp = FileStamp.Of(path);
			map = YamlParser.Deserializer.Deserialize<MapSchematic>(File.ReadAllText(path));
			map.Name = mapName;
			ReadStamps[mapName] = stamp;
		}
		catch (YamlException e)
		{
			string error = $"Failed to load map data: File {mapName}.yml has YAML errors!\n{e.ToString().Split('\n')[0]}";
			throw new YamlException(error);
		}

		return map;
	}

	public static bool TryGetSchematicDataByName(string schematicName, out SchematicObjectDataList data)
	{
		try
		{
			data = GetSchematicDataByName(schematicName);
			return true;
		}
		catch (Exception)
		{
			data = null!;
			return false;
		}
	}

	/// <summary>
	/// Checks that a schematic file exists (moving a bare <c>Schematics/&lt;name&gt;.json</c> into its folder, as
	/// <see cref="GetSchematicDataByName"/> does) without parsing it, so commands can validate a name without a hitch.
	/// </summary>
	/// <param name="schematicName">The schematic name.</param>
	/// <param name="error">The reason when it does not exist.</param>
	public static bool SchematicFileExists(string schematicName, out string error)
	{
		if (!IsValidName(schematicName, out error))
			return false;

		string schematicDirPath = Path.Combine(ProjectMER.SchematicsDir, schematicName);
		string schematicJsonPath = Path.Combine(schematicDirPath, $"{schematicName}.json");
		string misplacedSchematicJsonPath = schematicDirPath + ".json";
		if (File.Exists(schematicJsonPath))
			return true;

		if (File.Exists(misplacedSchematicJsonPath))
		{
			Directory.CreateDirectory(schematicDirPath);
			File.Move(misplacedSchematicJsonPath, schematicJsonPath);
			return true;
		}

		error = Directory.Exists(schematicDirPath)
			? $"Failed to load schematic data: File {schematicName}.json does not exist!"
			: $"Failed to load schematic data: Directory {schematicName} does not exist!";
		return false;
	}

	/// <summary>
	/// Gets a schematic's data. The result is cached while the file is unchanged and shared between spawns; treat it as
	/// read-only.
	/// </summary>
	public static SchematicObjectDataList GetSchematicDataByName(string schematicName)
	{
		if (!IsValidName(schematicName, out string invalid))
			throw new ArgumentException(invalid, nameof(schematicName));

		SchematicObjectDataList data;
		string schematicDirPath = Path.Combine(ProjectMER.SchematicsDir, schematicName);
		string schematicJsonPath = Path.Combine(schematicDirPath, $"{schematicName}.json");
		string misplacedSchematicJsonPath = schematicDirPath + ".json";

		if (!Directory.Exists(schematicDirPath))
		{
			// Some users may throw a single JSON file into Schematics folder, this automatically creates and moved the file to the correct schematic directory.
			if (File.Exists(misplacedSchematicJsonPath))
			{
				Directory.CreateDirectory(schematicDirPath);
				File.Move(misplacedSchematicJsonPath, schematicJsonPath);
				return GetSchematicDataByName(schematicName);
			}

			string error = $"Failed to load schematic data: Directory {schematicName} does not exist!";
			Logger.Error(error);
			throw new DirectoryNotFoundException(error);
		}

		if (!File.Exists(schematicJsonPath))
		{
			// Same as above but with the folder existing and file not being there for some reason.
			if (File.Exists(misplacedSchematicJsonPath))
			{
				File.Move(misplacedSchematicJsonPath, schematicJsonPath);
				return GetSchematicDataByName(schematicName);
			}

			string error = $"Failed to load schematic data: File {schematicName}.json does not exist!";
			Logger.Error(error);
			throw new FileNotFoundException(error);
		}

		try
		{
			data = SchematicJson.ReadCached<SchematicObjectDataList>(schematicJsonPath);
			data.Path = schematicDirPath;
		}
		catch (JsonException e)
		{
			string error = $"Failed to load schematic data: File {schematicName}.json has JSON errors!\n{e.ToString().Split('\n')[0]}";
			Logger.Error(error);
			throw new JsonSerializationException(error);
		}

		return data;
	}

	/// <summary>
	/// Gets whether a map or schematic name is a plain file name, so its files stay inside the Maps or Schematics folder:
	/// not empty, without folder separators or other characters that are invalid in file names, not ending in a dot or a
	/// space (Windows drops them), and not a reserved device name such as <c>CON</c> or <c>NUL</c>.
	/// </summary>
	/// <param name="name">The name.</param>
	/// <param name="error">Why the name is refused.</param>
	public static bool IsValidName(string? name, out string error)
	{
		error = string.Empty;
		if (string.IsNullOrWhiteSpace(name))
		{
			error = "A map or schematic name is required.";
			return false;
		}

		if (name!.IndexOfAny(InvalidNameChars) >= 0 || name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
		{
			error = $"\"{name}\" is not a valid map or schematic name: use a plain file name, without folders or the characters \\ / : * ? \" < > |, that does not end in a dot.";
			return false;
		}

		int dot = name.IndexOf('.');
		if (ReservedNames.Contains((dot < 0 ? name : name.Substring(0, dot)).TrimEnd(' ')))
		{
			error = $"\"{name}\" is a reserved device name on Windows.";
			return false;
		}

		return true;
	}

	/// <summary>
	/// Gets the file of a map.
	/// </summary>
	/// <exception cref="ArgumentException">The name is not a plain file name (<see cref="IsValidName"/>).</exception>
	internal static string GetMapPath(string mapName)
	{
		if (!IsValidName(mapName, out string error))
			throw new ArgumentException(error, nameof(mapName));

		return Path.Combine(ProjectMER.MapsDir, $"{mapName}.yml");
	}

	/// <summary>
	/// Gets whether a map file is unchanged since the server last read it. The file watcher then skips the reload: the
	/// server's own save writes the file and loads the map itself.
	/// </summary>
	internal static bool IsUnchangedSinceRead(string mapName) =>
		ReadStamps.TryGetValue(mapName, out FileStamp read) && IsValidName(mapName, out _) &&
		FileStamp.Of(Path.Combine(ProjectMER.MapsDir, $"{mapName}.yml")) == read;

	public static string[] GetAvailableSchematicNames() => Directory.GetFiles(ProjectMER.SchematicsDir, "*.json", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension).Where(x => !x.Contains('-')).ToArray();

	public static string GetColoredMapName(string mapName)
	{
		if (mapName == UntitledMapName)
			return $"<color=grey><b><i>{UntitledMapName}</i></b></color>";

		bool isDirty = false;
		if (LoadedMaps.TryGetValue(mapName, out MapSchematic mapSchematic))
			isDirty = mapSchematic.IsDirty;

		return isDirty ? $"<i>{GetColoredString(mapName)}</i>" : GetColoredString(mapName);
	}

	public static string GetColoredString(string s)
	{
		uint value = Math.Min(((uint)s.GetHashCode()) / 255, 16777215);
		string colorHex = value.ToString("X6");
		return $"<color=#{colorHex}><b>{s}</b></color>";
	}
}
