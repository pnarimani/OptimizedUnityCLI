using System.Text.Json.Nodes;
using OptimizedUnity.Render;

namespace OptimizedUnity.Tests;

public class StackAndLogTests
{
	[Fact]
	public void StackDropsPlumbingAndShortensPackagePaths()
	{
		var trace = string.Join('\n',
			"UnityEngine.Debug:LogError (object)",
			"Unity.Pipeline.BasePipelineServer:CommandNotFound () (at ./Library/PackageCache/com.unity.pipeline@73d99a22b984/Runtime/Common/BasePipelineServer.cs:3034)",
			"System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int>:SetResult (int)",
			"System.Threading._ThreadPoolWaitCallback:PerformWaitCallback ()");
		Assert.Equal(["Unity.Pipeline.BasePipelineServer:CommandNotFound () (at com.unity.pipeline/Runtime/Common/BasePipelineServer.cs:3034)"],
			EditorRenderers.CompactStack(trace));
	}

	[Fact]
	public void StackIsCapped()
	{
		var frames = EditorRenderers.CompactStack(string.Join('\n', Enumerable.Range(0, 20).Select(i => $"Game.Frame{i} ()")));
		Assert.Equal(9, frames.Count);
		Assert.Equal("… 12 more frames", frames[^1]);
	}

	[Fact]
	public void ConsecutiveDuplicatesCollapse()
	{
		var data = JsonNode.Parse("""
		{"entries":[
		  {"level":"warn","timestampUtc":"2026-10-26T11:21:48.1Z","message":"same"},
		  {"level":"warn","timestampUtc":"2026-10-26T11:21:49.1Z","message":"same"},
		  {"level":"warn","timestampUtc":"2026-10-26T11:21:50.1Z","message":"same"},
		  {"level":"error","timestampUtc":"2026-10-26T11:21:51.1Z","message":"other\nsecond line","stackTrace":"Game.A ()\nGame.B ()"},
		  {"level":"log","timestampUtc":"2026-10-26T11:21:52.1Z","message":"same"}
		],"cursor":9,"session":"s","dropped":false,"reset":false}
		""")!;
		Assert.Equal(
		[
			"W 11:21:48 same ×3",
			"E 11:21:51 other",
			"    second line",
			"    Game.A ()",
			"    Game.B ()",
			"I 11:21:52 same",
			"cursor: 9  session: s",
		], EditorRenderers.Console(data));
	}

	[Fact]
	public void CliLogContextKeepsInvocationsApart()
	{
		var data = JsonNode.Parse("""
		[{"levelName":"info","timestamp":"2026-10-26T13:48:27.6Z","moduleName":"CLI","msg":"CLI invocation started","context":{"command":"status"}},
		 {"levelName":"info","timestamp":"2026-10-26T13:48:28.6Z","moduleName":"CLI","msg":"CLI invocation started","context":{"command":"logs"}}]
		""")!;
		Assert.Equal(["I 13:48:27 CLI CLI invocation started command=status", "  ~ 13:48:28 …logs"],
			EditorRenderers.Logs(data));
	}

	[Fact]
	public void TracesOnlyForErrorLevels()
	{
		var data = JsonNode.Parse("""{"entries":[{"level":"log","message":"m","stackTrace":"Game.A ()"}]}""")!;
		Assert.Equal(["I m"], EditorRenderers.Logs(data));
	}
}
