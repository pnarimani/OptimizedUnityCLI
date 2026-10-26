using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using OptimizedUnity.Json;

namespace OptimizedUnity.Render;

/// <summary>Generic compact rendering: flat objects on one line, nested values indented.</summary>
internal static partial class Renderer
{
	public static List<string> Render(JsonNode? value, int depth = 0)
	{
		var pad = new string(' ', depth * 2);
		if (value is null) return [];
		if (value.IsScalar()) return [pad + Format.Fmt(value)];

		if (value is JsonArray array)
		{
			if (array.Count == 0) return [$"{pad}(empty)"];
			if (array.All(item => item.IsScalar())) return [pad + Format.JoinScalars(array)];
			if (array.All(item => item.IsFlat()))
				return Table(array, pad) ?? [.. array.Select(item => pad + Format.ObjLine((JsonObject)item!))];
			var result = new List<string>();
			foreach (var item in array)
			{
				var lines = Render(item, depth + 1);
				if (lines.Count == 0) continue;
				result.Add($"{pad}- {lines[0].TrimStart()}");
				result.AddRange(lines.Skip(1));
			}
			return result;
		}

		var obj = (JsonObject)value;
		var simple = new JsonObject();
		var nested = new List<KeyValuePair<string, JsonNode?>>();
		foreach (var pair in obj)
		{
			if (pair.Value.IsScalar()) simple[pair.Key] = pair.Value?.DeepClone();
			else nested.Add(pair);
		}

		var output = simple.Count > 0 ? new List<string> { pad + Format.ObjLine(simple) } : [];
		foreach (var (key, item) in nested)
		{
			if (item.IsFlat()) output.Add($"{pad}{key}: {Format.ObjLine((JsonObject)item!)}");
			else if (item is JsonArray list && list.All(entry => entry.IsScalar())) output.Add($"{pad}{key}: {Format.JoinScalars(list)}");
			else
			{
				output.Add($"{pad}{key}:");
				output.AddRange(Render(item, depth + 1));
			}
		}
		return output;
	}

	const int TableMinRows = 8;
	/// <summary>Fields that name a file; the first present in every row groups the table.</summary>
	static readonly string[] PathKeys = ["assetPath", "path", "scenePath"];
	/// <summary>Identifiers that add nothing once each row is addressed by its path.</summary>
	static readonly HashSet<string> PathIdentified = ["guid", "id", "globalId", "fileId", "instanceId"];

	/// <summary>
	/// Long lists of flat rows: hoist columns that never change into one `all:` line, drop ids and
	/// columns that only repeat the path, and group rows by directory under shared parents. Row order is kept.
	/// </summary>
	static List<string>? Table(JsonArray array, string pad)
	{
		if (array.Count < TableMinRows) return null;
		var rows = array.Cast<JsonObject>().ToList();
		var keys = rows.SelectMany(row => row.Select(pair => pair.Key)).Distinct().ToList();

		var pathKey = PathKeys.FirstOrDefault(key => rows.All(row => row.Get(key).Str() is { } path && path.Contains('/')));
		var constant = keys
			.Where(key => key != pathKey && rows.All(row => row.ContainsKey(key)))
			.Where(key => rows.Select(row => Format.Fmt(row[key])).Distinct().Count() == 1)
			.ToList();
		var dropped = new HashSet<string>(constant);
		if (pathKey is not null) dropped.UnionWith(keys.Where(PathIdentified.Contains));
		if (dropped.Count == 0 && pathKey is null) return null;

		var lines = new List<string>();
		if (constant.Count > 0)
			lines.Add($"{pad}all: {string.Join("  ", constant.Select(key => $"{key}: {Format.Fmt(rows[0][key])}"))}");
		string Rest(JsonObject row) => string.Join("  ", row
			.Where(pair => pair.Key != pathKey && !dropped.Contains(pair.Key) && !RepeatsPath(row, pair.Value))
			.Select(pair => $"{pair.Key}: {Format.Fmt(pair.Value)}"));

		// A label that is the file name, or a description that embeds the path.
		bool RepeatsPath(JsonObject row, JsonNode? value)
		{
			if (pathKey is null || value.Str() is not { Length: > 0 } text) return false;
			var path = row[pathKey].Str()!;
			return path.Contains(text, StringComparison.Ordinal) || text.Contains(path, StringComparison.Ordinal);
		}

		if (pathKey is null)
		{
			lines.AddRange(rows.Select(row => pad + Rest(row)));
			return lines;
		}

		var groups = new List<(string Directory, List<JsonObject> Rows)>();
		var index = 0;
		while (index < rows.Count)
		{
			var directory = DirectoryOf(rows[index][pathKey].Str()!);
			var group = new List<JsonObject>();
			while (index < rows.Count && DirectoryOf(rows[index][pathKey].Str()!) == directory) group.Add(rows[index++]);
			groups.Add((directory, group));
		}

		var names = groups.Select(group => group.Rows.Select(row => row[pathKey].Str()![(group.Directory.Length + 1)..]).ToList()).ToList();
		var rests = groups.Select(group => group.Rows.Select(Rest).ToList()).ToList();
		var inline = rests.Select(group => group.All(rest => rest.Length == 0)).ToList();
		var outline = Outline(groups.Select(group => group.Directory).ToList(), inline.Select((one, g) => one ? 1 : 1 + groups[g].Rows.Count).ToList());
		for (var g = 0; g < groups.Count; g++)
		{
			var (headers, depth, label) = outline[g];
			foreach (var (headerDepth, header) in headers) lines.Add($"{pad}{Indent(headerDepth)}{header}/");
			var at = pad + Indent(depth);
			if (inline[g])
				lines.Add($"{at}{label}/: {string.Join("  ", FoldNumbered(names[g]))}");
			else
			{
				lines.Add($"{at}{label}/");
				lines.AddRange(names[g].Select((name, i) => rests[g][i].Length == 0 ? $"{at}  {name}" : $"{at}  {name}  {rests[g][i]}"));
			}
		}
		return lines;
	}

	static string Indent(int depth) => new(' ', depth * 2);

	/// <summary>A shared parent shorter than this (`Assets/`) is not worth its own line.</summary>
	const int MinHeaderLength = 10;

	/// <summary>
	/// For consecutive directories, the parent headers to print before each one, its depth, and its
	/// path relative to the innermost header. A header opens when this directory and the next share a
	/// parent that is neither of them, and only when dropping it from the groups it covers saves more
	/// than its own line and the indent it adds to their `lineCounts` lines. Order is kept: a parent
	/// is never reopened to pull in later directories.
	/// </summary>
	internal static List<(List<(int Depth, string Header)> Headers, int Depth, string Label)> Outline(IReadOnlyList<string> directories, IReadOnlyList<int>? lineCounts = null)
	{
		var result = new List<(List<(int, string)>, int, string)>(directories.Count);
		var open = new List<string>();
		for (var i = 0; i < directories.Count; i++)
		{
			var directory = directories[i];
			while (open.Count > 0 && !IsUnder(directory, open[^1])) open.RemoveAt(open.Count - 1);
			var headers = new List<(int, string)>();
			if (i + 1 < directories.Count)
			{
				var shared = SharedParent(directory, directories[i + 1]);
				var enclosing = open.Count > 0 ? open[^1] : "";
				if (shared.Length >= enclosing.Length + MinHeaderLength && shared != directory && shared != directories[i + 1] && Saving(shared, enclosing, i) > 0)
				{
					headers.Add((open.Count, Relative(shared, enclosing)));
					open.Add(shared);
				}
			}
			result.Add((headers, open.Count, Relative(directory, open.Count > 0 ? open[^1] : "")));
		}
		return result;

		int Saving(string header, string enclosing, int from)
		{
			var label = Relative(header, enclosing).Length + 1;
			var (covered, indented) = (0, 0);
			for (var j = from; j < directories.Count && IsUnder(directories[j], header); j++)
			{
				covered++;
				indented += lineCounts?[j] ?? 1;
			}
			return covered * label - (label + open.Count * 2 + 1) - indented * 2;
		}
	}

	static bool IsUnder(string path, string parent) =>
		path.Length > parent.Length && path.StartsWith(parent, StringComparison.Ordinal) && path[parent.Length] == '/';

	static string Relative(string path, string parent) => parent.Length == 0 ? path : path[(parent.Length + 1)..];

	/// <summary>Longest common parent of two directories, on whole path segments.</summary>
	static string SharedParent(string a, string b)
	{
		var length = 0;
		for (var i = 0; i <= Math.Min(a.Length, b.Length); i++)
		{
			var endA = i == a.Length || a[i] == '/';
			var endB = i == b.Length || b[i] == '/';
			if (endA && endB) length = i;
			if (i == a.Length || i == b.Length || a[i] != b[i]) break;
		}
		return a[..length];
	}

	static string DirectoryOf(string path)
	{
		var slash = path.LastIndexOf('/');
		return slash <= 0 ? "" : path[..slash];
	}

	[GeneratedRegex(@"^(?<prefix>.*?)(?<number>\d+)(?<suffix>\D*)$")]
	private static partial Regex Numbered();

	/// <summary>
	/// `Level_1.unity Level_2.unity Level_3.unity` → `Level_{1..3}.unity`. Runs need the same prefix
	/// and suffix (and width, when zero-padded); order is kept, and runs shorter than three stay as they are.
	/// </summary>
	public static List<string> FoldNumbered(IReadOnlyList<string> names)
	{
		var output = new List<string>();
		var index = 0;
		while (index < names.Count)
		{
			var match = Numbered().Match(names[index]);
			if (!match.Success)
			{
				output.Add(names[index++]);
				continue;
			}
			var (prefix, suffix, first) = (match.Groups["prefix"].Value, match.Groups["suffix"].Value, match.Groups["number"].Value);
			var numbers = new List<string> { first };
			var next = index + 1;
			while (next < names.Count && Numbered().Match(names[next]) is { Success: true } other
				&& other.Groups["prefix"].Value == prefix && other.Groups["suffix"].Value == suffix
				&& SameWidthRule(first, other.Groups["number"].Value))
			{
				numbers.Add(other.Groups["number"].Value);
				next++;
			}
			if (numbers.Count < 3)
			{
				output.Add(names[index++]);
				continue;
			}
			output.Add($"{prefix}{{{Ranges(numbers)}}}{suffix}");
			index = next;
		}
		return output;
	}

	/// <summary>Zero-padded numbers (`007`) only run with the same width; unpadded ones (`9`, `10`) always do.</summary>
	static bool SameWidthRule(string a, string b)
	{
		var padded = (a.Length > 1 && a[0] == '0') || (b.Length > 1 && b[0] == '0');
		return !padded || a.Length == b.Length;
	}

	/// <summary>`1 2 3 5 7 8 9` → `1..3,5,7..9`.</summary>
	static string Ranges(List<string> numbers)
	{
		var parts = new List<string>();
		var start = 0;
		for (var i = 1; i <= numbers.Count; i++)
		{
			var contiguous = i < numbers.Count && long.TryParse(numbers[i], out var current) && long.TryParse(numbers[i - 1], out var previous) && current == previous + 1;
			if (contiguous) continue;
			parts.Add(i - start >= 3 ? $"{numbers[start]}..{numbers[i - 1]}" : string.Join(",", numbers.Skip(start).Take(i - start)));
			start = i;
		}
		return string.Join(",", parts);
	}
}
