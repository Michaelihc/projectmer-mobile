using AdminToys;
using LabApi.Features.Wrappers;
using NorthwoodLib.Pools;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Interfaces;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.Serializable;
using ProjectMER.Features.ToolGun;
using UnityEngine;
using PrimitiveObjectToy = LabApi.Features.Wrappers.PrimitiveObjectToy;

namespace ProjectMER.Features.Objects;

/// <summary>
/// Shows invisible map objects (lights, spawnpoints, teleports) as small primitives that the tool gun can select.
/// </summary>
/// <remarks>
/// <para>
/// An indicator is a server-only root GameObject with a trigger <see cref="BoxCollider"/> for tool-gun selection, plus
/// networked parts created at root level with world transforms (Carl Mod clients have no toy parenting). Parts follow their
/// object when it is edited (<see cref="MapEditorObject.UpdateObjectAndCopies"/>); nothing runs per frame. The owner of an
/// indicator is found through a reverse dictionary.
/// </para>
/// <para>
/// Indicators are per player (docs/projectmer-port-plan.md §1.5, §3.8). The parts are admin-only objects bound to
/// <see cref="Viewers"/>: they exist only while at least one player has indicators on, and nobody else receives any message
/// for them. The first player who turns indicators on builds them for every loaded object; when the last one turns them
/// off (or leaves) they are destroyed. Roots and their selection triggers are also created for objects made or edited
/// while nobody views indicators, as in ProjectMER, so the tool gun can still select them.
/// </para>
/// </remarks>
public class IndicatorObject : MapEditorObject
{
	public static Dictionary<IndicatorObject, MapEditorObject> Dictionary = [];

	private static readonly Dictionary<MapEditorObject, IndicatorObject> ByObject = [];

	/// <summary>
	/// Gets the players who see indicators.
	/// </summary>
	public static VisibilityAudience Viewers { get; } = CreateViewers();

	/// <summary>
	/// Gets whether a player sees indicators.
	/// </summary>
	public static bool IsShownTo(Player player) => Viewers.Contains(player);

	/// <summary>
	/// Turns indicators on or off for one player.
	/// </summary>
	/// <param name="player">The player.</param>
	/// <param name="shown">Whether the player should see indicators.</param>
	/// <returns><see langword="false"/> when nothing changed.</returns>
	public static bool SetShown(Player player, bool shown)
	{
		if (!shown)
			return Viewers.Remove(player);

		if (Viewers.Contains(player))
			return false;

		bool first = Viewers.Count == 0;
		if (!Viewers.Add(player))
			return false;

		// The first viewer builds the networked parts; later viewers are shown the existing ones. While anyone views
		// indicators, non-collidable primitives get editing triggers, so aiming at them selects them without a tool gun.
		if (first)
		{
			EditingColliders.Acquire(Viewers);
			RefreshIndicators();
		}

		return true;
	}

	/// <summary>
	/// Turns indicators off for everyone and destroys them.
	/// </summary>
	public static void HideAll()
	{
		Viewers.Clear();
		ClearIndicators();
	}

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

			// The selection trigger stays out of the game's own queries (bullets, line of sight), like the editing triggers.
			indicator.gameObject.layer = EditingColliders.Layer;
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
	/// Forgets every indicator without destroying anything (round restart: the scene change destroyed them, and
	/// <see cref="MerVisibility.Reset"/> forgot the viewers).
	/// </summary>
	public static void ResetState()
	{
		Dictionary.Clear();
		ByObject.Clear();

		// Indicators are off after a round reset (the visibility reset drops the members of a registered audience; this
		// also covers an audience that never had parts). EditingColliders forgets its owners in the same reset.
		Viewers.ResetMembers();
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
	/// Creates or updates one networked part of an indicator, with a world transform. Parts are only created while
	/// someone views indicators.
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

		if (Viewers.Count == 0)
			return;

		part = ToyFactory.CreatePrimitive(position, rotation, scale, type, color, PrimitiveFlags.Visible, true, null, queue: false, MerObjectKind.Indicator);
		if (part == null)
			return;

		parts.Parts[index] = part;
		MerVisibility.Spawn(part.Base.netIdentity, null, MerObjectKind.Indicator, adminOnly: true, Viewers);
		SpawnQueue.DisableWhenReady(part.Base);
	}

	private static VisibilityAudience CreateViewers()
	{
		VisibilityAudience audience = new("indicators");
		audience.Emptied += OnViewersEmptied;
		return audience;
	}

	private static void OnViewersEmptied(VisibilityAudience audience)
	{
		EditingColliders.Release(Viewers);
		ClearIndicators();
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
