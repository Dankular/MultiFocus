using System.Collections.Generic;
using System;
using System.Text;

namespace SharpestInjector
{
    public static class Constants
    {
        private const string LoadLibraryName = "LoadLibraryW";
        private const string FreeLibraryName = "FreeLibraryAndExitThread";

        public static readonly int LoadLibrary;
        public static readonly int LoadLibrary32;

        public static readonly int FreeLibrary;
        public static readonly int FreeLibrary32;

        public static readonly bool Is64Bit = (IntPtr.Size == 8); // The best 64-bit detection out there

        public static readonly Encoding AnsiEncoding = Encoding.GetEncoding(1252);

        static Constants()
        {
            var kernel = PeFile.Parse($@"{Environment.GetFolderPath(Environment.SpecialFolder.System)}\kernel32.dll");
            LoadLibrary = kernel.GetExportAddress(LoadLibraryName);
            FreeLibrary = kernel.GetExportAddress(FreeLibraryName);

            if (Is64Bit) 
            {
                var kernel32 = PeFile.Parse($@"{Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)}\kernel32.dll");
                LoadLibrary32 = kernel32.GetExportAddress(LoadLibraryName);
                FreeLibrary32 = kernel32.GetExportAddress(FreeLibraryName);
            }
        }
    }

    public class ModuleInfo
    {
        public string Path;
        public long Size;
        public IntPtr MemoryAddress;

        public override string ToString()
        {
            return Path;
        }
    }

    public class ProcessInfo
    {
        public string FileName;
        public string WindowTitle;
        public IntPtr WindowHandle;
        public List<ChildWindow> ChildWindows;
        public uint Id;
        public Dictionary<string, ModuleInfo> Modules = new Dictionary<string, ModuleInfo>();
        public IntPtr Kernel32;
        public bool IsWOW64;

        public bool Is64Bit => Constants.Is64Bit != IsWOW64;

        public override string ToString()
        {
            return $"{(string.IsNullOrEmpty(WindowTitle) ? "" : WindowTitle + " ")}[{FileName}]{(IsWOW64 ? " (32 bit)" : "")}";
        }
    }

}