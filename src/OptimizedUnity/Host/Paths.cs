using OptimizedUnity.Proc;

namespace OptimizedUnity.Host;

internal static class Paths
{
	/// <summary>Canonical absolute path with every symlink resolved, or the full path if it does not exist.</summary>
	public static string RealPath(string path)
	{
		var full = Path.GetFullPath(path);
		if (!OperatingSystem.IsWindows()) return Posix.RealPath(full) ?? full;
		try
		{
			return new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
		}
		catch (IOException)
		{
			return full;
		}
	}

	public static bool IsWindowsAbsolute(string value) =>
		value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/'
		|| value.StartsWith(@"\\", StringComparison.Ordinal);

	/// <summary>Comparison key for project paths: forward slashes, no trailing slash, case-folded for Windows paths.</summary>
	public static string Key(string path)
	{
		var normalized = path.Trim().Replace('\\', '/');
		if (normalized.Length > 1) normalized = normalized.TrimEnd('/');
		return OperatingSystem.IsWindows() || IsWindowsAbsolute(path) ? normalized.ToLowerInvariant() : normalized;
	}

	public static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
