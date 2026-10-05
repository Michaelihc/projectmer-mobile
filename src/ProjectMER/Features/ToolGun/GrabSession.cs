using AdminToys;
using LabApi.Features.Wrappers;
using MapGeneration;
using MapGeneration.Distributors;
using MEC;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.Objects;
using UnityEngine;

namespace ProjectMER.Features.ToolGun;

/// <summary>
/// Moves a selected object in front of a player (<c>mp position grab</c>, docs/projectmer-port-plan.md §4).
/// </summary>
/// <remarks>
/// <para>
/// Every 0.1 s the grabbed object's origin is placed on the player's view ray at the distance it had when the grab began.
/// What moves depends on the object:
/// </para>
/// <list type="bullet">
/// <item><b>Toys</b> (primitives, lights, shooting targets): a static toy switches to dynamic for the grab, so each move is
/// a small SyncVar delta that clients interpolate; it switches back to static on release and is resent in place at full
/// precision.</item>
/// <item><b>Schematics with at most <see cref="ProxyThreshold"/> networked blocks</b>: the same switch for the whole
/// schematic (<see cref="SchematicObject.IsStatic"/>); its blocks follow the root through <c>SchematicSync</c>.</item>
/// <item><b>Structures</b> (workstations, lockers): moved and resent in place every step.</item>
/// <item><b>Everything else</b> (larger schematics, doors, which clients only place when they spawn, and server-only objects
/// such as teleports and spawnpoints): only the player's selection box moves, as a proxy visible to that player alone.
/// The object is moved once, on release, and its blocks are resent in place (a door is respawned once).</item>
/// </list>
/// <para>
/// Release (the command again, a new selection, the object's destruction, the player's death or leaving) writes the position to
/// the object's data relative to its room, as ProjectMER does, and updates every copy.
/// </para>
/// </remarks>
internal sealed class GrabSession
{
	/// <summary>
	/// Schematics with more networked blocks than this move a proxy box while grabbed.
	/// </summary>
	public const int ProxyThreshold = 50;

	private const float StepInterval = 0.1f;

	private readonly ToolGunState _state;

	private readonly GrabKind _kind;

	private readonly float _distance;

	private CoroutineHandle _handle;

	private Vector3 _position;

	private bool _restoreStatic;

	private byte _smoothing;

	private bool _ended;

	private GrabSession(ToolGunState state, MapEditorObject target, GrabKind kind, float distance)
	{
		_state = state;
		Target = target;
		_kind = kind;
		_distance = distance;
		_position = target.transform.position;
	}

	private enum GrabKind
	{
		Toy,
		Schematic,
		Structure,
		Proxy,
	}

	/// <summary>
	/// Gets the grabbed object.
	/// </summary>
	public MapEditorObject Target { get; }

	/// <summary>
	/// Gets whether only the proxy box moves until release.
	/// </summary>
	public bool UsesProxy => _kind == GrabKind.Proxy;

	/// <summary>
	/// Gets the number of steps that moved something, since the plugin was enabled.
	/// </summary>
	public static int Moves { get; private set; }

	/// <summary>
	/// Gets a short description of what the grab moves.
	/// </summary>
	public string Description => _kind switch
	{
		GrabKind.Toy => "the toy (dynamic while grabbed)",
		GrabKind.Schematic => "the schematic (dynamic while grabbed)",
		GrabKind.Structure => "the structure",
		_ => "a proxy box (the object moves on release)",
	};

	/// <summary>
	/// Starts or ends a player's grab.
	/// </summary>
	/// <returns>Whether the command succeeded.</returns>
	public static bool Toggle(Player player, out string response)
	{
		ToolGunState state = ToolGunState.Get(player);
		if (state.Grab != null)
		{
			MapEditorObject target = state.Grab.Target;
			state.Grab.End(commit: true);
			response = target != null ? $"Released {target.Id} at {target.transform.position:F2}." : "Released.";
			return true;
		}

		if (!ToolGunHandler.TryGetSelectedMapObject(player, out MapEditorObject selected))
		{
			response = "You need to select an object first!";
			return false;
		}

		if (player.Camera == null)
		{
			response = "You need a role with a camera to grab.";
			return false;
		}

		GrabSession session = Start(state, selected);
		response = $"Grabbed {selected.Id}: moving {session.Description}. Run the command again to drop it.";
		return true;
	}

	/// <summary>
	/// Starts a grab of a map object.
	/// </summary>
	public static GrabSession Start(ToolGunState state, MapEditorObject target)
	{
		Transform camera = state.Player.Camera;
		GrabKind kind = Classify(target);
		GrabSession session = new(state, target, kind, Vector3.Distance(camera.position, target.transform.position));
		state.Grab = session;

		switch (kind)
		{
			case GrabKind.Toy:
				{
					AdminToyBase toyBase = target.GetComponent<AdminToyBase>();
					AdminToy? toy = AdminToy.Get(toyBase);
					if (toy != null && toy.IsStatic)
					{
						session._smoothing = toyBase.MovementSmoothing;
						toy.IsStatic = false;
						toyBase.NetworkMovementSmoothing = ToyFactory.DynamicMovementSmoothing;
						session._restoreStatic = true;
						SetBudgetStatic(target.gameObject, false);
					}

					break;
				}

			case GrabKind.Schematic:
				{
					SchematicObject schematic = target.GetComponent<SchematicObject>();
					if (schematic.IsStatic)
					{
						schematic.IsStatic = false;
						session._restoreStatic = true;
					}

					break;
				}
		}

		state.Box.BeginGrab(state);
		session._handle = Timing.RunCoroutine(session.Run());
		ToolGunLoop.EnsureRunning();
		ToolGunHud.Refresh(state);
		return session;
	}

	/// <summary>
	/// Ends the grab.
	/// </summary>
	/// <param name="commit">Whether to write the new position to the object's data and update its copies.</param>
	public void End(bool commit)
	{
		if (_ended)
			return;

		_ended = true;
		Timing.KillCoroutines(_handle);
		if (ReferenceEquals(_state.Grab, this))
			_state.Grab = null;

		MapEditorObject target = Target;
		if (target != null)
		{
			switch (_kind)
			{
				case GrabKind.Toy when _restoreStatic:
					{
						// Static first, so the commit's update resends the spawn payload in place at full precision.
						AdminToyBase toyBase = target.GetComponent<AdminToyBase>();
						AdminToy? toy = AdminToy.Get(toyBase);
						if (toy != null)
						{
							toyBase.NetworkMovementSmoothing = _smoothing;
							toy.IsStatic = true;
							SetBudgetStatic(target.gameObject, true);
						}

						if (commit)
							Commit(target, _position);

						break;
					}

				case GrabKind.Schematic when _restoreStatic:
					// Commit while the blocks still follow the root (no resync is scheduled for followers); switching back
					// to static then resends every block once.
					if (commit)
						Commit(target, _position);

					target.GetComponent<SchematicObject>().IsStatic = true;
					break;

				default:
					if (commit)
						Commit(target, _position);

					break;
			}
		}

		_state.Box.EndGrab(_state);
		ToolGunHud.Refresh(_state);
	}

	/// <summary>
	/// Stops the coroutine without touching objects (round restart: the scene change destroyed them).
	/// </summary>
	public void Abandon()
	{
		_ended = true;
		Timing.KillCoroutines(_handle);
	}

	private static GrabKind Classify(MapEditorObject target)
	{
		if (target.TryGetComponent(out SchematicObject schematic))
			return schematic.NetworkedCount > ProxyThreshold ? GrabKind.Proxy : GrabKind.Schematic;

		if (target.TryGetComponent(out AdminToyBase _))
			return GrabKind.Toy;

		if (target.TryGetComponent(out StructurePositionSync _))
			return GrabKind.Structure;

		return GrabKind.Proxy;
	}

	private static void Commit(MapEditorObject target, Vector3 position)
	{
		Room room = target.Room;
		target.Base.Position = room == null || room.Name == RoomName.Outside ? position : room.Transform.InverseTransformPoint(position);
		target.UpdateObjectAndCopies();
	}

	private static void SetBudgetStatic(GameObject gameObject, bool isStatic)
	{
		if (gameObject.TryGetComponent(out MerBlockLink link))
			Budget.Update(link, isStatic, link.IsTransparent);
	}

	private IEnumerator<float> Run()
	{
		while (true)
		{
			yield return Timing.WaitForSeconds(StepInterval);

			// A new selection, the object's destruction or the player's death drops the object where it is.
			MapEditorObject target = Target;
			if (target == null || !ReferenceEquals(_state.Selected, target) || !_state.Player.IsAlive)
				break;

			Transform camera = _state.Player.Camera;
			if (camera == null)
				continue;

			Vector3 position = camera.position + (camera.forward * _distance);
			if ((position - _position).sqrMagnitude < 0.000001f)
				continue;

			_position = position;
			Moves++;
			try
			{
				Move(target, position);
			}
			catch (Exception e)
			{
				Logger.Error($"Grab move failed: {e}");
			}
		}

		// Drop it where it is.
		End(commit: Target != null);
	}

	private void Move(MapEditorObject target, Vector3 position)
	{
		switch (_kind)
		{
			case GrabKind.Toy:
				{
					AdminToy? toy = AdminToy.Get(target.GetComponent<AdminToyBase>());
					if (toy != null)
					{
						toy.Position = position;
						if (toy.IsStatic && ToyFactory.IsSpawned(toy.Base.netIdentity))
							ToyFactory.Resend(toy.Base.netIdentity);
					}

					break;
				}

			case GrabKind.Schematic:
				target.GetComponent<SchematicObject>().Position = position;
				break;

			case GrabKind.Structure:
				ToyFactory.MoveTo(target.gameObject, position);
				break;
		}

		_state.Box.MoveGrab(_state.Box.CenterFor(position, target.transform.rotation));
	}
}
