using OptimizedUnity.Features;
using OptimizedUnity.Proc;

namespace OptimizedUnity.Tests;

public class ScriptEvalTests
{
    const string Hint = "<claude-code-hint v=\"1\" type=\"plugin\" value=\"unity@claude-plugins-official\" />\n";

    static ChildResult Eval(string stdout, int code = 0, string stderr = Hint) =>
        ScriptEval.Render(new ChildResult(stdout, stderr, code));

    [Theory]
    [InlineData("2\n", "2")]
    [InlineData("null\n", "null")]
    [InlineData("true\n", "true")]
    [InlineData("\"a\\nb\"\n", "a\nb")]
    [InlineData("[\n  1,\n  \"x\",\n  {\n    \"a\": 1\n  }\n]\n", """[1,"x",{"a":1}]""")]
    [InlineData("{\n  \"a\": \"é\"\n}\n", """{"a":"é"}""")]
    public void PrintsOnlyTheResult(string stdout, string expected)
    {
        var result = Eval(stdout);
        Assert.Equal(expected, result.Stdout);
        Assert.Equal("", result.Stderr);
        Assert.Equal(0, result.Code);
    }

    [Fact]
    public void PrintsOnlyTheCompileErrors()
    {
        const string envelope = """
            {"success":false,"command":"command","data":null,"errors":[{"code":"COMMAND_FAILED","message":"Failed to execute command 'script': Command execution failed: error: COMPILATION_ERROR: Compilation Failed: 1 error(s)\nerror: line 1, col 7: error CS0103: The name 'foo' does not exist in the current context"}],"warnings":[]}
            """;
        var result = Eval(envelope, ExitCodes.CommandFailed);
        Assert.Equal("", result.Stdout);
        Assert.Equal("COMPILATION_ERROR: Compilation Failed: 1 error(s)\nline 1, col 7: error CS0103: The name 'foo' does not exist in the current context", result.Stderr);
        Assert.Equal(ExitCodes.CommandFailed, result.Code);
    }

    [Fact]
    public void DropsPipelineFramesFromRuntimeErrors()
    {
        const string envelope = """
            {"success":false,"data":null,"errors":[{"code":"COMMAND_FAILED","message":"Failed to execute command 'script': Command execution failed: error: RUNTIME_EXCEPTION: Runtime Error. Exception: boom\n  at Script+<<Initialize>>d__0.MoveNext () [0x00007] in eval.cs:1 \r\n  at Unity.Pipeline.Compilation.EvalCodeCompiler.Run () [0x00117] in EvalCodeCompiler.cs:129"}]}
            """;
        var result = Eval(envelope, ExitCodes.CommandFailed);
        Assert.Equal("RUNTIME_EXCEPTION: Runtime Error. Exception: boom\n  at Script+<<Initialize>>d__0.MoveNext () [0x00007] in eval.cs:1", result.Stderr);
    }

    [Fact]
    public void ReportsAFailedEnvelopeEvenWithExitCodeZero()
    {
        var result = Eval("""{"success":false,"errors":[{"message":"The Editor reported: error: INVALID_COMMAND_ARGS: Bad Request"}]}""");
        Assert.Equal("INVALID_COMMAND_ARGS: Bad Request", result.Stderr);
        Assert.Equal(ExitCodes.CommandFailed, result.Code);
    }

    [Fact]
    public void FallsBackToTheCliTextWithoutHints()
    {
        var result = Eval("", ExitCodes.BadArgs, $"error: unknown option '--bogus'\n{Hint}");
        Assert.Equal("unknown option '--bogus'", result.Stderr);
        Assert.Equal(ExitCodes.BadArgs, result.Code);
    }

    [Fact]
    public void AddsResultOnlyOnce()
    {
        string[] args = ["cmd", "script", "eval", "return 1;", "--json"];
        Assert.Equal([.. args, "--result-only"], ScriptEval.WithResultOnly(args));
        Assert.Equal([.. args, "--result-only"], ScriptEval.WithResultOnly([.. args, "--result-only"]));
    }

    [Theory]
    [InlineData(true, "cmd", "script", "eval", "return 1;")]
    [InlineData(true, "command", "script", "eval", "--file", "a.cs")]
    [InlineData(false, "cmd", "script", "eval", "return 1;", "--detach")]
    [InlineData(false, "cmd", "script", "eval_file", "a.cs")]
    [InlineData(false, "cmd", "console", "get")]
    public void AppliesToScriptEvalOnly(bool expected, params string[] args) =>
        Assert.Equal(expected, ScriptEval.AppliesTo(args));
}
