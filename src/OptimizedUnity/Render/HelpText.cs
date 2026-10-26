namespace OptimizedUnity.Render;

/// <summary>Subcommand help repeats ~1.3 KB of root options; `unity --help` already lists them.</summary>
internal static class HelpText
{
	const string GlobalOptions = "Global Options:";
	const string Pointer = "Global options: see `unity --help`.";

	public static string Compact(string help)
	{
		var lines = Format.SplitLines(Format.StripAnsi(help));
		var output = new List<string>(lines.Count);
		var skipping = false;
		foreach (var raw in lines)
		{
			var line = raw.TrimEnd();
			if (line == GlobalOptions)
			{
				output.Add(Pointer);
				skipping = true;
				continue;
			}
			// The block ends at the next unindented, non-empty line (a new section or a note).
			if (skipping)
			{
				if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue;
				skipping = false;
				output.Add("");
			}
			if (line.Length == 0 && (output.Count == 0 || output[^1].Length == 0)) continue;
			output.Add(line);
		}
		while (output.Count > 0 && output[^1].Length == 0) output.RemoveAt(output.Count - 1);
		return output.Count == 0 ? "" : string.Join('\n', output) + "\n";
	}
}
