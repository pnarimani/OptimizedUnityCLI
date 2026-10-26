using System.Globalization;
using System.Text;

namespace OptimizedUnity.Output;

internal sealed record Truncation(string Content, bool Truncated, int OutputLines, int TotalLines, int OutputBytes, int TotalBytes);

/// <summary>Keep output within a line and byte budget, tail first, with the full text saved aside.</summary>
internal static class OutputBudget
{
	const int DefaultMaxLines = 2_000;
	const int DefaultMaxBytes = 50 * 1_024;

	public static int MaxLines => EnvInt("UNITY_WRAP_MAX_LINES", DefaultMaxLines);
	public static int MaxBytes => EnvInt("UNITY_WRAP_MAX_BYTES", DefaultMaxBytes);

	static int EnvInt(string name, int fallback) =>
		int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
			? value
			: fallback;

	static int LineCount(string value) => value.Length == 0 ? 0 : value.Count(c => c == '\n') + 1;

	/// <summary>Cut to the budget from the end (`keepHead` keeps the start: documents put what matters first).</summary>
	public static Truncation Truncate(string value, int maxLines, int maxBytes, bool keepHead = false)
	{
		var totalLines = LineCount(value);
		var totalBytes = Encoding.UTF8.GetByteCount(value);
		var content = value;

		if (totalLines > maxLines)
		{
			if (keepHead)
			{
				var end = -1;
				for (var seen = 0; seen < maxLines; seen++) end = content.IndexOf('\n', end + 1);
				content = content[..end];
			}
			else
			{
				var start = content.Length;
				for (var seen = 0; seen < maxLines; seen++) start = content.LastIndexOf('\n', start - 1);
				content = content[(start + 1)..];
			}
		}
		if (Encoding.UTF8.GetByteCount(content) > maxBytes)
		{
			// Cutting mid-character leaves a replacement char at the cut; drop it.
			var bytes = Encoding.UTF8.GetBytes(content);
			content = keepHead
				? Encoding.UTF8.GetString(bytes, 0, maxBytes).TrimEnd('\uFFFD')
				: Encoding.UTF8.GetString(bytes, bytes.Length - maxBytes, maxBytes).TrimStart('\uFFFD');
		}

		var outputBytes = Encoding.UTF8.GetByteCount(content);
		var outputLines = LineCount(content);
		return new Truncation(content, outputLines < totalLines || outputBytes < totalBytes, outputLines, totalLines, outputBytes, totalBytes);
	}

	public static string FormatSize(int bytes) => bytes switch
	{
		< 1_024 => $"{bytes} B",
		< 1_024 * 1_024 => $"{Math.Round(bytes / 1_024.0)} KB",
		_ => (bytes / (1_024.0 * 1_024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
	};

	/// <summary>Apply the budget; when truncated, save the full text and point at it.</summary>
	public static string Compact(string value, string label, int? maxLines = null, int? maxBytes = null, bool keepHead = false)
	{
		var trimmed = value.TrimEnd('\n');
		var lines = maxLines ?? MaxLines;
		var bytes = maxBytes ?? MaxBytes;
		var truncation = Truncate(trimmed, lines, bytes, keepHead);
		if (!truncation.Truncated) return value;
		var saved = Spill.Write(label, "txt", Encoding.UTF8.GetBytes(trimmed + "\n"));
		var where = saved is null ? "Narrow the query, e.g. with --limit/--query." : $"Full output: {saved}";
		var notice = $"[Output truncated: kept the {(keepHead ? "first" : "last")} {truncation.OutputLines} of {truncation.TotalLines} lines ({FormatSize(truncation.OutputBytes)} of {FormatSize(truncation.TotalBytes)}). {where}]";
		return keepHead ? $"{truncation.Content}\n{notice}\n" : $"{notice}\n{truncation.Content}\n";
	}
}
