using System.Runtime.CompilerServices;
using OptimizedUnity.Features;
using OptimizedUnity.Json;
using OptimizedUnity.Render;

namespace OptimizedUnity.Tests;

/// <summary>
/// Every fixture's rendered output must match `&lt;fixture&gt;.expected.txt` beside it.
/// Run with `UPDATE_GOLDEN=1` to rewrite the expected files from the current output.
/// </summary>
public class GoldenTests
{
	const string ExpectedSuffix = ".expected.txt";

	static string SourceFixtures([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Fixtures");

	static string BuiltFixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");

	static IEnumerable<string> FixtureNames() => Directory.EnumerateFiles(SourceFixtures())
		.Select(path => Path.GetFileName(path))
		.Where(name => !name.EndsWith(ExpectedSuffix, StringComparison.Ordinal))
		.Order(StringComparer.Ordinal);

	public static TheoryData<string> Fixtures() => new(FixtureNames());

	[Theory]
	[MemberData(nameof(Fixtures))]
	public void MatchesExpectedOutput(string fixture)
	{
		var actual = Render(fixture);
		var expectedPath = Path.Combine(SourceFixtures(), ExpectedName(fixture));
		if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
		{
			File.WriteAllText(expectedPath, actual);
			return;
		}
		Assert.True(File.Exists(expectedPath), $"Missing {ExpectedName(fixture)}; run the tests with UPDATE_GOLDEN=1 to create it.");
		Assert.Equal(File.ReadAllText(expectedPath).ReplaceLineEndings("\n"), actual);
	}

	/// <summary>The streamed shape holds the same tests as the Pipeline 0.10 one, so it must render the same.</summary>
	[Fact]
	public void StreamedTestListRendersLikeTheWrappedOne() =>
		Assert.Equal(Render("test-list-small.json"), Render("test-list-ndjson.json"));

	[Fact]
	public void EveryExpectedFileHasAFixture()
	{
		var expected = FixtureNames().Select(ExpectedName).ToHashSet();
		var orphans = Directory.EnumerateFiles(SourceFixtures(), "*" + ExpectedSuffix)
			.Select(path => Path.GetFileName(path))
			.Where(name => !expected.Contains(name));
		Assert.Empty(orphans);
	}

	/// <summary>`status.json` → `status.expected.txt`; `test-results.nunit.xml` → `test-results.nunit.expected.txt`.</summary>
	static string ExpectedName(string fixture) => Path.GetFileNameWithoutExtension(fixture) + ExpectedSuffix;

	/// <summary>What the wrapper prints for the response in `fixture`.</summary>
	static string Render(string fixture)
	{
		var path = Path.Combine(BuiltFixtures, fixture);
		return fixture switch
		{
			"build-status.json" => RenderBuildStatus(File.ReadAllText(path)),
			"command-query-console-get.json" => Lines(RenderCommandHelp(path, ["console", "get"])),
			// CLI 1.0 betas print `cmd test list --json` bare, one test object per line.
			"test-list-ndjson.json" => Lines(EnvelopeRenderer.Render(ResultEnvelope.Parse(["cmd", "test", "list"], File.ReadAllText(path), 0)!)),
			_ when fixture.EndsWith(".txt", StringComparison.Ordinal) => HelpText.Compact(File.ReadAllText(path)),
			_ when fixture.EndsWith(".xml", StringComparison.Ordinal) => Lines(TestReports.Render(TestReports.FromReport(path)!)),
			_ => Lines(EnvelopeRenderer.Render(Fixture.Json(fixture))),
		};
	}

	/// <summary>`unity cmd console get --help`: the command's usage from the live schema.</summary>
	static List<string> RenderCommandHelp(string path, string[] words)
	{
		var data = Pruner.Prune(Fixture.Json(Path.GetFileName(path)).Get("data"));
		var requested = string.Join(' ', words);
		return CommandHelp.Render(data, requested, CommandHelp.Match(data, words)).Lines;
	}

	/// <summary>`build status` also writes report files, so it renders against a throwaway project.</summary>
	static string RenderBuildStatus(string stdout)
	{
		using var project = new TempDir();
		Directory.CreateDirectory(project.Sub("Assets"));
		Directory.CreateDirectory(project.Sub("ProjectSettings"));
		var result = BuildStatus.TryRender(stdout, _ => project.Path)!;
		return result.Stdout + result.Stderr;
	}

	static string Lines(IEnumerable<string> lines) => string.Concat(lines.Select(line => line + "\n"));
}
