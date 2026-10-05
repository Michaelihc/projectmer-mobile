using LabApi.Features.Wrappers;
using Mirror;
using UnityEngine;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// Keeps the flattened networked blocks of an animated or physics schematic in step with its server-side anchors
/// (docs/projectmer-port-plan.md §3.2).
/// </summary>
/// <remarks>
/// Toys of such schematics are dynamic and their own server behaviour is disabled. Once per frame this copies the world
/// transform of every anchor whose <see cref="Transform.hasChanged"/> is set into its toy (position, rotation and
/// <see cref="Transform.lossyScale"/>). Animators and rigidbodies move the anchors: a block's rigidbody is on its anchor,
/// with the subtree's colliders on the anchors below it (see <c>SchematicObject.SetUpDynamic</c>).
/// </remarks>
[DisallowMultipleComponent]
public sealed class SchematicSync : MonoBehaviour
{
	/// <summary>
	/// Seconds a body is held at most (<see cref="HoldUntilCollidersOff"/>).
	/// </summary>
	private const float MaxHold = 10f;

	private readonly List<Link> _driven = [];

	private readonly List<ColliderOff> _collidersOff = [];

	private readonly List<Rigidbody> _heldBodies = [];

	private float _releaseDeadline;

	/// <summary>
	/// Gets the number of synchronized blocks.
	/// </summary>
	public int Count => _driven.Count;

	/// <summary>
	/// Adds a toy that follows an anchor.
	/// </summary>
	public void AddFollower(Transform anchor, AdminToy toy)
	{
		anchor.hasChanged = true;
		_driven.Add(new Link(anchor, toy));
	}

	/// <summary>
	/// Removes every link (the schematic switched back to static).
	/// </summary>
	public void Clear() => _driven.Clear();

	/// <summary>
	/// Keeps the server-side collider of a toy switched off: a block of a physics subtree, whose collider is a copy on its
	/// anchor (part of the body's compound collider). The server builds a toy's primitive object again after the toy
	/// started (when the spawn payload reaches the server's own host client, and on respawns), so it is checked every frame.
	/// </summary>
	internal void KeepServerColliderOff(AdminToys.PrimitiveObjectToy toy) => _collidersOff.Add(new ColliderOff(toy));

	/// <summary>
	/// Keeps a non-kinematic body kinematic until the server colliders of the toys that follow it are switched off for
	/// good: each toy builds its collider in <c>Start</c> and again when its spawn reaches the host client (a SyncVar hook),
	/// and a body overlapping them would be knocked away. Released after <see cref="MaxHold"/> seconds at the latest.
	/// </summary>
	internal void HoldUntilCollidersOff(Rigidbody body)
	{
		body.isKinematic = true;
		_heldBodies.Add(body);
		_releaseDeadline = Time.time + MaxHold;
	}

	private void LateUpdate()
	{
		for (int i = 0; i < _collidersOff.Count; i++)
		{
			ColliderOff entry = _collidersOff[i];
			GameObject primitive = entry.Toy._spawnedPrimitve;
			if (ReferenceEquals(primitive, entry.Handled))
				continue;

			entry.Handled = primitive;
			if (primitive != null)
				ToyFactory.DisableServerCollider(primitive);
		}

		if (_heldBodies.Count > 0 && (Time.time >= _releaseDeadline || CollidersSettled()))
		{
			foreach (Rigidbody body in _heldBodies)
			{
				if (body != null)
					body.isKinematic = false;
			}

			_heldBodies.Clear();
		}

		for (int i = 0; i < _driven.Count; i++)
		{
			Link link = _driven[i];
			Transform anchor = link.Anchor;
			if (anchor == null || link.Toy.IsDestroyed || !anchor.hasChanged)
				continue;

			anchor.hasChanged = false;
			anchor.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
			Vector3 scale = anchor.lossyScale;

			AdminToy toy = link.Toy;
			toy.Position = position;
			if (link.LastRotation != rotation)
			{
				link.LastRotation = rotation;
				toy.Rotation = rotation;
			}

			if (link.LastScale != scale)
			{
				link.LastScale = scale;
				toy.Scale = scale;
			}

			_driven[i] = link;
		}
	}

	/// <summary>
	/// Gets whether every toy whose collider is kept off has been spawned, also on the host client, and its current
	/// collider is off.
	/// </summary>
	private bool CollidersSettled()
	{
		bool host = NetworkClient.active;
		foreach (ColliderOff entry in _collidersOff)
		{
			AdminToys.PrimitiveObjectToy toy = entry.Toy;
			if (toy == null)
				continue;

			if (entry.Handled == null || toy.netId == 0 || (host && !NetworkClient.spawned.ContainsKey(toy.netId)))
				return false;
		}

		return true;
	}

	private sealed class ColliderOff(AdminToys.PrimitiveObjectToy toy)
	{
		public AdminToys.PrimitiveObjectToy Toy { get; } = toy;

		public GameObject? Handled { get; set; }
	}

	private struct Link(Transform anchor, AdminToy toy)
	{
		public readonly Transform Anchor = anchor;

		public readonly AdminToy Toy = toy;

		public Quaternion LastRotation = new(0f, 0f, 0f, 0f);

		public Vector3 LastScale = new(float.NaN, float.NaN, float.NaN);
	}
}
