using System.Diagnostics;
using Mirror;
using UnityEngine;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// Visibility state of one remote connection: which zones it sees and the paced show and hide work waiting for it.
/// </summary>
internal sealed class VisibilityViewer
{
	public VisibilityViewer(ReferenceHub hub, NetworkConnectionToClient connection)
	{
		Hub = hub;
		Connection = connection;
		ConnectionId = connection.connectionId;
	}

	/// <summary>
	/// Gets the player.
	/// </summary>
	public ReferenceHub Hub { get; }

	/// <summary>
	/// Gets the connection.
	/// </summary>
	public NetworkConnectionToClient Connection { get; }

	/// <summary>
	/// Gets the connection id (kept after the connection is gone).
	/// </summary>
	public int ConnectionId { get; }

	/// <summary>
	/// Gets or sets whether the connection was ready and received its join stream (it then also gets new spawns directly).
	/// </summary>
	public bool Started { get; set; }

	/// <summary>
	/// Gets or sets the zones whose culled cells this connection sees (bit per zone index).
	/// </summary>
	public int Mask { get; set; }

	/// <summary>
	/// Gets or sets the zones wanted at the previous evaluation (the hysteresis of a zone starts when it stops being wanted).
	/// </summary>
	public int PreviousWanted { get; set; }

	/// <summary>
	/// Gets or sets whether zones are only added (spectators, SCP-079 and players without a role).
	/// </summary>
	public bool AddOnly { get; set; }

	/// <summary>
	/// Gets or sets the last zone the player was in (per-zone culling keeps it while the room lookup finds nothing).
	/// </summary>
	public int LastZone { get; set; } = -1;

	/// <summary>
	/// Gets or sets the zone the player's body is in (-1 without a body or outside every zone).
	/// </summary>
	public int CurrentZone { get; set; } = -1;

	/// <summary>
	/// Gets or sets the number of shows sent to this connection in the current frame.
	/// </summary>
	public int FrameSent { get; set; }

	/// <summary>
	/// Gets or sets the time at which the zones should be evaluated outside the poll (after a role change), or -1.
	/// </summary>
	public float EvaluateAt { get; set; } = -1f;

	/// <summary>
	/// Gets the last time each zone was wanted (current, prefetched or spectated), for the hysteresis.
	/// </summary>
	public float[] LastWanted { get; } = new float[MerVisibility.ZoneCount];

	/// <summary>
	/// Gets the point streams of each zone are ordered around (nearest first).
	/// </summary>
	public Vector3[] Focus { get; } = new Vector3[MerVisibility.ZoneCount];

	/// <summary>
	/// Gets the objects waiting to be shown, sorted so the next one is last.
	/// </summary>
	public List<StreamItem> Shows { get; } = [];

	/// <summary>
	/// Gets or sets whether <see cref="Shows"/> needs sorting.
	/// </summary>
	public bool ShowsUnsorted { get; set; }

	/// <summary>
	/// Gets the objects waiting to be hidden (processed in order).
	/// </summary>
	public List<VisibilityEntry> Hides { get; } = [];

	/// <summary>
	/// Gets or sets the index of the next hide.
	/// </summary>
	public int HideCursor { get; set; }

	/// <summary>
	/// Gets or sets the number of shows sent to this connection since it was added.
	/// </summary>
	public long TotalShows { get; set; }

	/// <summary>
	/// Gets or sets the number of hides sent to this connection since it was added.
	/// </summary>
	public long TotalHides { get; set; }

	/// <summary>
	/// Gets or sets the number of zone transitions (zones added or removed).
	/// </summary>
	public int Transitions { get; set; }

	// Statistics of the current stream session (from the first queued show or hide until both lists are empty).
	public bool SessionActive { get; set; }

	public string SessionReason { get; set; } = string.Empty;

	public long SessionStart { get; set; }

	public int SessionShows { get; set; }

	public int SessionHides { get; set; }

	public int SessionSkipped { get; set; }

	public int SessionFrames { get; set; }

	public int SessionMaxPerFrame { get; set; }

	public int SessionLastFrame { get; set; } = -1;

	public int SessionFrameCount { get; set; }

	/// <summary>
	/// Gets whether the connection has show or hide work.
	/// </summary>
	public bool HasWork => Shows.Count > 0 || HideCursor < Hides.Count;

	/// <summary>
	/// Gets the player's nickname for logs.
	/// </summary>
	public string Name => Hub != null && Hub.nicknameSync != null && Hub.nicknameSync.NickSet ? Hub.nicknameSync.MyNick : $"connection {ConnectionId}";

	/// <summary>
	/// Starts a statistics session when none is running.
	/// </summary>
	/// <param name="reason">What started the stream (join, zone, audience...).</param>
	public void BeginSession(string reason)
	{
		if (SessionActive)
		{
			if (SessionReason.IndexOf(reason, StringComparison.Ordinal) < 0)
				SessionReason = SessionReason + "+" + reason;

			return;
		}

		SessionActive = true;
		SessionReason = reason;
		SessionStart = Stopwatch.GetTimestamp();
		SessionShows = 0;
		SessionHides = 0;
		SessionSkipped = 0;
		SessionFrames = 0;
		SessionMaxPerFrame = 0;
		SessionLastFrame = -1;
		SessionFrameCount = 0;
	}

	/// <summary>
	/// Counts one message sent this frame for the session statistics.
	/// </summary>
	public void CountSent(bool show)
	{
		if (show)
		{
			SessionShows++;
			TotalShows++;
		}
		else
		{
			SessionHides++;
			TotalHides++;
		}

		int frame = Time.frameCount;
		if (frame != SessionLastFrame)
		{
			SessionLastFrame = frame;
			SessionFrames++;
			SessionFrameCount = 0;
		}

		SessionFrameCount++;
		if (SessionFrameCount > SessionMaxPerFrame)
			SessionMaxPerFrame = SessionFrameCount;
	}

	/// <summary>
	/// Drops pending work and the zone state (the connection is not ready, or a round reset).
	/// </summary>
	public void ResetState()
	{
		Started = false;
		Mask = 0;
		PreviousWanted = 0;
		AddOnly = false;
		LastZone = -1;
		CurrentZone = -1;
		FrameSent = 0;
		EvaluateAt = -1f;
		Array.Clear(LastWanted, 0, LastWanted.Length);
		Shows.Clear();
		ShowsUnsorted = false;
		Hides.Clear();
		HideCursor = 0;
		SessionActive = false;
	}
}

/// <summary>
/// An object waiting to be shown to a connection, with its sort key (priority, then squared distance).
/// </summary>
internal struct StreamItem
{
	public VisibilityEntry Entry;

	public float Key;

	public StreamItem(VisibilityEntry entry, float key)
	{
		Entry = entry;
		Key = key;
	}
}
