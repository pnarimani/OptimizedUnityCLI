using System.Text.Json.Nodes;
using OptimizedUnity.Json;

namespace OptimizedUnity.Tests;

public class JsonNodeExtensionsTests
{
	[Fact]
	public void ParsesPlainJson() => Assert.True(JsonNode.ParseLenient("""{"success":true,"data":1}""").IsEnvelope());

	[Fact]
	public void SkipsBannerAndTrailingNoise()
	{
		var node = JsonNode.ParseLenient("Unity CLI v1 [beta]\n{\"success\":false,\"errors\":[{\"message\":\"x}\"}]}\ntrailing {");
		Assert.True(node.IsEnvelope());
		Assert.Equal("x}", node.Get("errors").Items().First().Get("message").Str());
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("error: unknown option")]
	[InlineData("{ broken")]
	public void ReturnsNullWithoutJson(string text) => Assert.Null(JsonNode.ParseLenient(text));

	[Fact]
	public void PlainValueIsNotEnvelope() => Assert.False(JsonNode.ParseLenient("[1,2]").IsEnvelope());
}
