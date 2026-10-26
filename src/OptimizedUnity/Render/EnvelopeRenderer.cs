using System.Text.Json.Nodes;
using OptimizedUnity.Json;

namespace OptimizedUnity.Render;

internal static class EnvelopeRenderer
{
	/// <summary>Inner Pipeline envelope fields that repeat the outer one.</summary>
	static readonly HashSet<string> InnerEnvelope = ["success", "command", "executionTimeMs", "executedAt", "errorDetails"];

	public static List<string> Render(JsonNode envelope)
	{
		var lines = new List<string>();
		foreach (var error in envelope.Get("errors").Items())
		{
			var code = error is JsonObject ? Format.Fmt(error.Get("code")) : "";
			var body = Format.MsgLines(error is JsonObject ? error.Get("message") : error);
			lines.Add($"{(code.Length > 0 ? $"ERROR: {code}: " : "ERROR: ")}{body[0]}");
			lines.AddRange(body.Skip(1));
		}
		foreach (var warning in envelope.Get("warnings").Items())
		{
			if (IsChrome(warning)) continue;
			var body = Format.MsgLines(warning);
			lines.Add($"WARN: {body[0]}");
			lines.AddRange(body.Skip(1));
		}
		// A failed envelope can still carry the answer (e.g. status listing unreachable Editors).
		var data = envelope.Get("data");
		if (envelope.Get("success").Truthy() || !data.IsEmpty())
			lines.AddRange(envelope.Get("command").Str() == "status" && data.Get("instances") is JsonArray instances
				? Status(instances)
				: RenderData(data));
		return lines;
	}

	/// <summary>One line per Editor with just what callers act on; `UNITY_WRAP=off unity status` has the rest.</summary>
	static List<string> Status(JsonArray instances)
	{
		var lines = instances.OfType<JsonObject>()
			.Select(instance => string.Join("  ", ((string[])["state", "pid", "project"])
				.Where(key => instance.Get(key).Truthy())
				.Select(key => $"{key}: {Format.Fmt(instance.Get(key))}")))
			.Where(line => line.Length > 0)
			.ToList();
		return lines.Count > 0 ? lines : ["(no Editors running)"];
	}

	/// <summary>The CLI puts UI decoration (pagination separators) in `warnings`.</summary>
	static bool IsChrome(JsonNode? warning)
	{
		var text = Format.Fmt(warning).Trim();
		return text.Length == 0 || "-─—".Contains(text[0]);
	}

	/// <summary>`unity command &lt;name&gt;` wrapper: {command, parameters, result, target}.</summary>
	public static bool IsEditorWrapper(JsonNode? data) =>
		data is JsonObject && data.Has("command") && data.Has("result") && data.Has("target");

	public static List<string> RenderData(JsonNode? data)
	{
		if (IsEditorWrapper(data)) return RenderEditor(data.Get("command").Str(), data.Get("result"));

		// An empty search is reported by the caller; pruning would leave only endpoint metadata.
		if (data.First("commands", "tools") is JsonArray { Count: 0 }) return [];

		var result = Pruner.Prune(Pruner.WithoutEndpoint(data));
		if (result.IsEmpty()) return ["ok"];
		if (result is JsonObject)
		{
			if (result.Has("jobId")) return EditorRenderers.Job(result);
			if (result.Has("commands") || result.Has("tools")) return EditorRenderers.CommandCatalog(result);
			if (result.Has("groups"))
				return EditorRenderers.NameListing(result, "groups", "group", "(unity command --query <group> lists its commands)");
			if (result.Has("tags"))
				return EditorRenderers.NameListing(result, "tags", "tag", "(unity command --tag <tag> lists its commands)");
			if (result.Get("packages") is JsonObject packages)
			{
				var summary = new JsonObject();
				foreach (var (key, value) in (JsonObject)result) summary[key] = key == "packages" ? JsonValue.Create(packages.Count) : value?.DeepClone();
				return Renderer.Render(summary);
			}
		}
		if (result is JsonArray { Count: > 0 } rows && rows[0].Has("msg")) return EditorRenderers.Logs(result);
		return Renderer.Render(result);
	}

	public static List<string> RenderEditor(string? command, JsonNode? rawResult)
	{
		var result = Pruner.Prune(Unwrap(rawResult), Pruner.CommandFields(command));
		if (result.IsEmpty()) return ["ok"];
		var renderer = EditorRenderers.For(command);
		var applies = renderer is not null && (result is JsonObject
			|| result is JsonArray array && array.All(item => item is JsonObject) && EditorRenderers.AcceptsArray(renderer));
		return applies ? renderer!(result!) : Renderer.Render(result);
	}

	/// <summary>Editor results are sometimes a JSON string (e.g. recompile_status).</summary>
	public static JsonNode? Decode(JsonNode? result)
	{
		if (result.Str() is not { } text) return result;
		var value = text.Trim();
		if (!value.StartsWith('{') && !value.StartsWith('[')) return result;
		return JsonNode.ParseLenient(value) ?? result;
	}

	static JsonNode? Unwrap(JsonNode? result)
	{
		var value = Decode(result);
		if (value is not JsonObject obj || (!obj.ContainsKey("success") && !obj.ContainsKey("executedAt"))) return value;
		var unwrapped = new JsonObject();
		foreach (var (key, item) in obj)
			if (!InnerEnvelope.Contains(key)) unwrapped[key] = item?.DeepClone();
		return unwrapped;
	}

	/// <summary>Inner command success, else the wrapper's, else the envelope's.</summary>
	public static bool? CommandSuccess(JsonNode envelope)
	{
		var data = envelope.Get("data");
		var wrapper = IsEditorWrapper(data);
		var decoded = wrapper ? Decode(data.Get("result")) : data;
		return decoded.First("success", "Success").Bool()
			?? (wrapper ? data.First("success", "Success").Bool() : null)
			?? envelope.Get("success").Bool();
	}
}
