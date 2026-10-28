using System.Text.Json.Nodes;
using OptimizedUnity.Args;
using OptimizedUnity.Json;
using OptimizedUnity.Proc;
using OptimizedUnity.Render;

namespace OptimizedUnity.Features;

static class GameViewCapture
{
    public static bool AppliesTo(IReadOnlyList<string> args) =>
        ArgRewriter.EditorCommandWords(args).SequenceEqual(new[] { "capture", "game_view" });

    public static ChildResult Render(JsonNode envelope, Func<string, string?> toLocal)
    {
        if (EnvelopeRenderer.CommandSuccess(envelope) != true)
        {
            return new ChildResult("", string.Join('\n', EnvelopeRenderer.Render(envelope)), ExitCodes.CommandFailed);
        }
        try
        {
            var project = envelope.Get("data").Get("target").Get("projectPath").Str();
            if (string.IsNullOrWhiteSpace(project))
            {
                return Failure("Capture response has no target project path.");
            }
            var root = toLocal(project);
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            {
                return Failure("Cannot resolve the target project directory.");
            }
            var images = new List<byte[]>();
            FindImages(EnvelopeRenderer.Decode(envelope.Get("data").Get("result")), images);
            if (images.Count != 1)
            {
                return Failure("Capture response must contain exactly one inline PNG.");
            }
            var directory = Path.Combine(root, "Temp", "pipeline-screenshots");
            Directory.CreateDirectory(directory);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.png";
                var path = Path.Combine(directory, name);
                FileStream stream;
                try
                {
                    stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                }
                catch (IOException) when (File.Exists(path))
                {
                    continue;
                }
                using (stream)
                {
                    stream.Write(images[0]);
                }
                return new ChildResult($"Temp/pipeline-screenshots/{name}\n", "", ExitCodes.Success);
            }
            return Failure("Cannot allocate a unique screenshot timestamp.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Failure($"Cannot save Game View capture: {error.Message}");
        }
    }

    static ChildResult Failure(string message) => new("", $"ERROR: {message}\n", ExitCodes.CommandFailed);

    static void FindImages(JsonNode? node, List<byte[]> images)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                FindImages(pair.Value, images);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                FindImages(item, images);
            }
        }
        else if (node.Str() is { } text)
        {
            const string prefix = "data:image/png;base64,";
            var payload = text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
            var buffer = new byte[payload.Length * 3 / 4 + 3];
            if (Convert.TryFromBase64String(payload, buffer, out var length) && length >= 8)
            {
                if (buffer.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                {
                    images.Add(buffer[..length]);
                }
            }
        }
    }
}
