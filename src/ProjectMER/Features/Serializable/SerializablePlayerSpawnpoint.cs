using LabApi.Features.Wrappers;
using PlayerRoles;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Interfaces;
using ProjectMER.Features.Objects;
using UnityEngine;
using YamlDotNet.Serialization;

namespace ProjectMER.Features.Serializable;

public class SerializablePlayerSpawnpoint : SerializableObject, IIndicatorDefinition
{
	public List<RoleTypeId> Roles { get; set; } = [];

	[YamlIgnore]
	public override Vector3 Scale { get; set; }

	public override GameObject SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		GameObject spawnpoint = instance ?? new GameObject("PlayerSpawnpoint");
		Vector3 position = room.GetAbsolutePosition(Position);
		Quaternion rotation = room.GetAbsoluteRotation(Rotation);
		_prevIndex = Index;

		spawnpoint.transform.SetPositionAndRotation(position, rotation);

		return spawnpoint.gameObject;
	}

	/// <summary>
	/// A disc at the spawnpoint and an arrow at head height showing the look direction, in world space.
	/// </summary>
	public GameObject SpawnOrUpdateIndicator(Room room, GameObject? instance = null)
	{
		Vector3 position = room.GetAbsolutePosition(Position);
		Quaternion rotation = room.GetAbsoluteRotation(Rotation);
		GameObject root = IndicatorObject.CreateRoot(instance, "Indicator", position, Quaternion.identity);

		Color color = new(1f, 1f, 1f, 0.25f);
		int known = 0;
		Color colorSum = new(0f, 0f, 0f, 1f);
		foreach (RoleTypeId roleType in Roles)
		{
			if (!PlayerRoleLoader.TryGetRoleTemplate(roleType, out PlayerRoleBase role))
				continue;

			Color roleColor = role.RoleColor;
			colorSum.r += roleColor.r;
			colorSum.g += roleColor.g;
			colorSum.b += roleColor.b;
			known++;
		}

		if (known > 0)
			color = new Color(colorSum.r / known, colorSum.g / known, colorSum.b / known, 1f);

		Quaternion arrowRotation = Quaternion.Euler(0f, rotation.eulerAngles.y, 0f) * Quaternion.Euler(-rotation.eulerAngles.x, 0f, 0f);
		Vector3 arrowPosition = position + (Vector3.up * 1.6f) + (arrowRotation * Vector3.forward);

		IndicatorObject.SetPart(root, 0, PrimitiveType.Cylinder, position, Quaternion.identity, new Vector3(1f, 0.001f, 1f), color);
		IndicatorObject.SetPart(root, 1, PrimitiveType.Cube, arrowPosition, arrowRotation, new Vector3(0.1f, 0.1f, 1f), color);

		return root;
	}
}
