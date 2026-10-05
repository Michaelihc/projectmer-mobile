using LabApi.Features.Wrappers;
using UnityEngine;

namespace ProjectMER.Features.Serializable;

/// <summary>
/// A capybara of a map. Data only: the Carl Mod client has no CapybaraToy, so it is skipped with a warning and kept in
/// saved maps.
/// </summary>
public class SerializableCapybara : SerializableObject
{
	public override GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		_prevIndex = Index;
		UnsupportedContent.Skip("capybaras", "CapybaraToy is not available in the Carl Mod client");
		return null;
	}
}
