namespace ProjectMER.Features.Enums;

/// <summary>
/// How a primitive with only <c>PrimitiveFlags.Collidable</c> spawns. Carl Mod always renders primitives.
/// </summary>
public enum InvisibleColliderMode
{
	/// <summary>
	/// A collidable primitive with zero alpha. It still renders (transparent pass) on the client.
	/// </summary>
	Transparent = 0,

	/// <summary>
	/// Not spawned at all; the collision is lost.
	/// </summary>
	Skip = 1,
}
