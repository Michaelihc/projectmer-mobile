using Newtonsoft.Json;
using ProjectMER.Features.Serializable.Schematics;
using UnityEngine;

namespace ProjectMER.Features.Serialization;

/// <summary>
/// Schematic JSON reading with Newtonsoft.Json (Carl Mod does not ship Utf8Json), plus a parse cache.
/// </summary>
/// <remarks>
/// <see cref="SchematicBlockData.Properties"/> is filled with the same value shapes Utf8Json produced:
/// <see cref="Dictionary{TKey,TValue}"/> of <see cref="string"/> to <see cref="object"/> for objects,
/// <see cref="List{T}"/> of <see cref="object"/> for arrays, <see cref="long"/>/<see cref="double"/> for numbers,
/// <see cref="string"/>, <see cref="bool"/> and <see langword="null"/>. Parsed schematics are cached by path, file
/// length and write time; ProjectMER re-read and re-parsed the file on every spawn.
/// </remarks>
public static class SchematicJson
{
	/// <summary>
	/// Gets the serializer settings used for schematic files.
	/// </summary>
	public static JsonSerializerSettings Settings { get; } = new()
	{
		Converters = { new Vector3JsonConverter(), new PropertiesJsonConverter() },
		MissingMemberHandling = MissingMemberHandling.Ignore,
		NullValueHandling = NullValueHandling.Include,
		FloatParseHandling = FloatParseHandling.Double,
	};

	private static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

	private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Deserializes JSON text.
	/// </summary>
	public static T Deserialize<T>(string json)
	{
		using StringReader stringReader = new(json);
		using JsonTextReader reader = new(stringReader);
		return Serializer.Deserialize<T>(reader)!;
	}

	/// <summary>
	/// Reads a file, reusing the parsed result while the file's length and write time are unchanged.
	/// </summary>
	/// <param name="path">The file path.</param>
	/// <returns>The cached or freshly parsed value. Treat it as read-only.</returns>
	public static T ReadCached<T>(string path)
		where T : class
	{
		FileInfo file = new(path);
		long length = file.Length;
		DateTime writeTime = file.LastWriteTimeUtc;

		if (Cache.TryGetValue(path, out CacheEntry entry) && entry.Length == length && entry.WriteTime == writeTime && entry.Value is T cached)
			return cached;

		T value = Deserialize<T>(File.ReadAllText(path));
		Cache[path] = new CacheEntry(length, writeTime, value);
		return value;
	}

	/// <summary>
	/// Gets or creates data derived from a cached parse result (for example the simplified build plan), dropped
	/// together with the parse result when the file changes.
	/// </summary>
	internal static TDerived GetDerived<TSource, TDerived>(TSource source, Func<TSource, TDerived> factory)
		where TSource : class
		where TDerived : class
	{
		foreach (CacheEntry entry in Cache.Values)
		{
			if (!ReferenceEquals(entry.Value, source))
				continue;

			if (entry.Derived is TDerived derived)
				return derived;

			derived = factory(source);
			entry.Derived = derived;
			return derived;
		}

		return factory(source);
	}

	/// <summary>
	/// Drops every cached file.
	/// </summary>
	public static void ClearCache() => Cache.Clear();

	private sealed class CacheEntry(long length, DateTime writeTime, object value)
	{
		public long Length { get; } = length;

		public DateTime WriteTime { get; } = writeTime;

		public object Value { get; } = value;

		public object? Derived { get; set; }
	}

	/// <summary>
	/// Reads <c>{"x":..,"y":..,"z":..}</c> (any case) and writes lowercase components, as Utf8Json did.
	/// </summary>
	private sealed class Vector3JsonConverter : JsonConverter<Vector3>
	{
		public override Vector3 ReadJson(JsonReader reader, Type objectType, Vector3 existingValue, bool hasExistingValue, JsonSerializer serializer)
		{
			Vector3 result = Vector3.zero;
			if (reader.TokenType == JsonToken.Null)
				return result;

			if (reader.TokenType != JsonToken.StartObject)
				throw new JsonSerializationException($"Expected a vector object, got {reader.TokenType}.");

			while (reader.Read() && reader.TokenType != JsonToken.EndObject)
			{
				string name = (string)reader.Value!;
				reader.Read();
				float value = reader.TokenType == JsonToken.Null ? 0f : Convert.ToSingle(reader.Value, System.Globalization.CultureInfo.InvariantCulture);
				switch (name)
				{
					case "x":
					case "X":
						result.x = value;
						break;
					case "y":
					case "Y":
						result.y = value;
						break;
					case "z":
					case "Z":
						result.z = value;
						break;
				}
			}

			return result;
		}

		public override void WriteJson(JsonWriter writer, Vector3 value, JsonSerializer serializer)
		{
			writer.WriteStartObject();
			writer.WritePropertyName("x");
			writer.WriteValue(value.x);
			writer.WritePropertyName("y");
			writer.WriteValue(value.y);
			writer.WritePropertyName("z");
			writer.WriteValue(value.z);
			writer.WriteEndObject();
		}
	}

	/// <summary>
	/// Reads <c>Dictionary&lt;string, object&gt;</c> without <c>JObject</c>/<c>JArray</c> values.
	/// </summary>
	private sealed class PropertiesJsonConverter : JsonConverter<Dictionary<string, object>>
	{
		public override Dictionary<string, object>? ReadJson(JsonReader reader, Type objectType, Dictionary<string, object>? existingValue, bool hasExistingValue, JsonSerializer serializer)
		{
			if (reader.TokenType == JsonToken.Null)
				return null;

			return (Dictionary<string, object>)ReadValue(reader)!;
		}

		public override void WriteJson(JsonWriter writer, Dictionary<string, object>? value, JsonSerializer serializer)
		{
			if (value == null)
			{
				writer.WriteNull();
				return;
			}

			writer.WriteStartObject();
			foreach (KeyValuePair<string, object> pair in value)
			{
				writer.WritePropertyName(pair.Key);
				serializer.Serialize(writer, pair.Value);
			}

			writer.WriteEndObject();
		}

		private static object? ReadValue(JsonReader reader)
		{
			switch (reader.TokenType)
			{
				case JsonToken.StartObject:
					{
						Dictionary<string, object> dictionary = [];
						while (reader.Read() && reader.TokenType != JsonToken.EndObject)
						{
							string name = (string)reader.Value!;
							reader.Read();
							dictionary[name] = ReadValue(reader)!;
						}

						return dictionary;
					}

				case JsonToken.StartArray:
					{
						List<object> list = [];
						while (reader.Read() && reader.TokenType != JsonToken.EndArray)
							list.Add(ReadValue(reader)!);

						return list;
					}

				case JsonToken.Integer:
					return Convert.ToInt64(reader.Value, System.Globalization.CultureInfo.InvariantCulture);

				case JsonToken.Float:
					return Convert.ToDouble(reader.Value, System.Globalization.CultureInfo.InvariantCulture);

				case JsonToken.Null:
				case JsonToken.Undefined:
					return null;

				default:
					return reader.Value;
			}
		}
	}
}
