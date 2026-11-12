using System.Text.Json.Nodes;
using OptimizedUnity.Features;
using OptimizedUnity.Host;
using OptimizedUnity.Output;
using OptimizedUnity.Proc;

namespace OptimizedUnity.Tests;

public class BuildStatusTests
{
	const string ReportDir = "Temp/build-reports/build_9d0968bbb24f";

	static TempDir Project()
	{
		var project = new TempDir();
		Directory.CreateDirectory(project.Sub("Assets"));
		Directory.CreateDirectory(project.Sub("ProjectSettings"));
		return project;
	}

	static ChildResult Render(string stdout, string? projectRoot) => BuildStatus.TryRender(stdout, _ => projectRoot)!;

	static string Report(TempDir project, string file) => File.ReadAllText(Path.Combine(project.Path, ReportDir, file));

	[Fact]
	public void PrintsTheHeadlineInline()
	{
		using var project = Project();
		var result = Render(Fixture.Text("build-status.json"), project.Path);
		Assert.Equal(ExitCodes.Success, result.Code);
		Assert.Equal(
			"status: completed  result: Succeeded  platform: StandaloneWindows64  time: 11m27s  size: 1.75 GB  buildId: build_9d0968bbb24f\n" +
			"output: C:/dev/Game/Builds/Win/Game.exe\n" +
			"errors: 3  warnings: 2\n" +
			$"reports: {ReportDir}/  errors.txt  warnings.txt  files.txt  steps.txt  assets.txt\n",
			result.Stdout);
	}

	[Fact]
	public void MergesRepeatedMessages()
	{
		using var project = Project();
		Render(Fixture.Text("build-status.json"), project.Path);
		Assert.Equal("Tree prefab at index 0 is missing. ×2\n\nIOException: Sharing violation\n\n", Report(project, "errors.txt"));
		Assert.StartsWith("Read/Write is disabled on 1 mesh(es).\nAffected meshes:\n - 'Plane001'\n\nPre-baked", Report(project, "warnings.txt"));
	}

	[Fact]
	public void ListsFilesLargestFirstRelativeToTheOutputFolder()
	{
		using var project = Project();
		Render(Fixture.Text("build-status.json"), project.Path);
		Assert.Equal(
			"4 files, 13.5 MB, paths relative to C:/dev/Game/Builds/Win/\n" +
			"   12.8 MB  SharedAssets  Game_Data/sharedassets0.assets\n" +
			"    652 KB  exe           Game.exe\n" +
			"    2.8 KB  Scene         Game_Data/level0\n" +
			"      1 KB  dll           D:/elsewhere/crash.dll\n",
			Report(project, "files.txt"));
	}

	[Fact]
	public void StepsKeepLongStepsAndStepsWithProblems()
	{
		using var project = Project();
		Render(Fixture.Text("build-status.json"), project.Path);
		Assert.Equal(
			"5 steps; indented by depth\n" +
			"Build player  11m20s\n" +
			"  Building scenes  6m21s\n" +
			"    Building scene Boot  0.04s  [2 errors, 1 warning]\n" +
			"  Writing asset files  1m36s\n" +
			"(1 of 5 steps omitted: under 100 ms with no errors or warnings)\n",
			Report(project, "steps.txt"));
	}

	[Fact]
	public void AssetsAreTotalledAcrossBundles()
	{
		using var project = Project();
		Render(Fixture.Text("build-status.json"), project.Path);
		Assert.Equal(
			"Bundles: 2, 5.57 KB\n" +
			"   2.94 KB  sharedassets0.assets (2 objects)\n" +
			"   2.63 KB  level0 (3 objects)\n" +
			"\n" +
			"Assets: 3, 5.47 KB; sizes summed across bundles, types largest first\n" +
			"   4.88 KB  Assets/Art/Rock.fbx  [Mesh]\n" +
			"     591 B  Assets/Scenes/Boot.unity  [Transform, GameObject]\n" +
			"      10 B  (no asset path)  [Shader]\n",
			Report(project, "assets.txt"));
	}

	[Fact]
	public void AcceptsTheEnvelopeForm()
	{
		using var project = Project();
		var envelope = new JsonObject
		{
			["success"] = true,
			["data"] = new JsonObject { ["command"] = "build status", ["result"] = JsonNode.Parse(Fixture.Text("build-status.json")) },
		};
		var result = Render(envelope.ToJsonString(), project.Path);
		Assert.StartsWith("status: completed  result: Succeeded", result.Stdout);
	}

	[Fact]
	public void InProgressShowsScalarsAndWritesNothing()
	{
		using var project = Project();
		var result = Render("""{"status":"running","buildId":"b1","platform":"Android","progress":0.4}""", project.Path);
		Assert.Equal("status: running  platform: Android  buildId: b1\nprogress: 0.4\n", result.Stdout);
		Assert.False(Directory.Exists(project.Sub("Temp")));
	}

	[Fact]
	public void FailedBuildExitsNonZero()
	{
		using var project = Project();
		var result = Render("""{"status":"completed","buildId":"b2","result":"Failed","totalErrors":1,"errors":[{"message":"boom"}]}""", project.Path);
		Assert.Equal(ExitCodes.CommandFailed, result.Code);
		Assert.Contains("errors: 1\n", result.Stdout);
	}

	[Fact]
	public void UnknownShapeFallsBackToTheGenericRenderer() =>
		Assert.Null(BuildStatus.TryRender("""{"something":"else"}""", _ => null));

	[Fact]
	public void WithoutAProjectReportsGoToTheSpillFolder()
	{
		var result = Render("""{"buildId":"b3","result":"Succeeded","errors":[{"message":"e"}]}""", null);
		var directory = Path.Combine(Spill.Root, "build-reports", "b3");
		Assert.Contains($"reports: {directory}{Path.DirectorySeparatorChar}  errors.txt\n", result.Stdout);
		Assert.True(File.Exists(Path.Combine(directory, "errors.txt")));
	}

	[Fact]
	public void ProjectRootPrefersTheFlag()
	{
		using var project = Project();
		using var other = Project();
		Assert.Equal(other.Path, BuildStatus.ProjectRoot(["cmd", "build", "status", "--project-path", other.Path], project.Path, null, null));
	}

	[Fact]
	public void ProjectRootWalksUpFromTheWorkingDirectory()
	{
		using var project = Project();
		var nested = project.Sub("Assets", "Scripts");
		Directory.CreateDirectory(nested);
		Assert.Equal(project.Path, BuildStatus.ProjectRoot(["cmd", "build", "status"], nested, null, null));
	}

	[Fact]
	public void ProjectRootFallsBackToTheBuildOutput()
	{
		using var project = Project();
		using var elsewhere = new TempDir();
		var output = project.Sub("Builds", "Win", "Game.exe");
		Assert.Equal(project.Path, BuildStatus.ProjectRoot(["cmd", "build", "status"], elsewhere.Path, null, output));
		Assert.Null(BuildStatus.ProjectRoot(["cmd", "build", "status"], elsewhere.Path, null, null));
	}

	[LinuxFact]
	public void ProjectRootConvertsWindowsOutputPathsUnderWsl()
	{
		using var project = Project();
		using var elsewhere = new TempDir();
		var wsl = new Wsl((_, _) => project.Sub("Builds", "Game.exe"));
		Assert.Equal(project.Path, BuildStatus.ProjectRoot(["cmd", "build", "status"], elsewhere.Path, wsl, @"C:\dev\Game\Builds\Game.exe"));
	}

	[Theory]
	[InlineData("cmd", true)]
	[InlineData("command", true)]
	[InlineData("other", false)]
	public void MatchesAliases(string command, bool expected) =>
		Assert.Equal(expected, BuildStatus.AppliesTo([command, "build", "status"]));
}
