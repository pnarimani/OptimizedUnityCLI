using System.Text;
using System.Text.RegularExpressions;
using OptimizedUnity.Args;
using OptimizedUnity.Host;
using OptimizedUnity.Render;

namespace OptimizedUnity.Features;

internal sealed record CompilerErrorReport(string Path, List<string> Lines, bool Truncated)
{
	public string Format()
	{
		var omitted = Truncated ? "\n[Additional compiler errors omitted]" : "";
		return $"ERROR: Unity compiler errors from {Path}:\n{string.Join('\n', Lines)}{omitted}";
	}
}

/// <summary>Size of the Editor log before a headless run, so only that run's errors are reported.</summary>
internal sealed record LogSnapshot(string Path, long Size);

/// <summary>Read C# compiler failures from the Editor log after a failed `run`/`test`.</summary>
internal static partial class CompilerErrors
{
	const string OutputMarker = "##### Output";
	const int ScanChunkBytes = 64 * 1_024;
	const int MaxRegionBytes = 4 * 1_024 * 1_024;
	const int MaxLines = 200;
	const int MaxBytes = 16 * 1_024;

	static readonly HashSet<string> HeadlessCommands = ["run", "test"];

	[GeneratedRegex(@"\berror\s+CS\d+\b", RegexOptions.IgnoreCase)]
	private static partial Regex CompilerError();

	public static bool AppliesTo(IReadOnlyList<string> args) => HeadlessCommands.Contains(ArgRewriter.Command(args) ?? "");

	/// <summary>`-l/--log-file` on `run`, `-logFile` passed through to the Editor, else Unity's default log.</summary>
	public static string? LogPath(IReadOnlyList<string> args, string cwd, Func<string?> defaultLog)
	{
		var explicitLog = ArgRewriter.Command(args) == "run" ? ArgRewriter.FlagValue(args, "-l", "--log-file") : null;
		if (explicitLog is null)
		{
			var forwarded = ArgRewriter.Tail(args);
			for (var i = 1; i + 1 < forwarded.Count; i++)
				if (forwarded[i].Equals("-logFile", StringComparison.OrdinalIgnoreCase)) explicitLog = forwarded[i + 1];
		}
		if (explicitLog is "-" or "") return null;
		return explicitLog is null ? defaultLog() : Path.GetFullPath(explicitLog, cwd);
	}

	/// <summary>Unity's documented default Editor.log location for the current user.</summary>
	public static string? DefaultLogPath(Wsl? wsl)
	{
		if (wsl is not null)
		{
			var localAppData = wsl.LocalAppData();
			return localAppData is null ? null : Path.Combine(localAppData, "Unity", "Editor", "Editor.log");
		}
		var home = Paths.Home();
		if (OperatingSystem.IsWindows())
		{
			var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } value
				? value
				: Path.Combine(home, "AppData", "Local");
			return Path.Combine(localAppData, "Unity", "Editor", "Editor.log");
		}
		if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Logs", "Unity", "Editor.log");
		if (OperatingSystem.IsLinux()) return Path.Combine(home, ".config", "unity3d", "Editor.log");
		return null;
	}

	public static LogSnapshot? Snapshot(string? path)
	{
		if (path is null) return null;
		try
		{
			var info = new FileInfo(path);
			return new LogSnapshot(path, info.Exists ? info.Length : 0);
		}
		catch (IOException)
		{
			return new LogSnapshot(path, 0);
		}
	}

	/// <summary>
	/// Errors written since the snapshot: the final `##### Output` section when it is new, else
	/// whatever the run appended. A shrunken file was rewritten by this run and is read whole.
	/// </summary>
	public static CompilerErrorReport? Read(LogSnapshot snapshot)
	{
		try
		{
			using var stream = new FileStream(snapshot.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			var size = stream.Length;
			var since = size < snapshot.Size ? 0 : snapshot.Size;
			if (size == since) return null;

			var marker = LastMarker(stream, size);
			var start = marker >= since ? marker : since;
			start = Math.Max(start, size - MaxRegionBytes);
			var region = new byte[size - start];
			stream.Position = start;
			stream.ReadExactly(region);
			var (lines, truncated) = ErrorLines(Encoding.UTF8.GetString(region));
			return lines.Count > 0 ? new CompilerErrorReport(snapshot.Path, lines, truncated) : null;
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>Distinct, ANSI-stripped `error CSxxxx` lines, bounded in count and size.</summary>
	public static (List<string> Lines, bool Truncated) ErrorLines(string contents)
	{
		var lines = new List<string>();
		var seen = new HashSet<string>();
		var bytes = 0;
		var truncated = false;
		foreach (var raw in contents.Split('\n'))
		{
			var line = Format.StripAnsi(raw).Trim();
			if (line.Length == 0 || !CompilerError().IsMatch(line) || !seen.Add(line)) continue;
			var size = Encoding.UTF8.GetByteCount(line);
			if (lines.Count >= MaxLines || bytes + size > MaxBytes)
			{
				truncated = true;
				continue;
			}
			lines.Add(line);
			bytes += size;
		}
		return (lines, truncated);
	}

	/// <summary>Offset of the last output marker, scanning backward in chunks; -1 when absent.</summary>
	static long LastMarker(FileStream stream, long size)
	{
		var marker = Encoding.UTF8.GetBytes(OutputMarker);
		var cursor = size;
		var following = Array.Empty<byte>();
		var buffer = new byte[ScanChunkBytes + marker.Length];
		while (cursor > 0)
		{
			var length = (int)Math.Min(ScanChunkBytes, cursor);
			cursor -= length;
			stream.Position = cursor;
			stream.ReadExactly(buffer, 0, length);
			following.CopyTo(buffer, length);
			var searchable = buffer.AsSpan(0, length + following.Length);
			var found = searchable.LastIndexOf(marker);
			if (found >= 0) return cursor + found;
			following = buffer.AsSpan(0, Math.Min(marker.Length - 1, length)).ToArray();
		}
		return -1;
	}
}
