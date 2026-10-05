using MEC;
using Mirror;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Objects;
using RelativePositioning;
using UnityEngine;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// Keeps the server's numbering of door waypoints equal to the clients' when MER doors spawn, move or disappear.
/// </summary>
/// <remarks>
/// <para>
/// Player, ragdoll and pickup positions travel relative to the nearest waypoint, identified by a byte id
/// (<c>RelativePositioning/RelativePosition.cs</c>). Every door prefab carries a <see cref="NetIdWaypoint"/>; each side numbers
/// its door waypoints 32, 33, ... in netId order, but only in the frame after a waypoint's <c>Start</c> (or after map
/// generation), and reads a waypoint's position only then (<c>NetIdWaypoint.Update</c>).
/// </para>
/// <para>
/// A MER door is instantiated before the spawn queue network spawns it. If its waypoint started then, the server would
/// number it with netId 0, ahead of every vanilla door, and never again once it had a netId: every door id on the server
/// would be one higher than on the clients, which would place teleported or moving players and new pickups near any door
/// relative to the wrong door. Moving a door (a respawn gives a new netId) or destroying one causes the same kind of
/// difference. So:
/// </para>
/// <list type="bullet">
/// <item>a new MER door's waypoint stays disabled until the door is network spawned, so it starts (and is numbered) with
/// its netId, after every vanilla door, as on the clients;</item>
/// <item>after a door is network spawned or respawned, the server renumbers on the next frame, as each client does when
/// the door arrives (a respawned door has a new netId, and a moved door a new waypoint position);</item>
/// <item>destroying a door does not renumber anywhere: connected clients keep a gap, and so does the server. A client
/// that joins later numbers compactly, which differs for every door after the gap; so when a MER door with a higher
/// netId remains, the newest one is respawned the next frame, which makes every client and the server renumber
/// compactly.</item>
/// </list>
/// </remarks>
internal static class MerWaypoints
{
	/// <summary>
	/// The first id the game gives a door waypoint (<c>NetIdWaypoint.Offset</c>).
	/// </summary>
	private const int FirstId = 32;

	/// <summary>
	/// Door waypoint ids are bytes and 0 is reserved: the game's renumbering throws when the 225th door would get id 0.
	/// </summary>
	public const int MaxDoorWaypoints = byte.MaxValue - FirstId + 1;

	private static uint _destroyedMinNetId = uint.MaxValue;

	private static bool _compactScheduled;

	/// <summary>
	/// Gets the number of server renumberings requested.
	/// </summary>
	public static int Refreshes { get; private set; }

	/// <summary>
	/// Gets the number of doors respawned to renumber clients after a destroy.
	/// </summary>
	public static int CompactingRespawns { get; private set; }

	/// <summary>
	/// Gets the number of door waypoints in the scene (vanilla and MER doors).
	/// </summary>
	public static int Count => NetIdWaypoint.AllNetWaypoints.Count;

	/// <summary>
	/// Keeps the waypoint of a freshly instantiated door from starting until <see cref="OnSpawned"/>: Unity runs
	/// <c>Start</c> only on enabled components, and a waypoint that starts with netId 0 is numbered ahead of every other door.
	/// </summary>
	public static void HoldUntilSpawned(GameObject gameObject)
	{
		if (gameObject.TryGetComponent(out NetIdWaypoint waypoint))
			waypoint.enabled = false;
	}

	/// <summary>
	/// Call after an object was network spawned (also for respawns).
	/// </summary>
	public static void OnSpawned(NetworkIdentity identity)
	{
		if (identity == null || identity.netId == 0 || !identity.TryGetComponent(out NetIdWaypoint waypoint))
			return;

		RequestServerRefresh(waypoint);
	}

	/// <summary>
	/// Call before a spawned object is destroyed for good.
	/// </summary>
	public static void OnDestroying(NetworkIdentity identity)
	{
		if (identity == null || identity.netId == 0 || !identity.TryGetComponent(out NetIdWaypoint _))
			return;

		if (identity.netId < _destroyedMinNetId)
			_destroyedMinNetId = identity.netId;

		if (_compactScheduled)
			return;

		_compactScheduled = true;
		Timing.CallDelayed(0f, Compact);
	}

	/// <summary>
	/// Forgets pending work (round restart: map generation renumbers everything).
	/// </summary>
	public static void Reset()
	{
		_destroyedMinNetId = uint.MaxValue;
		_compactScheduled = false;
	}

	/// <summary>
	/// Makes the game renumber the server's door waypoints (and re-read their positions) on the next frame.
	/// </summary>
	private static void RequestServerRefresh(NetIdWaypoint waypoint)
	{
		NetIdWaypoint._refreshNextFrame = true;

		// Waypoints disable themselves after a renumbering; one enabled waypoint runs the next one.
		waypoint.enabled = true;
		Refreshes++;
	}

	private static void Compact()
	{
		if (!_compactScheduled)
			return;

		_compactScheduled = false;
		uint destroyed = _destroyedMinNetId;
		_destroyedMinNetId = uint.MaxValue;

		// The newest spawned MER door after the gap; vanilla doors have lower netIds than any MER door.
		NetworkIdentity? newest = null;
		foreach (NetIdWaypoint waypoint in NetIdWaypoint.AllNetWaypoints)
		{
			if (waypoint == null)
				continue;

			NetworkIdentity identity = waypoint._targetNetId;
			if (identity == null || identity.netId <= destroyed || !identity.TryGetComponent(out MerBlockLink link) || link.DestroyRequested)
				continue;

			if (newest == null || identity.netId > newest.netId)
				newest = identity;
		}

		if (newest == null)
			return;

		CompactingRespawns++;
		MerBlockLink newestLink = newest.GetComponent<MerBlockLink>();
		MerVisibility.Respawn(newest, newestLink.Group, MerObjectKind.Door);
	}
}
