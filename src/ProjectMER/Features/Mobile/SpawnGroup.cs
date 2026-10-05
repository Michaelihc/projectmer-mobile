using UnityEngine;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// A set of objects queued together: one map load or one schematic.
/// </summary>
/// <remarks>
/// Unloading cancels the group, so its still-queued entries are dropped before any destroy is sent.
/// <see cref="MerVisibility"/> uses groups (and their <see cref="Parent"/>) as the unit of zone culling.
/// </remarks>
public sealed class SpawnGroup
{
	/// <summary>
	/// Initializes a new instance of the <see cref="SpawnGroup"/> class.
	/// </summary>
	/// <param name="name">A name for logs, such as <c>map Facility</c>.</param>
	/// <param name="parent">The enclosing group (the map of a schematic), if any.</param>
	public SpawnGroup(string name, SpawnGroup? parent = null)
	{
		Name = name;
		Parent = parent;
	}

	/// <summary>
	/// Gets the group of the map whose objects are being spawned right now (set by <c>MapSchematic</c> around
	/// <c>SerializableObject.SpawnOrUpdateObject</c>), or <see langword="null"/>.
	/// </summary>
	public static SpawnGroup? Current { get; internal set; }

	/// <summary>
	/// Gets the name used in logs.
	/// </summary>
	public string Name { get; }

	/// <summary>
	/// Gets the enclosing group, if any.
	/// </summary>
	public SpawnGroup? Parent { get; }

	/// <summary>
	/// Gets whether the group was cancelled (its owner was unloaded or destroyed).
	/// </summary>
	public bool Cancelled { get; private set; }

	/// <summary>
	/// Gets the number of entries waiting in the queue.
	/// </summary>
	public int Pending { get; internal set; }

	/// <summary>
	/// Gets the number of objects spawned so far.
	/// </summary>
	public int Spawned { get; internal set; }

	/// <summary>
	/// Gets or sets a world position representative of the group (for culling decisions).
	/// </summary>
	public Vector3 Anchor { get; set; }

	/// <summary>
	/// Raised once when the last pending entry of the group has been spawned. Not raised for cancelled groups.
	/// </summary>
	public event Action<SpawnGroup>? Completed;

	/// <summary>
	/// Gets whether the group or one of its parents was cancelled.
	/// </summary>
	public bool IsCancelled
	{
		get
		{
			for (SpawnGroup? group = this; group != null; group = group.Parent)
			{
				if (group.Cancelled)
					return true;
			}

			return false;
		}
	}

	/// <summary>
	/// Cancels the group. Queued entries are dropped by the queue.
	/// </summary>
	public void Cancel()
	{
		Cancelled = true;
		Completed = null;
	}

	/// <summary>
	/// Raises <see cref="Completed"/> when nothing is pending. Called after a spawn and when a load finished queuing.
	/// </summary>
	internal void CheckCompleted()
	{
		if (Pending != 0 || Cancelled)
			return;

		Action<SpawnGroup>? completed = Completed;
		Completed = null;
		completed?.Invoke(this);
	}

	/// <inheritdoc />
	public override string ToString() => Name;
}
