using System.Text.RegularExpressions;

namespace OptimizedUnity.Render;

/// <summary>Shrink .NET/Unity stack traces to the frames worth reading.</summary>
internal static partial class StackFrames
{
	/// <summary>Logging and async plumbing in console traces.</summary>
	public static readonly string[] ConsoleNoise =
	[
		"System.Runtime.CompilerServices.", "System.Threading.", "UnityEngine.Debug:", "UnityEngine.Logger:",
		"UnityEngine.DebugLogHandler:", "UnityEngine.StackTraceUtility:",
	];

	/// <summary>Test-runner and reflection plumbing in NUnit traces (after the leading `at `).</summary>
	public static readonly string[] TestNoise =
	[
		"(wrapper ", "System.Reflection.", "System.RuntimeMethodHandle", "System.Runtime.CompilerServices.",
		"System.Threading.", "NUnit.", "UnityEngine.TestTools.", "UnityEditor.TestTools.", "UnityEngine.TestRunner.",
		"UnityEditor.TestRunner.",
	];

	[GeneratedRegex(@"\./Library/PackageCache/([^/@\s]+)@[0-9a-fA-F]+/")]
	private static partial Regex RelativePackageCache();

	[GeneratedRegex(@" in (?:[^\s]*?[/\\])?Library[/\\]PackageCache[/\\]([^/\\@\s]+)@[0-9a-fA-F]+[/\\]")]
	private static partial Regex AbsolutePackageCache();

	[GeneratedRegex(@" in [^\s]*?[/\\](Assets|Packages)[/\\]")]
	private static partial Regex ProjectPath();

	/// <summary>Unity's own sources, as built on its CI machines: not on the reader's disk.</summary>
	[GeneratedRegex(@" \(at [^)]*[/\\]bokken[/\\][^)]*\)")]
	private static partial Regex UnityBuildMachine();

	[GeneratedRegex(@" \[0x[0-9a-fA-F]+\]")]
	private static partial Regex IlOffset();

	[GeneratedRegex(@" in <[0-9a-fA-F]+>:0\s*$")]
	private static partial Regex NoSource();

	public static List<string> Compact(string? trace, IReadOnlyList<string> noise, int maxFrames)
	{
		var frames = new List<string>();
		foreach (var raw in Format.SplitLines(trace))
		{
			var line = raw.Trim();
			if (line.Length == 0) continue;
			var body = line.StartsWith("at ", StringComparison.Ordinal) ? line[3..] : line;
			if (noise.Any(prefix => body.StartsWith(prefix, StringComparison.Ordinal))) continue;
			line = RelativePackageCache().Replace(line, "$1/");
			line = AbsolutePackageCache().Replace(line, " in $1/");
			line = ProjectPath().Replace(line, " in $1/");
			line = UnityBuildMachine().Replace(line, "");
			line = NoSource().Replace(IlOffset().Replace(line, ""), "");
			frames.Add(line.Replace('\\', '/'));
		}
		if (frames.Count <= maxFrames) return frames;
		var kept = frames.Take(maxFrames).ToList();
		kept.Add($"… {frames.Count - maxFrames} more frames");
		return kept;
	}
}
