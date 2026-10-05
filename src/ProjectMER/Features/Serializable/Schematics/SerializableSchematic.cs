using LabApi.Features.Wrappers;
using ProjectMER.Events.Arguments;
using ProjectMER.Events.Handlers;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.Objects;
using UnityEngine;

namespace ProjectMER.Features.Serializable.Schematics;

/// <summary>
/// A schematic placed in a map.
/// </summary>
/// <remarks>
/// The root is a plain server GameObject (ProjectMER used an invisible networked primitive, which Carl Mod would render).
/// Schematic data comes from the parse cache; <see cref="Schematic.SchematicSpawning"/> handlers receive a copy they may
/// change. Editing the root's position, rotation or scale resends static blocks in place.
/// </remarks>
public class SerializableSchematic : SerializableObject
{
	public string SchematicName { get; set; } = "None";

	public override GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		Vector3 position = room.GetAbsolutePosition(Position);
		Quaternion rotation = room.GetAbsoluteRotation(Rotation);
		_prevIndex = Index;

		if (instance != null)
		{
			if (instance.TryGetComponent(out SchematicObject existing))
			{
				existing.transform.SetPositionAndRotation(position, rotation);
				existing.Scale = Scale;
			}

			return instance;
		}

		if (!MapUtils.TryGetSchematicDataByName(SchematicName, out SchematicObjectDataList data))
			return null;

		if (Schematic.HasSchematicSpawningSubscribers)
		{
			// Handlers may change the data; the cached instance must stay untouched.
			SchematicSpawningEventArgs ev = new(Copy(data), SchematicName);
			Schematic.OnSchematicSpawning(ev);
			data = ev.Data;

			if (!ev.IsAllowed || data == null)
				return null;
		}

		GameObject root = new($"CustomSchematic-{SchematicName}");
		root.transform.SetPositionAndRotation(position, rotation);
		root.transform.localScale = Scale;
		root.AddComponent<SchematicObject>().Init(data, Mobile.SpawnGroup.Current);

		return root;
	}

	private static SchematicObjectDataList Copy(SchematicObjectDataList data)
	{
		SchematicObjectDataList copy = new()
		{
			Path = data.Path,
			RootObjectId = data.RootObjectId,
			Blocks = new List<SchematicBlockData>(data.Blocks.Count),
		};

		foreach (SchematicBlockData block in data.Blocks)
			copy.Blocks.Add(block.Clone());

		return copy;
	}
}
