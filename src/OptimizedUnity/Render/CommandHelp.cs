using System.Text.Json.Nodes;
using OptimizedUnity.Json;

namespace OptimizedUnity.Render;

/// <summary>Compact usage for one Editor command, from `command --query &lt;name&gt; --detail full`.</summary>
internal static class CommandHelp
{
	static string NameOf(JsonNode? command) => Format.Fmt(command.First("command", "name"));

	/// <summary>The longest leading run of `words` that names a catalog command exactly.</summary>
	public static string? Match(JsonNode? data, IReadOnlyList<string> words)
	{
		var names = new HashSet<string>(Rows(data).Select(NameOf));
		for (var count = words.Count; count > 0; count--)
		{
			var candidate = string.Join(' ', words.Take(count));
			if (names.Contains(candidate)) return candidate;
		}
		return null;
	}

	public static IEnumerable<JsonNode?> Rows(JsonNode? data) => data.First("commands", "tools").Items();

	/// <returns>The rendered lines, and whether the lookup found the command.</returns>
	public static (List<string> Lines, bool Found) Render(JsonNode? data, string requested, string? matched)
	{
		var rows = Rows(data).ToList();
		if (rows.Count == 0) return ([$"no command matching '{requested}'"], false);
		if (matched is null)
		{
			var lines = new List<string> { $"no command named '{requested}'; closest matches:" };
			lines.AddRange(rows.Select(c => $"  {NameOf(c)}: {Format.Fmt(c.Get("description"))}"));
			return (lines, false);
		}

		var output = new List<string>();
		foreach (var command in rows.Where(c => NameOf(c) == matched))
		{
			var name = NameOf(command);
			var description = name == "capture game_view" ? "Save the inline PNG to Temp/pipeline-screenshots/{timestamp}.png and return its project-relative path." : Format.Fmt(command.Get("description"));
			output.Add(description.Length > 0 ? $"{name}: {description}" : name);

			var parameters = command.Get("parameters").Items().Where(p => name != "capture game_view" || p.Get("name").Str() != "save_path").ToList();
			var required = parameters.Where(p => p.Get("required").Truthy()).ToList();
			var optional = parameters.Where(p => !p.Get("required").Truthy()).ToList();
			var usage = new List<string> { $"usage: unity cmd {name}" };
			usage.AddRange(required.Select(p => $"--{Format.Fmt(p.Get("name"))} <{TypeOf(p)}>"));
			usage.AddRange(optional.Select(p => $"[--{Format.Fmt(p.Get("name"))} <{TypeOf(p)}>]"));
			output.Add(string.Join(' ', usage));

			foreach (var parameter in required.Concat(optional))
			{
				var defaultValue = parameter.First("defaultValue", "default");
				var requirement = parameter.Get("required").Truthy()
					? "required"
					: defaultValue.IsEmpty() ? "optional" : $"default={Format.Fmt(defaultValue)}";
				output.Add(string.Join("  ", new[]
				{
					$"  --{Format.Fmt(parameter.Get("name"))}", TypeOf(parameter), requirement, Format.Fmt(parameter.Get("description")),
				}.Where(part => part.Length > 0)));
			}

			var notes = new List<string>();
			if (command.Get("package").Truthy()) notes.Add(Format.Fmt(command.Get("package")));
			if (command.Get("mainThreadRequired").Truthy()) notes.Add("main thread required");
			if (command.Get("runtimeOnly").Truthy()) notes.Add("runtime (player) only");
			if (notes.Count > 0) output.Add($"notes: {string.Join(", ", notes)}");
		}
		return (output, true);
	}

	static string TypeOf(JsonNode? parameter) => Format.Fmt(parameter.Get("type")).ToLowerInvariant();

	/// <summary>
	/// Explain a successful, non-empty `command --query` search that found nothing. A blank query
	/// keeps the CLI's list behaviour.
	/// </summary>
	public static string? EmptyQueryError(IReadOnlyList<string> args, JsonNode envelope)
	{
		if (!Args.ArgRewriter.IsEditorCommand(args) || Args.ArgRewriter.EditorCommandWords(args).Count > 0) return null;
		var query = Args.ArgRewriter.FlagValue(args, "--query");
		if (string.IsNullOrWhiteSpace(query) || query.StartsWith('-') || !envelope.Get("success").Truthy()) return null;
		if (envelope.Get("data").First("commands", "tools") is not JsonArray { Count: 0 }) return null;

		return string.Join('\n',
			$"ERROR: no Unity commands matched query \"{query}\".",
			"Search uses a case-insensitive substring match against command names, descriptions, and tags; it is not fuzzy and a multi-word query must appear as one contiguous phrase.",
			"Try a shorter single term (for example, `scene`) or run `unity command` to list all command groups.");
	}
}
