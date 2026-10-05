namespace ProjectMER.Features.Enums;

public enum DoorType
{
	Lcz = 0,
	LightContainmentDoor = 0,
	Hcz = 1,
	HeavyContainmentDoor = 1,
	Ez = 2,
	EntranceDoor = 2,
	// Carl Mod has no spawnable bulk door or gate prefab; doors of these types are skipped with a warning.
	Bulkdoor = 3,
	HeavyBulkDoor = 3,
	Gate = 4,
}
