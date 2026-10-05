using System.Diagnostics;
using System.Globalization;
using System.Text;
using AdminToys;
using InventorySystem.Items.Firearms.Attachments;
using LabApi.Features.Wrappers;
using Mirror;
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
/// <c>"Static"</c> properties only count with <c>honor_static_property</c> or without <c>static_by_default</c>.
/// </para>
/// <para>
/// Loading never stalls a frame (§3.5): the file is parsed and planned (<see cref="SchematicOptimizer"/>) on a worker
/// thread, then <see cref="SpawnQueue"/> builds anchors and toys a few at a time within each frame's spawn budget and
/// networks them. <see cref="IsSpawned"/> and <see cref="Schematic.SchematicSpawned"/> mark that every server object
/// exists; <see cref="IsBuilt"/> and <see cref="Schematic.SchematicBuilt"/> that every networked block reached the
/// clients. Members that return blocks (<see cref="AttachedBlocks"/>, <see cref="NetworkIdentities"/>,
/// <see cref="AdminToyBases"/>, <see cref="AnimationController"/>) and the <see cref="IsStatic"/> setter finish the
/// build synchronously first (<see cref="EnsureSpawned"/>), so code written for ProjectMER sees a complete schematic.
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
	/// has one (both for networked blocks with children), plus merged blocks. Networked blocks are not children of this
	/// object. Finishes the build first.
	/// </summary>
	public IReadOnlyList<GameObject> AttachedBlocks
	{
		get
		{
			EnsureSpawned();
			_attachedBlocks.RemoveAll(static x => x == null);
			return _attachedBlocks;
		}
	}

	/// <summary>
	/// Gets the network identities of the networked blocks. Finishes the build first.
	/// </summary>
	public IReadOnlyList<NetworkIdentity> NetworkIdentities
	{
		get
		{
			EnsureSpawned();
			return CurrentNetworkIdentities;
		}
	}

	/// <summary>
	/// Gets the admin toys among the networked blocks. Finishes the build first.
	/// </summary>
	public IReadOnlyList<AdminToyBase> AdminToyBases
	{
		get
		{
			EnsureSpawned();
			return CurrentAdminToyBases;
		}
	}

	/// <summary>
	/// Gets the admin toys created so far, without finishing the build (for periodic scans that must not stall a frame).
	/// </summary>
	public IReadOnlyList<AdminToyBase> CurrentAdminToyBases
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

	/// <summary>
	/// Gets the network identities of the networked blocks created so far, without finishing the build.
	/// </summary>
	public IReadOnlyList<NetworkIdentity> CurrentNetworkIdentities
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

	public AnimationController AnimationController => AnimationController.Get(this);

	/// <summary>
	/// Gets the data the schematic was built from. Shared with the parse cache; do not modify it. Waits for the parse
	/// when it is still running.
	/// </summary>
	public SchematicObjectDataList Data
	{
		get
		{
			EnsureLoaded();
			return _data;
		}
	}

	/// <summary>
	/// Gets the simplified build plan. Waits for the plan when it is still being made.
	/// </summary>
	public SchematicBuildPlan Plan
	{
		get
		{
			EnsureLoaded();
			return _plan;
		}
	}

	/// <summary>
	/// Gets the spawn group of the schematic's networked blocks.
	/// </summary>
	public SpawnGroup SpawnGroup { get; private set; }

	/// <summary>
	/// Gets whether every server-side object of the schematic exists (raised as <see cref="Schematic.SchematicSpawned"/>).
	/// The networked blocks may still be streaming to clients.
	/// </summary>
	public bool IsSpawned { get; private set; }

	/// <summary>
	/// Gets whether every networked block has been spawned for clients.
	/// </summary>
	public bool IsBuilt { get; private set; }

	/// <summary>
	/// Gets the number of networked blocks created so far (all of them once <see cref="IsSpawned"/>).
	/// </summary>
	public int NetworkedCount => _records.Count;

	/// <summary>
	/// Gets the map object of this schematic, when it belongs to a map.
	/// </summary>
	public MapEditorObject? MapEditorObject => _mapEditorObject != null ? _mapEditorObject : (_mapEditorObject = GetComponent<MapEditorObject>());

	/// <summary>
	/// Gets or sets whether the blocks are static toys. Setting it to <see langword="false"/> finishes the build, networks
	/// merged and duplicate blocks individually again, and switches every toy to a synced dynamic toy that follows its
	/// anchor; setting it back resends the blocks in place.
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
	/// Builds the schematic. The plan is made on a worker thread (or taken from the cache), and the blocks are built and
	/// networked across the next frames.
	/// </summary>
	/// <param name="data">The schematic data (may be the cached instance; it is not modified).</param>
	/// <param name="parentGroup">The spawn group of the map that loads the schematic, if any.</param>
	public SchematicObject Init(SchematicObjectDataList data, SpawnGroup? parentGroup)
	{
		if (data == null)
			throw new ArgumentNullException(nameof(data));

		Name = data.Path != null ? Path.GetFileNameWithoutExtension(data.Path) : gameObject.name;
		DirectoryPath = data.Path!;
		_data = data;
		Begin(parentGroup);
		_planJob = SchematicLoader.LoadPlan(data, _settings);
		_phase = BuildPhase.LoadingPlan;
		SpawnQueue.AddBuilder(this);
		return this;
	}

	/// <summary>
	/// Builds the schematic from its file, parsed on a worker thread. <see cref="Schematic.SchematicSpawning"/> is raised
	/// once the data is available; cancelling it destroys this object.
	/// </summary>
	internal SchematicObject InitFromFile(string schematicName, string directory, string jsonPath, SpawnGroup? parentGroup)
	{
		Name = schematicName;
		DirectoryPath = directory;
		Begin(parentGroup);
		_raiseSpawning = true;
		_dataJob = SchematicLoader.LoadFile(schematicName, directory, jsonPath, Schematic.HasSchematicSpawningSubscribers ? null : _settings);
		_phase = BuildPhase.LoadingData;
		SpawnQueue.AddBuilder(this);
		return this;
	}

	/// <summary>
	/// Finishes loading and building synchronously (waiting for the worker thread if needed). The networked blocks keep
	/// streaming to clients through the spawn queue. Does nothing once <see cref="IsSpawned"/>.
	/// </summary>
	public void EnsureSpawned()
	{
		if (_phase is BuildPhase.NotStarted or >= BuildPhase.Spawned || _stepping)
			return;

		while (_phase < BuildPhase.Spawned)
		{
			if (_phase == BuildPhase.LoadingData)
				_dataJob!.Wait();
			else if (_phase == BuildPhase.LoadingPlan)
				_planJob!.Wait();

			if (StepBuild(long.MaxValue))
				break;
		}
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

	/// <summary>
	/// Runs build steps until <paramref name="deadline"/> (a <see cref="Stopwatch"/> timestamp). Each call that has work
	/// makes at least one step. Called by <see cref="SpawnQueue"/> every frame.
	/// </summary>
	/// <returns>Whether the build is over (finished, failed or destroyed). A step that throws fails the build.</returns>
	internal bool StepBuild(long deadline)
	{
		if (this == null || _phase is BuildPhase.NotStarted or >= BuildPhase.Spawned)
			return true;

		if (_stepping)
			return false;

		_stepping = true;
		long sliceStart = Stopwatch.GetTimestamp();
		bool worked = false;
		bool reporting = false;
		try
		{
			while (true)
			{
				switch (_phase)
				{
					case BuildPhase.LoadingData:
						if (!_dataJob!.IsCompleted)
							return false;

						worked = true;
						if (!CompleteData())
							return true;

						break;

					case BuildPhase.LoadingPlan:
						if (!_planJob!.IsCompleted)
							return false;

						worked = true;
						if (!CompletePlan())
							return true;

						break;

					case BuildPhase.Prepare:
						worked = true;
						_report = UnsupportedContent.Begin($"Schematic \"{Name}\"");
						reporting = true;
						Prepare();
						_phase = BuildPhase.Animators;
						break;

					case BuildPhase.Animators:
						worked = true;
						Report(ref reporting);
						if (!LoadAnimators(deadline))
							return false;

						_phase = BuildPhase.Blocks;
						break;

					case BuildPhase.Blocks:
						worked = true;
						Report(ref reporting);
						if (!BuildBlocks(deadline))
							return false;

						_phase = BuildPhase.AddedBlocks;
						break;

					case BuildPhase.AddedBlocks:
						worked = true;
						Report(ref reporting);
						if (!BuildAddedBlocks(deadline))
							return false;

						_phase = BuildPhase.Finish;
						break;

					case BuildPhase.Finish:
						worked = true;
						Report(ref reporting);
						reporting = false;
						Finish(sliceStart);
						return true;

					default:
						return true;
				}

				if (Stopwatch.GetTimestamp() >= deadline)
					return false;
			}
		}
		catch (Exception e)
		{
			// Malformed block data (a "Chance" or "IsInteractable" that is no number or boolean, a null rigidbody entry...):
			// destroy the half-built schematic so its light reservations are released and its map does not wait for it.
			Logger.Error($"Schematic \"{Name}\" could not be built and was removed: {e}");
			Fail();
			return true;
		}
		finally
		{
			if (reporting && _report != null)
				UnsupportedContent.Suspend(_report);

			ReleaseScratch();
			_stepping = false;
			if (worked)
			{
				double sliceMs = (Stopwatch.GetTimestamp() - sliceStart) * 1000.0 / Stopwatch.Frequency;
				_buildMs += sliceMs;
				_buildSlices++;
				if (sliceMs > _worstSliceMs)
					_worstSliceMs = sliceMs;
			}
		}
	}

	internal Dictionary<int, Transform> ObjectFromId = [];

	private void Begin(SpawnGroup? parentGroup)
	{
		_loadStart = Stopwatch.GetTimestamp();
		SpawnGroup = new SpawnGroup($"schematic {Name}", parentGroup) { Anchor = transform.position };
		_settings = OptimizerSettings.FromConfig(parentGroup != null);

		// Merged blocks are exact in schematic space; a non-uniform root scale would shear rotated ones differently.
		if (_settings.MergeBlocks && !IsUniform(transform.lossyScale))
			_settings = _settings with { MergeBlocks = false };
	}

	private static bool IsUniform(Vector3 scale) =>
		Mathf.Abs(scale.x - scale.y) <= 1e-4f * Mathf.Abs(scale.x) && Mathf.Abs(scale.x - scale.z) <= 1e-4f * Mathf.Abs(scale.x);

	/// <summary>
	/// Resumes this schematic's warning scope for the current step.
	/// </summary>
	private void Report(ref bool reporting)
	{
		if (reporting || _report == null)
			return;

		UnsupportedContent.Resume(_report);
		reporting = true;
	}

	private bool CompleteData()
	{
		SchematicLoadJob job = _dataJob!;
		job.Publish();
		if (job.Data == null)
		{
			Logger.Error(job.Error ?? $"Failed to load schematic data: {Name}");
			Fail();
			return false;
		}

		SchematicObjectDataList data = job.Data;
		if (_raiseSpawning && Schematic.HasSchematicSpawningSubscribers)
		{
			// Handlers may change the data; the cached instance must stay untouched.
			SchematicSpawningEventArgs ev = new(data.Clone(), Name);
			Schematic.OnSchematicSpawning(ev);
			if (!ev.IsAllowed || ev.Data == null)
			{
				Fail();
				return false;
			}

			data = ev.Data;
		}

		_data = data;
		DirectoryPath ??= data.Path;
		_planJob = job.Plan != null && ReferenceEquals(job.Data, data) ? job : SchematicLoader.LoadPlan(data, _settings);
		_phase = BuildPhase.LoadingPlan;
		return true;
	}

	private bool CompletePlan()
	{
		SchematicLoadJob job = _planJob!;
		job.Publish();
		if (job.Plan == null)
		{
			Logger.Error(job.Error ?? $"Schematic \"{Name}\" could not be planned.");
			Fail();
			return false;
		}

		if (job.RigidbodiesError != null)
			Logger.Warn($"Schematic \"{Name}\": {job.RigidbodiesError}; the schematic stays static.");

		_plan = job.Plan;
		_planWasCached = job.WasCached;
		_phase = BuildPhase.Prepare;
		return true;
	}

	private void Fail()
	{
		_phase = BuildPhase.Failed;

		// A map already listed this schematic (the load was deferred); take it out again.
		MapEditorObject? mapEditorObject = MapEditorObject;
		if (mapEditorObject != null && mapEditorObject.MapName != null && MapUtils.LoadedMaps.TryGetValue(mapEditorObject.MapName, out Serializable.MapSchematic map))
			map.SpawnedObjects.Remove(mapEditorObject);

		Destroy(gameObject);
	}

	private void Prepare()
	{
		ObjectFromId = new Dictionary<int, Transform>(_data.Blocks.Count + 1)
		{
			{ _data.RootObjectId, transform },
		};

		foreach (KeyValuePair<BlockType, int> pair in _plan.Unsupported)
		{
			for (int i = 0; i < pair.Value; i++)
				UnsupportedContent.Skip($"{pair.Key} blocks", SchematicOptimizer.GetUnsupportedReason(pair.Key));
		}

		for (int i = 0; i < _plan.SkippedInvisibleColliders; i++)
			UnsupportedContent.Skip("invisible colliders", "invisible_collider_mode is Skip");

		// Only animated and physics blocks and their subtrees move; everything else stays static.
		_rigidbodies = _plan.Rigidbodies;
		_dynamicRoots.Clear();
		foreach (int id in _rigidbodies.Keys)
			_dynamicRoots.Add(id);

		// Rigidbody entries of blocks other than pickups put the body on the block's anchor (SetUpDynamic).
		_physicsRoots.Clear();
		List<SchematicBlockData> blocks = _data.Blocks;
		for (int i = 0; i < blocks.Count; i++)
		{
			if (_rigidbodies.ContainsKey(blocks[i].ObjectId) && _plan.Outputs[i] != BlockOutput.Pickup)
				_physicsRoots.Add(blocks[i].ObjectId);
		}

		_lightAdmitted = AdmitLights();
		_stack.Clear();
		PushChildren(_data.RootObjectId, transform, false, false);
	}

	/// <summary>
	/// Loads the animator bundles, one block per step.
	/// </summary>
	private bool LoadAnimators(long deadline)
	{
		List<int> animated = _plan.AnimatedBlocks;
		while (_animatorIndex < animated.Count)
		{
			SchematicBlockData block = _data.Blocks[animated[_animatorIndex++]];
			if (TryGetAnimatorController(block.AnimatorName, out RuntimeAnimatorController controller))
			{
				_animators[block.ObjectId] = controller;
				_dynamicRoots.Add(block.ObjectId);
			}

			if (_animatorIndex < animated.Count && Stopwatch.GetTimestamp() >= deadline)
				return false;
		}

		// The controllers stay loaded; the bundles are no longer needed.
		if (_animators.Count > 0)
			AssetBundle.UnloadAllAssetBundles(false);

		return true;
	}

	private bool BuildBlocks(long deadline)
	{
		List<SchematicBlockData> blocks = _data.Blocks;
		while (_stack.Count > 0)
		{
			(int index, Transform parent, bool dynamicParent, bool physicsParent) = _stack.Pop();
			SchematicBlockData block = blocks[index];
			bool dynamic = dynamicParent || _dynamicRoots.Contains(block.ObjectId);
			bool physics = physicsParent || _physicsRoots.Contains(block.ObjectId);
			List<int> children = _plan.GetChildren(block.ObjectId);
			BlockOutput output = _plan.Outputs[index];

			if (output == BlockOutput.Light && !_lightAdmitted[index])
				output = BlockOutput.None;

			if (output == BlockOutput.Pickup && block.Properties != null && block.Properties.TryGetValue("Chance", out object chance) && UnityEngine.Random.Range(0, 101) > Convert.ToSingle(chance, CultureInfo.InvariantCulture))
				output = BlockOutput.None;

			Quaternion localRotation = Quaternion.Euler(block.Rotation);
			Transform? anchor = null;
			if (dynamic || children.Count > 0 || output == BlockOutput.None)
				anchor = CreateAnchor(block, parent, localRotation);

			BlockRecord record = new(block, index, output, parent, anchor, localRotation) { InDynamicSubtree = dynamic, InPhysicsSubtree = physics, Flags = _plan.Flags[index] };
			GameObject? networked = output == BlockOutput.None ? null : CreateNetworked(record);
			if (networked == null && anchor == null)
				anchor = record.Anchor = CreateAnchor(block, parent, localRotation);

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
				PushChildren(block.ObjectId, anchor!, dynamic, physics);

			if (_stack.Count > 0 && Stopwatch.GetTimestamp() >= deadline)
				return false;
		}

		return true;
	}

	/// <summary>
	/// Builds the blocks the optimizer added (merged cubes and quads) as static leaves under the root.
	/// </summary>
	private bool BuildAddedBlocks(long deadline)
	{
		List<SchematicBuildPlan.AddedBlock> added = _plan.AddedBlocks;
		while (_addedIndex < added.Count)
		{
			SchematicBuildPlan.AddedBlock block = added[_addedIndex++];
			BlockRecord record = new(block.Data, -1, block.Output, transform, null, block.Rotation) { Flags = block.Flags, SourceIndex = block.Sources[0] };
			GameObject? networked = CreateNetworked(record);
			if (networked != null)
			{
				networked.name = block.Data.Name;
				record.Networked = networked;
				_records.Add(record);
				_attachedBlocks.Add(networked);
			}

			if (_addedIndex < added.Count && Stopwatch.GetTimestamp() >= deadline)
				return false;
		}

		return true;
	}

	private void Finish(long sliceStart)
	{
		if (_dynamicRoots.Count > 0)
			SetUpDynamic(followAll: false);

		foreach (KeyValuePair<int, RuntimeAnimatorController> pair in _animators)
		{
			if (ObjectFromId.TryGetValue(pair.Key, out Transform anchor))
				anchor.gameObject.AddComponent<Animator>().runtimeAnimatorController = pair.Value;
		}

		ReleaseLightReservations();

		if (_report != null)
		{
			_report.Dispose();
			_report = null;
		}

		double sliceMs = (Stopwatch.GetTimestamp() - sliceStart) * 1000.0 / Stopwatch.Frequency;
		LogSummary(_buildMs + sliceMs, _buildSlices + 1, Math.Max(_worstSliceMs, sliceMs));

		IsSpawned = true;
		_phase = BuildPhase.Spawned;
		Schematic.OnSchematicSpawned(new(this, Name));

		SpawnGroup.Completed += OnSpawnGroupCompleted;
		SpawnGroup.CheckCompleted();
	}

	/// <summary>
	/// Waits for the data and the plan (not for the build).
	/// </summary>
	private void EnsureLoaded()
	{
		if (_stepping)
			return;

		while (_phase is BuildPhase.LoadingData or BuildPhase.LoadingPlan)
		{
			if (_phase == BuildPhase.LoadingData)
				_dataJob!.Wait();
			else
				_planJob!.Wait();

			// A deadline in the past: complete the finished phase and stop.
			if (StepBuild(0))
				break;
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

	private void PushChildren(int objectId, Transform parent, bool dynamic, bool physics)
	{
		List<int> children = _plan.GetChildren(objectId);
		for (int i = children.Count - 1; i >= 0; i--)
			_stack.Push((children[i], parent, dynamic, physics));
	}

	private bool IsStaticBlock(SchematicBlockData block) => SchematicOptimizer.IsStaticBlock(block, _settings.StaticByDefault, _settings.HonorStaticProperty);

	private static Transform CreateAnchor(SchematicBlockData block, Transform parent, Quaternion localRotation)
	{
		Transform anchor = new GameObject(block.Name).transform;
		anchor.SetParent(parent, false);
		anchor.SetLocalPositionAndRotation(block.Position, localRotation);
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
					// The plan parsed type and colour on the worker; only named colours need Unity's parser.
					int source = record.Index >= 0 ? record.Index : record.SourceIndex;
					PrimitiveType primitiveType;
					Color color;
					if (source >= 0 && _plan.ColorKnown[source])
					{
						primitiveType = _plan.PrimitiveTypes[source];
						color = _plan.Colors[source];
					}
					else
					{
						block.GetPrimitive(out primitiveType, out color, out _);
					}

					LabApi.Features.Wrappers.PrimitiveObjectToy? toy = ToyFactory.CreatePrimitive(position, rotation, scale, primitiveType, color, record.Flags, isStatic, SpawnGroup);
					if (toy == null)
						return null;

					record.Toy = toy;
					Link(toy.GameObject, block);
					return toy.GameObject;
				}

			case BlockOutput.Light:
				{
					ReleaseLightReservation();
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

		scratch.SetLocalPositionAndRotation(block.Position, record.LocalRotation);
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

	/// <summary>
	/// Applies <c>max_lights</c> to the plan's lights and reserves the admitted ones until they are created.
	/// </summary>
	private bool[] AdmitLights()
	{
		List<SchematicBlockData> blocks = _data.Blocks;
		List<int> indices = [];
		List<float> strengths = [];
		for (int i = 0; i < blocks.Count; i++)
		{
			if (_plan.Outputs[i] != BlockOutput.Light)
				continue;

			blocks[i].ReadLightStrength(out float intensity, out float range);
			indices.Add(i);
			strengths.Add(intensity * range);
		}

		bool[] admitted = new bool[blocks.Count];
		bool[] result = Budget.AdmitLights(strengths, true, out int admittedCount);
		_reservedLights = admittedCount;
		_reservationGeneration = Budget.Generation;
		for (int i = 0; i < indices.Count; i++)
			admitted[indices[i]] = result[i];

		return admitted;
	}

	private void ReleaseLightReservation()
	{
		if (_reservedLights <= 0)
			return;

		_reservedLights--;
		Budget.ReleaseLights(1, _reservationGeneration);
	}

	private void ReleaseLightReservations()
	{
		if (_reservedLights <= 0)
			return;

		Budget.ReleaseLights(_reservedLights, _reservationGeneration);
		_reservedLights = 0;
	}

	/// <summary>
	/// Makes the toys of animated and physics subtrees (or every toy, for <paramref name="followAll"/>) follow their
	/// anchors through <see cref="SchematicSync"/>, and puts the rigidbodies on their blocks' anchors.
	/// </summary>
	/// <remarks>
	/// Blocks are not children of each other on the server, so a body cannot take its subtree's colliders from the toys as
	/// in ProjectMER. A rigidbody entry therefore goes on the block's anchor, and every collidable primitive of its subtree
	/// gets a server-side copy of its collider on its own anchor, under the body: together they are the body's compound
	/// collider, as the parented toys were in ProjectMER. The toys' own server colliders are switched off (they would
	/// collide with the body they follow), and every toy of the subtree follows its anchor. Entries of pickups apply to the
	/// pickup's own body.
	/// </remarks>
	private void SetUpDynamic(bool followAll)
	{
		if (!TryGetComponent(out SchematicSync sync))
			sync = gameObject.AddComponent<SchematicSync>();

		sync.enabled = true;
		float syncInterval = Config.DynamicToySyncInterval;

		foreach (BlockRecord record in _records)
		{
			if (record.Toy == null || record.Follows || !(followAll || record.InDynamicSubtree))
				continue;

			record.Follows = true;
			record.Toy.SyncInterval = syncInterval;
			sync.AddFollower(record.Anchor!, record.Toy);
			SpawnQueue.DisableWhenReady(record.Toy.Base);

			if (record.Output != BlockOutput.Primitive || (record.Flags & PrimitiveFlags.Collidable) == 0)
				continue;

			if (record.InPhysicsSubtree)
			{
				AddPhysicsCollider(record, sync);
				continue;
			}

			if (!record.Networked!.TryGetComponent(out Rigidbody _))
			{
				// A moving static collider makes PhysX rebuild it; a kinematic body is cheap to move.
				Rigidbody kinematic = record.Networked.AddComponent<Rigidbody>();
				kinematic.isKinematic = true;
				kinematic.useGravity = false;
			}
		}

		// Once: a later switch to dynamic (IsStatic = false) must not reset the bodies.
		if (_rigidbodiesApplied)
			return;

		_rigidbodiesApplied = true;
		foreach (KeyValuePair<int, SerializableRigidbody> pair in _rigidbodies)
		{
			if (_physicsRoots.Contains(pair.Key))
			{
				if (ObjectFromId.TryGetValue(pair.Key, out Transform anchor) && anchor != transform)
				{
					Rigidbody body = ApplyRigidbody(anchor.gameObject, pair.Value);

					// The toys' own colliders exist until they are switched off; a moving body would be knocked by them.
					if (!pair.Value.IsKinematic)
						sync.HoldUntilCollidersOff(body);
				}

				continue;
			}

			foreach (BlockRecord record in _records)
			{
				if (record.Data.ObjectId == pair.Key && record.Output == BlockOutput.Pickup)
				{
					ApplyRigidbody(record.Networked!, pair.Value);
					break;
				}
			}
		}
	}

	/// <summary>
	/// Gives a collidable primitive of a physics subtree its collider on its anchor (part of the body's compound collider)
	/// and switches the toy's own server collider off.
	/// </summary>
	private static void AddPhysicsCollider(BlockRecord record, SchematicSync sync)
	{
		if (record.PhysicsCollider != null || record.Anchor == null || record.Toy is not LabApi.Features.Wrappers.PrimitiveObjectToy toy)
			return;

		// The anchor has the block's world transform; the toy renders the same shape (the flag encoding only mirrors it).
		PrimitiveType type = toy.Type;
		GameObject holder = new("MER physics collider");
		holder.transform.SetParent(record.Anchor, false);
		Mesh mesh = PrimitiveMesh(type);
		if (type is PrimitiveType.Plane or PrimitiveType.Quad)
		{
			// Flat: the toy's collider is a non-convex mesh, which a moving body cannot use. A thin box covers the same face.
			Bounds bounds = mesh.bounds;
			Vector3 size = bounds.size;
			Vector3 lossy = record.Anchor.lossyScale;
			size.x = Mathf.Max(size.x, PhysicsMinThickness / Mathf.Max(Mathf.Abs(lossy.x), 0.0001f));
			size.y = Mathf.Max(size.y, PhysicsMinThickness / Mathf.Max(Mathf.Abs(lossy.y), 0.0001f));
			size.z = Mathf.Max(size.z, PhysicsMinThickness / Mathf.Max(Mathf.Abs(lossy.z), 0.0001f));
			BoxCollider box = holder.AddComponent<BoxCollider>();
			box.center = bounds.center;
			box.size = size;
			record.PhysicsCollider = box;
		}
		else
		{
			// The same convex mesh collider the toy builds (PrimitiveObjectToy.SetPrimitive).
			MeshCollider meshCollider = holder.AddComponent<MeshCollider>();
			meshCollider.sharedMesh = mesh;
			meshCollider.convex = true;
			record.PhysicsCollider = meshCollider;
		}

		sync.KeepServerColliderOff(toy.Base);
	}

	/// <summary>
	/// Gets the mesh Unity uses for a primitive type (a built-in asset, shared).
	/// </summary>
	private static Mesh PrimitiveMesh(PrimitiveType type)
	{
		int index = (int)type;
		Mesh? mesh = PrimitiveMeshes[index];
		if (mesh != null)
			return mesh;

		GameObject temporary = GameObject.CreatePrimitive(type);
		mesh = temporary.GetComponent<MeshFilter>().sharedMesh;
		Destroy(temporary);
		PrimitiveMeshes[index] = mesh;
		return mesh;
	}

	private static Rigidbody ApplyRigidbody(GameObject gameObject, SerializableRigidbody data)
	{
		if (!gameObject.TryGetComponent(out Rigidbody rigidbody))
			rigidbody = gameObject.AddComponent<Rigidbody>();

		rigidbody.isKinematic = data.IsKinematic;
		rigidbody.useGravity = data.UseGravity;
		rigidbody.constraints = data.Constraints;
		rigidbody.mass = data.Mass;
		return rigidbody;
	}

	private void SetStatic(bool value)
	{
		EnsureSpawned();

		// The getter is false as soon as one toy is dynamic (animated or physics parts), so it cannot tell whether every toy
		// was switched already; _forcedDynamic does.
		if (!IsSpawned || (value ? !_forcedDynamic && IsStatic : _forcedDynamic))
			return;

		if (!value)
		{
			_forcedDynamic = true;

			// Merged and duplicate blocks only make sense while nothing moves.
			Unmerge();

			// Every toy needs an anchor to follow.
			foreach (BlockRecord record in _records)
			{
				if (record.Anchor != null || record.Toy == null)
					continue;

				record.Anchor = CreateAnchor(record.Data, record.Parent, record.LocalRotation);
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
			// Kept (disabled) instead of destroyed: Destroy is deferred to the end of the frame, and a switch back to dynamic
			// in the same frame would register its followers on the dying component.
			sync.Clear();
			sync.enabled = false;
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

	/// <summary>
	/// Replaces merged blocks by their sources and networks removed duplicates again, each on its own anchor.
	/// </summary>
	private void Unmerge()
	{
		if (_unmerged || _plan == null || _plan.StaticOnlyDrops.Count == 0)
			return;

		_unmerged = true;
		for (int i = _records.Count - 1; i >= 0; i--)
		{
			BlockRecord record = _records[i];
			if (record.Index >= 0)
				continue;

			if (record.Networked != null)
			{
				_attachedBlocks.Remove(record.Networked);
				SpawnQueue.Destroy(record.Networked);
			}

			_records.RemoveAt(i);
		}

		try
		{
			foreach (int index in _plan.StaticOnlyDrops)
			{
				SchematicBlockData block = _data.Blocks[index];
				if (!ObjectFromId.TryGetValue(block.ObjectId, out Transform anchor) || anchor == null || anchor == transform)
					continue;

				BlockRecord record = new(block, index, BlockOutput.Primitive, anchor.parent, anchor, anchor.localRotation) { Flags = _plan.Flags[index] };
				GameObject? networked = CreateNetworked(record);
				if (networked == null)
					continue;

				networked.name = block.Name;
				record.Networked = networked;
				_records.Add(record);
				_attachedBlocks.Add(networked);
			}
		}
		finally
		{
			ReleaseScratch();
		}
	}

	private void OnRootMoved()
	{
		if (SpawnGroup == null)
			return;

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

	private void LogSummary(double buildMs, int slices, double worstSliceMs)
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

		StringBuilder sb = new();
		sb.Append($"Schematic \"{Name}\": {_data.Blocks.Count} blocks ({_plan.ReachableBlocks} reachable). ProjectMER would network {_plan.ProjectMerNetworked} objects; ");
		sb.Append($"this port networks {_records.Count} (primitives {primitives}, lights {lights}, pickups {pickups}, workstations {workstations}; transparent {transparent}, dynamic {dynamic}). ");
		sb.Append("Optimizer steps:");
		for (int i = 1; i < _plan.Steps.Count; i++)
			sb.Append(i == 1 ? " " : ", ").Append(_plan.Steps[i].Networked);

		sb.Append($" ({_plan.Anchors} blocks not networked: {_plan.Empties} empty, {_plan.DroppedInvisible} invisible, {_plan.DroppedZeroScale} zero-scale, {_plan.LightsCapped} lights over max_lights_per_schematic, {_plan.Duplicates} duplicates, {_plan.MergedSources} merged into {_plan.MergedBlocks}). ");
		sb.Append($"Animated/physics subtrees: {(_dynamicRoots.Count == 0 ? "none" : _dynamicRoots.Count + " roots")}. ");
		sb.Append(_planWasCached ? "Plan cached; " : $"Parse {_plan.ParseMilliseconds:F1} ms and plan {_plan.PlanMilliseconds:F1} ms on a worker thread; ");
		double readySeconds = (Stopwatch.GetTimestamp() - _loadStart) / (double)Stopwatch.Frequency;
		sb.Append($"server build {buildMs:F1} ms over {slices} frame slices (slowest {worstSliceMs:F2} ms), all server objects {readySeconds:F2} s after the load.");
		Logger.Info(sb.ToString());

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
		bool wasSpawned = IsSpawned;
		_phase = BuildPhase.Failed;
		SpawnGroup?.Cancel();
		ReleaseLightReservations();

		if (_report != null)
		{
			UnsupportedContent.Resume(_report);
			_report.Dispose();
			_report = null;
		}

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

		// Spawned and destroyed are raised in pairs; a schematic destroyed while loading was never announced.
		if (wasSpawned)
			Schematic.OnSchematicDestroyed(new(this, Name));
	}

	private static Configs.Config Config => ProjectMER.Singleton.Config!;

	private readonly List<GameObject> _attachedBlocks = [];
	private readonly List<NetworkIdentity> _networkIdentities = [];
	private readonly List<AdminToyBase> _adminToyBases = [];
	private readonly List<BlockRecord> _records = [];
	private readonly Stack<(int Index, Transform Parent, bool Dynamic, bool Physics)> _stack = new();
	private readonly Dictionary<int, RuntimeAnimatorController> _animators = [];

	private static Transform? _scratch;

	private static readonly Mesh?[] PrimitiveMeshes = new Mesh?[6];

	// Thickness of the colliders that stand in for planes and quads in physics subtrees, in metres.
	private const float PhysicsMinThickness = 0.01f;

	private MapEditorObject? _mapEditorObject;
	private readonly HashSet<int> _dynamicRoots = [];
	private readonly HashSet<int> _physicsRoots = [];
	private Dictionary<int, SerializableRigidbody> _rigidbodies = [];
	private bool _forcedDynamic;
	private bool _rigidbodiesApplied;
	private bool _resyncScheduled;
	private float _lastResync = float.NegativeInfinity;

	private SchematicObjectDataList _data;
	private SchematicBuildPlan _plan;
	private OptimizerSettings _settings;
	private BuildPhase _phase;
	private SchematicLoadJob? _dataJob;
	private SchematicLoadJob? _planJob;
	private bool _raiseSpawning;
	private bool _stepping;
	private bool _planWasCached;
	private bool _unmerged;
	private IDisposable? _report;
	private bool[] _lightAdmitted = [];
	private int _reservedLights;
	private int _reservationGeneration;
	private int _animatorIndex;
	private int _addedIndex;
	private long _loadStart;
	private double _buildMs;
	private double _worstSliceMs;
	private int _buildSlices;

	/// <summary>
	/// Where the build is. Ordered: every phase before <see cref="Spawned"/> is still in progress.
	/// </summary>
	private enum BuildPhase : byte
	{
		NotStarted,
		LoadingData,
		LoadingPlan,
		Prepare,
		Animators,
		Blocks,
		AddedBlocks,
		Finish,
		Spawned,
		Failed,
	}

	/// <summary>
	/// A schematic block that produced (or may produce) a networked object.
	/// </summary>
	private sealed class BlockRecord(SchematicBlockData data, int index, BlockOutput output, Transform parent, Transform? anchor, Quaternion localRotation)
	{
		public SchematicBlockData Data { get; } = data;

		/// <summary>
		/// Gets the block index in the data, or -1 for blocks the optimizer added.
		/// </summary>
		public int Index { get; } = index;

		public BlockOutput Output { get; } = output;

		/// <summary>
		/// Gets the transform the block's local transform is relative to.
		/// </summary>
		public Transform Parent { get; } = parent;

		/// <summary>
		/// Gets the local rotation (exact for merged blocks; <see cref="SchematicBlockData.Rotation"/> holds Euler angles).
		/// </summary>
		public Quaternion LocalRotation { get; } = localRotation;

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
		/// Gets or sets whether the block is in the subtree of a rigidbody that is not a pickup's.
		/// </summary>
		public bool InPhysicsSubtree { get; set; }

		/// <summary>
		/// Gets or sets the server-side collider on the block's anchor that stands in for the toy's in a physics subtree.
		/// </summary>
		public Collider? PhysicsCollider { get; set; }

		/// <summary>
		/// Gets or sets the first source block of a merged block (its type and colour), or -1.
		/// </summary>
		public int SourceIndex { get; set; } = -1;

		/// <summary>
		/// Gets or sets whether <see cref="SchematicSync"/> moves the toy (it follows or drives its anchor).
		/// </summary>
		public bool Follows { get; set; }
	}
}
