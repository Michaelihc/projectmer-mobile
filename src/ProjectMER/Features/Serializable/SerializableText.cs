using LabApi.Features.Wrappers;
using UnityEngine;

namespace ProjectMER.Features.Serializable;

/// <summary>
/// A text of a map. Data only: the Carl Mod client has no TextToy, so it is skipped with a warning and kept in saved maps.
/// </summary>
public class SerializableText : SerializableObject
{
	/// <summary>
	/// The official <c>TextToy.DefaultDisplaySize</c> (200, 50).
	/// </summary>
	public static readonly Vector3 DefaultDisplaySize = new(200f, 50f, 0f);

	public string Text { get; set; } = "Custom Text";

	public Vector3 DisplaySize { get; set; } = DefaultDisplaySize;

	public override GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		_prevIndex = Index;
		UnsupportedContent.Skip("texts", "TextToy is not available in the Carl Mod client");
		return null;
	}
}
