using System.Text.Json.Nodes;
using OptimizedUnity.Json;

namespace OptimizedUnity.Render;

internal static class Pruner
{
	/// <summary>Fields that are provably redundant, echoed back, or actively misleading.</summary>
	static readonly HashSet<string> Noise =
	[
		"executedAt",
		"executionTimeMs",
		"schema", // verbatim JSON-Schema duplicate of `parameters`
		"x-command-metadata",
		"resolvedPath", // hash-suffixed absolute PackageCache path
		"sourceFingerprint", // ~300 opaque chars per project
		"localProjectId",
		"hasCustomDisplayName",
		"openInEditorUrl",
		"globalId",
		"instanceId", // opaque ids; prefer hierarchyPath or assetPath
	];

	/// <summary>key → the richer key that supersedes it when both are present.</summary>
	static readonly Dictionary<string, string> Superseded = new()
	{
		["level"] = "levelName",
		["time"] = "timestamp",
		["name"] = "moduleName",
	};

	static readonly HashSet<string> IdHeavyCommands =
	[
		"component get_properties", "component set_properties", "component get_fields", "component set_field",
		"get_component_properties", "set_component_properties", "get_serialized_fields", "get_serialized_field",
		"set_serialized_field",
	];
	static readonly HashSet<string> ObjectIdFields = ["guid", "fileId"];
	static readonly HashSet<string> None = [];

	public static IReadOnlySet<string> CommandFields(string? command) =>
		command is not null && IdHeavyCommands.Contains(command) ? ObjectIdFields : None;

	/// <summary>Recursively drop global noise, command-specific fields, and empty values. Returns a new tree.</summary>
	public static JsonNode? Prune(JsonNode? node, IReadOnlySet<string>? remove = null)
	{
		remove ??= None;
		switch (node)
		{
			case JsonObject obj:
			{
				var result = new JsonObject();
				foreach (var (key, raw) in obj)
				{
					if (Noise.Contains(key) || remove.Contains(key)) continue;
					if (Superseded.TryGetValue(key, out var better) && obj.ContainsKey(better)) continue;
					var value = Prune(raw, remove);
					if (value.IsEmpty() || RepeatsSibling(result, key, value)) continue;
					result[key] = value;
				}
				return result;
			}
			case JsonArray array:
			{
				var result = new JsonArray();
				foreach (var item in array)
				{
					var value = Prune(item, remove);
					if (!value.IsEmpty()) result.Add(value);
				}
				return result;
			}
			default:
				return node?.DeepClone();
		}
	}

	/// <summary>
	/// `project`/`projectPath`, `version`/`editorVersion`: an alias field whose name contains (or is
	/// contained in) an earlier sibling's name, carrying the same string, adds nothing.
	/// </summary>
	static bool RepeatsSibling(JsonObject kept, string key, JsonNode? value)
	{
		if (value.Str() is not { Length: > 0 } text) return false;
		foreach (var (other, existing) in kept)
		{
			if (existing.Str() != text) continue;
			if (key.Contains(other, StringComparison.OrdinalIgnoreCase) || other.Contains(key, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	/// <summary>Listing responses echo the Editor endpoint they used; it is never the answer.</summary>
	public static JsonNode? WithoutEndpoint(JsonNode? data)
	{
		if (data is not JsonObject obj) return data;
		var clone = new JsonObject();
		foreach (var (key, value) in obj)
		{
			if (key is "target" or "server" && value is JsonObject endpoint && endpoint.ContainsKey("port")) continue;
			clone[key] = value?.DeepClone();
		}
		return clone;
	}
}
