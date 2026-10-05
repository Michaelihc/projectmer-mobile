namespace ProjectMER.Features.Enums;

public enum ToolGunObjectType
{
	Schematic = 0,
	Primitive = 1,
	Light = 2,
	Door = 3,
	Workstation = 4,
	ItemSpawnpoint = 5,
	PlayerSpawnpoint = 6,
	// Carl Mod lacks Capybara, Text, Scp079Camera, Interactable, Waypoint and Triangle; they cannot be created.
	Capybara = 7,
	Text = 8,
	Scp079Camera = 9,
	ShootingTarget = 10,
	Locker = 11,
	Teleport = 12,
	Interactable = 13,
	Waypoint = 14,
	Triangle = 15,
	Quad = 16,
}
