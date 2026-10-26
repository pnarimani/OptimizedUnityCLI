using System.Text.Json.Nodes;
using OptimizedUnity.Json;
using OptimizedUnity.Render;

namespace OptimizedUnity.Tests;

public class RenderTests
{
	static List<string> Render(string fixture) => EnvelopeRenderer.Render(Fixture.Json(fixture));

	[Fact]
	public void StatusShowsOnlyStatePidAndProject() =>
		Assert.Equal(["state: ready  pid: 12345  project: /Users/dev/projects/SampleGame"], Render("status.json"));

	[Fact]
	public void FailedEnvelopeStillShowsItsData()
	{
		var lines = Render("status-unreachable.json");
		Assert.StartsWith("ERROR: STATUS_ALL_UNREACHABLE: ", lines[0]);
		Assert.Equal("state: unreachable  pid: 12345  project: /Users/dev/projects/SampleGame", lines[1]);
	}

	[Fact]
	public void StatusWithoutEditors()
	{
		var envelope = JsonNode.Parse("""{"success":true,"command":"status","data":{"count":0,"instances":[]},"errors":[],"warnings":[]}""")!;
		Assert.Equal(["(no Editors running)"], EnvelopeRenderer.Render(envelope));
	}

	[Fact]
	public void CommandGroupsRenderOnOneLine()
	{
		var lines = Render("command-groups.json");
		Assert.StartsWith("groups: animation, animation.animator, animation.clip, animation.timeline, asset, authoring, build, build.target, capture", lines[0]);
		Assert.EndsWith(", settings.time, test", lines[0]);
		Assert.Equal("(unity command --query <group> lists its commands)", lines[1]);
		Assert.DoesNotContain(lines, line => line.Contains("127.0.0.1"));
	}

	[Fact]
	public void TagsRenderOnOneLine() =>
		Assert.StartsWith("tags: animation/animator, animation/clip, animation/timeline, assets, assets/import", Render("command-tags.json")[0]);

	[Fact]
	public void QueryCatalogUsesFullCommandNamesAndShowsParametersForFewRows()
	{
		var lines = Render("command-query-console.json");
		Assert.Contains(lines, line => line.StartsWith("console get: Get captured", StringComparison.Ordinal));
		Assert.Contains("    tail:int32=100  Maximum number of most-recent entries to return", lines);
		Assert.Contains("    message:string!  Message to log to console", lines);
		Assert.DoesNotContain(lines, line => line.Contains("$schema") || line.Contains("x-command-metadata"));
	}

	[Theory]
	[InlineData("command-query-console.json")]
	[InlineData("list.json")]
	public void CommandCatalogPointsToCommandHelp(string fixture) =>
		Assert.Equal("(unity command <name> --help shows a command's parameters and usage)", Render(fixture)[^1]);

	[Fact]
	public void ListUsesNamesAndHidesParametersForManyRows()
	{
		var lines = Render("list.json");
		Assert.Equal("animation.animator add_layer: Add a layer to an AnimatorController.", lines[0]);
		Assert.DoesNotContain(lines, line => line.StartsWith("    ", StringComparison.Ordinal));
		Assert.True(lines.Count < 200);
	}

	[Fact]
	public void ConsoleCompactsEntries()
	{
		var lines = Render("console-get.json");
		Assert.All(lines.Where(line => !line.StartsWith(' ') && !line.StartsWith("cursor", StringComparison.Ordinal)),
			line => Assert.Matches(@"^[IWEXADFT] \d\d:\d\d:\d\d ", line));
		Assert.Contains(lines, line => line.StartsWith("cursor: ", StringComparison.Ordinal) && line.Contains("session: "));
		Assert.DoesNotContain(lines, line => line.Contains("System.Runtime.CompilerServices"));
		Assert.DoesNotContain(lines, line => line.Contains("groundTruth") || line.Contains("dropped: false"));
	}

	[Fact]
	public void HierarchyRendersTree()
	{
		var lines = Render("scene-hierarchy.json");
		Assert.Equal(
		[
			"Sample  Assets/Scenes/Sample.unity",
			"  Main Camera  [Camera, AudioListener]",
			"  Level  [LevelLoader, Spawner]",
			"    Stage 0 (inactive)",
		], lines);
	}

	[Fact]
	public void ScriptEvalShowsResultOnly() => Assert.Equal(["2"], Render("script-eval.json"));

	[Fact]
	public void PackageListIsOneLinePerPackage()
	{
		var lines = Render("package-list.json");
		Assert.Contains("packages", lines[0]);
		Assert.Contains(lines, line => line == "*com.example.analytics 1.2.0");
		Assert.DoesNotContain(lines, line => line.Contains("PackageCache"));
	}

	[Theory]
	[InlineData("error-remote-not-found.json", "ERROR: REMOTE_COMMAND_NOT_FOUND: Pipeline server returned 400 Bad Request: no command named 'example_cmd'.")]
	[InlineData("error-bad-args.json", "ERROR: INVALID_COMMAND_ARGS: unknown command 'bogus'")]
	[InlineData("error-timeout.json", "ERROR: COMMAND_FAILED: Pipeline server returned 504 Gateway Timeout")]
	public void ErrorsRenderWithCode(string fixture, string prefix) =>
		Assert.StartsWith(prefix, Assert.Single(Render(fixture)));

	[Fact]
	public void EmptyQueryRendersNothingAndExplains()
	{
		var envelope = Fixture.Json("command-query-empty.json");
		Assert.Empty(EnvelopeRenderer.Render(envelope));
		Assert.Contains("no Unity commands matched query \"zzzqqq\"", CommandHelp.EmptyQueryError(["cmd", "--query", "zzzqqq"], envelope));
		Assert.Null(CommandHelp.EmptyQueryError(["cmd", "--query", ""], envelope));
	}

	[Fact]
	public void CommandSuccessPrefersInnerResult()
	{
		var envelope = JsonNode.Parse("""{"success":true,"data":{"command":"x","target":{},"result":"{\"success\":false}"},"errors":[]}""")!;
		Assert.False(EnvelopeRenderer.CommandSuccess(envelope));
		Assert.True(EnvelopeRenderer.CommandSuccess(Fixture.Json("script-eval.json")));
	}

	[Fact]
	public void StringResultIsDecodedAndUnwrapped()
	{
		var lines = EnvelopeRenderer.RenderEditor("script recompile_status", JsonValue.Create("""{"success":true,"executedAt":"t","status":"completed"}"""));
		Assert.Equal(["status: completed"], lines);
	}

	[Fact]
	public void IdHeavyCommandsDropObjectIds()
	{
		var result = JsonNode.Parse("""{"component":{"type":"Camera","guid":"abc","fileId":12,"fov":60.123456}}""");
		Assert.Equal(["component: type: Camera  fov: 60.12"], EnvelopeRenderer.RenderEditor("component get_properties", result));
		Assert.Contains("guid", string.Join("", EnvelopeRenderer.RenderEditor("asset find", result)));
	}

	[Fact]
	public void JobRendersHeadAndResult()
	{
		var data = JsonNode.Parse("""{"success":true,"data":{"jobId":"0123456789abcdef","command":"script eval","state":"completed","result":{"result":5}},"errors":[]}""")!;
		Assert.Equal(["job 01234567 script eval completed", "5"], EnvelopeRenderer.Render(data));
	}

	[Fact]
	public void TestResultsKeepOnlyFailures()
	{
		var result = JsonNode.Parse("""{"summary":{"passed":2,"failed":1},"results":[{"name":"a","status":"Passed"},{"name":"b","status":"Failed","message":"boom"},{"name":"c","Status":"passed"}]}""");
		var lines = EnvelopeRenderer.RenderEditor("test status", result);
		Assert.Equal(["3 tests: 2 passed, 1 failed", "FAIL b", "    boom"], lines);
	}

	[Fact]
	public void CommandDumpCollapses() =>
		Assert.Equal("No command x. Available: [3 commands, see `unity command`]", Format.CollapseCommandDump("No command x. Available: [a, b, c]"));

	[Theory]
	[InlineData("0.123456", "0.1235")]
	[InlineData("12345.678", "12350")]
	[InlineData("100", "100")]
	[InlineData("1.0", "1")]
	[InlineData("1234567890123456789", "1234567890123456789")]
	public void NumbersUseFourSignificantDigits(string json, string expected) => Assert.Equal(expected, Format.Fmt(JsonNode.Parse(json)));
}
