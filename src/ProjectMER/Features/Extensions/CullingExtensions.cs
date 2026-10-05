namespace ProjectMER.Features.Extensions;

using LabApi.Features.Wrappers;
using Mirror;
using Mobile;
using Objects;

/// <summary>
/// A set of useful extensions to easily interact with culling features.
/// </summary>
/// <remarks>
/// Reimplemented on <see cref="MerVisibility"/>: ProjectMER sent raw spawn/destroy messages, which left Mirror's
/// <c>observers</c>/<c>observing</c> bookkeeping out of sync (the destroyed object kept receiving updates, and a later
/// spawn for everyone skipped the player).
/// </remarks>
public static class CullingExtensions
{
	/// <summary>
	/// Spawns the given <paramref name="schematic"/> for the specified <paramref name="player"/>.
	/// </summary>
	/// <param name="player">The target.</param>
	/// <param name="schematic">The schematic to spawn.</param>
	public static void SpawnSchematic(this Player player, SchematicObject schematic)
	{
		foreach (NetworkIdentity networkIdentity in schematic.NetworkIdentities)
			player.SpawnNetworkIdentity(networkIdentity);
	}

	/// <summary>
	/// Destroys the given <paramref name="schematic"/> for the specified <paramref name="player"/>.
	/// </summary>
	/// <param name="player">The target.</param>
	/// <param name="schematic">The schematic to destroy.</param>
	public static void DestroySchematic(this Player player, SchematicObject schematic)
	{
		foreach (NetworkIdentity networkIdentity in schematic.NetworkIdentities)
			player.DestroyNetworkIdentity(networkIdentity);
	}

	/// <summary>
	/// Spawns the given <paramref name="networkIdentity"/> for the specified <paramref name="player"/>.
	/// </summary>
	/// <param name="player">The target.</param>
	/// <param name="networkIdentity">The network identity to spawn.</param>
	public static void SpawnNetworkIdentity(this Player player, NetworkIdentity networkIdentity) =>
		MerVisibility.ShowTo(networkIdentity, player);

	/// <summary>
	/// Destroys the given <paramref name="networkIdentity"/> for the specified <paramref name="player"/>.
	/// </summary>
	/// <param name="player">The target.</param>
	/// <param name="networkIdentity">The network identity to destroy.</param>
	public static void DestroyNetworkIdentity(this Player player, NetworkIdentity networkIdentity) =>
		MerVisibility.HideFrom(networkIdentity, player);
}
