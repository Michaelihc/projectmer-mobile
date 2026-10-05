using System.Globalization;
using UnityEngine;

namespace ProjectMER.Features.Serialization;

/// <summary>
/// Parses schematic colour strings without Unity calls, so schematics can be planned on a worker thread.
/// </summary>
/// <remarks>
/// Follows <c>StructExtensions.GetColorFromString</c>: <c>r:g:b:a</c> (0-255 channels, alpha 0-1), <c>#RGB</c>,
/// <c>#RGBA</c>, <c>#RRGGBB</c>, <c>#RRGGBBAA</c>, and 8 hex digits without <c>#</c>. Invalid hex gives
/// <c>Color.magenta * 3</c> as there. Named colours (<c>red</c>, <c>teal</c>...) need Unity's
/// <c>ColorUtility</c>; <see cref="TryParse"/> returns <see langword="false"/> for them and the main thread resolves them.
/// </remarks>
internal static class SchematicColor
{
	/// <summary>
	/// The colour <c>GetColorFromString</c> returns for unparsable text.
	/// </summary>
	public static Color Invalid => new(3f, 0f, 3f, 3f);

	/// <summary>
	/// Parses a colour.
	/// </summary>
	/// <param name="text">The colour text.</param>
	/// <param name="color">The colour, when the result is <see langword="true"/>.</param>
	/// <returns><see langword="false"/> only for text that only Unity can resolve (named colours).</returns>
	public static bool TryParse(string? text, out Color color)
	{
		color = Invalid;
		if (string.IsNullOrEmpty(text))
			return true;

		string[] parts = text!.Split(':');
		if (parts.Length >= 4)
		{
			Color parsed = new(-1f, -1f, -1f);
			if (TryParseFloat(parts[0], out float red))
				parsed.r = red / 255f;

			if (TryParseFloat(parts[1], out float green))
				parsed.g = green / 255f;

			if (TryParseFloat(parts[2], out float blue))
				parsed.b = blue / 255f;

			if (TryParseFloat(parts[3], out float alpha))
				parsed.a = alpha;

			color = parsed != new Color(-1f, -1f, -1f) ? parsed : Invalid;
			return true;
		}

		if (text[0] != '#' && text.Length == 8)
			text = '#' + text;

		if (text[0] != '#')
			return false;

		int digits = text.Length - 1;
		if (digits is not (3 or 4 or 6 or 8))
			return true;

		uint value = 0;
		for (int i = 1; i < text.Length; i++)
		{
			int nibble = HexValue(text[i]);
			if (nibble < 0)
				return true;

			value = (value << 4) | (uint)nibble;
		}

		byte r, g, b, a = 255;
		switch (digits)
		{
			case 3:
				r = (byte)(((value >> 8) & 0xF) * 17);
				g = (byte)(((value >> 4) & 0xF) * 17);
				b = (byte)((value & 0xF) * 17);
				break;
			case 4:
				r = (byte)(((value >> 12) & 0xF) * 17);
				g = (byte)(((value >> 8) & 0xF) * 17);
				b = (byte)(((value >> 4) & 0xF) * 17);
				a = (byte)((value & 0xF) * 17);
				break;
			case 6:
				r = (byte)((value >> 16) & 0xFF);
				g = (byte)((value >> 8) & 0xFF);
				b = (byte)(value & 0xFF);
				break;
			default:
				r = (byte)((value >> 24) & 0xFF);
				g = (byte)((value >> 16) & 0xFF);
				b = (byte)((value >> 8) & 0xFF);
				a = (byte)(value & 0xFF);
				break;
		}

		color = new Color32(r, g, b, a);
		return true;
	}

	private static bool TryParseFloat(string s, out float result) =>
		float.TryParse(s.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out result);

	private static int HexValue(char c) => c switch
	{
		>= '0' and <= '9' => c - '0',
		>= 'a' and <= 'f' => c - 'a' + 10,
		>= 'A' and <= 'F' => c - 'A' + 10,
		_ => -1,
	};
}
