using LabApi.Features.Wrappers;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Interfaces;
using ProjectMER.Features.Objects;
using UnityEngine;

namespace ProjectMER.Features.Serializable;

/// <summary>
/// A teleport of a map: a server-only trigger <see cref="BoxCollider"/>.
/// </summary>
public class SerializableTeleport : SerializableObject, IIndicatorDefinition
{
	public List<string> Targets { get; set; } = [];

	public float Cooldown { get; set; } = 5f;

	public override GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		GameObject gameObject = instance ?? new GameObject("Teleport");
		Vector3 position = room.GetAbsolutePosition(Position);
		Quaternion rotation = room.GetAbsoluteRotation(Rotation);
		_prevIndex = Index;
		gameObject.transform.SetLocalPositionAndRotation(position, rotation);

		if (instance == null)
			gameObject.AddComponent<TeleportObject>();

		if (!gameObject.TryGetComponent(out BoxCollider boxCollider))
			boxCollider = gameObject.AddComponent<BoxCollider>();

		boxCollider.isTrigger = true;
		boxCollider.size = Scale;

		return gameObject;
	}

	/// <summary>
	/// The trigger volume and an arrow showing the exit direction, in world space.
	/// </summary>
	public GameObject SpawnOrUpdateIndicator(Room room, GameObject? instance = null)
	{
		Vector3 position = room.GetAbsolutePosition(Position);
		Quaternion rotation = room.GetAbsoluteRotation(Rotation);
		GameObject root = IndicatorObject.CreateRoot(instance, "Indicator", position, Quaternion.identity);

		Color color = Targets.Count > 0 ? new Color(0.11f, 0.98f, 0.92f, 0.5f) : new Color(1f, 1f, 1f, 0.25f);

		Quaternion arrowRotation = Quaternion.Euler(0f, rotation.eulerAngles.y, 0f) * Quaternion.Euler(-rotation.eulerAngles.x, 0f, 0f);
		Vector3 arrowPosition = position + (Vector3.up * 0.6f) + (arrowRotation * Vector3.forward);

		IndicatorObject.SetPart(root, 0, PrimitiveType.Cube, position, rotation, Scale, color);
		IndicatorObject.SetPart(root, 1, PrimitiveType.Cube, arrowPosition, arrowRotation, new Vector3(0.1f, 0.1f, 1f), color);

		return root;
	}
}
