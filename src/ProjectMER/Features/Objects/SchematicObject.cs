using System.Globalization;
using AdminToys;
using InventorySystem.Items.Firearms.Attachments;
using LabApi.Features.Wrappers;
using Mirror;
using ProjectMER.Configs;
using ProjectMER.Events.Arguments;
using ProjectMER.Events.Handlers;
using ProjectMER.Events.Handlers.Internal;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.Serializable.Schematics;
using ProjectMER.Features.Serialization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProjectMER.Features.Objects;

/// <summary>
/// A spawned schematic.
/// </summary>
/// <remarks>
/// <para>
/// Carl Mod clients have no toy parenting, so the port flattens schematics (docs/projectmer-port-plan.md §3.2): the root
/// is a plain server GameObject, the block hierarchy exists only as server-side anchor GameObjects, and every networked
/// block (primitive, light, pickup, workstation) is spawned at root level with its world transform (position, rotation and
/// <see cref="Transform.lossyScale"/>). Anchors are created only where needed: for blocks with children, for blocks that
/// produce nothing on the client, and for every block of an animated or physics schematic.
/// </para>
/// <para>
/// Blocks are static toys unless they are in the subtree of an animated block or a rigidbody entry (those follow their
/// anchors through <see cref="SchematicSync"/>), or <see cref="IsStatic"/> is set to <see langword="false"/>. Exported
/// <c>"Static"</c> properties only count with <c>honor_static_property</c> or without <c>static_by_default</c>. Network spawning goes through <see cref="SpawnQueue"/>: <see cref="Schematic.SchematicSpawned"/>
/// still fires synchronously once the server objects exist, and <see cref="Schematic.SchematicBuilt"/> (with
/// <see cref="IsBuilt"/>) once every block is networked.
/// </para>
/// </remarks>
public class SchematicObject : MonoBehaviour
{
	private const float ResyncInterval = 0.25f;

	/// <summary>
	/// Loads with more transparent primitives than this are warned about (docs/projectmer-port-plan.md §3.3).
	/// </summary>
	internal const int TransparentWarnThreshold = 50;

	/// <summary>
	/// Gets the schematic name.
	/// </summary>
	public string Name { get; private set; }

	/// <summary>
	/// Gets a schematic directory path.
	/// </summary>
	public string DirectoryPath { get; private set; }

	/// <summary>
	/// Gets or sets the global position of the object.
	/// </summary>
	/// <remarks>Static blocks are resent in place, at most every 0.25 s.</remarks>
	public Vector3 Position
	{
		get => transform.position;
		set
		{
			transform.position = value;
			OnRootMoved();
		}
	}

	/// <summary>
	/// Gets or sets the global rotation of the object.
	/// </summary>
	public Quaternion Rotation
	{
		get => transform.rotation;
		set
		{
			transform.rotation = value;
			OnRootMoved();
		}
	}

	/// <summary>
	/// Gets or sets the global euler angles of the object.
	/// </summary>
	public Vector3 EulerAngles
	{
		get => Rotation.eulerAngles;
		set => Rotation = Quaternion.Euler(value);
	}

	/// <summary>
	/// Gets or sets the scale of the object.
	/// </summary>
	public Vector3 Scale
	{
		get => transform.localScale;
		set
		{
			transform.localScale = value;
			OnRootMoved();
		}
	}

	/// <summary>
	/// Gets the GameObjects of the blocks: the server-side anchor where a block has one, and the networked object where it
	/// has one (both for networked blocks with children). Networked blocks are not children of this object.
	/// </summary>
	public IReadOnlyList<GameObject> AttachedBlocks
	{
		get
		{
			_attachedBlocks.RemoveAll(static x => x == null);
			return _attachedBlocks;
		}
	}

	/// <summary>
	/// Gets the network identities of the networked blocks.
	/// </summary>
	public IReadOnlyList<NetworkIdentity> NetworkIdentities
	{
		get
		{
			_networkIdentities.Clear();
			foreach (BlockRecord record in _records)
			{
				if (record.Networked != null && record.Networked.TryGetComponent(out NetworkIdentity identity))
					_networkIdentities.Add(identity);
			}

			return _networkIdentities;
		}
	}

	/// <summary>
	/// Gets the admin toys among the networked blocks.
	/// </summary>
	public IReadOnlyList<AdminToyBase> AdminToyBases
	{
		get
		{
			_adminToyBases.Clear();
			foreach (BlockRecord record in _records)
			{
				if (record.Toy != null && !record.Toy.IsDestroyed)
					_adminToyBases.Add(record.Toy.Base);
			}

			return _adminToyBases;
		}
	}

	public AnimationController AnimationController => AnimationController.Get(this);

	/// <summary>
	/// Gets the data the schematic was built from. Shared with the parse cache; do not modify it.
	/// </summary>
	public SchematicObjectDataList Data { get; private set; }

	/// <summary>
	/// Gets the simplified build plan.
	/// </summary>
	public SchematicBuildPlan Plan { get; private set; }

	/// <summary>
	/// Gets the spawn group of the schematic's networked blocks.
	/// </summary>
	public SpawnGroup SpawnGroup { get; private set; }

	/// <summary>
	/// Gets whether every networked block has been spawned for clients.
	/// </summary>
	public bool IsBuilt { get; private set; }

	/// <summary>
	/// Gets the number of networked blocks.
	/// </summary>
	public int NetworkedCount => _records.Count;

	/// <summary>
	/// Gets the map object of this schematic, when it belongs to a map.
	/// </summary>
	public MapEditorObject? MapEditorObject => _mapEditorObject != null ? _mapEditorObject : (_mapEditorObject = GetComponent<MapEditorObject>());

	/// <summary>
	/// Gets or sets whether the blocks are static toys. Setting it to <see langword="false"/> switches every toy to a
	/// synced dynamic toy that follows its anchor; setting it back resends the blocks in place.
	/// </summary>
	public bool IsStatic
	{
		get
		{
			foreach (BlockRecord record in _records)
			{
				if (record.Toy != null && !record.Toy.IsStatic)
					return false;
			}

			return true;
		}

		set => SetStatic(value);
	}

	/// <summary>
	/// Builds the schematic as a stand-alone spawn group.
	/// </summary>
	public SchematicObject Init(SchematicObjectDataList data) => Init(data, null);

	/// <summary>
	/// Builds the schematic.
	/// </summary>
	/// <param name="data">The schematic data (may be the cached instance; it is not modified).</param>
	/// <param name="parentGroup">The spawn group of the map that loads the schematic, if any.</param>
	public SchematicObject Init(SchematicObjectDataList data, SpawnGroup? parentGroup)
	{
		long buildStart = System.Diagnostics.Stopwatch.GetTimestamp();
		Data = data;
		Name = Path.GetFileNameWithoutExtension(data.Path);
		DirectoryPath = data.Path;
		Plan = SchematicOptimizer.GetPlan(data);
		SpawnGroup = new SpawnGroup($"schematic {Name}", parentGroup) { Anchor = transform.position };

		ObjectFromId = new Dictionary<int, Transform>(data.Blocks.Count + 1)
		{
			{ data.RootObjectId, transform },
		};

		using (UnsupportedContent.Begin($"Schematic \"{Name}\""))
		try
		{
			foreach (KeyValuePair<BlockType, int> pair in Plan.Unsupported)
			{
				for (int i = 0; i < pair.Value; i++)
					UnsupportedContent.Skip($"{pair.Key} blocks", SchematicOptimizer.GetUnsupportedReason(pair.Key));
			}

			Dictionary<int, RuntimeAnimatorController> animators = LoadAnimators();
			_rigidbodies = LoadRigidbodies();

			// Only animated and physics blocks and their subtrees move; everything else stays static.
			_dynamicRoots.Clear();
			foreach (int id in animators.Keys)
				_dynamicRoots.Add(id);

			foreach (int id in _rigidbodies.Keys)
				_dynamicRoots.Add(id);

			Build();

			if (_dynamicRoots.Count > 0)
				SetUpDynamic(followAll: false);

			foreach (KeyValuePair<int, RuntimeAnimatorController> pair in animators)
			{
				if (ObjectFromId.TryGetValue(pair.Key, out Transform anchor))
					anchor.gameObject.AddComponent<Animator>().runtimeAnimatorController = pair.Value;
			}

			if (animators.Count > 0)
				AssetBundle.UnloadAllAssetBundles(false);
		}
		finally
		{
			ReleaseScratch();
		}

		LogSummary((System.Diagnostics.Stopwatch.GetTimestamp() - buildStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

		Schematic.OnSchematicSpawned(new(this, Name));

		SpawnGroup.Completed += OnSpawnGroupCompleted;
		SpawnGroup.CheckCompleted();

		return this;
	}

	public void Destroy() => Destroy(gameObject);

	/// <summary>
	/// Gets the time at which a pending in-place resend is due.
	/// </summary>
	internal float ResyncDueTime { get; private set; }

	/// <summary>
	/// Moves every networked block to the world transform of its anchor (or parent and local transform) and resends spawned
	/// static blocks in place.
	/// </summary>
	internal void ResyncNow()
	{
		try
		{
			Resync();
		}
		finally
		{
			ReleaseScratch();
		}
	}

	private void Resync()
	{
		_lastResync = Time.time;
		_resyncScheduled = false;

		foreach (BlockRecord record in _records)
		{
			// Followers are moved by SchematicSync from their anchors.
			if (record.Networked == null || record.Follows)
				continue;

			GetWorld(record, out Vector3 position, out Quaternion rotation, out Vector3 scale);
			switch (record.Output)
			{
				case BlockOutput.Primitive:
					{
						AdminToy toy = record.Toy!;
						toy.Position = position;
						toy.Rotation = rotation;
						toy.Scale = scale;
						if (toy.IsStatic)
							ToyFactory.Resend(toy.Base.netIdentity);

						break;
					}

				case BlockOutput.Light:
					{
						AdminToy toy = record.Toy!;
						toy.Position = position;
						toy.Rotation = rotation;
						if (toy.IsStatic)
							ToyFactory.Resend(toy.Base.netIdentity);

						break;
					}

				case BlockOutput.Pickup:
					{
						Pickup? pickup = Pickup.Get(record.Networked.GetComponent<InventorySystem.Items.Pickups.ItemPickupBase>());
						if (pickup != null)
						{
							pickup.Position = position;
							pickup.Rotation = rotation;
						}

						break;
					}

				case BlockOutput.Workstation:
					record.Networked.transform.SetPositionAndRotation(position, rotation);
					ToyFactory.SyncStructure(record.Networked);
					if (ToyFactory.IsSpawned(record.Networked.GetComponent<NetworkIdentity>()))
						ToyFactory.Resend(record.Networked.GetComponent<NetworkIdentity>());

					break;
			}
		}
	}

	internal Dictionary<int, Transform> ObjectFromId = [];

	private void Build()
	{
		List<SchematicBlockData> blocks = Data.Blocks;
		bool[] lightAdmitted = AdmitLights();

		Stack<(int Index, Transform Parent, bool Dynamic)> stack = new();
		PushChildren(stack, Data.RootObjectId, transform, false);

		while (stack.Count > 0)
		{
			(int index, Transform parent, bool dynamicParent) = stack.Pop();
			SchematicBlockData block = blocks[index];
			bool dynamic = dynamicParent || _dynamicRoots.Contains(block.ObjectId);
			List<int> children = Plan.GetChildren(block.ObjectId);
			BlockOutput output = Plan.Outputs[index];

			if (output == BlockOutput.Light && !lightAdmitted[index])
				output = BlockOutput.None;

			if (output == BlockOutput.Pickup && block.Properties != null && block.Properties.TryGetValue("Chance", out object chance) && UnityEngine.Random.Range(0, 101) > Convert.ToSingle(chance, CultureInfo.InvariantCulture))
				output = BlockOutput.None;

			Transform? anchor = null;
			if (dynamic || children.Count > 0 || output == BlockOutput.None)
				anchor = CreateAnchor(block, parent);

			BlockRecord record = new(block, index, output, parent, anchor) { InDynamicSubtree = dynamic, Flags = Plan.Flags[index] };
			GameObject? networked = output == BlockOutput.None ? null : CreateNetworked(record);
			if (networked == null && anchor == null)
				anchor = record.Anchor = CreateAnchor(block, parent);

			if (anchor != null)
			{
				ObjectFromId[block.ObjectId] = anchor;
				_attachedBlocks.Add(anchor.gameObject);
			}

			if (networked != null)
			{
				networked.name = block.Name;
				record.Networked = networked;
				_records.Add(record);
				_attachedBlocks.Add(networked);
				if (anchor == null)
					ObjectFromId[block.ObjectId] = networked.transform;
			}

			if (children.Count > 0)
				PushChildren(stack, block.ObjectId, anchor!, dynamic);
		}

		BuildAddedBlocks();
	}

	/// <summary>
	/// Builds the blocks the optimizer added (for example merged cubes) as static leaves under their parent.
	/// </summary>
	private void BuildAddedBlocks()
	{
		foreach (SchematicBuildPlan.AddedBlock added in Plan.AddedBlocks)
		{
			if (!ObjectFromId.TryGetValue(added.Data.ParentId, out Transform parent) || parent.GetComponent<MerBlockLink>() != null)
				parent = transform;

			BlockRecord record = new(added.Data, -1, added.Output, parent, null) { Flags = added.Flags };
			GameObject? networked = CreateNetworked(record);
			if (networked == null)
				continue;

			networked.name = added.Data.Name;
			record.Networked = networked;
			_records.Add(record);
			_attachedBlocks.Add(networked);
		}
	}

	private void PushChildren(Stack<(int Index, Transform Parent, bool Dynamic)> stack, int objectId, Transform parent, bool dynamic)
	{
		List<int> children = Plan.GetChildren(objectId);
		for (int i = children.Count - 1; i >= 0; i--)
			stack.Push((children[i], parent, dynamic));
	}

	/// <summary>
	/// Decides whether a block outside animated and physics subtrees spawns as a static toy.
	/// </summary>
	/// <remarks>
	/// With <c>static_by_default</c> (the default) every such block is static: exporters write <c>"Static": false</c> for
	/// blocks that never move, so the property only counts with <c>honor_static_property</c>. Without
	/// <c>static_by_default</c>, ProjectMER's rule applies (only <c>"Static": true</c> is static).
	/// </remarks>
	private static bool IsStaticBlock(SchematicBlockData block)
	{
		bool? property = block.StaticProperty;
		if (Config.StaticByDefault)
			return !(Config.HonorStaticProperty && property == false);

		return property == true;
	}

	private Transform CreateAnchor(SchematicBlockData block, Transform parent)
	{
		Transform anchor = new GameObject(block.Name).transform;
		anchor.SetParent(parent, false);
		anchor.SetLocalPositionAndRotation(block.Position, Quaternion.Euler(block.Rotation));
		anchor.localScale = block.EffectiveScale;
		return anchor;
	}

	private GameObject? CreateNetworked(BlockRecord record)
	{
		SchematicBlockData block = record.Data;
		GetWorld(record, out Vector3 position, out Quaternion rotation, out Vector3 scale);
		bool isStatic = !record.InDynamicSubtree && !_forcedDynamic && IsStaticBlock(block);

		switch (record.Output)
		{
			case BlockOutput.Primitive:
				{
					block.GetPrimitive(out PrimitiveType primitiveType, out Color color, out _);
					LabApi.Features.Wrappers.PrimitiveObjectToy? toy = ToyFactory.CreatePrimitive(position, rotation, scale, primitiveType, color, record.Flags, isStatic, SpawnGroup);
					if (toy == null)
						return null;

					record.Toy = toy;
					Link(toy.GameObject, block);
					return toy.GameObject;
				}

			case BlockOutput.Light:
				{
					block.GetLight(out Color color, out float intensity, out float range, out bool shadows, out LightType lightType);
					if (lightType != LightType.Point)
						UnsupportedContent.Adapt($"{lightType} lights", "spawned as point lights; Carl Mod's light toy has no type, shape or spot angles");

					LabApi.Features.Wrappers.LightSourceToy? light = ToyFactory.CreateLight(position, rotation, color, intensity, range, shadows, isStatic, SpawnGroup);
					if (light == null)
						return null;

					record.Toy = light;
					Link(light.GameObject, block);
					return light.GameObject;
				}

			case BlockOutput.Pickup:
				{
					if (!block.TryGetItemType(out ItemType itemType, out string raw))
					{
						UnsupportedContent.Skip("pickups", $"item \"{raw}\" does not exist in Carl Mod");
						return null;
					}

					if (!Budget.CanCreate())
						return null;

					Pickup? pickup = Pickup.Create(itemType, position, rotation, scale, networkSpawn: false);
					if (pickup == null)
					{
						UnsupportedContent.Skip("pickups", $"item {itemType} has no pickup");
						return null;
					}

					if (block.Properties != null && block.Properties.ContainsKey("Locked"))
						PickupEventsHandler.ButtonPickups[pickup.Serial] = this;

					MerBlockLink link = ToyFactory.Track(pickup.GameObject, MerObjectKind.Pickup, SpawnGroup, false);
					Link(pickup.GameObject, block);
					ToyFactory.Queue(link, SpawnPriority.Pickup);
					return pickup.GameObject;
				}

			case BlockOutput.Workstation:
				{
					if (PrefabManager.Workstation == null || !Budget.CanCreate())
						return null;

					WorkstationController workstation = Object.Instantiate(PrefabManager.Workstation);
					workstation.transform.SetPositionAndRotation(position, rotation);
					workstation.transform.localScale = scale;
					workstation.NetworkStatus = (byte)(block.Properties != null && block.Properties.TryGetValue("IsInteractable", out object isInteractable) && Convert.ToBoolean(isInteractable, CultureInfo.InvariantCulture) ? 0 : 4);
					ToyFactory.SyncStructure(workstation.gameObject);

					MerBlockLink link = ToyFactory.Track(workstation.gameObject, MerObjectKind.Structure, SpawnGroup, false);
					Link(workstation.gameObject, block);
					ToyFactory.Queue(link, SpawnPriority.Collidable);
					return workstation.gameObject;
				}

			default:
				return null;
		}
	}

	private void Link(GameObject gameObject, SchematicBlockData block)
	{
		MerBlockLink link = gameObject.GetComponent<MerBlockLink>();
		link.Schematic = this;
		link.BlockId = block.ObjectId;
	}

	private static void GetWorld(BlockRecord record, out Vector3 position, out Quaternion rotation, out Vector3 scale)
	{
		if (record.Anchor != null)
		{
			record.Anchor.GetPositionAndRotation(out position, out rotation);
			scale = record.Anchor.lossyScale;
			return;
		}

		// Unity's world rotation and lossyScale are not the plain products once a parent has negative scale components,
		// so let a scratch transform compute them; it is reparented only when the parent changes.
		SchematicBlockData block = record.Data;
		Transform scratch = Scratch;
		if (scratch.parent != record.Parent)
			scratch.SetParent(record.Parent, false);

		scratch.SetLocalPositionAndRotation(block.Position, Quaternion.Euler(block.Rotation));
		scratch.localScale = block.EffectiveScale;
		scratch.GetPositionAndRotation(out position, out rotation);
		scale = scratch.lossyScale;
	}

	private static Transform Scratch
	{
		get
		{
			if (_scratch == null)
			{
				GameObject gameObject = new("MER flatten scratch");
				gameObject.SetActive(false);
				_scratch = gameObject.transform;
			}

			return _scratch;
		}
	}

	/// <summary>
	/// Moves the scratch transform back to the scene root so destroying a schematic never destroys it.
	/// </summary>
	private static void ReleaseScratch()
	{
		if (_scratch != null && _scratch.parent != null)
			_scratch.SetParent(null, false);
	}

	private bool[] AdmitLights()
	{
		List<SchematicBlockData> blocks = Data.Blocks;
		List<int> indices = [];
		List<float> strengths = [];
		for (int i = 0; i < blocks.Count; i++)
		{
			if (Plan.Outputs[i] != BlockOutput.Light)
				continue;

			blocks[i].GetLight(out _, out float intensity, out float range, out _, out _);
			indices.Add(i);
			strengths.Add(intensity * range);
		}

		bool[] admitted = new bool[blocks.Count];
		bool[] result = Budget.AdmitLights(strengths);
		for (int i = 0; i < indices.Count; i++)
			admitted[indices[i]] = result[i];

		return admitted;
	}

	/// <summary>
	/// Makes the toys of animated and physics subtrees (or every toy, for <paramref name="followAll"/>) follow their
	/// anchors through <see cref="SchematicSync"/>; physics toys drive their anchors instead.
	/// </summary>
	private void SetUpDynamic(bool followAll)
	{
		if (!TryGetComponent(out SchematicSync sync))
			sync = gameObject.AddComponent<SchematicSync>();

		float syncInterval = Config.DynamicToySyncInterval;

		foreach (BlockRecord record in _records)
		{
			if (record.Toy == null || record.Follows || !(followAll || record.InDynamicSubtree))
				continue;

			record.Follows = true;
			record.Toy.SyncInterval = syncInterval;
			if (_rigidbodies.TryGetValue(record.Data.ObjectId, out SerializableRigidbody rigidbodyData))
			{
				// Physics moves the toy (it has the collider); its own LateUpdate syncs it and it drives its anchor.
				ApplyRigidbody(record.Networked!, rigidbodyData);
				sync.AddDriver(record.Anchor!, record.Toy);
				continue;
			}

			sync.AddFollower(record.Anchor!, record.Toy);
			SpawnQueue.DisableWhenReady(record.Toy.Base);

			if (record.Output == BlockOutput.Primitive && (record.Flags & PrimitiveFlags.Collidable) != 0 && !record.Networked!.TryGetComponent(out Rigidbody _))
			{
				// A moving static collider makes PhysX rebuild it; a kinematic body is cheap to move.
				Rigidbody kinematic = record.Networked.AddComponent<Rigidbody>();
				kinematic.isKinematic = true;
				kinematic.useGravity = false;
			}
		}

		// Rigidbody entries of pickups apply to the pickup's own body (it has the collider); entries of blocks without a
		// networked object (empties) move their anchor, and the subtree follows.
		foreach (KeyValuePair<int, SerializableRigidbody> pair in _rigidbodies)
		{
			BlockRecord? owner = null;
			foreach (BlockRecord record in _records)
			{
				if (record.Data.ObjectId == pair.Key)
				{
					owner = record;
					break;
				}
			}

			if (owner != null)
			{
				if (owner.Output == BlockOutput.Pickup)
					ApplyRigidbody(owner.Networked!, pair.Value);

				continue;
			}

			if (ObjectFromId.TryGetValue(pair.Key, out Transform target) && target != transform)
				ApplyRigidbody(target.gameObject, pair.Value);
		}
	}

	private static void ApplyRigidbody(GameObject gameObject, SerializableRigidbody data)
	{
		if (!gameObject.TryGetComponent(out Rigidbody rigidbody))
			rigidbody = gameObject.AddComponent<Rigidbody>();

		rigidbody.isKinematic = data.IsKinematic;
		rigidbody.useGravity = data.UseGravity;
		rigidbody.constraints = data.Constraints;
		rigidbody.mass = data.Mass;
	}

	private void SetStatic(bool value)
	{
		if (IsStatic == value)
			return;

		if (!value)
		{
			_forcedDynamic = true;

			// Every toy needs an anchor to follow.
			foreach (BlockRecord record in _records)
			{
				if (record.Anchor != null || record.Toy == null)
					continue;

				record.Anchor = CreateAnchor(record.Data, record.Parent);
				ObjectFromId[record.Data.ObjectId] = record.Anchor;
				_attachedBlocks.Add(record.Anchor.gameObject);
			}

			foreach (BlockRecord record in _records)
			{
				if (record.Toy == null || !record.Toy.IsStatic)
					continue;

				record.Toy.IsStatic = false;
				record.Toy.Base.NetworkMovementSmoothing = ToyFactory.DynamicMovementSmoothing;
				if (record.Networked!.TryGetComponent(out MerBlockLink link))
					Budget.Update(link, false, link.IsTransparent);
			}

			SetUpDynamic(followAll: true);
			return;
		}

		_forcedDynamic = false;
		if (TryGetComponent(out SchematicSync sync))
		{
			sync.Clear();
			Destroy(sync);
		}

		foreach (BlockRecord record in _records)
		{
			if (record.Toy == null)
				continue;

			record.Follows = false;
			record.Toy.IsStatic = true;
			if (record.Networked!.TryGetComponent(out MerBlockLink link))
				Budget.Update(link, true, link.IsTransparent);

			SpawnQueue.DisableWhenReady(record.Toy.Base);
		}

		ResyncNow();
	}

	private void OnRootMoved()
	{
		SpawnGroup.Anchor = transform.position;
		if (_resyncScheduled || _records.Count == CountFollowers())
			return;

		_resyncScheduled = true;
		ResyncDueTime = Math.Max(Time.time, _lastResync + ResyncInterval);
		SpawnQueue.ScheduleResync(this);
	}

	private int CountFollowers()
	{
		int count = 0;
		foreach (BlockRecord record in _records)
		{
			if (record.Follows)
				count++;
		}

		return count;
	}

	private Dictionary<int, RuntimeAnimatorController> LoadAnimators()
	{
		Dictionary<int, RuntimeAnimatorController> result = [];
		foreach (int index in Plan.AnimatedBlocks)
		{
			SchematicBlockData block = Data.Blocks[index];
			if (TryGetAnimatorController(block.AnimatorName, out RuntimeAnimatorController controller))
				result[block.ObjectId] = controller;
		}

		return result;
	}

	private bool TryGetAnimatorController(string animatorName, out RuntimeAnimatorController animatorController)
	{
		animatorController = null!;

		if (string.IsNullOrEmpty(animatorName))
			return false;

		try
		{
			Object? animatorObject = AssetBundle.GetAllLoadedAssetBundles().FirstOrDefault(x => x.mainAsset != null && x.mainAsset.name == animatorName)?.LoadAllAssets().FirstOrDefault(x => x is RuntimeAnimatorController);

			if (animatorObject is null)
			{
				string path = Path.Combine(DirectoryPath, animatorName);

				if (!File.Exists(path))
				{
					Logger.Warn($"{gameObject.name} block of schematic should have a {animatorName} animator attached, but the file does not exist!");
					return false;
				}

				AssetBundle? bundle = AssetBundle.LoadFromFile(path);
				animatorObject = bundle != null ? bundle.LoadAllAssets().FirstOrDefault(x => x is RuntimeAnimatorController) : null;
			}

			if (animatorObject is not RuntimeAnimatorController controller)
			{
				Logger.Warn($"Schematic \"{Name}\": animator bundle {animatorName} could not be loaded (it may be built for another Unity version); the schematic stays static.");
				return false;
			}

			animatorController = controller;
			return true;
		}
		catch (Exception e)
		{
			Logger.Warn($"Schematic \"{Name}\": failed to load animator {animatorName}: {e.Message}");
			return false;
		}
	}

	private Dictionary<int, SerializableRigidbody> LoadRigidbodies()
	{
		string rigidbodyPath = Path.Combine(DirectoryPath, $"{Name}-Rigidbodies.json");
		if (!File.Exists(rigidbodyPath))
			return [];

		try
		{
			Dictionary<int, SerializableRigidbody> all = SchematicJson.ReadCached<Dictionary<int, SerializableRigidbody>>(rigidbodyPath);
			Dictionary<int, SerializableRigidbody> reachable = [];
			HashSet<int> ids = [];
			foreach (List<int> children in Plan.Children.Values)
			{
				foreach (int index in children)
					ids.Add(Data.Blocks[index].ObjectId);
			}

			foreach (KeyValuePair<int, SerializableRigidbody> pair in all)
			{
				if (ids.Contains(pair.Key))
					reachable[pair.Key] = pair.Value;
			}

			return reachable;
		}
		catch (Exception e)
		{
			Logger.Warn($"Schematic \"{Name}\": failed to read {Name}-Rigidbodies.json: {e.Message}");
			return [];
		}
	}

	private void LogSummary(double buildMs)
	{
		int primitives = 0, lights = 0, pickups = 0, workstations = 0, transparent = 0, dynamic = 0;
		foreach (BlockRecord record in _records)
		{
			switch (record.Output)
			{
				case BlockOutput.Primitive:
					primitives++;
					break;
				case BlockOutput.Light:
					lights++;
					break;
				case BlockOutput.Pickup:
					pickups++;
					break;
				case BlockOutput.Workstation:
					workstations++;
					break;
			}

			if (record.Networked != null && record.Networked.TryGetComponent(out MerBlockLink link))
			{
				if (link.IsTransparent)
					transparent++;

				if (record.Toy != null && !record.Toy.IsStatic)
					dynamic++;
			}
		}

		Logger.Info($"Schematic \"{Name}\": {Data.Blocks.Count} blocks ({Plan.ReachableBlocks} reachable). ProjectMER would network {Plan.ProjectMerNetworked} objects; " +
			$"this port networks {_records.Count} (primitives {primitives}, lights {lights}, pickups {pickups}, workstations {workstations}; transparent {transparent}, dynamic {dynamic}). " +
			$"Not networked: {Plan.Anchors} empty, invisible or unsupported blocks (of which {Plan.DroppedInvisible} invisible primitives, {Plan.DroppedZeroScale} zero-scale). " +
			$"Animated/physics subtrees: {(_dynamicRoots.Count == 0 ? "none" : _dynamicRoots.Count + " roots")}; server build {buildMs:F1} ms.");

		if (transparent > TransparentWarnThreshold)
			Logger.Warn($"Schematic \"{Name}\" has {transparent} transparent primitives (alpha below 1 or invisible colliders). Each one still renders with blending, which costs fill rate on phones.");

		if (_records.Count > Config.PrimitiveWarnPerSchematic)
			Logger.Warn($"Schematic \"{Name}\" networks {_records.Count} blocks (primitive_warn_per_schematic: {Config.PrimitiveWarnPerSchematic}); {transparent} transparent, {dynamic} dynamic. Phones render every one of them.");
	}

	private void OnSpawnGroupCompleted(SpawnGroup group)
	{
		IsBuilt = true;
		Schematic.OnSchematicBuilt(new(this, Name));
	}

	private void OnDestroy()
	{
		SpawnGroup?.Cancel();

		foreach (BlockRecord record in _records)
		{
			if (record.Networked == null)
				continue;

			if (record.Output == BlockOutput.Pickup && record.Networked.TryGetComponent(out InventorySystem.Items.Pickups.ItemPickupBase pickupBase))
				PickupEventsHandler.ButtonPickups.Remove(pickupBase.Info.Serial);

			SpawnQueue.Destroy(record.Networked);
		}

		_records.Clear();
		AnimationController.Dictionary.Remove(this);
		Schematic.OnSchematicDestroyed(new(this, Name));
	}

	private static Config Config => ProjectMER.Singleton.Config!;

	private readonly List<GameObject> _attachedBlocks = [];
	private readonly List<NetworkIdentity> _networkIdentities = [];
	private readonly List<AdminToyBase> _adminToyBases = [];
	private readonly List<BlockRecord> _records = [];

	private static Transform? _scratch;

	private MapEditorObject? _mapEditorObject;
	private readonly HashSet<int> _dynamicRoots = [];
	private Dictionary<int, SerializableRigidbody> _rigidbodies = [];
	private bool _forcedDynamic;
	private bool _resyncScheduled;
	private float _lastResync = float.NegativeInfinity;

	/// <summary>
	/// A schematic block that produced a networked object.
	/// </summary>
	private sealed class BlockRecord(SchematicBlockData data, int index, BlockOutput output, Transform parent, Transform? anchor)
	{
		public SchematicBlockData Data { get; } = data;

		public int Index { get; } = index;

		public BlockOutput Output { get; } = output;

		/// <summary>
		/// Gets the transform the block's local transform is relative to.
		/// </summary>
		public Transform Parent { get; } = parent;

		/// <summary>
		/// Gets or sets the block's own anchor, if it has one.
		/// </summary>
		public Transform? Anchor { get; set; } = anchor;

		public GameObject? Networked { get; set; }

		public AdminToy? Toy { get; set; }

		/// <summary>
		/// Gets or sets the networked primitive flags (primitives only).
		/// </summary>
		public PrimitiveFlags Flags { get; set; }

		/// <summary>
		/// Gets or sets whether the block is in an animated or physics subtree.
		/// </summary>
		public bool InDynamicSubtree { get; set; }

		/// <summary>
		/// Gets or sets whether <see cref="SchematicSync"/> moves the toy (it follows or drives its anchor).
		/// </summary>
		public bool Follows { get; set; }
	}
}
