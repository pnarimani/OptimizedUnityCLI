namespace OptimizedUnity.Host;

/// <summary>Find the real `unity` CLI without ever resolving to this shim.</summary>
internal sealed class CliLocator
{
	public required Func<string, string?> GetEnv { get; init; }
	public required string Home { get; init; }
	public string? SelfPath { get; init; }
	public bool IsWindows { get; init; }
	public bool IsWsl { get; init; }
	public Func<string, bool> FileExists { get; init; } = File.Exists;
	public Func<string, string> RealPath { get; init; } = Paths.RealPath;
	/// <summary>Windows path → WSL path, under WSL only.</summary>
	public Func<string, string?>? ToLinux { get; init; }

	public static CliLocator ForCurrentProcess(Wsl? wsl) => new()
	{
		GetEnv = Environment.GetEnvironmentVariable,
		Home = Paths.Home(),
		SelfPath = Environment.ProcessPath,
		IsWindows = OperatingSystem.IsWindows(),
		IsWsl = wsl is not null,
		ToLinux = wsl is null ? null : wsl.ToLinux,
	};

	public string? Resolve()
	{
		var configured = GetEnv("UNITY_CLI_PATH")?.Trim();
		if (!string.IsNullOrEmpty(configured))
		{
			if (IsWsl && Paths.IsWindowsAbsolute(configured)) configured = ToLinux?.Invoke(configured) ?? configured;
			if (!IsSelf(configured)) return configured;
		}

		if (!IsWsl)
		{
			var installed = Path.Combine(Home, ".unity", "bin", IsWindows ? "unity.exe" : "unity");
			if (FileExists(installed) && !IsSelf(installed)) return installed;
		}

		return SearchPath();
	}

	string? SearchPath()
	{
		string[] names = IsWsl ? ["unity.exe"] : IsWindows ? ["unity.exe", "unity.cmd", "unity.bat"] : ["unity"];
		var path = GetEnv("PATH") ?? "";
		foreach (var directory in path.Split(IsWindows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries))
		{
			foreach (var name in names)
			{
				var candidate = Path.Combine(directory.Trim('"'), name);
				if (FileExists(candidate) && !IsSelf(candidate)) return candidate;
			}
		}
		return null;
	}

	bool IsSelf(string candidate)
	{
		if (SelfPath is null) return false;
		var comparison = IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		return string.Equals(RealPath(candidate), RealPath(SelfPath), comparison);
	}
}
