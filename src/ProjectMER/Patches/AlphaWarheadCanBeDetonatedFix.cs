using HarmonyLib;
using MapGeneration;
using UnityEngine;

namespace ProjectMER.Patches;

/// <summary>
/// Keeps ProjectMER's warhead rule on Carl Mod: positions below the surface that are outside every room (custom MER areas)
/// survive the detonation. Behind <c>warhead_spares_outside_rooms</c>.
/// </summary>
/// <remarks>
/// Carl Mod kills everything below y = 900 (<c>pos.y &lt; 900f</c>); SL 14.x uses the facility zone, which ProjectMER
/// extended to spare zone <c>None</c>. Lifts are still checked by the original method.
/// </remarks>
// Official: AlphaWarheadController.cs AlphaWarheadController.CanBeDetonated
[HarmonyPatch(typeof(AlphaWarheadController), nameof(AlphaWarheadController.CanBeDetonated))]
public static class AlphaWarheadCanBeDetonatedFix
{
	public static bool Prefix(Vector3 pos, bool includeOnlyLifts, ref bool __result)
	{
		if (includeOnlyLifts || pos.y >= 900f || ProjectMER.Singleton?.Config?.WarheadSparesOutsideRooms != true)
			return true;

		if (RoomIdUtils.RoomAtPositionRaycasts(pos, prioritizeRaycast: false) != null)
			return true;

		// Outside every room: only a lift (checked by the original with includeOnlyLifts) is dangerous.
		__result = AlphaWarheadController.CanBeDetonated(pos, includeOnlyLifts: true);
		return false;
	}
}
