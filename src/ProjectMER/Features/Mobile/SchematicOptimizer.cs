using System.Diagnostics;
using System.Globalization;
using AdminToys;
using ProjectMER.Configs;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Serializable.Schematics;
using ProjectMER.Features.Serialization;
using UnityEngine;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// What a schematic block produces on the client.
/// </summary>
public enum BlockOutput : byte
{
	/// <summary>
	/// Nothing networked; the block exists only as a server-side anchor GameObject.
	/// </summary>
	None = 0,

	Primitive = 1,

	Light = 2,

	Pickup = 3,

	Workstation = 4,
}

/// <summary>
/// Why a block is not networked (see <see cref="SchematicBuildPlan.Drops"/>).
/// </summary>
public enum BlockDrop : byte
{
	/// <summary>
	/// The block is networked, or it is not reachable from the root.
	/// </summary>
	None = 0,

	/// <summary>
	/// An empty block (group or marker).
	/// </summary>
	Empty,

	/// <summary>
	/// A primitive with <c>PrimitiveFlags.None</c>, or with zero alpha and no collider.
	/// </summary>
	Invisible,

	/// <summary>
	/// A collidable-only primitive while <c>invisible_collider_mode</c> is <c>Skip</c>.
	/// </summary>
	InvisibleCollider,

	/// <summary>
	/// A primitive or pickup with zero scale.
	/// </summary>
	ZeroScale,

	/// <summary>
	/// A block type the Carl Mod client cannot show.
	/// </summary>
	Unsupported,

	/// <summary>
	/// A light beyond <c>max_lights_per_schematic</c>.
	/// </summary>
	LightCap,

	/// <summary>
	/// A static primitive identical to an earlier one (type, transform, colour, flags).
	/// </summary>
	Duplicate,

	/// <summary>
	/// A static cube or quad replaced by a merged block (<see cref="SchematicBuildPlan.AddedBlocks"/>).
	/// </summary>
	Merged,
}

/// <summary>
/// The configuration a <see cref="SchematicBuildPlan"/> is made with. Plans are cached per schematic file and settings.
/// </summary>
/// <param name="OptimizeSchematics">Do not network blocks that produce nothing (<c>optimize_schematics</c>).</param>
/// <param name="SkipInvisibleColliders">Collidable-only primitives are not spawned (<c>invisible_collider_mode: Skip</c>).</param>
/// <param name="MaxLightsPerSchematic">Most lights kept per schematic; negative for no cap.</param>
/// <param name="MergeBlocks">Remove duplicates and merge cubes and quads in static parts (<c>merge_blocks</c>).</param>
/// <param name="StaticByDefault">The <c>static_by_default</c> option.</param>
/// <param name="HonorStaticProperty">The <c>honor_static_property</c> option.</param>
public readonly record struct OptimizerSettings(bool OptimizeSchematics, bool SkipInvisibleColliders, int MaxLightsPerSchematic, bool MergeBlocks, bool StaticByDefault, bool HonorStaticProperty)
{
	/// <summary>
	/// Gets the settings from the plugin configuration.
	/// </summary>
	/// <param name="inMap">Whether the schematic is placed by a map (decides <c>merge_blocks: Maps</c>).</param>
	public static OptimizerSettings FromConfig(bool inMap)
	{
		Config config = ProjectMER.Singleton.Config!;
		bool merge = config.MergeBlocks switch
		{
			BlockMergeMode.All => true,
			BlockMergeMode.Maps => inMap,
			_ => false,
		};

		return new OptimizerSettings(config.OptimizeSchematics, config.InvisibleColliderMode == InvisibleColliderMode.Skip, config.MaxLightsPerSchematic, merge, config.StaticByDefault, config.HonorStaticProperty);
	}
}

/// <summary>
/// The networked object counts after one optimizer step.
/// </summary>
/// <param name="Name">The step.</param>
/// <param name="Primitives">Networked primitives.</param>
/// <param name="Lights">Networked lights.</param>
/// <param name="Pickups">Networked pickups.</param>
/// <param name="Workstations">Networked workstations.</param>
public readonly record struct OptimizerStep(string Name, int Primitives, int Lights, int Pickups, int Workstations)
{
	/// <summary>
	/// Gets the number of networked objects.
	/// </summary>
	public int Networked => Primitives + Lights + Pickups + Workstations;

	/// <summary>
	/// Gets the estimated bytes of their spawn messages for one player (docs/projectmer-port-plan.md §3.1).
	/// </summary>
	public long EstimatedSpawnBytes => Budget.EstimateSpawnBytes(Primitives, Lights, Pickups + Workstations);
}

/// <summary>
/// The simplified, indexed form of a schematic that <see cref="Objects.SchematicObject"/> builds from. Computed off the
/// main thread once per schematic file and <see cref="Settings"/>, and cached with the parse result.
/// </summary>
public sealed class SchematicBuildPlan
{
	internal SchematicBuildPlan(SchematicObjectDataList data, int blockCount, OptimizerSettings settings)
	{
		Data = data;
		Settings = settings;
		Outputs = new BlockOutput[blockCount];
		Flags = new PrimitiveFlags[blockCount];
		Drops = new BlockDrop[blockCount];
		PrimitiveTypes = new PrimitiveType[blockCount];
		Colors = new Color[blockCount];
		ColorKnown = new bool[blockCount];
	}

	/// <summary>
	/// Gets the data the plan was made from.
	/// </summary>
	public SchematicObjectDataList Data { get; }

	/// <summary>
	/// Gets the settings the plan was made with.
	/// </summary>
	public OptimizerSettings Settings { get; }

	/// <summary>
	/// Gets the reachable children of each object id, as indices into <see cref="SchematicObjectDataList.Blocks"/>, in file
	/// order. Children of schematic blocks are not reachable (ProjectMER skips them too).
	/// </summary>
	public Dictionary<int, List<int>> Children { get; } = [];

	/// <summary>
	/// Gets the output of each block index.
	/// </summary>
	public BlockOutput[] Outputs { get; }

	/// <summary>
	/// Gets the networked primitive flags of each primitive block index (kept for merged and duplicate blocks).
	/// </summary>
	public PrimitiveFlags[] Flags { get; }

	/// <summary>
	/// Gets the primitive type of each primitive block index.
	/// </summary>
	public PrimitiveType[] PrimitiveTypes { get; }

	/// <summary>
	/// Gets the colour of each primitive block index, where <see cref="ColorKnown"/> is set (named colours are resolved
	/// on the main thread when the block is built).
	/// </summary>
	public Color[] Colors { get; }

	/// <summary>
	/// Gets whether <see cref="Colors"/> holds the colour of a primitive block index.
	/// </summary>
	public bool[] ColorKnown { get; }

	/// <summary>
	/// Gets why each block index is not networked.
	/// </summary>
	public BlockDrop[] Drops { get; }

	/// <summary>
	/// Gets the indices of reachable blocks with an <c>AnimatorName</c>.
	/// </summary>
	public List<int> AnimatedBlocks { get; } = [];

	/// <summary>
	/// Gets the reachable entries of the schematic's <c>-Rigidbodies.json</c>, by object id.
	/// </summary>
	public Dictionary<int, SerializableRigidbody> Rigidbodies { get; internal set; } = [];

	/// <summary>
	/// Gets the number of blocks reachable from the root.
	/// </summary>
	public int ReachableBlocks { get; internal set; }

	/// <summary>
	/// Gets the number of networked objects ProjectMER would spawn (root, every block, six per triangle).
	/// </summary>
	public int ProjectMerNetworked { get; internal set; }

	/// <summary>
	/// Gets the number of networked objects the plan spawns before the global light and hard caps.
	/// </summary>
	public int Networked { get; internal set; }

	/// <summary>
	/// Gets the number of reachable blocks that are not networked (server-side anchors only), for any reason.
	/// </summary>
	public int Anchors { get; internal set; }

	/// <summary>
	/// Gets the number of empty blocks.
	/// </summary>
	public int Empties { get; internal set; }

	/// <summary>
	/// Gets the number of primitives dropped because they produce nothing visible or collidable (§3.9 step 1).
	/// </summary>
	public int DroppedInvisible { get; internal set; }

	/// <summary>
	/// Gets the number of blocks dropped for zero scale (§3.9 step 1).
	/// </summary>
	public int DroppedZeroScale { get; internal set; }

	/// <summary>
	/// Gets the number of collidable-only primitives skipped by <c>invisible_collider_mode: Skip</c>.
	/// </summary>
	public int SkippedInvisibleColliders { get; internal set; }

	/// <summary>
	/// Gets the number of unsupported blocks per type.
	/// </summary>
	public Dictionary<BlockType, int> Unsupported { get; } = [];

	/// <summary>
	/// Gets the number of networked transparent primitives (alpha below 1, including invisible colliders).
	/// </summary>
	public int Transparent { get; internal set; }

	/// <summary>
	/// Gets the number of light blocks.
	/// </summary>
	public int Lights { get; internal set; }

	/// <summary>
	/// Gets the number of lights dropped by <c>max_lights_per_schematic</c>.
	/// </summary>
	public int LightsCapped { get; internal set; }

	/// <summary>
	/// Gets the number of duplicate primitives removed.
	/// </summary>
	public int Duplicates { get; internal set; }

	/// <summary>
	/// Gets the number of static cubes and quads considered for merging.
	/// </summary>
	public int MergeCandidates { get; internal set; }

	/// <summary>
	/// Gets the number of candidates left alone because their colour is partly transparent (internal faces would show).
	/// </summary>
	public int MergeSkippedTranslucent { get; internal set; }

	/// <summary>
	/// Gets the number of candidates left alone because their hierarchy shears or mirrors them (no exact box).
	/// </summary>
	public int MergeSkippedSheared { get; internal set; }

	/// <summary>
	/// Gets the number of blocks replaced by merged blocks.
	/// </summary>
	public int MergedSources { get; internal set; }

	/// <summary>
	/// Gets the number of merged blocks that replace them.
	/// </summary>
	public int MergedBlocks { get; internal set; }

	/// <summary>
	/// Gets the number of networked blocks in animated or physics subtrees (dynamic toys).
	/// </summary>
	public int DynamicBlocks { get; internal set; }

	/// <summary>
	/// Gets the counts after each optimizer step, starting with ProjectMER's.
	/// </summary>
	public List<OptimizerStep> Steps { get; } = [];

	/// <summary>
	/// Gets the indices of blocks dropped only because they are static (duplicates and merged blocks). Switching a
	/// schematic to dynamic networks them again.
	/// </summary>
	public List<int> StaticOnlyDrops { get; } = [];

	/// <summary>
	/// Gets blocks the optimizer adds, built as static leaves after the file's blocks: merged cubes and quads replacing
	/// the blocks listed in <see cref="AddedBlock.Sources"/>. Their transform is local to the schematic root.
	/// </summary>
	public List<AddedBlock> AddedBlocks { get; } = [];

	/// <summary>
	/// Gets the time the JSON parse took (worker thread), when the plan was made right after it.
	/// </summary>
	public double ParseMilliseconds { get; internal set; }

	/// <summary>
	/// Gets the time the plan took (worker thread).
	/// </summary>
	public double PlanMilliseconds { get; internal set; }

	/// <summary>
	/// A block added by the optimizer.
	/// </summary>
	/// <param name="Data">The block data; its transform is local to the schematic root.</param>
	/// <param name="Output">What it produces (primitives).</param>
	/// <param name="Flags">The networked primitive flags.</param>
	/// <param name="Rotation">The local rotation (exact; <see cref="SchematicBlockData.Rotation"/> holds it as Euler angles).</param>
	/// <param name="Sources">The block indices it replaces.</param>
	public readonly record struct AddedBlock(SchematicBlockData Data, BlockOutput Output, PrimitiveFlags Flags, Quaternion Rotation, int[] Sources);

	/// <summary>
	/// Gets the children of an object id, or an empty list.
	/// </summary>
	public List<int> GetChildren(int objectId) => Children.TryGetValue(objectId, out List<int> children) ? children : Empty;

	private static readonly List<int> Empty = [];
}

/// <summary>
/// Simplifies schematics before they are built (docs/projectmer-port-plan.md §3.9). Pure data: runs on a worker thread.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Blocks that produce nothing on the client are not networked (empties, <c>PrimitiveFlags.None</c>, zero-alpha
/// primitives without collider, zero scale, unsupported types). They stay server-side anchors, which plugins find through
/// <c>AttachedBlocks</c>.</item>
/// <item>At most <c>max_lights_per_schematic</c> lights, strongest (intensity × range) first.</item>
/// <item>With <c>merge_blocks</c>: exact duplicates of static opaque (or fully invisible) primitives are removed; stacked
/// translucent copies blend into a deeper tint, so they stay.</item>
/// <item>With <c>merge_blocks</c>: static opaque (or fully invisible) cubes that share a full face, and coplanar quads
/// facing the same way that share a full edge, are merged greedily per group of equal rotation (up to the cube's or
/// quad's symmetries), colour, flags and transparency. The merged block covers exactly the union of its sources.</item>
/// </list>
/// </remarks>
public static class SchematicOptimizer
{
	/// <summary>
	/// Faces closer than this (metres) count as touching; cross-sections must agree within it.
	/// </summary>
	public const double Tolerance = 0.001;

	private const double FrameQuantum = 1e5;

	private const double DuplicateQuantum = 1e4;

	private static readonly Mat3[] CubeSymmetries = CreateCubeSymmetries();

	private static readonly Mat3[] QuadSymmetries =
	[
		Mat3.Identity,
		new Mat3(0, -1, 0, 1, 0, 0, 0, 0, 1),
		new Mat3(-1, 0, 0, 0, -1, 0, 0, 0, 1),
		new Mat3(0, 1, 0, -1, 0, 0, 0, 0, 1),
	];

	private static readonly int[][] CubeAxisOrders = [[0, 1, 2], [1, 2, 0], [2, 0, 1]];

	private static readonly int[][] QuadAxisOrders = [[0, 1], [1, 0]];

	/// <summary>
	/// Gets the plan of a schematic synchronously: the cached plan when <paramref name="data"/> is the cached parse result,
	/// otherwise a new one. Prefer <see cref="Objects.SchematicObject"/>, which plans on a worker thread.
	/// </summary>
	/// <param name="data">The schematic data.</param>
	/// <param name="inMap">Whether to plan as for a schematic placed by a map (<c>merge_blocks: Maps</c>).</param>
	public static SchematicBuildPlan GetPlan(SchematicObjectDataList data, bool inMap = false)
	{
		SchematicLoadJob job = SchematicLoader.LoadPlan(data, OptimizerSettings.FromConfig(inMap));
		job.Wait();
		job.Publish();
		if (job.Plan == null)
			throw new InvalidOperationException(job.Error ?? "The schematic could not be planned.");

		return job.Plan;
	}

	/// <summary>
	/// Builds the plan of a schematic. Does not touch Unity or plugin state, so it may run on any thread.
	/// </summary>
	/// <param name="data">The schematic data (read only).</param>
	/// <param name="settings">The optimizer settings.</param>
	/// <param name="rigidbodies">The schematic's rigidbody entries, if it has a <c>-Rigidbodies.json</c>.</param>
	public static SchematicBuildPlan Build(SchematicObjectDataList data, OptimizerSettings settings, Dictionary<int, SerializableRigidbody>? rigidbodies)
	{
		long start = Stopwatch.GetTimestamp();
		List<SchematicBlockData> blocks = data.Blocks;
		int count = blocks.Count;
		SchematicBuildPlan plan = new(data, count, settings);
		BlockInfo[] info = new BlockInfo[count];

		// Children by parent id, in file order.
		Dictionary<int, List<int>> allChildren = new(count);
		int minId = data.RootObjectId;
		for (int i = 0; i < count; i++)
		{
			SchematicBlockData block = blocks[i];
			if (block == null || block.ObjectId == data.RootObjectId)
				continue;

			if (!allChildren.TryGetValue(block.ParentId, out List<int> list))
				allChildren[block.ParentId] = list = [];

			list.Add(i);
			minId = Math.Min(minId, block.ObjectId);
		}

		plan.ProjectMerNetworked = 1; // ProjectMER's root is a networked primitive.
		int baselineLights = 0, baselinePickups = 0, baselineWorkstations = 0;

		Stack<(int Index, int Parent)> stack = new();
		PushChildren(stack, allChildren, data.RootObjectId, -1);
		while (stack.Count > 0)
		{
			(int index, int parent) = stack.Pop();
			SchematicBlockData block = blocks[index];
			plan.ReachableBlocks++;
			plan.ProjectMerNetworked += block.BlockType == BlockType.Triangle ? 6 : 1;
			switch (block.BlockType)
			{
				case BlockType.Light:
					baselineLights++;
					break;
				case BlockType.Pickup:
					baselinePickups++;
					break;
				case BlockType.Workstation:
					baselineWorkstations++;
					break;
			}

			if (!plan.Children.TryGetValue(block.ParentId, out List<int> siblings))
				plan.Children[block.ParentId] = siblings = [];

			siblings.Add(index);

			bool animated = !string.IsNullOrEmpty(block.AnimatorName) && block.BlockType != BlockType.Light;
			if (animated)
				plan.AnimatedBlocks.Add(index);

			ref BlockInfo current = ref info[index];
			current.Reached = true;
			Mat3 parentMatrix = parent < 0 ? Mat3.Identity : info[parent].Matrix;
			Mat3 parentRotation = parent < 0 ? Mat3.Identity : info[parent].Rotation;
			Vec3 parentPosition = parent < 0 ? default : info[parent].Position;
			Mat3 localRotation = Mat3.FromEuler(block.Rotation);
			Vector3 scale = block.EffectiveScale;
			current.Matrix = parentMatrix * localRotation * Mat3.Scale(scale.x, scale.y, scale.z);
			current.Rotation = parentRotation * localRotation;
			current.Position = parentPosition + (parentMatrix * new Vec3(block.Position));
			current.Dynamic = (parent >= 0 && info[parent].Dynamic) || animated || (rigidbodies != null && rigidbodies.ContainsKey(block.ObjectId));
			current.MirroredParent = parent >= 0 && (info[parent].MirroredParent || HasNegative(blocks[parent].EffectiveScale));

			BlockOutput output = Classify(block, index, plan, settings, ref current);
			plan.Outputs[index] = output;

			// ProjectMER does not build the children of schematic blocks (they would be spawned twice).
			if (block.BlockType != BlockType.Schematic)
				PushChildren(stack, allChildren, block.ObjectId, index);
		}

		// The stack visits children in reverse; restore file order.
		foreach (List<int> list in plan.Children.Values)
			list.Sort();

		if (rigidbodies != null && rigidbodies.Count > 0)
		{
			HashSet<int> reachable = [];
			for (int i = 0; i < count; i++)
			{
				if (info[i].Reached)
					reachable.Add(blocks[i].ObjectId);
			}

			foreach (KeyValuePair<int, SerializableRigidbody> pair in rigidbodies)
			{
				if (reachable.Contains(pair.Key))
					plan.Rigidbodies[pair.Key] = pair.Value;
			}
		}

		plan.Steps.Add(new OptimizerStep("ProjectMER", plan.ProjectMerNetworked - baselineLights - baselinePickups - baselineWorkstations, baselineLights, baselinePickups, baselineWorkstations));
		plan.Steps.Add(Snapshot(plan, "Not networked: empty, invisible, zero-scale and unsupported blocks"));

		CapLights(plan, blocks);
		plan.Steps.Add(Snapshot(plan, $"Light cap (max_lights_per_schematic {settings.MaxLightsPerSchematic})"));

		if (settings.MergeBlocks)
		{
			RemoveDuplicates(plan, blocks, info);
			plan.Steps.Add(Snapshot(plan, "Exact duplicates removed"));

			MergeBlocks(plan, blocks, info, minId);
			plan.Steps.Add(Snapshot(plan, "Cubes and quads merged"));
		}

		Finish(plan, blocks, info);
		plan.PlanMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
		return plan;
	}

	/// <summary>
	/// Gets the reason an unsupported block type is skipped.
	/// </summary>
	public static string GetUnsupportedReason(BlockType blockType) => blockType switch
	{
		BlockType.Text => "TextToy is not available in the Carl Mod client",
		BlockType.Interactable => "InvisibleInteractableToy is not available in the Carl Mod client",
		BlockType.Waypoint => "WaypointToy is not available in the Carl Mod client",
		BlockType.Triangle => "triangles need sheared (parented) quads, which Carl Mod clients cannot show",
		BlockType.Schematic or BlockType.Teleport or BlockType.Locker => "ProjectMER does not build this block type either",
		_ => "unknown block type",
	};

	/// <summary>
	/// Decides whether a block outside animated and physics subtrees spawns as a static toy.
	/// </summary>
	/// <remarks>
	/// With <c>static_by_default</c> (the default) every such block is static: exporters write <c>"Static": false</c> for
	/// blocks that never move, so the property only counts with <c>honor_static_property</c>. Without
	/// <c>static_by_default</c>, ProjectMER's rule applies (only <c>"Static": true</c> is static).
	/// </remarks>
	public static bool IsStaticBlock(SchematicBlockData block, bool staticByDefault, bool honorStaticProperty)
	{
		bool? property = block.StaticProperty;
		if (staticByDefault)
			return !(honorStaticProperty && property == false);

		return property == true;
	}

	private static void PushChildren(Stack<(int Index, int Parent)> stack, Dictionary<int, List<int>> children, int parentId, int parentIndex)
	{
		if (!children.TryGetValue(parentId, out List<int> list))
			return;

		for (int i = list.Count - 1; i >= 0; i--)
			stack.Push((list[i], parentIndex));
	}

	private static bool HasNegative(Vector3 scale) => scale.x < 0f || scale.y < 0f || scale.z < 0f;

	private static BlockOutput Classify(SchematicBlockData block, int index, SchematicBuildPlan plan, OptimizerSettings settings, ref BlockInfo info)
	{
		bool optimize = settings.OptimizeSchematics;
		switch (block.BlockType)
		{
			case BlockType.Empty:
				plan.Empties++;
				return Drop(plan, index, BlockDrop.Empty);

			case BlockType.Primitive:
				{
					block.ReadPrimitive(out PrimitiveType type, out string colorText, out PrimitiveFlags flags);
					info.Type = type;
					plan.PrimitiveTypes[index] = type;
					info.ColorText = colorText;
					info.ColorParsed = SchematicColor.TryParse(colorText, out info.Color);
					plan.Colors[index] = info.Color;
					plan.ColorKnown[index] = info.ColorParsed;
					float alpha = info.ColorParsed ? info.Color.a : 1f; // Named colours are opaque.

					if (block.Scale == Vector3.zero && optimize)
					{
						plan.DroppedZeroScale++;
						return Drop(plan, index, BlockDrop.ZeroScale);
					}

					PrimitiveFlags networked = flags & (PrimitiveFlags.Visible | PrimitiveFlags.Collidable);
					if (networked == PrimitiveFlags.None)
					{
						plan.DroppedInvisible++;
						return Drop(plan, index, BlockDrop.Invisible);
					}

					if (networked == PrimitiveFlags.Collidable && settings.SkipInvisibleColliders)
					{
						plan.SkippedInvisibleColliders++;
						return Drop(plan, index, BlockDrop.InvisibleCollider);
					}

					if (optimize && (networked & PrimitiveFlags.Collidable) == 0 && alpha <= 0f)
					{
						plan.DroppedInvisible++;
						return Drop(plan, index, BlockDrop.Invisible);
					}

					info.Transparent = alpha < 1f || (networked & PrimitiveFlags.Visible) == 0;
					plan.Flags[index] = networked;
					return BlockOutput.Primitive;
				}

			case BlockType.Light:
				plan.Lights++;
				return BlockOutput.Light;

			case BlockType.Pickup:
				if (block.Scale == Vector3.zero && optimize)
				{
					plan.DroppedZeroScale++;
					return Drop(plan, index, BlockDrop.ZeroScale);
				}

				return BlockOutput.Pickup;

			case BlockType.Workstation:
				return BlockOutput.Workstation;

			default:
				plan.Unsupported.TryGetValue(block.BlockType, out int count);
				plan.Unsupported[block.BlockType] = count + 1;
				return Drop(plan, index, BlockDrop.Unsupported);
		}
	}

	private static BlockOutput Drop(SchematicBuildPlan plan, int index, BlockDrop reason)
	{
		plan.Drops[index] = reason;
		return BlockOutput.None;
	}

	private static OptimizerStep Snapshot(SchematicBuildPlan plan, string name)
	{
		int primitives = plan.AddedBlocks.Count, lights = 0, pickups = 0, workstations = 0;
		foreach (BlockOutput output in plan.Outputs)
		{
			switch (output)
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
		}

		return new OptimizerStep(name, primitives, lights, pickups, workstations);
	}

	private static void CapLights(SchematicBuildPlan plan, List<SchematicBlockData> blocks)
	{
		int cap = plan.Settings.MaxLightsPerSchematic;
		if (cap < 0)
			return;

		List<int> lights = [];
		List<float> strengths = [];
		for (int i = 0; i < blocks.Count; i++)
		{
			if (plan.Outputs[i] != BlockOutput.Light)
				continue;

			blocks[i].ReadLightStrength(out float intensity, out float range);
			lights.Add(i);
			strengths.Add(intensity * range);
		}

		if (lights.Count <= cap)
			return;

		int[] order = new int[lights.Count];
		for (int i = 0; i < order.Length; i++)
			order[i] = i;

		// Strongest first; equal strengths keep file order.
		Array.Sort(order, (a, b) =>
		{
			int byStrength = strengths[b].CompareTo(strengths[a]);
			return byStrength != 0 ? byStrength : a.CompareTo(b);
		});

		for (int k = cap; k < order.Length; k++)
		{
			int index = lights[order[k]];
			plan.Outputs[index] = Drop(plan, index, BlockDrop.LightCap);
			plan.LightsCapped++;
		}
	}

	private static bool IsMergeable(SchematicBuildPlan plan, List<SchematicBlockData> blocks, BlockInfo[] info, int index) =>
		plan.Outputs[index] == BlockOutput.Primitive &&
		!info[index].Dynamic &&
		IsStaticBlock(blocks[index], plan.Settings.StaticByDefault, plan.Settings.HonorStaticProperty);

	private static void RemoveDuplicates(SchematicBuildPlan plan, List<SchematicBlockData> blocks, BlockInfo[] info)
	{
		HashSet<BlockKey> seen = [];
		for (int i = 0; i < blocks.Count; i++)
		{
			if (!IsMergeable(plan, blocks, info, i))
				continue;

			ref BlockInfo block = ref info[i];

			// Stacked translucent copies blend once each (a deeper tint); dropping one would lighten the block.
			float alpha = block.ColorParsed ? block.Color.a : 1f;
			if ((plan.Flags[i] & PrimitiveFlags.Visible) != 0 && alpha > 0f && alpha < 1f)
				continue;

			Mat3 m = block.Matrix;
			long[] values =
			[
				(long)block.Type, (long)plan.Flags[i],
				Quantize(block.Position.X, DuplicateQuantum), Quantize(block.Position.Y, DuplicateQuantum), Quantize(block.Position.Z, DuplicateQuantum),
				Quantize(m.M00, DuplicateQuantum), Quantize(m.M01, DuplicateQuantum), Quantize(m.M02, DuplicateQuantum),
				Quantize(m.M10, DuplicateQuantum), Quantize(m.M11, DuplicateQuantum), Quantize(m.M12, DuplicateQuantum),
				Quantize(m.M20, DuplicateQuantum), Quantize(m.M21, DuplicateQuantum), Quantize(m.M22, DuplicateQuantum),
			];

			if (seen.Add(new BlockKey(values, ColorKey(ref block))))
				continue;

			plan.Outputs[i] = Drop(plan, i, BlockDrop.Duplicate);
			plan.StaticOnlyDrops.Add(i);
			plan.Duplicates++;
		}
	}

	private static void MergeBlocks(SchematicBuildPlan plan, List<SchematicBlockData> blocks, BlockInfo[] info, int minId)
	{
		Dictionary<BlockKey, MergeGroup> groups = [];
		List<MergeGroup> ordered = [];

		for (int i = 0; i < blocks.Count; i++)
		{
			if (!IsMergeable(plan, blocks, info, i))
				continue;

			ref BlockInfo block = ref info[i];
			bool quad = block.Type == PrimitiveType.Quad;
			if (!quad && block.Type != PrimitiveType.Cube)
				continue;

			PrimitiveFlags flags = plan.Flags[i];
			float alpha = block.ColorParsed ? block.Color.a : 1f;
			bool invisible = (flags & PrimitiveFlags.Visible) == 0 || alpha <= 0f;
			if (!invisible && alpha < 1f)
			{
				// Faces between translucent blocks are visible; merging would remove them.
				plan.MergeSkippedTranslucent++;
				continue;
			}

			// The toy renders (Unity world rotation) x diag(lossyScale); that equals the block's matrix only when the
			// rotation-relative matrix is diagonal (no shear). Under a mirrored (negatively scaled) parent Unity's world
			// rotation is not the product of the local rotations, so such blocks are left alone.
			if (block.MirroredParent)
			{
				plan.MergeSkippedSheared++;
				continue;
			}

			Mat3 rotation = block.Rotation;
			Mat3 relative = rotation.Transposed() * block.Matrix;
			if (!relative.IsDiagonal())
			{
				plan.MergeSkippedSheared++;
				continue;
			}

			if (quad)
			{
				// Quads are one-sided: frame them so the visible side faces local -Z (positive z scale).
				if (relative.M22 < 0)
				{
					rotation *= Mat3.RotationX180;
					relative = rotation.Transposed() * block.Matrix;
				}

				if (Math.Abs(relative.M00) < 1e-6 || Math.Abs(relative.M11) < 1e-6 || relative.M22 < 1e-9)
					continue;
			}
			else if (Math.Abs(relative.M00) < 1e-6 || Math.Abs(relative.M11) < 1e-6 || Math.Abs(relative.M22) < 1e-6)
			{
				continue;
			}

			Mat3 frame = Canonical(rotation, quad ? QuadSymmetries : CubeSymmetries, out long[] frameKey);
			long[] values = new long[12];
			values[0] = (long)block.Type;
			values[1] = (long)flags;
			values[2] = invisible ? 1 : 0;
			Array.Copy(frameKey, 0, values, 3, 9);
			BlockKey key = new(values, invisible ? string.Empty : ColorKey(ref block));

			if (!groups.TryGetValue(key, out MergeGroup group))
			{
				group = new MergeGroup(frame, quad, i);
				groups[key] = group;
				ordered.Add(group);
			}

			// The block's box in the group frame. Its own frame may differ from the group frame by a symmetry (a signed
			// axis permutation), so take the extent along each group axis from the whole row, not the diagonal.
			Vec3 center = group.Frame.Transposed() * block.Position;
			Mat3 inFrame = group.Frame.Transposed() * block.Matrix;
			MergeBox box = new([i]);
			double ex = Math.Abs(inFrame.M00) + Math.Abs(inFrame.M01) + (quad ? 0 : Math.Abs(inFrame.M02));
			double ey = Math.Abs(inFrame.M10) + Math.Abs(inFrame.M11) + (quad ? 0 : Math.Abs(inFrame.M12));
			double ez = quad ? 0 : Math.Abs(inFrame.M20) + Math.Abs(inFrame.M21) + Math.Abs(inFrame.M22);
			box.Min[0] = center.X - (ex / 2);
			box.Max[0] = center.X + (ex / 2);
			box.Min[1] = center.Y - (ey / 2);
			box.Max[1] = center.Y + (ey / 2);
			box.Min[2] = center.Z - (ez / 2);
			box.Max[2] = center.Z + (ez / 2);
			group.Boxes.Add(box);
			plan.MergeCandidates++;
		}

		int nextId = Math.Min(minId, 0) - 1;
		foreach (MergeGroup group in ordered)
		{
			if (group.Boxes.Count < 2)
				continue;

			List<MergeBox> merged = Merge(group.Boxes, group.Quad);
			if (merged.Count == group.Boxes.Count)
				continue;

			ref BlockInfo first = ref info[group.FirstIndex];
			Quaternion rotation = group.Frame.ToQuaternion();
			Vector3 euler = group.Frame.ToEuler();
			foreach (MergeBox box in merged)
			{
				if (box.Sources.Count < 2)
					continue;

				Vec3 center = new((box.Min[0] + box.Max[0]) / 2, (box.Min[1] + box.Max[1]) / 2, (box.Min[2] + box.Max[2]) / 2);
				Vector3 scale = group.Quad
					? new Vector3((float)(box.Max[0] - box.Min[0]), (float)(box.Max[1] - box.Min[1]), 1f)
					: new Vector3((float)(box.Max[0] - box.Min[0]), (float)(box.Max[1] - box.Min[1]), (float)(box.Max[2] - box.Min[2]));

				PrimitiveFlags flags = plan.Flags[group.FirstIndex];
				SchematicBlockData data = new()
				{
					Name = $"Merged {(group.Quad ? "Quad" : "Cube")} ({box.Sources.Count} blocks)",
					ObjectId = nextId--,
					ParentId = plan.Data.RootObjectId,
					Position = (group.Frame * center).ToVector3(),
					Rotation = euler,
					Scale = scale,
					BlockType = BlockType.Primitive,
					Properties = new Dictionary<string, object>
					{
						["PrimitiveType"] = (long)first.Type,
						["Color"] = first.ColorText,
						["PrimitiveFlags"] = (long)flags,
						["Static"] = true,
					},
				};

				int[] sources = box.Sources.ToArray();
				Array.Sort(sources);
				plan.AddedBlocks.Add(new SchematicBuildPlan.AddedBlock(data, BlockOutput.Primitive, flags, rotation, sources));
				plan.MergedBlocks++;
				plan.MergedSources += sources.Length;
				foreach (int source in sources)
				{
					plan.Outputs[source] = Drop(plan, source, BlockDrop.Merged);
					plan.StaticOnlyDrops.Add(source);
				}
			}
		}
	}

	/// <summary>
	/// Merges boxes greedily: in each axis order, sweeps the axes until no two boxes with the same cross-section touch or
	/// overlap along the swept axis; keeps the order with the fewest boxes.
	/// </summary>
	private static List<MergeBox> Merge(List<MergeBox> input, bool quad)
	{
		List<MergeBox>? best = null;
		foreach (int[] order in quad ? QuadAxisOrders : CubeAxisOrders)
		{
			List<MergeBox> work = new(input.Count);
			foreach (MergeBox box in input)
				work.Add(box.Clone());

			bool changed;
			do
			{
				changed = false;
				foreach (int axis in order)
					changed |= Sweep(work, axis);
			}
			while (changed);

			if (best == null || work.Count < best.Count)
				best = work;
		}

		return best!;
	}

	private static bool Sweep(List<MergeBox> boxes, int axis)
	{
		if (boxes.Count < 2)
			return false;

		int a = (axis + 1) % 3;
		int b = (axis + 2) % 3;
		Dictionary<(long, long, long, long), List<MergeBox>> buckets = [];
		List<List<MergeBox>> order = [];
		foreach (MergeBox box in boxes)
		{
			(long, long, long, long) key = (Quantize(box.Min[a], 1 / Tolerance), Quantize(box.Max[a], 1 / Tolerance), Quantize(box.Min[b], 1 / Tolerance), Quantize(box.Max[b], 1 / Tolerance));
			if (!buckets.TryGetValue(key, out List<MergeBox> list))
			{
				buckets[key] = list = [];
				order.Add(list);
			}

			list.Add(box);
		}

		bool changed = false;
		boxes.Clear();
		foreach (List<MergeBox> list in order)
		{
			if (list.Count == 1)
			{
				boxes.Add(list[0]);
				continue;
			}

			list.Sort((x, y) =>
			{
				int byMin = x.Min[axis].CompareTo(y.Min[axis]);
				return byMin != 0 ? byMin : x.Sources[0].CompareTo(y.Sources[0]);
			});

			MergeBox current = list[0];
			for (int i = 1; i < list.Count; i++)
			{
				MergeBox next = list[i];
				if (next.Min[axis] <= current.Max[axis] + Tolerance)
				{
					current.Max[axis] = Math.Max(current.Max[axis], next.Max[axis]);
					current.Sources.AddRange(next.Sources);
					changed = true;
					continue;
				}

				boxes.Add(current);
				current = next;
			}

			boxes.Add(current);
		}

		return changed;
	}

	private static void Finish(SchematicBuildPlan plan, List<SchematicBlockData> blocks, BlockInfo[] info)
	{
		int networked = plan.AddedBlocks.Count, anchors = 0, transparent = 0, dynamic = 0;
		for (int i = 0; i < blocks.Count; i++)
		{
			if (!info[i].Reached)
				continue;

			if (plan.Outputs[i] == BlockOutput.None)
			{
				anchors++;
				continue;
			}

			networked++;
			if (info[i].Dynamic)
				dynamic++;

			if (plan.Outputs[i] == BlockOutput.Primitive && info[i].Transparent)
				transparent++;
		}

		foreach (SchematicBuildPlan.AddedBlock added in plan.AddedBlocks)
		{
			if ((added.Flags & PrimitiveFlags.Visible) == 0 || (info[added.Sources[0]].ColorParsed && info[added.Sources[0]].Color.a < 1f))
				transparent++;
		}

		plan.Networked = networked;
		plan.Anchors = anchors;
		plan.Transparent = transparent;
		plan.DynamicBlocks = dynamic;
	}

	private static Mat3 Canonical(Mat3 rotation, Mat3[] symmetries, out long[] key)
	{
		Mat3 best = default;
		long[] bestKey = new long[9];
		long[] candidate = new long[9];
		bool found = false;
		foreach (Mat3 symmetry in symmetries)
		{
			Mat3 frame = rotation * symmetry;
			frame.Quantize(candidate, FrameQuantum);
			if (found && Compare(candidate, bestKey) >= 0)
				continue;

			found = true;
			best = frame;
			(bestKey, candidate) = (candidate, bestKey);
		}

		key = bestKey;
		return best;
	}

	private static int Compare(long[] a, long[] b)
	{
		for (int i = 0; i < a.Length; i++)
		{
			int c = a[i].CompareTo(b[i]);
			if (c != 0)
				return c;
		}

		return 0;
	}

	private static string ColorKey(ref BlockInfo block)
	{
		if (!block.ColorParsed)
			return "name:" + block.ColorText.ToLowerInvariant();

		Color c = block.Color;
		return string.Concat(
			Quantize(c.r, 1e5).ToString(CultureInfo.InvariantCulture), ",",
			Quantize(c.g, 1e5).ToString(CultureInfo.InvariantCulture), ",",
			Quantize(c.b, 1e5).ToString(CultureInfo.InvariantCulture), ",",
			Quantize(c.a, 1e5).ToString(CultureInfo.InvariantCulture));
	}

	private static long Quantize(double value, double scale) => (long)Math.Round(value * scale);

	private static Mat3[] CreateCubeSymmetries()
	{
		List<Mat3> result = [];
		int[][] permutations = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
		foreach (int[] permutation in permutations)
		{
			for (int signs = 0; signs < 8; signs++)
			{
				double[] m = new double[9];
				for (int column = 0; column < 3; column++)
					m[(permutation[column] * 3) + column] = (signs & (1 << column)) != 0 ? -1 : 1;

				Mat3 matrix = new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8]);
				if (matrix.Determinant() > 0)
					result.Add(matrix);
			}
		}

		return result.ToArray();
	}

	/// <summary>
	/// Per-block data the plan computes: root-space transform and primitive properties.
	/// </summary>
	private struct BlockInfo
	{
		public bool Reached;

		public bool Dynamic;

		public bool Transparent;

		/// <summary>
		/// Whether an ancestor has a negative scale component.
		/// </summary>
		public bool MirroredParent;

		/// <summary>
		/// The root-space matrix (rotation and scale of the whole chain).
		/// </summary>
		public Mat3 Matrix;

		/// <summary>
		/// The root-space rotation as Unity reports it (product of the local rotations).
		/// </summary>
		public Mat3 Rotation;

		public Vec3 Position;

		public PrimitiveType Type;

		public string ColorText;

		public bool ColorParsed;

		public Color Color;
	}

	private sealed class MergeGroup(Mat3 frame, bool quad, int firstIndex)
	{
		public readonly Mat3 Frame = frame;

		public readonly bool Quad = quad;

		public readonly int FirstIndex = firstIndex;

		public readonly List<MergeBox> Boxes = [];
	}

	private sealed class MergeBox(List<int> sources)
	{
		public readonly double[] Min = new double[3];

		public readonly double[] Max = new double[3];

		public readonly List<int> Sources = sources;

		public MergeBox Clone()
		{
			MergeBox clone = new([.. Sources]);
			Array.Copy(Min, clone.Min, 3);
			Array.Copy(Max, clone.Max, 3);
			return clone;
		}
	}

	private readonly struct BlockKey(long[] values, string text) : IEquatable<BlockKey>
	{
		private readonly long[] _values = values;

		private readonly string _text = text;

		public bool Equals(BlockKey other)
		{
			if (_text != other._text || _values.Length != other._values.Length)
				return false;

			for (int i = 0; i < _values.Length; i++)
			{
				if (_values[i] != other._values[i])
					return false;
			}

			return true;
		}

		public override bool Equals(object? obj) => obj is BlockKey other && Equals(other);

		public override int GetHashCode()
		{
			int hash = _text.GetHashCode();
			foreach (long value in _values)
				hash = (hash * 31) + value.GetHashCode();

			return hash;
		}
	}

	/// <summary>
	/// A double-precision vector (the plan avoids Unity math so it can run off the main thread).
	/// </summary>
	private readonly struct Vec3(double x, double y, double z)
	{
		public readonly double X = x;

		public readonly double Y = y;

		public readonly double Z = z;

		public Vec3(Vector3 v)
			: this(v.x, v.y, v.z)
		{
		}

		public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

		public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
	}

	/// <summary>
	/// A double-precision 3x3 matrix, row-major (<c>Mrc</c> = row r, column c); columns are the images of the axes.
	/// </summary>
	private readonly struct Mat3(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
	{
		public static readonly Mat3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

		public static readonly Mat3 RotationX180 = new(1, 0, 0, 0, -1, 0, 0, 0, -1);

		public readonly double M00 = m00, M01 = m01, M02 = m02, M10 = m10, M11 = m11, M12 = m12, M20 = m20, M21 = m21, M22 = m22;

		public static Mat3 Scale(double x, double y, double z) => new(x, 0, 0, 0, y, 0, 0, 0, z);

		/// <summary>
		/// Unity's <c>Quaternion.Euler</c>: rotates about Z, then X, then Y (R = Ry · Rx · Rz).
		/// </summary>
		public static Mat3 FromEuler(Vector3 degrees)
		{
			const double toRadians = Math.PI / 180.0;
			double x = degrees.x * toRadians, y = degrees.y * toRadians, z = degrees.z * toRadians;
			double cx = Math.Cos(x), sx = Math.Sin(x), cy = Math.Cos(y), sy = Math.Sin(y), cz = Math.Cos(z), sz = Math.Sin(z);
			Mat3 rx = new(1, 0, 0, 0, cx, -sx, 0, sx, cx);
			Mat3 ry = new(cy, 0, sy, 0, 1, 0, -sy, 0, cy);
			Mat3 rz = new(cz, -sz, 0, sz, cz, 0, 0, 0, 1);
			return ry * rx * rz;
		}

		public static Mat3 operator *(Mat3 a, Mat3 b) => new(
			(a.M00 * b.M00) + (a.M01 * b.M10) + (a.M02 * b.M20), (a.M00 * b.M01) + (a.M01 * b.M11) + (a.M02 * b.M21), (a.M00 * b.M02) + (a.M01 * b.M12) + (a.M02 * b.M22),
			(a.M10 * b.M00) + (a.M11 * b.M10) + (a.M12 * b.M20), (a.M10 * b.M01) + (a.M11 * b.M11) + (a.M12 * b.M21), (a.M10 * b.M02) + (a.M11 * b.M12) + (a.M12 * b.M22),
			(a.M20 * b.M00) + (a.M21 * b.M10) + (a.M22 * b.M20), (a.M20 * b.M01) + (a.M21 * b.M11) + (a.M22 * b.M21), (a.M20 * b.M02) + (a.M21 * b.M12) + (a.M22 * b.M22));

		public static Vec3 operator *(Mat3 m, Vec3 v) => new(
			(m.M00 * v.X) + (m.M01 * v.Y) + (m.M02 * v.Z),
			(m.M10 * v.X) + (m.M11 * v.Y) + (m.M12 * v.Z),
			(m.M20 * v.X) + (m.M21 * v.Y) + (m.M22 * v.Z));

		public Mat3 Transposed() => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);

		public double Determinant() =>
			(M00 * ((M11 * M22) - (M12 * M21))) - (M01 * ((M10 * M22) - (M12 * M20))) + (M02 * ((M10 * M21) - (M11 * M20)));

		/// <summary>
		/// Gets whether the off-diagonal entries are negligible relative to the largest entry.
		/// </summary>
		public bool IsDiagonal()
		{
			double max = Math.Max(Math.Max(Math.Abs(M00), Math.Abs(M11)), Math.Abs(M22));
			double limit = (max * 1e-6) + 1e-9;
			return Math.Abs(M01) <= limit && Math.Abs(M02) <= limit && Math.Abs(M10) <= limit &&
				Math.Abs(M12) <= limit && Math.Abs(M20) <= limit && Math.Abs(M21) <= limit;
		}

		public void Quantize(long[] into, double scale)
		{
			into[0] = SchematicOptimizer.Quantize(M00, scale);
			into[1] = SchematicOptimizer.Quantize(M01, scale);
			into[2] = SchematicOptimizer.Quantize(M02, scale);
			into[3] = SchematicOptimizer.Quantize(M10, scale);
			into[4] = SchematicOptimizer.Quantize(M11, scale);
			into[5] = SchematicOptimizer.Quantize(M12, scale);
			into[6] = SchematicOptimizer.Quantize(M20, scale);
			into[7] = SchematicOptimizer.Quantize(M21, scale);
			into[8] = SchematicOptimizer.Quantize(M22, scale);
		}

		/// <summary>
		/// Converts a rotation matrix to a quaternion (same convention as <c>Matrix4x4.Rotate</c>).
		/// </summary>
		public Quaternion ToQuaternion()
		{
			double trace = M00 + M11 + M22;
			double x, y, z, w;
			if (trace > 0)
			{
				double s = Math.Sqrt(trace + 1.0) * 2;
				w = 0.25 * s;
				x = (M21 - M12) / s;
				y = (M02 - M20) / s;
				z = (M10 - M01) / s;
			}
			else if (M00 > M11 && M00 > M22)
			{
				double s = Math.Sqrt(1.0 + M00 - M11 - M22) * 2;
				w = (M21 - M12) / s;
				x = 0.25 * s;
				y = (M01 + M10) / s;
				z = (M02 + M20) / s;
			}
			else if (M11 > M22)
			{
				double s = Math.Sqrt(1.0 + M11 - M00 - M22) * 2;
				w = (M02 - M20) / s;
				x = (M01 + M10) / s;
				y = 0.25 * s;
				z = (M12 + M21) / s;
			}
			else
			{
				double s = Math.Sqrt(1.0 + M22 - M00 - M11) * 2;
				w = (M10 - M01) / s;
				x = (M02 + M20) / s;
				y = (M12 + M21) / s;
				z = 0.25 * s;
			}

			double length = Math.Sqrt((x * x) + (y * y) + (z * z) + (w * w));
			return new Quaternion((float)(x / length), (float)(y / length), (float)(z / length), (float)(w / length));
		}

		/// <summary>
		/// Converts a rotation matrix to Unity Euler angles in degrees (inverse of <see cref="FromEuler"/>).
		/// </summary>
		public Vector3 ToEuler()
		{
			const double toDegrees = 180.0 / Math.PI;
			double cosX = Math.Sqrt((M02 * M02) + (M22 * M22));
			double x = Math.Atan2(-M12, cosX);
			double y, z;
			if (cosX > 1e-9)
			{
				y = Math.Atan2(M02, M22);
				z = Math.Atan2(M10, M11);
			}
			else
			{
				y = Math.Atan2(-M20, M00);
				z = 0;
			}

			return new Vector3(Normalize(x * toDegrees), Normalize(y * toDegrees), Normalize(z * toDegrees));
		}

		private static float Normalize(double degrees)
		{
			degrees %= 360.0;
			if (degrees < 0)
				degrees += 360.0;

			return (float)degrees;
		}
	}
}
