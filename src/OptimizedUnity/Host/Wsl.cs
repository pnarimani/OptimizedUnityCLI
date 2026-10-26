using OptimizedUnity.Args;
using OptimizedUnity.Proc;

namespace OptimizedUnity.Host;

/// <summary>Drive a Windows Unity CLI from WSL: path translation in both directions.</summary>
internal sealed class Wsl
{
	/// <summary>Flags whose value is a filesystem path the Windows CLI must read or write.</summary>
	static readonly string[] PathFlags =
	[
		"--project-path", "-l", "--log-file", "-e", "--editor-path", "--output", "--junit-output",
		"--coverage-output", "--shard-inventory",
	];
	static readonly HashSet<string> ProjectCommands = ["open", "close", "run", "test", "build"];

	readonly Func<string, string, string?> convert;

	/// <param name="convert">(direction "-w"|"-u", path) → converted path, or null on failure.</param>
	public Wsl(Func<string, string, string?>? convert = null) => this.convert = convert ?? RunWslPath;

	public static bool Detect()
	{
		if (!OperatingSystem.IsLinux()) return false;
		if (Environment.GetEnvironmentVariable("WSL_INTEROP") is { Length: > 0 }
			|| Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is { Length: > 0 }) return true;
		try
		{
			var release = File.ReadAllText("/proc/sys/kernel/osrelease");
			return release.Contains("microsoft", StringComparison.OrdinalIgnoreCase)
				|| release.Contains("wsl", StringComparison.OrdinalIgnoreCase);
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	public string? ToWindows(string linuxPath) => Paths.IsWindowsAbsolute(linuxPath) ? linuxPath : convert("-w", linuxPath);
	public string? ToLinux(string windowsPath) => Paths.IsWindowsAbsolute(windowsPath) ? convert("-u", windowsPath) : windowsPath;

	/// <summary>Rewrite path-valued arguments so the Windows CLI receives Windows paths.</summary>
	public List<string> TranslateArgs(IReadOnlyList<string> args, string cwd)
	{
		var result = new List<string>(args);
		var head = ArgRewriter.Head(args);
		for (var i = 0; i < head.Count; i++)
		{
			var arg = head[i];
			foreach (var flag in PathFlags)
			{
				if (arg == flag && i + 1 < head.Count)
				{
					result[i + 1] = TranslateValue(head[i + 1], cwd, mustExist: false);
					i++;
					break;
				}
				if (flag.StartsWith("--", StringComparison.Ordinal) && arg.StartsWith(flag + "=", StringComparison.Ordinal))
				{
					result[i] = $"{flag}={TranslateValue(arg[(flag.Length + 1)..], cwd, mustExist: false)}";
					break;
				}
			}
		}

		if (ProjectCommands.Contains(ArgRewriter.Command(args) ?? "") && ArgRewriter.PositionalIndex(args) is var position and >= 0)
			result[position] = TranslateValue(result[position], cwd, mustExist: true);
		return result;
	}

	/// <summary>
	/// Absolute Linux paths always need translating; relative ones already resolve through the
	/// Windows view of the WSL cwd. A positional project may be a Hub registry name, so it is
	/// translated (to an absolute path) only when it names an existing directory.
	/// </summary>
	string TranslateValue(string value, string cwd, bool mustExist)
	{
		if (value.Length == 0 || Paths.IsWindowsAbsolute(value)) return value;
		var absolute = Path.GetFullPath(value, cwd);
		var translate = mustExist ? Directory.Exists(absolute) : value.StartsWith('/');
		return translate ? ToWindows(absolute) ?? value : value;
	}

	/// <summary>Windows processes only see variables listed in WSLENV.</summary>
	public static string WslEnv(IEnumerable<string> names)
	{
		var existing = Environment.GetEnvironmentVariable("WSLENV") ?? "";
		var entries = existing.Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
		foreach (var name in names)
			if (!entries.Any(entry => entry.Split('/')[0] == name)) entries.Add(name);
		return string.Join(':', entries);
	}

	/// <summary>Windows %LOCALAPPDATA% as a WSL path.</summary>
	public string? LocalAppData()
	{
		var result = ChildProcess.Run("cmd.exe", ["/d", "/s", "/c", "echo %LOCALAPPDATA%"], new ChildOptions { TimeoutMs = 5_000, Cwd = "/" });
		var value = result.Stdout.Trim();
		return result.Code == 0 && value.Length > 0 && !value.Contains('%') ? ToLinux(value) : null;
	}

	static string? RunWslPath(string direction, string path)
	{
		var result = ChildProcess.Run("wslpath", [direction, path], new ChildOptions { TimeoutMs = 5_000 });
		var converted = result.Stdout.Trim();
		return result.Code == 0 && converted.Length > 0 ? converted : null;
	}
}
