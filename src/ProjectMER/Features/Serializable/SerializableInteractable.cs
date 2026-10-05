using LabApi.Features.Wrappers;
using ProjectMER.Features.Enums;
using UnityEngine;

namespace ProjectMER.Features.Serializable;

/// <summary>
/// An interactable of a map. Data only: the Carl Mod client has no InvisibleInteractableToy, so it is skipped with a
/// warning and kept in saved maps. The closest working alternative is a locked schematic pickup ("button").
/// </summary>
public class SerializableInteractable : SerializableObject
{
	public ColliderShape ColliderShape { get; set; } = ColliderShape.Box;
	public float InteractionDuration { get; set; } = 0f;
	public bool IsLocked { get; set; } = false;

	public override GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		_prevIndex = Index;
		UnsupportedContent.Skip("interactables", "InvisibleInteractableToy is not available in the Carl Mod client");
		return null;
	}
}
