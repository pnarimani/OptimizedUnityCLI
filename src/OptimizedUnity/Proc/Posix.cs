using System.Reflection;
using System.Runtime.InteropServices;

namespace OptimizedUnity.Proc;

internal static partial class Posix
{
	public const int SIGINT = 2;
	public const int SIGKILL = 9;
	public const int SIGTERM = 15;
	const int ESRCH = 3;
	const string Libc = "libc";

	static Posix()
	{
		// "libc" alone does not resolve on glibc (libc.so is a linker script).
		NativeLibrary.SetDllImportResolver(typeof(Posix).Assembly, Resolve);
	}

	static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
	{
		if (name != Libc) return IntPtr.Zero;
		string[] candidates = OperatingSystem.IsMacOS()
			? ["/usr/lib/libSystem.B.dylib", "libc.dylib"]
			: ["libc.so.6", "libc.so", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1"];
		foreach (var candidate in candidates)
			if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
		return IntPtr.Zero;
	}

	[LibraryImport(Libc, EntryPoint = "execv", StringMarshalling = StringMarshalling.Utf8)]
	private static partial int ExecV(string path, string?[] argv);

	[LibraryImport(Libc, EntryPoint = "kill", SetLastError = true)]
	private static partial int Kill(int pid, int signal);

	[LibraryImport(Libc, EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
	private static partial IntPtr RealPathNative(string path, IntPtr resolved);

	[LibraryImport(Libc, EntryPoint = "free")]
	private static partial void Free(IntPtr pointer);

	/// <summary>Replace the current process image. Returns only on failure.</summary>
	public static void Exec(string path, IReadOnlyList<string> args)
	{
		var argv = new string?[args.Count + 2];
		argv[0] = path;
		for (var i = 0; i < args.Count; i++) argv[i + 1] = args[i];
		ExecV(path, argv);
	}

	/// <summary>Send a signal; a process that is already gone is not an error.</summary>
	public static bool Signal(int pid, int signal)
	{
		if (Kill(pid, signal) == 0) return true;
		return Marshal.GetLastPInvokeError() == ESRCH;
	}

	public static string? RealPath(string path)
	{
		var pointer = RealPathNative(path, IntPtr.Zero);
		if (pointer == IntPtr.Zero) return null;
		try
		{
			return Marshal.PtrToStringUTF8(pointer);
		}
		finally
		{
			Free(pointer);
		}
	}
}
