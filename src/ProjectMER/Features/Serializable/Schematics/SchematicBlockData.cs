using System.Globalization;
using AdminToys;
using ProjectMER.Features.Enums;
using ProjectMER.Features.Extensions;
using UnityEngine;

namespace ProjectMER.Features.Serializable.Schematics;

/// <summary>
/// One block of an exported schematic.
/// </summary>
/// <remarks>
/// The data model and JSON format are ProjectMER's. Building moved to <see cref="Objects.SchematicObject"/>: blocks are
/// flattened to world space and spawned through the spawn queue (docs/projectmer-port-plan.md §3.2), so ProjectMER's
/// per-block <c>Create</c>, <c>SpawnNestedObjects</c> and <c>SpawnTriangleShared</c> do not exist in the port.
/// </remarks>
public class SchematicBlockData
{
	public virtual string Name { get; set; }

	public virtual int ObjectId { get; set; }

	public virtual int ParentId { get; set; }

	public virtual string AnimatorName { get; set; }

	public virtual Vector3 Position { get; set; }

	public virtual Vector3 Rotation { get; set; }

	public virtual Vector3 Scale { get; set; }

	public virtual BlockType BlockType { get; set; }

	public virtual Dictionary<string, object> Properties { get; set; }

	/// <summary>
	/// Gets the local scale the block's transform uses (an Empty with zero scale counts as one, as in ProjectMER).
	/// </summary>
	public Vector3 EffectiveScale => BlockType == BlockType.Empty && Scale == Vector3.zero ? Vector3.one : Scale;

	/// <summary>
	/// Gets the block's explicit <c>Static</c> property, if present.
	/// </summary>
	public bool? StaticProperty => Properties != null && Properties.TryGetValue("Static", out object value) && value != null ? Convert.ToBoolean(value, CultureInfo.InvariantCulture) : null;

	/// <summary>
	/// Reads a primitive block's type, color and flags.
	/// </summary>
	/// <remarks>
	/// Schematics exported before <c>PrimitiveFlags</c> existed keep ProjectMER's rule: <c>Scale.x &gt;= 0</c> means
	/// collidable.
	/// </remarks>
	public void GetPrimitive(out PrimitiveType primitiveType, out Color color, out PrimitiveFlags primitiveFlags)
	{
		primitiveType = (PrimitiveType)Convert.ToInt32(GetProperty("PrimitiveType", (long)PrimitiveType.Cube), CultureInfo.InvariantCulture);
		color = GetProperty("Color", "#FFFFFF").ToString().GetColorFromString();

		if (Properties != null && Properties.TryGetValue("PrimitiveFlags", out object flags) && flags != null)
		{
			primitiveFlags = (PrimitiveFlags)Convert.ToByte(flags, CultureInfo.InvariantCulture);
		}
		else
		{
			// Backward compatibility
			primitiveFlags = PrimitiveFlags.Visible;
			if (Scale.x >= 0f)
				primitiveFlags |= PrimitiveFlags.Collidable;
		}
	}

	/// <summary>
	/// Reads a primitive block like <see cref="GetPrimitive"/> but leaves the colour as text, so it can run on a worker
	/// thread (<see cref="Serialization.SchematicColor"/> parses it there).
	/// </summary>
	internal void ReadPrimitive(out PrimitiveType primitiveType, out string colorText, out PrimitiveFlags primitiveFlags)
	{
		primitiveType = (PrimitiveType)Convert.ToInt32(GetProperty("PrimitiveType", (long)PrimitiveType.Cube), CultureInfo.InvariantCulture);
		colorText = GetProperty("Color", "#FFFFFF").ToString();

		if (Properties != null && Properties.TryGetValue("PrimitiveFlags", out object flags) && flags != null)
		{
			primitiveFlags = (PrimitiveFlags)Convert.ToByte(flags, CultureInfo.InvariantCulture);
		}
		else
		{
			primitiveFlags = PrimitiveFlags.Visible;
			if (Scale.x >= 0f)
				primitiveFlags |= PrimitiveFlags.Collidable;
		}
	}

	/// <summary>
	/// Reads a light block's intensity and range (worker-thread safe).
	/// </summary>
	internal void ReadLightStrength(out float intensity, out float range)
	{
		intensity = Convert.ToSingle(GetProperty("Intensity", 1d), CultureInfo.InvariantCulture);
		range = Convert.ToSingle(GetProperty("Range", 1d), CultureInfo.InvariantCulture);
	}

	/// <summary>
	/// Reads a light block. Spot lights, shapes, angles and shadow strength do not exist in Carl Mod's light toy.
	/// </summary>
	public void GetLight(out Color color, out float intensity, out float range, out bool shadows, out LightType lightType)
	{
		color = GetProperty("Color", "#FFFFFF").ToString().GetColorFromString();
		intensity = Convert.ToSingle(GetProperty("Intensity", 1d), CultureInfo.InvariantCulture);
		range = Convert.ToSingle(GetProperty("Range", 1d), CultureInfo.InvariantCulture);
		lightType = Properties != null && Properties.TryGetValue("LightType", out object type) && type != null ? (LightType)Convert.ToInt32(type, CultureInfo.InvariantCulture) : LightType.Point;

		if (Properties != null && Properties.TryGetValue("Shadows", out object legacyShadows) && legacyShadows != null)
			shadows = Convert.ToBoolean(legacyShadows, CultureInfo.InvariantCulture); // Backward compatibility
		else
			shadows = Convert.ToInt32(GetProperty("ShadowType", 0L), CultureInfo.InvariantCulture) != (int)LightShadows.None;
	}

	/// <summary>
	/// Reads a pickup block's item. Accepts numbers (ProjectMER) and names (MapEditorReborn 13.2).
	/// </summary>
	/// <returns><see langword="false"/> when the item does not exist in Carl Mod.</returns>
	public bool TryGetItemType(out ItemType itemType, out string rawValue)
	{
		object value = GetProperty("ItemType", (long)ItemType.None);
		rawValue = Convert.ToString(value, CultureInfo.InvariantCulture);

		if (value is string name)
		{
			if (Enum.TryParse(name, true, out itemType) && Enum.IsDefined(typeof(ItemType), itemType))
				return itemType != ItemType.None;

			switch (name.ToLowerInvariant())
			{
				case "keycardmtfprivate":
					itemType = ItemType.KeycardNTFOfficer;
					return true;
				case "keycardmtfoperative":
					itemType = ItemType.KeycardNTFLieutenant;
					return true;
				case "keycardmtfcaptain":
					itemType = ItemType.KeycardNTFCommander;
					return true;
				case "lantern":
					itemType = ItemType.Flashlight;
					return true;
			}

			return false;
		}

		itemType = (ItemType)Convert.ToInt32(value, CultureInfo.InvariantCulture);
		return itemType != ItemType.None && Enum.IsDefined(typeof(ItemType), itemType);
	}

	public object GetProperty(string name, object fallback)
	{
		if (Properties != null && Properties.TryGetValue(name, out object value) && value != null)
			return value;

		return fallback;
	}

	public Vector3 GetVectorProperty(string name, Vector3 fallback)
	{
		if (Properties == null || !Properties.TryGetValue(name, out object value))
			return fallback;

		if (value is IDictionary<string, object> dictionary)
		{
			float x = Convert.ToSingle(dictionary.TryGetValue("x", out object xValue) ? xValue : dictionary.TryGetValue("X", out xValue) ? xValue : 0f, CultureInfo.InvariantCulture);
			float y = Convert.ToSingle(dictionary.TryGetValue("y", out object yValue) ? yValue : dictionary.TryGetValue("Y", out yValue) ? yValue : 0f, CultureInfo.InvariantCulture);
			float z = Convert.ToSingle(dictionary.TryGetValue("z", out object zValue) ? zValue : dictionary.TryGetValue("Z", out zValue) ? zValue : 0f, CultureInfo.InvariantCulture);

			return new Vector3(x, y, z);
		}

		if (value is string vectorString)
			return vectorString.ToVector3();

		return fallback;
	}

	/// <summary>
	/// Creates a copy whose <see cref="Properties"/> dictionary can be changed without touching the cached original.
	/// </summary>
	public SchematicBlockData Clone()
	{
		SchematicBlockData clone = (SchematicBlockData)MemberwiseClone();
		if (Properties != null)
			clone.Properties = new Dictionary<string, object>(Properties);

		return clone;
	}
}
