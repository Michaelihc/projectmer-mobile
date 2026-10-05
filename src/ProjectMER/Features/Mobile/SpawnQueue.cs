using System.Diagnostics;
using AdminToys;
using MEC;
using Mirror;
using ProjectMER.Configs;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Objects;
using ProjectMER.Features.Serialization;
using UnityEngine;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// Networks prepared MER objects across frames (docs/projectmer-port-plan.md §3.5).
/// </summary>
/// <remarks>
/// <para>
/// Objects are instantiated, positioned and given their SyncVars immediately, then wait here for
/// <see cref="MerVisibility.Spawn"/>. Each frame the queue spawns up to <c>spawn_max_per_frame</c> objects or until
/// <c>spawn_time_budget_ms</c> is used, in <see cref="SpawnPriority"/> order and nearest to a player first, so a phone
/// instantiates a few dozen primitives per frame instead of a whole schematic at once.
/// </para>
/// <para>
/// Schematics are built here too: after the spawns, each frame's remaining time budget goes to
/// <see cref="SchematicObject"/> builders, which create anchors and toys block by block (their files are parsed and
/// planned on worker threads by <see cref="SchematicLoader"/>). A load therefore never builds a whole schematic in one
/// frame.
/// </para>
/// <para>
/// Destroys of spawned objects go through the same coroutine (four times the spawn limit per frame; doors are destroyed at
/// once, see <see cref="MerWaypoints.Count"/>). An object whose destroy was requested is no longer shown to anyone. After a
/// toy has been spawned and its server <c>Start</c> has built the primitive, its component is disabled so it no longer
/// costs a <c>LateUpdate</c>/<c>Update</c> call (§3.4). The coroutine only runs while there is work.
/// </para>
/// </remarks>
public static class SpawnQueue
{
	private static readonly List<Entry>[] Buckets = [[], [], [], []];

	private static readonly bool[] BucketUnsorted = new bool[4];

	private static readonly List<GameObject> Destroys = [];

	private static readonly List<AdminToyBase> PendingDisable = [];

	private static readonly List<SchematicObject> PendingResync = [];

	private static readonly List<SchematicObject> Builders = [];

	private static readonly List<Vector3> PlayerPositions = [];

	private static readonly Comparison<Entry> FarthestFirst = static (a, b) => b.DistanceSq.CompareTo(a.DistanceSq);

	private static CoroutineHandle _handle;

	private static bool _running;

	private static bool _enabled;

	private static int _playerPositionsFrame = -1;

	// Statistics of the current drain session (from the first queued spawn until the queue is empty).
	private static bool _sessionActive;

	private static long _sessionStart;

	private static int _sessionSpawned;

	private static int _sessionDropped;

	private static int _sessionFrames;

	private static int _sessionMaxPerFrame;

	private static double _sessionWorstSliceMs;

	private static double _sessionTotalSliceMs;

	private static int _sessionSlicesOverBudget;

	private static int _sessionGcStart;

	private static float _sessionWorstFrameMs;

	private static double _sessionBuildMs;

	private static double _sessionWorstBuildMs;

	private static int _sessionBuildFrames;

	/// <summary>
	/// Gets the number of objects waiting to be spawned.
	/// </summary>
	public static int PendingSpawns
	{
		get
		{
			int count = 0;
			for (int i = 0; i < Buckets.Length; i++)
				count += Buckets[i].Count;

			return count;
		}
	}

	/// <summary>
	/// Gets the number of destroys waiting to be sent.
	/// </summary>
	public static int PendingDestroys => Destroys.Count;

	/// <summary>
	/// Gets the number of schematics still loading or building.
	/// </summary>
	public static int PendingBuilds => Builders.Count;

	/// <summary>
	/// Gets a summary of the last finished drain session.
	/// </summary>
	public static string LastSessionSummary { get; private set; } = "none";

	private static Config Config => ProjectMER.Singleton.Config!;

	/// <summary>
	/// Enables the queue. Called when the plugin is enabled.
	/// </summary>
	public static void Start() => _enabled = true;

	/// <summary>
	/// Stops the queue and drops pending work. Called when the plugin is disabled.
	/// </summary>
	public static void Stop()
	{
		_enabled = false;
		Clear();
		Timing.KillCoroutines(_handle);
		_running = false;
	}

	/// <summary>
	/// Queues a prepared object for network spawning.
	/// </summary>
	/// <param name="link">The object's link (its identity, kind and group).</param>
	/// <param name="priority">The spawn priority.</param>
	/// <param name="disableWhenReady">Whether to disable the toy component once its server <c>Start</c> ran.</param>
	/// <param name="onSpawned">Optional callback after the object was spawned.</param>
	public static void Enqueue(MerBlockLink link, SpawnPriority priority, bool disableWhenReady, Action<GameObject>? onSpawned = null)
	{
		SpawnGroup group = link.Group ?? new SpawnGroup("unnamed");
		link.Group = group;
		group.Pending++;

		Buckets[(int)priority].Add(new Entry
		{
			Link = link,
			Group = group,
			Kind = link.Kind,
			DisableWhenReady = disableWhenReady,
			OnSpawned = onSpawned,
			DistanceSq = DistanceToNearestPlayerSq(link.transform.position),
		});

		BucketUnsorted[(int)priority] = true;
		EnsureRunning();
	}

	/// <summary>
	/// Destroys a MER object: queued objects are dropped, spawned ones are destroyed in batches (doors at once).
	/// </summary>
	/// <param name="gameObject">The object.</param>
	public static void Destroy(GameObject gameObject)
	{
		if (gameObject == null)
			return;

		if (gameObject.TryGetComponent(out MerBlockLink link))
		{
			if (link.DestroyRequested)
				return;

			link.DestroyRequested = true;

			// Free the budget now: a reload admits its lights before the old objects are actually destroyed.
			Budget.Unregister(link);
		}

		// Players who join or enter its zone before the batched destroy must not receive it any more.
		if (gameObject.TryGetComponent(out VisibilityEntry entry))
			MerVisibility.Unregister(entry);

		bool door = gameObject.TryGetComponent(out RelativePositioning.NetIdWaypoint waypoint);
		if (gameObject.TryGetComponent(out NetworkIdentity identity) && identity.netId != 0 && NetworkServer.spawned.ContainsKey(identity.netId))
		{
			// A door goes at once: a reload's new doors must never be numbered alongside the old ones, which could exceed
			// the 223 door waypoints the game can number (MerWaypoints.Count no longer counts doors being destroyed).
			if (door)
			{
				MerVisibility.Destroy(gameObject);
				return;
			}

			Destroys.Add(gameObject);
			EnsureRunning();
			return;
		}

		if (door)
			MerWaypoints.OnDestroyingUnspawned(waypoint);

		UnityEngine.Object.Destroy(gameObject);
	}

	/// <summary>
	/// Disables a spawned toy's server behaviour once its server <c>Start</c> has run.
	/// </summary>
	/// <param name="toy">The toy.</param>
	public static void DisableWhenReady(AdminToyBase toy)
	{
		PendingDisable.Add(toy);
		EnsureRunning();
	}

	/// <summary>
	/// Adds a schematic whose server objects are built across frames (<see cref="SchematicObject.StepBuild"/>).
	/// </summary>
	/// <param name="schematic">The schematic.</param>
	internal static void AddBuilder(SchematicObject schematic)
	{
		if (Builders.Contains(schematic))
			return;

		Builders.Add(schematic);
		if (!_sessionActive && _enabled)
			BeginSession();

		EnsureRunning();
	}

	/// <summary>
	/// Schedules an in-place resend of a static schematic's blocks after its root moved.
	/// </summary>
	/// <param name="schematic">The schematic.</param>
	internal static void ScheduleResync(SchematicObject schematic)
	{
		if (!PendingResync.Contains(schematic))
			PendingResync.Add(schematic);

		EnsureRunning();
	}

	/// <summary>
	/// Drops all pending work. Objects were destroyed by the scene change on round restart.
	/// </summary>
	public static void Clear()
	{
		for (int i = 0; i < Buckets.Length; i++)
			Buckets[i].Clear();

		Destroys.Clear();
		PendingDisable.Clear();
		PendingResync.Clear();
		Builders.Clear();
		SchematicLoader.Clear();
		_sessionActive = false;
	}

	private static void EnsureRunning()
	{
		if (_running || !_enabled)
			return;

		_running = true;
		_handle = Timing.RunCoroutine(Run(), Segment.Update);
	}

	private static bool HasWork()
	{
		if (Destroys.Count > 0 || PendingDisable.Count > 0 || PendingResync.Count > 0 || Builders.Count > 0)
			return true;

		for (int i = 0; i < Buckets.Length; i++)
		{
			if (Buckets[i].Count > 0)
				return true;
		}

		return false;
	}

	private static IEnumerator<float> Run()
	{
		while (true)
		{
			yield return Timing.WaitForOneFrame;

			try
			{
				Tick();
			}
			catch (Exception e)
			{
				Logger.Error($"Spawn queue error: {e}");
			}

			if (!HasWork())
				break;
		}

		_running = false;
	}

	private static void Tick()
	{
		long start = Stopwatch.GetTimestamp();
		ProcessDisables();
		ProcessDestroys();
		ProcessResyncs();

		int spawned = ProcessSpawns(start);
		double buildMs = ProcessBuilds(start);

		if (_sessionActive)
		{
			if (buildMs > 0)
			{
				_sessionBuildFrames++;
				_sessionBuildMs += buildMs;
				if (buildMs > _sessionWorstBuildMs)
					_sessionWorstBuildMs = buildMs;
			}

			_sessionFrames++;
			_sessionSpawned += spawned;
			if (spawned > _sessionMaxPerFrame)
				_sessionMaxPerFrame = spawned;

			double sliceMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
			_sessionTotalSliceMs += sliceMs;
			if (sliceMs > _sessionWorstSliceMs)
				_sessionWorstSliceMs = sliceMs;

			if (sliceMs > Config.SpawnTimeBudgetMs * 2f)
				_sessionSlicesOverBudget++;

			float frameMs = Time.unscaledDeltaTime * 1000f;
			if (frameMs > _sessionWorstFrameMs)
				_sessionWorstFrameMs = frameMs;

			if (PendingSpawns == 0 && Builders.Count == 0)
				EndSession();
		}
	}

	private static void ProcessDisables()
	{
		for (int i = PendingDisable.Count - 1; i >= 0; i--)
		{
			AdminToyBase toy = PendingDisable[i];
			if (toy == null)
			{
				PendingDisable.RemoveAt(i);
				continue;
			}

			// The server Start builds the primitive and its collider; it does not run on a disabled behaviour.
			if (toy is AdminToys.PrimitiveObjectToy primitive && primitive._spawnedPrimitve == null)
				continue;

			toy.enabled = false;
			PendingDisable.RemoveAt(i);
		}
	}

	private static void ProcessDestroys()
	{
		int limit = Math.Max(1, Config.SpawnMaxPerFrame) * 4;
		int count = Math.Min(limit, Destroys.Count);
		for (int i = 0; i < count; i++)
			MerVisibility.Destroy(Destroys[i]);

		Destroys.RemoveRange(0, count);
	}

	private static void ProcessResyncs()
	{
		float now = Time.time;
		for (int i = PendingResync.Count - 1; i >= 0; i--)
		{
			SchematicObject schematic = PendingResync[i];
			if (schematic == null)
			{
				PendingResync.RemoveAt(i);
				continue;
			}

			if (schematic.ResyncDueTime > now)
				continue;

			PendingResync.RemoveAt(i);
			schematic.ResyncNow();
		}
	}

	/// <summary>
	/// Gives the rest of the frame's time budget to schematic builders, oldest first. Each builder that runs makes at
	/// least one step, so builds progress even when spawning used the whole budget.
	/// </summary>
	/// <returns>The time spent building, in milliseconds.</returns>
	private static double ProcessBuilds(long start)
	{
		if (Builders.Count == 0)
			return 0;

		long sliceStart = Stopwatch.GetTimestamp();
		long deadline = start + BudgetTicks;
		for (int i = 0; i < Builders.Count; i++)
		{
			SchematicObject builder = Builders[i];
			bool done;
			try
			{
				done = builder == null || builder.StepBuild(deadline);
			}
			catch (Exception e)
			{
				Logger.Error($"Schematic build failed: {e}");
				done = true;
			}

			if (done)
			{
				Builders.RemoveAt(i);
				i--;
			}

			if (Stopwatch.GetTimestamp() >= deadline)
				break;
		}

		return (Stopwatch.GetTimestamp() - sliceStart) * 1000.0 / Stopwatch.Frequency;
	}

	private static long BudgetTicks => (long)(Math.Max(0.1f, Config.SpawnTimeBudgetMs) * Stopwatch.Frequency / 1000.0);

	private static int ProcessSpawns(long start)
	{
		int maxPerFrame = Math.Max(1, Config.SpawnMaxPerFrame);
		long budgetTicks = BudgetTicks;
		int spawned = 0;

		for (int priority = 0; priority < Buckets.Length && spawned < maxPerFrame; priority++)
		{
			List<Entry> bucket = Buckets[priority];
			if (bucket.Count == 0)
				continue;

			if (BucketUnsorted[priority])
			{
				bucket.Sort(FarthestFirst);
				BucketUnsorted[priority] = false;
			}

			while (bucket.Count > 0 && spawned < maxPerFrame)
			{
				if (Stopwatch.GetTimestamp() - start >= budgetTicks)
					return spawned;

				int last = bucket.Count - 1;
				Entry entry = bucket[last];
				bucket.RemoveAt(last);
				entry.Group.Pending--;

				MerBlockLink link = entry.Link;
				if (link == null || link.DestroyRequested || entry.Group.IsCancelled || link.Identity == null)
				{
					if (_sessionActive)
						_sessionDropped++;

					entry.Group.CheckCompleted();
					continue;
				}

				if (!_sessionActive)
					BeginSession();

				MerVisibility.Spawn(link.Identity, entry.Group, entry.Kind);
				spawned++;
				entry.Group.Spawned++;

				if (entry.DisableWhenReady && link.TryGetComponent(out AdminToyBase toy))
					PendingDisable.Add(toy);

				try
				{
					entry.OnSpawned?.Invoke(link.gameObject);
				}
				catch (Exception e)
				{
					Logger.Error($"Spawn callback for {link.name} failed: {e}");
				}

				entry.Group.CheckCompleted();
			}
		}

		return spawned;
	}

	private static void BeginSession()
	{
		_sessionActive = true;
		_sessionStart = Stopwatch.GetTimestamp();
		_sessionSpawned = 0;
		_sessionDropped = 0;
		_sessionFrames = 0;
		_sessionMaxPerFrame = 0;
		_sessionWorstSliceMs = 0;
		_sessionTotalSliceMs = 0;
		_sessionSlicesOverBudget = 0;
		_sessionWorstFrameMs = 0;
		_sessionBuildMs = 0;
		_sessionWorstBuildMs = 0;
		_sessionBuildFrames = 0;
		_sessionGcStart = GC.CollectionCount(0);
	}

	private static void EndSession()
	{
		_sessionActive = false;
		double seconds = (Stopwatch.GetTimestamp() - _sessionStart) / (double)Stopwatch.Frequency;
		double rate = seconds > 0 ? _sessionSpawned / seconds : _sessionSpawned;
		double averageSlice = _sessionFrames > 0 ? _sessionTotalSliceMs / _sessionFrames : 0;
		LastSessionSummary = $"{_sessionSpawned} spawned in {seconds:F2} s ({rate:F0}/s) over {_sessionFrames} frames, max {_sessionMaxPerFrame}/frame; " +
			$"queue slice average {averageSlice:F2} ms, slowest {_sessionWorstSliceMs:F2} ms, {_sessionSlicesOverBudget} slices over twice the budget; " +
			$"schematic building {_sessionBuildMs:F1} ms over {_sessionBuildFrames} frames, slowest {_sessionWorstBuildMs:F2} ms; " +
			$"slowest server frame {_sessionWorstFrameMs:F1} ms; {GC.CollectionCount(0) - _sessionGcStart} GCs; {_sessionDropped} dropped (cancelled)";

		if (Config.LogSpawnStats)
			Logger.Info($"Spawn queue drained: {LastSessionSummary}.");
	}

	private static float DistanceToNearestPlayerSq(Vector3 position)
	{
		if (_playerPositionsFrame != Time.frameCount)
		{
			_playerPositionsFrame = Time.frameCount;
			PlayerPositions.Clear();
			foreach (ReferenceHub hub in ReferenceHub.AllHubs)
			{
				if (hub.isLocalPlayer || hub.connectionToClient == null || !hub.connectionToClient.isReady)
					continue;

				PlayerPositions.Add(hub.transform.position);
			}
		}

		float best = 0f;
		for (int i = 0; i < PlayerPositions.Count; i++)
		{
			float distance = (PlayerPositions[i] - position).sqrMagnitude;
			if (i == 0 || distance < best)
				best = distance;
		}

		return best;
	}

	private struct Entry
	{
		public MerBlockLink Link;

		public SpawnGroup Group;

		public MerObjectKind Kind;

		public bool DisableWhenReady;

		public Action<GameObject>? OnSpawned;

		public float DistanceSq;
	}
}
