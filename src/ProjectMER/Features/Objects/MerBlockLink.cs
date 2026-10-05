using Mirror;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Mobile;
using UnityEngine;

namespace ProjectMER.Features.Objects;

/// <summary>
/// Marks a networked object spawned by MER and links it back to what owns it.
/// </summary>
/// <remarks>
/// Networked objects never have a server-side parent at spawn time (Carl Mod clients have no toy parenting), so
/// schematic blocks are no longer children of their <see cref="SchematicObject"/>. Tool-gun raycasts and pickup events
/// resolve their owner through this component instead of <c>TryGetComponentInParent</c>.
/// </remarks>
[DisallowMultipleComponent]
public sealed class MerBlockLink : MonoBehaviour
{
	/// <summary>
	/// Gets the map object that owns this object directly (standalone map objects), if any.
	/// </summary>
	public MapEditorObject? Owner { get; internal set; }

	/// <summary>
	/// Gets the schematic this object is a block of, if any.
	/// </summary>
	public SchematicObject? Schematic { get; internal set; }

	/// <summary>
	/// Gets the schematic block id, or -1.
	/// </summary>
	public int BlockId { get; internal set; } = -1;

	/// <summary>
	/// Gets the object's network identity.
	/// </summary>
	public NetworkIdentity Identity { get; internal set; }

	/// <summary>
	/// Gets the kind of object, for budgets.
	/// </summary>
	public MerObjectKind Kind { get; internal set; }

	/// <summary>
	/// Gets the spawn group the object was queued with.
	/// </summary>
	public SpawnGroup? Group { get; internal set; }

	/// <summary>
	/// Gets whether the object was spawned as a static toy.
	/// </summary>
	public bool IsStatic { get; internal set; }

	/// <summary>
	/// Gets whether the object is a transparent primitive (alpha below 1, which includes invisible colliders).
	/// </summary>
	public bool IsTransparent { get; internal set; }

	/// <summary>
	/// Gets the map object that owns this object, directly or through its schematic.
	/// </summary>
	public MapEditorObject? ResolveOwner()
	{
		if (Owner != null)
			return Owner;

		return Schematic != null ? Schematic.MapEditorObject : null;
	}

	/// <summary>
	/// Gets whether a destroy was requested; the spawn queue skips such objects.
	/// </summary>
	internal bool DestroyRequested { get; set; }

	/// <summary>
	/// Gets whether <see cref="Budget"/> counts this object.
	/// </summary>
	internal bool Counted { get; set; }

	/// <summary>
	/// Gets the <see cref="Budget"/> generation the object was counted in.
	/// </summary>
	internal int BudgetGeneration { get; set; }

	private void OnDestroy() => Budget.Unregister(this);
}
