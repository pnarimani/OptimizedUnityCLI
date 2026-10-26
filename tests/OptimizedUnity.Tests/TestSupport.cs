using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OptimizedUnity.Json;

namespace OptimizedUnity.Tests;

internal static class Fixture
{
	public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
	public static JsonNode Json(string name) => JsonNode.ParseLenient(Text(name))!;
}

/// <summary>A [Fact] for behaviour that only exists under Linux/WSL path semantics.</summary>
internal sealed class LinuxFactAttribute : FactAttribute
{
	public LinuxFactAttribute()
	{
		if (OperatingSystem.IsWindows()) Skip = "Linux path semantics";
	}
}

internal static class TestSetup
{
	public static readonly string SpillDir = Path.Combine(Path.GetTempPath(), "optimized-unity-tests", Guid.NewGuid().ToString("N"));

	[ModuleInitializer]
	public static void Init() => Environment.SetEnvironmentVariable("UNITY_WRAP_SPILL_DIR", SpillDir);
}

internal sealed class TempDir : IDisposable
{
	public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ou-" + Guid.NewGuid().ToString("N"));
	public TempDir() => Directory.CreateDirectory(Path);
	public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
	public void Dispose()
	{
		try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
	}
}
