using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using OptimizedUnity.Json;

namespace OptimizedUnity.Render;

internal enum TestOutcome { Passed, Failed, Skipped, Inconclusive }

internal sealed record TestCaseResult(string Name, TestOutcome Outcome, string? Message, string? Stack, string? Output);

/// <summary>A test run reduced to what a reader acts on: the counts and every non-passing test.</summary>
internal sealed class TestRunSummary
{
	public int Total, Passed, Failed, Skipped, Inconclusive;
	public double? DurationSeconds;
	public string? Mode, Filter;
	public List<TestCaseResult> Problems { get; } = [];
}

/// <summary>Parse in-Editor (`cmd test run/status`) results and NUnit/JUnit reports, and render them compactly.</summary>
internal static partial class TestReports
{
	const int MaxStackFrames = 6;
	const int MaxMessageLines = 12;
	const int MaxOutputLines = 10;
	const int MaxSkippedListed = 20;
	/// <summary>Above this many tests a listing shows assembly counts, with the full list in a file.</summary>
	public const int MaxListedTests = 300;

	public static TestOutcome Classify(string? status) => (status ?? "").Trim().ToLowerInvariant() switch
	{
		"passed" or "success" => TestOutcome.Passed,
		"skipped" or "ignored" or "notrunnable" or "notexecuted" => TestOutcome.Skipped,
		"inconclusive" => TestOutcome.Inconclusive,
		_ => TestOutcome.Failed,
	};

	// ------------------------------------------------------------------ in-Editor JSON

	/// <summary>`test run`/`test status`: {Summary, Results[{FullName, Status, Message, StackTrace}], Duration, Mode}.</summary>
	public static TestRunSummary? FromEditorResult(JsonNode? result)
	{
		var results = result.GetAny("Results") as JsonArray;
		var summaryNode = result.GetAny("Summary");
		if (results is null && summaryNode is not JsonObject) return null;

		var summary = new TestRunSummary
		{
			Mode = result.GetAny("Mode").Str(),
			Filter = result.GetAny("FilterApplied").Str(),
			DurationSeconds = result.GetAny("Duration").TryNumber(out var duration) ? duration : null,
		};
		foreach (var item in results.Items())
		{
			var outcome = Classify(item.GetAny("Status").Str() ?? item.GetAny("Result").Str());
			Count(summary, outcome);
			if (outcome == TestOutcome.Passed) continue;
			summary.Problems.Add(new TestCaseResult(
				item.GetAny("FullName").Str() ?? item.GetAny("Name").Str() ?? "?",
				outcome,
				item.GetAny("Message").Str(),
				item.GetAny("StackTrace").Str(),
				item.GetAny("Output").Str()));
		}
		if (summaryNode is JsonObject counts)
		{
			summary.Total = Int(counts, "Total") ?? summary.Total;
			summary.Passed = Int(counts, "Passed") ?? summary.Passed;
			summary.Failed = Int(counts, "Failed") ?? summary.Failed;
			summary.Skipped = Int(counts, "Skipped") ?? summary.Skipped;
			summary.Inconclusive = Int(counts, "Inconclusive") ?? summary.Inconclusive;
		}
		return summary;
	}

	static int? Int(JsonNode node, string key) => node.GetAny(key).Int() is { } value ? (int)value : null;

	static void Count(TestRunSummary summary, TestOutcome outcome)
	{
		summary.Total++;
		switch (outcome)
		{
			case TestOutcome.Passed: summary.Passed++; break;
			case TestOutcome.Failed: summary.Failed++; break;
			case TestOutcome.Skipped: summary.Skipped++; break;
			case TestOutcome.Inconclusive: summary.Inconclusive++; break;
		}
	}

	// ------------------------------------------------------------------ NUnit / JUnit XML

	static readonly XmlReaderSettings XmlSettings = new() { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = false, XmlResolver = null };

	/// <summary>Parse an NUnit 3 or JUnit report, streaming so suite-sized reports stay cheap.</summary>
	public static TestRunSummary? FromReport(string path)
	{
		try
		{
			using var reader = XmlReader.Create(path, XmlSettings);
			reader.MoveToContent();
			return reader.LocalName switch
			{
				"test-run" => FromNUnit(reader),
				"testsuites" or "testsuite" => FromJUnit(reader),
				_ => null,
			};
		}
		catch (Exception error) when (error is XmlException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	static TestRunSummary FromNUnit(XmlReader reader)
	{
		var summary = new TestRunSummary
		{
			Total = Attr(reader, "total"), Passed = Attr(reader, "passed"), Failed = Attr(reader, "failed"),
			Skipped = Attr(reader, "skipped"), Inconclusive = Attr(reader, "inconclusive"),
			DurationSeconds = Seconds(reader.GetAttribute("duration")),
		};
		while (reader.Read())
		{
			if (reader.NodeType != XmlNodeType.Element) continue;
			if (reader.LocalName == "property" && summary.Mode is null && reader.GetAttribute("name") == "platform")
				summary.Mode = reader.GetAttribute("value");
			else if (reader.LocalName == "test-case")
				ReadNUnitCase(reader, summary);
			else if (IsSuiteFailure(reader))
				ReadNUnitSuiteFailure(reader, summary);
		}
		return summary;
	}

	static void ReadNUnitCase(XmlReader reader, TestRunSummary summary)
	{
		var outcome = Classify(reader.GetAttribute("result"));
		var name = reader.GetAttribute("fullname") ?? reader.GetAttribute("name") ?? "?";
		if (outcome == TestOutcome.Passed || reader.IsEmptyElement)
		{
			if (outcome != TestOutcome.Passed) summary.Problems.Add(new TestCaseResult(name, outcome, null, null, null));
			return;
		}
		string? message = null, stack = null, output = null;
		using var subtree = reader.ReadSubtree();
		Walk(subtree, element =>
		{
			switch (element.LocalName)
			{
				case "message": message ??= element.ReadElementContentAsString(); return true;
				case "stack-trace": stack ??= element.ReadElementContentAsString(); return true;
				case "output": output ??= element.ReadElementContentAsString(); return true;
				default: return false;
			}
		});
		summary.Problems.Add(new TestCaseResult(name, outcome, message, stack, output));
	}

	static bool IsSuiteFailure(XmlReader reader) =>
		reader.LocalName == "test-suite" && reader.GetAttribute("result") == "Failed"
		&& reader.GetAttribute("site") is "SetUp" or "TearDown" or "Parent";

	/// <summary>A fixture-level failure (OneTimeSetUp and friends) that its children only echo.</summary>
	static void ReadNUnitSuiteFailure(XmlReader reader, TestRunSummary summary)
	{
		var name = (reader.GetAttribute("fullname") ?? reader.GetAttribute("name") ?? "?") + $" ({reader.GetAttribute("site")})";
		string? message = null, stack = null;
		// The suite's own <failure> precedes its children; read it without consuming them.
		var depth = reader.Depth;
		if (reader.IsEmptyElement || !reader.Read()) return;
		while (!reader.EOF && reader.Depth > depth)
		{
			if (reader.NodeType == XmlNodeType.Element)
			{
				if (reader.LocalName is "test-suite" or "test-case")
				{
					summary.Problems.Add(new TestCaseResult(name, TestOutcome.Failed, message, stack, null));
					if (reader.LocalName == "test-case") ReadNUnitCase(reader, summary);
					else if (IsSuiteFailure(reader)) ReadNUnitSuiteFailure(reader, summary);
					return;
				}
				// ReadElementContentAsString advances past the element itself.
				if (reader.LocalName == "message") { message ??= reader.ReadElementContentAsString(); continue; }
				if (reader.LocalName == "stack-trace") { stack ??= reader.ReadElementContentAsString(); continue; }
			}
			reader.Read();
		}
		summary.Problems.Add(new TestCaseResult(name, TestOutcome.Failed, message, stack, null));
	}

	static TestRunSummary FromJUnit(XmlReader reader)
	{
		var summary = new TestRunSummary();
		// Root totals when present; otherwise each test case is counted as it is read.
		var counted = reader.GetAttribute("tests") is not null;
		if (counted)
		{
			summary.Total = Attr(reader, "tests");
			summary.Failed = Attr(reader, "failures") + Attr(reader, "errors");
			summary.Skipped = Attr(reader, "skipped");
			summary.Passed = Math.Max(0, summary.Total - summary.Failed - summary.Skipped);
			summary.DurationSeconds = Seconds(reader.GetAttribute("time"));
		}
		while (reader.Read())
		{
			if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "testcase") continue;
			var className = reader.GetAttribute("classname");
			var caseName = reader.GetAttribute("name") ?? "?";
			var name = string.IsNullOrEmpty(className) ? caseName : $"{className}.{caseName}";
			if (reader.IsEmptyElement)
			{
				if (!counted) Count(summary, TestOutcome.Passed);
				continue;
			}
			TestOutcome outcome = TestOutcome.Passed;
			string? message = null, stack = null, output = null;
			using (var subtree = reader.ReadSubtree())
			{
				Walk(subtree, element =>
				{
					switch (element.LocalName)
					{
						case "failure" or "error":
							outcome = TestOutcome.Failed;
							message ??= element.GetAttribute("message");
							stack ??= element.ReadElementContentAsString();
							return true;
						case "skipped":
							outcome = TestOutcome.Skipped;
							message ??= element.GetAttribute("message");
							return false;
						case "system-out":
							output ??= element.ReadElementContentAsString();
							return true;
						default:
							return false;
					}
				});
			}
			if (!counted) Count(summary, outcome);
			if (outcome != TestOutcome.Passed) summary.Problems.Add(new TestCaseResult(name, outcome, message, stack, output));
		}
		return summary;
	}

	/// <summary>
	/// Visit every element of a subtree. `visit` returns true when it consumed the element (and so
	/// already advanced the reader), which a plain `Read()` loop would otherwise skip past.
	/// </summary>
	static void Walk(XmlReader subtree, Func<XmlReader, bool> visit)
	{
		subtree.Read();
		while (!subtree.EOF)
		{
			if (subtree.NodeType == XmlNodeType.Element && visit(subtree)) continue;
			subtree.Read();
		}
	}

	static int Attr(XmlReader reader, string name) =>
		int.TryParse(reader.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

	static double? Seconds(string? value) =>
		double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;

	// ------------------------------------------------------------------ rendering

	public static List<string> Render(TestRunSummary summary, string? reportPath = null)
	{
		var counts = new List<string> { $"{summary.Passed} passed" };
		if (summary.Failed > 0) counts.Add($"{summary.Failed} failed");
		if (summary.Skipped > 0) counts.Add($"{summary.Skipped} skipped");
		if (summary.Inconclusive > 0) counts.Add($"{summary.Inconclusive} inconclusive");
		var context = new List<string>();
		if (summary.DurationSeconds is { } seconds) context.Add(Duration(seconds));
		if (!string.IsNullOrEmpty(summary.Mode)) context.Add(summary.Mode!);
		if (!string.IsNullOrEmpty(summary.Filter)) context.Add(summary.Filter!);
		var lines = new List<string>
		{
			$"{summary.Total} tests: {string.Join(", ", counts)}" + (context.Count > 0 ? $" ({string.Join(", ", context)})" : ""),
		};

		// Identical failures (a broken [OneTimeSetUp] fails a whole fixture) print once.
		var groups = summary.Problems
			.Where(p => p.Outcome == TestOutcome.Failed)
			.Select(p => (Problem: p, Message: MessageLines(p.Message).ToList(), Stack: StackFrames.Compact(p.Stack, StackFrames.TestNoise, MaxStackFrames)))
			.GroupBy(f => string.Join('\n', f.Message) + "\0" + string.Join('\n', f.Stack));
		foreach (var group in groups)
		{
			var (problem, message, stack) = group.First();
			// Names in the same fixture repeat its prefix; show only what differs.
			var fixture = FixtureOf(problem.Name);
			var others = group.Skip(1)
				.Select(f => fixture.Length > 0 && f.Problem.Name.StartsWith(fixture + ".", StringComparison.Ordinal) ? f.Problem.Name[(fixture.Length + 1)..] : f.Problem.Name)
				.ToList();
			lines.Add(others.Count == 0 ? $"FAIL {problem.Name}" : $"FAIL {problem.Name} (+{others.Count} more with the same failure)");
			lines.AddRange(message.Select(line => "    " + line));
			lines.AddRange(stack.Select(frame => "    " + frame));
			var output = Format.SplitLines(problem.Output).Select(line => line.TrimEnd()).Where(line => line.Length > 0).ToList();
			if (output.Count > 0)
			{
				lines.Add(output.Count > MaxOutputLines ? $"    output (last {MaxOutputLines} of {output.Count} lines):" : "    output:");
				lines.AddRange(output.TakeLast(MaxOutputLines).Select(line => "      " + line));
			}
			if (others.Count > 0)
				lines.Add($"    same failure: {string.Join(", ", others.Take(MaxSkippedListed))}{(others.Count > MaxSkippedListed ? $", … {others.Count - MaxSkippedListed} more" : "")}");
		}

		var unrun = summary.Problems.Where(p => p.Outcome != TestOutcome.Failed).ToList();
		foreach (var problem in unrun.Take(MaxSkippedListed))
		{
			var label = problem.Outcome == TestOutcome.Skipped ? "SKIP" : "INCONCLUSIVE";
			var reason = MessageLines(problem.Message).FirstOrDefault();
			lines.Add(reason is null ? $"{label} {problem.Name}" : $"{label} {problem.Name}: {reason}");
		}
		if (unrun.Count > MaxSkippedListed) lines.Add($"... {unrun.Count - MaxSkippedListed} more skipped/inconclusive");
		if (reportPath is not null) lines.Add($"report: {reportPath}");
		return lines;
	}

	static IEnumerable<string> MessageLines(string? message)
	{
		var lines = Format.SplitLines(message).Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
		if (lines.Count <= MaxMessageLines) return lines;
		return [.. lines.Take(MaxMessageLines), $"… {lines.Count - MaxMessageLines} more lines"];
	}

	static string Duration(double seconds) => seconds switch
	{
		< 1 => $"{seconds.ToString("0.##", CultureInfo.InvariantCulture)}s",
		< 60 => $"{seconds.ToString("0.#", CultureInfo.InvariantCulture)}s",
		_ => $"{(int)(seconds / 60)}m{(int)(seconds % 60):00}s",
	};

	// ------------------------------------------------------------------ test list

	[GeneratedRegex(@"^(?<method>[^(]+)\((?<args>.*)\)$")]
	private static partial Regex Parameterized();

	/// <summary>
	/// `test list`: tests grouped by assembly and fixture, parameterised cases folded into one name.
	/// A large catalog shows assembly counts here and the full grouped list in a file.
	/// </summary>
	public static List<string>? RenderList(JsonNode? result, Func<string, string?> spill)
	{
		if (result.GetAny("Tests") is not JsonArray tests) return null;
		var rows = tests.Select(test => (
				Name: test.GetAny("FullName").Str() ?? test.GetAny("Name").Str() ?? "?",
				Assembly: test.GetAny("Assembly").Str() ?? "?",
				Mode: test.GetAny("Mode").Str() ?? "",
				Categories: test.GetAny("Categories").Items().Select(Format.Fmt).Where(c => c is not ("" or "Uncategorized")).ToList(),
				Explicit: test.GetAny("Explicit").Bool() == true))
			.ToList();

		var modes = rows.Select(r => r.Mode).Where(m => m.Length > 0).Distinct().ToList();
		var assemblies = rows.GroupBy(r => r.Assembly).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
		var head = $"{rows.Count} tests" + (modes.Count > 0 ? $" ({string.Join(", ", modes)})" : "") + $" in {assemblies.Count} assemblies";

		var full = new List<string>();
		foreach (var assembly in assemblies.OrderBy(g => g.Key, StringComparer.Ordinal))
		{
			full.Add($"{assembly.Key} ({assembly.Count()})");
			foreach (var fixture in assembly.GroupBy(r => FixtureOf(r.Name)).OrderBy(g => g.Key, StringComparer.Ordinal))
			{
				var methods = new List<string>();
				foreach (var method in fixture.GroupBy(r => MethodOf(r.Name, fixture.Key)))
				{
					var first = method.First();
					// MethodOf folds parameterised cases to `Name()`; plain NUnit names carry no parentheses.
					var label = method.Key.EndsWith("()", StringComparison.Ordinal) ? $"{method.Key[..^2]}(×{method.Count()})" : method.Key;
					if (first.Categories.Count > 0) label += $"[{string.Join(",", first.Categories)}]";
					if (first.Explicit) label += "(explicit)";
					methods.Add(label);
				}
				full.Add($"  {fixture.Key}: {string.Join(" ", methods)}");
			}
		}

		if (rows.Count <= MaxListedTests) return [head, .. full];
		var path = spill(string.Join('\n', full) + "\n");
		var lines = new List<string> { head };
		lines.AddRange(assemblies.Select(g => $"  {g.Key}  {g.Count()}"));
		lines.Add(path is null
			? "Narrow with `unity cmd test list --mode editor|playmode`."
			: $"full list, grouped by fixture: {path} (grep it; run a subset with `unity cmd test run --filter <name|assembly> --filter_type testName|assembly`)");
		return lines;
	}

	/// <summary>`Ns.Fixture.Method(args)` → `Ns.Fixture`, ignoring dots inside the argument list.</summary>
	static string FixtureOf(string fullName)
	{
		var open = fullName.IndexOf('(');
		var head = open < 0 ? fullName : fullName[..open];
		var dot = head.LastIndexOf('.');
		return dot < 0 ? "" : head[..dot];
	}

	static string MethodOf(string fullName, string fixture)
	{
		var method = fixture.Length == 0 ? fullName : fullName[(fixture.Length + 1)..];
		return Parameterized().Match(method) is { Success: true } match ? match.Groups["method"].Value + "()" : method;
	}
}
