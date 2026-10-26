using OptimizedUnity.Features;

namespace OptimizedUnity.Tests;

public class CompilerErrorsTests
{
	const string Error1 = "Assets/A.cs(3,5): error CS0103: The name 'x' does not exist in the current context";
	const string Error2 = "Assets/B.cs(9,1): error CS1002: ; expected";

	[Fact]
	public void ReadsOnlyTheNewOutputSection()
	{
		using var temp = new TempDir();
		var log = temp.Sub("Editor.log");
		File.WriteAllText(log, $"##### Output\n{Error2}\nold run\n");
		var snapshot = CompilerErrors.Snapshot(log)!;
		File.AppendAllText(log, $"loading\n##### Output\n\u001b[31m{Error1}\u001b[0m\n{Error1}\nwarning CS0168: unused\n");
		var report = CompilerErrors.Read(snapshot)!;
		Assert.Equal([Error1], report.Lines);
		Assert.StartsWith($"ERROR: Unity compiler errors from {log}:", report.Format());
	}

	[Fact]
	public void WithoutMarkerReadsAppendedContent()
	{
		using var temp = new TempDir();
		var log = temp.Sub("Editor.log");
		File.WriteAllText(log, $"{Error2}\n");
		var snapshot = CompilerErrors.Snapshot(log)!;
		File.AppendAllText(log, $"{Error1}\n");
		Assert.Equal([Error1], CompilerErrors.Read(snapshot)!.Lines);
	}

	[Fact]
	public void StaleLogReportsNothing()
	{
		using var temp = new TempDir();
		var log = temp.Sub("Editor.log");
		File.WriteAllText(log, $"##### Output\n{Error1}\n");
		Assert.Null(CompilerErrors.Read(CompilerErrors.Snapshot(log)!));
	}

	[Fact]
	public void RewrittenLogIsReadWhole()
	{
		using var temp = new TempDir();
		var log = temp.Sub("Editor.log");
		File.WriteAllText(log, new string('x', 10_000));
		var snapshot = CompilerErrors.Snapshot(log)!;
		File.WriteAllText(log, $"{Error2}\n");
		Assert.Equal([Error2], CompilerErrors.Read(snapshot)!.Lines);
	}

	[Fact]
	public void MarkerAcrossChunkBoundaryIsFound()
	{
		using var temp = new TempDir();
		var log = temp.Sub("Editor.log");
		var snapshot = CompilerErrors.Snapshot(log)!;
		// The marker starts 5 bytes before the last 64 KB chunk, so it straddles the boundary.
		var head = $"##### Output\n{Error1}\n";
		var tail = head + new string('z', 64 * 1024 + 5 - head.Length);
		File.WriteAllText(log, $"{Error2}\n{new string('y', 70_000)}{tail}");
		Assert.Equal([Error1], CompilerErrors.Read(snapshot)!.Lines);
	}

	[Fact]
	public void ErrorLinesAreBounded()
	{
		var contents = string.Join('\n', Enumerable.Range(0, 300).Select(i => $"Assets/F{i}.cs(1,1): error CS0001: e"));
		var (lines, truncated) = CompilerErrors.ErrorLines(contents);
		Assert.Equal(200, lines.Count);
		Assert.True(truncated);
	}

	[Fact]
	public void LogPathPrefersExplicitFlags()
	{
		var cwd = Path.GetFullPath("/w");
		Assert.Equal(Path.Combine(cwd, "run.log"), CompilerErrors.LogPath(["run", ".", "--log-file", "run.log"], cwd, () => "/default"));
		Assert.Equal(Path.Combine(cwd, "u.log"), CompilerErrors.LogPath(["test", ".", "--", "-nographics", "-logFile", "u.log"], cwd, () => "/default"));
		Assert.Equal("/default", CompilerErrors.LogPath(["test", "."], cwd, () => "/default"));
		Assert.Null(CompilerErrors.LogPath(["run", ".", "--", "-logFile", "-"], cwd, () => "/default"));
		Assert.True(CompilerErrors.AppliesTo(["test"]));
		Assert.False(CompilerErrors.AppliesTo(["build"]));
	}
}
