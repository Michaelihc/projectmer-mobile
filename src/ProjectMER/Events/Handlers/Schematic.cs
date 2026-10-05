using LabApi.Events;
using ProjectMER.Events.Arguments;

namespace ProjectMER.Events.Handlers;

public static class Schematic
{
	public static event LabEventHandler<SchematicSpawningEventArgs> SchematicSpawning;

	/// <summary>
	/// Raised synchronously once the schematic's server-side objects exist. Its networked blocks are still being spawned
	/// for clients by the spawn queue; see <see cref="SchematicBuilt"/>.
	/// </summary>
	public static event LabEventHandler<SchematicSpawnedEventArgs> SchematicSpawned;

	/// <summary>
	/// Raised once every networked block of a schematic has been spawned for clients (Carl Mod port addition).
	/// </summary>
	public static event LabEventHandler<SchematicBuiltEventArgs> SchematicBuilt;

	public static event LabEventHandler<ButtonInteractedEventArgs> ButtonInteracted;

	public static event LabEventHandler<SchematicDestroyedEventArgs> SchematicDestroyed;

	/// <summary>
	/// Gets whether anything handles <see cref="SchematicSpawning"/> (the data is only copied for handlers).
	/// </summary>
	public static bool HasSchematicSpawningSubscribers => SchematicSpawning != null;

	internal static void OnSchematicSpawning(SchematicSpawningEventArgs ev) => SchematicSpawning.InvokeEvent(ev);

	internal static void OnSchematicSpawned(SchematicSpawnedEventArgs ev) => SchematicSpawned.InvokeEvent(ev);

	internal static void OnSchematicBuilt(SchematicBuiltEventArgs ev) => SchematicBuilt.InvokeEvent(ev);

	internal static void OnButtonInteracted(ButtonInteractedEventArgs ev) => ButtonInteracted.InvokeEvent(ev);

	internal static void OnSchematicDestroyed(SchematicDestroyedEventArgs ev) => SchematicDestroyed.InvokeEvent(ev);
}
