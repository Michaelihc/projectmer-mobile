using LabApi.Features.Wrappers;
using Mirror;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Objects;
using UnityEngine;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// Decides which players observe each networked MER object. Every MER spawn, respawn, in-place resend and destroy goes
/// through this class.
/// </summary>
/// <remarks>
/// <para>
/// Phase A behaviour: every ready connection observes every MER object (Mirror's default, <c>NetworkServer.aoi</c> is
/// null in Carl Mod), and a joining player receives all of them in Mirror's spawn burst.
/// </para>
/// <para>
/// Seam for managed visibility (docs/projectmer-port-plan.md §3.8), to be implemented inside this file:
/// set <c>identity.visibility = Visibility.ForceHidden</c> in <see cref="Spawn"/> before <c>NetworkServer.Spawn</c>, then
/// add observers per connection with <c>identity.AddObserver(conn)</c> and remove them with
/// <c>identity.RemoveObserver(conn)</c> plus <c>conn.RemoveFromObserving(identity, false)</c>. Join streaming,
/// admin-only objects (<paramref name="adminOnly"/>), zone culling by <see cref="SpawnGroup"/> and spectator handling
/// hook in here. Callers already pass the group, kind and world position this needs.
/// </para>
/// </remarks>
public static class MerVisibility
{
	/// <summary>
	/// Network spawns a prepared MER object.
	/// </summary>
	/// <param name="identity">The object's identity. It must have no parent transform.</param>
	/// <param name="group">The group it was loaded with.</param>
	/// <param name="kind">The kind of object.</param>
	/// <param name="adminOnly">Whether only editing admins should see it (indicators, selection feedback).</param>
	public static void Spawn(NetworkIdentity identity, SpawnGroup? group, MerObjectKind kind, bool adminOnly = false)
	{
		NetworkServer.Spawn(identity.gameObject);
	}

	/// <summary>
	/// Re-sends the full spawn payload of a spawned object to its current observers. The client reuses the existing
	/// object (<c>NetworkClient.FindOrSpawnObject</c> finds the netId) and applies the transform at full precision, so
	/// static toys move without a destroy, a re-instantiation or leaked materials.
	/// </summary>
	/// <param name="identity">The spawned object's identity.</param>
	public static void Resend(NetworkIdentity identity)
	{
		if (identity == null || identity.netId == 0 || !NetworkServer.spawned.ContainsKey(identity.netId))
			return;

		foreach (NetworkConnectionToClient connection in identity.observers.Values)
			NetworkServer.SendSpawnMessage(identity, connection);

		// The payload carried every SyncVar; do not send them again as a delta.
		identity.ClearAllComponentsDirtyBits();
	}

	/// <summary>
	/// Unspawns and spawns an object again, for state the client only reads when it creates the object (the primitive
	/// collider). Observers stay the same as for a first spawn.
	/// </summary>
	/// <param name="identity">The object's identity.</param>
	/// <param name="group">The group it belongs to.</param>
	/// <param name="kind">The kind of object.</param>
	public static void Respawn(NetworkIdentity identity, SpawnGroup? group, MerObjectKind kind)
	{
		if (identity.netId != 0 && NetworkServer.spawned.ContainsKey(identity.netId))
			NetworkServer.UnSpawn(identity.gameObject);

		Spawn(identity, group, kind);
	}

	/// <summary>
	/// Destroys a MER object on the server and for every observer. Objects that were never spawned are destroyed
	/// locally only.
	/// </summary>
	/// <param name="gameObject">The object.</param>
	public static void Destroy(GameObject gameObject)
	{
		if (gameObject == null)
			return;

		if (gameObject.TryGetComponent(out NetworkIdentity identity) && identity.netId != 0 && NetworkServer.spawned.ContainsKey(identity.netId))
		{
			NetworkServer.Destroy(gameObject);
			return;
		}

		UnityEngine.Object.Destroy(gameObject);
	}

	/// <summary>
	/// Makes a player observe a spawned object (sends its spawn message), keeping Mirror's observer bookkeeping right.
	/// Resends in place if the player already observes it.
	/// </summary>
	/// <param name="identity">The spawned object's identity.</param>
	/// <param name="player">The player.</param>
	public static void ShowTo(NetworkIdentity identity, Player player)
	{
		NetworkConnectionToClient? connection = player.ConnectionToClient;
		if (connection == null || identity == null || identity.netId == 0)
			return;

		if (identity.observers.ContainsKey(connection.connectionId))
		{
			NetworkServer.SendSpawnMessage(identity, connection);
			return;
		}

		identity.AddObserver(connection);
	}

	/// <summary>
	/// Stops a player from observing an object (sends <c>ObjectHideMessage</c>; the client destroys its copy).
	/// </summary>
	/// <param name="identity">The spawned object's identity.</param>
	/// <param name="player">The player.</param>
	public static void HideFrom(NetworkIdentity identity, Player player)
	{
		NetworkConnectionToClient? connection = player.ConnectionToClient;
		if (connection == null || identity == null || !identity.observers.ContainsKey(connection.connectionId))
			return;

		identity.RemoveObserver(connection);
		connection.RemoveFromObserving(identity, false);
	}

	/// <summary>
	/// Gets the number of MER objects a player currently observes.
	/// </summary>
	/// <param name="player">The player.</param>
	/// <returns>The count.</returns>
	public static int CountObserved(Player player)
	{
		NetworkConnectionToClient? connection = player.ConnectionToClient;
		if (connection == null)
			return 0;

		int count = 0;
		foreach (NetworkIdentity identity in connection.observing)
		{
			if (identity != null && identity.TryGetComponent(out MerBlockLink _))
				count++;
		}

		return count;
	}

	/// <summary>
	/// Clears per-round state. Called on round restart and when waiting for players.
	/// </summary>
	public static void Reset()
	{
	}
}
