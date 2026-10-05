namespace ProjectMER.Features.Extensions;

using LabApi.Features.Wrappers;
using Mirror;
using Mobile;
using Objects;

/// <summary>
/// A set of useful extensions to easily interact with culling features.
/// </summary>
/// <remarks>
/// <para>
/// Reimplemented on <see cref="MerVisibility"/>: ProjectMER sent raw spawn/destroy messages, which left Mirror's
/// <c>observers</c>/<c>observing</c> bookkeeping out of sync (the destroyed object kept receiving updates, and a later
/// spawn for everyone skipped the player).
/// </para>
/// <para>
/// For MER objects the decision sticks: zone culling and join streaming no longer change the object for that player
/// until <see cref="ResetSchematicVisibility"/> or <see cref="ResetNetworkIdentityVisibility"/>. Showing a whole schematic is
/// streamed over several frames, nearest first.
/// </para>
/// </remarks>
public static class CullingExtensions
{
	/// <summary>
	/// Spawns the given <paramref name="schematic"/> for the specified <paramref name="player"/>.
	/// </summary>
	/// <param name="player">The target.</param>
	/// <param name="schematic">The schematic to spawn.</param>
	public static void SpawnSchematic(this Player player, SchematicObject schematic) => MerVisibility.ShowTo(schematic, player);

	/// <summary>
	/// Destroys the given <paramref name="schematic"/> for the specified <paramref name="player"/>.
	/// </summary>
	/// <param name="player">The target.</param>
	/// <param name="schematic">The schematic to destroy.</param>
	public static void DestroySchematic(this Player player, SchematicObject schematic) => MerVisibility.HideFrom(schematic, player);

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

	/// <summary>
	/// Returns the given <paramref name="schematic"/> to MER's own visibility rules for the specified <paramref name="player"/>.
	/// </summary>
	/// <param name="player">The target.</param>
	/// <param name="schematic">The schematic.</param>
	public static void ResetSchematicVisibility(this Player player, SchematicObject schematic) => MerVisibility.ClearOverrides(schematic, player);

	/// <summary>
	/// Returns the given <paramref name="networkIdentity"/> to MER's own visibility rules for the specified <paramref name="player"/>.
	/// </summary>
	/// <param name="player">The target.</param>
	/// <param name="networkIdentity">The network identity.</param>
	public static void ResetNetworkIdentityVisibility(this Player player, NetworkIdentity networkIdentity) =>
		MerVisibility.ClearOverride(networkIdentity, player);
}
