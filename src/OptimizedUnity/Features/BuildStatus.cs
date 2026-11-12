using System.Text.Json;
using OptimizedUnity.Args;
using OptimizedUnity.Host;
using OptimizedUnity.Json;
using OptimizedUnity.Output;
using OptimizedUnity.Proc;
using OptimizedUnity.Render;

namespace OptimizedUnity.Features;

/// <summary>
/// `build status` returns the whole BuildReport, which runs to 100s of MB for a real project.
/// The headline fields print inline; errors, warnings, files, steps and packed assets go to
/// files under the project's Temp folder.
/// </summary>
static class BuildStatus
{
	const string ReportDirectory = "Temp/build-reports";

	/// <summary>Fields the inline summary already shows in its own form.</summary>
	static readonly HashSet<string> SummarizedFields =
	[
		"status", "buildId", "result", "platform", "outputPath", "totalSizeBytes", "buildTimeMs",
		"buildStartedAt", "buildEndedAt", "totalErrors", "totalWarnings",
	];

	static readonly HashSet<string> FailedResults = ["Failed", "Cancelled"];

	static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 };

	public static bool AppliesTo(IReadOnlyList<string> args) =>
		ArgRewriter.EditorCommandWords(args).SequenceEqual(new[] { "build", "status" });

	/// <summary>
	/// Render the report, writing its long sections beside the project resolved by `projectRoot`
	/// (given the build's output path; null means none was found). Null for a shape this does not know.
	/// </summary>
	public static ChildResult? TryRender(string stdout, Func<string?, string?> projectRoot)
	{
		using var document = Parse(stdout);
		if (document is null || Locate(document.RootElement) is not { } report) return null;

		var lines = Summary(report);
		if (BuildReportFiles.HasSections(report))
			lines.AddRange(WriteReports(report, projectRoot(JsonElements.String(report, "outputPath"))));
		var code = FailedResults.Contains(JsonElements.String(report, "result") ?? "") ? ExitCodes.CommandFailed : ExitCodes.Success;
		return new ChildResult(string.Join('\n', lines) + "\n", "", code);
	}

	static JsonDocument? Parse(string stdout)
	{
		var start = stdout.AsSpan().IndexOfAny('{', '[');
		if (start < 0) return null;
		try
		{
			return JsonDocument.Parse(stdout.AsMemory(start), Lenient);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>The bare 0.10 result, or the result inside an older `{success, data: {result}}` envelope.</summary>
	static JsonElement? Locate(JsonElement node)
	{
		if (node.ValueKind != JsonValueKind.Object) return null;
		if (node.TryGetProperty("buildId", out _) || node.TryGetProperty("status", out _)) return node;
		foreach (var key in (string[])["data", "result"])
		{
			if (node.TryGetProperty(key, out var inner) && Locate(inner) is { } found) return found;
		}
		return null;
	}

	static List<string> Summary(JsonElement report)
	{
		var lines = new List<string>();
		var head = HeadFields(report).Where(field => field.Value.Length > 0).Select(field => $"{field.Key}: {field.Value}").ToList();
		if (head.Count > 0) lines.Add(string.Join("  ", head));
		if (JsonElements.String(report, "outputPath") is { Length: > 0 } output) lines.Add($"output: {output}");
		if (Counts(report) is { Length: > 0 } counts) lines.Add(counts);
		foreach (var property in report.EnumerateObject())
		{
			if (SummarizedFields.Contains(property.Name) || JsonElements.Scalar(property.Value) is not { Length: > 0 } value) continue;
			lines.Add($"{property.Name}: {value}");
		}
		return lines.Count > 0 ? lines : ["(no build)"];
	}

	static IEnumerable<(string Key, string Value)> HeadFields(JsonElement report)
	{
		yield return ("status", JsonElements.String(report, "status") ?? "");
		yield return ("result", JsonElements.String(report, "result") ?? "");
		yield return ("platform", JsonElements.String(report, "platform") ?? "");
		yield return ("time", JsonElements.Long(report, "buildTimeMs") is { } ms ? Format.Duration(ms / 1_000.0) : "");
		yield return ("size", JsonElements.Long(report, "totalSizeBytes") is { } bytes ? Format.Bytes(bytes) : "");
		yield return ("buildId", JsonElements.String(report, "buildId") ?? "");
	}

	static string Counts(JsonElement report)
	{
		var errors = JsonElements.Long(report, "totalErrors") ?? JsonElements.Array(report, "errors").GetArrayLength();
		var warnings = JsonElements.Long(report, "totalWarnings") ?? JsonElements.Array(report, "warnings").GetArrayLength();
		var parts = new List<string>();
		if (errors > 0) parts.Add($"errors: {errors}");
		if (warnings > 0) parts.Add($"warnings: {warnings}");
		return string.Join("  ", parts);
	}

	static List<string> WriteReports(JsonElement report, string? projectRoot)
	{
		var name = SafeName(JsonElements.String(report, "buildId") ?? "latest");
		var fallback = Path.Combine(Spill.Root, "build-reports", name);
		if (projectRoot is not null)
		{
			var directory = Path.Combine(projectRoot, ReportDirectory, name);
			if (BuildReportFiles.Write(report, directory) is { } written)
				return [ReportsLine($"{ReportDirectory}/{name}/", written)];
		}
		if (BuildReportFiles.Write(report, fallback) is { } spilled)
		{
			var note = projectRoot is null ? "note: no Unity project found; reports saved outside it" : $"note: could not write under {projectRoot}";
			return [ReportsLine(fallback + Path.DirectorySeparatorChar, spilled), note];
		}
		return ["note: could not write the build reports; `unity cmd build status --json` has the full report"];
	}

	static string ReportsLine(string directory, List<string> files) => $"reports: {directory}  {string.Join("  ", files)}";

	static string SafeName(string buildId)
	{
		var safe = new string(buildId.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '-').ToArray()).Trim('.');
		return safe.Length > 0 ? safe : "latest";
	}

	// ------------------------------------------------------------------ project root

	/// <summary>
	/// The project the Editor serves, so the reports land in its Temp folder: `--project-path`,
	/// `UNITY_PROJECT_PATH`, the project containing `cwd`, then the one containing the build output.
	/// Null when none is found.
	/// </summary>
	public static string? ProjectRoot(IReadOnlyList<string> localArgs, string cwd, Wsl? wsl, string? outputPath)
	{
		var explicitPath = ArgRewriter.FlagValue(localArgs, "--project-path")
			?? Environment.GetEnvironmentVariable("UNITY_PROJECT_PATH");
		if (Local(explicitPath, cwd, wsl) is { } given) return given;
		return ContainingProject(cwd) ?? ContainingProject(Local(outputPath, cwd, wsl));
	}

	/// <summary>A path as this process sees it; Windows paths are converted under WSL and unusable elsewhere off Windows.</summary>
	static string? Local(string? path, string cwd, Wsl? wsl)
	{
		if (string.IsNullOrWhiteSpace(path)) return null;
		if (!Paths.IsWindowsAbsolute(path) || OperatingSystem.IsWindows()) return Path.GetFullPath(path, cwd);
		return wsl?.ToLinux(path);
	}

	static string? ContainingProject(string? start)
	{
		for (var directory = start; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
		{
			if (IsProject(directory)) return directory;
		}
		return null;
	}

	static bool IsProject(string directory) =>
		Directory.Exists(Path.Combine(directory, "Assets")) && Directory.Exists(Path.Combine(directory, "ProjectSettings"));
}
