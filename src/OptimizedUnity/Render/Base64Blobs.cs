using System.Text.Json.Nodes;
using OptimizedUnity.Json;

namespace OptimizedUnity.Render;

/// <summary>Long base64 strings (screenshots, captures) are unreadable as text; replace them with their size.</summary>
internal static class Base64Blobs
{
	public const int Threshold = 4 * 1_024;

	/// <summary>Replace each long base64 string under `node` with `&lt;base64 N bytes omitted&gt;`, in place.</summary>
	public static void Omit(JsonNode? node)
	{
		switch (node)
		{
			case JsonObject obj:
				foreach (var key in obj.Select(pair => pair.Key).ToList())
				{
					if (Placeholder(obj[key]) is { } replacement) obj[key] = replacement;
					else Omit(obj[key]);
				}
				break;
			case JsonArray array:
				for (var i = 0; i < array.Count; i++)
				{
					if (Placeholder(array[i]) is { } replacement) array[i] = replacement;
					else Omit(array[i]);
				}
				break;
		}
	}

	static JsonNode? Placeholder(JsonNode? node)
	{
		if (node.Str() is not { Length: >= Threshold } text) return null;
		var payload = text;
		if (payload.StartsWith("data:", StringComparison.Ordinal))
		{
			var comma = payload.IndexOf(";base64,", StringComparison.Ordinal);
			if (comma < 0) return null;
			payload = payload[(comma + ";base64,".Length)..];
		}
		return DecodedLength(payload) is { } bytes ? JsonValue.Create($"<base64 {bytes} bytes omitted>") : null;
	}

	static int? DecodedLength(string payload)
	{
		if (payload.Length % 4 == 1) return null;
		foreach (var ch in payload)
			if (!(char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/' or '=' or '\n' or '\r')) return null;
		var buffer = new byte[payload.Length * 3 / 4 + 3];
		return Convert.TryFromBase64String(payload, buffer, out var written) && written > 0 ? written : null;
	}
}
