using System.Text;
using LabApi.Features.Wrappers;
using NorthwoodLib.Pools;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Objects;
using UnityEngine;

namespace ProjectMER.Features.ToolGun;

/// <summary>
/// The tool gun hint.
/// </summary>
/// <remarks>
/// Phase A port: a short hint (mode, type or schematic, selected object, room) without ProjectMER's 36 padding lines,
/// property dump and <c>LiberationSans SDF</c> font tag. The mobile HUD of docs/projectmer-port-plan.md §4 replaces it.
/// </remarks>
public static class ToolGunUI
{
	public static string GetHintHUD(Player player)
	{
		StringBuilder sb = StringBuilderPool.Shared.Rent();

		if (ToolGunHandler.TryGetSelectedMapObject(player, out MapEditorObject mapEditorObject) && mapEditorObject != null)
		{
			sb.Append("<size=50%>MapName: ").Append(MapUtils.GetColoredMapName(mapEditorObject.MapName)).Append("</size>\n");
			sb.Append("<size=50%>ID: ").Append(MapUtils.GetColoredString(mapEditorObject.Id)).Append("</size>\n");
		}

		if (!player.CurrentItem.IsToolGun(out ToolGunItem toolGun))
			return StringBuilderPool.Shared.ToStringReturn(sb);

		sb.Append("<size=50%>").Append(GetToolGunModeString(player, toolGun)).Append("</size>\n");
		sb.Append("<size=50%>").Append(GetRoomString(player)).Append("</size>");

		return StringBuilderPool.Shared.ToStringReturn(sb);
	}

	private static string GetToolGunModeString(Player player, ToolGunItem toolGun)
	{
		if (toolGun.CreateMode)
		{
			string output;
			if (toolGun.SelectedObjectToSpawn == ToolGunObjectType.Schematic)
			{
				output = ToolGunItem.SelectedSchematics.TryGetValue(player, out string schematicName)
					? schematicName.ToUpper()
					: "Pick one with: mp tg schematic <name>";
			}
			else
			{
				output = toolGun.SelectedObjectToSpawn.ToString().ToUpper();
			}

			return $"<color=green>CREATE</color> <color=yellow>{output}</color>";
		}

		string name = " ";
		if (ToolGunHandler.TryGetMapObject(player, out MapEditorObject mapEditorObject))
		{
			if (mapEditorObject.gameObject.TryGetComponent(out SchematicObject schematicObject))
				name = schematicObject.Name.ToUpper();
			else
				name = mapEditorObject.Base.GetType().Name.Replace("Serializable", "").ToUpper();
		}

		if (toolGun.DeleteMode)
			return $"<color=red>DELETE</color> <color=yellow>{name}</color>";

		if (toolGun.SelectMode)
			return $"<color=yellow>SELECT</color> <color=yellow>{name}</color>";

		return " ";
	}

	private static string GetRoomString(Player player)
	{
		Room room = RoomExtensions.GetRoomAtPosition(player.Camera.transform.position);
		List<Room> list = ListPool<Room>.Shared.Rent(Room.List.Where(x => x.Base != null && x.Zone == room.Zone && x.Shape == room.Shape && x.Name == room.Name));

		string roomString;
		if (list.Count == 1)
		{
			roomString = room.GetRoomStringId();
		}
		else
		{
			roomString = $"{room.GetRoomStringId()} ({list.IndexOf(room)}) ({list.Count})";
		}

		ListPool<Room>.Shared.Return(list);
		return roomString;
	}
}
