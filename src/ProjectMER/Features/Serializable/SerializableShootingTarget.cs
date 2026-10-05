using AdminToys;
using LabApi.Features.Wrappers;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Mobile;
using UnityEngine;
using YamlDotNet.Serialization;

namespace ProjectMER.Features.Serializable;

/// <summary>
/// A shooting target of a map. Spawned static; its behaviour stays enabled (it handles hits and buttons).
/// </summary>
public class SerializableShootingTarget : SerializableObject
{
	public TargetType TargetType { get; set; } = TargetType.ClassD;

	public override GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		if (instance == null && (TargetPrefab == null || !Budget.CanCreate()))
			return null;

		ShootingTarget shootingTarget = instance == null ? UnityEngine.Object.Instantiate(TargetPrefab) : instance.GetComponent<ShootingTarget>();
		Vector3 position = room.GetAbsolutePosition(Position);
		Quaternion rotation = room.GetAbsoluteRotation(Rotation);
		_prevIndex = Index;

		shootingTarget.transform.SetPositionAndRotation(position, rotation);
		shootingTarget.transform.localScale = Scale;

		_prevType = TargetType;

		AdminToy toy = AdminToy.Get(shootingTarget);
		if (instance == null)
		{
			toy.IsStatic = true;
			toy.Position = position;
			ToyFactory.Queue(ToyFactory.Track(shootingTarget.gameObject, MerObjectKind.ShootingTarget, Mobile.SpawnGroup.Current, true), SpawnPriority.Collidable);
		}
		else
		{
			toy.Position = position;
			if (toy.IsStatic && ToyFactory.IsSpawned(shootingTarget.netIdentity))
				ToyFactory.Resend(shootingTarget.netIdentity);
		}

		return shootingTarget.gameObject;
	}

	private ShootingTarget? TargetPrefab => TargetType switch
	{
		TargetType.Binary => PrefabManager.ShootingTargetBinary,
		TargetType.ClassD => PrefabManager.ShootingTargetDBoy,
		TargetType.Sport => PrefabManager.ShootingTargetSport,
		_ => null,
	};

	[YamlIgnore]
	public override bool RequiresReloading => TargetType != _prevType || base.RequiresReloading;

	internal TargetType _prevType;
}
