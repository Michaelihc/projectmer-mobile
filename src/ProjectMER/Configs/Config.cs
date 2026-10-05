using System.ComponentModel;
using ProjectMER.Features.Enums;

namespace ProjectMER.Configs;

public class Config
{
	[Description("Enables FileSystemWatcher in this plugin. What it does is when you manually change values in a currently loaded map file, after saving the file the plugin will automatically reload the map in-game with the new changes so you won't need to do it yourself.")]
    public bool EnableFileSystemWatcher { get; set; } = false;

	[Description("Whether the object will be auto selected when spawning it.")]
	public bool AutoSelect { get; set; } = true;

	[Description(
	"\n" +
	"# ------------------------------Actions on event------------------------------\n" +
	"# Below is the list of in-game events that you can use to call certain action.\n" +
	"# ----------------------------------------------------------------------------\n" +
	"# \n" +
	"# Map loading/unloading\n" +
	"# You can use it to load or unload a map on demand. It supports basic pattern matching, loading/unloading multiple maps or loading/unloading a random map from a list. Loading the same map again reloads it. Unloading the already unloaded map won't do anything.\n" +
	"# \n" +
	"# - load:CoolMap\n" +
	"#   Loads a map that is called CoolMap\n" +
	"# \n" +
	"# - unload:CoolMap\n" +
	"#   Unloads a map that is called CoolMap\n" +
	"# \n" +
	"# - load:LczMap,HczMap,EzMap\n" +
	"#   Loads ALL of the maps listed. You can also just load them individualy with multiple loads.\n" +
	"# \n" +
	"# - load:VariantA||VariantB||VariantC\n" +
	"#   Loads ONE of the maps listed, chances are equal, you can increase them by typing same map name multiple times\n" +
	"# \n" +
	"# - load:*\n" +
	"#   Loads all saved maps\n" +
	"# \n" +
	"# - unload:*\n" +
	"#   Loads all loaded maps, including the Untitled one\n" +
	"# \n" +
	"# Console command\n" +
	"# You can use it to run a custom console command. Remote Admin commands must be prefixed with \"/\"\n" +
	"# \n" +
	"# - console:buildinfo\n" +
	"#   Prints a buildinfo of the server\n" +
	"# \n" +
	"# - console:/bc 10 MER is cool\n" +
	"#   Sends a broadcast to all players\n"
	)]
	public List<string> OnWaitingForPlayers { get; set; } = [];
	public List<string> OnRoundStarted { get; set; } = [];
	public List<string> OnLczDecontaminationStarted { get; set; } = [];
	public List<string> OnWarheadStarted { get; set; } = [];
	public List<string> OnWarheadStopped { get; set; } = [];
	public List<string> OnWarheadDetonated { get; set; } = [];

	[Description(
	"\n" +
	"# ------------------------------Mobile client options------------------------------\n" +
	"# Carl Mod phones render every networked object. These options keep MER content cheap for them.\n" +
	"# ---------------------------------------------------------------------------------\n" +
	"# Schematic blocks spawn as static toys (no per-frame sync) unless they are in an animated or physics subtree.\n" +
	"# Off: ProjectMER's rule (only blocks exported with \"Static\": true are static).")]
	public bool StaticByDefault { get; set; } = true;

	[Description("With static_by_default, also honor \"Static\": false from the export (dynamic toy). Exporters write false for blocks that never move, so this is off.")]
	public bool HonorStaticProperty { get; set; } = false;

	[Description("Most networked objects spawned per server frame while maps and schematics stream in.")]
	public int SpawnMaxPerFrame { get; set; } = 20;

	[Description("Time budget in milliseconds per server frame for spawning queued objects.")]
	public float SpawnTimeBudgetMs { get; set; } = 3f;

	[Description("Seconds between transform updates of animated or physics schematic blocks (0 = every frame).")]
	public float DynamicToySyncInterval { get; set; } = 0.1f;

	[Description("Whether MER lights may cast shadows. Shadowed point lights are very expensive on phones.")]
	public bool AllowLightShadows { get; set; } = false;

	[Description("Most MER lights across all loaded maps and schematics. Weaker lights (intensity x range) beyond the cap are skipped.")]
	public int MaxLights { get; set; } = 16;

	[Description("Warn when one schematic spawns more networked blocks than this (after simplification).")]
	public int PrimitiveWarnPerSchematic { get; set; } = 300;

	[Description("Warn when all MER content together exceeds this many networked objects.")]
	public int NetworkedWarnTotal { get; set; } = 1500;

	[Description("Refuse further MER spawns above this many networked objects.")]
	public int NetworkedHardCap { get; set; } = 4000;

	[Description("How Collidable-only (invisible collider) primitives spawn: Transparent (alpha 0, still rendered) or Skip.")]
	public InvisibleColliderMode InvisibleColliderMode { get; set; } = InvisibleColliderMode.Transparent;

	[Description("Drop schematic blocks that produce nothing on the client (empty groups, invisible non-colliding primitives, zero scale).")]
	public bool OptimizeSchematics { get; set; } = true;

	[Description("Per-player visibility of MER objects through Mirror observers (join streaming, admin-only indicators).")]
	public bool ManagedVisibility { get; set; } = true;

	[Description("Zone culling of large MER groups: None, SurfaceFacility or PerZone.")]
	public ZoneCullingMode ZoneCulling { get; set; } = ZoneCullingMode.SurfaceFacility;

	[Description("Only groups with at least this many networked objects are zone culled.")]
	public int ZoneCullingMinObjects { get; set; } = 150;

	[Description("Seconds between tool gun HUD keep-alive updates.")]
	public float HudInterval { get; set; } = 0.5f;

	[Description("Keep ProjectMER's warhead rule: positions below the surface that are outside every room survive the detonation.")]
	public bool WarheadSparesOutsideRooms { get; set; } = true;

	[Description("Log spawn queue throughput when a batch of spawns finishes.")]
	public bool LogSpawnStats { get; set; } = true;
}
