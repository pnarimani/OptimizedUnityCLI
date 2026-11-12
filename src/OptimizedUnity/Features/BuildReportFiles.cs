using System.Text;
using System.Text.Json;
using OptimizedUnity.Json;
using OptimizedUnity.Render;

namespace OptimizedUnity.Features;

/// <summary>The long sections of a BuildReport, each written to its own text file.</summary>
static class BuildReportFiles
{
	/// <summary>Short steps with nothing to say are the bulk of the step tree.</summary>
	const long MinStepMs = 100;
	const int SizeColumn = 10;

	static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	static readonly (string File, string Section, Action<JsonElement, TextWriter> Write)[] Reports =
	[
		("errors.txt", "errors", (report, writer) => WriteMessages(JsonElements.Array(report, "errors"), writer)),
		("warnings.txt", "warnings", (report, writer) => WriteMessages(JsonElements.Array(report, "warnings"), writer)),
		("files.txt", "files", WriteFiles),
		("steps.txt", "buildSteps", WriteSteps),
		("assets.txt", "packedAssets", WriteAssets),
	];

	public static bool HasSections(JsonElement report) =>
		Reports.Any(entry => JsonElements.Array(report, entry.Section).GetArrayLength() > 0);

	/// <summary>Write every non-empty section into `directory`; the file names written, or null when the filesystem refuses.</summary>
	public static List<string>? Write(JsonElement report, string directory)
	{
		try
		{
			Directory.CreateDirectory(directory);
			var written = new List<string>();
			foreach (var (file, section, write) in Reports)
			{
				var path = Path.Combine(directory, file);
				// A file left by an earlier status call must not outlive its section.
				File.Delete(path);
				if (JsonElements.Array(report, section).GetArrayLength() == 0) continue;
				using (var writer = new StreamWriter(path, append: false, Utf8) { NewLine = "\n" })
				{
					write(report, writer);
				}
				written.Add(file);
			}
			return written;
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>Identical messages once each, in first-seen order, with a repeat count.</summary>
	static void WriteMessages(JsonElement messages, TextWriter writer)
	{
		var counts = new Dictionary<string, int>();
		var order = new List<string>();
		foreach (var item in messages.EnumerateArray())
		{
			var message = (item.ValueKind == JsonValueKind.String ? item.GetString() : JsonElements.String(item, "message"))?.TrimEnd() ?? "";
			if (counts.TryAdd(message, 1)) order.Add(message);
			else counts[message]++;
		}
		foreach (var message in order)
		{
			var lines = Format.SplitLines(message);
			if (lines.Count == 0) lines.Add("");
			if (counts[message] > 1) lines[0] += $" ×{counts[message]}";
			foreach (var line in lines) writer.WriteLine(line);
			writer.WriteLine();
		}
	}

	static void WriteFiles(JsonElement report, TextWriter writer)
	{
		var files = JsonElements.Array(report, "files").EnumerateArray()
			.Select(file => (Path: JsonElements.String(file, "path") ?? "?", Role: JsonElements.String(file, "role") ?? "", Size: JsonElements.Long(file, "sizeBytes") ?? 0))
			.OrderByDescending(file => file.Size)
			.ThenBy(file => file.Path, StringComparer.Ordinal)
			.ToList();
		var root = OutputFolder(JsonElements.String(report, "outputPath"));
		var roleWidth = files.Max(file => file.Role.Length);
		writer.WriteLine($"{files.Count} files, {Format.Bytes(files.Sum(file => file.Size))}" + (root.Length > 0 ? $", paths relative to {root}" : ""));
		foreach (var file in files)
			writer.WriteLine($"{Size(file.Size)}  {file.Role.PadRight(roleWidth)}  {Relative(file.Path, root)}");
	}

	/// <summary>`C:/b/Game.exe` → `C:/b/`; empty when unknown.</summary>
	static string OutputFolder(string? outputPath)
	{
		var normalized = (outputPath ?? "").Replace('\\', '/');
		var slash = normalized.LastIndexOf('/');
		return slash < 0 ? "" : normalized[..(slash + 1)];
	}

	static string Relative(string path, string root)
	{
		var normalized = path.Replace('\\', '/');
		var inside = root.Length > 0 && normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase);
		return inside ? normalized[root.Length..] : normalized;
	}

	static void WriteSteps(JsonElement report, TextWriter writer)
	{
		var steps = JsonElements.Array(report, "buildSteps");
		writer.WriteLine($"{steps.GetArrayLength()} steps; indented by depth");
		var omitted = 0;
		foreach (var step in steps.EnumerateArray())
		{
			var duration = JsonElements.Long(step, "durationMs") ?? 0;
			var problems = Problems(JsonElements.Array(step, "messages"));
			if (duration < MinStepMs && problems.Length == 0)
			{
				omitted++;
				continue;
			}
			var depth = (int)Math.Clamp(JsonElements.Long(step, "depth") ?? 0, 0, 64);
			var line = $"{new string(' ', depth * 2)}{JsonElements.String(step, "name") ?? "?"}  {Format.Duration(duration / 1_000.0)}";
			writer.WriteLine(problems.Length > 0 ? $"{line}  [{problems}]" : line);
		}
		if (omitted > 0) writer.WriteLine($"({omitted} of {steps.GetArrayLength()} steps omitted: under {MinStepMs} ms with no errors or warnings)");
	}

	/// <summary>`2 errors, 1 warning`; Log messages are not counted.</summary>
	static string Problems(JsonElement messages)
	{
		int errors = 0, warnings = 0;
		foreach (var message in messages.EnumerateArray())
		{
			switch (JsonElements.String(message, "type"))
			{
				case "Error" or "Exception" or "Assert":
					errors++;
					break;
				case "Warning":
					warnings++;
					break;
			}
		}
		var parts = new List<string>();
		if (errors > 0) parts.Add(errors == 1 ? "1 error" : $"{errors} errors");
		if (warnings > 0) parts.Add(warnings == 1 ? "1 warning" : $"{warnings} warnings");
		return string.Join(", ", parts);
	}

	sealed class AssetTotal
	{
		public long Size;
		public readonly Dictionary<string, long> Types = [];
	}

	/// <summary>Bundles by size, then each asset totalled across every bundle it was packed into.</summary>
	static void WriteAssets(JsonElement report, TextWriter writer)
	{
		var bundles = new List<(string Path, long Size, int Count)>();
		var assets = new Dictionary<string, AssetTotal>(StringComparer.Ordinal);
		foreach (var bundle in JsonElements.Array(report, "packedAssets").EnumerateArray())
		{
			var size = JsonElements.Long(bundle, "overheadBytes") ?? 0;
			var count = 0;
			foreach (var content in JsonElements.Array(bundle, "contents").EnumerateArray())
			{
				var contentSize = JsonElements.Long(content, "sizeBytes") ?? 0;
				size += contentSize;
				count++;
				Add(assets, content, contentSize);
			}
			bundles.Add((JsonElements.String(bundle, "bundlePath") ?? "?", size, count));
		}

		writer.WriteLine($"Bundles: {bundles.Count}, {Format.Bytes(bundles.Sum(bundle => bundle.Size))}");
		foreach (var bundle in bundles.OrderByDescending(bundle => bundle.Size).ThenBy(bundle => bundle.Path, StringComparer.Ordinal))
			writer.WriteLine($"{Size(bundle.Size)}  {bundle.Path} ({bundle.Count} objects)");
		writer.WriteLine();
		writer.WriteLine($"Assets: {assets.Count}, {Format.Bytes(assets.Values.Sum(asset => asset.Size))}; sizes summed across bundles, types largest first");
		foreach (var (path, asset) in assets.OrderByDescending(pair => pair.Value.Size).ThenBy(pair => pair.Key, StringComparer.Ordinal))
		{
			var types = asset.Types.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key);
			writer.WriteLine($"{Size(asset.Size)}  {path}  [{string.Join(", ", types)}]");
		}
	}

	static void Add(Dictionary<string, AssetTotal> assets, JsonElement content, long size)
	{
		var path = JsonElements.String(content, "assetPath") is { Length: > 0 } assetPath ? assetPath : "(no asset path)";
		if (!assets.TryGetValue(path, out var asset))
		{
			asset = new AssetTotal();
			assets[path] = asset;
		}
		asset.Size += size;
		var type = JsonElements.String(content, "type") ?? "?";
		asset.Types[type] = asset.Types.GetValueOrDefault(type) + size;
	}

	static string Size(long bytes) => Format.Bytes(bytes).PadLeft(SizeColumn);
}
