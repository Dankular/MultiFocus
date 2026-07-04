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

            if (process.MainWindowHandle != IntPtr.Zero)
            {
                procc.ChildWindows = GetAllWindows(procc.WindowHandle);
            }

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

        public static List<IntPtr> GetRootWindowsOfProcess(int pid)
        {
            List<IntPtr> rootWindows = GetChildWindows(IntPtr.Zero);
            List<IntPtr> dsProcRootWindows = new List<IntPtr>();
            foreach (IntPtr hWnd in rootWindows)
            {
                uint lpdwProcessId;
                GetWindowThreadProcessId(hWnd, out lpdwProcessId);
                if (lpdwProcessId == pid)
                    dsProcRootWindows.Add(hWnd);
            }
            return dsProcRootWindows;
        }

        public static List<ChildWindow> GetAllWindows(IntPtr parent)
        {
            var list = new List<ChildWindow>();

            foreach(var child in GetChildWindows(parent))
            {
                var title = GetWindowTitle(child);

                if(string.IsNullOrEmpty(title))
                    continue;

                var children = GetAllWindows(child);

                list.Add(new ChildWindow()
                {
                    Handle = child,
                    Title = title,
                    Children = children
                });
            }


            return list;
        }

        public static string GetWindowTitle(IntPtr parent)
        {
            var length = GetWindowTextLength(parent);
            var sb = new StringBuilder(length + 1);
            GetWindowText(parent, sb, sb.Capacity);
            return sb.ToString();
        }

        public static List<IntPtr> GetChildWindows(IntPtr parent)
        {
            List<IntPtr> result = new List<IntPtr>();
            GCHandle listHandle = GCHandle.Alloc(result);
            try
            {
                Win32Callback childProc = new Win32Callback(EnumWindow);
                EnumChildWindows(parent, childProc, GCHandle.ToIntPtr(listHandle));
            }
            finally
            {
                if (listHandle.IsAllocated)
                    listHandle.Free();
            }
            return result;
        }

        private static bool EnumWindow(IntPtr handle, IntPtr pointer)
        {
            GCHandle gch = GCHandle.FromIntPtr(pointer);
            List<IntPtr> list = gch.Target as List<IntPtr>;
            if (list == null)
            {
                throw new InvalidCastException("GCHandle Target could not be cast as List<IntPtr>");
            }
            list.Add(handle);
            //  You can modify this to check to see if you want to cancel the operation, then return a null here
            return true;
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
        
        public struct ExternParam
        {
            public uint Offset;
            public byte[] Value;

            public ExternParam(uint offset)
            {
                Offset = offset;
                Value = null;
            }

            public ExternParam(uint offset, byte[] value)
            {
                Offset = offset;
                Value = value;
            }
        }

        // TODO: Notes - __chkstk seems to be called for both x86 (at 4K) and x64 (at 8K). I'll get to it once I'll need that much stack space for variables.
        // Also really gotta love Microsoft for making x64 calls fastcall instead of just storing all parameters on the stack like with the good old x86
        public static IntPtr CallExport(ProcessInfo process, IntPtr exportAddress, params object[] parameters)
        {
            var is64 = Constants.Is64Bit != process.IsWOW64; // Truth table let's go

            if (parameters.Length > 0xFF / 8) // Max byte value, divided by 8 for 64-bit stack size - equal to 31 but this sounds better in my head
                throw new Exception("Are you quite sane?"); // I don't want to implement mov with longer offset just because someone decided to use more than 31 parameters, in fact I want a word with the person who did that

            // Getting the handle of the process - with required privileges
            // MSDN says QUERY_INFORMATION and VM_READ privileges are required otherwise CreateRemoteThread may fail on.. certain platforms?
            // Well, better safe than sorry
            IntPtr hProcess = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, false, process.Id);

            if (hProcess == IntPtr.Zero) // try again with lowest required privileges
                hProcess = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_VM_OPERATION | PROCESS_VM_WRITE, false, process.Id);

            if (hProcess == IntPtr.Zero)
                return IntPtr.Zero;


            if (parameters == null || parameters.Length == 0)
                return CreateAndRunThread(hProcess, exportAddress, IntPtr.Zero);

            var parameterOffsetList = new List<uint>();
            var paramBytesList = new List<byte>();

            foreach (var parameter in parameters)
            {
                parameterOffsetList.Add((uint)paramBytesList.Count);

                switch (parameter)
                {
                    case string sParam:
                        paramBytesList.AddRange(Encoding.Unicode.GetBytes(sParam + '\0'));
                        break;
                    case AnsiString asParam: 
                        paramBytesList.AddRange(Constants.AnsiEncoding.GetBytes(asParam.Parameter + '\0'));
                        break;
                    case char cParam:
                        paramBytesList.AddRange(BitConverter.GetBytes(cParam));
                        break;
                    case AnsiChar acParam:
                        paramBytesList.AddRange(Constants.AnsiEncoding.GetBytes(new char[] { acParam.Parameter }));
                        break;


                    case int iParam:
                        paramBytesList.AddRange(BitConverter.GetBytes(iParam));
                        break;
                    case float fParam:
                        paramBytesList.AddRange(BitConverter.GetBytes(fParam));
                        break;
                    case byte bParam:
                        paramBytesList.Add(bParam);
                        break;
                    case byte[] bParams:
                        paramBytesList.AddRange(bParams);
                        break;
                }
            }

            // Alocating some memory on the target process (enough to store the parameters)
            IntPtr ptrParams = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)paramBytesList.Count, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);

            if (ptrParams == IntPtr.Zero)
            {
                CloseHandle(hProcess);
                return IntPtr.Zero;
            }

            var codeSize = 20 + (8 * (parameterOffsetList.Count - 1)); // The code is always at least 20 bytes, plus 8 * number of parameters (excluding first) for each mov;
            // I'll fix the calculations later. For now it's always gonna allocate way more than what's needed anyway.

            var stackSize = (byte)(0x04 * parameterOffsetList.Count);

            if (is64)
            {
                stackSize = (byte)(0x08 * parameterOffsetList.Count);
                stackSize = (byte)(Math.Ceiling(stackSize / 16f) * 16); // Stack needs to be 16-byte aligned

                if (stackSize >= 128)
                    throw new Exception("Oops"); // I am sorry to whoever gets this exception, but I am not changing it
            }


            IntPtr ptrCode = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)codeSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);

            var codeBytesList = new List<byte>();
            
            if(is64 == false)
            {
                codeBytesList.Add(                  0x55                    );                                  // push ebp                              | Push EBP to stack  
                codeBytesList.AddRange(new byte[] { 0x89,0xE5               });                                 // mov ebp,esp                           | Stores stack pointer in EBP for release later
                codeBytesList.AddRange(new byte[] { 0x83,0xEC,    stackSize });                                 // sub esp,(stackSize)                   | Moves stack pointer to store all parameters
                codeBytesList.AddRange(new byte[] { 0xC7,0x04,0x24          });                                 // mov dword ptr ss:[esp], (address)     | First parameter
                    codeBytesList.AddRange(BitConverter.GetBytes((uint)ptrParams+parameterOffsetList[0]));      // Address pointing to the first parameter

                for(int i = 1; i < parameterOffsetList.Count; i++)
                {
                    codeBytesList.AddRange(new byte[] { 0xC7,0x44,0x24,     (byte)(0x04*i) });                      // mov dword ptr ss:[esp+(4*i)], (address)   | All subsequent parameters
                        codeBytesList.AddRange(BitConverter.GetBytes((uint)ptrParams + parameterOffsetList[i]));    // Address pointing to the parameter
                }

                // TODO: Change to absolute call, something something ±2GB
                codeBytesList.AddRange(new byte[] { 0xE8                    });                                 // call (address)                        | Call the function
                    var addressOffset = codeBytesList.Count + 4;                                                        // Offset for the address - starts at the end of this instruction
                    codeBytesList.AddRange(BitConverter.GetBytes((uint)(exportAddress-addressOffset)-(uint)ptrCode));   // Address to the export function - offset 
                codeBytesList.Add(                  0xC9                    );                                  // leave                                 | Restores stack pointer and pops EBP from stack
                codeBytesList.Add(                  0xC3                    );                                  // ret                                   | Return
            }
            else 
            { 
                codeBytesList.Add(                  0x55                    );                                  // push rbp                              | Push RBP to stack  
                codeBytesList.AddRange(new byte[] { 0x48, 0x8B,0xEC         });                                 // mov rbp,rsp                           | Stores stack pointer in RBP for release later
                codeBytesList.AddRange(new byte[] { 0x48, 0x83,0xEC, stackSize});                               // sub esp,(stackSize)                   | Moves stack pointer to store all parameters

                for(int i = 0; i < parameterOffsetList.Count && i < 4; i++) // Could've used LEA but decided to make it simple. Not sure what the advantage would be anyway, other than slightly smaller size if the relative address fits within 32 bits
                {
                    switch (i)
                    {
                        case 0:
                            codeBytesList.AddRange(new byte[] { 0x48, 0xB9 });                                          // mov rcx,(paramAddress)
                            break;
                        case 1:
                            codeBytesList.AddRange(new byte[] { 0x48, 0xBA });                                          // mov rdx,(paramAddress)
                            break;
                        case 2:
                            codeBytesList.AddRange(new byte[] { 0x49, 0xB8 });                                          // mov r8,(paramAddress)
                            break;
                        case 3:
                            codeBytesList.AddRange(new byte[] { 0x49, 0xB9 });                                          // mov r9,(paramAddress)
                            break;
                    }

                    codeBytesList.AddRange(BitConverter.GetBytes((ulong)ptrParams + parameterOffsetList[i]));
                }

                byte stackOffset = 32; // The first 4 params still count as being on the stack, because screw you

                for (int i = 4; i < parameterOffsetList.Count; i++) // Subsequent parameters are pushed to the stack. Need to do it through a temporary register because screw you if you need to move a value to a 64-bit register with offset
                {
                    codeBytesList.AddRange(new byte[] { 0x48, 0xB8 });                                              // mov rax,(paramAddress)
                        codeBytesList.AddRange(BitConverter.GetBytes((ulong)ptrParams + parameterOffsetList[i]));

                    codeBytesList.AddRange(new byte[] { 0x48, 0x89,0x44,0x24, stackOffset });                       // mov qword ptr ss:[rsp+(stackOffset)],rax

                    stackOffset += 8;
                }


                codeBytesList.AddRange(new byte[] { 0x48, 0xB8 });                                              // mov rax,(functionAddress)
                    codeBytesList.AddRange(BitConverter.GetBytes((ulong)exportAddress));
                codeBytesList.AddRange(new byte[] { 0xFF, 0xD0 });                                              // call rax

                IntPtr paramAddress = IntPtr.Add(ptrParams, paramBytesList.Count);

                codeBytesList.AddRange(new byte[] { 0x48, 0xB9 });                                              // mov rcx,(paramAddress)
                    codeBytesList.AddRange(BitConverter.GetBytes((ulong)paramAddress));

                codeBytesList.AddRange(new byte[] { 0x48, 0x89,0x01 });                                         // mov qword ptr ds:[rcx],rax


                codeBytesList.Add(                  0xC9                    );                                  // leave                                 | Restores stack pointer and pops RBP from stack
                codeBytesList.Add(                  0xC3                    );                                  // ret                                   | Return
            }

            var codeBytes = codeBytesList.ToArray();
            var paramBytes = paramBytesList.ToArray();

            // This is where the parameters go, those don't need execute permissions
            if (WriteProcessMemory(hProcess, ptrParams, paramBytes, (uint)paramBytes.Length, out uint bytesWritten) == false || bytesWritten != paramBytes.Length)
            {
                VirtualFreeEx(hProcess, ptrParams, 0, MEM_RELEASE);
                CloseHandle(hProcess);
                return IntPtr.Zero;
            }

            // This is where the code goes
            if (WriteProcessMemory(hProcess, ptrCode, codeBytes, (uint)codeBytes.Length, out uint bytesWritten2) == false || bytesWritten2 != codeBytes.Length)
            {
                VirtualFreeEx(hProcess, ptrCode, 0, MEM_RELEASE);
                CloseHandle(hProcess);
                return IntPtr.Zero;
            }

            VirtualProtectEx(hProcess, ptrCode, (uint)codeBytes.Length, PAGE_EXECUTE_READ, out uint oldProtect); // Yay execute read

            var hModule = CreateAndRunThread(hProcess, ptrCode, ptrParams);

            //var outBytes = new byte[8];

            //var teste = ReadProcessMemory(hProcess, paramAddress, outBytes, 8, out int bytesRead); // TODO: yay, it works, use it someplace now

            VirtualFreeEx(hProcess, ptrParams, 0, MEM_RELEASE); // TODO: It's possible that the pointers passed on as parameters might still be used somewhere else in the program depending
                                                                // on what function you call. Not entirely sure. But in that case, since the memory is freed, uhh, unspecified behavior

            VirtualFreeEx(hProcess, ptrCode, 0, MEM_RELEASE);

            CloseHandle(hProcess);

            return hModule;
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