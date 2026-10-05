using Interactables.Interobjects.DoorUtils;

namespace ProjectMER.Features.Serializable.Lockers;

/// <summary>
/// A locker chamber. <see cref="RequiredPermissions"/> uses Carl Mod's <see cref="KeycardPermissions"/> (ProjectMER:
/// <c>DoorPermissionFlags</c>, same names and values; <c>All</c> is read as every flag).
/// </summary>
public class SerializableLockerChamber
{
	public SerializableLockerChamber() { }

	public SerializableLockerChamber(ItemType[] acceptableItems, bool isOpen, KeycardPermissions requiredPermissions)
	{
		AcceptableItems = acceptableItems.ToList();
		IsOpen = isOpen;
		RequiredPermissions = requiredPermissions;
	}

	public List<ItemType> AcceptableItems { get; set; } = [];

	public bool IsOpen { get; set; }

	public KeycardPermissions RequiredPermissions { get; set; }
}
