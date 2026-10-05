using AdminToys;
using ProjectMER.Features.Objects;
using ProjectMER.Features.Serializable;
using ProjectMER.Features.Serializable.Schematics;
using UnityEngine;

namespace ProjectMER.Features;

public static class ObjectSpawner
{
	/// <summary>
	/// Spawns a primitive. Returns <see langword="null"/> when it is not networked (flags <c>None</c>, hard cap).
	/// </summary>
	public static PrimitiveObjectToy SpawnPrimitive(SerializablePrimitive serializablePrimitive)
	{
		GameObject gameObject = serializablePrimitive.SpawnOrUpdateObject();
		return gameObject != null ? gameObject.GetComponent<PrimitiveObjectToy>() : null!;
	}

	/// <summary>
	/// Always <see langword="null"/>: triangles cannot be shown by Carl Mod clients (a warning is logged).
	/// </summary>
	public static PrimitiveObjectToy SpawnTriangle(SerializableTriangle serializableTriangle)
	{
		GameObject? gameObject = serializableTriangle.SpawnOrUpdateObject();
		return gameObject != null ? gameObject.GetComponent<PrimitiveObjectToy>() : null!;
	}

	public static SchematicObject SpawnSchematic(SerializableSchematic serializableSchematic)
	{
		GameObject? gameObject = serializableSchematic.SpawnOrUpdateObject();
		if (gameObject == null)
			return null!;

		return gameObject.GetComponent<SchematicObject>();
	}

	public static SchematicObject SpawnSchematic(string schematicName, Vector3 position) =>
		SpawnSchematic(new() { SchematicName = schematicName, Position = position });

	public static SchematicObject SpawnSchematic(string schematicName, Vector3 position, Quaternion rotation) =>
		SpawnSchematic(new() { SchematicName = schematicName, Position = position, Rotation = rotation.eulerAngles });

	public static SchematicObject SpawnSchematic(string schematicName, Vector3 position, Vector3 eulerAngles) =>
		SpawnSchematic(new() { SchematicName = schematicName, Position = position, Rotation = eulerAngles });

	public static SchematicObject SpawnSchematic(string schematicName, Vector3 position, Quaternion rotation, Vector3 scale) =>
		SpawnSchematic(new() { SchematicName = schematicName, Position = position, Rotation = rotation.eulerAngles, Scale = scale });

	public static SchematicObject SpawnSchematic(string schematicName, Vector3 position, Vector3 eulerAngles, Vector3 scale) =>
		SpawnSchematic(new() { SchematicName = schematicName, Position = position, Rotation = eulerAngles, Scale = scale });

	public static bool TrySpawnSchematic(SerializableSchematic serializableSchematic, out SchematicObject schematic)
	{
		try
		{
			schematic = SpawnSchematic(serializableSchematic);
			return schematic != null;
		}
		catch (Exception)
		{
			schematic = null!;
			return false;
		}
	}

	public static bool TrySpawnSchematic(string schematicName, Vector3 position, out SchematicObject schematic) =>
		TrySpawnSchematic(new() { SchematicName = schematicName, Position = position }, out schematic);

	public static bool TrySpawnSchematic(string schematicName, Vector3 position, Quaternion rotation, out SchematicObject schematic) =>
		TrySpawnSchematic(new() { SchematicName = schematicName, Position = position, Rotation = rotation.eulerAngles }, out schematic);

	public static bool TrySpawnSchematic(string schematicName, Vector3 position, Vector3 eulerAngles, out SchematicObject schematic) =>
		TrySpawnSchematic(new() { SchematicName = schematicName, Position = position, Rotation = eulerAngles }, out schematic);

	public static bool TrySpawnSchematic(string schematicName, Vector3 position, Quaternion rotation, Vector3 scale, out SchematicObject schematic) =>
		TrySpawnSchematic(new() { SchematicName = schematicName, Position = position, Rotation = rotation.eulerAngles, Scale = scale }, out schematic);

	public static bool TrySpawnSchematic(string schematicName, Vector3 position, Vector3 eulerAngles, Vector3 scale, out SchematicObject schematic) =>
		TrySpawnSchematic(new() { SchematicName = schematicName, Position = position, Rotation = eulerAngles, Scale = scale }, out schematic);
}
