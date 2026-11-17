using System.Text.Json.Nodes;
using OptimizedUnity.Args;
using OptimizedUnity.Json;

namespace OptimizedUnity.Render;

/// <summary>
/// Pipeline 0.10 prints an Editor command's bare result under `--json`, with no `{success, data}`
/// envelope. This puts one back, so a single rendering path serves both shapes.
/// </summary>
internal static class ResultEnvelope
{
	/// <summary>The CLI's own envelope, or an Editor command's bare JSON result wrapped in one; else null.</summary>
	public static JsonNode? Parse(IReadOnlyList<string> args, string stdout, int exitCode)
	{
		var parsed = JsonLines(stdout) ?? JsonNode.ParseLenient(stdout);
		if (parsed.IsEnvelope()) return parsed;
		if (parsed is null || !ArgRewriter.IsEditorCommand(args) || !StartsWithJson(stdout)) return null;
		return Wrap(args, parsed, exitCode);
	}

	/// <summary>Newline-delimited JSON (`test list` streams one object per test) as one array; null unless every line is JSON.</summary>
	static JsonArray? JsonLines(string text)
	{
		var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (lines.Length < 2) return null;
		var items = new JsonArray();
		foreach (var line in lines)
		{
			if (line[0] != '{') return null;
			try { items.Add(JsonNode.Parse(line)); }
			catch (System.Text.Json.JsonException) { return null; }
		}
		return items;
	}

	static bool StartsWithJson(string text) => text.AsSpan().TrimStart() is [ '{' or '[', .. ];

	static JsonObject Wrap(IReadOnlyList<string> args, JsonNode result, int exitCode)
	{
		var success = result.First("success", "Success").Bool() ?? exitCode == 0;
		return new JsonObject
		{
			["success"] = success,
			["data"] = IsListing(args, result) ? result : EditorWrapper(args, result),
		};
	}

	/// <summary>Command catalogs, group and tag lists, and detached-job handles describe the Editor, not a command run.</summary>
	static bool IsListing(IReadOnlyList<string> args, JsonNode result) =>
		ArgRewriter.EditorCommandWords(args).Count == 0
		|| result.Has("commands") || result.Has("tools") || result.Has("groups") || result.Has("tags") || result.Has("jobId");

	static JsonObject EditorWrapper(IReadOnlyList<string> args, JsonNode result) => new()
	{
		["command"] = CommandName(ArgRewriter.EditorCommandWords(args)),
		["parameters"] = new JsonObject(),
		["result"] = result.DeepClone(),
		["target"] = new JsonObject(),
	};

	/// <summary>
	/// The words after `cmd` also include positional arguments (`script eval "1+1"`), so take the
	/// two-word name unless only the first word names a command we know.
	/// </summary>
	static string CommandName(List<string> words)
	{
		var twoWords = string.Join(' ', words.Take(2));
		var oneWord = words[0];
		return !IsKnown(twoWords) && IsKnown(oneWord) ? oneWord : twoWords;
	}

	static bool IsKnown(string command) =>
		EditorRenderers.For(command) is not null || Pruner.CommandFields(command).Count > 0;
}
