using System.Text.Json.Nodes;
using OptimizedUnity.Json;
using OptimizedUnity.Output;

namespace OptimizedUnity.Tests;

public class OutputTests
{
	[Fact]
	public void KeepsTailWithinLineBudget()
	{
		var text = string.Join('\n', Enumerable.Range(1, 10));
		var result = OutputBudget.Truncate(text, maxLines: 3, maxBytes: 1_000);
		Assert.True(result.Truncated);
		Assert.Equal("8\n9\n10", result.Content);
		Assert.Equal(10, result.TotalLines);
	}

	[Fact]
	public void KeepsTailWithinByteBudgetWithoutSplittingCharacters()
	{
		var result = OutputBudget.Truncate("ab€€€", maxLines: 10, maxBytes: 7);
		Assert.Equal("€€", result.Content);
	}

	[Fact]
	public void UntruncatedOutputIsUnchanged() => Assert.Equal("a\nb\n", OutputBudget.Compact("a\nb\n", "t", 5, 100));

	[Fact]
	public void TruncatedOutputPointsAtFullCopy()
	{
		var text = string.Join('\n', Enumerable.Range(1, 50)) + "\n";
		var compact = OutputBudget.Compact(text, "status", 5, 10_000);
		var header = compact.Split('\n')[0];
		Assert.StartsWith("[Output truncated: kept the last 5 of 50 lines", header);
		var path = header[(header.IndexOf("Full output: ", StringComparison.Ordinal) + 13)..^1];
		Assert.Equal(text, File.ReadAllText(path));
		Assert.EndsWith("46\n47\n48\n49\n50\n", compact);
	}

	[Fact]
	public void DocumentsKeepTheirHead()
	{
		var text = string.Join('\n', Enumerable.Range(1, 50)) + "\n";
		var compact = OutputBudget.Compact(text, "changelog", 5, 10_000, keepHead: true);
		Assert.StartsWith("1\n2\n3\n4\n5\n[Output truncated: kept the first 5 of 50 lines", compact);
		Assert.Equal("€€", OutputBudget.Truncate("€€€ab", maxLines: 10, maxBytes: 7, keepHead: true).Content);
	}

	[Fact]
	public void LongBase64BecomesFile()
	{
		var png = new byte[6_000];
		new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
		var node = new JsonObject { ["data"] = new JsonObject { ["base64"] = Convert.ToBase64String(png), ["width"] = 10 } };
		Spill.Blobs(node, "capture-game_view");
		var text = node["data"]!["base64"].Str()!;
		Assert.StartsWith("<base64 6000 bytes → ", text);
		var path = text["<base64 6000 bytes → ".Length..^1];
		Assert.EndsWith(".png", path);
		Assert.Equal(png, File.ReadAllBytes(path));
		Assert.Equal(10, node["data"]!["width"].Int());
	}

	[Fact]
	public void LongTextIsNotTreatedAsBase64()
	{
		var prose = string.Concat(Enumerable.Repeat("plain words here ", 400));
		var node = new JsonObject { ["message"] = prose };
		Spill.Blobs(node, "x");
		Assert.Equal(prose, node["message"].Str());
	}
}
