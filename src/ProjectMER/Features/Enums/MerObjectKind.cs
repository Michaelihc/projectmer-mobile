namespace ProjectMER.Features.Enums;

/// <summary>
/// Kind of networked object spawned by MER, used for budgets and statistics.
/// </summary>
public enum MerObjectKind : byte
{
	Primitive = 0,
	Light = 1,
	Pickup = 2,
	Structure = 3,
	Door = 4,
	ShootingTarget = 5,
	Indicator = 6,
}
