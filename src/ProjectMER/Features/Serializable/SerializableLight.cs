using LabApi.Features.Wrappers;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Interfaces;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.Objects;
using UnityEngine;
using YamlDotNet.Serialization;
using LightSourceToy = LabApi.Features.Wrappers.LightSourceToy;

namespace ProjectMER.Features.Serializable;

/// <summary>
/// A light of a map.
/// </summary>
/// <remarks>
/// Carl Mod's light toy syncs intensity, range, color and whether shadows are on. <see cref="LightType"/>,
/// <see cref="Shape"/>, <see cref="SpotAngle"/>, <see cref="InnerSpotAngle"/> and <see cref="Strength"/> are kept in the
/// map but not applied (spot lights become point lights, with a warning). Shadows follow <c>allow_light_shadows</c> and
/// lights count against <c>max_lights</c>.
/// </remarks>
public class SerializableLight : SerializableObject, IIndicatorDefinition
{
	public string Color { get; set; } = "white";

	public float Intensity { get; set; } = 1f;

	public float Range { get; set; } = 1f;

	public LightShadows Shadows { get; set; } = LightShadows.Hard;

	public float Strength { get; set; } = 0f;

	public LightType LightType { get; set; } = LightType.Point;

	public LightShape Shape { get; set; } = LightShape.Cone;

	public float SpotAngle { get; set; } = 30f;

	public float InnerSpotAngle { get; set; } = 0f;

	[YamlIgnore]
	public override Vector3 Scale { get; set; }

	public override GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		Vector3 position = room.GetAbsolutePosition(Position);
		Quaternion rotation = room.GetAbsoluteRotation(Rotation);
		_prevIndex = Index;

		Color color = ColorUtility.TryParseHtmlString(Color, out Color parsed) ? parsed : UnityEngine.Color.magenta;
		bool shadows = Shadows != LightShadows.None;

		LightSourceToy? existing = ToyFactory.GetLight(instance);
		if (existing != null)
		{
			ToyFactory.UpdateLight(existing, position, rotation, color, Intensity, Range, shadows);
			return instance;
		}

		if (LightType != LightType.Point)
			UnsupportedContent.Adapt($"{LightType} lights", "spawned as point lights; Carl Mod's light toy has no type, shape or spot angles");

		LightSourceToy? light = ToyFactory.CreateLight(position, rotation, color, Intensity, Range, shadows, isStatic: true, Mobile.SpawnGroup.Current);
		return light?.GameObject;
	}

	public GameObject SpawnOrUpdateIndicator(Room room, GameObject? instance = null)
	{
		Vector3 position = room.GetAbsolutePosition(Position);
		GameObject root = IndicatorObject.CreateRoot(instance, "Indicator", position, Quaternion.identity);

		Color color = ColorUtility.TryParseHtmlString(Color, out Color parsed) ? parsed : UnityEngine.Color.magenta;
		Color transparentColor = new(color.r, color.g, color.b, 0.9f);
		IndicatorObject.SetPart(root, 0, PrimitiveType.Sphere, position, Quaternion.identity, new Vector3(0.25f, 0.25f, 0.25f), transparentColor);

		return root;
	}
}
