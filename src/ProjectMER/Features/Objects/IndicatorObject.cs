using AdminToys;
using NorthwoodLib.Pools;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Interfaces;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.Serializable;
using UnityEngine;
using PrimitiveObjectToy = LabApi.Features.Wrappers.PrimitiveObjectToy;

namespace ProjectMER.Features.Objects;

/// <summary>
/// Shows invisible map objects (lights, spawnpoints, teleports) as small primitives that the tool gun can select.
/// </summary>
/// <remarks>
/// <para>
/// Port state (phase A): an indicator is a server-only root GameObject with a trigger <see cref="BoxCollider"/> for
/// selection, plus networked parts created at root level with world transforms (Carl Mod clients have no toy parenting,
/// so ProjectMER's parented indicator hierarchies would show in the wrong place). Parts are visible to everyone, and they
/// follow their object when it is edited (<see cref="MapEditorObject.UpdateObjectAndCopies"/>) instead of every frame.
/// </para>
/// <para>
/// Seam for the rewrite (docs/projectmer-port-plan.md §1.5, §3.8, §3.10): indicator definitions build their geometry only
/// through <see cref="CreateRoot"/> and <see cref="SetPart"/>, so per-player visibility (spawn parts with
/// <c>MerVisibility.Spawn(..., adminOnly: true)</c> for players who enabled indicators) and the server-only selection
/// trigger can change here without touching the definitions.
/// </para>
/// </remarks>
public class IndicatorObject : MapEditorObject
{
	public static Dictionary<IndicatorObject, MapEditorObject> Dictionary = [];

	private static readonly Dictionary<MapEditorObject, IndicatorObject> ByObject = [];

	public static bool TrySpawnOrUpdateIndicator(MapEditorObject mapEditorObject)
	{
		if (mapEditorObject.Base is not IIndicatorDefinition indicatorDefinition)
			return false;

		if (TryGetIndicator(mapEditorObject, out IndicatorObject indicator))
		{
			indicatorDefinition.SpawnOrUpdateIndicator(mapEditorObject.Room, indicator.gameObject);
		}
		else
		{
			indicator = indicatorDefinition.SpawnOrUpdateIndicator(mapEditorObject.Room).AddComponent<IndicatorObject>();
			BoxCollider collider = indicator.gameObject.AddComponent<BoxCollider>();
			collider.isTrigger = true;
			Dictionary.Add(indicator, mapEditorObject);
			ByObject[mapEditorObject] = indicator;
		}

		return true;
	}

	public static bool TryGetIndicator(MapEditorObject mapEditorObject, out IndicatorObject indicator)
	{
		indicator = null!;
		if (mapEditorObject == null || mapEditorObject.Base is not IIndicatorDefinition _)
			return false;

		if (!ByObject.TryGetValue(mapEditorObject, out indicator) || indicator == null)
			return false;

		return true;
	}

	public static bool TryDestroyIndicator(MapEditorObject mapEditorObject)
	{
		if (!TryGetIndicator(mapEditorObject, out IndicatorObject indicator))
			return false;

		Dictionary.Remove(indicator);
		ByObject.Remove(mapEditorObject);
		indicator.Destroy();
		return true;
	}

	public static void ClearIndicators()
	{
		List<MapEditorObject> values = ListPool<MapEditorObject>.Shared.Rent(Dictionary.Values);
		foreach (MapEditorObject mapEditorObject in values)
			TryDestroyIndicator(mapEditorObject);

		ListPool<MapEditorObject>.Shared.Return(values);
		Dictionary.Clear();
		ByObject.Clear();
	}

	/// <summary>
	/// Forgets every indicator without destroying anything (round restart: the scene change destroyed them).
	/// </summary>
	public static void ResetState()
	{
		Dictionary.Clear();
		ByObject.Clear();
	}

	public static void RefreshIndicators()
	{
		ClearIndicators();

		foreach (MapSchematic map in MapUtils.LoadedMaps.Values)
		{
			foreach (MapEditorObject mapEditorObject in map.SpawnedObjects)
			{
				TrySpawnOrUpdateIndicator(mapEditorObject);
			}
		}
	}

	/// <summary>
	/// Creates or moves the server-only root of an indicator.
	/// </summary>
	/// <param name="instance">The existing root, or <see langword="null"/>.</param>
	/// <param name="name">The root name.</param>
	/// <param name="position">World position.</param>
	/// <param name="rotation">World rotation.</param>
	/// <returns>The root.</returns>
	public static GameObject CreateRoot(GameObject? instance, string name, Vector3 position, Quaternion rotation)
	{
		GameObject root = instance ?? new GameObject(name);
		root.transform.SetPositionAndRotation(position, rotation);
		if (!root.TryGetComponent(out IndicatorParts _))
			root.AddComponent<IndicatorParts>();

		return root;
	}

	/// <summary>
	/// Creates or updates one networked part of an indicator, with a world transform.
	/// </summary>
	/// <param name="root">The indicator root from <see cref="CreateRoot"/>.</param>
	/// <param name="index">The part index (stable per definition).</param>
	public static void SetPart(GameObject root, int index, PrimitiveType type, Vector3 position, Quaternion rotation, Vector3 scale, Color color)
	{
		IndicatorParts parts = root.GetComponent<IndicatorParts>();
		while (parts.Parts.Count <= index)
			parts.Parts.Add(null);

		PrimitiveObjectToy? part = parts.Parts[index];
		if (part != null && !part.IsDestroyed)
		{
			ToyFactory.UpdatePrimitive(part, position, rotation, scale, type, color, PrimitiveFlags.Visible);
			return;
		}

		part = ToyFactory.CreatePrimitive(position, rotation, scale, type, color, PrimitiveFlags.Visible, true, null, queue: false, MerObjectKind.Indicator);
		if (part == null)
			return;

		parts.Parts[index] = part;
		MerVisibility.Spawn(part.Base.netIdentity, null, MerObjectKind.Indicator, adminOnly: true);
		SpawnQueue.DisableWhenReady(part.Base);
	}

	/// <summary>
	/// Holds the networked parts of an indicator and destroys them with it.
	/// </summary>
	public sealed class IndicatorParts : MonoBehaviour
	{
		public List<PrimitiveObjectToy?> Parts { get; } = [];

		private void OnDestroy()
		{
			foreach (PrimitiveObjectToy? part in Parts)
			{
				if (part != null && !part.IsDestroyed)
					SpawnQueue.Destroy(part.GameObject);
			}

			Parts.Clear();
		}
	}
}
