using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OptimizedUnity.Proc;

internal sealed record ChildResult(string Stdout, string Stderr, int Code, bool TimedOut = false, bool Missing = false)
{
	public static ChildResult NotFound(string path) => new("", $"ERROR: unity CLI not found at {path}\n", ExitCodes.NotFound, Missing: true);
}

internal sealed class ChildOptions
{
	public string? Cwd { get; init; }
	public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
	public int? TimeoutMs { get; init; }
	/// <summary>Inherit stdio for live output instead of capturing.</summary>
	public bool Inherit { get; init; }
}

internal static class ExitCodes
{
	public const int Success = 0;
	public const int BadArgs = 2;
	public const int CommandFailed = 6;
	public const int Timeout = 14;
	public const int NotFound = 127;
	public const int Interrupted = 130;
	public const int Terminated = 143;
}

internal static class ChildProcess
{
	const int KillGraceMs = 2_000;
	const int PipeDrainMs = 2_000;

	/// <summary>Hand the invocation to the real CLI unchanged: execv on Unix, spawn-and-wait elsewhere.</summary>
	public static int Exec(string file, IReadOnlyList<string> args)
	{
		if (!OperatingSystem.IsWindows())
		{
			Posix.Exec(file, args);
			// execv only returns on failure; fall back to a child process.
		}
		return Run(file, args, new ChildOptions { Inherit = true }).Code;
	}

	public static ChildResult Run(string file, IReadOnlyList<string> args, ChildOptions options)
	{
		var info = new ProcessStartInfo(file)
		{
			UseShellExecute = false,
			RedirectStandardOutput = !options.Inherit,
			RedirectStandardError = !options.Inherit,
			// A closed stdin makes prompts fail fast instead of hanging unseen.
			RedirectStandardInput = !options.Inherit,
			WorkingDirectory = options.Cwd ?? "",
		};
		if (!options.Inherit)
		{
			info.StandardOutputEncoding = Encoding.UTF8;
			info.StandardErrorEncoding = Encoding.UTF8;
		}
		foreach (var arg in args) info.ArgumentList.Add(arg);
		foreach (var (key, value) in options.Environment)
		{
			if (value is null) info.Environment.Remove(key);
			else info.Environment[key] = value;
		}

		using var process = new Process { StartInfo = info };
		try
		{
			if (!process.Start()) return ChildResult.NotFound(file);
		}
		catch (Win32Exception)
		{
			return ChildResult.NotFound(file);
		}

		if (!options.Inherit) process.StandardInput.Close();
		var stdout = new StringBuilder();
		var stderr = new StringBuilder();
		var readers = options.Inherit
			? []
			: new[] { Capture(process.StandardOutput, stdout), Capture(process.StandardError, stderr) };

		int? received = null;
		using var interrupt = Forward(PosixSignal.SIGINT, process, () => received ??= Posix.SIGINT);
		using var terminate = Forward(PosixSignal.SIGTERM, process, () => received ??= Posix.SIGTERM);

		var timedOut = false;
		if (options.TimeoutMs is { } timeout && !process.WaitForExit(timeout))
		{
			timedOut = true;
			Stop(process);
		}
		process.WaitForExit();
		// A detached grandchild (e.g. a launched Editor) can hold the pipes open; don't wait on it.
		Task.WaitAll(readers, PipeDrainMs);

		var code = received switch
		{
			Posix.SIGINT => ExitCodes.Interrupted,
			Posix.SIGTERM => ExitCodes.Terminated,
			_ => timedOut ? ExitCodes.Timeout : process.ExitCode,
		};
		return new ChildResult(Snapshot(stdout), Snapshot(stderr), code, timedOut);
	}

	static async Task Capture(StreamReader reader, StringBuilder sink)
	{
		var buffer = new char[16 * 1024];
		int read;
		while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
			lock (sink) sink.Append(buffer, 0, read);
	}

	static string Snapshot(StringBuilder sink)
	{
		lock (sink) return sink.ToString();
	}

	/// <summary>Pass a signal to the child and let it decide how to exit.</summary>
	static PosixSignalRegistration? Forward(PosixSignal signal, Process process, Action onSignal)
	{
		try
		{
			return PosixSignalRegistration.Create(signal, context =>
			{
				context.Cancel = true;
				onSignal();
				if (OperatingSystem.IsWindows()) return;
				try
				{
					Posix.Signal(process.Id, signal == PosixSignal.SIGINT ? Posix.SIGINT : Posix.SIGTERM);
				}
				catch (InvalidOperationException)
				{
					// Already exited.
				}
			});
		}
		catch (PlatformNotSupportedException)
		{
			return null;
		}
	}

	/// <summary>SIGTERM, then SIGKILL of the whole tree after a grace period.</summary>
	static void Stop(Process process)
	{
		try
		{
			if (!OperatingSystem.IsWindows()) Posix.Signal(process.Id, Posix.SIGTERM);
			if (OperatingSystem.IsWindows() || !process.WaitForExit(KillGraceMs)) process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// Already exited.
		}
	}
}
