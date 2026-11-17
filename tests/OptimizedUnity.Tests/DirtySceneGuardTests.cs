using OptimizedUnity.Features;

namespace OptimizedUnity.Tests;

public class DirtySceneGuardTests
{
	[Theory]
	[InlineData(true, "cmd", "test", "run")]
	[InlineData(true, "cmd", "test", "run", "--filter", "Foo", "--port", "7800")]
	[InlineData(true, "command", "run_tests")]
	[InlineData(false, "cmd", "test", "list")]
	[InlineData(false, "cmd", "test", "status")]
	[InlineData(false, "test")]
	[InlineData(false, "cmd", "scene", "list_open")]
	public void AppliesOnlyToEditorTestRuns(bool expected, params string[] args) =>
		Assert.Equal(expected, DirtySceneGuard.AppliesTo(args));

	[Fact]
	public void ListsOpenScenesOnTheTargetedEditor() =>
		Assert.Equal(
			["cmd", "scene", "list_open", "--json", "--port", "7800"],
			DirtySceneGuard.ListOpenArgs(["cmd", "test", "run", "--port", "7800", "--filter", "Foo"]));

	[Fact]
	public void FindsDirtyScenesInLineDelimitedResults()
	{
		const string stdout = """
			{"name":"World_3","path":"Assets/Scenes/World_3.unity","isLoaded":true,"isDirty":true,"isActive":true,"rootCount":32}
			{"name":"Tile","path":"Assets/Scenes/Tile.unity","isLoaded":true,"isDirty":false,"isActive":false,"rootCount":1}
			{"name":"","path":"","isLoaded":true,"isDirty":true,"isActive":false,"rootCount":1}

			""";
		Assert.Equal(["Assets/Scenes/World_3.unity", "untitled"], DirtySceneGuard.DirtyScenes(stdout));
	}

	[Fact]
	public void FindsDirtyScenesInAnEnvelope()
	{
		const string stdout = """
			{"success":true,"data":{"result":[{"name":"Main","path":"Assets/Main.unity","isDirty":true}]}}
			""";
		Assert.Equal(["Assets/Main.unity"], DirtySceneGuard.DirtyScenes(stdout));
	}

	[Theory]
	[InlineData("""{"name":"Main","path":"Assets/Main.unity","isDirty":false}""")]
	[InlineData("""{"success":false,"command":"command","data":{"candidates":[]},"errors":[{"code":"AMBIGUOUS_EDITOR","message":"x"}],"warnings":[]}""")]
	[InlineData("error: no Editor")]
	[InlineData("")]
	public void FindsNoneWhenCleanOrUnreadable(string stdout) =>
		Assert.Empty(DirtySceneGuard.DirtyScenes(stdout));

	[Fact]
	public void RejectionNamesTheDirtyScenes() =>
		Assert.Contains("dirty (Assets/A.unity, Assets/B.unity)", DirtySceneGuard.Rejection(["Assets/A.unity", "Assets/B.unity"]));
}
