using System.Text.Json.Nodes;
using OptimizedUnity.Features;
using OptimizedUnity.Render;

namespace OptimizedUnity.Tests;

public class GameViewCaptureTests
{
    const string CapturePath = "Temp/pipeline-screenshots/";

    static JsonNode Capture(string savedPath, string message = "") =>
        ResultEnvelope.Parse(
            ["cmd", "capture", "game_view"],
            new JsonObject { ["width"] = 1280, ["savedPath"] = savedPath, ["message"] = message }.ToJsonString(),
            0)!;

    [Theory]
    [InlineData(@"C:\dev\Game\Temp\pipeline-screenshots\a.png")]
    [InlineData("/mnt/c/dev/Game/Temp/pipeline-screenshots/a.png")]
    public void PrintsPathInsideTheProject(string savedPath)
    {
        var result = GameViewCapture.Render(Capture(savedPath));
        Assert.Equal(0, result.Code);
        Assert.Equal($"{CapturePath}a.png\n", result.Stdout);
    }

    [Fact]
    public void KeepsTheCameraFallbackNote()
    {
        var result = GameViewCapture.Render(Capture("/p/Temp/pipeline-screenshots/a.png", "fell back to camera"));
        Assert.Equal($"{CapturePath}a.png\nnote: fell back to camera\n", result.Stdout);
    }

    [Fact]
    public void FailsWithoutSavedPath()
    {
        var envelope = ResultEnvelope.Parse(["cmd", "capture", "game_view"], """{"width":1280}""", 0)!;
        Assert.NotEqual(0, GameViewCapture.Render(envelope).Code);
    }

    [Fact]
    public void ChoosesAnOutputPathOnlyWhenTheCallerDidNot()
    {
        string[] plain = ["cmd", "capture", "game_view"];
        Assert.True(GameViewCapture.ShouldChooseOutputPath(plain));
        Assert.False(GameViewCapture.ShouldChooseOutputPath([.. plain, "--output-path", "x.png"]));
        Assert.False(GameViewCapture.ShouldChooseOutputPath(["cmd", "capture", "scene_view"]));

        var withPath = GameViewCapture.WithOutputPath([.. plain, "--", "tail"]);
        Assert.Equal("--output-path", withPath[3]);
        Assert.StartsWith(CapturePath, withPath[4]);
        Assert.EndsWith(".png", withPath[4]);
        Assert.Equal("--", withPath[5]);
    }

    [Theory]
    [InlineData("cmd", true)]
    [InlineData("command", true)]
    [InlineData("other", false)]
    public void MatchesAliases(string command, bool expected)
    {
        Assert.Equal(expected, GameViewCapture.AppliesTo([command, "capture", "game_view"]));
        Assert.False(GameViewCapture.AppliesTo([command, "capture", "scene_view"]));
    }
}
