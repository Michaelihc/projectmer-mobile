using System.Diagnostics;
using System.Text;
using Interactables.Interobjects;
using LabApi.Features.Wrappers;
using MapGeneration;
using MEC;
using Mirror;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.PlayableScps.Scp079;
using PlayerRoles.Spectating;
using ProjectMER.Configs;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Objects;
using ProjectMER.Features.Serializable;
using UnityEngine;
using ElevatorDoor = Interactables.Interobjects.ElevatorDoor;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// Decides which players observe each networked MER object (docs/projectmer-port-plan.md §3.8). Every MER spawn, respawn,
/// in-place resend and destroy goes through this class.
/// </summary>
/// <remarks>
/// <para>
/// Carl Mod has no interest management (<c>NetworkServer.aoi</c> is null), so Mirror would give every ready connection
/// every object, and a joining player every object in one burst. With <c>managed_visibility</c> MER objects are spawned
/// <see cref="Visibility.ForceHidden"/> instead, and this class adds and removes Mirror observers per connection
/// (<c>identity.AddObserver</c> sends the spawn message, <c>RemoveObserver</c> plus <c>RemoveFromObserving</c> the
/// <c>ObjectHideMessage</c>). Mirror's <c>identity.observers</c> remains the record of who receives an object.
/// </para>
/// <list type="bullet">
/// <item><b>Spawns</b> from the spawn queue are shown at once to every ready player who should see them (the queue already
/// paces them). The server's own host connection observes every MER object, as without managed visibility, so server-side
/// SyncVar hooks keep running.</item>
/// <item><b>Join streaming.</b> A player whose connection becomes ready receives the existing objects over several frames:
/// collidable first, then visible, lights and pickups, each nearest first (the spawn queue's order). Each player gets at
/// most <c>spawn_max_per_frame</c> objects per frame, and all streams together stay within <c>spawn_time_budget_ms</c>
/// (a quarter of both while the spawn queue is busy). Hides are paced at four times the limit.</item>
/// <item><b>Players who are not ready</b> are never made observers. A player whose connection stops being ready (Mirror
/// clears its observers) is streamed again from scratch once it is ready again. Disconnects drop the player's pending
/// work; Mirror removes its observers.</item>
/// <item><b>Admin-only objects</b> (indicators, selection feedback, grab proxies) are bound to a
/// <see cref="VisibilityAudience"/>, or shown explicitly with <see cref="ShowTo(NetworkIdentity, Player)"/>. Nobody else
/// receives any message for them.</item>
/// <item><b>Zone culling</b> (<c>zone_culling</c>). Objects of a top-level group (a map load, or a schematic outside a map)
/// with at least <c>zone_culling_min_objects</c> networked objects are only shown to players in the same zone (groups are
/// culled provisionally while they load, see <see cref="VisibilityRoot"/>; doors never are, see
/// <see cref="VisibilityEntry.IsWaypoint"/>):
/// Surface (y ≥ 900, the game's own threshold) or Facility with <c>SurfaceFacility</c>; the room's zone with
/// <c>PerZone</c>. A schematic belongs to the zone of its root. A 1 s poll reads player positions. Players inside an
/// elevator whose floors are in different zones see every zone it serves (prefetch); a zone stays visible for 5 s after
/// it was last needed (hysteresis). Spectators and Overwatch follow the zone of the player they spectate, SCP-079 the
/// zone of its camera, and both only ever add zones until the player spawns again.</item>
/// <item><b>Teleports.</b> <see cref="Prefetch"/> shows the destination zone before a player is moved (MER teleports and
/// player spawnpoints), its nearest collidable objects at once.</item>
/// </list>
/// <para>
/// Hysteresis, prefetch and add-only spectators bound the churn: every hide costs the phone a client-side destroy that
/// leaks the primitive's two materials (docs/projectmer-port-plan.md fact 5), so zones never change on a walk.
/// </para>
/// </remarks>
public static class MerVisibility
{
	/// <summary>
	/// Number of zone indices (bits of a viewer mask).
	/// </summary>
	public const int ZoneCount = 8;

	/// <summary>
	/// Seconds between position polls.
	/// </summary>
	public const float PollInterval = 1f;

	/// <summary>
	/// Seconds a zone stays visible after it was last needed.
	/// </summary>
	public const float Hysteresis = 5f;

	/// <summary>
	/// The height from which a position is on the surface (<c>AlphaWarheadController.CanBeDetonated</c> uses it too).
	/// </summary>
	public const float SurfaceHeight = 900f;

	private const int FacilityIndex = 0;

	private const int SurfaceIndex = 1;

	// Spawn points are applied after the role change event; evaluate a little later.
	private const float RoleChangeDelay = 0.1f;

	// Sort key offset per priority; squared distances stay far below it (2 km² < 1e8 m²... 4e6 m²).
	private const float PriorityKeyStep = 1e8f;

	private static readonly List<VisibilityEntry> Entries = [];

	private static readonly Dictionary<SpawnGroup, VisibilityRoot> Roots = [];

	private static readonly List<VisibilityViewer> Viewers = [];

	private static readonly List<VisibilityAudience> Audiences = [];

	private static readonly List<VisibilityEntry> OverrideEntries = [];

	private static readonly Dictionary<ElevatorManager.ElevatorGroup, int> ElevatorZoneMasks = [];

	private static readonly Comparison<StreamItem> FarthestFirst = static (a, b) => b.Key.CompareTo(a.Key);

	private static bool _hooked;

	private static bool _pollRunning;

	private static bool _pumpRunning;

	private static CoroutineHandle _pollHandle;

	private static CoroutineHandle _pumpHandle;

	private static int _roundRobin;

	private static ZoneCullingMode _elevatorMaskMode;

	private static ZoneCullingMode? _mode;

	/// <summary>
	/// Gets the number of spawn messages sent to remote players when objects were spawned or respawned.
	/// </summary>
	public static long SpawnShows { get; private set; }

	/// <summary>
	/// Gets the number of spawn messages sent by paced streams (join, zone, audience, explicit).
	/// </summary>
	public static long StreamShows { get; private set; }

	/// <summary>
	/// Gets the number of hide messages sent (zone culling, audiences, explicit hides).
	/// </summary>
	public static long HidesSent { get; private set; }

	/// <summary>
	/// Gets the number of zone transitions (a player's set of visible zones changed).
	/// </summary>
	public static long ZoneTransitions { get; private set; }

	/// <summary>
	/// Gets the slowest stream frame (pump slice) in milliseconds since the last reset.
	/// </summary>
	public static double WorstPumpMs { get; private set; }

	/// <summary>
	/// Gets the number of frames the stream pump ran since the last reset.
	/// </summary>
	public static long PumpFrames { get; private set; }

	/// <summary>
	/// Gets the slowest position poll in milliseconds since the last reset.
	/// </summary>
	public static double WorstPollMs { get; private set; }

	/// <summary>
	/// Gets a summary of the last finished stream.
	/// </summary>
	public static string LastStreamSummary { get; private set; } = "none";

	/// <summary>
	/// Gets the number of MER objects with managed visibility.
	/// </summary>
	public static int ManagedCount => Entries.Count;

	private static Config? Config => ProjectMER.Singleton?.Config;

	/// <summary>
	/// Gets the zone culling mode of the current round (read from the config at the round reset, so zones of objects
	/// already spawned never change meaning mid-round).
	/// </summary>
	private static ZoneCullingMode Mode
	{
		get
		{
			if (_mode is ZoneCullingMode mode)
				return mode;

			Config? config = Config;
			if (config == null)
				return ZoneCullingMode.None;

			mode = config.ManagedVisibility ? config.ZoneCulling : ZoneCullingMode.None;
			_mode = mode;
			return mode;
		}
	}

	/// <summary>
	/// Subscribes to the game's player and role events. Called when the plugin is enabled and before the first managed spawn.
	/// </summary>
	public static void Start()
	{
		if (_hooked)
			return;

		_hooked = true;
		ReferenceHub.OnPlayerAdded += OnPlayerAdded;
		ReferenceHub.OnPlayerRemoved += OnPlayerRemoved;
		PlayerRoleManager.OnRoleChanged += OnRoleChanged;
	}

	/// <summary>
	/// Unsubscribes and drops all state. Called when the plugin is disabled.
	/// </summary>
	public static void Stop()
	{
		if (_hooked)
		{
			_hooked = false;
			ReferenceHub.OnPlayerAdded -= OnPlayerAdded;
			ReferenceHub.OnPlayerRemoved -= OnPlayerRemoved;
			PlayerRoleManager.OnRoleChanged -= OnRoleChanged;
		}

		Timing.KillCoroutines(_pollHandle);
		Timing.KillCoroutines(_pumpHandle);
		_pollRunning = false;
		_pumpRunning = false;
		ResetState();
		Viewers.Clear();
	}

	/// <summary>
	/// Network spawns a prepared MER object.
	/// </summary>
	/// <param name="identity">The object's identity. It must have no parent transform.</param>
	/// <param name="group">The group it was loaded with.</param>
	/// <param name="kind">The kind of object.</param>
	/// <param name="adminOnly">Whether nobody sees it until it is shown explicitly (<see cref="ShowTo(NetworkIdentity, Player)"/>).</param>
	public static void Spawn(NetworkIdentity identity, SpawnGroup? group, MerObjectKind kind, bool adminOnly = false) =>
		Spawn(identity, group, kind, adminOnly, null);

	/// <summary>
	/// Network spawns a prepared MER object.
	/// </summary>
	/// <param name="identity">The object's identity. It must have no parent transform.</param>
	/// <param name="group">The group it was loaded with.</param>
	/// <param name="kind">The kind of object.</param>
	/// <param name="adminOnly">Whether nobody sees it until it is shown explicitly (<see cref="ShowTo(NetworkIdentity, Player)"/>).</param>
	/// <param name="audience">The players who see this admin-only object (implies <paramref name="adminOnly"/>).</param>
	public static void Spawn(NetworkIdentity identity, SpawnGroup? group, MerObjectKind kind, bool adminOnly, VisibilityAudience? audience)
	{
		if (identity == null)
			return;

		// Spawning a registered object again (a respawn) keeps it admin-only unless told otherwise.
		if (!adminOnly && audience == null && identity.TryGetComponent(out VisibilityEntry existing) && existing.RegistryIndex >= 0)
		{
			adminOnly = existing.IsAdminOnly;
			audience = existing.Audience;
		}

		adminOnly |= audience != null;
		Config? config = Config;
		if (!adminOnly && (config == null || !config.ManagedVisibility))
		{
			// Mirror's default: every ready connection, and joining players get it in their spawn burst.
			if (identity.TryGetComponent(out VisibilityEntry stale))
			{
				Unregister(stale);
				UnityEngine.Object.Destroy(stale);
			}

			identity.visibility = Visibility.Default;
			NetworkServer.Spawn(identity.gameObject);
			MerWaypoints.OnSpawned(identity);
			return;
		}

		Start();
		identity.visibility = Visibility.ForceHidden;
		NetworkServer.Spawn(identity.gameObject);
		if (identity.netId == 0)
			return;

		MerWaypoints.OnSpawned(identity);

		if (!identity.TryGetComponent(out VisibilityEntry entry))
		{
			entry = identity.gameObject.AddComponent<VisibilityEntry>();
			entry.Identity = identity;
		}

		entry.Group = group;
		entry.Kind = kind;
		entry.IsAdminOnly = adminOnly;
		BindAudience(entry, audience);
		OnSpawned(entry, immediate: !adminOnly);
	}

	/// <summary>
	/// Binds a spawned admin-only object to an audience (or unbinds it with <see langword="null"/>): members see it, nobody
	/// else does.
	/// </summary>
	/// <param name="identity">The object's identity, spawned through <see cref="Spawn(NetworkIdentity, SpawnGroup?, MerObjectKind, bool, VisibilityAudience?)"/>.</param>
	/// <param name="audience">The audience.</param>
	public static void SetAudience(NetworkIdentity identity, VisibilityAudience? audience)
	{
		if (identity == null || !identity.TryGetComponent(out VisibilityEntry entry) || entry.RegistryIndex < 0 || entry.Audience == audience)
			return;

		entry.IsAdminOnly = true;
		RemoveFromCell(entry);
		BindAudience(entry, audience);
		Reapply(entry);
	}

	/// <summary>
	/// Re-sends the full spawn payload of a spawned object to its current observers. The client reuses the existing
	/// object (<c>NetworkClient.FindOrSpawnObject</c> finds the netId) and applies the transform at full precision, so
	/// static toys move without a destroy, a re-instantiation or leaked materials.
	/// </summary>
	/// <param name="identity">The spawned object's identity.</param>
	/// <remarks>A moved object that changed zone is shown to and hidden from the players of the new and old zone.</remarks>
	public static void Resend(NetworkIdentity identity)
	{
		if (!IsSpawned(identity))
			return;

		bool zoneChanged = false;
		if (identity.TryGetComponent(out VisibilityEntry entry) && entry.RegistryIndex >= 0 && !entry.IsAdminOnly)
		{
			int zone = ZoneOfEntry(entry);
			if (zone != entry.Zone && entry.Root != null)
			{
				AssignCell(entry, zone);
				Reapply(entry);
				zoneChanged = true;
			}
		}

		foreach (NetworkConnectionToClient connection in identity.observers.Values)
		{
			// Never to the server's own host connection: the host client deserializes a spawn payload into the server's
			// object one frame later (Mirror NetworkClient.OnHostClientSpawn), which would revert every SyncVar written
			// after this resend in the same frame (a selection box made dynamic for a grab snapped back to static).
			if (connection is LocalConnectionToClient)
				continue;

			// Players about to lose the object (it moved to another zone) get the hide instead of the update.
			if (zoneChanged && FindViewer(connection.connectionId) is { Started: true } viewer && !ShouldSee(entry!, viewer))
				continue;

			NetworkServer.SendSpawnMessage(identity, connection);
		}

		// The payload carried every SyncVar; do not send them again as a delta.
		identity.ClearAllComponentsDirtyBits();
	}

	/// <summary>
	/// Unspawns and spawns an object again, for state the client only reads when it creates the object (the primitive
	/// collider, door transforms). The object keeps its visibility (group, zone, audience, explicit decisions).
	/// </summary>
	/// <param name="identity">The object's identity.</param>
	/// <param name="group">The group it belongs to.</param>
	/// <param name="kind">The kind of object.</param>
	public static void Respawn(NetworkIdentity identity, SpawnGroup? group, MerObjectKind kind)
	{
		if (identity == null)
			return;

		if (IsSpawned(identity))
			NetworkServer.UnSpawn(identity.gameObject);

		if (identity.TryGetComponent(out VisibilityEntry entry) && entry.RegistryIndex >= 0)
		{
			Spawn(identity, group ?? entry.Group, kind, entry.IsAdminOnly, entry.Audience);
			return;
		}

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

		if (gameObject.TryGetComponent(out VisibilityEntry entry))
			Unregister(entry);

		if (gameObject.TryGetComponent(out NetworkIdentity identity) && IsSpawned(identity))
		{
			MerWaypoints.OnDestroying(identity);
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
	/// <remarks>For a MER object the decision sticks: zone culling and audiences no longer hide it from this player
	/// until <see cref="ClearOverride(NetworkIdentity, Player)"/>. A player who is not ready yet gets it once ready.</remarks>
	public static void ShowTo(NetworkIdentity identity, Player player)
	{
		NetworkConnectionToClient? connection = player.ConnectionToClient;
		if (connection == null || identity == null || identity.netId == 0)
			return;

		if (identity.TryGetComponent(out VisibilityEntry entry) && entry.RegistryIndex >= 0)
			SetOverride(entry, connection.connectionId, true);

		if (identity.observers.ContainsKey(connection.connectionId))
		{
			// See Resend: the host connection must not get in-place spawn payloads.
			if (connection is not LocalConnectionToClient)
				NetworkServer.SendSpawnMessage(identity, connection);

			return;
		}

		if (!connection.isReady || !IsSpawned(identity))
			return;

		identity.AddObserver(connection);
		CountShow(connection, false);
	}

	/// <summary>
	/// Stops a player from observing an object (sends <c>ObjectHideMessage</c>; the client destroys its copy).
	/// </summary>
	/// <param name="identity">The spawned object's identity.</param>
	/// <param name="player">The player.</param>
	/// <remarks>For a MER object the decision sticks until <see cref="ClearOverride(NetworkIdentity, Player)"/>.
	/// The server's host connection always observes MER objects.</remarks>
	public static void HideFrom(NetworkIdentity identity, Player player)
	{
		NetworkConnectionToClient? connection = player.ConnectionToClient;
		if (connection == null || identity == null || connection is LocalConnectionToClient)
			return;

		if (identity.TryGetComponent(out VisibilityEntry entry) && entry.RegistryIndex >= 0)
			SetOverride(entry, connection.connectionId, false);

		if (!identity.observers.ContainsKey(connection.connectionId))
			return;

		identity.RemoveObserver(connection);
		connection.RemoveFromObserving(identity, false);
		HidesSent++;
		VisibilityViewer? viewer = FindViewer(connection.connectionId);
		if (viewer != null)
			viewer.TotalHides++;
	}

	/// <summary>
	/// Shows every networked block of a schematic to a player (paced like a join stream), and keeps it shown.
	/// </summary>
	/// <param name="schematic">The schematic.</param>
	/// <param name="player">The player.</param>
	public static void ShowTo(SchematicObject schematic, Player player) => SetSchematic(schematic, player, true);

	/// <summary>
	/// Hides every networked block of a schematic from a player, and keeps it hidden.
	/// </summary>
	/// <param name="schematic">The schematic.</param>
	/// <param name="player">The player.</param>
	public static void HideFrom(SchematicObject schematic, Player player) => SetSchematic(schematic, player, false);

	/// <summary>
	/// Forgets an explicit <see cref="ShowTo(NetworkIdentity, Player)"/> or <see cref="HideFrom(NetworkIdentity, Player)"/>
	/// for a player; zone culling and audiences decide again.
	/// </summary>
	/// <param name="identity">The object's identity.</param>
	/// <param name="player">The player.</param>
	public static void ClearOverride(NetworkIdentity identity, Player player)
	{
		NetworkConnectionToClient? connection = player.ConnectionToClient;
		if (connection == null || identity == null || !identity.TryGetComponent(out VisibilityEntry entry) || entry.Overrides == null)
			return;

		if (!entry.Overrides.Remove(connection.connectionId))
			return;

		if (entry.Overrides.Count == 0)
		{
			entry.Overrides = null;
			OverrideEntries.Remove(entry);
		}

		VisibilityViewer? viewer = FindViewer(connection.connectionId);
		if (viewer != null)
			Reapply(entry, viewer);
	}

	/// <summary>
	/// Forgets the explicit decisions about a schematic's blocks for a player.
	/// </summary>
	/// <param name="schematic">The schematic.</param>
	/// <param name="player">The player.</param>
	public static void ClearOverrides(SchematicObject schematic, Player player)
	{
		foreach (NetworkIdentity identity in schematic.NetworkIdentities)
			ClearOverride(identity, player);
	}

	/// <summary>
	/// Shows the zone of <paramref name="destination"/> to a player before a teleport: the nearest objects there are sent
	/// at once, the rest is streamed. The previous zone stays visible for <see cref="Hysteresis"/> seconds.
	/// </summary>
	/// <param name="player">The player about to be moved.</param>
	/// <param name="destination">The destination.</param>
	public static void Prefetch(Player player, Vector3 destination)
	{
		if (player == null || Mode == ZoneCullingMode.None)
			return;

		VisibilityViewer? viewer = FindViewer(player.ReferenceHub);
		if (viewer == null || !viewer.Started || !IsReady(viewer))
			return;

		int zone = ObjectZone(destination);
		if (zone < 0)
			return;

		float now = Time.unscaledTime;
		if (viewer.CurrentZone >= 0)
			viewer.LastWanted[viewer.CurrentZone] = now; // the hysteresis of the zone being left starts now

		viewer.LastWanted[zone] = now;
		viewer.Focus[zone] = destination;
		int bit = 1 << zone;
		if ((viewer.Mask & bit) != 0)
			return;

		int previous = viewer.Mask;
		viewer.Mask |= bit;
		viewer.Transitions++;
		ZoneTransitions++;
		EnqueueZone(viewer, zone, true, "teleport");

		// The floor at the destination first: send the nearest batch now, before the player is moved.
		SortShows(viewer);
		int limit = Math.Max(1, Config!.SpawnMaxPerFrame);
		int sent = 0;
		while (sent < limit && viewer.Shows.Count > 0)
		{
			if (TrySendNextShow(viewer))
				sent++;
		}

		LogTransition(viewer, previous, "teleport prefetch");
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
	/// Gets the zones whose culled MER groups a player sees, as names (for stats and tests).
	/// </summary>
	/// <param name="player">The player.</param>
	/// <returns>The zones, or <c>none</c>.</returns>
	public static string DescribeZones(Player player)
	{
		VisibilityViewer? viewer = FindViewer(player.ReferenceHub);
		return viewer == null ? "untracked" : ZonesToString(viewer.Mask);
	}

	/// <summary>
	/// Gets the number of objects waiting to be shown to and hidden from a player.
	/// </summary>
	/// <param name="player">The player.</param>
	/// <param name="shows">Pending shows (entries are re-checked when sent).</param>
	/// <param name="hides">Pending hides.</param>
	/// <returns>Whether the player is tracked.</returns>
	public static bool TryGetPending(Player player, out int shows, out int hides)
	{
		VisibilityViewer? viewer = FindViewer(player.ReferenceHub);
		shows = viewer?.Shows.Count ?? 0;
		hides = viewer == null ? 0 : viewer.Hides.Count - viewer.HideCursor;
		return viewer != null;
	}

	/// <summary>
	/// Gets the name of a zone index for the current <c>zone_culling</c> mode.
	/// </summary>
	/// <param name="zone">The zone index.</param>
	/// <returns>The name.</returns>
	public static string GetZoneName(int zone)
	{
		if (zone < 0)
			return "unculled";

		return Mode == ZoneCullingMode.SurfaceFacility
			? (zone == SurfaceIndex ? "Surface" : "Facility")
			: ((FacilityZone)zone).ToString();
	}

	/// <summary>
	/// Appends a visibility report (for <c>mp stats</c>).
	/// </summary>
	/// <param name="sb">The builder.</param>
	public static void AppendStats(StringBuilder sb)
	{
		Config? config = Config;
		sb.Append($"\nVisibility: managed {(config?.ManagedVisibility == true ? "on" : "off")}, zone culling {Mode} (min {config?.ZoneCullingMinObjects}); {Entries.Count} managed objects; ");
		int culled = 0, culledGroups = 0, provisional = 0;
		foreach (VisibilityRoot root in Roots.Values)
		{
			if (!root.Active)
				continue;

			culledGroups++;
			culled += root.Count;
			if (!root.Settled)
				provisional++;
		}

		sb.Append($"{culledGroups} culled groups ({culled} objects, {provisional} groups still loading); shows {SpawnShows} on spawn + {StreamShows} streamed, hides {HidesSent}, zone transitions {ZoneTransitions}; ");
		sb.Append($"stream frames {PumpFrames} (slowest {WorstPumpMs:F2} ms), slowest poll {WorstPollMs:F2} ms");
		sb.Append($"\nLast stream: {LastStreamSummary}");
		foreach (VisibilityViewer viewer in Viewers)
		{
			sb.Append($"\n- {viewer.Name}: zones {ZonesToString(viewer.Mask)}{(viewer.AddOnly ? " (add-only)" : string.Empty)}, ");
			sb.Append(viewer.Started ? $"{viewer.Shows.Count} shows and {viewer.Hides.Count - viewer.HideCursor} hides pending, " : "not ready, ");
			sb.Append($"sent {viewer.TotalShows} shows / {viewer.TotalHides} hides, {viewer.Transitions} transitions");
		}
	}

	/// <summary>
	/// Clears per-round state. Called on round restart; also subscribes to the player events (<see cref="Start"/>) so
	/// players who join before the first MER object spawns are tracked.
	/// </summary>
	public static void Reset()
	{
		ResetState();
		if (Config == null)
			return;

		Start();

		// Players who are already ready receive the new round's spawns directly instead of after the next poll.
		foreach (VisibilityViewer viewer in Viewers)
			TryStart(viewer);
	}

	private static void ResetState()
	{
		foreach (VisibilityEntry entry in Entries)
		{
			entry.RegistryIndex = -1;
			entry.Root = null;
			entry.Zone = -1;
			entry.CellIndex = -1;
			entry.Overrides = null;
			if (entry.Audience != null)
			{
				entry.Audience.Entries.Clear();
				entry.Audience.ResetMembers();
			}

			entry.Audience = null;
			entry.AudienceIndex = -1;
		}

		foreach (VisibilityAudience audience in Audiences)
		{
			audience.Entries.Clear();
			audience.ResetMembers();
		}

		Entries.Clear();
		Roots.Clear();
		Audiences.Clear();
		OverrideEntries.Clear();
		ElevatorZoneMasks.Clear();
		_mode = null;

		for (int i = Viewers.Count - 1; i >= 0; i--)
		{
			VisibilityViewer viewer = Viewers[i];
			viewer.ResetState();
			if (viewer.Hub == null || !IsConnected(viewer))
				Viewers.RemoveAt(i);
		}

		SpawnShows = 0;
		StreamShows = 0;
		HidesSent = 0;
		ZoneTransitions = 0;
		WorstPumpMs = 0;
		PumpFrames = 0;
		WorstPollMs = 0;
	}

	/// <summary>
	/// Forgets an object (its component is being destroyed, or it is destroyed through <see cref="Destroy"/>).
	/// </summary>
	internal static void Unregister(VisibilityEntry entry)
	{
		int index = entry.RegistryIndex;
		if (index >= 0 && index < Entries.Count && ReferenceEquals(Entries[index], entry))
		{
			int last = Entries.Count - 1;
			VisibilityEntry moved = Entries[last];
			Entries[index] = moved;
			moved.RegistryIndex = index;
			Entries.RemoveAt(last);
		}

		entry.RegistryIndex = -1;
		RemoveFromCell(entry);
		BindAudience(entry, null);
		if (entry.Overrides != null)
		{
			entry.Overrides = null;
			OverrideEntries.Remove(entry);
		}
	}

	/// <summary>
	/// Shows an audience's objects to a new member.
	/// </summary>
	internal static void OnAudienceMemberAdded(VisibilityAudience audience, ReferenceHub hub)
	{
		Start();
		if (!audience.Registered)
		{
			audience.Registered = true;
			Audiences.Add(audience);
		}

		VisibilityViewer? viewer = FindViewer(hub);
		if (viewer == null)
		{
			// A new viewer's join stream includes the audience's objects.
			AddViewer(hub);
			return;
		}

		if (!viewer.Started)
			return;

		Vector3 focus = PositionOf(viewer);
		int queued = 0;
		foreach (VisibilityEntry entry in audience.Entries)
		{
			if (!IsObserving(entry, viewer) && ShouldSee(entry, viewer))
			{
				viewer.Shows.Add(new StreamItem(entry, Key(entry, focus)));
				queued++;
			}
		}

		if (queued == 0)
			return;

		viewer.ShowsUnsorted = true;
		viewer.BeginSession("audience");
		EnsurePump();
	}

	/// <summary>
	/// Hides an audience's objects from a former member.
	/// </summary>
	internal static void OnAudienceMemberRemoved(VisibilityAudience audience, ReferenceHub hub)
	{
		if (audience.Count == 0 && audience.Registered)
		{
			audience.Registered = false;
			Audiences.Remove(audience);
		}

		VisibilityViewer? viewer = FindViewer(hub);
		if (viewer == null || !viewer.Started)
			return;

		int queued = 0;
		foreach (VisibilityEntry entry in audience.Entries)
		{
			if (IsObserving(entry, viewer) && !ShouldSee(entry, viewer))
			{
				viewer.Hides.Add(entry);
				queued++;
			}
		}

		if (queued == 0)
			return;

		viewer.BeginSession("audience");
		EnsurePump();
	}

	private static void OnSpawned(VisibilityEntry entry, bool immediate)
	{
		if (entry.RegistryIndex < 0)
		{
			entry.RegistryIndex = Entries.Count;
			Entries.Add(entry);
		}

		entry.Priority = ComputePriority(entry);
		entry.IsWaypoint = entry.TryGetComponent(out RelativePositioning.NetIdWaypoint _);
		if (entry.IsAdminOnly)
			RemoveFromCell(entry);
		else
			AssignCell(entry, ZoneOfEntry(entry));

		NetworkIdentity identity = entry.Identity;
		LocalConnectionToClient? host = NetworkServer.localConnection;
		if (host != null && host.isReady && !identity.observers.ContainsKey(host.connectionId))
			identity.AddObserver(host);

		foreach (VisibilityViewer viewer in Viewers)
		{
			if (!viewer.Started || !IsReady(viewer) || !ShouldSee(entry, viewer))
				continue;

			if (immediate)
			{
				if (Show(entry, viewer))
				{
					viewer.TotalShows++;
					SpawnShows++;
				}

				continue;
			}

			viewer.Shows.Add(new StreamItem(entry, Key(entry, PositionOf(viewer))));
			viewer.ShowsUnsorted = true;
			viewer.BeginSession("audience");
			EnsurePump();
		}

		EnsurePoll();
	}

	private static SpawnPriority ComputePriority(VisibilityEntry entry)
	{
		switch (entry.Kind)
		{
			case MerObjectKind.Light:
				return SpawnPriority.Light;
			case MerObjectKind.Pickup:
				return SpawnPriority.Pickup;
			case MerObjectKind.Structure:
			case MerObjectKind.Door:
			case MerObjectKind.ShootingTarget:
				return SpawnPriority.Collidable;
		}

		// The client builds a collider when a component of the Scale SyncVar is positive (the flag encoding).
		if (entry.TryGetComponent(out AdminToys.PrimitiveObjectToy primitive))
		{
			Vector3 scale = primitive.Scale;
			return scale.x > 0f || scale.y > 0f || scale.z > 0f ? SpawnPriority.Collidable : SpawnPriority.Visible;
		}

		return SpawnPriority.Visible;
	}

	private static void AssignCell(VisibilityEntry entry, int zone)
	{
		VisibilityRoot? root = null;
		if (Mode != ZoneCullingMode.None)
		{
			SpawnGroup? top = entry.Group;
			while (top?.Parent != null)
				top = top.Parent;

			if (top != null)
				root = GetOrCreateRoot(top, entry);
		}

		// Waypoints stay with every player: clients number them by the set they have (see VisibilityEntry.IsWaypoint).
		if (root == null || entry.IsWaypoint)
			zone = -1;

		if (ReferenceEquals(entry.Root, root))
		{
			// The same group in another zone (the object moved): only its cell changes. Leaving the group, even briefly, would
			// drop a group of one from Roots, and nothing would settle it or stream it to players entering its zone.
			if (root != null && entry.Zone != zone)
			{
				TakeOutOfZone(entry, root);
				PutInZone(entry, root, zone);
			}

			return;
		}

		RemoveFromCell(entry);
		if (root == null)
			return;

		entry.Root = root;
		root.Count++;
		root.LastAdded = Time.unscaledTime;
		PutInZone(entry, root, zone);

		// A settled small group that grows (tool gun) becomes culled; this is the only case that hides shown objects.
		if (root.Settled && !root.Active && root.Count >= Config!.ZoneCullingMinObjects)
			Activate(root);
	}

	private static void RemoveFromCell(VisibilityEntry entry)
	{
		VisibilityRoot? root = entry.Root;
		if (root == null)
			return;

		TakeOutOfZone(entry, root);
		entry.Root = null;
		root.Count--;
		if (root.Count <= 0 && Roots.TryGetValue(root.Group, out VisibilityRoot current) && ReferenceEquals(current, root))
			Roots.Remove(root.Group);
	}

	private static void PutInZone(VisibilityEntry entry, VisibilityRoot root, int zone)
	{
		entry.Zone = zone;
		entry.CellIndex = -1;
		if (zone < 0)
			return;

		List<VisibilityEntry> cell = root.Cells[zone] ??= [];
		entry.CellIndex = cell.Count;
		cell.Add(entry);
	}

	private static void TakeOutOfZone(VisibilityEntry entry, VisibilityRoot root)
	{
		int zone = entry.Zone;
		int index = entry.CellIndex;
		List<VisibilityEntry>? cell = zone >= 0 ? root.Cells[zone] : null;
		if (cell != null && index >= 0 && index < cell.Count && ReferenceEquals(cell[index], entry))
		{
			int last = cell.Count - 1;
			VisibilityEntry moved = cell[last];
			cell[index] = moved;
			moved.CellIndex = index;
			cell.RemoveAt(last);
		}

		entry.Zone = -1;
		entry.CellIndex = -1;
	}

	private static VisibilityRoot GetOrCreateRoot(SpawnGroup top, VisibilityEntry first)
	{
		if (Roots.TryGetValue(top, out VisibilityRoot root))
			return root;

		// Count what the load has created so far: queued and spawned objects of the group itself (the queue takes an object
		// out of Pending before spawning it and counts it in Spawned afterwards) plus the blocks of its schematics, which
		// wait in their schematic's own group. A group known to be large is culled for good; any other one is culled
		// provisionally until its load has settled (see Settle), so no player receives objects that are hidden again.
		int expected = top.Pending + top.Spawned + (ReferenceEquals(first.Group, top) ? 1 : 0);
		MapSchematic? owner = FindMap(top);
		if (owner != null)
		{
			foreach (MapEditorObject mapEditorObject in owner.SpawnedObjects)
			{
				if (mapEditorObject != null && mapEditorObject.TryGetComponent(out SchematicObject schematic) && !ReferenceEquals(schematic.SpawnGroup, top))
					expected += schematic.NetworkedCount;
			}
		}

		root = new VisibilityRoot(top, expected)
		{
			Map = owner,
			Schematic = owner == null && first.TryGetComponent(out MerBlockLink link) && link.Schematic != null && ReferenceEquals(link.Schematic.SpawnGroup, top) ? link.Schematic : null,
			Active = true,
		};

		Roots[top] = root;
		if (expected >= Config!.ZoneCullingMinObjects)
		{
			root.Settled = true;
			LogCulled(root);
		}

		return root;
	}

	private static MapSchematic? FindMap(SpawnGroup group)
	{
		foreach (MapSchematic map in MapUtils.LoadedMaps.Values)
		{
			if (ReferenceEquals(map.SpawnGroup, group))
				return map;
		}

		return null;
	}

	/// <summary>
	/// Decides provisionally culled groups whose load has finished: a group below <c>zone_culling_min_objects</c> is shown
	/// in every zone (its objects are streamed to the players of the other zones).
	/// </summary>
	private static void SettleRoots(float now)
	{
		foreach (VisibilityRoot root in Roots.Values)
		{
			if (root.Settled || now - root.LastAdded < PollInterval || !IsLoadFinished(root))
				continue;

			root.Settled = true;
			if (root.Count >= Config!.ZoneCullingMinObjects)
			{
				LogCulled(root);
				continue;
			}

			root.Active = false;
			int shows = 0;
			foreach (VisibilityViewer viewer in Viewers)
			{
				if (!viewer.Started || !IsReady(viewer))
					continue;

				Vector3 focus = PositionOf(viewer);
				int queued = 0;
				for (int zone = 0; zone < ZoneCount; zone++)
				{
					List<VisibilityEntry>? cell = root.Cells[zone];
					if (cell == null || (viewer.Mask & (1 << zone)) != 0)
						continue;

					foreach (VisibilityEntry entry in cell)
					{
						if (!IsObserving(entry, viewer) && ShouldSee(entry, viewer))
						{
							viewer.Shows.Add(new StreamItem(entry, Key(entry, focus)));
							queued++;
						}
					}
				}

				if (queued == 0)
					continue;

				shows += queued;
				viewer.ShowsUnsorted = true;
				viewer.BeginSession("small group");
				EnsurePump();
			}

			if (Config!.LogSpawnStats)
				Logger.Info($"Zone culling: {root.Group} has {root.Count} networked objects (below zone_culling_min_objects); shown in every zone ({shows} objects streamed to players in other zones).");
		}
	}

	/// <summary>
	/// Gets whether a group's load has finished: nothing of it is queued and its schematics are built.
	/// </summary>
	private static bool IsLoadFinished(VisibilityRoot root)
	{
		if (root.Group.Pending > 0)
			return false;

		if (root.Schematic != null && !root.Schematic.IsBuilt)
			return false;

		if (root.Map == null)
			return true;

		foreach (MapEditorObject mapEditorObject in root.Map.SpawnedObjects)
		{
			if (mapEditorObject != null && mapEditorObject.TryGetComponent(out SchematicObject schematic) && !schematic.IsBuilt)
				return false;
		}

		return true;
	}

	private static void LogCulled(VisibilityRoot root)
	{
		if (Config!.LogSpawnStats)
			Logger.Info($"Zone culling: {root.Group} ({Math.Max(root.Expected, root.Count)} networked objects) is shown per zone.");
	}

	private static void Activate(VisibilityRoot root)
	{
		root.Active = true;
		root.Settled = true;
		int hides = 0;
		foreach (VisibilityViewer viewer in Viewers)
		{
			if (!viewer.Started)
				continue;

			for (int zone = 0; zone < ZoneCount; zone++)
			{
				List<VisibilityEntry>? cell = root.Cells[zone];
				if (cell == null || (viewer.Mask & (1 << zone)) != 0)
					continue;

				foreach (VisibilityEntry entry in cell)
				{
					if (IsObserving(entry, viewer) && !ShouldSee(entry, viewer))
					{
						viewer.Hides.Add(entry);
						hides++;
					}
				}
			}

			if (viewer.HasWork)
			{
				viewer.BeginSession("activation");
				EnsurePump();
			}
		}

		if (Config!.LogSpawnStats)
			Logger.Info($"Zone culling: {root.Group} ({Math.Max(root.Expected, root.Count)} networked objects) is shown per zone; {hides} objects hidden from players in other zones.");
	}

	private static int ZoneOfEntry(VisibilityEntry entry)
	{
		// A schematic is never split: its blocks belong to the zone of its root.
		Vector3 position = entry.TryGetComponent(out MerBlockLink link) && link.Schematic != null
			? link.Schematic.transform.position
			: entry.transform.position;

		return ObjectZone(position);
	}

	private static int ObjectZone(Vector3 position)
	{
		switch (Mode)
		{
			case ZoneCullingMode.SurfaceFacility:
				return position.y >= SurfaceHeight ? SurfaceIndex : FacilityIndex;
			case ZoneCullingMode.PerZone:
				{
					FacilityZone zone = RoomZone(position);
					if (zone != FacilityZone.None)
						return (int)zone;

					return position.y >= SurfaceHeight ? (int)FacilityZone.Surface : -1;
				}

			default:
				return -1;
		}
	}

	private static FacilityZone RoomZone(Vector3 position)
	{
		RoomIdentifier room = RoomIdUtils.RoomAtPosition(position);
		return room != null ? room.Zone : FacilityZone.None;
	}

	private static bool ShouldSee(VisibilityEntry entry, VisibilityViewer viewer)
	{
		if (entry.Overrides != null && entry.Overrides.TryGetValue(viewer.ConnectionId, out bool forced))
			return forced;

		if (entry.IsAdminOnly)
			return entry.Audience != null && entry.Audience.Contains(viewer.Hub);

		VisibilityRoot? root = entry.Root;
		if (root == null || !root.Active || entry.Zone < 0)
			return true;

		return (viewer.Mask & (1 << entry.Zone)) != 0;
	}

	private static bool IsObserving(VisibilityEntry entry, VisibilityViewer viewer)
	{
		NetworkIdentity identity = entry.Identity;
		return identity != null && identity.observers.ContainsKey(viewer.ConnectionId);
	}

	private static bool IsSpawned(NetworkIdentity? identity) =>
		identity != null && identity.netId != 0 && NetworkServer.spawned.TryGetValue(identity.netId, out NetworkIdentity spawned) && ReferenceEquals(spawned, identity);

	private static bool Show(VisibilityEntry entry, VisibilityViewer viewer)
	{
		NetworkIdentity identity = entry.Identity;
		if (!IsSpawned(identity) || identity.observers.ContainsKey(viewer.ConnectionId))
			return false;

		identity.AddObserver(viewer.Connection);
		return true;
	}

	private static bool Hide(VisibilityEntry entry, VisibilityViewer viewer)
	{
		NetworkIdentity identity = entry.Identity;
		if (identity == null || !identity.observers.ContainsKey(viewer.ConnectionId))
			return false;

		identity.RemoveObserver(viewer.Connection);
		viewer.Connection.RemoveFromObserving(identity, false);
		return true;
	}

	private static void CountShow(NetworkConnectionToClient connection, bool stream)
	{
		if (stream)
			StreamShows++;
		else
			SpawnShows++;

		VisibilityViewer? viewer = FindViewer(connection.connectionId);
		if (viewer != null)
			viewer.TotalShows++;
	}

	private static void SetOverride(VisibilityEntry entry, int connectionId, bool show)
	{
		if (entry.Overrides == null)
		{
			entry.Overrides = [];
			OverrideEntries.Add(entry);
		}

		entry.Overrides[connectionId] = show;
	}

	private static void SetSchematic(SchematicObject schematic, Player player, bool show)
	{
		NetworkConnectionToClient? connection = player.ConnectionToClient;
		if (schematic == null || connection == null)
			return;

		VisibilityViewer? viewer = FindViewer(connection.connectionId);
		Vector3 focus = viewer != null ? PositionOf(viewer) : Vector3.zero;
		int queued = 0;
		foreach (NetworkIdentity identity in schematic.NetworkIdentities)
		{
			if (!identity.TryGetComponent(out VisibilityEntry entry) || entry.RegistryIndex < 0)
			{
				// Not managed (managed_visibility off): change Mirror's observers directly.
				if (show)
					ShowTo(identity, player);
				else
					HideFrom(identity, player);

				continue;
			}

			SetOverride(entry, connection.connectionId, show);
			if (viewer == null || !viewer.Started)
				continue;

			if (show && !IsObserving(entry, viewer))
			{
				viewer.Shows.Add(new StreamItem(entry, Key(entry, focus)));
				queued++;
			}
			else if (!show && IsObserving(entry, viewer))
			{
				viewer.Hides.Add(entry);
				queued++;
			}
		}

		if (viewer == null || queued == 0)
			return;

		viewer.ShowsUnsorted |= show;
		viewer.BeginSession("explicit");
		EnsurePump();
	}

	/// <summary>
	/// Re-decides one object for every player (after its zone, audience or override changed); changes are paced.
	/// </summary>
	private static void Reapply(VisibilityEntry entry)
	{
		foreach (VisibilityViewer viewer in Viewers)
			Reapply(entry, viewer);
	}

	private static void Reapply(VisibilityEntry entry, VisibilityViewer viewer)
	{
		if (!viewer.Started)
			return;

		bool should = ShouldSee(entry, viewer);
		bool observing = IsObserving(entry, viewer);
		if (should == observing)
			return;

		if (should)
		{
			viewer.Shows.Add(new StreamItem(entry, Key(entry, PositionOf(viewer))));
			viewer.ShowsUnsorted = true;
		}
		else
		{
			viewer.Hides.Add(entry);
		}

		viewer.BeginSession("change");
		EnsurePump();
	}

	private static void BindAudience(VisibilityEntry entry, VisibilityAudience? audience)
	{
		VisibilityAudience? current = entry.Audience;
		if (ReferenceEquals(current, audience))
			return;

		if (current != null)
		{
			List<VisibilityEntry> list = current.Entries;
			int index = entry.AudienceIndex;
			if (index >= 0 && index < list.Count && ReferenceEquals(list[index], entry))
			{
				int last = list.Count - 1;
				VisibilityEntry moved = list[last];
				list[index] = moved;
				moved.AudienceIndex = index;
				list.RemoveAt(last);
			}
		}

		entry.Audience = audience;
		entry.AudienceIndex = -1;
		if (audience == null)
			return;

		entry.AudienceIndex = audience.Entries.Count;
		audience.Entries.Add(entry);
	}

	private static float Key(VisibilityEntry entry, Vector3 focus) =>
		((int)entry.Priority * PriorityKeyStep) + (entry.transform.position - focus).sqrMagnitude;

	private static void SortShows(VisibilityViewer viewer)
	{
		if (!viewer.ShowsUnsorted)
			return;

		viewer.Shows.Sort(FarthestFirst);
		viewer.ShowsUnsorted = false;
	}

	/// <summary>
	/// Takes the next pending show of a player and sends it if it is still wanted.
	/// </summary>
	/// <returns>Whether a spawn message was sent.</returns>
	private static bool TrySendNextShow(VisibilityViewer viewer)
	{
		int last = viewer.Shows.Count - 1;
		VisibilityEntry entry = viewer.Shows[last].Entry;
		viewer.Shows.RemoveAt(last);

		if (entry.RegistryIndex < 0 || !ShouldSee(entry, viewer) || !Show(entry, viewer))
		{
			viewer.SessionSkipped++;
			return false;
		}

		viewer.CountSent(true);
		StreamShows++;
		return true;
	}

	// ---- Players ----

	private static VisibilityViewer? FindViewer(ReferenceHub? hub)
	{
		if (hub == null)
			return null;

		foreach (VisibilityViewer viewer in Viewers)
		{
			if (ReferenceEquals(viewer.Hub, hub))
				return viewer;
		}

		return null;
	}

	private static VisibilityViewer? FindViewer(int connectionId)
	{
		foreach (VisibilityViewer viewer in Viewers)
		{
			if (viewer.ConnectionId == connectionId)
				return viewer;
		}

		return null;
	}

	private static VisibilityViewer? AddViewer(ReferenceHub hub)
	{
		if (hub == null || hub.isLocalPlayer)
			return null;

		NetworkConnectionToClient connection = hub.connectionToClient;
		if (connection == null || connection is LocalConnectionToClient)
			return null;

		VisibilityViewer? existing = FindViewer(connection.connectionId);
		if (existing != null)
		{
			if (ReferenceEquals(existing.Hub, hub) && ReferenceEquals(existing.Connection, connection))
				return existing;

			RemoveViewer(existing);
		}

		VisibilityViewer viewer = new(hub, connection);
		Viewers.Add(viewer);
		TryStart(viewer);
		return viewer;
	}

	private static void RemoveViewer(VisibilityViewer viewer)
	{
		Viewers.Remove(viewer);
		if (viewer.SessionActive)
			EndSession(viewer, "dropped");

		viewer.ResetState();

		for (int i = Audiences.Count - 1; i >= 0; i--)
		{
			if (i < Audiences.Count)
				Audiences[i].Remove(viewer.Hub, true);
		}

		for (int i = OverrideEntries.Count - 1; i >= 0; i--)
		{
			VisibilityEntry entry = OverrideEntries[i];
			if (entry.Overrides == null || !entry.Overrides.Remove(viewer.ConnectionId) || entry.Overrides.Count > 0)
				continue;

			entry.Overrides = null;
			OverrideEntries.RemoveAt(i);
		}
	}

	/// <summary>
	/// Starts a ready player's join stream: every object it should see, collidable first, nearest first.
	/// </summary>
	private static void TryStart(VisibilityViewer viewer)
	{
		if (viewer.Started || !IsReady(viewer))
			return;

		viewer.Started = true;
		float now = Time.unscaledTime;
		EvaluateZones(viewer, now, false);

		Vector3 position = PositionOf(viewer);
		int queued = 0;
		foreach (VisibilityEntry entry in Entries)
		{
			if (!ShouldSee(entry, viewer) || IsObserving(entry, viewer))
				continue;

			// Waypoints at once, so the client numbers them as the server does from the first frame.
			if (entry.IsWaypoint)
			{
				if (Show(entry, viewer))
				{
					viewer.TotalShows++;
					StreamShows++;
				}

				continue;
			}

			Vector3 focus = entry.Zone >= 0 && entry.Zone != viewer.CurrentZone && (viewer.Mask & (1 << entry.Zone)) != 0 ? viewer.Focus[entry.Zone] : position;
			viewer.Shows.Add(new StreamItem(entry, Key(entry, focus)));
			queued++;
		}

		if (queued == 0)
			return;

		viewer.ShowsUnsorted = true;
		viewer.BeginSession("join");
		EnsurePump();
	}

	private static bool IsConnected(VisibilityViewer viewer) =>
		NetworkServer.connections.TryGetValue(viewer.ConnectionId, out NetworkConnectionToClient connection) && ReferenceEquals(connection, viewer.Connection);

	private static bool IsReady(VisibilityViewer viewer) => viewer.Connection.isReady && IsConnected(viewer);

	private static Vector3 PositionOf(VisibilityViewer viewer)
	{
		ReferenceHub hub = viewer.Hub;
		if (hub == null)
			return Vector3.zero;

		PlayerRoleBase role = hub.roleManager.CurrentRole;
		if (role is IFpcRole fpc)
			return fpc.FpcModule.Position;

		if (TryGetObservedPosition(role, out Vector3 position))
			return position;

		return hub.transform.position;
	}

	/// <summary>
	/// Gets the position a player without a body looks at: the spectated player, or SCP-079's camera.
	/// </summary>
	private static bool TryGetObservedPosition(PlayerRoleBase role, out Vector3 position)
	{
		position = default;
		if (role is SpectatorRole spectator)
		{
			if (spectator.SyncedSpectatedNetId == 0 || !ReferenceHub.TryGetHubNetID(spectator.SyncedSpectatedNetId, out ReferenceHub target) || target == null)
				return false;

			PlayerRoleBase targetRole = target.roleManager.CurrentRole;
			if (targetRole is IFpcRole targetFpc)
			{
				position = targetFpc.FpcModule.Position;
				return true;
			}

			// Spectating SCP-079 shows its camera.
			return targetRole is Scp079Role && TryGetObservedPosition(targetRole, out position);
		}

		if (role is Scp079Role scp079)
		{
			try
			{
				PlayerRoles.PlayableScps.Scp079.Cameras.Scp079Camera camera = scp079.CurrentCamera;
				if (camera != null)
				{
					position = camera.CameraPosition;
					return true;
				}
			}
			catch (Exception)
			{
				// The camera subroutine is not set up yet.
			}
		}

		return false;
	}

	private static void OnPlayerAdded(ReferenceHub hub)
	{
		if (Config == null)
			return;

		try
		{
			AddViewer(hub);
		}
		catch (Exception e)
		{
			Logger.Error($"Visibility: adding a player failed: {e}");
		}
	}

	private static void OnPlayerRemoved(ReferenceHub hub)
	{
		try
		{
			VisibilityViewer? viewer = FindViewer(hub);
			if (viewer != null)
			{
				RemoveViewer(viewer);
				return;
			}

			for (int i = Audiences.Count - 1; i >= 0; i--)
			{
				if (i < Audiences.Count)
					Audiences[i].Remove(hub, true);
			}
		}
		catch (Exception e)
		{
			Logger.Error($"Visibility: removing a player failed: {e}");
		}
	}

	private static void OnRoleChanged(ReferenceHub hub, PlayerRoleBase previous, PlayerRoleBase next)
	{
		VisibilityViewer? viewer = FindViewer(hub);
		if (viewer == null || Config == null)
			return;

		viewer.EvaluateAt = Time.unscaledTime + RoleChangeDelay;
		EnsurePump();
	}

	// ---- Zones ----

	/// <summary>
	/// Updates the zones a player sees from its position, role and elevator; queues the streams of added and removed zones.
	/// </summary>
	private static void EvaluateZones(VisibilityViewer viewer, float now, bool enqueue)
	{
		ZoneCullingMode mode = Mode;
		if (mode == ZoneCullingMode.None || viewer.Hub == null)
			return;

		int wanted = 0;
		bool addOnly = true;
		int current = -1;
		PlayerRoleBase role = viewer.Hub.roleManager.CurrentRole;
		if (role is IFpcRole fpc)
		{
			addOnly = false;
			Vector3 position = fpc.FpcModule.Position;
			current = ViewerZone(viewer, position, mode);
			if (current >= 0)
			{
				wanted |= 1 << current;
				viewer.Focus[current] = position;
			}

			wanted |= PrefetchZones(viewer, position, mode);
		}
		else if (TryGetObservedPosition(role, out Vector3 observed))
		{
			int zone = ObjectZone(observed);
			if (zone >= 0)
			{
				wanted |= 1 << zone;
				viewer.Focus[zone] = observed;
			}
		}

		viewer.AddOnly = addOnly;
		viewer.CurrentZone = current;
		int mask = viewer.Mask;
		int previous = viewer.PreviousWanted;
		viewer.PreviousWanted = wanted;
		int next = wanted;
		for (int zone = 0; zone < ZoneCount; zone++)
		{
			// A zone's hysteresis starts when it is first seen unwanted (the poll cannot tell the exact moment it was left).
			int bit = 1 << zone;
			if ((wanted & bit) != 0 || (addOnly && (mask & bit) != 0) || (previous & bit) != 0)
				viewer.LastWanted[zone] = now;

			if ((mask & bit) != 0 && now - viewer.LastWanted[zone] < Hysteresis)
				next |= bit;
		}

		if (next == mask)
			return;

		viewer.Mask = next;
		if (!enqueue)
			return;

		viewer.Transitions++;
		ZoneTransitions++;
		int added = next & ~mask;
		int removed = mask & ~next;
		for (int zone = 0; zone < ZoneCount; zone++)
		{
			int bit = 1 << zone;
			if ((added & bit) != 0)
				EnqueueZone(viewer, zone, true, "zone");
			else if ((removed & bit) != 0)
				EnqueueZone(viewer, zone, false, "zone");
		}

		LogTransition(viewer, mask, addOnly ? "spectating" : "moved");
	}

	private static int ViewerZone(VisibilityViewer viewer, Vector3 position, ZoneCullingMode mode)
	{
		if (mode == ZoneCullingMode.SurfaceFacility)
			return position.y >= SurfaceHeight ? SurfaceIndex : FacilityIndex;

		// Per zone: keep the last zone while the room lookup finds nothing (elevator shafts, gaps between rooms).
		FacilityZone zone = RoomZone(position);
		int index = zone != FacilityZone.None ? (int)zone : (position.y >= SurfaceHeight ? (int)FacilityZone.Surface : viewer.LastZone);
		viewer.LastZone = index;
		return index;
	}

	/// <summary>
	/// Gets the zones a player is about to enter: every zone served by an elevator it stands in, and with per-zone culling
	/// both sides of the HCZ-EZ checkpoint.
	/// </summary>
	private static int PrefetchZones(VisibilityViewer viewer, Vector3 position, ZoneCullingMode mode)
	{
		if (mode != _elevatorMaskMode)
		{
			ElevatorZoneMasks.Clear();
			_elevatorMaskMode = mode;
		}

		int zones = 0;
		foreach (KeyValuePair<ElevatorManager.ElevatorGroup, ElevatorChamber> pair in ElevatorManager.SpawnedChambers)
		{
			ElevatorChamber chamber = pair.Value;
			if (chamber == null)
				continue;

			int served = GetElevatorZones(pair.Key);
			if ((served & (served - 1)) == 0)
				continue; // one zone or none: not a zone transition

			Bounds bounds = chamber.WorldspaceBounds;
			bounds.Expand(1f);
			if (!bounds.Contains(position))
				continue;

			zones |= served;
			if (ElevatorDoor.AllElevatorDoors.TryGetValue(pair.Key, out List<ElevatorDoor> doors))
			{
				foreach (ElevatorDoor door in doors)
				{
					if (door == null)
						continue;

					Vector3 target = door.TargetPosition;
					int zone = ObjectZone(target);
					if (zone >= 0)
						viewer.Focus[zone] = target;
				}
			}
		}

		if (mode == ZoneCullingMode.PerZone)
		{
			RoomIdentifier room = RoomIdUtils.RoomAtPosition(position);
			if (room != null && room.Name == RoomName.HczCheckpointToEntranceZone)
			{
				zones |= (1 << (int)FacilityZone.HeavyContainment) | (1 << (int)FacilityZone.Entrance);
				viewer.Focus[(int)FacilityZone.Entrance] = position;
			}
		}

		return zones;
	}

	private static int GetElevatorZones(ElevatorManager.ElevatorGroup group)
	{
		if (ElevatorZoneMasks.TryGetValue(group, out int cached))
			return cached;

		if (!ElevatorDoor.AllElevatorDoors.TryGetValue(group, out List<ElevatorDoor> doors) || doors == null || doors.Count < 2)
			return 0;

		int mask = 0;
		foreach (ElevatorDoor door in doors)
		{
			if (door == null)
				return 0; // the level is not set up yet; try again later

			int zone = ObjectZone(door.TargetPosition);
			if (zone >= 0)
				mask |= 1 << zone;
		}

		ElevatorZoneMasks[group] = mask;
		return mask;
	}

	/// <summary>
	/// Queues the culled objects of one zone to be shown to (or hidden from) a player, nearest to the zone's focus first.
	/// </summary>
	private static void EnqueueZone(VisibilityViewer viewer, int zone, bool show, string reason)
	{
		Vector3 focus = viewer.Focus[zone];
		int queued = 0;
		foreach (VisibilityRoot root in Roots.Values)
		{
			if (!root.Active)
				continue;

			List<VisibilityEntry>? cell = root.Cells[zone];
			if (cell == null)
				continue;

			foreach (VisibilityEntry entry in cell)
			{
				if (show)
					viewer.Shows.Add(new StreamItem(entry, Key(entry, focus)));
				else
					viewer.Hides.Add(entry);
			}

			queued += cell.Count;
		}

		if (queued == 0)
			return;

		if (show)
			viewer.ShowsUnsorted = true;

		viewer.BeginSession(reason);
		EnsurePump();
	}

	private static void LogTransition(VisibilityViewer viewer, int previous, string reason)
	{
		if (Config?.LogSpawnStats != true || Roots.Count == 0)
			return;

		Logger.Info($"Zone culling: {viewer.Name} sees {ZonesToString(viewer.Mask)} (was {ZonesToString(previous)}; {reason}); {viewer.Shows.Count} shows and {viewer.Hides.Count - viewer.HideCursor} hides queued.");
	}

	private static string ZonesToString(int mask)
	{
		if (mask == 0)
			return "none";

		StringBuilder sb = new();
		for (int zone = 0; zone < ZoneCount; zone++)
		{
			if ((mask & (1 << zone)) == 0)
				continue;

			if (sb.Length > 0)
				sb.Append('+');

			sb.Append(GetZoneName(zone));
		}

		return sb.ToString();
	}

	// ---- Coroutines ----

	private static void EnsurePoll()
	{
		if (_pollRunning || Config == null)
			return;

		_pollRunning = true;
		_pollHandle = Timing.RunCoroutine(Poll());
	}

	private static void EnsurePump()
	{
		if (_pumpRunning || Config == null)
			return;

		_pumpRunning = true;
		_pumpHandle = Timing.RunCoroutine(Pump(), Segment.Update);
	}

	private static IEnumerator<float> Poll()
	{
		while (true)
		{
			yield return Timing.WaitForSeconds(PollInterval);
			if (Config == null)
				break;

			try
			{
				PollOnce();
			}
			catch (Exception e)
			{
				Logger.Error($"Visibility poll error: {e}");
			}

			if (Entries.Count == 0 && Audiences.Count == 0)
				break;
		}

		_pollRunning = false;
	}

	private static void PollOnce()
	{
		long start = Stopwatch.GetTimestamp();
		try
		{
			PollViewers();
		}
		finally
		{
			double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
			if (ms > WorstPollMs)
				WorstPollMs = ms;
		}
	}

	private static void PollViewers()
	{
		float now = Time.unscaledTime;

		// Safety net for players added before the plugin was enabled, or not ready when they were added.
		foreach (ReferenceHub hub in ReferenceHub.AllHubs)
		{
			if (hub == null || hub.isLocalPlayer)
				continue;

			NetworkConnectionToClient connection = hub.connectionToClient;
			if (connection == null || connection is LocalConnectionToClient)
				continue;

			VisibilityViewer? known = FindViewer(connection.connectionId);
			if (known == null || !ReferenceEquals(known.Hub, hub))
				AddViewer(hub);
		}

		for (int i = Viewers.Count - 1; i >= 0; i--)
		{
			if (i >= Viewers.Count)
				continue;

			VisibilityViewer viewer = Viewers[i];
			if (viewer.Hub == null || !IsConnected(viewer))
			{
				RemoveViewer(viewer);
				continue;
			}

			if (!viewer.Connection.isReady)
			{
				// Mirror cleared its observers (SetClientNotReady); it is streamed again once ready.
				if (viewer.Started)
				{
					if (viewer.SessionActive)
						EndSession(viewer, "not ready");

					viewer.ResetState();
				}

				continue;
			}

			if (!viewer.Started)
			{
				TryStart(viewer);
				continue;
			}

			EvaluateZones(viewer, now, true);
		}

		SettleRoots(now);
	}

	private static bool HasPumpWork()
	{
		foreach (VisibilityViewer viewer in Viewers)
		{
			if (viewer.HasWork || viewer.EvaluateAt >= 0f || viewer.SessionActive)
				return true;
		}

		return false;
	}

	private static IEnumerator<float> Pump()
	{
		while (true)
		{
			yield return Timing.WaitForOneFrame;
			if (Config == null)
				break;

			try
			{
				PumpFrame();
			}
			catch (Exception e)
			{
				Logger.Error($"Visibility stream error: {e}");
			}

			if (!HasPumpWork())
				break;
		}

		_pumpRunning = false;
	}

	private static void PumpFrame()
	{
		Config config = Config!;
		float now = Time.unscaledTime;
		long start = Stopwatch.GetTimestamp();

		foreach (VisibilityViewer viewer in Viewers)
		{
			if (viewer.EvaluateAt < 0f || now < viewer.EvaluateAt)
				continue;

			viewer.EvaluateAt = -1f;
			if (!viewer.Started)
				TryStart(viewer);
			else if (IsReady(viewer))
				EvaluateZones(viewer, now, true);
		}

		// Share the frame with the spawn queue: while it networks a load, streams use a quarter of the budget.
		bool busy = SpawnQueue.PendingSpawns > 0;
		int perViewer = Math.Max(1, config.SpawnMaxPerFrame);
		double budgetMs = Math.Max(0.1f, config.SpawnTimeBudgetMs);
		if (busy)
		{
			perViewer = Math.Max(1, perViewer / 4);
			budgetMs /= 4;
		}

		long budgetTicks = (long)(budgetMs * Stopwatch.Frequency / 1000.0);
		int hideLimit = perViewer * 4;

		// Hides first: they are small and free phone memory.
		foreach (VisibilityViewer viewer in Viewers)
		{
			if (viewer.HideCursor >= viewer.Hides.Count)
				continue;

			if (!viewer.Started || !IsReady(viewer))
			{
				viewer.Hides.Clear();
				viewer.HideCursor = 0;
				continue;
			}

			int sent = 0;
			while (sent < hideLimit && viewer.HideCursor < viewer.Hides.Count && Stopwatch.GetTimestamp() - start < budgetTicks)
			{
				VisibilityEntry entry = viewer.Hides[viewer.HideCursor++];
				if (entry != null && entry.RegistryIndex >= 0 && !ShouldSee(entry, viewer) && Hide(entry, viewer))
				{
					viewer.CountSent(false);
					HidesSent++;
					sent++;
				}
				else
				{
					viewer.SessionSkipped++;
				}
			}

			if (viewer.HideCursor >= viewer.Hides.Count)
			{
				viewer.Hides.Clear();
				viewer.HideCursor = 0;
			}
		}

		// Shows, round-robin so every player progresses within the time budget.
		int count = Viewers.Count;
		for (int i = 0; i < count; i++)
		{
			VisibilityViewer viewer = Viewers[i];
			viewer.FrameSent = 0;
			if (viewer.Shows.Count == 0)
				continue;

			if (!viewer.Started || !IsReady(viewer))
			{
				viewer.Shows.Clear();
				continue;
			}

			SortShows(viewer);
		}

		bool progress = count > 0;
		while (progress)
		{
			progress = false;
			for (int k = 0; k < count; k++)
			{
				VisibilityViewer viewer = Viewers[(_roundRobin + k) % count];
				if (viewer.FrameSent >= perViewer)
					continue;

				while (viewer.Shows.Count > 0)
				{
					if (Stopwatch.GetTimestamp() - start >= budgetTicks)
					{
						progress = false;
						goto Done;
					}

					if (TrySendNextShow(viewer))
					{
						viewer.FrameSent++;
						progress = true;
						break;
					}
				}
			}
		}

	Done:
		_roundRobin = count > 0 ? (_roundRobin + 1) % count : 0;
		double sliceMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
		PumpFrames++;
		if (sliceMs > WorstPumpMs)
			WorstPumpMs = sliceMs;

		foreach (VisibilityViewer viewer in Viewers)
		{
			if (viewer.SessionActive && !viewer.HasWork)
				EndSession(viewer, null);
		}
	}

	private static void EndSession(VisibilityViewer viewer, string? interrupted)
	{
		viewer.SessionActive = false;
		double seconds = (Stopwatch.GetTimestamp() - viewer.SessionStart) / (double)Stopwatch.Frequency;
		if (viewer.SessionShows == 0 && viewer.SessionHides == 0 && interrupted == null)
			return;

		LastStreamSummary = $"{viewer.Name} {viewer.SessionReason}: {viewer.SessionShows} shown, {viewer.SessionHides} hidden, {viewer.SessionSkipped} skipped in {seconds:F2} s " +
			$"over {viewer.SessionFrames} frames, max {viewer.SessionMaxPerFrame}/frame{(interrupted != null ? $" ({interrupted})" : string.Empty)}";

		if (Config?.LogSpawnStats == true)
			Logger.Info($"Visibility stream: {LastStreamSummary}.");
	}
}
