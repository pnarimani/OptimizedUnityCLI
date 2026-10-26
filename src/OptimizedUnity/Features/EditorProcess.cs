using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimizedUnity.Json;
using OptimizedUnity.Proc;

namespace OptimizedUnity.Features;

internal sealed record EditorProcessInfo(int Pid, string Project, string Executable, string Started);

internal interface IProcessControl
{
	/// <summary>Identity of a live Unity Editor process; null when the process is gone.</summary>
	EditorProcessInfo? Inspect(int pid);
	void Signal(int pid, bool kill);
}

internal sealed class ProcessControlException(string message) : Exception(message);

/// <summary>Native fallback for a CLI quit request blocked by the Editor's main thread.</summary>
internal sealed partial class NativeProcessControl(bool wsl) : IProcessControl
{
	const int CommandTimeoutMs = 5_000;
	readonly bool windows = wsl || OperatingSystem.IsWindows();

	[GeneratedRegex(@"(?:^|\s)-projectpath(?:\s+|=)", RegexOptions.IgnoreCase)]
	private static partial Regex ProjectPathFlag();

	[GeneratedRegex(@"\s+(?=-{1,2}[A-Za-z])")]
	private static partial Regex NextOption();

	/// <summary>ps rebuilds argv without quoting whitespace; the project ends at the next Unity option.</summary>
	public static string? ProjectFromCommandLine(string command)
	{
		var match = ProjectPathFlag().Match(command);
		if (!match.Success) return null;
		var tail = command[(match.Index + match.Length)..].TrimStart();
		if (tail.Length == 0) return null;
		if (tail[0] is '"' or '\'')
		{
			var end = tail.IndexOf(tail[0], 1);
			return end > 1 ? tail[1..end] : null;
		}
		var project = NextOption().Split(tail, 2)[0].Trim();
		return project.Length > 0 ? project : null;
	}

	public EditorProcessInfo? Inspect(int pid)
	{
		if (pid <= 0) throw new ProcessControlException("Refusing an invalid Editor PID");
		string? executable, started, project;
		if (windows)
		{
			var powershell = wsl ? "powershell.exe" : "powershell";
			var script = $"$ErrorActionPreference='Stop'; $p=Get-CimInstance Win32_Process -Filter 'ProcessId = {pid}'; " +
				"if ($null -ne $p) { @{ executable=$p.ExecutablePath; started=$p.CreationDate.ToUniversalTime().ToString('o'); commandLine=$p.CommandLine } | ConvertTo-Json -Compress }";
			var output = Run(powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script], pid);
			if (output is null) return null;
			var record = JsonNode.ParseLenient(output);
			executable = record.Get("executable").Str();
			started = record.Get("started").Str();
			project = ProjectFromCommandLine(record.Get("commandLine").Str() ?? "");
		}
		else if (OperatingSystem.IsLinux())
		{
			try
			{
				var argv = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0');
				var stat = File.ReadAllText($"/proc/{pid}/stat");
				executable = new FileInfo($"/proc/{pid}/exe").LinkTarget;
				var index = Array.FindIndex(argv, arg => arg.Equals("-projectpath", StringComparison.OrdinalIgnoreCase));
				project = index >= 0 && index + 1 < argv.Length ? argv[index + 1] : null;
				// Field 22 (starttime), counted after the parenthesised command name.
				started = stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19];
			}
			catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
			{
				return null;
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException)
			{
				throw Unverifiable(pid);
			}
		}
		else
		{
			executable = Run("ps", ["-p", $"{pid}", "-o", "comm="], pid);
			if (executable is null) return null;
			started = Run("ps", ["-p", $"{pid}", "-o", "lstart="], pid);
			project = ProjectFromCommandLine(Run("ps", ["-p", $"{pid}", "-ww", "-o", "args="], pid) ?? "");
		}

		var name = windows
			? (executable ?? "").Replace('\\', '/').Split('/')[^1].ToLowerInvariant()
			: Path.GetFileName(executable ?? "");
		if (name != (windows ? "unity.exe" : "Unity") || project is null || string.IsNullOrEmpty(started))
			throw new ProcessControlException($"PID {pid} could not be verified as a Unity Editor for the project; refusing to terminate it.");
		return new EditorProcessInfo(pid, project, executable!, started!);
	}

	public void Signal(int pid, bool kill)
	{
		if (pid <= 0) throw new ProcessControlException("Refusing an invalid Editor PID");
		if (wsl)
		{
			if (ChildProcess.Run("taskkill.exe", ["/PID", $"{pid}", "/F"], new ChildOptions { TimeoutMs = CommandTimeoutMs, Cwd = "/" }).Code is not (0 or 128))
				throw new ProcessControlException($"OS termination failed for Editor PID {pid}. Check process-control permissions.");
			return;
		}
		if (OperatingSystem.IsWindows())
		{
			try
			{
				using var process = System.Diagnostics.Process.GetProcessById(pid);
				process.Kill();
			}
			catch (ArgumentException)
			{
				// Already gone.
			}
			return;
		}
		if (!Posix.Signal(pid, kill ? Posix.SIGKILL : Posix.SIGTERM))
			throw new ProcessControlException($"OS termination failed for Editor PID {pid}. Check process-control permissions.");
	}

	/// <summary>Trimmed stdout; null when the process does not exist (exit 1, no output).</summary>
	static string? Run(string file, string[] args, int pid)
	{
		var result = ChildProcess.Run(file, args, new ChildOptions { TimeoutMs = CommandTimeoutMs });
		var output = result.Stdout.Trim();
		if (result.Code == 0) return output.Length > 0 ? output : null;
		if (result.Code == 1 && output.Length == 0) return null;
		// Never include command lines: Hub-launched Editors can carry authentication tokens.
		throw Unverifiable(pid);
	}

	static ProcessControlException Unverifiable(int pid) =>
		new($"OS inspection failed for Editor PID {pid}; cannot safely terminate it.");
}
