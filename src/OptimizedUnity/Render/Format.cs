using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimizedUnity.Json;

namespace OptimizedUnity.Render;

internal static partial class Format
{
	/// <summary>Scalars as text, floats at 4 significant digits, containers as compact JSON.</summary>
	public static string Fmt(JsonNode? value)
	{
		switch (value.Kind())
		{
			case JsonValueKind.Null or JsonValueKind.Undefined:
				return "";
			case JsonValueKind.String:
				return value.Str()!;
			case JsonValueKind.True:
				return "true";
			case JsonValueKind.False:
				return "false";
			case JsonValueKind.Number:
				var raw = value.Compact();
				if (!value.TryNumber(out var number) || !double.IsFinite(number)) return raw;
				if (Math.Floor(number) != number) return Number(number);
				// Keep raw digits for integers (64-bit ids survive); normalise `1.0` / `1e3`.
				return raw.AsSpan().IndexOfAny(".eE") >= 0 ? number.ToString("R", CultureInfo.InvariantCulture) : raw;
			default:
				return value.Compact();
		}
	}

	/// <summary>JS `String(Number(x.toPrecision(4)))`.</summary>
	public static string Number(double value)
	{
		var rounded = double.Parse(value.ToString("G4", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
		return rounded.ToString("R", CultureInfo.InvariantCulture);
	}

	public static string ObjLine(JsonObject value) =>
		string.Join("  ", value.Select(pair => $"{pair.Key}: {Fmt(pair.Value)}"));

	/// <summary>Scalars on one line; three or more identical values print once with a count.</summary>
	public static string JoinScalars(JsonArray values)
	{
		var texts = values.Select(Fmt).ToList();
		return texts.Count >= 3 && texts.Distinct().Count() == 1 ? $"{texts[0]} ×{texts.Count}" : string.Join("  ", texts);
	}

	public static List<string> SplitLines(string? text)
	{
		if (string.IsNullOrEmpty(text)) return [];
		var lines = text.ReplaceLineEndings("\n").Split('\n').ToList();
		if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
		return lines;
	}

	/// <summary>2026-08-14T09:37:38.3722520Z → 09:37:38.</summary>
	public static string ShortTime(JsonNode? timestamp)
	{
		if (!timestamp.Truthy()) return "";
		var s = Fmt(timestamp);
		var t = s.IndexOf('T');
		var time = t >= 0 ? s[(t + 1)..] : s;
		return time.Length > 8 ? time[..8] : time;
	}

	[GeneratedRegex(@"Available: \[([^\]]*)\]")]
	private static partial Regex CommandDump();

	/// <summary>
	/// The pipeline server embeds its whole command list in 'no command named X' messages, and
	/// logs them to the console too.
	/// </summary>
	public static string CollapseCommandDump(string text) =>
		CommandDump().Replace(text, match =>
		{
			var commands = match.Groups[1].Value;
			var count = commands.Trim().Length > 0 ? commands.Split(',').Length : 0;
			return $"Available: [{count} commands, see `unity command`]";
		});

	/// <summary>First line inline, continuation lines indented.</summary>
	public static List<string> MsgLines(JsonNode? message, string indent = "    ") =>
		MsgLines(Fmt(message), indent);

	public static List<string> MsgLines(string message, string indent = "    ")
	{
		var lines = SplitLines(CollapseCommandDump(message.TrimEnd()));
		if (lines.Count == 0) return [""];
		var result = new List<string>(lines.Count) { lines[0] };
		result.AddRange(lines.Skip(1).Select(line => indent + line.Trim()));
		return result;
	}

	[GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
	private static partial Regex Ansi();

	public static string StripAnsi(string text) => text.Contains('\u001B') ? Ansi().Replace(text, "") : text;
}
