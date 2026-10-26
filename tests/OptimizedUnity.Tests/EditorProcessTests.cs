using OptimizedUnity.Features;

namespace OptimizedUnity.Tests;

public class EditorProcessTests
{
	[Theory]
	[InlineData("/Applications/Unity/Unity.app/Contents/MacOS/Unity -projectpath /Users/me/My Game -useHub -hubIPC", "/Users/me/My Game")]
	[InlineData("Unity -projectPath \"C:\\Games\\My Game\" -logFile x", "C:\\Games\\My Game")]
	[InlineData("Unity -projectpath=/p/q --flag", "/p/q")]
	[InlineData("Unity -batchmode", null)]
	[InlineData("Unity -projectpath", null)]
	public void ParsesProjectFromCommandLine(string command, string? expected) =>
		Assert.Equal(expected, NativeProcessControl.ProjectFromCommandLine(command));

	[Fact]
	public void InspectsCurrentProcessAsNonUnity()
	{
		if (OperatingSystem.IsWindows()) return;
		var control = new NativeProcessControl(wsl: false);
		var error = Assert.Throws<ProcessControlException>(() => control.Inspect(Environment.ProcessId));
		Assert.Contains("could not be verified", error.Message);
	}

	[Fact]
	public void MissingProcessIsNull()
	{
		if (OperatingSystem.IsWindows()) return;
		Assert.Null(new NativeProcessControl(wsl: false).Inspect(int.MaxValue - 7));
	}
}
