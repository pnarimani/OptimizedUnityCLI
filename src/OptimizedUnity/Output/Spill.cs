using System.Text;
using System.Text.Json.Nodes;
using OptimizedUnity.Json;

namespace OptimizedUnity.Output;

/// <summary>Files written beside the output so large payloads never reach the caller's context.</summary>
internal static class Spill
{
	public const int BlobThreshold = 4 * 1_024;
	static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

	static string Root =>
		Environment.GetEnvironmentVariable("UNITY_WRAP_SPILL_DIR") is { Length: > 0 } configured
			? configured
			: Path.Combine(Path.GetTempPath(), "optimized-unity");

	/// <summary>Write `bytes` to a fresh file; null when the filesystem refuses.</summary>
	public static string? Write(string label, string extension, byte[] bytes)
	{
		try
		{
			Directory.CreateDirectory(Root);
			var safe = new StringBuilder();
			foreach (var ch in label) safe.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
			var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{safe.ToString().Trim('-')}-{Guid.NewGuid().ToString("N")[..6]}.{extension}";
			var path = Path.Combine(Root, name);
			File.WriteAllBytes(path, bytes);
			return path;
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// Replace long base64 strings (screenshots, captures) with a pointer to the decoded file,
	/// in place.
	/// </summary>
	public static void Blobs(JsonNode? node, string label)
	{
		switch (node)
		{
			case JsonObject obj:
				foreach (var key in obj.Select(pair => pair.Key).ToList())
				{
					if (TryBlob(obj[key], key, label) is { } replacement) obj[key] = replacement;
					else Blobs(obj[key], label);
				}
				break;
			case JsonArray array:
				for (var i = 0; i < array.Count; i++)
				{
					if (TryBlob(array[i], "item", label) is { } replacement) array[i] = replacement;
					else Blobs(array[i], label);
				}
				break;
		}
	}

	static JsonNode? TryBlob(JsonNode? node, string key, string label)
	{
		if (node.Str() is not { Length: >= BlobThreshold } text) return null;
		var payload = text;
		if (payload.StartsWith("data:", StringComparison.Ordinal))
		{
			var comma = payload.IndexOf(";base64,", StringComparison.Ordinal);
			if (comma < 0) return null;
			payload = payload[(comma + ";base64,".Length)..];
		}
		var bytes = Decode(payload);
		if (bytes is null) return null;
		var extension = bytes.AsSpan().StartsWith(PngMagic) ? "png" : "bin";
		var path = Write($"{label}-{key}", extension, bytes);
		return path is null ? null : JsonValue.Create($"<base64 {bytes.Length} bytes → {path}>");
	}

	static byte[]? Decode(string payload)
	{
		if (payload.Length % 4 == 1) return null;
		foreach (var ch in payload)
			if (!(char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/' or '=' or '\n' or '\r')) return null;
		var buffer = new byte[payload.Length * 3 / 4 + 3];
		return Convert.TryFromBase64String(payload, buffer, out var written) && written > 0 ? buffer[..written] : null;
	}
}
