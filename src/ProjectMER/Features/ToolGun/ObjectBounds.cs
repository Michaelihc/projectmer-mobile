using AdminToys;
using Mirror;
using ProjectMER.Features.Objects;
using UnityEngine;

namespace ProjectMER.Features.ToolGun;

/// <summary>
/// Computes the oriented bounding box of a map object for the selection box and the grab proxy.
/// </summary>
/// <remarks>
/// The box is aligned with the object's rotation. Schematic blocks are not children of their root (Carl Mod has no toy
/// parenting), so a schematic's box is built from its blocks. Primitives use the Unity primitive mesh sizes through the
/// toy's transform, which already carries the flag encoding (a 180° turn or a mirror that leaves the shape unchanged).
/// Other objects use their colliders; objects without any get a 0.5 m box.
/// </remarks>
internal static class ObjectBounds
{
	private static readonly List<Collider> Colliders = [];

	/// <summary>
	/// Gets the local size of a Unity primitive mesh.
	/// </summary>
	public static Vector3 PrimitiveSize(PrimitiveType type) => type switch
	{
		PrimitiveType.Capsule or PrimitiveType.Cylinder => new Vector3(1f, 2f, 1f),
		PrimitiveType.Plane => new Vector3(10f, 0f, 10f),
		PrimitiveType.Quad => new Vector3(1f, 1f, 0f),
		_ => Vector3.one,
	};

	/// <summary>
	/// Gets the oriented bounds of a map object.
	/// </summary>
	/// <param name="mapEditorObject">The object.</param>
	/// <param name="center">World center.</param>
	/// <param name="rotation">World rotation of the box.</param>
	/// <param name="size">Size along the box axes, in meters.</param>
	/// <returns><see langword="false"/> when the object is a schematic that is still building (the box covers the blocks
	/// that exist so far; call again later). Bounds never force a synchronous build.</returns>
	public static bool Get(MapEditorObject mapEditorObject, out Vector3 center, out Quaternion rotation, out Vector3 size)
	{
		bool complete = true;
		Transform root = mapEditorObject.transform;
		Vector3 origin = root.position;
		rotation = root.rotation;
		Matrix4x4 toFrame = Matrix4x4.TRS(origin, rotation, Vector3.one).inverse;
		Accumulator accumulator = new();

		if (mapEditorObject.TryGetComponent(out SchematicObject schematic))
		{
			complete = schematic.IsSpawned;
			foreach (AdminToyBase toy in schematic.CurrentAdminToyBases)
				AddToy(ref accumulator, toFrame, toy);

			foreach (NetworkIdentity identity in schematic.CurrentNetworkIdentities)
			{
				if (identity != null && !identity.TryGetComponent(out AdminToyBase _))
					AddColliders(ref accumulator, toFrame, identity.gameObject);
			}
		}
		else if (mapEditorObject.TryGetComponent(out AdminToyBase toy))
		{
			AddToy(ref accumulator, toFrame, toy);
		}
		else
		{
			AddColliders(ref accumulator, toFrame, mapEditorObject.gameObject);
		}

		if (!accumulator.Any)
		{
			center = origin;
			size = Vector3.one * 0.5f;
			return complete;
		}

		Vector3 localCenter = (accumulator.Min + accumulator.Max) * 0.5f;
		center = origin + (rotation * localCenter);
		size = accumulator.Max - accumulator.Min;
		return complete;
	}

	private static void AddToy(ref Accumulator accumulator, Matrix4x4 toFrame, AdminToyBase toy)
	{
		if (toy == null)
			return;

		switch (toy)
		{
			case PrimitiveObjectToy primitive:
				AddBox(ref accumulator, toFrame * primitive.transform.localToWorldMatrix, Vector3.zero, PrimitiveSize(primitive.PrimitiveType) * 0.5f);
				break;

			case LightSourceToy light:
				AddBox(ref accumulator, toFrame, light.transform.position, Vector3.one * 0.15f);
				break;

			default:
				AddColliders(ref accumulator, toFrame, toy.gameObject);
				break;
		}
	}

	private static void AddColliders(ref Accumulator accumulator, Matrix4x4 toFrame, GameObject gameObject)
	{
		gameObject.GetComponentsInChildren(true, Colliders);
		foreach (Collider collider in Colliders)
		{
			if (collider == null || !collider.enabled)
				continue;

			Bounds bounds = collider.bounds;
			AddBox(ref accumulator, toFrame, bounds.center, bounds.extents);
		}

		Colliders.Clear();
	}

	/// <summary>
	/// Adds a box given by a local center and extents and its matrix into the frame.
	/// </summary>
	private static void AddBox(ref Accumulator accumulator, Matrix4x4 matrix, Vector3 center, Vector3 extents)
	{
		Vector3 c = matrix.MultiplyPoint3x4(center);
		Vector3 e = new(
			(Mathf.Abs(matrix.m00) * extents.x) + (Mathf.Abs(matrix.m01) * extents.y) + (Mathf.Abs(matrix.m02) * extents.z),
			(Mathf.Abs(matrix.m10) * extents.x) + (Mathf.Abs(matrix.m11) * extents.y) + (Mathf.Abs(matrix.m12) * extents.z),
			(Mathf.Abs(matrix.m20) * extents.x) + (Mathf.Abs(matrix.m21) * extents.y) + (Mathf.Abs(matrix.m22) * extents.z));

		if (float.IsNaN(c.x) || float.IsInfinity(e.x))
			return;

		if (!accumulator.Any)
		{
			accumulator.Min = c - e;
			accumulator.Max = c + e;
			accumulator.Any = true;
			return;
		}

		accumulator.Min = Vector3.Min(accumulator.Min, c - e);
		accumulator.Max = Vector3.Max(accumulator.Max, c + e);
	}

	private struct Accumulator
	{
		public bool Any;

		public Vector3 Min;

		public Vector3 Max;
	}
}
