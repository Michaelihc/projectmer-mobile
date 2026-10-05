using LabApi.Features.Wrappers;
using MapGeneration;
using NorthwoodLib.Pools;
using ProjectMER.Features.Serializable;
using UnityEngine;

namespace ProjectMER.Features.Extensions;

public static class RoomExtensions
{
	public static Room GetRoomAtPosition(Vector3 position) => Room.TryGetRoomAtPosition(position, out Room? room) ? room : Room.List.First(x => x.Base != null && x.Name == RoomName.Outside);

	public static string GetRoomStringId(this Room room) => $"{room.Zone}_{room.Shape}_{room.Name}";

	/// <summary>
	/// Gets the rooms an object's room id refers to. The returned list is rented from <see cref="ListPool{T}"/>.
	/// </summary>
	/// <remarks>
	/// Room names that do not exist in Carl Mod (for example <c>Hcz127</c>) skip the object with one warning per load
	/// instead of failing the whole map as ProjectMER's <c>Enum.Parse</c> did.
	/// </remarks>
	public static List<Room> GetRooms(this SerializableObject serializableObject)
	{
		string[] split = serializableObject.Room.Split('_');
		if (split.Length != 3)
			return ListPool<Room>.Shared.Rent(Room.List.Where(x => x.Base != null && x.Name == RoomName.Outside));

		if (!Enum.TryParse(split[0], true, out FacilityZone facilityZone) ||
			!Enum.TryParse(split[1], true, out RoomShape roomShape) ||
			!Enum.TryParse(split[2], true, out RoomName roomName))
		{
			UnsupportedContent.Skip("objects in unknown rooms", $"room id \"{serializableObject.Room}\" does not exist in Carl Mod");
			return ListPool<Room>.Shared.Rent();
		}

		return ListPool<Room>.Shared.Rent(Room.List.Where(x => x.Base != null && x.Zone == facilityZone && x.Shape == roomShape && x.Name == roomName));
	}

	public static int GetRoomIndex(this Room room)
	{
		List<Room> list = ListPool<Room>.Shared.Rent(Room.List.Where(x => x.Base != null && x.Zone == room.Zone && x.Shape == room.Shape && x.Name == room.Name));
		int index = list.IndexOf(room);
		ListPool<Room>.Shared.Return(list);
		return index;
	}

	public static Vector3 GetAbsolutePosition(this Room? room, Vector3 position)
	{
		if (room is null || room.Name == RoomName.Outside)
			return position;

		return room.Transform.TransformPoint(position);
	}

	public static Quaternion GetAbsoluteRotation(this Room? room, Vector3 eulerAngles)
	{
		if (room is null || room.Name == RoomName.Outside)
			return Quaternion.Euler(eulerAngles);

		return room.Transform.rotation * Quaternion.Euler(eulerAngles);
	}
}
