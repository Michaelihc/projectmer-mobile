namespace ProjectMER.Features.Enums;

/// <summary>
/// Order in which the spawn queue networks prepared objects (lower first).
/// </summary>
public enum SpawnPriority : byte
{
	/// <summary>
	/// Collidable blocks and structures, so floors appear before decoration.
	/// </summary>
	Collidable = 0,

	/// <summary>
	/// Visible, non-collidable blocks.
	/// </summary>
	Visible = 1,

	/// <summary>
	/// Light sources.
	/// </summary>
	Light = 2,

	/// <summary>
	/// Item pickups.
	/// </summary>
	Pickup = 3,
}
