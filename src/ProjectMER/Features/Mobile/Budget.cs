using ProjectMER.Configs;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Objects;

namespace ProjectMER.Features.Mobile;

/// <summary>
/// Counts networked MER objects and applies the budgets of docs/projectmer-port-plan.md §3.6 and §3.7: the global warning
/// and hard cap, the per-schematic warning and the light cap. The per-schematic light cap is part of the schematic plan
/// (<see cref="SchematicOptimizer"/>).
/// </summary>
/// <remarks>
/// Objects are counted from creation (while still queued) until they are destroyed, through <see cref="MerBlockLink"/>.
/// </remarks>
public static class Budget
{
	private static readonly int[] CountByKind = new int[7];

	private static bool _warnedTotal;

	private static bool _reportedHardCap;

	private static bool _warnedLights;

	private static int _generation;

	/// <summary>
	/// Gets the number of networked MER objects (spawned or queued).
	/// </summary>
	public static int Networked { get; private set; }

	/// <summary>
	/// Gets the number of static toys.
	/// </summary>
	public static int Static { get; private set; }

	/// <summary>
	/// Gets the number of transparent primitives (alpha below 1, including invisible colliders).
	/// </summary>
	public static int Transparent { get; private set; }

	/// <summary>
	/// Gets the number of objects refused by the hard cap since the last reset.
	/// </summary>
	public static int Refused { get; private set; }

	/// <summary>
	/// Gets the number of MER lights (spawned, queued, or admitted by a schematic that is still being built).
	/// </summary>
	public static int Lights => CountByKind[(int)MerObjectKind.Light] + ReservedLights;

	/// <summary>
	/// Gets the number of lights admitted by schematics that are still being built.
	/// </summary>
	public static int ReservedLights { get; private set; }

	/// <summary>
	/// Gets the counter generation; it changes on every <see cref="Reset"/>.
	/// </summary>
	internal static int Generation => _generation;

	/// <summary>
	/// Gets the number of networked objects of a kind.
	/// </summary>
	public static int Count(MerObjectKind kind) => CountByKind[(int)kind];

	/// <summary>
	/// Approximate spawn message bytes of a primitive toy (docs/projectmer-port-plan.md §3.1).
	/// </summary>
	public const int PrimitiveSpawnBytes = 103;

	/// <summary>
	/// Approximate spawn message bytes of a light toy.
	/// </summary>
	public const int LightSpawnBytes = 111;

	/// <summary>
	/// Approximate spawn message bytes of a shooting target.
	/// </summary>
	public const int ShootingTargetSpawnBytes = 90;

	/// <summary>
	/// Approximate spawn message bytes of a pickup, door or structure.
	/// </summary>
	public const int OtherSpawnBytes = 120;

	/// <summary>
	/// Gets an estimate of the bytes a joining player receives for all MER objects (spawn messages, §3.1).
	/// </summary>
	public static long EstimatedSpawnBytes =>
		EstimateSpawnBytes(CountByKind[(int)MerObjectKind.Primitive] + CountByKind[(int)MerObjectKind.Indicator], CountByKind[(int)MerObjectKind.Light], CountByKind[(int)MerObjectKind.Pickup] + CountByKind[(int)MerObjectKind.Structure] + CountByKind[(int)MerObjectKind.Door]) +
		((long)ShootingTargetSpawnBytes * CountByKind[(int)MerObjectKind.ShootingTarget]);

	/// <summary>
	/// Estimates the spawn message bytes one player receives for a set of objects.
	/// </summary>
	/// <param name="primitives">Primitive toys.</param>
	/// <param name="lights">Light toys.</param>
	/// <param name="others">Pickups, doors and structures.</param>
	public static long EstimateSpawnBytes(int primitives, int lights, int others) =>
		((long)PrimitiveSpawnBytes * primitives) + ((long)LightSpawnBytes * lights) + ((long)OtherSpawnBytes * others);

	private static Config Config => ProjectMER.Singleton.Config!;

	/// <summary>
	/// Checks the hard cap before an object is created.
	/// </summary>
	/// <returns><see langword="false"/> when <c>networked_hard_cap</c> is reached; the caller must not create the object.</returns>
	public static bool CanCreate()
	{
		if (Networked < Config.NetworkedHardCap)
			return true;

		Refused++;
		if (!_reportedHardCap)
		{
			_reportedHardCap = true;
			Logger.Error($"MER reached networked_hard_cap ({Config.NetworkedHardCap} networked objects); further objects are not spawned. Run \"mp stats\" and unload maps or schematics.");
		}

		UnsupportedContent.Skip("objects", $"networked_hard_cap {Config.NetworkedHardCap} reached");
		return false;
	}

	/// <summary>
	/// Decides which candidate lights of one load fit under <c>max_lights</c>, strongest (intensity × range) first.
	/// </summary>
	/// <param name="strengths">The intensity × range of each candidate.</param>
	/// <returns>A flag per candidate: <see langword="true"/> to spawn it.</returns>
	public static bool[] AdmitLights(IReadOnlyList<float> strengths) => AdmitLights(strengths, false, out _);

	/// <summary>
	/// Decides which candidate lights fit under <c>max_lights</c>; with <paramref name="reserve"/>, the admitted lights
	/// count against the cap until they are created (<see cref="ReleaseLights"/>), so loads built across frames do not
	/// over-admit.
	/// </summary>
	/// <param name="strengths">The intensity × range of each candidate.</param>
	/// <param name="reserve">Whether to reserve the admitted lights.</param>
	/// <param name="admittedCount">The number of admitted lights.</param>
	/// <returns>A flag per candidate: <see langword="true"/> to spawn it.</returns>
	internal static bool[] AdmitLights(IReadOnlyList<float> strengths, bool reserve, out int admittedCount)
	{
		admittedCount = 0;
		bool[] admitted = new bool[strengths.Count];
		if (strengths.Count == 0)
			return admitted;

		int free = Math.Max(0, Config.MaxLights - Lights);
		int[] order = new int[strengths.Count];
		for (int i = 0; i < order.Length; i++)
			order[i] = i;

		Array.Sort(order, (a, b) => strengths[b].CompareTo(strengths[a]));
		for (int i = 0; i < order.Length && i < free; i++)
		{
			admitted[order[i]] = true;
			admittedCount++;
		}

		int skipped = order.Length - Math.Min(free, order.Length);
		for (int i = 0; i < skipped; i++)
			UnsupportedContent.Skip("lights", $"max_lights {Config.MaxLights} reached; the weakest lights by intensity x range are skipped");

		if (!_warnedLights && Lights + admittedCount > 8)
		{
			_warnedLights = true;
			Logger.Warn($"More than 8 MER lights are loaded. Every pixel light adds a draw call per lit primitive on phones.");
		}

		if (reserve)
			ReservedLights += admittedCount;

		return admitted;
	}

	/// <summary>
	/// Releases light reservations made by <see cref="AdmitLights(IReadOnlyList{float}, bool, out int)"/> (the light is
	/// about to be created, or will not be).
	/// </summary>
	/// <param name="count">The number of reservations.</param>
	/// <param name="generation">The <see cref="Generation"/> they were made in; older reservations were already cleared.</param>
	internal static void ReleaseLights(int count, int generation)
	{
		if (generation == _generation)
			ReservedLights = Math.Max(0, ReservedLights - count);
	}

	/// <summary>
	/// Counts a created object.
	/// </summary>
	internal static void Register(MerBlockLink link)
	{
		if (link.Counted)
			return;

		link.Counted = true;
		link.BudgetGeneration = _generation;
		Networked++;
		CountByKind[(int)link.Kind]++;
		if (link.IsStatic)
			Static++;

		if (link.IsTransparent)
			Transparent++;

		if (!_warnedTotal && Networked > Config.NetworkedWarnTotal)
		{
			_warnedTotal = true;
			Logger.Warn($"MER content exceeds networked_warn_total: {Networked} networked objects. Phones render every one of them; run \"mp stats\".");
		}
	}

	/// <summary>
	/// Updates the counters after an object changed between static and dynamic or transparent and opaque.
	/// </summary>
	internal static void Update(MerBlockLink link, bool isStatic, bool isTransparent)
	{
		if (link.Counted)
		{
			Static += (isStatic ? 1 : 0) - (link.IsStatic ? 1 : 0);
			Transparent += (isTransparent ? 1 : 0) - (link.IsTransparent ? 1 : 0);
		}

		link.IsStatic = isStatic;
		link.IsTransparent = isTransparent;
	}

	/// <summary>
	/// Stops counting a destroyed object.
	/// </summary>
	internal static void Unregister(MerBlockLink link)
	{
		if (!link.Counted)
			return;

		link.Counted = false;

		// Objects counted before the last reset (round restart) are no longer part of the totals.
		if (link.BudgetGeneration != _generation)
			return;

		Networked--;
		CountByKind[(int)link.Kind]--;
		if (link.IsStatic)
			Static--;

		if (link.IsTransparent)
			Transparent--;

		if (Networked <= Config.NetworkedWarnTotal)
			_warnedTotal = false;

		if (Networked < Config.NetworkedHardCap)
			_reportedHardCap = false;

		if (Lights <= 8)
			_warnedLights = false;
	}

	/// <summary>
	/// Clears every counter (round restart: the scene change destroyed all objects).
	/// </summary>
	public static void Reset()
	{
		_generation++;
		Array.Clear(CountByKind, 0, CountByKind.Length);
		Networked = 0;
		Static = 0;
		Transparent = 0;
		Refused = 0;
		ReservedLights = 0;
		_warnedTotal = false;
		_reportedHardCap = false;
		_warnedLights = false;
	}
}
