namespace ProjectMER.Features.Enums;

/// <summary>
/// All available blocks for schematic object.
/// </summary>
public enum BlockType
{
	/// <summary>
	/// Represents an empty transform.
	/// </summary>
	Empty = 0,

	/// <summary>
	/// Represents a primitive.
	/// </summary>
	Primitive = 1,

	/// <summary>
	/// Represents a light.
	/// </summary>
	Light = 2,

	/// <summary>
	/// Represents a pickup.
	/// </summary>
	Pickup = 3,

	/// <summary>
	/// Represents a workstation.
	/// </summary>
	Workstation = 4,

	/// <summary>
	/// Represents a sub-schematic.
	/// </summary>
	Schematic = 5,

	/// <summary>
	/// Represents a teleporter.
	/// </summary>
	Teleport = 6,

	/// <summary>
	/// Represents a locker.
	/// </summary>
	Locker = 7,

	// Carl Mod: Text, Interactable and Waypoint toys do not exist in the client and a triangle needs a sheared
	// (parented) quad, so these blocks are skipped with one warning per type. Their children still spawn.
	Text = 8,
	Interactable = 9,
	Waypoint = 10,
	Triangle = 11,
}
