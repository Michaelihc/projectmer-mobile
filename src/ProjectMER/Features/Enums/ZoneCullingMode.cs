namespace ProjectMER.Features.Enums;

/// <summary>
/// Granularity of the zone culling done by <c>MerVisibility</c>.
/// </summary>
public enum ZoneCullingMode
{
	/// <summary>
	/// Every player observes every MER object.
	/// </summary>
	None = 0,

	/// <summary>
	/// Large groups are only shown to players on the same side of the surface (y = 900) boundary.
	/// </summary>
	SurfaceFacility = 1,

	/// <summary>
	/// Large groups are only shown to players in the same facility zone.
	/// </summary>
	PerZone = 2,
}
