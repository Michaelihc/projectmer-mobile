namespace ProjectMER.Features.Enums;

/// <summary>
/// Which schematics get duplicate removal and cube/quad merging (docs/projectmer-port-plan.md §3.9).
/// </summary>
/// <remarks>
/// Merged-away blocks keep their server-side anchor (name, transform, <c>AttachedBlocks</c> entry) but have no toy of their
/// own, so a plugin that recolours, moves or destroys individual cubes of a schematic would not see the change. Plugins
/// usually spawn schematics through <c>ObjectSpawner</c>; maps rarely have code acting on single blocks.
/// </remarks>
public enum BlockMergeMode
{
	/// <summary>
	/// Every block keeps its own toy.
	/// </summary>
	None = 0,

	/// <summary>
	/// Only schematics placed by maps (map files, <c>mp create</c>, the tool gun). Schematics spawned by plugins through
	/// the API keep every block.
	/// </summary>
	Maps = 1,

	/// <summary>
	/// Every schematic.
	/// </summary>
	All = 2,
}
