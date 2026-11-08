using System.Text.Json.Nodes;
using OptimizedUnity.Json;
using OptimizedUnity.Render;

namespace OptimizedUnity.Tests;

public class ResultEnvelopeTests
{
	[Fact]
	public void KeepsTheCliEnvelope()
	{
		var envelope = ResultEnvelope.Parse(["status"], Fixture.Text("status.json"), 0);
		Assert.True(envelope.IsEnvelope());
		Assert.Equal("status", envelope.Get("command").Str());
	}

	[Fact]
	public void WrapsABareCommandResultAndRendersItLikeTheEnvelope()
	{
		var old = Fixture.Json("console-get.json");
		var bare = old.Get("data").Get("result")!.ToJsonString();

		var envelope = ResultEnvelope.Parse(["cmd", "console", "get", "--tail", "30"], bare, 0)!;

		Assert.Equal(EnvelopeRenderer.Render(old), EnvelopeRenderer.Render(envelope));
	}

	[Fact]
	public void NamesTheCommandFromItsFirstTwoWords()
	{
		var envelope = ResultEnvelope.Parse(["cmd", "script", "eval", "return 1+1;"], """{"success":true,"result":2}""", 0)!;
		Assert.Equal("script eval", envelope.Get("data").Get("command").Str());
	}

	[Fact]
	public void FallsBackToTheFirstWordForOneWordCommands()
	{
		var envelope = ResultEnvelope.Parse(["cmd", "execute", "Debug.Log(1)"], """{"success":true}""", 0)!;
		Assert.Equal("execute", envelope.Get("data").Get("command").Str());
	}

	[Fact]
	public void TakesSuccessFromTheResultOverTheExitCode()
	{
		var failed = ResultEnvelope.Parse(["cmd", "script", "eval", "x"], """{"success":false,"error":"Compilation Failed"}""", 0)!;
		Assert.False(failed.Get("success").Bool());
		Assert.Contains("Compilation Failed", string.Join('\n', EnvelopeRenderer.Render(failed)));

		var ok = ResultEnvelope.Parse(["cmd", "editor", "status"], """{"status":"ready"}""", 0)!;
		Assert.True(ok.Get("success").Bool());
	}

	[Theory]
	[InlineData("""{"groups":[{"group":"scene","count":3}]}""")]
	[InlineData("""{"commands":[{"command":"scene get_hierarchy","description":"d"}]}""")]
	[InlineData("""{"tags":[{"tag":"scene","count":3}]}""")]
	[InlineData("""{"jobId":"abc"}""")]
	public void LeavesListingsUnwrapped(string listing)
	{
		var envelope = ResultEnvelope.Parse(["cmd", "--query", "scene"], listing, 0)!;
		Assert.False(EnvelopeRenderer.IsEditorWrapper(envelope.Get("data")));
	}

	[Fact]
	public void IgnoresPlainTextAndNonEditorCommands()
	{
		Assert.Null(ResultEnvelope.Parse(["cmd", "nosuch"], "No command named 'nosuch' is available.", 13));
		Assert.Null(ResultEnvelope.Parse(["cmd", "scene", "get_hierarchy"], "see {docs} for details", 2));
		Assert.Null(ResultEnvelope.Parse(["projects", "list"], """{"a":1}""", 0));
	}
}
