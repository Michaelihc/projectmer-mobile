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
/// The simplified, indexed form of a schematic that <see cref="Objects.SchematicObject"/> builds from. Computed once per
/// schematic file and cached with its parse result.
/// </summary>
public sealed class SchematicBuildPlan
{
	internal SchematicBuildPlan(SchematicObjectDataList data, int blockCount)
	{
		Data = data;
		Outputs = new BlockOutput[blockCount];
		Flags = new PrimitiveFlags[blockCount];
	}

	/// <summary>
	/// Gets the data the plan was made from.
	/// </summary>
	public SchematicObjectDataList Data { get; }

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
	/// Gets the networked primitive flags of each primitive block index.
	/// </summary>
	public PrimitiveFlags[] Flags { get; }

	/// <summary>
	/// Gets the indices of reachable blocks with an <c>AnimatorName</c>.
	/// </summary>
	public List<int> AnimatedBlocks { get; } = [];

	/// <summary>
	/// Gets the number of blocks reachable from the root.
	/// </summary>
	public int ReachableBlocks { get; internal set; }

	/// <summary>
	/// Gets the number of networked objects ProjectMER would spawn (root, every block, six per triangle).
	/// </summary>
	public int ProjectMerNetworked { get; internal set; }

	/// <summary>
	/// Gets the number of networked objects the port spawns before the light and hard caps.
	/// </summary>
	public int Networked { get; internal set; }

	/// <summary>
	/// Gets the number of empty or anchor-only blocks (never networked in the port).
	/// </summary>
	public int Anchors { get; internal set; }

	/// <summary>
	/// Gets the number of primitives dropped because they produce nothing visible or collidable (§3.9 step 1).
	/// </summary>
	public int DroppedInvisible { get; internal set; }

	/// <summary>
	/// Gets the number of blocks dropped for zero scale (§3.9 step 1).
	/// </summary>
	public int DroppedZeroScale { get; internal set; }

	/// <summary>
	/// Gets the number of unsupported blocks per type.
	/// </summary>
	public Dictionary<BlockType, int> Unsupported { get; } = [];

	/// <summary>
	/// Gets the number of transparent primitives (alpha below 1, including invisible colliders).
	/// </summary>
	public int Transparent { get; internal set; }

	/// <summary>
	/// Gets the number of light blocks.
	/// </summary>
	public int Lights { get; internal set; }

	/// <summary>
	/// Gets blocks the optimizer adds, built as static leaves after the file's blocks (for example a merged cube
	/// replacing several blocks whose <see cref="Outputs"/> were set to <see cref="BlockOutput.None"/>). Their transform
	/// is local to the block <see cref="SchematicBlockData.ParentId"/> names (the schematic root when it has no anchor).
	/// </summary>
	public List<AddedBlock> AddedBlocks { get; } = [];

	/// <summary>
	/// A block added by the optimizer.
	/// </summary>
	/// <param name="Data">The block data; <see cref="SchematicBlockData.ParentId"/>, transform and properties are used.</param>
	/// <param name="Output">What it produces (primitives in practice).</param>
	/// <param name="Flags">The networked primitive flags.</param>
	public readonly record struct AddedBlock(SchematicBlockData Data, BlockOutput Output, PrimitiveFlags Flags);

	/// <summary>
	/// Gets the children of an object id, or an empty list.
	/// </summary>
	public List<int> GetChildren(int objectId) => Children.TryGetValue(objectId, out List<int> children) ? children : Empty;

	private static readonly List<int> Empty = [];
}

/// <summary>
/// Simplifies schematics before they are built (docs/projectmer-port-plan.md §3.9).
/// </summary>
/// <remarks>
/// <para>
/// Implemented: step 1 (blocks that produce nothing on the client are not networked: <c>PrimitiveFlags.None</c>,
/// zero-alpha non-collidable primitives, zero scale, unsupported types) and step 2 (empty groups are never networked).
/// Such blocks stay as server-only anchor GameObjects, which cost nothing on the client: plugins find marker empties
/// through <c>AttachedBlocks</c>/<c>ObjectFromId</c>, and their children need the transform.
/// </para>
/// <para>
/// Seam for later steps (cube merging, duplicate removal, a per-schematic light cap, <c>mp optimize</c>): they rewrite the
/// plan's <see cref="SchematicBuildPlan.Outputs"/> (for example to <see cref="BlockOutput.None"/> for merged-away cubes) and
/// can add synthetic blocks; <see cref="Objects.SchematicObject"/> only reads the plan.
/// </para>
/// </remarks>
public static class SchematicOptimizer
{
	private static Config Config => ProjectMER.Singleton.Config!;

	/// <summary>
	/// Gets the plan of a schematic, cached with the parse result when <paramref name="data"/> is the cached instance.
	/// </summary>
	public static SchematicBuildPlan GetPlan(SchematicObjectDataList data) => SchematicJson.GetDerived(data, Build);

	/// <summary>
	/// Builds the plan of a schematic. O(n) in the number of blocks.
	/// </summary>
	public static SchematicBuildPlan Build(SchematicObjectDataList data)
	{
		List<SchematicBlockData> blocks = data.Blocks;
		SchematicBuildPlan plan = new(data, blocks.Count);
		bool optimize = Config.OptimizeSchematics;

		// Children by parent id, in file order.
		Dictionary<int, List<int>> allChildren = new(blocks.Count);
		for (int i = 0; i < blocks.Count; i++)
		{
			SchematicBlockData block = blocks[i];
			if (block == null || block.ObjectId == data.RootObjectId)
				continue;

			if (!allChildren.TryGetValue(block.ParentId, out List<int> list))
				allChildren[block.ParentId] = list = [];

			list.Add(i);
		}

		plan.ProjectMerNetworked = 1; // ProjectMER's root is a networked primitive.

		Stack<int> stack = new();
		PushChildren(stack, allChildren, data.RootObjectId);
		while (stack.Count > 0)
		{
			int index = stack.Pop();
			SchematicBlockData block = blocks[index];
			plan.ReachableBlocks++;
			plan.ProjectMerNetworked += block.BlockType == BlockType.Triangle ? 6 : 1;

			if (!plan.Children.TryGetValue(block.ParentId, out List<int> siblings))
				plan.Children[block.ParentId] = siblings = [];

			siblings.Add(index);

			if (!string.IsNullOrEmpty(block.AnimatorName) && block.BlockType != BlockType.Light)
				plan.AnimatedBlocks.Add(index);

			BlockOutput output = Classify(block, index, plan, optimize);
			plan.Outputs[index] = output;
			if (output == BlockOutput.None)
				plan.Anchors++;
			else
				plan.Networked++;

			// ProjectMER does not build the children of schematic blocks (they would be spawned twice).
			if (block.BlockType != BlockType.Schematic)
				PushChildren(stack, allChildren, block.ObjectId);
		}

		// The stack visits children in reverse; restore file order.
		foreach (List<int> list in plan.Children.Values)
			list.Sort();

		return plan;
	}

	private static void PushChildren(Stack<int> stack, Dictionary<int, List<int>> children, int parentId)
	{
		if (!children.TryGetValue(parentId, out List<int> list))
			return;

		for (int i = list.Count - 1; i >= 0; i--)
			stack.Push(list[i]);
	}

	private static BlockOutput Classify(SchematicBlockData block, int index, SchematicBuildPlan plan, bool optimize)
	{
		switch (block.BlockType)
		{
			case BlockType.Empty:
				return BlockOutput.None;

			case BlockType.Primitive:
				{
					block.GetPrimitive(out _, out Color color, out PrimitiveFlags flags);
					if (block.Scale == Vector3.zero && optimize)
					{
						plan.DroppedZeroScale++;
						return BlockOutput.None;
					}

					if (!ToyFactory.TryGetNetworkedFlags(flags, out PrimitiveFlags networked))
					{
						plan.DroppedInvisible++;
						return BlockOutput.None;
					}

					if (optimize && (networked & PrimitiveFlags.Collidable) == 0 && color.a <= 0f)
					{
						plan.DroppedInvisible++;
						return BlockOutput.None;
					}

					if (color.a < 1f || (networked & PrimitiveFlags.Visible) == 0)
						plan.Transparent++;

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
					return BlockOutput.None;
				}

				return BlockOutput.Pickup;

			case BlockType.Workstation:
				return BlockOutput.Workstation;

			default:
				plan.Unsupported.TryGetValue(block.BlockType, out int count);
				plan.Unsupported[block.BlockType] = count + 1;
				return BlockOutput.None;
		}
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
}
