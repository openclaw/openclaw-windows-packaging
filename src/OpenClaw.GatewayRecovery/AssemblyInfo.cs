using System.Runtime.InteropServices;

// Every P/Invoke in this assembly targets kernel32.dll, which always lives in
// System32. Restricting the search path prevents DLL planting from the
// application or current directory.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
