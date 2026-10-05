using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using ProjectMER.Features.Serializable.Schematics;
using UnityEngine;

namespace ProjectMER.Features.Serialization;

/// <summary>
/// Schematic JSON reading with Newtonsoft.Json (Carl Mod does not ship Utf8Json), plus a parse cache.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SchematicBlockData.Properties"/> is filled with the same value shapes Utf8Json produced:
/// <see cref="Dictionary{TKey,TValue}"/> of <see cref="string"/> to <see cref="object"/> for objects,
/// <see cref="List{T}"/> of <see cref="object"/> for arrays, <see cref="long"/>/<see cref="double"/> for numbers,
/// <see cref="string"/>, <see cref="bool"/> and <see langword="null"/>. Parsed files are cached by path, file length and
/// write time; ProjectMER re-read and re-parsed the file on every spawn. Derived data (build plans) is cached with the
/// parse result and dropped with it when the file changes.
/// </para>
/// <para>
/// The cache is main-thread only. <see cref="SchematicLoader"/> parses on worker threads with their own serializer and
/// stores the result here when the main thread picks it up.
/// </para>
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

	private static ConditionalWeakTable<object, CacheEntry> _byValue = new();

	/// <summary>
	/// Deserializes JSON text (main thread).
	/// </summary>
	public static T Deserialize<T>(string json) => Deserialize<T>(json, Serializer);

	/// <summary>
	/// Reads a file, reusing the parsed result while the file's length and write time are unchanged (main thread).
	/// </summary>
	/// <param name="path">The file path.</param>
	/// <returns>The cached or freshly parsed value. Treat it as read-only.</returns>
	public static T ReadCached<T>(string path)
		where T : class
	{
		FileInfo file = new(path);
		FileStamp stamp = new(file.Length, file.LastWriteTimeUtc);
		if (TryGetCached(path, stamp, out T cached))
			return cached;

		T value = DeserializeFile<T>(path, Serializer);
		Store(path, stamp, value);
		return value;
	}

	/// <summary>
	/// Drops every cached file.
	/// </summary>
	public static void ClearCache()
	{
		Cache.Clear();
		_byValue = new ConditionalWeakTable<object, CacheEntry>();
	}

	/// <summary>
	/// Creates a serializer for one worker-thread job (serializer instances are not shared across threads).
	/// </summary>
	internal static JsonSerializer CreateSerializer() => JsonSerializer.Create(Settings);

	/// <summary>
	/// Deserializes JSON text with a given serializer.
	/// </summary>
	internal static T Deserialize<T>(string json, JsonSerializer serializer)
	{
		using StringReader stringReader = new(json);
		using JsonTextReader reader = new(stringReader);
		return serializer.Deserialize<T>(reader)!;
	}

	/// <summary>
	/// Deserializes a file by streaming it (no intermediate string the size of the file).
	/// </summary>
	internal static T DeserializeFile<T>(string path, JsonSerializer serializer)
	{
		using StreamReader streamReader = new(path, System.Text.Encoding.UTF8, true, 64 * 1024);
		using JsonTextReader reader = new(streamReader);
		return serializer.Deserialize<T>(reader)!;
	}

	/// <summary>
	/// Gets a cached value whose file still has the given stamp.
	/// </summary>
	internal static bool TryGetCached<T>(string path, FileStamp stamp, out T value)
		where T : class
	{
		if (Cache.TryGetValue(path, out CacheEntry entry) && entry.Stamp == stamp && entry.Value is T cached)
		{
			value = cached;
			return true;
		}

		value = null!;
		return false;
	}

	/// <summary>
	/// Caches a parsed value unless an entry with the same stamp exists.
	/// </summary>
	/// <returns>Whether <paramref name="value"/> is now the cached instance.</returns>
	internal static bool Store(string path, FileStamp stamp, object value)
	{
		if (Cache.TryGetValue(path, out CacheEntry existing) && existing.Stamp == stamp)
			return ReferenceEquals(existing.Value, value);

		CacheEntry entry = new(stamp, value);
		Cache[path] = entry;
		_byValue.Remove(value);
		_byValue.Add(value, entry);
		return true;
	}

	/// <summary>
	/// Gets the table of data derived from a cached value (for example build plans by settings), or
	/// <see langword="null"/> when <paramref name="source"/> is not a cached value.
	/// </summary>
	internal static Dictionary<TKey, TValue>? GetDerivedTable<TKey, TValue>(object source)
	{
		if (!_byValue.TryGetValue(source, out CacheEntry entry))
			return null;

		if (entry.Derived is not Dictionary<TKey, TValue> table)
			entry.Derived = table = [];

		return table;
	}

	/// <summary>
	/// The length and write time of a file, which together identify a cached parse.
	/// </summary>
	/// <param name="Length">The file length, or -1 when the file does not exist.</param>
	/// <param name="WriteTime">The last write time (UTC).</param>
	internal readonly record struct FileStamp(long Length, DateTime WriteTime)
	{
		/// <summary>
		/// The stamp of a missing file.
		/// </summary>
		public static readonly FileStamp Missing = new(-1, default);

		/// <summary>
		/// Gets the stamp of a file.
		/// </summary>
		public static FileStamp Of(string? path)
		{
			if (path == null)
				return Missing;

			FileInfo file = new(path);
			return file.Exists ? new FileStamp(file.Length, file.LastWriteTimeUtc) : Missing;
		}
	}

	private sealed class CacheEntry(FileStamp stamp, object value)
	{
		public FileStamp Stamp { get; } = stamp;

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
