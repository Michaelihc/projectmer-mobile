using AdminToys;
using LabApi.Features.Wrappers;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Mobile;
using UnityEngine;
using YamlDotNet.Serialization;
using PrimitiveObjectToy = LabApi.Features.Wrappers.PrimitiveObjectToy;

namespace ProjectMER.Features.Serializable;

/// <summary>
/// A primitive of a map.
/// </summary>
/// <remarks>
/// Spawned as a static toy with the flag encoding of docs/projectmer-port-plan.md §3.3. <see cref="PrimitiveFlags"/>
/// <c>None</c> (and <c>Collidable</c> alone with <c>invisible_collider_mode: Skip</c>) is not networked: the object is a
/// server-only GameObject that indicators and the tool gun can still use.
/// </remarks>
public class SerializablePrimitive : SerializableObject
{
	/// <summary>
	/// Gets or sets the <see cref="UnityEngine.PrimitiveType"/>.
	/// </summary>
	public PrimitiveType PrimitiveType { get; set; } = PrimitiveType.Cube;

	/// <summary>
	/// Gets or sets the <see cref="SerializablePrimitive"/>'s color.
	/// </summary>
	public string Color { get; set; } = "#FF0000";

	/// <summary>
	/// Gets or sets the <see cref="SerializablePrimitive"/>'s flags.
	/// </summary>
	public PrimitiveFlags PrimitiveFlags { get; set; } = (PrimitiveFlags)3;

	[YamlIgnore]
	public override bool RequiresReloading => base.RequiresReloading || ToyFactory.IsNetworked(PrimitiveFlags) != _spawnedNetworked;

	public override GameObject SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		Vector3 position = room.GetAbsolutePosition(Position);
		Quaternion rotation = room.GetAbsoluteRotation(Rotation);
		Color color = Color.GetColorFromString();
		_prevIndex = Index;

		if (instance != null)
		{
			PrimitiveObjectToy? existing = ToyFactory.GetPrimitive(instance);
			if (existing == null || !ToyFactory.UpdatePrimitive(existing, position, rotation, Scale, PrimitiveType, color, PrimitiveFlags))
			{
				instance.transform.SetPositionAndRotation(position, rotation);
				instance.transform.localScale = Scale;
			}

			return instance;
		}

		PrimitiveObjectToy? toy = ToyFactory.CreatePrimitive(position, rotation, Scale, PrimitiveType, color, PrimitiveFlags, isStatic: true, SpawnGroup.Current);
		_spawnedNetworked = toy != null;
		if (toy != null)
			return toy.GameObject;

		GameObject anchor = new("Primitive");
		anchor.transform.SetPositionAndRotation(position, rotation);
		anchor.transform.localScale = Scale;
		return anchor;
	}

	internal bool _spawnedNetworked;
}
