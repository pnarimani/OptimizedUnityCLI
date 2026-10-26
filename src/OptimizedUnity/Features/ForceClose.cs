using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using OptimizedUnity.Args;
using OptimizedUnity.Host;
using OptimizedUnity.Json;
using OptimizedUnity.Proc;

namespace OptimizedUnity.Features;

internal sealed class LifecycleException(string message, int code = ExitCodes.CommandFailed) : Exception(message)
{
	public int Code { get; } = code;
}

/// <summary>Run one CLI invocation: (args, cwd, timeoutMs) → result.</summary>
internal delegate ChildResult CliRunner(IReadOnlyList<string> args, string? cwd, int timeoutMs);

/// <summary>
/// `close &lt;project&gt; --force` that confirms the Editor process actually exited, falls back to
/// verified OS termination when the CLI's quit request hangs, then removes the project's Temp so
/// the next open starts clean. Never infers exit from a missing Pipeline endpoint.
/// </summary>
internal sealed class ForceClose
{
	const int DefaultDeadlineMs = 30_000;
	const int DiscoveryTimeoutMs = 5_000;
	const int CliCloseTimeoutMs = 12_000;
	const int TermGraceMs = 2_000;
	const int PollMs = 250;

	public required CliRunner Cli { get; init; }
	public required IProcessControl Processes { get; init; }
	public Func<string, string> RealPath { get; init; } = Paths.RealPath;
	/// <summary>Windows→local path translation for instances reported by a Windows CLI (WSL).</summary>
	public Func<string, string?> ToLocal { get; init; } = path => path;
	public Func<long> Now { get; init; } = () => Stopwatch.GetTimestamp() * 1_000 / Stopwatch.Frequency;
	public Action<int> Sleep { get; init; } = Thread.Sleep;
	public Action<string> DeleteDirectory { get; init; } = DeleteWithRetries;

	/// <param name="localProject">The project directory as this host sees it.</param>
	/// <param name="cliProject">The same project as the CLI sees it (a Windows path under WSL).</param>
	public ChildResult Run(string localProject, string cliProject, IReadOnlyList<string> args)
	{
		var deadline = Now() + DeadlineMs(args);
		var stopped = false;
		var nativeTermination = false;
		int Remaining()
		{
			var left = deadline - Now();
			if (left <= 0) throw new LifecycleException("Unity close timed out; process exit was not confirmed and Temp was preserved.");
			return (int)left;
		}

		try
		{
			var project = Key(localProject);
			var pids = ProjectProcessIds(project, Remaining());
			var wasRunning = pids.Count > 0;
			if (wasRunning)
			{
				var identities = pids.Select(Processes.Inspect).ToList();
				if (identities.Any(identity => identity is not null && Key(identity.Project) != project))
					throw new LifecycleException("Editor PID belongs to another project; refusing OS termination. Temp was preserved.");

				var close = Cli(["close", cliProject, "--timeout", "5", "--force", "--json"], null, Math.Min(Remaining(), CliCloseTimeoutMs));
				if (close.Missing) return close;

				if (ProjectProcessIds(project, Remaining()).Count > 0)
				{
					foreach (var identity in identities)
					{
						if (identity is null || !SameProcess(identity, project)) continue;
						Processes.Signal(identity.Pid, kill: false);
						nativeTermination = true;
						Sleep(Math.Min(TermGraceMs, Remaining()));
						if (SameProcess(identity, project)) Processes.Signal(identity.Pid, kill: true);
					}
				}
				while (ProjectProcessIds(project, Remaining()).Count > 0) Sleep(Math.Min(PollMs, Remaining()));
			}

			stopped = true;
			CleanTemp(localProject);
			var message = $"Unity Editor {(wasRunning ? "closed" : "already stopped")}; Temp cleaned.\n" +
				(nativeTermination ? "Native OS termination was required.\n" : "");
			return new ChildResult(message, "", ExitCodes.Success);
		}
		catch (Exception error) when (error is LifecycleException or ProcessControlException)
		{
			var code = error is LifecycleException lifecycle ? lifecycle.Code : ExitCodes.CommandFailed;
			return new ChildResult("", $"ERROR: {(stopped ? "Editor exit confirmed. " : "")}{error.Message}\n", code);
		}
	}

	/// <summary>Honour a longer `--timeout` than the default deadline, with room for the fallback.</summary>
	static int DeadlineMs(IReadOnlyList<string> args)
	{
		var value = ArgRewriter.FlagValue(args, "--timeout");
		return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
			? (int)Math.Max(DefaultDeadlineMs, Math.Min(int.MaxValue / 2.0, seconds * 1_000 + 15_000))
			: DefaultDeadlineMs;
	}

	string Key(string path) => Paths.Key(RealPath(path));

	bool SameProcess(EditorProcessInfo identity, string project)
	{
		var current = Processes.Inspect(identity.Pid);
		return current is not null && current.Started == identity.Started && current.Executable == identity.Executable
			&& Key(current.Project) == project;
	}

	/// <summary>A complete OS process scan is required, even when discovery exits successfully.</summary>
	List<int> ProjectProcessIds(string project, int remainingMs)
	{
		// Run outside any project: beta.13 reports the cwd as a running instance without a PID.
		var result = Cli(["pipeline", "list", "--json"], Path.GetTempPath(), Math.Min(remainingMs, DiscoveryTimeoutMs));
		LifecycleException Fail(string detail) =>
			new($"Cannot confirm Unity process state; Temp was preserved. {detail}",
				result.Missing ? ExitCodes.NotFound : ExitCodes.CommandFailed);

		var envelope = JsonNode.ParseLenient(result.Stdout);
		if (result.Code != 0 || !envelope.IsEnvelope() || envelope.Get("success").Bool() != true)
			throw Fail(FirstError(envelope) ?? (result.Stderr.Trim() is { Length: > 0 } stderr ? stderr : "Process discovery failed."));
		if (envelope.Get("warnings") is JsonArray { Count: > 0 } warnings)
			throw Fail(string.Join("; ", warnings.Select(warning => warning.Str() ?? warning.Compact())));
		if (envelope.Get("data").Get("instances") is not JsonArray instances)
			throw Fail("Process discovery returned an invalid instance list.");

		var pids = new List<int>();
		foreach (var instance in instances)
		{
			var path = instance.Get("projectPath").Str();
			var running = instance.Get("isRunning").Bool();
			if (string.IsNullOrWhiteSpace(path) || running is null)
				throw Fail("Process discovery did not provide a project path and process state for every instance.");
			if (running != true) continue;
			if (instance.Get("pid").Int() is not { } pid || pid <= 0 || pid > int.MaxValue)
				throw Fail("Process discovery reported a running Editor without an OS process ID.");
			if (Key(ToLocal(path) ?? path) == project && !pids.Contains((int)pid)) pids.Add((int)pid);
		}
		return pids;
	}

	static string? FirstError(JsonNode? envelope) =>
		envelope.Get("errors").Items().Select(error => error.Get("message").Str() ?? error.Str()).FirstOrDefault(m => m is not null);

	/// <summary>Remove Temp only when it is a real directory inside the project, never through a link.</summary>
	void CleanTemp(string localProject)
	{
		var root = RealPath(localProject);
		var temp = Path.Combine(root, "Temp");
		var info = new DirectoryInfo(temp);
		if (!info.Exists && !File.Exists(temp) && info.LinkTarget is null) return;
		if (info.LinkTarget is not null || !info.Exists || Paths.Key(RealPath(temp)) != Paths.Key(temp))
			throw new LifecycleException($"Editor is stopped, but Temp cleanup failed at {temp}: Temp must be a directory inside the project, not a symlink or junction");
		try
		{
			DeleteDirectory(temp);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			throw new LifecycleException($"Editor is stopped, but Temp cleanup failed at {temp}: {error.Message}");
		}
	}

	static void DeleteWithRetries(string path)
	{
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				Directory.Delete(path, recursive: true);
				return;
			}
			catch (DirectoryNotFoundException)
			{
				return;
			}
			catch (IOException) when (attempt < 5)
			{
				Thread.Sleep(100);
			}
		}
	}
}
