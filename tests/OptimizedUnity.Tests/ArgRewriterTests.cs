using OptimizedUnity.Args;

namespace OptimizedUnity.Tests;

public class ArgRewriterTests
{
	[Theory]
	[InlineData("status", "Optimized")]
	[InlineData("status --json", "Optimized")]
	[InlineData("--json status", "Optimized")]
	[InlineData("--format json status", "Optimized")]
	[InlineData("", "Help")]
	[InlineData("--json", "Help")]
	[InlineData("status --help", "Help")]
	[InlineData("help status", "Help")]
	[InlineData("cmd --help", "Help")]
	[InlineData("cmd console get --help", "EditorHelp")]
	[InlineData("command scene get_hierarchy -h --project-path /p", "EditorHelp")]
	[InlineData("--version", "Exec")]
	[InlineData("-V", "Exec")]
	[InlineData("shell --protocol ndjson", "Exec")]
	[InlineData("completion zsh", "Exec")]
	[InlineData("auth login --secret-from-stdin", "Exec")]
	[InlineData("logs --follow", "Stream")]
	[InlineData("editors -w", "Stream")]
	[InlineData("watch test .", "Stream")]
	[InlineData("close ./Proj --force", "ForceClose")]
	[InlineData("close ./Proj", "Optimized")]
	[InlineData("close --force", "Optimized")]
	[InlineData("run . -- --help", "Optimized")]
	[InlineData("run . -- -logFile x --follow", "Optimized")]
	public void Classifies(string line, string expected) =>
		Assert.Equal(Enum.Parse<InvocationKind>(expected), ArgRewriter.Classify(Split(line)));

	[Fact]
	public void StripsFormatOnlyBeforeDelimiter()
	{
		var result = ArgRewriter.WithJson(Split("--format tsv status --json --format=human -- --json --format x"));
		Assert.Equal(Split("status --json -- --json --format x"), result);
	}

	[Fact]
	public void AppendsJsonAtEndWithoutDelimiter() =>
		Assert.Equal(Split("cmd console get --tail 3 --json"), ArgRewriter.WithJson(Split("cmd console get --tail 3")));

	[Fact]
	public void EditorCommandWordsStopAtFirstFlag() =>
		Assert.Equal(["console", "get"], ArgRewriter.EditorCommandWords(Split("--json cmd console get --tail 3 extra")));

	[Fact]
	public void TargetArgsKeepsValuesAndInlineForms() =>
		Assert.Equal(Split("--project-path /p --port=7800 --instance two"),
			ArgRewriter.TargetArgs(Split("cmd x --project-path /p --tail 3 --port=7800 --instance two -- --pid 9")));

	[Fact]
	public void FlagValueReadsBothForms()
	{
		Assert.Equal("q", ArgRewriter.FlagValue(Split("cmd --query q"), "--query"));
		Assert.Equal("a b", ArgRewriter.FlagValue(["cmd", "--query=a b"], "--query"));
		Assert.Null(ArgRewriter.FlagValue(Split("cmd -- --query q"), "--query"));
	}

	[Fact]
	public void CommandSkipsValuedGlobals()
	{
		Assert.Equal("status", ArgRewriter.Command(Split("--color never --proxy http://x status")));
		Assert.Null(ArgRewriter.Command(Split("--format json")));
	}

	static string[] Split(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
