using System.Diagnostics;
using AdminToys;
using ProjectMER.Features.Objects;
using ProjectMER.Features.Serializable;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProjectMER.Features.ToolGun;

/// <summary>
/// Server-only trigger colliders that make non-collidable MER primitives selectable while someone edits
/// (docs/projectmer-port-plan.md §3.3).
/// </summary>
/// <remarks>
/// <para>
/// A primitive without <c>Collidable</c> has no collider on the server either (its <c>Scale</c> SyncVar has no positive
/// component), so the tool gun's raycast would pass through it. While at least one owner holds a reference (every
/// existing tool gun does; <c>Acquire</c>/<c>Release</c> are public so indicators can do the same), each such primitive
/// of a loaded map or schematic gets a <see cref="BoxCollider"/> trigger sized to its mesh, on a child of its server-side
/// primitive object. Clients never see it. The triggers are removed when the last owner releases.
/// </para>
/// <para>
/// The triggers live on <see cref="Layer"/>, Unity's "Ignore Raycast" layer. Raycasts without a mask leave it out, and of
/// the game's own layer masks only the tesla gate's player overlap includes it (it looks for players, so the triggers
/// change nothing there). On the Default layer they would stop the server's own queries while anyone edits (Carl Mod
/// keeps <c>Physics.queriesHitTriggers</c> on): bullets (<c>StandardHitregBase.HitregMask</c>), line-of-sight and
/// explosion linecasts and SCP-049's corpse ray would stop at see-through decor. An unnamed layer would not do either:
/// raycasts without a mask hit every layer but this one, and the game's pooled objects use some unnamed layers. Only the
/// tool gun's ray (<see cref="ToolGunHandler.Raycast(Vector3, Vector3, out RaycastHit)"/>) adds the layer to its mask.
/// </para>
/// <para>
/// New objects are picked up by a rescan every <see cref="RescanInterval"/> seconds (and soon after a tool-gun create).
/// Primitives already handled are skipped by instance id, and built schematics whose primitives all existed at an
/// earlier scan are skipped whole, so a rescan visits only standalone toys and schematics still streaming in.
/// </para>
/// </remarks>
public static class EditingColliders
{
	/// <summary>
	/// Seconds between rescans while editing is active.
	/// </summary>
	public const float RescanInterval = 2f;

	/// <summary>
	/// The physics layer of the editing triggers and of indicator triggers: Unity's built-in "Ignore Raycast" layer.
	/// </summary>
	public const int Layer = 2;

	private const float MinThickness = 0.05f;

	private static readonly HashSet<object> Owners = [];

	private static readonly List<BoxCollider> Added = [];

	private static readonly HashSet<int> Handled = [];

	private static readonly HashSet<SchematicObject> Complete = [];

	private static float _nextScan;

	/// <summary>
	/// Gets whether editing triggers are active.
	/// </summary>
	public static bool IsActive => Owners.Count > 0;

	/// <summary>
	/// Gets the number of trigger colliders currently added.
	/// </summary>
	public static int Count
	{
		get
		{
			int count = 0;
			foreach (BoxCollider collider in Added)
			{
				if (collider != null)
					count++;
			}

			return count;
		}
	}

	/// <summary>
	/// Gets the number of toys visited by the last scan.
	/// </summary>
	public static int LastScanVisited { get; private set; }

	/// <summary>
	/// Gets the duration of the last scan in milliseconds.
	/// </summary>
	public static double LastScanMs { get; private set; }

	/// <summary>
	/// Gets the number of scans since the plugin was enabled.
	/// </summary>
	public static int Scans { get; private set; }

	/// <summary>
	/// Gets the slowest scan in milliseconds and the toys it visited.
	/// </summary>
	public static (double Ms, int Visited) SlowestScan { get; private set; }

	/// <summary>
	/// Adds an owner; the first one adds the triggers.
	/// </summary>
	/// <param name="owner">Any object identifying the reason (a tool gun, a player with indicators on).</param>
	public static void Acquire(object owner)
	{
		if (!Owners.Add(owner) || Owners.Count != 1)
			return;

		Scan(Time.realtimeSinceStartup);
		ToolGunLoop.EnsureRunning();
	}

	/// <summary>
	/// Removes an owner; the last one removes the triggers.
	/// </summary>
	/// <param name="owner">The object passed to <see cref="Acquire"/>.</param>
	public static void Release(object owner)
	{
		if (!Owners.Remove(owner) || Owners.Count != 0)
			return;

		foreach (BoxCollider collider in Added)
		{
			if (collider != null)
				Object.Destroy(collider.gameObject);
		}

		Added.Clear();
		Handled.Clear();
		Complete.Clear();
	}

	/// <summary>
	/// Requests a rescan on the next tick (new objects were created).
	/// </summary>
	public static void MarkDirty() => _nextScan = 0f;

	/// <summary>
	/// Forgets everything (round restart: the scene change destroyed the objects and their colliders).
	/// </summary>
	internal static void Reset()
	{
		Owners.Clear();
		Added.Clear();
		Handled.Clear();
		Complete.Clear();
		_nextScan = 0f;
	}

	/// <summary>
	/// Rescans when due.
	/// </summary>
	internal static void Tick(float now)
	{
		if (Owners.Count == 0 || now < _nextScan)
			return;

		Scan(now);
	}

	private static void Scan(float now)
	{
		long start = Stopwatch.GetTimestamp();
		_nextScan = now + RescanInterval;
		int visited = 0;
		Complete.RemoveWhere(static schematic => schematic == null);

		foreach (MapSchematic map in MapUtils.LoadedMaps.Values)
		{
			foreach (MapEditorObject mapEditorObject in map.SpawnedObjects)
			{
				if (mapEditorObject == null)
					continue;

				if (mapEditorObject.TryGetComponent(out SchematicObject schematic))
				{
					// A built schematic whose primitives all existed at a previous scan has nothing new.
					if (Complete.Contains(schematic))
						continue;

					// The current blocks only: a periodic scan must not force a building schematic to finish synchronously.
					bool complete = schematic.IsBuilt;
					foreach (AdminToyBase toy in schematic.CurrentAdminToyBases)
					{
						visited++;
						if (!Process(toy))
							complete = false;
					}

					if (complete)
						Complete.Add(schematic);

					continue;
				}

				if (mapEditorObject.TryGetComponent(out AdminToyBase standalone))
				{
					visited++;
					Process(standalone);
				}
			}
		}

		Scans++;
		LastScanVisited = visited;
		LastScanMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
		if (LastScanMs > SlowestScan.Ms)
			SlowestScan = (LastScanMs, visited);
	}

	/// <returns><see langword="false"/> when the primitive does not exist yet.</returns>
	private static bool Process(AdminToyBase toy)
	{
		if (toy == null || toy is not PrimitiveObjectToy primitive)
			return true;

		GameObject shape = primitive._spawnedPrimitve;

		// Not started yet (Start builds the primitive); the next scan handles it.
		if (shape == null)
			return false;

		if (!Handled.Add(shape.GetInstanceID()))
			return true;

		// The server builds a collider exactly when a component of the Scale SyncVar is positive (SetPrimitive).
		Vector3 sync = primitive.Scale;
		if (sync.x > 0f || sync.y > 0f || sync.z > 0f)
			return true;

		Vector3 center = Vector3.zero;
		Vector3 size = ObjectBounds.PrimitiveSize(primitive.PrimitiveType);
		if (shape.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
		{
			Bounds bounds = filter.sharedMesh.bounds;
			center = bounds.center;
			size = bounds.size;
		}

		// Planes and quads are flat; give every axis a few centimeters so the ray can hit them.
		Vector3 lossy = shape.transform.lossyScale;
		size.x = Mathf.Max(size.x, MinThickness / Mathf.Max(Mathf.Abs(lossy.x), 0.0001f));
		size.y = Mathf.Max(size.y, MinThickness / Mathf.Max(Mathf.Abs(lossy.y), 0.0001f));
		size.z = Mathf.Max(size.z, MinThickness / Mathf.Max(Mathf.Abs(lossy.z), 0.0001f));

		// A child, so the trigger has its own layer and leaves the primitive object untouched.
		GameObject holder = new("MER editing trigger") { layer = Layer };
		holder.transform.SetParent(shape.transform, false);
		BoxCollider collider = holder.AddComponent<BoxCollider>();
		collider.isTrigger = true;
		collider.center = center;
		collider.size = size;
		Added.Add(collider);
		return true;
	}
}
