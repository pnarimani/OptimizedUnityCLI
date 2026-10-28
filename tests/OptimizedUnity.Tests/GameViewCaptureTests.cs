using System.Text.Json.Nodes;
using OptimizedUnity.Features;

namespace OptimizedUnity.Tests;

public class GameViewCaptureTests
{
    [Fact]
    public void SavesSmallPngUnderTargetProject()
    {
        using var temp = new TempDir();
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 1];
        var envelope = new JsonObject
        {
            ["success"] = true,
            ["data"] = new JsonObject
            {
                ["command"] = "capture game_view",
                ["target"] = new JsonObject { ["projectPath"] = temp.Path },
                ["result"] = new JsonObject { ["image"] = Convert.ToBase64String(png) },
            },
        };
        var result = GameViewCapture.Render(envelope, path => path);
        Assert.Equal(0, result.Code);
        Assert.StartsWith("Temp/pipeline-screenshots/", result.Stdout);
        Assert.Equal(png, File.ReadAllBytes(Path.Combine(temp.Path, result.Stdout.Trim())));
        envelope["data"]!["result"] = new JsonObject { ["image"] = "invalid" };
        Assert.NotEqual(0, GameViewCapture.Render(envelope, path => path).Code);
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
