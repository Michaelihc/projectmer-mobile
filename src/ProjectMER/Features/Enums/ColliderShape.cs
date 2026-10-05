namespace ProjectMER.Features.Enums;

/// <summary>
/// Collider shape of an interactable (official <c>InvisibleInteractableToy.ColliderShape</c>, absent from Carl Mod).
/// Kept so maps with interactables load and save unchanged.
/// </summary>
public enum ColliderShape
{
	Box = 0,
	Sphere = 1,
	Capsule = 2,
}
