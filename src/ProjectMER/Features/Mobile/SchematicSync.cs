using LabApi.Features.Wrappers;
using UnityEngine;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// Keeps the flattened networked blocks of an animated or physics schematic in step with its server-side anchors
/// (docs/projectmer-port-plan.md §3.2).
/// </summary>
/// <remarks>
/// Toys of such schematics are dynamic and their own server behaviour is disabled. Once per frame this copies the world
/// transform of every anchor whose <see cref="Transform.hasChanged"/> is set into its toy (position, rotation and
/// <see cref="Transform.lossyScale"/>). Blocks with a <see cref="Rigidbody"/> work the other way: physics moves the toy and
/// the toy moves its anchor, so the block's children follow.
/// </remarks>
[DisallowMultipleComponent]
public sealed class SchematicSync : MonoBehaviour
{
	private readonly List<Link> _driven = [];

	private readonly List<Link> _drivers = [];

	/// <summary>
	/// Gets the number of synchronized blocks.
	/// </summary>
	public int Count => _driven.Count + _drivers.Count;

	/// <summary>
	/// Adds a toy that follows an anchor.
	/// </summary>
	public void AddFollower(Transform anchor, AdminToy toy)
	{
		anchor.hasChanged = true;
		_driven.Add(new Link(anchor, toy));
	}

	/// <summary>
	/// Adds a physics toy that drives its anchor.
	/// </summary>
	public void AddDriver(Transform anchor, AdminToy toy) => _drivers.Add(new Link(anchor, toy));

	/// <summary>
	/// Removes every link (the schematic switched back to static).
	/// </summary>
	public void Clear()
	{
		_driven.Clear();
		_drivers.Clear();
	}

	private void LateUpdate()
	{
		for (int i = 0; i < _drivers.Count; i++)
		{
			Link link = _drivers[i];
			if (link.Toy.IsDestroyed || link.Anchor == null)
				continue;

			Transform toyTransform = link.Toy.Transform;
			if (!toyTransform.hasChanged)
				continue;

			toyTransform.hasChanged = false;
			link.Anchor.SetPositionAndRotation(toyTransform.position, link.Toy.Rotation);
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

	private struct Link(Transform anchor, AdminToy toy)
	{
		public readonly Transform Anchor = anchor;

		public readonly AdminToy Toy = toy;

		public Quaternion LastRotation = new(0f, 0f, 0f, 0f);

		public Vector3 LastScale = new(float.NaN, float.NaN, float.NaN);
	}
}
