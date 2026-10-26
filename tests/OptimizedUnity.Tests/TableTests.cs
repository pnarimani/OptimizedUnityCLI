using System.Text.Json.Nodes;
using OptimizedUnity.Render;

namespace OptimizedUnity.Tests;

public class TableTests
{
	static List<string> Render(string fixture) => EnvelopeRenderer.Render(Fixture.Json(fixture));

	[Fact]
	public void AssetListGroupsByDirectoryAndHoistsConstants()
	{
		var lines = Render("asset-find.json");
		Assert.Equal("  all: type: Material", lines[2]);
		Assert.Equal("  Assets/Art/Environments/", lines[3]);
		Assert.StartsWith("    Forest/Materials/: mat_far.mat  mat_lamps.mat  mat_middle.mat", lines[4]);
		Assert.Contains(lines, line => line == "    Snow/Materials/: mat_Glow {1..11}.mat  mat_ice.mat  mat_snow.mat");
		Assert.DoesNotContain(lines, line => line.Contains("guid"));
		Assert.True(string.Join('\n', lines).Length < Fixture.Text("asset-find.json").Length / 5);
	}

	[Fact]
	public void BuildScenesFoldNumberedRunsAndKeepOrder()
	{
		var lines = Render("build-settings.json");
		Assert.Contains("  all: enabled: true", lines);
		var boot = lines.FindIndex(line => line.Contains("Boot.unity"));
		var main = lines.FindIndex(line => line.Contains("Main.unity"));
		Assert.True(boot >= 0 && boot <= main);
		Assert.Contains(lines, line => line.Contains("{") && line.Contains("..") && line.EndsWith(".unity", StringComparison.Ordinal));
	}

	[Fact]
	public void EditorSearchRendersResultsNotBlankLines()
	{
		var lines = Render("editor-search.json");
		Assert.DoesNotContain(lines, line => line.Trim().Length == 0);
		Assert.Contains("  all: provider: asset", lines);
		Assert.Contains("  Assets/Debug/: iconBlack.png  iconGrey.png", lines);
		// A label that differs from the file name survives; ones that repeat it are dropped.
		Assert.Contains("    Display-Bold.otf  label: Font Texture", lines);
		Assert.DoesNotContain(lines, line => line.Contains("GlobalObjectId"));
	}

	[Fact]
	public void ShortListsKeepOneLinePerRow()
	{
		var rows = new JsonArray(Enumerable.Range(0, 3).Select(i => (JsonNode)new JsonObject { ["path"] = $"Assets/A/{i}.mat", ["guid"] = $"{i}" }).ToArray());
		Assert.Equal(["path: Assets/A/0.mat  guid: 0", "path: Assets/A/1.mat  guid: 1", "path: Assets/A/2.mat  guid: 2"], Renderer.Render(rows));
	}

	[Fact]
	public void RowsWithoutPathsOnlyHoistConstants()
	{
		var rows = new JsonArray(Enumerable.Range(0, 8).Select(i => (JsonNode)new JsonObject { ["name"] = $"n{i}", ["kind"] = "x" }).ToArray());
		var lines = Renderer.Render(rows);
		Assert.Equal("all: kind: x", lines[0]);
		Assert.Equal("name: n7", lines[^1]);
	}

	[Theory]
	[InlineData("L_1.unity L_2.unity L_3.unity", "L_{1..3}.unity")]
	[InlineData("L_1.unity L_2.unity", "L_1.unity L_2.unity")]
	[InlineData("L_01 L_02 L_03 L_05 L_07 L_08 L_09", "L_{01..03,05,07..09}")]
	[InlineData("L_008 L_09 L_10", "L_008 L_09 L_10")]
	[InlineData("A_1.x A_2.x A_3.x B.x", "A_{1..3}.x B.x")]
	[InlineData("L_9.x L_10.x L_11.x", "L_{9..11}.x")]
	[InlineData("L_09.x L_10.x L_11.x", "L_{09..11}.x")]
	[InlineData("plain other", "plain other")]
	public void FoldsNumberedRuns(string names, string expected) =>
		Assert.Equal(expected.Split(' '), Renderer.FoldNumbered(names.Split(' ')));

	[Fact]
	public void IdenticalScalarsFold()
	{
		Assert.Equal(["props: <unsupported:Generic> ×7"], Renderer.Render(JsonNode.Parse("""{"props":["<unsupported:Generic>","<unsupported:Generic>","<unsupported:Generic>","<unsupported:Generic>","<unsupported:Generic>","<unsupported:Generic>","<unsupported:Generic>"]}""")));
		Assert.Equal(["0  0"], Renderer.Render(JsonNode.Parse("[0,0]")));
		Assert.Equal(["1  1  2"], Renderer.Render(JsonNode.Parse("[1,1,2]")));
	}

	[Fact]
	public void ConsoleRepeatsCollapseAcrossTheResponse()
	{
		var data = JsonNode.Parse("""
		{"entries":[
		  {"level":"warn","timestampUtc":"T11:00:00.0Z","message":"a"},
		  {"level":"warn","timestampUtc":"T11:00:01.0Z","message":"b"},
		  {"level":"warn","timestampUtc":"T11:00:02.0Z","message":"a"},
		  {"level":"error","timestampUtc":"T11:00:03.0Z","message":"e1","stackTrace":"Game.A ()\nGame.B ()"},
		  {"level":"error","timestampUtc":"T11:00:04.0Z","message":"e2","stackTrace":"Game.A ()\nGame.B ()"}
		]}
		""")!;
		Assert.Equal(["W 11:00:00 a ×2", "W 11:00:01 b", "E 11:00:03 e1", "    Game.A ()", "    Game.B ()", "E 11:00:04 e2", "    (same stack)"],
			EditorRenderers.Logs(data));
	}

	[Fact]
	public void ConsoleErrorsShareStacksAndDropEnginePaths()
	{
		var lines = Render("console-errors.json");
		// The same error for 17 clips: printed once, then only the clip that differs.
		Assert.Contains(lines, line => line.StartsWith("  ~ …run.fbx' exists twice: 'Assets/Art/Characters/Knight/Animations/Run.fbx'", StringComparison.Ordinal));
		Assert.DoesNotContain(lines, line => line.Contains("bokken"));
		Assert.Contains("    UnityEditor.EditorAssemblies:ProcessInitializeOnLoadMethodAttributes ()", lines);
		Assert.True(string.Join('\n', lines).Length < Fixture.Text("console-errors.json").Length / 4);
	}

	[Fact]
	public void SharedParentsPrintOnceAndKeepOrder()
	{
		var paths = new[]
		{
			"Assets/Content/Levels/Forest/A.unity", "Assets/Content/Levels/Forest/B.unity",
			"Assets/Content/Levels/Desert/C.unity", "Assets/Content/Levels/Desert/Night/D.unity",
			"Assets/Scenes/Boot.unity", "Assets/Content/Levels/Forest/E.unity",
			"Assets/Content/Levels/Ice/F.unity", "Assets/Content/Levels/Ice/G.unity",
		};
		var rows = new JsonArray(paths.Select(path => (JsonNode)new JsonObject { ["path"] = path, ["enabled"] = true }).ToArray());
		Assert.Equal(
		[
			"all: enabled: true",
			"Assets/Content/Levels/",
			"  Forest/: A.unity  B.unity",
			"  Desert/: C.unity",
			"  Desert/Night/: D.unity",
			"Assets/Scenes/: Boot.unity",
			"Assets/Content/Levels/",
			"  Forest/: E.unity",
			"  Ice/: F.unity  G.unity",
		], Renderer.Render(rows));
	}

	[Fact]
	public void OutlineSkipsShortParentsAndParentsThatHoldFiles()
	{
		// `Assets/` is too short to be worth a line; `…/Summer` holds files itself, so it stays a group.
		var outline = Renderer.Outline(["Assets/A", "Assets/B", "Assets/Backgrounds/Summer/Animations", "Assets/Backgrounds/Summer"]);
		Assert.Equal(["Assets/A", "Assets/B", "Assets/Backgrounds/Summer/Animations", "Assets/Backgrounds/Summer"], outline.Select(o => o.Label));
		Assert.All(outline, o => Assert.Empty(o.Headers));
	}

	[Fact]
	public void HeadersOnlyOpenWhenTheySaveMoreThanTheIndentCosts()
	{
		string[] directories = ["Assets/Plugins/EditorResources/A", "Assets/Plugins/EditorResources/B"];
		Assert.Single(Renderer.Outline(directories, [1, 1])[0].Headers);
		// 200 per-row lines would each gain two spaces of indent.
		Assert.Empty(Renderer.Outline(directories, [201, 1])[0].Headers);
	}

	[Fact]
	public void NearDuplicateMessagesPrintOnlyWhatDiffers()
	{
		var data = JsonNode.Parse("""
		{"entries":[
		  {"level":"error","timestampUtc":"T11:00:00.0Z","message":"Missing reference on 'Hero' in scene Main. Fix the prefab before building.","stackTrace":"Game.A ()"},
		  {"level":"error","timestampUtc":"T11:00:00.5Z","message":"Missing reference on 'Villain' in scene Main. Fix the prefab before building.","stackTrace":"Game.A ()"},
		  {"level":"error","timestampUtc":"T11:00:01.0Z","message":"Missing reference on 'Sidekick' in scene Main. Fix the prefab before building.","stackTrace":"Game.A ()"},
		  {"level":"error","timestampUtc":"T11:00:02.0Z","message":"Missing reference on 'Villain' in scene Main. Fix the prefab before building.","stackTrace":"Game.A ()"},
		  {"level":"error","timestampUtc":"T11:00:03.0Z","message":"Missing reference on 'Boss' in scene Main. Fix the prefab before building.","stackTrace":"Game.B ()"}
		]}
		""")!;
		Assert.Equal(
		[
			"E 11:00:00 Missing reference on 'Hero' in scene Main. Fix the prefab before building.",
			"    Game.A ()",
			"  ~ …Villain… ×2",
			"  ~ 11:00:01 …Sidekick…",
			// A different stack is a different error, even with the same text.
			"E 11:00:03 Missing reference on 'Boss' in scene Main. Fix the prefab before building.",
			"    Game.B ()",
		], EditorRenderers.Logs(data));
	}

	[Theory]
	[InlineData("Compiling scripts", "Compiling shaders")]
	[InlineData("Duplicate key 'a' found", "Duplicate key 'b' found")]
	[InlineData("same message here, nothing different at all", "same message here, nothing different at all")]
	public void ShortOrIdenticalMessagesDoNotFold(string template, string message) =>
		Assert.Null(EditorRenderers.Variation(template, message));
}
