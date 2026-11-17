using System.Text.Json;
using System.Text.Json.Nodes;
using OptimizedUnity.Args;
using OptimizedUnity.Json;

namespace OptimizedUnity.Features;

/// <summary>
/// Refuses `cmd test run` while an open scene has unsaved changes: running tests can reload or
/// replace the open scenes, which loses those changes. The open scenes come from `cmd scene list_open`.
/// </summary>
static class DirtySceneGuard
{
	/// <summary>The current command name and the legacy snake_case alias.</summary>
	static readonly string[][] TestRunCommands = [["test", "run"], ["run_tests"]];

	public static bool AppliesTo(IReadOnlyList<string> args)
	{
		var words = ArgRewriter.EditorCommandWords(args);
		return TestRunCommands.Any(command => words.Take(command.Length).SequenceEqual(command));
	}

	/// <summary>`cmd scene list_open --json` against the Editor the test run targets.</summary>
	public static List<string> ListOpenArgs(IReadOnlyList<string> args) =>
		["cmd", "scene", "list_open", "--json", .. ArgRewriter.TargetArgs(args)];

	/// <summary>
	/// Names of dirty scenes in the `scene list_open` output. Pipeline 0.10 prints one bare scene
	/// object per line; older versions print an envelope or array. Unreadable output yields none.
	/// </summary>
	public static List<string> DirtyScenes(string stdout) =>
		Scenes(stdout)
			.Where(scene => scene.Get("isDirty").Bool() == true)
			.Select(SceneName)
			.ToList();

	public static string Rejection(IReadOnlyList<string> dirtyScenes) =>
		$"ERROR: test run rejected: the open scene is dirty ({string.Join(", ", dirtyScenes)}). Save or discard its changes, then run the tests again.";

	static IEnumerable<JsonNode?> Scenes(string stdout)
	{
		var whole = Parse(stdout.Trim());
		if (whole is not null) return SceneList(whole);
		return stdout.ReplaceLineEndings("\n").Split('\n').Select(line => Parse(line.Trim())).Where(scene => scene is not null);
	}

	/// <summary>Unwraps an envelope (`data.result` or `data`) down to its scene array.</summary>
	static IEnumerable<JsonNode?> SceneList(JsonNode node)
	{
		var data = node.IsEnvelope() ? node.Get("data") : node;
		var list = data.Has("result") ? data.Get("result") : data;
		if (list.Has("scenes")) list = list.Get("scenes");
		return list is JsonArray array ? array : new[] { list };
	}

	static JsonNode? Parse(string text)
	{
		if (text.Length == 0) return null;
		try
		{
			return JsonNode.Parse(text);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	static string SceneName(JsonNode? scene)
	{
		var path = scene.Get("path").Str();
		if (!string.IsNullOrEmpty(path)) return path;
		var name = scene.Get("name").Str();
		return string.IsNullOrEmpty(name) ? "untitled" : name;
	}
}
