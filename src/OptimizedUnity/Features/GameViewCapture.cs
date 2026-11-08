using System.Text.Json.Nodes;
using OptimizedUnity.Args;
using OptimizedUnity.Json;
using OptimizedUnity.Proc;
using OptimizedUnity.Render;

namespace OptimizedUnity.Features;

/// <summary>
/// `capture game_view` returns the PNG inline as base64 unless given `--output-path`. The wrapper
/// always supplies one, so the image lands in the project's Temp folder and only its path is printed.
/// </summary>
static class GameViewCapture
{
    const string OutputPathFlag = "--output-path";
    const string ScreenshotDirectory = "Temp/pipeline-screenshots";

    public static bool AppliesTo(IReadOnlyList<string> args) =>
        ArgRewriter.EditorCommandWords(args).SequenceEqual(new[] { "capture", "game_view" });

    /// <summary>Whether the wrapper should choose the destination: the caller did not.</summary>
    public static bool ShouldChooseOutputPath(IReadOnlyList<string> args) =>
        AppliesTo(args) && !ArgRewriter.HasFlag(args, OutputPathFlag);

    /// <summary>Relative paths resolve from the project root, so the file never lands under Assets/.</summary>
    public static List<string> WithOutputPath(IReadOnlyList<string> args) =>
        ArgRewriter.InsertBeforeDelimiter(args, OutputPathFlag, $"{ScreenshotDirectory}/{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.png");

    public static ChildResult Render(JsonNode envelope)
    {
        if (EnvelopeRenderer.CommandSuccess(envelope) != true)
        {
            return new ChildResult("", string.Join('\n', EnvelopeRenderer.Render(envelope)), ExitCodes.CommandFailed);
        }
        var result = EnvelopeRenderer.Decode(envelope.Get("data").Get("result"));
        if (result.Get("savedPath").Str() is not { Length: > 0 } savedPath)
        {
            return new ChildResult("", "ERROR: Capture response has no savedPath.\n", ExitCodes.CommandFailed);
        }
        var note = result.Get("message").Str();
        var text = string.IsNullOrWhiteSpace(note) ? $"{ProjectRelative(savedPath)}\n" : $"{ProjectRelative(savedPath)}\nnote: {note}\n";
        return new ChildResult(text, "", ExitCodes.Success);
    }

    /// <summary>`savedPath` is absolute (a Windows path under WSL); callers want the path inside the project.</summary>
    static string ProjectRelative(string savedPath)
    {
        var normalized = savedPath.Replace('\\', '/');
        var marker = normalized.LastIndexOf($"/{ScreenshotDirectory}/", StringComparison.Ordinal);
        return marker < 0 ? normalized : normalized[(marker + 1)..];
    }
}
