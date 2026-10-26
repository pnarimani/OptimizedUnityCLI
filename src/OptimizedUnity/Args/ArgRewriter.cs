namespace OptimizedUnity.Args;

internal enum InvocationKind
{
	/// <summary>Replace this process with the real CLI, untouched.</summary>
	Exec,
	/// <summary>`cmd &lt;name&gt; --help`: compact usage from the live command schema.</summary>
	EditorHelp,
	/// <summary>Any other help request: human help with repeated global options removed.</summary>
	Help,
	/// <summary>`--follow`/`--watch`: live output, no JSON.</summary>
	Stream,
	/// <summary>`close &lt;project&gt; --force` with confirmed process exit.</summary>
	ForceClose,
	/// <summary>JSON envelope rendered as compact text.</summary>
	Optimized,
}

/// <summary>Argument parsing and rewriting. Nothing after a `--` delimiter is ever inspected.</summary>
internal static class ArgRewriter
{
	const string Delimiter = "--";

	static readonly HashSet<string> HelpFlags = ["-h", "--help"];
	static readonly HashSet<string> StreamFlags = ["-f", "--follow", "-w", "--watch"];
	static readonly HashSet<string> CommandTokens = ["command", "cmd"];
	static readonly HashSet<string> ExecCommands = ["shell", "completion"];
	static readonly HashSet<string> VersionFlags = ["-V", "--version"];
	/// <summary>Root options that consume the following token.</summary>
	static readonly HashSet<string> ValuedGlobals = ["--format", "--color", "--proxy"];
	/// <summary>Flags that select the Editor `cmd` talks to; carried into helper lookups.</summary>
	static readonly string[] TargetFlags = ["--project-path", "--runtime", "--runtime-path", "--port", "--pid", "--instance"];

	/// <summary>Index of the first `--`, or the argument count when there is none.</summary>
	static int DelimiterIndex(IReadOnlyList<string> args)
	{
		for (var i = 0; i < args.Count; i++)
			if (args[i] == Delimiter) return i;
		return args.Count;
	}

	/// <summary>Arguments before the first `--` (exclusive).</summary>
	public static List<string> Head(IReadOnlyList<string> args) => [.. args.Take(DelimiterIndex(args))];

	/// <summary>Arguments from the first `--` (inclusive), or empty.</summary>
	public static List<string> Tail(IReadOnlyList<string> args) => [.. args.Skip(DelimiterIndex(args))];

	/// <summary>Index of the subcommand token, skipping root options and their values.</summary>
	public static int CommandIndex(IReadOnlyList<string> head)
	{
		for (var i = 0; i < head.Count; i++)
		{
			var arg = head[i];
			if (arg == Delimiter) return -1;
			if (!arg.StartsWith('-')) return i;
			if (ValuedGlobals.Contains(arg)) i++;
		}
		return -1;
	}

	public static string? Command(IReadOnlyList<string> args)
	{
		var head = Head(args);
		var index = CommandIndex(head);
		return index < 0 ? null : head[index];
	}

	/// <summary>Drop caller output-format flags before the delimiter; the wrapper owns the format.</summary>
	public static List<string> StripFormat(IReadOnlyList<string> args)
	{
		var result = new List<string>(args.Count + 1);
		for (var i = 0; i < args.Count; i++)
		{
			var arg = args[i];
			if (arg == Delimiter)
			{
				result.AddRange(args.Skip(i));
				return result;
			}
			if (arg == "--json") continue;
			if (arg == "--format")
			{
				i++;
				continue;
			}
			if (arg.StartsWith("--format=", StringComparison.Ordinal)) continue;
			result.Add(arg);
		}
		return result;
	}

	/// <summary>Insert flags just before the delimiter (or at the end).</summary>
	public static List<string> InsertBeforeDelimiter(IReadOnlyList<string> args, params string[] flags)
	{
		var result = new List<string>(args);
		result.InsertRange(DelimiterIndex(args), flags);
		return result;
	}

	public static List<string> WithJson(IReadOnlyList<string> args) => InsertBeforeDelimiter(StripFormat(args), "--json");

	public static bool HasFlag(IReadOnlyList<string> args, string flag)
	{
		foreach (var arg in Head(args))
			if (arg == flag || arg.StartsWith(flag + "=", StringComparison.Ordinal)) return true;
		return false;
	}

	/// <summary>Value of `--flag v` or `--flag=v` before the delimiter.</summary>
	public static string? FlagValue(IReadOnlyList<string> args, params string[] names)
	{
		var head = Head(args);
		for (var i = 0; i < head.Count; i++)
		{
			foreach (var name in names)
			{
				if (head[i] == name) return i + 1 < head.Count ? head[i + 1] : null;
				if (head[i].StartsWith(name + "=", StringComparison.Ordinal)) return head[i][(name.Length + 1)..];
			}
		}
		return null;
	}

	/// <summary>Positional argument directly after the subcommand, e.g. `close &lt;project&gt;`.</summary>
	public static int PositionalIndex(IReadOnlyList<string> args)
	{
		var head = Head(args);
		var index = CommandIndex(head);
		if (index < 0 || index + 1 >= head.Count) return -1;
		return head[index + 1].StartsWith('-') ? -1 : index + 1;
	}

	/// <summary>Editor command words after `cmd`: `cmd console get --tail 3` → [console, get].</summary>
	public static List<string> EditorCommandWords(IReadOnlyList<string> args)
	{
		var head = Head(args);
		var index = CommandIndex(head);
		var words = new List<string>();
		if (index < 0 || !CommandTokens.Contains(head[index])) return words;
		for (var i = index + 1; i < head.Count && !head[i].StartsWith('-'); i++) words.Add(head[i]);
		return words;
	}

	public static bool IsEditorCommand(IReadOnlyList<string> args) => CommandTokens.Contains(Command(args) ?? "");

	/// <summary>Collect Editor-targeting flags (with values) to reuse on helper calls.</summary>
	public static List<string> TargetArgs(IReadOnlyList<string> args)
	{
		var head = Head(args);
		var result = new List<string>();
		for (var i = 0; i < head.Count; i++)
		{
			var arg = head[i];
			var flag = TargetFlags.FirstOrDefault(candidate => arg == candidate || arg.StartsWith(candidate + "=", StringComparison.Ordinal));
			if (flag is null) continue;
			result.Add(arg);
			if (arg == flag && i + 1 < head.Count) result.Add(head[++i]);
		}
		return result;
	}

	public static InvocationKind Classify(IReadOnlyList<string> args)
	{
		var head = Head(args);
		var commandIndex = CommandIndex(head);
		var command = commandIndex < 0 ? null : head[commandIndex];

		if (command is not null && ExecCommands.Contains(command)) return InvocationKind.Exec;
		if (command is null && head.Any(VersionFlags.Contains)) return InvocationKind.Exec;
		// Secrets read from stdin need the caller's real stdin.
		if (head.Any(arg => arg.StartsWith("--", StringComparison.Ordinal) && arg.EndsWith("-stdin", StringComparison.Ordinal)))
			return InvocationKind.Exec;

		// Bare `unity` (with only root options) prints usage.
		var help = command is null || command == "help" || head.Any(HelpFlags.Contains);
		if (help)
			return command is not null && CommandTokens.Contains(command) && EditorCommandWords(args).Count > 0
				? InvocationKind.EditorHelp
				: InvocationKind.Help;

		if (command == "watch" || head.Any(StreamFlags.Contains)) return InvocationKind.Stream;
		if (command == "close" && HasFlag(args, "--force") && PositionalIndex(args) >= 0) return InvocationKind.ForceClose;
		return InvocationKind.Optimized;
	}
}
