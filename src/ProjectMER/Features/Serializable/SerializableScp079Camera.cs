using LabApi.Features.Wrappers;
using UnityEngine;
using CameraType = ProjectMER.Features.Enums.CameraType;

namespace ProjectMER.Features.Serializable;

/// <summary>
/// An SCP-079 camera of a map. Data only: Carl Mod has no Scp079CameraToy (its cameras are scene objects), so it is
/// skipped with a warning and kept in saved maps.
/// </summary>
public class SerializableScp079Camera : SerializableObject
{
	public CameraType CameraType { get; set; } = CameraType.Lcz;
	public string Label { get; set; } = "CustomCamera";

	public override GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null)
	{
		_prevIndex = Index;
		UnsupportedContent.Skip("SCP-079 cameras", "Scp079CameraToy is not available in the Carl Mod client");
		return null;
	}
}
