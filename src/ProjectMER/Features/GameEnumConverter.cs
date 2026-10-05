using Interactables.Interobjects.DoorUtils;
using PlayerRoles;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace ProjectMER.Features;

/// <summary>
/// Reads and writes the game enums whose members differ between SL 14.x (where ProjectMER maps are made) and Carl Mod.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="ItemType"/>: 14.x names are mapped to their Carl Mod equivalents (<c>KeycardMTFPrivate</c> is
/// <c>KeycardNTFOfficer</c>, <c>Lantern</c> becomes <c>Flashlight</c>).</item>
/// <item><see cref="ItemType"/> and <see cref="RoleTypeId"/> names that do not exist in Carl Mod (<c>GunA7</c>,
/// <c>Flamingo</c>...) become an undefined placeholder value that remembers the name, so the object is skipped but a
/// saved map still contains the original name.</item>
/// <item><see cref="KeycardPermissions"/> (ProjectMER's <c>DoorPermissionFlags</c>) accepts <c>All</c>.</item>
/// </list>
/// </remarks>
public sealed class GameEnumConverter : IYamlTypeConverter
{
	private const int ItemPlaceholderBase = 10000;

	private const int RolePlaceholderBase = 100;

	private static readonly Dictionary<string, ItemType> ItemAliases = new(StringComparer.OrdinalIgnoreCase)
	{
		{ "KeycardMTFPrivate", ItemType.KeycardNTFOfficer },
		{ "KeycardMTFOperative", ItemType.KeycardNTFLieutenant },
		{ "KeycardMTFCaptain", ItemType.KeycardNTFCommander },
		{ "Lantern", ItemType.Flashlight },
	};

	private static readonly List<string> UnknownItems = [];

	private static readonly List<string> UnknownRoles = [];

	/// <summary>
	/// Gets the original name of a placeholder <see cref="ItemType"/>, or <see langword="null"/>.
	/// </summary>
	public static string? GetUnknownName(ItemType itemType)
	{
		int index = (int)itemType - ItemPlaceholderBase;
		return index >= 0 && index < UnknownItems.Count ? UnknownItems[index] : null;
	}

	/// <summary>
	/// Gets the original name of a placeholder <see cref="RoleTypeId"/>, or <see langword="null"/>.
	/// </summary>
	public static string? GetUnknownName(RoleTypeId roleType)
	{
		int index = (int)roleType - RolePlaceholderBase;
		return index >= 0 && index < UnknownRoles.Count ? UnknownRoles[index] : null;
	}

	/// <inheritdoc />
	public bool Accepts(Type type) => type == typeof(ItemType) || type == typeof(RoleTypeId) || type == typeof(KeycardPermissions);

	/// <inheritdoc />
	public object? ReadYaml(IParser parser, Type type)
	{
		string value = parser.Consume<Scalar>().Value.Trim();

		if (type == typeof(KeycardPermissions))
		{
			if (value.Equals("All", StringComparison.OrdinalIgnoreCase))
				return AllPermissions;

			return value.Length == 0 ? KeycardPermissions.None : Enum.Parse(typeof(KeycardPermissions), value, true);
		}

		if (type == typeof(ItemType))
		{
			if (Enum.TryParse(value, true, out ItemType item) && (Enum.IsDefined(typeof(ItemType), item) || char.IsDigit(value[0]) || value[0] == '-'))
				return item;

			if (ItemAliases.TryGetValue(value, out item))
				return item;

			return (ItemType)(ItemPlaceholderBase + Placeholder(UnknownItems, value));
		}

		if (Enum.TryParse(value, true, out RoleTypeId role) && (Enum.IsDefined(typeof(RoleTypeId), role) || char.IsDigit(value[0]) || value[0] == '-'))
			return role;

		int index = Placeholder(UnknownRoles, value);
		if (RolePlaceholderBase + index > sbyte.MaxValue)
			throw new YamlException($"Too many unknown role names; \"{value}\" cannot be kept.");

		return (RoleTypeId)(sbyte)(RolePlaceholderBase + index);
	}

	/// <inheritdoc />
	public void WriteYaml(IEmitter emitter, object? value, Type type)
	{
		string text = value switch
		{
			ItemType item => GetUnknownName(item) ?? item.ToString(),
			RoleTypeId role => GetUnknownName(role) ?? role.ToString(),
			KeycardPermissions permissions => permissions.ToString(),
			_ => value?.ToString() ?? string.Empty,
		};

		emitter.Emit(new Scalar(text));
	}

	/// <inheritdoc />
	public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) => ReadYaml(parser, type);

	/// <inheritdoc />
	public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) => WriteYaml(emitter, value, type);

	private static KeycardPermissions AllPermissions
	{
		get
		{
			KeycardPermissions all = KeycardPermissions.None;
			foreach (KeycardPermissions permission in Enum.GetValues(typeof(KeycardPermissions)))
				all |= permission;

			return all;
		}
	}

	private static int Placeholder(List<string> names, string name)
	{
		int index = names.IndexOf(name);
		if (index >= 0)
			return index;

		names.Add(name);
		return names.Count - 1;
	}
}
