using CommandSystem;
using LabApi.Features.Permissions;
using LabApi.Features.Wrappers;
using ProjectMER.Features.Objects;

namespace ProjectMER.Commands.Utility;

/// <summary>
/// <c>mp indicators [on|off] [all]</c>: shows indicators for invisible objects to the player who runs it.
/// </summary>
/// <remarks>
/// Indicators are per player: other players never receive them. <c>mp indicators</c> toggles them for the sender,
/// <c>on</c>/<c>off</c> set them, and <c>off all</c> (also from the server console) turns them off for everyone.
/// </remarks>
public class Indicators : ICommand
{
	public string Command => "indicators";

	public string[] Aliases => ["i", "si"];

	public string Description => "Shows indicators for invisible objects to you (toggle, or on/off; \"off all\" for everyone).";

	public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
	{
		if (!sender.HasAnyPermission($"mpr.{Command}"))
		{
			response = $"You don't have permission to execute this command. Required permission: mpr.{Command}";
			return false;
		}

		string mode = arguments.Count > 0 ? arguments.At(0).ToLowerInvariant() : "toggle";
		bool all = arguments.Count > 1 && arguments.At(1).Equals("all", StringComparison.OrdinalIgnoreCase);

		if (mode == "off" && all)
		{
			int viewers = IndicatorObject.Viewers.Count;
			IndicatorObject.HideAll();
			response = $"Removed all indicators ({viewers} players had them on).";
			return true;
		}

		if (!Player.TryGet(sender, out Player? player) || player.IsHost)
		{
			response = "Indicators are shown per player: run this as a player, or use \"mp indicators off all\".";
			return false;
		}

		bool shown = mode switch
		{
			"on" => true,
			"off" => false,
			_ => !IndicatorObject.IsShownTo(player),
		};

		IndicatorObject.SetShown(player, shown);
		response = shown
			? $"Indicators are shown to you ({IndicatorObject.Dictionary.Count} indicators; {IndicatorObject.Viewers.Count} players see them)."
			: $"Indicators are hidden for you ({IndicatorObject.Viewers.Count} players still see them).";

		return true;
	}
}
