using System.Text;
using System.Text.Json.Nodes;
using OptimizedUnity.Args;
using OptimizedUnity.Features;
using OptimizedUnity.Host;
using OptimizedUnity.Json;
using OptimizedUnity.Output;
using OptimizedUnity.Proc;
using OptimizedUnity.Render;

namespace OptimizedUnity;

internal static class Program
{
	/// <summary>Set on every child so a nested `unity` call reaches the real CLI untouched.</summary>
	const string ActiveVariable = "UNITY_WRAP_ACTIVE";

	static int Main(string[] argv)
	{
		var wsl = Wsl.Detect() ? new Wsl() : null;
		var cli = CliLocator.ForCurrentProcess(wsl).Resolve();
		if (cli is null)
		{
			Console.Error.WriteLine("ERROR: unity CLI not found. Install it (https://unity.com/install.sh) or set UNITY_CLI_PATH.");
			return ExitCodes.NotFound;
		}

		var mode = Environment.GetEnvironmentVariable("UNITY_WRAP")?.Trim().ToLowerInvariant();
		if (mode is "off" or "0" or "false" or "raw" || Environment.GetEnvironmentVariable(ActiveVariable) is { Length: > 0 })
			return ChildProcess.Exec(cli, argv);

		var app = new App(cli, wsl, Environment.CurrentDirectory);
		try
		{
			return app.Run(argv);
		}
		catch (Exception error) when (error is not OutOfMemoryException && !app.Launched)
		{
			// Nothing ran yet, so the real CLI can safely take over.
			Console.Error.WriteLine($"WARN: optimized-unity failed ({error.GetType().Name}: {error.Message}); running the CLI directly.");
			return ChildProcess.Exec(cli, argv);
		}
	}

	sealed class App(string cli, Wsl? wsl, string cwd)
	{
		/// <summary>The caller's argv before WSL translation: local paths for files this process reads.</summary>
		IReadOnlyList<string> localArgs = [];

		/// <summary>Whether a CLI command has started; after that, re-running it is never safe.</summary>
		public bool Launched { get; private set; }

		public int Run(string[] argv)
		{
			localArgs = argv;
            if (GameViewCapture.AppliesTo(argv) && ArgRewriter.HasFlag(argv, "--save_path"))
            {
                return Emit("", "ERROR: --save_path is not supported for capture game_view.\n", ExitCodes.BadArgs, argv);
            }
			var kind = ArgRewriter.Classify(argv);
			if (kind == InvocationKind.Exec) return ChildProcess.Exec(cli, argv);
			if (kind == InvocationKind.ForceClose) return RunForceClose(argv);

			IReadOnlyList<string> args = wsl is null ? argv : wsl.TranslateArgs(argv, cwd);
			return kind switch
			{
				InvocationKind.EditorHelp => RunEditorHelp(args),
				InvocationKind.Help => RunHelp(args),
				InvocationKind.Stream => RunStream(args),
				_ => RunOptimized(args),
			};
		}

		ChildOptions Options(bool inherit = false, int? timeoutMs = null, string? childCwd = null)
		{
			var environment = new Dictionary<string, string?>
			{
				[ActiveVariable] = "1",
				["UNITY_NO_PAGER"] = "1",
				["UNITY_NON_INTERACTIVE"] = "1",
				["UNITY_NO_BANNER"] = "1",
				["NO_COLOR"] = "1",
				["UNITY_FORMAT"] = null,
			};
			if (wsl is not null)
			{
				if (Environment.GetEnvironmentVariable("UNITY_PROJECT_PATH") is { Length: > 0 } project && !Paths.IsWindowsAbsolute(project))
					environment["UNITY_PROJECT_PATH"] = wsl.ToWindows(Path.GetFullPath(project, cwd)) ?? project;
				environment["WSLENV"] = Wsl.WslEnv(environment.Where(pair => pair.Value is not null && pair.Key != "WSLENV").Select(pair => pair.Key));
			}
			return new ChildOptions { Inherit = inherit, TimeoutMs = timeoutMs ?? OperationTimeoutMs(), Environment = environment, Cwd = childCwd };
		}

		/// <summary>Opt-in hard cap; builds and imports can legitimately run for a long time.</summary>
		static int? OperationTimeoutMs() =>
			int.TryParse(Environment.GetEnvironmentVariable("UNITY_OP_TIMEOUT_MS"), out var value) && value > 0 ? value : null;

		ChildResult Invoke(IReadOnlyList<string> args, int? timeoutMs = null, string? childCwd = null)
		{
			Launched = true;
			return ChildProcess.Run(cli, args, Options(timeoutMs: timeoutMs, childCwd: childCwd));
		}

		int RunOptimized(IReadOnlyList<string> args)
		{
			var snapshot = CompilerErrors.AppliesTo(args)
				? CompilerErrors.Snapshot(CompilerErrors.LogPath(args, cwd, () => CompilerErrors.DefaultLogPath(wsl)))
				: null;

			var started = DateTime.UtcNow;
			var result = Invoke(ArgRewriter.WithJson(args));
			if (result.Missing) return Emit("", result.Stderr, result.Code, args);

			var envelope = JsonNode.ParseLenient(result.Stdout);
			string stdout;
			var stderr = result.Stderr.Trim().Length > 0 ? Format.StripAnsi(result.Stderr) : "";
			var code = result.Code;
			bool? success = null;

			if (!envelope.IsEnvelope())
			{
				// Some usage errors are bare text even under --json; pass them through.
				stdout = Format.StripAnsi(result.Stdout);
			}
			else
			{
				try
				{
					if (GameViewCapture.AppliesTo(localArgs))
                    {
                        var capture = GameViewCapture.Render(envelope!, path => wsl is null ? path : wsl.ToLinux(path));
                        return Emit(capture.Stdout, Append(stderr, capture.Stderr), code == 0 ? capture.Code : code, args);
                    }
                    Spill.Blobs(envelope, Label(args));
					stdout = Lines(EnvelopeRenderer.Render(envelope!));
				}
				catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException)
				{
					// A shape the renderers did not expect: the raw envelope is still the answer.
					stdout = result.Stdout;
					stderr = Append(stderr, $"WARN: optimized-unity could not render this result ({error.Message}); showing raw JSON.");
				}
				success = EnvelopeRenderer.CommandSuccess(envelope!);
				if (CommandHelp.EmptyQueryError(args, envelope!) is { } queryError && code == 0)
				{
					stderr = Append(stderr, queryError);
					code = ExitCodes.BadArgs;
				}
				if (code == 0 && success == false) code = ExitCodes.CommandFailed;
			}

			// `unity test` reports only "tests failed" in JSON; the detail is in the report file it wrote.
			if (TestReportPath() is { } report && IsFresh(report, started) && TestReports.FromReport(report) is { } summary)
				stdout += Lines(TestReports.Render(summary, report));

			if (result.TimedOut)
				stderr = Append(stderr, $"ERROR: timed out after {OperationTimeoutMs()}ms (UNITY_OP_TIMEOUT_MS)");
			if (snapshot is not null && (code != 0 || success == false) && CompilerErrors.Read(snapshot) is { } errors)
				stderr = Append(stderr, errors.Format());

			if (stdout.Length == 0 && stderr.Length == 0 && code == 0) stdout = "ok\n";
			return Emit(stdout, stderr, code, args);
		}

		/// <summary>`unity test` writes its NUnit (or JUnit) report to --output, default ./test-results.xml.</summary>
		string? TestReportPath()
		{
			if (ArgRewriter.Command(localArgs) != "test") return null;
			var output = ArgRewriter.FlagValue(localArgs, "--output") ?? "test-results.xml";
			return Path.GetFullPath(output, cwd);
		}

		/// <summary>Ignore a report left by an earlier run.</summary>
		static bool IsFresh(string path, DateTime started) =>
			File.Exists(path) && File.GetLastWriteTimeUtc(path) >= started.AddSeconds(-2);

		int RunHelp(IReadOnlyList<string> args)
		{
			var helpArgs = ArgRewriter.InsertBeforeDelimiter(ArgRewriter.StripFormat(args), "--no-banner", "--color", "never");
			var result = Invoke(helpArgs);
			return Emit(HelpText.Compact(result.Stdout), Format.StripAnsi(result.Stderr), result.Code, args);
		}

		int RunEditorHelp(IReadOnlyList<string> args)
		{
			var words = ArgRewriter.EditorCommandWords(args);
			var requested = string.Join(' ', words);
			var target = ArgRewriter.TargetArgs(args);

			var (envelope, result) = Query(requested, target);
			if (envelope is null || envelope.Get("success").Bool() != true)
				return RenderedOrRaw(envelope, result, args);

			var data = Pruner.Prune(envelope.Get("data"));
			var matched = CommandHelp.Match(data, words);
			if (matched is null && words.Count > 1)
			{
				// A phrase query does not always find multi-word names; search the group instead.
				var (groupEnvelope, _) = Query(words[0], target);
				if (groupEnvelope is not null && groupEnvelope.Get("success").Bool() == true)
				{
					var groupData = Pruner.Prune(groupEnvelope.Get("data"));
					var groupMatch = CommandHelp.Match(groupData, words);
					if (groupMatch is not null || !CommandHelp.Rows(data).Any())
					{
						data = groupData;
						matched = groupMatch;
					}
				}
			}

			var (lines, found) = CommandHelp.Render(data, requested, matched);
			return Emit(Lines(lines), "", found ? ExitCodes.Success : ExitCodes.BadArgs, args);
		}

		(JsonNode? Envelope, ChildResult Result) Query(string term, List<string> target)
		{
			var result = Invoke(["command", "--query", term, "--detail", "full", "--json", .. target]);
			var envelope = JsonNode.ParseLenient(result.Stdout);
			return (envelope.IsEnvelope() ? envelope : null, result);
		}

		int RenderedOrRaw(JsonNode? envelope, ChildResult result, IReadOnlyList<string> args)
		{
			if (result.Missing) return Emit("", result.Stderr, result.Code, args);
			var stdout = envelope is null ? Format.StripAnsi(result.Stdout) : Lines(EnvelopeRenderer.Render(envelope));
			var code = result.Code == 0 ? ExitCodes.CommandFailed : result.Code;
			return Emit(stdout, Format.StripAnsi(result.Stderr), code, args);
		}

		int RunStream(IReadOnlyList<string> args)
		{
			Launched = true;
			var streamArgs = ArgRewriter.InsertBeforeDelimiter(ArgRewriter.StripFormat(args), "--color", "never");
			return ChildProcess.Run(cli, streamArgs, Options(inherit: true)).Code;
		}

		int RunForceClose(string[] argv)
		{
			var position = ArgRewriter.PositionalIndex(argv);
			var localProject = Path.GetFullPath(argv[position], cwd);
			// A Hub registry name rather than a directory: process discovery cannot match it safely.
			if (!Directory.Exists(localProject))
				return RunOptimized(wsl is null ? argv : wsl.TranslateArgs(argv, cwd));

			var cliProject = wsl?.ToWindows(localProject) ?? localProject;
			var close = new ForceClose
			{
				Cli = (args, childCwd, timeoutMs) => Invoke(args, timeoutMs, childCwd),
				Processes = new NativeProcessControl(wsl is not null),
				ToLocal = path => wsl is null ? path : wsl.ToLinux(path),
			};
			var result = close.Run(localProject, cliProject, argv);
			return Emit(result.Stdout, result.Stderr, result.Code, argv);
		}

		static string Label(IReadOnlyList<string> args)
		{
			var head = ArgRewriter.Head(args);
			var index = ArgRewriter.CommandIndex(head);
			if (index < 0) return "unity";
			var words = head.Skip(index).TakeWhile(arg => !arg.StartsWith('-')).Take(3);
			return string.Join('-', words);
		}

		/// <summary>Commands whose output is a document read top-down.</summary>
		static readonly HashSet<string> DocumentCommands = ["changelog", "skill", "docs", "help"];

		/// <summary>Commands that stream the whole Editor log to stderr under `--json`.</summary>
		static readonly HashSet<string> LogStreamingCommands = ["run", "test", "build"];
		const int LogTailLines = 200;
		const int LogTailBytes = 16 * 1_024;

		static int Emit(string stdout, string stderr, int code, IReadOnlyList<string> args)
		{
			var label = Label(args);
			var command = ArgRewriter.Command(args) ?? "";
			// Documents lead with what matters (newest release notes, the guide's overview).
			var document = DocumentCommands.Contains(command);
			if (stdout.Length > 0) Write(Console.OpenStandardOutput(), OutputBudget.Compact(stdout, label, keepHead: document));
			if (stderr.Length > 0)
			{
				// The streamed Editor log is saved in full; its tail (and any compiler errors) is what matters.
				var log = LogStreamingCommands.Contains(command);
				var text = OutputBudget.Compact(EnsureNewline(stderr), label + "-stderr",
					log ? LogTailLines : null, log ? LogTailBytes : null);
				Write(Console.OpenStandardError(), text);
			}
			return code;
		}

		static void Write(Stream stream, string text)
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			try
			{
				stream.Write(bytes);
				stream.Flush();
			}
			catch (IOException)
			{
				// The reader closed the pipe.
			}
		}

		static string Lines(List<string> lines) => lines.Count == 0 ? "" : string.Join('\n', lines) + "\n";
		static string EnsureNewline(string text) => text.EndsWith('\n') ? text : text + "\n";
		static string Append(string existing, string addition) =>
			existing.Trim().Length == 0 ? addition + "\n" : $"{existing.TrimEnd()}\n{addition}\n";
	}
}
