using OptimizedUnity.Json;
using OptimizedUnity.Render;

namespace OptimizedUnity.Tests;

public class HelpTests
{
	[Fact]
	public void SubcommandHelpDropsGlobalOptions()
	{
		var original = Fixture.Text("status-help.txt");
		var compact = HelpText.Compact(original);
		Assert.DoesNotContain("--proxy", compact);
		Assert.Contains("--until-ready", compact);
		Assert.EndsWith("Global options: see `unity --help`.\n", compact);
		Assert.True(compact.Length < original.Length / 2);
	}

	[Fact]
	public void SectionsAfterGlobalOptionsSurvive()
	{
		var help = "Usage: x\n\nOptions:\n  -h  help\n\nGlobal Options:\n  --json  json\n                more\n\nExamples:\n  $ unity x\n";
		Assert.Equal("Usage: x\n\nOptions:\n  -h  help\n\nGlobal options: see `unity --help`.\n\nExamples:\n  $ unity x\n", HelpText.Compact(help));
	}

	[Fact]
	public void RootHelpIsUntouched()
	{
		var root = Fixture.Text("root-help.txt");
		Assert.Contains("--proxy", HelpText.Compact(root));
		Assert.DoesNotContain("Global options: see", HelpText.Compact(root));
	}

	[Fact]
	public void EditorCommandHelpRendersUsage()
	{
		var data = Pruner.Prune(Fixture.Json("command-query-console-get.json").Get("data"));
		var matched = CommandHelp.Match(data, ["console", "get"]);
		Assert.Equal("console get", matched);
		var (lines, found) = CommandHelp.Render(data, "console get", matched);
		Assert.True(found);
		Assert.StartsWith("console get: Get captured Unity console output", lines[0]);
		Assert.Equal("usage: unity cmd console get [--tail <int32>] [--level <string>] [--since <int64>] [--since_session <string>]", lines[1]);
		Assert.Contains("  --tail  int32  default=100  Maximum number of most-recent entries to return", lines);
	}

	[Fact]
	public void EditorCommandHelpMatchesLongestPrefix()
	{
		var data = Pruner.Prune(Fixture.Json("command-query-console.json").Get("data"));
		Assert.Equal("console get", CommandHelp.Match(data, ["console", "get", "extra"]));
		Assert.Null(CommandHelp.Match(data, ["console", "nope"]));
	}

	[Fact]
	public void EditorCommandHelpMissListsClosest()
	{
		var data = Pruner.Prune(Fixture.Json("command-query-console.json").Get("data"));
		var (lines, found) = CommandHelp.Render(data, "console nope", null);
		Assert.False(found);
		Assert.Equal("no command named 'console nope'; closest matches:", lines[0]);
		Assert.Contains(lines, line => line.StartsWith("  console clear: ", StringComparison.Ordinal));
	}
}
