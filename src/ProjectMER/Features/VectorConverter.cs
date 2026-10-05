using ProjectMER.Features.Extensions;
using UnityEngine;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace ProjectMER.Features;

public sealed class VectorConverter : IYamlTypeConverter
{
	/// <inheritdoc cref="IYamlTypeConverter" />
	public bool Accepts(Type type) => type == typeof(Vector3);

	/// <inheritdoc cref="IYamlTypeConverter" />
	public object ReadYaml(IParser parser, Type type)
	{
		string s = parser.Consume<Scalar>().Value;
		return s.ToVector3();
	}

	/// <inheritdoc cref="IYamlTypeConverter" />
	public void WriteYaml(IEmitter emitter, object? value, Type type) => emitter.Emit(new Scalar(((Vector3)value!).ToString("F3")));

	/// <inheritdoc />
	/// <remarks>YamlDotNet 16+ interface member (Carl Mod ships YamlDotNet 18).</remarks>
	public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) => ReadYaml(parser, type);

	/// <inheritdoc />
	/// <remarks>YamlDotNet 16+ interface member (Carl Mod ships YamlDotNet 18).</remarks>
	public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) => WriteYaml(emitter, value, type);
}
