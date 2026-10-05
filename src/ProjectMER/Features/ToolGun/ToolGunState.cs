using LabApi.Features.Wrappers;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Objects;

namespace ProjectMER.Features.ToolGun;

/// <summary>
/// The tool gun mode chosen with inspect or the light toggle. Aiming selects in either mode.
/// </summary>
public enum ToolGunMode : byte
{
	Create = 0,
	Delete = 1,
}

/// <summary>
/// Per-player tool gun state. Carl Mod has no server-specific settings, so what ProjectMER kept in its settings dropdown
/// (the schematic to create) lives here, together with the mode, the object type, the HUD cache, the selection box and
/// the grab session.
/// </summary>
public sealed class ToolGunState
{
	private static readonly Dictionary<Player, ToolGunState> States = [];

	private ToolGunState(Player player)
	{
		Player = player;
	}

	/// <summary>
	/// Gets the states of every player that used the tool gun or selected an object this round.
	/// </summary>
	public static IReadOnlyDictionary<Player, ToolGunState> All => States;

	/// <summary>
	/// Gets the states for allocation-free iteration.
	/// </summary>
	internal static Dictionary<Player, ToolGunState>.ValueCollection Values => States.Values;

	/// <summary>
	/// Gets the player.
	/// </summary>
	public Player Player { get; }

	/// <summary>
	/// Gets or sets the mode used when the player fires without aiming.
	/// </summary>
	public ToolGunMode Mode { get; set; } = ToolGunMode.Create;

	/// <summary>
	/// Gets or sets the object type created in <see cref="ToolGunMode.Create"/> mode.
	/// </summary>
	public ToolGunObjectType ObjectType { get; set; } = ToolGunObjectType.Primitive;

	/// <summary>
	/// Gets or sets the schematic created when <see cref="ObjectType"/> is <see cref="ToolGunObjectType.Schematic"/>.
	/// </summary>
	public string? SchematicName { get; set; }

	/// <summary>
	/// Gets the HUD state.
	/// </summary>
	internal ToolGunHud.PlayerHud Hud { get; } = new();

	/// <summary>
	/// Gets the selection box state.
	/// </summary>
	internal SelectionBox Box { get; } = new();

	/// <summary>
	/// Gets or sets the running grab, if any.
	/// </summary>
	internal GrabSession? Grab { get; set; }

	/// <summary>
	/// Gets the state of a player, creating it.
	/// </summary>
	public static ToolGunState Get(Player player)
	{
		if (!States.TryGetValue(player, out ToolGunState state))
		{
			state = new ToolGunState(player);
			States.Add(player, state);
		}

		return state;
	}

	/// <summary>
	/// Gets the state of a player if it exists.
	/// </summary>
	public static bool TryGet(Player? player, out ToolGunState state)
	{
		if (player is null)
		{
			state = null!;
			return false;
		}

		return States.TryGetValue(player, out state);
	}

	/// <summary>
	/// Drops the state of a player that left: stops the grab and destroys the selection box.
	/// </summary>
	internal static void Remove(Player player)
	{
		if (!States.TryGetValue(player, out ToolGunState state))
			return;

		state.Grab?.End(commit: true);
		state.Box.Destroy();
		States.Remove(player);
	}

	/// <summary>
	/// Forgets every state. On round restart the scene change already destroyed the boxes and objects.
	/// </summary>
	internal static void Reset()
	{
		foreach (ToolGunState state in States.Values)
		{
			state.Grab?.Abandon();
			state.Box.Forget();
		}

		States.Clear();
	}

	/// <summary>
	/// Gets whether the player currently holds a tool gun.
	/// </summary>
	public bool IsHolding => Player.CurrentItem.IsToolGun(out _);

	/// <summary>
	/// Gets the selected object, or <see langword="null"/> (also when it was destroyed).
	/// </summary>
	public MapEditorObject? Selected => ToolGunHandler.TryGetSelectedMapObject(Player, out MapEditorObject selected) ? selected : null;
}
