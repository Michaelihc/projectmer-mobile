global using Logger = LabApi.Features.Console.Logger;

using HarmonyLib;
using LabApi.Events.CustomHandlers;
using LabApi.Loader.Features.Paths;
using LabApi.Loader.Features.Plugins;
using ProjectMER.Configs;
using ProjectMER.Events.Handlers.Internal;
using ProjectMER.Features;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.ToolGun;

namespace ProjectMER;

public class ProjectMER : Plugin<Config>
{
	private Harmony _harmony;
	private FileSystemWatcher _mapFileSystemWatcher;

	public static ProjectMER Singleton { get; private set; }

	/// <summary>
	/// Gets the MapEditorReborn parent folder path.
	/// </summary>
	public static string PluginDir { get; private set; }

	/// <summary>
	/// Gets the folder path in which the maps are stored.
	/// </summary>
	public static string MapsDir { get; private set; }

	/// <summary>
	/// Gets the folder path in which the schematics are stored.
	/// </summary>
	public static string SchematicsDir { get; private set; }

	public GenericEventsHandler GenericEventsHandler { get; } = new();

	public ToolGunEventsHandler ToolGunEventsHandler { get; } = new();

	public ActionOnEventHandlers AcionOnEventHandlers { get; } = new();

	public PickupEventsHandler PickupEventsHandler { get; } = new();

	public override void Enable()
	{
		Singleton = this;

		PluginDir = Path.Combine(PathManager.Configs.FullName, "ProjectMER");
		MapsDir = Path.Combine(PluginDir, "Maps");
		SchematicsDir = Path.Combine(PluginDir, "Schematics");

		if (!Directory.Exists(PluginDir))
		{
			Logger.Warn("Plugin directory does not exist. Creating...");
			Directory.CreateDirectory(PluginDir);
		}

		if (!Directory.Exists(MapsDir))
		{
			Logger.Warn("Maps directory does not exist. Creating...");
			Directory.CreateDirectory(MapsDir);
		}

		if (!Directory.Exists(SchematicsDir))
		{
			Logger.Warn("Schematics directory does not exist. Creating...");
			Directory.CreateDirectory(SchematicsDir);
		}

		CustomHandlersManager.RegisterEventsHandler(GenericEventsHandler);
		CustomHandlersManager.RegisterEventsHandler(ToolGunEventsHandler);
		CustomHandlersManager.RegisterEventsHandler(AcionOnEventHandlers);
		CustomHandlersManager.RegisterEventsHandler(PickupEventsHandler);

		// ProjectMER patched twice with two Harmony instances; one PatchAll is enough.
		_harmony = new Harmony($"michal78900.mapEditorReborn-{DateTime.Now.Ticks}");
		_harmony.PatchAll(typeof(ProjectMER).Assembly);

		SpawnQueue.Start();
		MerVisibility.Start();

		if (Config!.EnableFileSystemWatcher)
		{
			MainThreadQueue.Start();

			_mapFileSystemWatcher = new FileSystemWatcher(MapsDir)
			{
				NotifyFilter = NotifyFilters.LastWrite,
				Filter = "*.yml",
				EnableRaisingEvents = true,
			};

			_mapFileSystemWatcher.Changed += OnMapFileChanged;

			Logger.Debug("FileSystemWatcher enabled!");
		}

		Logger.Info($"ProjectMER {Version} (Carl Mod port) enabled. Maps: {MapsDir}");
	}

	/// <summary>
	/// Runs on a thread-pool thread: only hand the map name to the main thread.
	/// </summary>
	private void OnMapFileChanged(object _, FileSystemEventArgs ev)
	{
		string mapName = Path.GetFileNameWithoutExtension(ev.Name);
		MainThreadQueue.EnqueueMapReload(mapName);
	}

	public override void Disable()
	{
		Singleton = null!;

		CustomHandlersManager.UnregisterEventsHandler(GenericEventsHandler);
		CustomHandlersManager.UnregisterEventsHandler(ToolGunEventsHandler);
		CustomHandlersManager.UnregisterEventsHandler(AcionOnEventHandlers);
		CustomHandlersManager.UnregisterEventsHandler(PickupEventsHandler);

		_harmony?.UnpatchAll(_harmony.Id);
		_mapFileSystemWatcher?.Dispose();

		MainThreadQueue.Stop();
		ToolGunLoop.Stop();
		MerVisibility.Stop();
		SpawnQueue.Stop();
	}

	public override string Name => "ProjectMER";

	public override string Description => "MER LabAPI (Carl Mod port)";

	public override string Author => "Michal78900";

	public override Version Version => new Version(2025, 11, 2, 1);

	public override Version RequiredApiVersion => new Version(1, 0, 0, 0);
}
