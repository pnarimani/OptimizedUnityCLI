using System.Runtime.InteropServices;

namespace OptimizedUnity.Proc;

internal static partial class WindowsConsole
{
	/// <summary>
	/// Whether this process runs without any console, e.g. started with DETACHED_PROCESS by a GUI host.
	/// A console child of such a process would open a new, visible console window. Always false off Windows.
	/// </summary>
	public static bool IsMissing { get; } = OperatingSystem.IsWindows() && !HasConsole();

	static unsafe bool HasConsole()
	{
		uint process;
		return GetConsoleProcessList(&process, 1) > 0;
	}

	[LibraryImport("kernel32.dll")]
	private static unsafe partial uint GetConsoleProcessList(uint* processList, uint processCount);
}
