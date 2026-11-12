using System.Text.Json;

namespace OptimizedUnity.Json;

/// <summary>Tolerant reads from a `JsonDocument`, for payloads too large to load as `JsonNode`s.</summary>
static class JsonElements
{
	static readonly JsonElement EmptyArray = JsonDocument.Parse("[]").RootElement.Clone();

	public static string? String(JsonElement node, string key) =>
		Property(node, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

	public static long? Long(JsonElement node, string key) =>
		Property(node, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;

	/// <summary>The array at `key`, or an empty one.</summary>
	public static JsonElement Array(JsonElement node, string key) =>
		Property(node, key) is { ValueKind: JsonValueKind.Array } value ? value : EmptyArray;

	/// <summary>A string, number or boolean as text; null for null and containers.</summary>
	public static string? Scalar(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.String => value.GetString(),
		JsonValueKind.Number => value.GetRawText(),
		JsonValueKind.True => "true",
		JsonValueKind.False => "false",
		_ => null,
	};

	static JsonElement? Property(JsonElement node, string key) =>
		node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value) ? value : null;
}
