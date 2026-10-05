using System.Collections.Concurrent;
using MEC;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// Hands work from other threads to the Unity main thread.
/// </summary>
/// <remarks>
/// <see cref="FileSystemWatcher"/> raises <c>Changed</c> on a thread-pool thread, where ProjectMER called
/// <c>Timing.CallDelayed</c> (not thread-safe) and reloaded maps. The watcher now only enqueues the map name; an MEC
/// coroutine drains the queue on the main thread every quarter second. Repeated change notifications for the same map
/// (editors often write a file twice) are merged, and a map whose file is unchanged since the server last read it (the
/// server's own <c>mp save</c>, which loads the map itself) is not reloaded again.
/// </remarks>
public static class MainThreadQueue
{
	private const float Interval = 0.25f;

	private static readonly ConcurrentQueue<string> MapReloads = new();

	private static readonly HashSet<string> Batch = [];

	private static CoroutineHandle _handle;

	/// <summary>
	/// Requests a reload of a loaded map. Thread-safe.
	/// </summary>
	/// <param name="mapName">The map name.</param>
	public static void EnqueueMapReload(string mapName) => MapReloads.Enqueue(mapName);

	/// <summary>
	/// Starts the drain coroutine.
	/// </summary>
	public static void Start()
	{
		Timing.KillCoroutines(_handle);
		_handle = Timing.RunCoroutine(Run());
	}

	/// <summary>
	/// Stops the drain coroutine.
	/// </summary>
	public static void Stop() => Timing.KillCoroutines(_handle);

	private static IEnumerator<float> Run()
	{
		while (true)
		{
			yield return Timing.WaitForSeconds(Interval);

			if (MapReloads.IsEmpty)
				continue;

			Batch.Clear();
			while (MapReloads.TryDequeue(out string mapName))
				Batch.Add(mapName);

			foreach (string mapName in Batch)
			{
				if (!MapUtils.LoadedMaps.ContainsKey(mapName))
					continue;

				try
				{
					// The server's own save writes the file and loads the map itself; reloading again would destroy and
					// stream the whole map a second time.
					if (MapUtils.IsUnchangedSinceRead(mapName))
						continue;

					Logger.Info($"Map file {mapName}.yml changed; reloading it.");
					MapUtils.LoadMap(mapName);
				}
				catch (Exception e)
				{
					Logger.Error(e);
				}
			}
		}
	}
}
