using System.Text.Json;
using System.Text.Json.Nodes;
using OptimizedUnity.Args;
using OptimizedUnity.Json;
using OptimizedUnity.Proc;

namespace OptimizedUnity.Features;

/// <summary>
/// `cmd script eval` prints the returned value and nothing else: no trailing newline, no CLI hints.
/// A failure prints only the error text. `--result-only` makes the CLI print the bare result
/// as one JSON document (plain `--json` prints an array one element per line).
/// </summary>
static class ScriptEval
{
    const string ResultOnlyFlag = "--result-only";
    const string DetachFlag = "--detach";

    /// <summary>Prefixes the CLI wraps around the Editor's own error text.</summary>
    static readonly string[] ErrorWrappers = ["Command execution failed: ", "The Editor reported: "];

    const string LinePrefix = "error: ";
    const string PipelineFrame = "at Unity.Pipeline.";

    public static bool AppliesTo(IReadOnlyList<string> args) =>
        ArgRewriter.EditorCommandWords(args).Take(2).SequenceEqual(["script", "eval"])
        && !ArgRewriter.HasFlag(args, DetachFlag);

    public static List<string> WithResultOnly(IReadOnlyList<string> args) =>
        ArgRewriter.HasFlag(args, ResultOnlyFlag) ? [.. args] : ArgRewriter.InsertBeforeDelimiter(args, ResultOnlyFlag);

    public static ChildResult Render(ChildResult raw)
    {
        var parsed = ParseDocument(raw.Stdout);
        if (parsed is { Value: { } envelope } && envelope.IsEnvelope())
        {
            return Failure(ErrorText(envelope), raw.Code);
        }
        if (raw.Code != ExitCodes.Success)
        {
            return Failure(FallbackError(raw), raw.Code);
        }
        var text = parsed is { } result ? ResultText(result.Value) : raw.Stdout.Trim();
        return new ChildResult(text, "", ExitCodes.Success);
    }

    readonly record struct Document(JsonNode? Value);

    /// <summary>A JSON `null` result parses to a null node, so it is boxed to tell it apart from no JSON.</summary>
    static Document? ParseDocument(string stdout)
    {
        var text = stdout.Trim();
        if (text.Length == 0)
        {
            return null;
        }
        try
        {
            return new Document(JsonNode.Parse(text));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string ResultText(JsonNode? value) =>
        value is JsonValue && value.Kind() == JsonValueKind.String ? value.Str()! : value.Compact();

    static ChildResult Failure(string error, int code) =>
        new("", error, code == ExitCodes.Success ? ExitCodes.CommandFailed : code);

    static string ErrorText(JsonNode envelope)
    {
        var messages = envelope.Get("errors").Items()
            .Select(error => error is JsonObject ? error.Get("message").Str() : error.Str())
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => Clean(message!));
        var text = string.Join('\n', messages);
        return text.Length > 0 ? text : "script eval failed";
    }

    /// <summary>Text the CLI printed instead of an envelope, such as a usage error.</summary>
    static string FallbackError(ChildResult raw)
    {
        var parts = new[] { raw.Stdout, WithoutHints(raw.Stderr) }.Where(part => part.Trim().Length > 0).Select(Clean);
        var text = string.Join('\n', parts);
        return text.Length > 0 ? text : $"script eval failed (exit {raw.Code})";
    }

    static string Clean(string message)
    {
        var unwrapped = message;
        foreach (var wrapper in ErrorWrappers)
        {
            var index = unwrapped.IndexOf(wrapper, StringComparison.Ordinal);
            if (index >= 0)
            {
                unwrapped = unwrapped[(index + wrapper.Length)..];
            }
        }
        var lines = unwrapped.ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.StartsWith(LinePrefix, StringComparison.Ordinal) ? line[LinePrefix.Length..] : line)
            .Select(line => line.TrimEnd())
            .Where(line => !line.TrimStart().StartsWith(PipelineFrame, StringComparison.Ordinal));
        return string.Join('\n', lines).Trim('\n');
    }

    /// <summary>The CLI appends `&lt;claude-code-hint … /&gt;` tags to stderr for agents.</summary>
    static string WithoutHints(string stderr) =>
        string.Join('\n', stderr.ReplaceLineEndings("\n").Split('\n')
            .Where(line => !line.TrimStart().StartsWith("<claude-code-hint", StringComparison.Ordinal)));
}
