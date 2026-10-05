using Hints;
using LabApi.Features.Wrappers;
using ProjectMER.Configs;
using ProjectMER.Features.Extensions;
using ProjectMER.Features.Objects;
using UnityEngine;

namespace ProjectMER.Features.ToolGun;

/// <summary>
/// Sends the tool gun HUD (docs/projectmer-port-plan.md §4): a compact <see cref="TextHint"/> that is rebuilt only when
/// what it shows changes, sent at once on a change and re-sent unchanged as a keep-alive.
/// </summary>
/// <remarks>
/// <para>
/// The client accepts at most 10 hints per 5 s (<c>HintDisplay._showRateLimit</c>) and silently drops the rest, so the HUD
/// sends at most <see cref="MaxPerWindow"/> per <see cref="Window"/> (sliding) and at least <see cref="MinSpacing"/> apart;
/// a change that does not fit is sent as soon as it does. Each hint lasts <c>keep-alive + 1 s</c>, and the keep-alive is
/// <c>max(2 s, 4 × hud_interval)</c>. Hints stack on the client, so hiding the HUD sends a blank hint that outlasts the
/// last one.
/// </para>
/// <para>
/// Every <c>hud_interval</c> <see cref="ToolGunLoop"/> compares the inputs (mode, aim, type, selection, the object under
/// the crosshair in Delete/Select, room); the text is built only when one of them changed.
/// </para>
/// </remarks>
internal static class ToolGunHud
{
	/// <summary>
	/// Hints the HUD may send per <see cref="Window"/>; the client allows 10, the rest is left for other hints.
	/// </summary>
	public const int MaxPerWindow = 8;

	/// <summary>
	/// The client's rate-limit window in seconds.
	/// </summary>
	public const float Window = 5f;

	/// <summary>
	/// Minimum seconds between two HUD hints.
	/// </summary>
	public const float MinSpacing = 0.25f;

	// One shared, never-mutated parameter: TextHint formats its text with string.Format when it has parameters, and
	// official LabAPI always sends one (an empty string), so the HUD escapes braces in names.
	private static readonly HintParameter[] Parameters = [new StringHintParameter(string.Empty)];

	/// <summary>
	/// Gets the number of HUD hints sent (changes, keep-alives and clears) since the plugin was enabled.
	/// </summary>
	public static int Sent { get; private set; }

	/// <summary>
	/// Gets the number of hints sent because the content changed.
	/// </summary>
	public static int Changes { get; private set; }

	/// <summary>
	/// Gets the number of keep-alive hints.
	/// </summary>
	public static int KeepAlives { get; private set; }

	/// <summary>
	/// Gets the number of blank hints sent to hide the HUD.
	/// </summary>
	public static int Clears { get; private set; }

	/// <summary>
	/// Gets the number of times a due hint waited for the rate limit.
	/// </summary>
	public static int Deferred { get; private set; }

	/// <summary>
	/// Gets the number of times the HUD text was rebuilt.
	/// </summary>
	public static int Rebuilds { get; private set; }

	/// <summary>
	/// Gets the keep-alive interval in seconds.
	/// </summary>
	public static float KeepAlive => Math.Max(2f, 4f * HudInterval);

	/// <summary>
	/// Gets how often the HUD inputs are checked.
	/// </summary>
	public static float HudInterval => Math.Max(0.2f, ProjectMER.Singleton?.Config?.HudInterval ?? 0.5f);

	/// <summary>
	/// Updates a player's HUD now (after an input), within the rate limit.
	/// </summary>
	public static void Refresh(ToolGunState state)
	{
		Update(state, Time.realtimeSinceStartup);
		ToolGunLoop.EnsureRunning();
	}

	/// <summary>
	/// Checks the HUD inputs and sends what is due.
	/// </summary>
	/// <returns>Whether the player still needs HUD updates.</returns>
	public static bool Update(ToolGunState state, float now)
	{
		Player player = state.Player;
		PlayerHud hud = state.Hud;
		if (player.IsHost || player.ReferenceHub == null)
			return false;

		bool holding = player.CurrentItem.IsToolGun(out ToolGunItem toolGun);
		MapEditorObject? selected = state.Selected;
		if (!holding && selected == null && state.Grab == null)
		{
			hud.Built = false;
			if (hud.ExpiresAt <= now)
				return false;

			// Hints stack on the client: cover the remaining time of the last HUD hint with a blank one.
			if (!CanSend(hud, now))
			{
				Deferred++;
				return true;
			}

			Send(player, hud, " ", hud.ExpiresAt - now + 0.25f, now);
			hud.ExpiresAt = now;
			Clears++;
			return false;
		}

		bool aiming = holding && toolGun.Aiming;
		MapEditorObject? target = null;
		if (holding && (aiming || state.Mode == ToolGunMode.Delete))
			ToolGunHandler.TryGetMapObject(player, out target);

		Room? room = null;
		if (holding && player.Camera != null)
			room = RoomExtensions.GetRoomAtPosition(player.Camera.position);

		bool selectedDirty = selected != null && MapUtils.LoadedMaps.TryGetValue(selected.MapName, out Serializable.MapSchematic map) && map.IsDirty;
		bool grabbing = state.Grab != null;
		bool proxy = grabbing && state.Grab!.UsesProxy;

		if (!hud.Built || hud.Holding != holding || hud.Aiming != aiming || hud.Mode != state.Mode || hud.Type != state.ObjectType ||
			!ReferenceEquals(hud.Schematic, state.SchematicName) || !ReferenceEquals(hud.Selected, selected) || hud.SelectedDirty != selectedDirty ||
			!ReferenceEquals(hud.Target, target) || !ReferenceEquals(hud.Room, room) || hud.Grabbing != grabbing || hud.Proxy != proxy ||
			hud.SchematicCount != ToolGunItem.SchematicNames.Length)
		{
			hud.Built = true;
			hud.Holding = holding;
			hud.Aiming = aiming;
			hud.Mode = state.Mode;
			hud.Type = state.ObjectType;
			hud.Schematic = state.SchematicName;
			hud.Selected = selected;
			hud.SelectedDirty = selectedDirty;
			hud.Target = target;
			hud.Room = room;
			hud.Grabbing = grabbing;
			hud.Proxy = proxy;
			hud.SchematicCount = ToolGunItem.SchematicNames.Length;

			string text = ToolGunUI.Build(state, holding, aiming, selected, target, room);
			Rebuilds++;
			if (!string.Equals(text, hud.SentText, StringComparison.Ordinal))
			{
				hud.Text = text;
				hud.Dirty = true;
			}
		}

		bool keepAliveDue = now - hud.LastSent >= KeepAlive || hud.ExpiresAt <= now;
		if (!hud.Dirty && !keepAliveDue)
			return true;

		if (!CanSend(hud, now))
		{
			Deferred++;
			return true;
		}

		if (hud.Dirty)
			Changes++;
		else
			KeepAlives++;

		float duration = KeepAlive + 1f;
		Send(player, hud, hud.Text, duration, now);
		hud.SentText = hud.Text;
		hud.Dirty = false;
		hud.ExpiresAt = now + duration;
		return true;
	}

	/// <summary>
	/// Gets the text last sent to a player, for diagnostics.
	/// </summary>
	public static string GetSentText(ToolGunState state) => state.Hud.SentText;

	private static bool CanSend(PlayerHud hud, float now) =>
		now - hud.LastSent >= MinSpacing && now - hud.SendTimes[hud.SendIndex] >= Window;

	private static void Send(Player player, PlayerHud hud, string text, float duration, float now)
	{
		hud.LastSent = now;
		hud.SendTimes[hud.SendIndex] = now;
		hud.SendIndex = (hud.SendIndex + 1) % MaxPerWindow;
		Sent++;
		player.ReferenceHub.hints.Show(new TextHint(text, Parameters, null, duration));
	}

	/// <summary>
	/// The HUD state of one player.
	/// </summary>
	internal sealed class PlayerHud
	{
		public PlayerHud()
		{
			for (int i = 0; i < SendTimes.Length; i++)
				SendTimes[i] = float.NegativeInfinity;
		}

		public readonly float[] SendTimes = new float[MaxPerWindow];

		public int SendIndex;

		public float LastSent = float.NegativeInfinity;

		public float ExpiresAt = float.NegativeInfinity;

		public string Text = string.Empty;

		public string SentText = string.Empty;

		public bool Dirty;

		public bool Built;

		public bool Holding;

		public bool Aiming;

		public ToolGunMode Mode;

		public Enums.ToolGunObjectType Type;

		public string? Schematic;

		public MapEditorObject? Selected;

		public bool SelectedDirty;

		public MapEditorObject? Target;

		public Room? Room;

		public bool Grabbing;

		public bool Proxy;

		public int SchematicCount;
	}
}
