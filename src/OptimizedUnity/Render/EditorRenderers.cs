using System.Text.Json.Nodes;
using OptimizedUnity.Json;

namespace OptimizedUnity.Render;

internal delegate List<string> NodeRenderer(JsonNode data);

/// <summary>Per-command renderers for the Editor's Pipeline results.</summary>
internal static class EditorRenderers
{
    private static readonly Dictionary<string, string> LevelLetter = new()
    {
        ["log"] = "I", ["info"] = "I", ["debug"] = "D", ["trace"] = "T", ["warning"] = "W", ["warn"] = "W",
        ["error"] = "E", ["fatal"] = "F", ["exception"] = "X", ["assert"] = "A"
    };

    private static readonly HashSet<string> TraceLevels = ["error", "exception", "assert", "fatal"];

    private const int MaxStackFrames = 8;

    /// <summary>Keyed by the Pipeline command name, with the legacy snake_case aliases.</summary>
    private static readonly Dictionary<string, NodeRenderer> ByCommand = new()
    {
        ["scene open"] = _ => ["Opened"],
        ["open_scene"] = _ => ["Opened"],
        ["test run"] = TestResults,
        ["test list"] = TestList,
        ["test status"] = TestResults,
        ["run_tests"] = TestResults,
        ["test_status"] = TestResults,
        ["script eval"] = Execute,
        ["script eval_file"] = Execute,
        ["script run"] = Execute,
        ["execute"] = Execute,
        ["console get"] = Console,
        ["console"] = Console,
        ["package list"] = PackageList,
        ["package_list"] = PackageList,
        ["scene get_hierarchy"] = Hierarchy,
        ["get_scene_hierarchy"] = Hierarchy,
        ["gameobject find"] = Objects,
        ["gameobject get_selection"] = Objects,
        ["find_gameobjects"] = Objects,
        ["get_selection"] = Objects
    };

    public static NodeRenderer? For(string? command)
    {
        return command is not null && ByCommand.TryGetValue(command, out var renderer) ? renderer : null;
    }

    /// <summary>Renderers that also accept a bare array of entries.</summary>
    public static bool AcceptsArray(NodeRenderer renderer)
    {
        return renderer == (NodeRenderer)Console;
    }

    public static List<string> Execute(JsonNode result)
    {
        if (result is not JsonObject) return Renderer.Render(result);
        var lines = new List<string>();
        if (result.Get("output").Truthy()) lines.AddRange(Format.MsgLines(result.Get("output"), ""));
        foreach (var diagnostic in result.Get("diagnostics").Items()) lines.Add($"diag: {Format.Fmt(diagnostic)}");
        if (result.Has("result"))
        {
            var value = result.Get("result");
            lines.AddRange(value.IsScalar() ? [Format.Fmt(value)] : Renderer.Render(value));
        }

        foreach (var key in (string[])["message", "error"])
            if (result.Get(key).Truthy())
                lines.AddRange(Format.MsgLines($"{key}: {Format.Fmt(result.Get(key))}"));
        return lines.Count > 0 ? lines : ["ok"];
    }

    public static List<string> Logs(JsonNode data)
    {
        var entries = data is JsonArray array ? array : data.First("logs", "entries") as JsonArray ?? [];
        var lines = new List<string>();
        // Identical entries anywhere in the response print once, with a repeat count on the first.
        var seen = new Dictionary<string, (int HeadIndex, int Count)>();
        List<string>? previousStack = null;
        // The last entry printed in full; the near-identical ones after it print only what differs.
        (string Level, string Module, string Message, string Trace, string Time)? template = null;

        foreach (var entry in entries)
        {
            var level = Format.Fmt(entry.First("type", "level", "levelName")).ToLowerInvariant();
            var message = Format.Fmt(entry.First("message", "msg"));
            // CLI log entries say which invocation they belong to; without it they all look alike.
            if (entry.Get("context") is JsonObject context && context.Count > 0)
                message += " " + string.Join(" ", context.Select(pair => $"{pair.Key}={Format.Fmt(pair.Value)}"));
            var trace = TraceLevels.Contains(level) ? Format.Fmt(entry.Get("stackTrace")).Trim() : "";

            var key = $"{level}\u0000{message}\u0000{trace}";
            if (seen.TryGetValue(key, out var earlier))
            {
                seen[key] = (earlier.HeadIndex, earlier.Count + 1);
                continue;
            }

            var body = Format.MsgLines(message);
            var time = Format.ShortTime(entry.First("timestampUtc", "timestamp"));
            var module = Format.Fmt(entry.Get("moduleName"));
            if (template is { } t && t.Level == level && t.Module == module && t.Trace == trace && body.Count == 1
                && Variation(t.Message, body[0]) is { } variation)
            {
                seen[key] = (lines.Count, 1);
                lines.Add(time.Length > 0 && time != t.Time ? $"  ~ {time} {variation}" : $"  ~ {variation}");
                continue;
            }

            var letter = LevelLetter.TryGetValue(level, out var known) ? known :
                level.Length > 0 ? level[..1].ToUpperInvariant() : "";
            var head = string.Join(" ", new[] { letter, time, module, body[0] }.Where(part => part.Length > 0));
            template = body.Count == 1 ? (level, module, body[0], trace, time) : null;
            seen[key] = (lines.Count, 1);
            lines.Add(head);
            lines.AddRange(body.Skip(1));
            if (trace.Length == 0) continue;
            // Errors raised from one place in a loop share a stack; print it once per run.
            var stack = CompactStack(trace);
            if (previousStack is not null && stack.SequenceEqual(previousStack)) lines.Add("    (same stack)");
            else lines.AddRange(stack.Select(frame => "    " + frame));
            previousStack = stack;
        }

        foreach (var (headIndex, count) in seen.Values)
            if (count > 1)
                lines[headIndex] += $" ×{count}";

        if (data is JsonObject && data.Get("total").Int() is { } total && total > entries.Count)
            lines.Add($"... {entries.Count} of {total}");
        return lines.Count > 0 ? lines : ["(no logs)"];
    }

    private const int MinSharedText = 24;

    /// <summary>
    /// `…changed part…` when a message repeats the template's start and end (the same error logged
    /// for many values), cut at word boundaries. Null when too little is shared to be worth it.
    /// </summary>
    internal static string? Variation(string template, string message)
    {
        if (template == message) return null;
        var max = Math.Min(template.Length, message.Length);
        var prefix = 0;
        while (prefix < max && template[prefix] == message[prefix]) prefix++;
        while (prefix > 0 && !IsBoundary(template[prefix - 1])) prefix--;
        var suffix = 0;
        while (suffix < max - prefix && template[^(suffix + 1)] == message[^(suffix + 1)]) suffix++;
        while (suffix > 0 && !IsBoundary(template[^suffix])) suffix--;
        if (prefix + suffix < MinSharedText) return null;
        var middle = message[prefix..^suffix];
        if (middle.Trim().Length == 0) return null;
        return $"{(prefix > 0 ? "…" : "")}{middle}{(suffix > 0 ? "…" : "")}";
    }

    private static bool IsBoundary(char c)
    {
        return !char.IsLetterOrDigit(c) && c != '_';
    }

    /// <summary>Drop async/logging plumbing frames, shorten PackageCache paths, cap the depth.</summary>
    public static List<string> CompactStack(string trace)
    {
        return StackFrames.Compact(trace, StackFrames.ConsoleNoise, MaxStackFrames);
    }

    public static List<string> Console(JsonNode data)
    {
        var lines = Logs(data);
        if (data is not JsonObject) return lines;
        var meta = new List<string>();
        if (data.Has("cursor")) meta.Add($"cursor: {Format.Fmt(data.Get("cursor"))}");
        if (data.Has("session")) meta.Add($"session: {Format.Fmt(data.Get("session"))}");
        // Only worth reporting when they are set.
        if (data.Get("dropped").Truthy()) meta.Add($"dropped: {Format.Fmt(data.Get("dropped"))}");
        if (data.Get("reset").Truthy()) meta.Add("reset: true");
        if (meta.Count > 0) lines.Add(string.Join("  ", meta));
        return lines;
    }

    public static List<string> PackageList(JsonNode result)
    {
        var packages = result.Get("packages") as JsonArray ?? [];
        var head = string.Join("  ",
            new[] { Format.Fmt(result.Get("scope")), $"{packages.Count} packages", "(*=direct)" }.Where(x =>
                x.Length > 0));
        var lines = new List<string> { head };
        foreach (var package in packages)
            lines.Add(
                $"{(package.Get("isDirectDependency").Bool() == true ? "*" : " ")}{Format.Fmt(package.Get("name"))} {Format.Fmt(package.Get("version"))}");
        return lines;
    }

    public static List<string> Hierarchy(JsonNode result)
    {
        var lines = new List<string>();
        var head = string.Join("  ",
            new[] { Format.Fmt(result.Get("sceneName")), Format.Fmt(result.Get("scenePath")) }
                .Where(x => x.Length > 0));
        if (head.Length > 0) lines.Add(head + (result.Get("isDirty").Truthy() ? "  dirty" : ""));

        void Walk(JsonNode? nodes, int depth)
        {
            foreach (var node in nodes.Items())
            {
                // Every GameObject has a Transform; listing it is pure noise.
                var components = node.Get("components").Items().Select(Format.Fmt).Where(c => c != "Transform")
                    .ToList();
                var line = new string(' ', depth * 2) + Format.Fmt(node.Get("name"));
                if (node.Get("activeSelf").Bool() == false) line += " (inactive)";
                if (components.Count > 0) line += $"  [{string.Join(", ", components)}]";
                lines.Add(line);
                Walk(node.Get("children"), depth + 1);
            }
        }

        Walk(result.First("roots", "children"), 1);
        return lines.Count > 0 ? lines : ["(empty scene)"];
    }

    private static string ObjRef(JsonNode? value)
    {
        return string.Join("  ",
            new[] { Format.Fmt(value.First("hierarchyPath", "assetPath", "name")), Format.Fmt(value.Get("type")) }
                .Where(x => x.Length > 0));
    }

    public static List<string> Objects(JsonNode result)
    {
        if (result.First("gameObjects", "objects", "results") is not JsonArray rows) return Renderer.Render(result);
        var lines = rows.Select(ObjRef).ToList();
        if (lines.Count > 0 && lines.All(line => line.Length == 0)) return Renderer.Render(result);
        var active = result.Get("active");
        if (active.Truthy() && rows.Count != 1) lines.Insert(0, $"active: {ObjRef(active)}");
        if (result.Get("unresolved") is JsonArray unresolved)
            lines.Add($"unresolved: {string.Join(", ", unresolved.Select(Format.Fmt))}");
        if (result.Get("total").Int() is { } total && total > rows.Count) lines.Add($"... {rows.Count} of {total}");
        return lines.Count > 0 ? lines : ["(none)"];
    }

    /// <summary>Counts plus every non-passing test; passing tests are only counted.</summary>
    public static List<string> TestResults(JsonNode result)
    {
        if (TestReports.FromEditorResult(result) is not { } summary) return Renderer.Render(result);
        var lines = TestReports.Render(summary);
        foreach (var key in (string[])["message", "error"])
            if (result.GetAny(key).Str() is { Length: > 0 } text)
                lines.AddRange(Format.MsgLines($"{key}: {text}"));
        return lines;
    }

    public static List<string> TestList(JsonNode result)
    {
        return TestReports.RenderList(result,
                   text => Output.Spill.Write("test-list", "txt", System.Text.Encoding.UTF8.GetBytes(text)))
               ?? Renderer.Render(result);
    }

    /// <summary>`unity command` listings and `unity list`; the catalog's full form is 100s of KB.</summary>
    public static List<string> CommandCatalog(JsonNode data)
    {
        var rows = data.First("commands", "tools") as JsonArray ?? [];
        var lines = new List<string>();
        foreach (var command in rows)
        {
            var name = Format.Fmt(command.First("command", "name"));
            var description = Format.Fmt(command.Get("description"));
            lines.Add(description.Length > 0 ? $"{name}: {description}" : name);
            // Only worth the tokens when the caller narrowed down to a few commands.
            if (rows.Count > 5) continue;
            foreach (var parameter in command.Get("parameters").Items())
            {
                var defaultValue = parameter.First("defaultValue", "default");
                var line =
                    $"    {Format.Fmt(parameter.Get("name"))}:{Format.Fmt(parameter.Get("type")).ToLowerInvariant()}";
                if (parameter.Get("required").Truthy()) line += "!";
                if (!defaultValue.IsEmpty()) line += $"={Format.Fmt(defaultValue)}";
                if (parameter.Get("description").Truthy()) line += $"  {Format.Fmt(parameter.Get("description"))}";
                lines.Add(line);
            }
        }

        if (data.Get("total").Int() is { } total && total > rows.Count)
            lines.Add($"... {rows.Count} of {total} (use --offset/--limit/--query)");
        if (lines.Count == 0) return ["(none)"];
        lines.Add("(unity command <name> --help shows a command's parameters and usage)");
        return lines;
    }

    /// <summary>Bare `unity command` lists groups, `--tag` lists tags: `groups: animation, asset`, full names on one line.</summary>
    public static List<string> NameListing(JsonNode data, string field, string label, string hint)
    {
        var names = data.Get(field).Items()
            .Select(item => Format.Fmt(item is JsonObject ? item.First(label, "name") : item));
        return [$"{field}: {string.Join(", ", names)}", hint];
    }

    public static List<string> Job(JsonNode data)
    {
        var id = Format.Fmt(data.Get("jobId"));
        var head = string.Join(" ",
            new[]
            {
                "job", id.Length > 8 ? id[..8] : id, Format.Fmt(data.Get("command")), Format.Fmt(data.Get("state"))
            }.Where(x => x.Length > 0));
        var lines = new List<string> { head };
        foreach (var key in (string[])["progress", "error", "errorDetails"])
            if (data.Get(key).Truthy())
                lines.AddRange(Format.MsgLines($"{key}: {Format.Fmt(data.Get(key))}"));
        if (data.Get("result") is { } result)
            lines.AddRange(EnvelopeRenderer.RenderEditor(Format.Fmt(data.Get("command")), result));
        return lines;
    }
}