using System.Text.Json.Nodes;
using OptimizedUnity.Render;

namespace OptimizedUnity.Tests;

public class TestReportTests
{
	static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

	/// <summary>The fixtures carry anonymised, hash-based test names; blank them so only the report structure is compared.</summary>
	static List<string> WithoutTestNames(IEnumerable<string> lines) => lines.Select(line =>
	{
		if (line.StartsWith("FAIL ", StringComparison.Ordinal)) return "FAIL <test>" + (line.Contains('(') ? line[line.LastIndexOf(" (", StringComparison.Ordinal)..] : "");
		if (line.StartsWith("SKIP ", StringComparison.Ordinal)) return "SKIP <test>" + line[line.IndexOf(": ", StringComparison.Ordinal)..];
		return line;
	}).ToList();

	[Fact]
	public void PassingEditorRunIsOneLine()
	{
		var lines = EnvelopeRenderer.Render(Fixture.Json("test-run-passed.json"));
		Assert.Equal(["143 tests: 143 passed (7m31s, EditMode, assembly: Sample.Navigation.Tests.Editor)"], lines);
	}

	[Fact]
	public void FailingEditorRunShowsOnlyProblems()
	{
		var lines = EnvelopeRenderer.Render(Fixture.Json("test-run-failed.json"));
		Assert.StartsWith("143 tests: 138 passed, 4 failed, 1 skipped (7m31s", lines[0]);
		Assert.Equal(2, lines.Count(line => line.StartsWith("FAIL ", StringComparison.Ordinal)));
		Assert.Contains("    Expected: 4", lines);
		Assert.Contains("    at Sample.Navigation.Tests.Editor.X.Y () in Assets/Tests/X.cs:16", lines);
		Assert.Single(lines, line => line.StartsWith("SKIP ", StringComparison.Ordinal) && line.EndsWith(": not ready", StringComparison.Ordinal));
		Assert.DoesNotContain(lines, line => line.Contains("Passed") || line.Contains("NUnit.") || line.Contains("(wrapper"));
	}

	[Fact]
	public void IdenticalFailuresAreGrouped()
	{
		var lines = EnvelopeRenderer.Render(Fixture.Json("test-run-failed.json"));
		var head = Assert.Single(lines, line => line.Contains("(+2 more with the same failure)"));
		Assert.StartsWith("FAIL Sample.Generated.Tests.Editor.GeneratedTest_", head);
		Assert.Contains("    at Sample.Navigation.Tests.Editor.Fixture.Setup () in com.example.nav/Tests/Fixture.cs:22", lines);
		var same = Assert.Single(lines, line => line.StartsWith("    same failure: ", StringComparison.Ordinal));
		Assert.Equal(2, same["    same failure: ".Length..].Split(", ").Length);
	}

	[Fact]
	public void DifferentLocationsAreNotGrouped()
	{
		var result = JsonNode.Parse("""
		{"Summary":{"Total":2,"Passed":0,"Failed":2},"Results":[
		  {"FullName":"A.T.One","Status":"Failed","Message":"Expected: True\n  But was: False","StackTrace":"at A.T.One () in /p/Assets/T.cs:5"},
		  {"FullName":"A.T.Two","Status":"Failed","Message":"Expected: True\n  But was: False","StackTrace":"at A.T.Two () in /p/Assets/T.cs:9"}]}
		""");
		var lines = EnvelopeRenderer.RenderEditor("test run", result);
		Assert.Equal(2, lines.Count(line => line.StartsWith("FAIL ", StringComparison.Ordinal)));
		Assert.DoesNotContain(lines, line => line.Contains("same failure"));
	}

	[Fact]
	public void LowercaseKeysAndMessagesAreHandled()
	{
		var result = JsonNode.Parse("""{"results":[{"fullName":"A.B.C","status":"failed","message":"boom"}],"message":"Tests already running"}""");
		Assert.Equal(["1 tests: 0 passed, 1 failed", "FAIL A.B.C", "    boom", "message: Tests already running"],
			EnvelopeRenderer.RenderEditor("test status", result));
	}

	[Fact]
	public void LongOutputAndMessagesAreCapped()
	{
		var output = string.Join('\n', Enumerable.Range(0, 50).Select(i => $"log {i}"));
		var message = string.Join('\n', Enumerable.Range(0, 30).Select(i => $"line {i}"));
		var result = new JsonObject
		{
			["Results"] = new JsonArray(new JsonObject { ["FullName"] = "A.B.C", ["Status"] = "Failed", ["Message"] = message, ["Output"] = output }),
		};
		var lines = EnvelopeRenderer.RenderEditor("test run", result);
		Assert.Contains("    output (last 10 of 50 lines):", lines);
		Assert.Contains("      log 49", lines);
		Assert.DoesNotContain("      log 39", lines);
		Assert.Contains("    … 18 more lines", lines);
	}

	[Fact]
	public void NUnitReportMatchesRealRun()
	{
		var summary = TestReports.FromReport(FixturePath("test-results.nunit.xml"))!;
		Assert.Equal((45, 41, 3, 1), (summary.Total, summary.Passed, summary.Failed, summary.Skipped));
		Assert.Equal("EditMode", summary.Mode);
		var lines = WithoutTestNames(TestReports.Render(summary, "/work/test-results.xml"));
		Assert.Equal(
		[
			"45 tests: 41 passed, 3 failed, 1 skipped (0.07s, EditMode)",
			"FAIL <test>",
			"    sum should be four",
			"    Expected: 4",
			"    But was:  5",
			"    at Demo.Tests.MathTests.FailsEquality () in Assets/Tests/DemoTests.cs:16",
			"FAIL <test>",
			"    System.InvalidOperationException : boom from helper",
			"    at Demo.Tests.Helper.Deeper () in Assets/Tests/DemoTests.cs:45",
			"    at Demo.Tests.Helper.Explode () in Assets/Tests/DemoTests.cs:44",
			"    at Demo.Tests.MathTests.FailsWithException () in Assets/Tests/DemoTests.cs:19",
			"FAIL <test>",
			"    Expected: collection containing 4",
			"    But was:  < 1, 2, 3 >",
			"    at Demo.Tests.NoisyTests.FailsAfterLogging () in Assets/Tests/DemoTests.cs:38",
			"    output:",
			"      about to fail",
			"SKIP <test>: not ready",
			"report: /work/test-results.xml",
		], lines);
	}

	[Fact]
	public void JUnitReportMatchesNUnit()
	{
		var nunit = WithoutTestNames(TestReports.Render(TestReports.FromReport(FixturePath("test-results.nunit.xml"))!));
		var junit = WithoutTestNames(TestReports.Render(TestReports.FromReport(FixturePath("test-results.junit.xml"))!));
		Assert.Equal(nunit.Skip(1), junit.Skip(1));
		Assert.StartsWith("45 tests: 41 passed, 3 failed, 1 skipped", junit[0]);
	}

	[Fact]
	public void SetUpFailureOfAFixtureIsReported()
	{
		using var temp = new TempDir();
		var path = temp.Sub("r.xml");
		File.WriteAllText(path, """
			<test-run total="2" passed="0" failed="2" skipped="0" inconclusive="0" duration="0.5">
			  <test-suite type="TestFixture" fullname="A.Fixture" result="Failed" site="SetUp">
			    <failure><message><![CDATA[OneTimeSetUp: boom]]></message><stack-trace><![CDATA[at A.Fixture.Setup () [0x0] in /p/Assets/F.cs:3]]></stack-trace></failure>
			    <test-case fullname="A.Fixture.One" result="Failed" site="Parent"><failure><message><![CDATA[OneTimeSetUp: boom]]></message></failure></test-case>
			    <test-case fullname="A.Fixture.Two" result="Failed" site="Parent"><failure><message><![CDATA[OneTimeSetUp: boom]]></message></failure></test-case>
			  </test-suite>
			</test-run>
			""");
		var lines = TestReports.Render(TestReports.FromReport(path)!);
		Assert.Contains("FAIL A.Fixture (SetUp)", lines);
		Assert.Contains("    at A.Fixture.Setup () in Assets/F.cs:3", lines);
		Assert.Contains("FAIL A.Fixture.One (+1 more with the same failure)", lines);
		Assert.Contains("    same failure: Two", lines);
	}

	[Theory]
	[InlineData("<html/>")]
	[InlineData("not xml")]
	public void UnknownReportsAreIgnored(string contents)
	{
		using var temp = new TempDir();
		var path = temp.Sub("r.xml");
		File.WriteAllText(path, contents);
		Assert.Null(TestReports.FromReport(path));
		Assert.Null(TestReports.FromReport(temp.Sub("missing.xml")));
	}

	[Fact]
	public void SmallTestListIsGroupedByFixture()
	{
		var lines = EnvelopeRenderer.Render(Fixture.Json("test-list-small.json"));
		Assert.Equal("42 tests (EditMode) in 2 assemblies", lines[0]);
		Assert.Contains("Sample.Inventory.Tests.Editor (30)", lines);
		Assert.Contains(lines, line => line.StartsWith("  Sample.Generated.Tests.Editor: GeneratedTest_", StringComparison.Ordinal));
		Assert.DoesNotContain(lines, line => line.Contains("Uncategorized") || line.Contains("Explicit"));
		Assert.True(string.Join('\n', lines).Length < Fixture.Text("test-list-small.json").Length / 5);
	}

	[Fact]
	public void LargeTestListSummarisesAndSpillsTheRest()
	{
		var lines = EnvelopeRenderer.Render(Fixture.Json("test-list-large.json"));
		Assert.Equal("350 tests (EditMode) in 3 assemblies", lines[0]);
		Assert.Equal("  Sample.Navigation.Tests.Editor  143", lines[1]);
		var pointer = lines[^1];
		var path = pointer["full list, grouped by fixture: ".Length..pointer.IndexOf(" (grep", StringComparison.Ordinal)];
		var full = File.ReadAllText(path);
		Assert.Contains("Sample.Inventory.Tests.Editor (74)", full);
		Assert.Contains("Sample.Generated.Tests.Editor: GeneratedTest_", full);
	}

	static JsonObject TestListOf(IEnumerable<(string Name, string Assembly, string[]? Categories, bool Explicit)> tests) => new()
	{
		["Mode"] = "EditMode",
		["Tests"] = new JsonArray(tests.Select(t => (JsonNode)new JsonObject
		{
			["FullName"] = t.Name,
			["Mode"] = "EditMode",
			["Assembly"] = t.Assembly,
			["Categories"] = new JsonArray((t.Categories ?? ["Uncategorized"]).Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
			["Explicit"] = t.Explicit,
		}).ToArray()),
	};

	[Fact]
	public void TestListFoldsParameterizedCasesPerMethod()
	{
		var tests = Enumerable.Range(0, 8).Select(i => ($"Shop.Tests.CartTests.AddItem_WithValidItem_Works({i})", "Shop.Tests", (string[]?)null, false))
			.Append(("Shop.Tests.CartTests.Clear_EmptiesCart", "Shop.Tests", null, false))
			.Append(("Shop.Tests.PriceTests.Slow_Case", "Shop.Tests", ["Slow"], true))
			.Append(("Shop.Tests.PriceTests.Rounds(1.5, \"a.b\")", "Other.Tests", null, false));
		var lines = EnvelopeRenderer.RenderEditor("test list", TestListOf(tests));
		Assert.Equal("11 tests (EditMode) in 2 assemblies", lines[0]);
		Assert.Contains("Shop.Tests (10)", lines);
		Assert.Contains("  Shop.Tests.CartTests: AddItem_WithValidItem_Works(×8) Clear_EmptiesCart", lines);
		Assert.Contains("  Shop.Tests.PriceTests: Slow_Case[Slow](explicit)", lines);
		Assert.Contains("  Shop.Tests.PriceTests: Rounds(×1)", lines);
		Assert.DoesNotContain(lines, line => line.Contains("Uncategorized"));
	}

	[Fact]
	public void LargeTestListSpillsFixtureGroupedFile()
	{
		var tests = Enumerable.Range(0, TestReports.MaxListedTests + 1)
			.Select(i => ($"Nav.Tests.RouteResolverTests.Case_{i}", i % 2 == 0 ? "Nav.Tests" : "Inv.Tests", (string[]?)null, false));
		var lines = EnvelopeRenderer.RenderEditor("test list", TestListOf(tests));
		Assert.Equal($"{TestReports.MaxListedTests + 1} tests (EditMode) in 2 assemblies", lines[0]);
		Assert.Equal("  Nav.Tests  151", lines[1]);
		var pointer = lines[^1];
		var path = pointer["full list, grouped by fixture: ".Length..pointer.IndexOf(" (grep", StringComparison.Ordinal)];
		var full = File.ReadAllText(path);
		Assert.Contains("Inv.Tests (150)", full);
		Assert.Contains("  Nav.Tests.RouteResolverTests: Case_0 ", full);
	}

	[Fact]
	public void IdenticalFailuresShowOnlyTheDifferingNameSuffix()
	{
		const string stack = "at Nav.Tests.Fixture.Setup () in /p/Assets/Fixture.cs:22";
		var result = new JsonObject
		{
			["Results"] = new JsonArray(
				new[] { "RouteIdTests.Equality_Works", "RouteIdTests.ToString_ReturnsValue", "RouteIdTests.Hash_IsStable" }
					.Select(name => (JsonNode)new JsonObject { ["FullName"] = "Nav.Tests." + name, ["Status"] = "Failed", ["Message"] = "OneTimeSetUp: boom", ["StackTrace"] = stack })
					.ToArray()),
		};
		var lines = EnvelopeRenderer.RenderEditor("test run", result);
		Assert.Contains("FAIL Nav.Tests.RouteIdTests.Equality_Works (+2 more with the same failure)", lines);
		Assert.Contains("    same failure: ToString_ReturnsValue, Hash_IsStable", lines);
		Assert.Single(lines, line => line.StartsWith("FAIL ", StringComparison.Ordinal));
	}

	[Fact]
	public void NamedNUnitAndJUnitReportsRenderTheSame()
	{
		using var temp = new TempDir();
		var nunit = temp.Sub("n.xml");
		var junit = temp.Sub("j.xml");
		File.WriteAllText(nunit, """
			<test-run total="3" passed="1" failed="1" skipped="1" inconclusive="0" duration="0.5">
			  <test-suite type="TestFixture" fullname="Demo.Tests.MathTests" result="Failed">
			    <test-case fullname="Demo.Tests.MathTests.Passes" result="Passed" />
			    <test-case fullname="Demo.Tests.MathTests.FailsEquality" result="Failed"><failure><message><![CDATA[sum should be four]]></message><stack-trace><![CDATA[at Demo.Tests.MathTests.FailsEquality () in /p/Assets/T.cs:16]]></stack-trace></failure></test-case>
			    <test-case fullname="Demo.Tests.MathTests.Skipped" result="Skipped"><reason><message><![CDATA[not ready]]></message></reason></test-case>
			  </test-suite>
			</test-run>
			""");
		File.WriteAllText(junit, """
			<testsuites tests="3" failures="1" errors="0" skipped="1" time="0.5">
			  <testsuite name="Demo.Tests.MathTests">
			    <testcase classname="Demo.Tests.MathTests" name="Passes" />
			    <testcase classname="Demo.Tests.MathTests" name="FailsEquality"><failure message="sum should be four">at Demo.Tests.MathTests.FailsEquality () in /p/Assets/T.cs:16</failure></testcase>
			    <testcase classname="Demo.Tests.MathTests" name="Skipped"><skipped message="not ready" /></testcase>
			  </testsuite>
			</testsuites>
			""");
		var fromNUnit = TestReports.Render(TestReports.FromReport(nunit)!);
		var fromJUnit = TestReports.Render(TestReports.FromReport(junit)!);
		Assert.Equal(fromNUnit, fromJUnit);
		Assert.Equal(
		[
			"3 tests: 1 passed, 1 failed, 1 skipped (0.5s)",
			"FAIL Demo.Tests.MathTests.FailsEquality",
			"    sum should be four",
			"    at Demo.Tests.MathTests.FailsEquality () in Assets/T.cs:16",
			"SKIP Demo.Tests.MathTests.Skipped: not ready",
		], fromNUnit);
	}

	[Fact]
	public void StackFramesDropTestPlumbing()
	{
		var frames = StackFrames.Compact("""
			  at Demo.T.Run () [0x00000] in C:\proj\Assets\T.cs:4
			  at (wrapper managed-to-native) System.Reflection.RuntimeMethodInfo.InternalInvoke(object)
			  at NUnit.Framework.Internal.Reflect.InvokeMethod () [0x0] in <abc123>:0
			  at Lib.Helper.Go () [0x00012] in <0123abcdef>:0
			""", StackFrames.TestNoise, 6);
		Assert.Equal(["at Demo.T.Run () in Assets/T.cs:4", "at Lib.Helper.Go ()"], frames);
	}
}
