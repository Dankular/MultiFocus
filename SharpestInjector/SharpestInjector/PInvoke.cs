using System.Runtime.InteropServices;
using System.Text;
using System;

namespace SharpestInjector
{
    public static class PInvoke
    {
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flNewProtect, out uint flOldProtect);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out uint lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool GetExitCodeThread(IntPtr hThread, out long lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, [Optional] uint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern uint GetFinalPathNameByHandleW(IntPtr hFile, [Out] StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool GetFileSizeEx(IntPtr hFile, out long lpFileSize);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool IsWow64Process(IntPtr processHandle, out bool wow64Process);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        [DllImport("psapi.dll", SetLastError = true, ExactSpelling = true)]
        public static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr lphModule, uint cb, out uint lpcbNeeded, uint dwFilterFlag);

        [DllImport("psapi.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern bool GetModuleFileNameExW(IntPtr hProcess, IntPtr hModule, [Out] StringBuilder lpFilename, uint nSize); // NOTE: So, this returns system DLLs path as if they were in System32 in Windows 10 and up, even if you're trying to get them from a 32-bit process. Definitely not gonna bother with the whole GetMappedFileName thing, but I'm leaving this here as a note for whoever has problems with it.

        [DllImport("psapi.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern bool GetMappedFileNameW(IntPtr hProcess, IntPtr hFile, [Out] StringBuilder lpFilename, uint nSize);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.Dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr parentHandle, Win32Callback callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        public delegate bool Win32Callback(IntPtr hwnd, IntPtr lParam);

        // privileges
        public const uint PROCESS_CREATE_THREAD = 0x0002;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_VM_OPERATION = 0x0008;
        public const uint PROCESS_VM_WRITE = 0x0020;
        public const uint PROCESS_VM_READ = 0x0010;

        // used for memory allocation
        public const uint MEM_COMMIT = 0x1000;
        public const uint MEM_RESERVE = 0x2000;
        public const uint MEM_RELEASE = 0x8000;
        public const uint PAGE_READWRITE = 0x4;
        public const uint PAGE_EXECUTE_READ = 0x20;

        public const uint CREATE_SUSPENDED = 0x4;

        public const uint LIST_MODULES_ALL = 0x03;

        public const uint TH32CS_SNAPMODULE = 0x8;
        public const uint TH32CS_SNAPMODULE32 = 0x10;

        public const uint ERROR_BAD_LENGTH = 0x18;

        public const uint MAX_PATH = 260;

        public const uint OPEN_EXISTING = 3;
        public const uint VOLUME_NAME_DOS = 0;

        public const uint INFINITE = 0xFFFFFFFF;

        /// <summary>
        /// Equal to a pointer with value of -1
        /// </summary>
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
    }

    public class AnsiString
    {
        public readonly string Parameter;

        public AnsiString(string parameter)
        {
            Parameter = parameter;
        }
    }

    public class AnsiChar
    {
        public readonly char Parameter;

        public AnsiChar(char parameter)
        {
            Parameter = parameter;
        }
    }

    public class PointerParam
    {
        public readonly object Parameter;

        public PointerParam(object parameter)
        {
            Parameter = parameter;
        }
    }
}
