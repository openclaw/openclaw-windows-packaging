using System.Runtime.InteropServices;

// Every P/Invoke in this assembly targets kernel32.dll, which always lives in
// System32. Restricting the search path to System32 assembly-wide prevents DLL
// planting: without it the loader would also probe the application directory
// and the current directory, where an attacker-supplied kernel32.dll could be
// loaded instead. Individual P/Invokes can override this with their own
// DefaultDllImportSearchPaths attribute if they ever need a different policy.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
