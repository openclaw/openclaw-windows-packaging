using System.Runtime.InteropServices;

// Every P/Invoke in this assembly targets kernel32.dll or iphlpapi.dll, both of
// which always live in System32. Restricting the search path to System32
// assembly-wide prevents DLL planting: without it the loader would also probe
// the application directory and the current directory. That matters more here
// than in the launcher, because this helper runs inside the isolated session
// alongside whatever the agent has written to its own workspace.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
