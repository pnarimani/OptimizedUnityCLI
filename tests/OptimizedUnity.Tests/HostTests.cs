using OptimizedUnity.Host;

namespace OptimizedUnity.Tests;

public class HostTests
{
	[Fact]
	public void LocatorPrefersConfiguredPath()
	{
		var locator = new CliLocator { GetEnv = name => name == "UNITY_CLI_PATH" ? "/opt/unity" : null, Home = "/h", FileExists = _ => true };
		Assert.Equal("/opt/unity", locator.Resolve());
	}

	[Fact]
	public void LocatorUsesInstalledDefault()
	{
		var installed = Path.Combine("/h", ".unity", "bin", "unity");
		var locator = new CliLocator { GetEnv = _ => null, Home = "/h", FileExists = path => path == installed };
		Assert.Equal(installed, locator.Resolve());
	}

	[Fact]
	public void LocatorSkipsItselfOnPath()
	{
		var locator = new CliLocator
		{
			GetEnv = name => name == "PATH" ? "/shim:/link:/real" : null,
			Home = "/h",
			SelfPath = Path.Combine("/shim", "unity"),
			FileExists = path => path.StartsWith(Path.Combine("/shim", ""), StringComparison.Ordinal) || path.StartsWith(Path.Combine("/link", ""), StringComparison.Ordinal) || path == Path.Combine("/real", "unity"),
			RealPath = path => path == Path.Combine("/link", "unity") ? Path.Combine("/shim", "unity") : path,
		};
		Assert.Equal(Path.Combine("/real", "unity"), locator.Resolve());
	}

	[Fact]
	public void LocatorIgnoresConfiguredSelf()
	{
		var locator = new CliLocator
		{
			GetEnv = name => name switch { "UNITY_CLI_PATH" => "/shim/unity", "PATH" => "/real", _ => null },
			Home = "/h",
			SelfPath = "/shim/unity",
			FileExists = path => path == Path.Combine("/real", "unity"),
		};
		Assert.Equal(Path.Combine("/real", "unity"), locator.Resolve());
	}

	[Fact]
	public void LocatorUnderWslFindsWindowsCli()
	{
		var locator = new CliLocator
		{
			GetEnv = name => name == "UNITY_CLI_PATH" ? @"C:\Users\me\.unity\bin\unity.exe" : null,
			Home = "/home/me",
			IsWsl = true,
			ToLinux = path => "/mnt/c/Users/me/.unity/bin/unity.exe",
		};
		Assert.Equal("/mnt/c/Users/me/.unity/bin/unity.exe", locator.Resolve());
	}

	[LinuxFact]
	public void WslTranslatesPathArguments()
	{
		using var temp = new TempDir();
		var project = temp.Sub("Proj");
		Directory.CreateDirectory(project);
		var wsl = new Wsl((direction, path) => direction == "-w" ? "W:" + path.Replace('/', '\\') : null);

		var translated = wsl.TranslateArgs(["test", "Proj", "--output", "/tmp/r.xml", "--log-file=/tmp/l.log", "--filter", "/not/a/path?", "--", "--project-path", "/x"], temp.Path);
		Assert.Equal("W:" + project.Replace('/', '\\'), translated[1]);
		Assert.Equal(@"W:\tmp\r.xml", translated[3]);
		Assert.Equal(@"--log-file=W:\tmp\l.log", translated[4]);
		Assert.Equal("/not/a/path?", translated[6]);
		Assert.Equal("/x", translated[^1]);

		Assert.Equal(["open", "My Game"], wsl.TranslateArgs(["open", "My Game"], temp.Path));
		Assert.Equal(["cmd", "x", "--project-path", @"C:\p"], wsl.TranslateArgs(["cmd", "x", "--project-path", @"C:\p"], temp.Path));
		Assert.Equal(["cmd", "x", "--project-path", @"W:\p"], wsl.TranslateArgs(["cmd", "x", "--project-path", "/p"], temp.Path));
	}

	[Theory]
	[InlineData(@"C:\x", true)]
	[InlineData("c:/x", true)]
	[InlineData(@"\\server\share", true)]
	[InlineData("/mnt/c", false)]
	[InlineData("C:", false)]
	public void DetectsWindowsAbsolutePaths(string path, bool expected) => Assert.Equal(expected, Paths.IsWindowsAbsolute(path));

	[Fact]
	public void KeysNormaliseSlashesAndCase()
	{
		Assert.Equal("c:/games/proj", Paths.Key(@"C:\Games\Proj\"));
		Assert.Equal(OperatingSystem.IsWindows() ? "/users/me/proj" : "/Users/Me/Proj", Paths.Key("/Users/Me/Proj/"));
	}
}
