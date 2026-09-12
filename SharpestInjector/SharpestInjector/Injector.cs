using static SharpestInjector.PInvoke;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System;

namespace SharpestInjector
{
    public static class Injector
    {
        public static ProcessInfo GetProcessInfo(Process process)
        {
            var procc = new ProcessInfo();

            var processID = (uint)process.Id;

            // geting the handle of the process - with required privileges
            var processHandle = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, false, processID);

            foreach (var module in EnumerateProcessModules(processHandle))
            {
                var path = module.Path.ToUpperInvariant();
                procc.Modules.Add(path, module);
                if (path.EndsWith("KERNEL32.DLL")) // TODO: Better
                    procc.Kernel32 = module.MemoryAddress;
            }

            if (IsWow64Process(processHandle, out bool isWow64)) // TODO: Use the sequel
                procc.IsWOW64 = isWow64;

            CloseHandle(processHandle);

            procc.Id = processID;

            procc.WindowHandle = process.MainWindowHandle;
            procc.WindowTitle = process.MainWindowTitle.Trim();

            return procc;
        }

        public static ProcessInfo GetProcessInfo(int processID)
        {
            return GetProcessInfo(Process.GetProcessById(processID));
        }

        public static IEnumerable<ModuleInfo> EnumerateProcessModules(IntPtr processHandle)
        {
            return new ProcessModuleIterator(processHandle);
        }

        public static bool Unload(ProcessInfo process, PeFile dll)
        {
            // geting the handle of the process - with required privileges
            IntPtr hProcess = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, false, process.Id);

            if (hProcess == IntPtr.Zero)
                return false;

            // searching for the address of FreeLibraryAndExitThread and storing it in a pointer
            IntPtr freeLibraryAddr = IntPtr.Add(process.Kernel32, process.IsWOW64 ? Constants.FreeLibrary32 : Constants.FreeLibrary);

            // Setting up the variable for the second argument for EnumProcessModules
            // This will actually be an array of pointers to the handles, rather than an array of handles, keep that in mind for later
            IntPtr[] hMods = new IntPtr[1024];

            GCHandle gch = GCHandle.Alloc(hMods, GCHandleType.Pinned); // Don't forget to free this later
            IntPtr pModules = gch.AddrOfPinnedObject();

            // Setting up the rest of the parameters for EnumProcessModules
            uint uiSize = (uint)(Marshal.SizeOf(typeof(IntPtr)) * hMods.Length);

            bool success = false;

            if (EnumProcessModulesEx(hProcess, pModules, uiSize, out var cbNeeded, LIST_MODULES_ALL))
            {
                int uiTotalNumberofModules = (int)(cbNeeded / Marshal.SizeOf(typeof(IntPtr)));

                for (int i = 0; i < uiTotalNumberofModules; i++)
                {
                    StringBuilder strbld = new StringBuilder(1024);

                    if (GetModuleFileNameExW(hProcess, hMods[i], strbld, (uint)strbld.Capacity) == false)
                        continue;

                    var path = strbld.ToString();
                    var moduleName = path.ToUpperInvariant();

                    if (moduleName == dll.FileName.ToUpperInvariant())
                    {
                        success = CreateAndRunThread(hProcess, freeLibraryAddr, hMods[i]) != IntPtr.Zero;
                        break;
                    }
                }
            }

            CloseHandle(hProcess);
            // Must free the GCHandle object
            gch.Free();

            return success;
        }

        public static IntPtr Inject(ProcessInfo process, PeFile dll)
        {
            if(dll.Is64Bit != process.Is64Bit)
                throw new Exception("Cannot inject a 64-bit dll into a 32-bit process or vice-versa.");

            // Getting the handle of the process - with required privileges
            // MSDN says QUERY_INFORMATION and VM_READ privileges are required otherwise CreateRemoteThread may fail on.. certain platforms?
            // Well, better safe than sorry
            IntPtr hProcess = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, false, process.Id);
                        
            if (hProcess == IntPtr.Zero) // Try again with lowest required privileges
                hProcess = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_VM_OPERATION | PROCESS_VM_WRITE, false, process.Id);

            if (hProcess == IntPtr.Zero)
                return IntPtr.Zero;

            // Look, I think it was worth it
            IntPtr loadLibraryAddr = IntPtr.Add(process.Kernel32, process.IsWOW64 ? Constants.LoadLibrary32 : Constants.LoadLibrary);

            // Mame of the dll we want to inject
            string dllName = dll.FileName;

            var dllNameBytes = Encoding.Unicode.GetBytes(dllName);

            // Alocating some memory on the target process (enough to store the path to the dll)
            // And storing its address in a pointer
            IntPtr ptrParam = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)dllNameBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);

            if (ptrParam == IntPtr.Zero)
            {
                CloseHandle(hProcess);
                return IntPtr.Zero;
            }

            // Writing the name of the dll there so it can be used as a parameter for LoadLibraryW
            if (WriteProcessMemory(hProcess, ptrParam, dllNameBytes, (uint)dllNameBytes.Length, out uint bytesWritten) == false || bytesWritten != dllNameBytes.Length)
            {
                VirtualFreeEx(hProcess, ptrParam, 0, MEM_RELEASE);
                CloseHandle(hProcess);
                return IntPtr.Zero;
            }

            var success = CreateAndRunThread(hProcess, loadLibraryAddr, ptrParam);

            VirtualFreeEx(hProcess, ptrParam, 0, MEM_RELEASE); // Yeah, no memory leaks here baby
            CloseHandle(hProcess);

            return success;
        }

        private static IntPtr CreateAndRunThread(IntPtr hProcess, IntPtr loadLibraryAddr, IntPtr ptrParam)
        {
            // Creating a thread that will call LoadLibraryA with loadLibraryAddr as argument
            // All that's needed for 32 bit injection is the right library address... How hard can it be?
            var hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, loadLibraryAddr, ptrParam, CREATE_SUSPENDED, out uint threadId);

            if (hThread == IntPtr.Zero)
                return IntPtr.Zero;

            var hModule = RunThread(hThread);
            CloseHandle(hThread);

            return hModule;
        }

        private static IntPtr RunThread(IntPtr hThread)
        {
            if (ResumeThread(hThread) == false)
                return IntPtr.Zero;

            var wait = WaitForSingleObject(hThread, INFINITE); // TODO: Uhh
            if (wait != 0)                                     // Future me: Why did I write that that's not very helpful at all what did I mean by this
                return IntPtr.Zero;                            // Did I just not like the "Infinite" wait?

            if (GetExitCodeThread(hThread, out long exitCode) == false) // Actually useless since CreateRemoteThread only returns a 32-bit handle
                return IntPtr.Zero;                                     // TODO: Now, I could use the funny assembly injection thing I wrote for this as well 
                                                                        // so I could get the handle, but that's a step too far even for me

            return new IntPtr(exitCode); // Exit code from that thread will be the module handle, since that's what LoadLibrary returns.
        }
    }
}