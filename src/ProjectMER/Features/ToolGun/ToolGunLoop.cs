using System.Diagnostics;
using MEC;
using UnityEngine;

namespace ProjectMER.Features.ToolGun;

/// <summary>
/// The one timed coroutine of the tool gun: every <c>hud_interval</c> it updates the HUD and the selection box of every
/// editing player and rescans the editing-only trigger colliders. It runs only while someone holds a tool gun, has a
/// selection, grabs, or still has a HUD hint on screen.
/// </summary>
internal static class ToolGunLoop
{
	private static CoroutineHandle _handle;

	private static bool _running;

	/// <summary>
	/// Gets the number of ticks run.
	/// </summary>
	public static int Ticks { get; private set; }

	/// <summary>
	/// Gets the duration of the last tick in milliseconds.
	/// </summary>
	public static double LastTickMs { get; private set; }

	/// <summary>
	/// Gets the slowest tick in milliseconds.
	/// </summary>
	public static double MaxTickMs { get; private set; }

	/// <summary>
	/// Gets whether the coroutine is running.
	/// </summary>
	public static bool IsRunning => _running;

	/// <summary>
	/// Starts the coroutine if it is not running.
	/// </summary>
	public static void EnsureRunning()
	{
		if (_running || ProjectMER.Singleton == null)
			return;

		_running = true;
		_handle = Timing.RunCoroutine(Run(), Segment.Update);
	}

	/// <summary>
	/// Stops the coroutine.
	/// </summary>
	public static void Stop()
	{
		Timing.KillCoroutines(_handle);
		_running = false;
	}

	/// <summary>
	/// Resets the statistics.
	/// </summary>
	public static void ResetStats()
	{
		Ticks = 0;
		LastTickMs = 0;
		MaxTickMs = 0;
	}

	private static IEnumerator<float> Run()
	{
		while (true)
		{
			yield return Timing.WaitForSeconds(ToolGunHud.HudInterval);

			bool active;
			try
			{
				active = Tick();
			}
			catch (Exception e)
			{
				Logger.Error($"Tool gun update failed: {e}");
				active = true;
			}

			if (!active)
				break;
		}

		_running = false;
	}

	private static bool Tick()
	{
		long start = Stopwatch.GetTimestamp();
		float now = Time.realtimeSinceStartup;
		bool active = EditingColliders.IsActive;

		foreach (ToolGunState state in ToolGunState.Values)
		{
			state.Box.Sync(state);
			if (ToolGunHud.Update(state, now) || state.Grab != null)
				active = true;
		}

		EditingColliders.Tick(now);

		Ticks++;
		LastTickMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
		if (LastTickMs > MaxTickMs)
			MaxTickMs = LastTickMs;

		return active;
	}
}
