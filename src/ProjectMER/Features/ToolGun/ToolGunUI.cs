using System.Text;
using LabApi.Features.Wrappers;
using MapGeneration;
using NorthwoodLib.Pools;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Objects;

namespace ProjectMER.Features.ToolGun;

/// <summary>
/// Builds the tool gun HUD text.
/// </summary>
/// <remarks>
/// At most five short lines with <c>&lt;size&gt;</c> and <c>&lt;color&gt;</c> only: the mode with the object type,
/// schematic or the object under the crosshair; the selected object (id, type, map); the grab state; the room; and the
/// button reminder. ProjectMER's 36 padding lines, property dump and <c>LiberationSans SDF</c> font tag are gone. Text is
/// built only when <see cref="ToolGunHud"/> sees an input change.
/// </remarks>
public static class ToolGunUI
{
	private const string Help = "<size=58%><color=#A0A0A0>Fire: use | Aim+Fire: select | Inspect/Light: create/delete | Reload/Drop: type</color></size>";

	private const string TopPadding = "<size=70%>\n\n\n\n\n\n\n\n</size>";

	private static readonly string[] TypeNames = BuildTypeNames();

	private static Room? _cachedRoom;

	private static string _cachedRoomText = string.Empty;

	/// <summary>
	/// Gets the HUD text a player would see now.
	/// </summary>
	public static string GetHintHUD(Player player)
	{
		ToolGunState state = ToolGunState.Get(player);
		bool holding = player.CurrentItem.IsToolGun(out ToolGunItem toolGun);
		bool aiming = holding && toolGun.Aiming;
		MapEditorObject? target = null;
		if (holding && (aiming || state.Mode == ToolGunMode.Delete))
			ToolGunHandler.TryGetMapObject(player, out target);

		Room? room = holding && player.Camera != null ? RoomExtensions.GetRoomAtPosition(player.Camera.position) : null;
		return Build(state, holding, aiming, state.Selected, target, room);
	}

	/// <summary>
	/// Builds the HUD text from its inputs.
	/// </summary>
	internal static string Build(ToolGunState state, bool holding, bool aiming, MapEditorObject? selected, MapEditorObject? target, Room? room)
	{
		StringBuilder sb = StringBuilderPool.Shared.Rent();

		// Push the lines below the crosshair, into the free strip between the joystick and the attack button, so the
		// HUD does not cover the object being edited (the client centres hint text vertically).
		sb.Append(TopPadding);

		if (holding)
		{
			sb.Append("<size=80%>");
			if (aiming)
			{
				sb.Append("<color=#4AC8FF>SELECT</color> ");
				AppendTarget(sb, target, "nothing (fire to deselect)");
			}
			else if (state.Mode == ToolGunMode.Delete)
			{
				sb.Append("<color=#FF5A5A>DELETE</color> ");
				AppendTarget(sb, target, "-");
			}
			else
			{
				sb.Append("<color=#5BE35B>CREATE</color> <color=#FFD84A>");
				string[] schematics = ToolGunItem.SchematicNames;
				if (state.ObjectType == ToolGunObjectType.Schematic)
				{
					if (string.IsNullOrEmpty(state.SchematicName))
						sb.Append("Schematic: none (Reload/Drop or mp tg schematic)");
					else
						AppendEscaped(sb.Append("Schematic: "), state.SchematicName!);
				}
				else
				{
					sb.Append(TypeNames[(int)state.ObjectType]);
				}

				int count = schematics.Length + ToolGunItem.TypeCycle.Length;
				sb.Append("</color> <color=#A0A0A0>").Append(ToolGunItem.GetCycleIndex(state, schematics) + 1).Append('/').Append(count).Append("</color>");
			}

			sb.Append("</size>\n");
		}

		sb.Append("<size=70%>");
		if (selected != null)
		{
			sb.Append("Selected <b>");
			AppendEscaped(sb, selected.Id).Append("</b> ");
			AppendObjectName(sb, selected);
			sb.Append(" <color=#A0A0A0>in</color> ");
			AppendEscaped(sb, selected.MapName);
			if (MapUtils.LoadedMaps.TryGetValue(selected.MapName, out Serializable.MapSchematic map) && map.IsDirty)
				sb.Append('*');
		}
		else
		{
			sb.Append("<color=#A0A0A0>Nothing selected</color>");
		}

		sb.Append("</size>");

		if (state.Grab != null)
		{
			sb.Append("\n<size=70%><color=#FFC83C>GRABBING</color> ");
			sb.Append(state.Grab.UsesProxy ? "box (the object moves on release)" : "object");
			sb.Append(" <color=#A0A0A0>mp pos grab: drop</color></size>");
		}

		if (holding)
		{
			if (room != null)
				sb.Append("\n<size=58%><color=#A0A0A0>").Append(GetRoomText(room)).Append("</color></size>");

			sb.Append('\n').Append(Help);
		}

		return StringBuilderPool.Shared.ToStringReturn(sb);
	}

	private static void AppendTarget(StringBuilder sb, MapEditorObject? target, string none)
	{
		if (target == null)
		{
			sb.Append("<color=#A0A0A0>").Append(none).Append("</color>");
			return;
		}

		sb.Append("<color=#FFD84A>");
		AppendObjectName(sb, target);
		sb.Append("</color> <color=#A0A0A0>");
		AppendEscaped(sb, target.Id).Append("</color>");
	}

	private static void AppendObjectName(StringBuilder sb, MapEditorObject mapEditorObject)
	{
		if (mapEditorObject.TryGetComponent(out SchematicObject schematic))
		{
			AppendEscaped(sb, schematic.Name);
			return;
		}

		string name = mapEditorObject.Base?.GetType().Name ?? "Object";
		sb.Append(name, name.StartsWith("Serializable", StringComparison.Ordinal) ? 12 : 0, name.StartsWith("Serializable", StringComparison.Ordinal) ? name.Length - 12 : name.Length);
	}

	/// <summary>
	/// Appends a name, doubling braces (TextHint formats its text) and replacing angle brackets (rich text).
	/// </summary>
	private static StringBuilder AppendEscaped(StringBuilder sb, string value)
	{
		foreach (char c in value)
		{
			switch (c)
			{
				case '{':
					sb.Append("{{");
					break;
				case '}':
					sb.Append("}}");
					break;
				case '<':
					sb.Append('(');
					break;
				case '>':
					sb.Append(')');
					break;
				default:
					sb.Append(c);
					break;
			}
		}

		return sb;
	}

	private static string GetRoomText(Room room)
	{
		if (ReferenceEquals(room, _cachedRoom))
			return _cachedRoomText;

		// Only when the room changes: ProjectMER's id plus the index among rooms of the same id.
		string id = room.GetRoomStringId();
		int same = 0;
		foreach (Room other in Room.List)
		{
			if (other.Base != null && other.Zone == room.Zone && other.Shape == room.Shape && other.Name == room.Name)
				same++;
		}

		_cachedRoom = room;
		_cachedRoomText = same > 1 && room.Name != RoomName.Outside ? $"{id} ({room.GetRoomIndex()} of {same})" : id;
		return _cachedRoomText;
	}

	private static string[] BuildTypeNames()
	{
		Array values = Enum.GetValues(typeof(ToolGunObjectType));
		int max = 0;
		foreach (object value in values)
			max = Math.Max(max, (int)value);

		string[] names = new string[max + 1];
		foreach (object value in values)
			names[(int)value] = value.ToString();

		return names;
	}
}
