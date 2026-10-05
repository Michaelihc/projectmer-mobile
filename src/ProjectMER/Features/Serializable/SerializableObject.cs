using LabApi.Features.Wrappers;
using UnityEngine;
using YamlDotNet.Serialization;

namespace ProjectMER.Features.Serializable;

public abstract class SerializableObject
{
	/// <summary>
	/// Gets or sets the objects's position.
	/// </summary>
	public virtual Vector3 Position { get; set; } = Vector3.zero;

	/// <summary>
	/// Gets or sets the objects's rotation.
	/// </summary>
	public virtual Vector3 Rotation { get; set; } = Vector3.zero;

	/// <summary>
	/// Gets or sets the objects's scale.
	/// </summary>
	public virtual Vector3 Scale { get; set; } = Vector3.one;

	public virtual string Room { get; set; } = "Unknown";

	public virtual int Index { get; set; } = -1;

	/// <summary>
	/// Creates the object (<paramref name="instance"/> is <see langword="null"/>) or updates an existing copy.
	/// </summary>
	/// <remarks>
	/// In the port, new networked objects are prepared immediately and network spawned by the spawn queue. Types that the
	/// Carl Mod client lacks return <see langword="null"/> (and report one warning per type per load).
	/// </remarks>
	public virtual GameObject? SpawnOrUpdateObject(Room? room = null, GameObject? instance = null) => throw new NotSupportedException();

	[YamlIgnore]
	public virtual bool RequiresReloading => Index != _prevIndex;

	public int _prevIndex;
}
