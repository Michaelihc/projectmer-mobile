using LabApi.Features.Wrappers;
using UnityEngine;

namespace ProjectMER.Features.Serializable;

/// <summary>
/// A triangle of a map. Data only: ProjectMER builds a triangle from quads under non-uniformly scaled, rotated parents
/// (a shear). Carl Mod clients have no toy parenting, so a toy can only be rotation times axis scale. Skipped with a
/// warning and kept in saved maps.
/// </summary>
public class SerializableTriangle : SerializableObject
{
	public const float DefaultThickness = 0.01f;

	public Vector3 PointA { get; set; } = new(-0.5f, -0.5f, 0f);

	public Vector3 PointB { get; set; } = new(0.5f, -0.5f, 0f);

	public Vector3 PointC { get; set; } = new(-0.5f, 0.5f, 0f);

	public string Color { get; set; } = "#FF0000";

	public float Thickness { get; set; } = DefaultThickness;

	public override GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		_prevIndex = Index;
		UnsupportedContent.Skip("triangles", "triangles need sheared (parented) quads, which Carl Mod clients cannot show");
		return null;
	}
}
