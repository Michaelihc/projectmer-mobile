using LabApi.Features.Wrappers;
using ProjectMER.Events.Arguments;
using ProjectMER.Events.Handlers;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Objects;
using ProjectMER.Features.Serialization;
using UnityEngine;

namespace ProjectMER.Features.Serializable.Schematics;

/// <summary>
/// A schematic placed in a map.
/// </summary>
/// <remarks>
/// <para>
/// The root is a plain server GameObject (ProjectMER used an invisible networked primitive, which Carl Mod would render).
/// It is returned at once; the schematic is parsed and planned on a worker thread and built over the next frames
/// (<see cref="SchematicObject"/>). Editing the root's position, rotation or scale resends static blocks in place.
/// </para>
/// <para>
/// <see cref="Schematic.SchematicSpawning"/> handlers receive a copy of the data they may change. When the file is already
/// parsed (cached and unchanged) the event is raised here and cancelling it returns <see langword="null"/>, as in
/// ProjectMER. Otherwise it is raised once the worker has parsed the file, and cancelling it destroys the root returned
/// earlier.
/// </para>
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

		if (!SchematicLoader.TryResolve(SchematicName, out string directory, out string jsonPath, out string error))
		{
			Logger.Error(error);
			return null;
		}

		Mobile.SpawnGroup? group = Mobile.SpawnGroup.Current;
		if (SchematicLoader.TryGetCached(directory, jsonPath, out SchematicObjectDataList data))
		{
			if (Schematic.HasSchematicSpawningSubscribers)
			{
				// Handlers may change the data; the cached instance must stay untouched.
				SchematicSpawningEventArgs ev = new(data.Clone(), SchematicName);
				Schematic.OnSchematicSpawning(ev);
				data = ev.Data;

				if (!ev.IsAllowed || data == null)
					return null;
			}

			GameObject cachedRoot = CreateRoot(position, rotation);
			cachedRoot.AddComponent<SchematicObject>().Init(data, group);
			return cachedRoot;
		}

		GameObject root = CreateRoot(position, rotation);
		root.AddComponent<SchematicObject>().InitFromFile(SchematicName, directory, jsonPath, group);
		return root;
	}

	private GameObject CreateRoot(Vector3 position, Quaternion rotation)
	{
		GameObject root = new($"CustomSchematic-{SchematicName}");
		root.transform.SetPositionAndRotation(position, rotation);
		root.transform.localScale = Scale;
		return root;
	}
}
