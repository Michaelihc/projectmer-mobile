using AdminToys;
using LabApi.Features.Wrappers;
using Mirror;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.Objects;
using UnityEngine;
using PrimitiveObjectToy = LabApi.Features.Wrappers.PrimitiveObjectToy;

namespace ProjectMER.Features.ToolGun;

/// <summary>
/// The translucent box (α 0.25) around a player's selected object, visible only to that player
/// (docs/projectmer-port-plan.md §4). While the player grabs, the same box is the grab proxy.
/// </summary>
/// <remarks>
/// <para>
/// The box is one non-collidable static cube spawned through <see cref="MerVisibility.Spawn"/> with
/// <c>adminOnly: true</c>, then shown to its owner with <see cref="MerVisibility.ShowTo"/>; any other observer is removed
/// with <see cref="MerVisibility.HideFrom"/> (checked again every HUD tick, so the box stays private whatever the
/// visibility policy for admin-only objects is). It is moved by resending its spawn payload in place to that one player,
/// only when the selection or the selected object's transform changed. During a grab it becomes a dynamic toy that the
/// grab moves through SyncVars, and it is resent at full precision when the grab ends.
/// </para>
/// <para>
/// The box has no collider (Visible only), is not part of any map, and is ignored by the editing triggers, so the tool
/// gun never selects it.
/// </para>
/// </remarks>
internal sealed class SelectionBox
{
	/// <summary>
	/// Color of the selection box.
	/// </summary>
	public static readonly Color SelectColor = new(0.25f, 0.75f, 1f, 0.25f);

	/// <summary>
	/// Color of the box while grabbing.
	/// </summary>
	public static readonly Color GrabColor = new(1f, 0.8f, 0.2f, 0.3f);

	private const float Padding = 0.06f;

	private static readonly List<Player> Others = [];

	private PrimitiveObjectToy? _box;

	private MapEditorObject? _target;

	private Vector3 _targetPosition;

	private Quaternion _targetRotation;

	private Vector3 _targetScale;

	private bool _grabbing;

	private Vector3 _localCenter;

	private Vector3 _localSize;

	private float _recomputeAt;

	private bool _forcePlace;

	/// <summary>
	/// Gets the box toy, if it exists.
	/// </summary>
	public PrimitiveObjectToy? Toy => _box != null && !_box.IsDestroyed ? _box : null;

	/// <summary>
	/// Gets the world center of the box.
	/// </summary>
	public Vector3 Center { get; private set; }

	/// <summary>
	/// Gets the box rotation.
	/// </summary>
	public Quaternion Rotation { get; private set; } = Quaternion.identity;

	/// <summary>
	/// Gets the box size.
	/// </summary>
	public Vector3 Size { get; private set; }

	/// <summary>
	/// Gets the number of in-place updates (resends) of boxes since the plugin was enabled.
	/// </summary>
	public static int Updates { get; private set; }

	/// <summary>
	/// Gets the number of boxes spawned since the plugin was enabled.
	/// </summary>
	public static int Spawns { get; private set; }

	/// <summary>
	/// Gets the number of bounds computations from an object's parts.
	/// </summary>
	public static int BoundsComputations { get; private set; }

	/// <summary>
	/// Shows, moves or removes the box to match the player's selection.
	/// </summary>
	/// <remarks>
	/// The bounds are computed from the object's parts when the selection or the object's scale changes, and kept relative
	/// to the object's root; a moved or rotated object only moves the box. Schematic blocks follow a scaled root up to
	/// 0.25 s later, so the bounds are computed again shortly after a scale change.
	/// </remarks>
	public void Sync(ToolGunState state)
	{
		if (_grabbing)
		{
			KeepPrivate(state.Player);
			return;
		}

		MapEditorObject? target = state.Selected;
		if (target == null)
		{
			Destroy();
			return;
		}

		float now = Time.realtimeSinceStartup;
		Transform transform = target.transform;
		transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
		Vector3 scale = transform.lossyScale;
		bool sameTarget = Toy != null && ReferenceEquals(target, _target);
		bool moved = position != _targetPosition || rotation != _targetRotation;
		bool scaled = scale != _targetScale;
		bool recompute = _recomputeAt > 0f && now >= _recomputeAt;
		if (sameTarget && !moved && !scaled && !recompute && !_forcePlace)
		{
			KeepPrivate(state.Player);
			return;
		}

		if (!sameTarget || scaled || recompute)
		{
			bool complete = ObjectBounds.Get(target, out Vector3 center, out Quaternion boxRotation, out Vector3 size);
			_localCenter = Quaternion.Inverse(boxRotation) * (center - position);
			_localSize = size;
			BoundsComputations++;

			// A schematic that is still building is measured again until its blocks exist.
			_recomputeAt = !complete ? now + 0.5f : sameTarget && scaled ? now + 0.4f : 0f;
		}

		_forcePlace = false;
		_target = target;
		_targetPosition = position;
		_targetRotation = rotation;
		_targetScale = scale;
		Place(state.Player, position + (rotation * _localCenter), rotation, _localSize, SelectColor);
	}

	/// <summary>
	/// Gets the box center for an object root at a position and rotation (the grab proxy follows the root).
	/// </summary>
	public Vector3 CenterFor(Vector3 rootPosition, Quaternion rootRotation) => rootPosition + (rootRotation * _localCenter);

	/// <summary>
	/// Turns the box into a dynamic grab proxy.
	/// </summary>
	public void BeginGrab(ToolGunState state)
	{
		// Make sure the box exists and matches the selection.
		Sync(state);
		_grabbing = true;
		if (Toy == null)
			return;

		_box!.IsStatic = false;
		_box.Base.NetworkMovementSmoothing = ToyFactory.DynamicMovementSmoothing;
		_box.Color = GrabColor;
	}

	/// <summary>
	/// Moves the grab proxy (a SyncVar change to its one observer).
	/// </summary>
	public void MoveGrab(Vector3 center)
	{
		Center = center;
		if (Toy != null)
			_box!.Position = center;
	}

	/// <summary>
	/// Ends the grab: the box becomes static again and is resent at full precision on the next sync.
	/// </summary>
	public void EndGrab(ToolGunState state)
	{
		_grabbing = false;
		if (Toy != null)
		{
			_box!.Base.NetworkMovementSmoothing = 0;
			_box.IsStatic = true;
		}

		// Resend in place at full precision. The bounds stay relative to the root: after a proxy grab the schematic's
		// blocks follow the root up to 0.25 s later, so recomputing them now would use their old positions.
		_forcePlace = true;
		Sync(state);
	}

	/// <summary>
	/// Destroys the box.
	/// </summary>
	public void Destroy()
	{
		if (Toy != null)
			SpawnQueue.Destroy(_box!.GameObject);

		_box = null;
		_target = null;
		_grabbing = false;
		_recomputeAt = 0f;
		_forcePlace = false;
	}

	/// <summary>
	/// Forgets the box without destroying it (round restart: the scene change destroyed it).
	/// </summary>
	public void Forget()
	{
		_box = null;
		_target = null;
		_grabbing = false;
	}

	private void Place(Player owner, Vector3 center, Quaternion rotation, Vector3 size, Color color)
	{
		size = new Vector3(Mathf.Max(0.1f, (size.x * 1.02f) + Padding), Mathf.Max(0.1f, (size.y * 1.02f) + Padding), Mathf.Max(0.1f, (size.z * 1.02f) + Padding));
		Center = center;
		Rotation = rotation;
		Size = size;

		if (Toy != null)
		{
			// A static toy: this resends the spawn payload in place to its observers (the owner).
			ToyFactory.UpdatePrimitive(_box!, center, rotation, size, PrimitiveType.Cube, color, PrimitiveFlags.Visible);
			Updates++;
			KeepPrivate(owner);
			return;
		}

		_box = ToyFactory.CreatePrimitive(center, rotation, size, PrimitiveType.Cube, color, PrimitiveFlags.Visible, true, null, queue: false, MerObjectKind.Indicator);
		if (_box == null)
			return;

		_box.GameObject.name = "MER selection box";
		NetworkIdentity identity = _box.Base.netIdentity;
		MerVisibility.Spawn(identity, null, MerObjectKind.Indicator, adminOnly: true);
		SpawnQueue.DisableWhenReady(_box.Base);
		Spawns++;
		KeepPrivate(owner);
	}

	/// <summary>
	/// Makes the owner the box's only remote observer (the server's own host connection may observe it too).
	/// </summary>
	private void KeepPrivate(Player owner)
	{
		if (Toy == null)
			return;

		NetworkIdentity identity = _box!.Base.netIdentity;
		if (identity.netId == 0)
			return;

		NetworkConnectionToClient? connection = owner.ConnectionToClient;
		if (connection != null && connection.isReady && !identity.observers.ContainsKey(connection.connectionId))
			MerVisibility.ShowTo(identity, owner);

		Others.Clear();
		foreach (NetworkConnectionToClient observer in identity.observers.Values)
		{
			if (observer == null || ReferenceEquals(observer, connection) || ReferenceEquals(observer, NetworkServer.localConnection) || observer.identity == null)
				continue;

			Player? other = Player.Get(observer.identity.gameObject);
			if (other != null)
				Others.Add(other);
		}

		foreach (Player other in Others)
			MerVisibility.HideFrom(identity, other);

		Others.Clear();
	}
}
