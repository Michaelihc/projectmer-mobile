using AdminToys;
using LabApi.Features.Wrappers;
using MapGeneration.Distributors;
using Mirror;
using ProjectMER.Configs;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Objects;
using UnityEngine;
using BaseLightSourceToy = AdminToys.LightSourceToy;
using BasePrimitiveObjectToy = AdminToys.PrimitiveObjectToy;
using LightSourceToy = LabApi.Features.Wrappers.LightSourceToy;
using PrimitiveObjectToy = LabApi.Features.Wrappers.PrimitiveObjectToy;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// Creates, edits and resends MER networked objects (docs/projectmer-port-plan.md §3.2-§3.4).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Every object is created at root level with its world transform: Carl Mod clients have no toy parenting.</item>
/// <item>Primitive flags use the encoding of the LabAPI <see cref="PrimitiveObjectToy"/> wrapper (§3.3).
/// <see cref="PrimitiveFlags.None"/> is not networked, and <see cref="PrimitiveFlags.Collidable"/> alone follows
/// <c>invisible_collider_mode</c>.</item>
/// <item>New objects are queued in <see cref="SpawnQueue"/>; the static toy and light behaviours are disabled once spawned.</item>
/// <item>Edits of spawned static objects resend the spawn payload in place (<see cref="Resend"/>); only a collider change
/// needs a respawn.</item>
/// </list>
/// </remarks>
public static class ToyFactory
{
	/// <summary>
	/// The movement smoothing ProjectMER gives dynamic toys.
	/// </summary>
	public const byte DynamicMovementSmoothing = 60;

	private static Config Config => ProjectMER.Singleton.Config!;

	/// <summary>
	/// Gets whether a primitive with these flags is networked (without reporting anything).
	/// </summary>
	public static bool IsNetworked(PrimitiveFlags flags)
	{
		flags &= PrimitiveFlags.Visible | PrimitiveFlags.Collidable;
		return flags != PrimitiveFlags.None && (flags != PrimitiveFlags.Collidable || Config.InvisibleColliderMode != InvisibleColliderMode.Skip);
	}

	/// <summary>
	/// Applies the port's flag policy.
	/// </summary>
	/// <param name="flags">The requested flags.</param>
	/// <param name="networkedFlags">The flags to give the toy.</param>
	/// <returns>Whether a toy is networked at all.</returns>
	public static bool TryGetNetworkedFlags(PrimitiveFlags flags, out PrimitiveFlags networkedFlags)
	{
		networkedFlags = flags & (PrimitiveFlags.Visible | PrimitiveFlags.Collidable);
		if (networkedFlags == PrimitiveFlags.None)
			return false;

		if (networkedFlags == PrimitiveFlags.Collidable && Config.InvisibleColliderMode == InvisibleColliderMode.Skip)
		{
			UnsupportedContent.Skip("invisible colliders", "invisible_collider_mode is Skip");
			return false;
		}

		return true;
	}

	/// <summary>
	/// Creates a primitive toy with its world transform and queues it for spawning.
	/// </summary>
	/// <param name="position">World position.</param>
	/// <param name="rotation">World rotation.</param>
	/// <param name="scale">World scale; negative components are kept.</param>
	/// <param name="type">Primitive type.</param>
	/// <param name="color">Color.</param>
	/// <param name="flags">Requested flags.</param>
	/// <param name="isStatic">Whether the toy is static.</param>
	/// <param name="group">The spawn group.</param>
	/// <param name="queue">Whether to queue the spawn now.</param>
	/// <param name="kind">The kind counted in the budget.</param>
	/// <returns>The wrapper, or <see langword="null"/> when nothing is networked (flags or hard cap).</returns>
	public static PrimitiveObjectToy? CreatePrimitive(Vector3 position, Quaternion rotation, Vector3 scale, PrimitiveType type, Color color, PrimitiveFlags flags, bool isStatic, SpawnGroup? group, bool queue = true, MerObjectKind kind = MerObjectKind.Primitive)
	{
		if (!TryGetNetworkedFlags(flags, out PrimitiveFlags networkedFlags) || !Budget.CanCreate())
			return null;

		PrimitiveObjectToy toy = PrimitiveObjectToy.Create(position, rotation, scale, null, networkSpawn: false);
		toy.IsStatic = isStatic;
		toy.Flags = networkedFlags;
		toy.Type = type;
		toy.Color = color;
		if (!isStatic)
			toy.Base.NetworkMovementSmoothing = DynamicMovementSmoothing;

		bool transparent = color.a < 1f || (networkedFlags & PrimitiveFlags.Visible) == 0;
		MerBlockLink link = Track(toy.GameObject, kind, group, isStatic, transparent);
		if (queue)
			SpawnQueue.Enqueue(link, (networkedFlags & PrimitiveFlags.Collidable) != 0 ? SpawnPriority.Collidable : SpawnPriority.Visible, disableWhenReady: isStatic);

		return toy;
	}

	/// <summary>
	/// Edits a primitive toy created by <see cref="CreatePrimitive"/>: changes are resent in place, and a collider change
	/// respawns it.
	/// </summary>
	/// <returns><see langword="false"/> when the new flags are not networked; the caller must destroy the toy.</returns>
	public static bool UpdatePrimitive(PrimitiveObjectToy toy, Vector3 position, Quaternion rotation, Vector3 scale, PrimitiveType type, Color color, PrimitiveFlags flags)
	{
		if (!TryGetNetworkedFlags(flags, out PrimitiveFlags networkedFlags))
			return false;

		GameObject gameObject = toy.GameObject;
		NetworkIdentity identity = toy.Base.netIdentity;
		bool spawned = IsSpawned(identity);
		bool respawn = spawned && ((toy.Flags ^ networkedFlags) & PrimitiveFlags.Collidable) != 0;

		// The wrapper would respawn through NetworkServer.Spawn; unspawn first so MerVisibility keeps control of observers.
		if (respawn)
			NetworkServer.UnSpawn(gameObject);

		toy.Flags = networkedFlags;
		toy.Position = position;
		toy.Rotation = rotation;
		toy.Scale = scale;
		toy.Color = color;

		PrimitiveType oldType = toy.Type;
		toy.Type = type;
		if (oldType != type && toy.Base._spawnedPrimitve != null)
			toy.Base.SetPrimitive(oldType, type); // Hooks do not run on a dedicated server.

		bool transparent = color.a < 1f || (networkedFlags & PrimitiveFlags.Visible) == 0;
		if (gameObject.TryGetComponent(out MerBlockLink link))
			Budget.Update(link, toy.IsStatic, transparent);

		if (respawn)
			MerVisibility.Spawn(identity, link != null ? link.Group : null, link != null ? link.Kind : MerObjectKind.Primitive);
		else if (spawned && toy.IsStatic)
			Resend(identity);

		return true;
	}

	/// <summary>
	/// Creates a light toy with the port's light policy (shadows) and queues it for spawning.
	/// </summary>
	/// <returns>The wrapper, or <see langword="null"/> at the hard cap.</returns>
	public static LightSourceToy? CreateLight(Vector3 position, Quaternion rotation, Color color, float intensity, float range, bool shadows, bool isStatic, SpawnGroup? group, bool queue = true)
	{
		// Map and schematic loads admit their strongest lights first (Budget.AdmitLights); this catches single creations.
		if (Budget.Lights >= Config.MaxLights)
		{
			UnsupportedContent.Skip("lights", $"max_lights {Config.MaxLights} reached");
			return null;
		}

		if (!Budget.CanCreate())
			return null;

		LightSourceToy light = LightSourceToy.Create(position, rotation, Vector3.one, null, networkSpawn: false);
		light.IsStatic = isStatic;
		if (!isStatic)
			light.Base.NetworkMovementSmoothing = DynamicMovementSmoothing;

		ApplyLight(light, color, intensity, range, shadows);

		MerBlockLink link = Track(light.GameObject, MerObjectKind.Light, group, isStatic, false);
		if (queue)
			SpawnQueue.Enqueue(link, SpawnPriority.Light, disableWhenReady: true);

		return light;
	}

	/// <summary>
	/// Edits a light toy; a static spawned light is resent in place.
	/// </summary>
	public static void UpdateLight(LightSourceToy light, Vector3 position, Quaternion rotation, Color color, float intensity, float range, bool shadows)
	{
		light.Position = position;
		light.Rotation = rotation;
		ApplyLight(light, color, intensity, range, shadows);

		if (light.IsStatic && IsSpawned(light.Base.netIdentity))
			Resend(light.Base.netIdentity);
	}

	/// <summary>
	/// Applies the light SyncVars Carl Mod has and the shadow policy (§3.6).
	/// </summary>
	public static void ApplyLight(LightSourceToy light, Color color, float intensity, float range, bool shadows)
	{
		light.Color = color;
		light.Intensity = intensity;
		light.Range = range;

		if (shadows && !Config.AllowLightShadows)
		{
			UnsupportedContent.Adapt("lights", "shadows forced off by allow_light_shadows: false");
			shadows = false;
		}

		light.Base.NetworkLightShadows = shadows;

		if (range > 30f)
			UnsupportedContent.Adapt("lights", "range above 30 m lights many objects; consider a smaller range");
	}

	/// <summary>
	/// Adds the <see cref="MerBlockLink"/> to a networked object and counts it in the <see cref="Budget"/>.
	/// </summary>
	/// <param name="gameObject">The unspawned object.</param>
	/// <param name="kind">Its kind.</param>
	/// <param name="group">Its spawn group.</param>
	/// <param name="isStatic">Whether it is a static toy.</param>
	/// <param name="isTransparent">Whether it is a transparent primitive.</param>
	/// <returns>The link.</returns>
	public static MerBlockLink Track(GameObject gameObject, MerObjectKind kind, SpawnGroup? group, bool isStatic, bool isTransparent = false)
	{
		if (!gameObject.TryGetComponent(out MerBlockLink link))
			link = gameObject.AddComponent<MerBlockLink>();

		link.Identity = gameObject.GetComponent<NetworkIdentity>();
		link.Kind = kind;
		link.Group = group;
		link.IsStatic = isStatic;
		link.IsTransparent = isTransparent;
		Budget.Register(link);
		return link;
	}

	/// <summary>
	/// Queues an object prepared with <see cref="Track"/>.
	/// </summary>
	public static void Queue(MerBlockLink link, SpawnPriority priority, bool disableWhenReady = false, Action<GameObject>? onSpawned = null) =>
		SpawnQueue.Enqueue(link, priority, disableWhenReady, onSpawned);

	/// <summary>
	/// Resends a spawned object's spawn payload in place to its observers (full-precision transform, every SyncVar).
	/// </summary>
	/// <param name="identity">The object's identity.</param>
	public static void Resend(NetworkIdentity identity) => MerVisibility.Resend(identity);

	/// <summary>
	/// Gets whether an object is network spawned.
	/// </summary>
	public static bool IsSpawned(NetworkIdentity? identity) => identity != null && identity.netId != 0 && NetworkServer.spawned.ContainsKey(identity.netId);

	/// <summary>
	/// Moves a networked MER object to a world position, keeping clients in sync (static toys are resent in place,
	/// doors respawned, structures re-synced).
	/// </summary>
	/// <param name="gameObject">The object.</param>
	/// <param name="position">The new world position.</param>
	public static void MoveTo(GameObject gameObject, Vector3 position)
	{
		if (gameObject.TryGetComponent(out SchematicObject schematic))
		{
			schematic.Position = position;
			return;
		}

		if (gameObject.TryGetComponent(out AdminToyBase toyBase))
		{
			AdminToy toy = AdminToy.Get(toyBase);
			toy.Position = position;
			if (toy.IsStatic && IsSpawned(toyBase.netIdentity))
				Resend(toyBase.netIdentity);

			return;
		}

		gameObject.transform.position = position;
		if (!gameObject.TryGetComponent(out NetworkIdentity identity) || !IsSpawned(identity))
			return;

		if (gameObject.TryGetComponent(out StructurePositionSync structure))
		{
			structure.Network_position = position;
			Resend(identity);
			return;
		}

		if (gameObject.TryGetComponent(out Interactables.Interobjects.DoorUtils.DoorVariant _))
		{
			// Doors have no transform sync.
			MerVisibility.Respawn(identity, gameObject.TryGetComponent(out MerBlockLink link) ? link.Group : null, MerObjectKind.Door);
			return;
		}

		Resend(identity);
	}

	/// <summary>
	/// Writes a structure's synced position and yaw (Carl Mod quantizes yaw to 5.625°; pitch and roll are lost).
	/// </summary>
	public static void SyncStructure(GameObject gameObject)
	{
		if (!gameObject.TryGetComponent(out StructurePositionSync structurePositionSync))
			return;

		Transform transform = gameObject.transform;
		structurePositionSync.Network_position = transform.position;
		structurePositionSync.Network_rotationY = (sbyte)Mathf.RoundToInt(transform.rotation.eulerAngles.y / 5.625f);
	}

	/// <summary>
	/// Gets the LabAPI wrapper of a primitive toy object.
	/// </summary>
	public static PrimitiveObjectToy? GetPrimitive(GameObject? gameObject) =>
		gameObject != null && gameObject.TryGetComponent(out BasePrimitiveObjectToy toy) ? PrimitiveObjectToy.Get(toy) : null;

	/// <summary>
	/// Gets the LabAPI wrapper of a light toy object.
	/// </summary>
	public static LightSourceToy? GetLight(GameObject? gameObject) =>
		gameObject != null && gameObject.TryGetComponent(out BaseLightSourceToy toy) ? LightSourceToy.Get(toy) : null;
}
