using System.Text.Json;
using System.Text.Json.Nodes;
using OptimizedUnity.Features;
using OptimizedUnity.Proc;

namespace OptimizedUnity.Tests;

public class ForceCloseTests
{
	sealed class FakeProcesses : IProcessControl
	{
		public readonly Dictionary<int, EditorProcessInfo> Alive = [];
		public readonly List<(int Pid, bool Kill)> Signals = [];
		public bool DieOnTerm = true;

		public EditorProcessInfo? Inspect(int pid) => Alive.GetValueOrDefault(pid);

		public void Signal(int pid, bool kill)
		{
			Signals.Add((pid, kill));
			if (kill || DieOnTerm) Alive.Remove(pid);
		}
	}

	sealed class Harness : IDisposable
	{
		public readonly TempDir Temp = new();
		public readonly FakeProcesses Processes = new();
		public readonly List<string> Calls = [];
		public string Project => Temp.Sub("Proj");
		public bool CloseQuits = true;
		public Func<string>? Discovery;
		long clock;

		public Harness()
		{
			Directory.CreateDirectory(Path.Combine(Project, "Temp", "nested"));
			File.WriteAllText(Path.Combine(Project, "Temp", "nested", "f.txt"), "x");
		}

		public string Instances() =>
			new JsonObject
			{
				["success"] = true,
				["data"] = new JsonObject
				{
					["instances"] = new JsonArray(Processes.Alive.Values.Select(p => (JsonNode)new JsonObject
					{
						["projectPath"] = p.Project, ["pid"] = p.Pid, ["isRunning"] = true,
					}).ToArray()),
				},
				["errors"] = new JsonArray(),
				["warnings"] = new JsonArray(),
			}.ToJsonString();

		public ForceClose Create() => new()
		{
			Processes = Processes,
			Now = () => clock,
			Sleep = ms => clock += ms,
			Cli = (args, _, _) =>
			{
				Calls.Add(string.Join(' ', args));
				if (args[0] == "pipeline") return new ChildResult(Discovery?.Invoke() ?? Instances(), "", 0);
				if (CloseQuits) Processes.Alive.Clear();
				return new ChildResult("{}", "", 0);
			},
		};

		public void Run(int pid, string? project = null) =>
			Processes.Alive[pid] = new EditorProcessInfo(pid, project ?? Project, "/Applications/Unity.app/Contents/MacOS/Unity", "Mon 1");

		public void Dispose() => Temp.Dispose();
	}

	[Fact]
	public void ClosesThroughCliAndCleansTemp()
	{
		using var h = new Harness();
		h.Run(42);
		var result = h.Create().Run(h.Project, h.Project, ["close", h.Project, "--force"]);
		Assert.Equal(0, result.Code);
		Assert.Equal("Unity Editor closed; Temp cleaned.\n", result.Stdout);
		Assert.False(Directory.Exists(Path.Combine(h.Project, "Temp")));
		Assert.Empty(h.Processes.Signals);
		Assert.Contains($"close {h.Project} --timeout 5 --force --json", h.Calls);
	}

	[Fact]
	public void FallsBackToNativeTermination()
	{
		using var h = new Harness { CloseQuits = false };
		h.Processes.DieOnTerm = false;
		h.Run(42);
		var result = h.Create().Run(h.Project, h.Project, ["close", h.Project, "--force"]);
		Assert.Equal(0, result.Code);
		Assert.Contains("Native OS termination was required.", result.Stdout);
		Assert.Equal([(42, false), (42, true)], h.Processes.Signals);
	}

	[Fact]
	public void AlreadyStoppedStillCleansTemp()
	{
		using var h = new Harness();
		var result = h.Create().Run(h.Project, h.Project, ["close", h.Project, "--force"]);
		Assert.Equal("Unity Editor already stopped; Temp cleaned.\n", result.Stdout);
		Assert.DoesNotContain(h.Calls, call => call.StartsWith("close", StringComparison.Ordinal));
	}

	[Fact]
	public void RefusesWhenPidBelongsToAnotherProject()
	{
		using var h = new Harness();
		h.Run(42);
		h.Processes.Alive[42] = h.Processes.Alive[42] with { Project = h.Temp.Sub("Other") };
		// Discovery still attributes the PID to our project.
		h.Discovery = () => h.Instances().Replace(JsonEncodedText.Encode(h.Temp.Sub("Other")).ToString(), JsonEncodedText.Encode(h.Project).ToString());
		var result = h.Create().Run(h.Project, h.Project, ["close", h.Project, "--force"]);
		Assert.Equal(ExitCodes.CommandFailed, result.Code);
		Assert.Contains("belongs to another project", result.Stderr);
		Assert.True(Directory.Exists(Path.Combine(h.Project, "Temp")));
	}

	[Theory]
	[InlineData("""{"success":true,"data":{"instances":[]},"errors":[],"warnings":["process scan incomplete"]}""", "process scan incomplete")]
	[InlineData("""{"success":true,"data":{"instances":[{"projectPath":"/x","isRunning":true}]},"errors":[],"warnings":[]}""", "without an OS process ID")]
	[InlineData("""{"success":true,"data":{},"errors":[],"warnings":[]}""", "invalid instance list")]
	[InlineData("not json", "Process discovery failed")]
	public void IncompleteDiscoveryPreservesTemp(string discovery, string message)
	{
		using var h = new Harness { Discovery = () => discovery };
		var result = h.Create().Run(h.Project, h.Project, ["close", h.Project, "--force"]);
		Assert.Equal(ExitCodes.CommandFailed, result.Code);
		Assert.Contains("Temp was preserved", result.Stderr);
		Assert.Contains(message, result.Stderr);
		Assert.True(Directory.Exists(Path.Combine(h.Project, "Temp")));
	}

	[Fact]
	public void TimesOutWhenProcessNeverExits()
	{
		using var h = new Harness { CloseQuits = false };
		h.Processes.DieOnTerm = false;
		h.Run(42);
		h.Processes.Alive[43] = h.Processes.Alive[42] with { Pid = 43 };
		var processes = h.Processes;
		var close = h.Create();
		// Signals never take effect.
		var stubborn = new ForceClose
		{
			Cli = close.Cli,
			Processes = new Stubborn(processes),
			Now = close.Now,
			Sleep = close.Sleep,
		};
		var result = stubborn.Run(h.Project, h.Project, ["close", h.Project, "--force"]);
		Assert.Contains("timed out", result.Stderr);
		Assert.True(Directory.Exists(Path.Combine(h.Project, "Temp")));
	}

	sealed class Stubborn(FakeProcesses inner) : IProcessControl
	{
		public EditorProcessInfo? Inspect(int pid) => inner.Inspect(pid);
		public void Signal(int pid, bool kill) { }
	}

	[Fact]
	public void SymlinkedTempIsRefused()
	{
		if (OperatingSystem.IsWindows()) return;
		using var h = new Harness();
		var outside = h.Temp.Sub("outside");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
		Directory.Delete(Path.Combine(h.Project, "Temp"), recursive: true);
		Directory.CreateSymbolicLink(Path.Combine(h.Project, "Temp"), outside);
		var result = h.Create().Run(h.Project, h.Project, ["close", h.Project, "--force"]);
		Assert.Contains("not a symlink", result.Stderr);
		Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
	}
}
