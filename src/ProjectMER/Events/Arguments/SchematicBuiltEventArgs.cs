using ProjectMER.Events.Arguments.Interfaces;
using ProjectMER.Features.Objects;

namespace ProjectMER.Events.Arguments;

/// <summary>
/// Arguments of <see cref="Handlers.Schematic.SchematicBuilt"/>: every networked block of the schematic has been spawned.
/// </summary>
public class SchematicBuiltEventArgs : EventArgs, ISchematicEvent
{
	public SchematicBuiltEventArgs(SchematicObject schematic, string name)
	{
		Schematic = schematic;
		Name = name;
	}

	public SchematicObject Schematic { get; }

	public string Name { get; }
}
