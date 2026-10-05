namespace ProjectMER.Features.Serializable.Schematics;

public class SchematicObjectDataList
{
	public string Path;

	public int RootObjectId { get; set; }

	public List<SchematicBlockData> Blocks { get; set; } = new();

	/// <summary>
	/// Creates a copy whose blocks and their <see cref="SchematicBlockData.Properties"/> can be changed without touching
	/// this instance (for example the shared, cached parse result).
	/// </summary>
	public SchematicObjectDataList Clone()
	{
		SchematicObjectDataList copy = new()
		{
			Path = Path,
			RootObjectId = RootObjectId,
			Blocks = new List<SchematicBlockData>(Blocks.Count),
		};

		foreach (SchematicBlockData block in Blocks)
			copy.Blocks.Add(block?.Clone()!);

		return copy;
	}
}
