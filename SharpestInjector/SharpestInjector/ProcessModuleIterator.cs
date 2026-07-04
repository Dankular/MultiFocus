using static SharpestInjector.PInvoke;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Collections;
using System.Threading;
using System.Text;
using System;

namespace SharpestInjector
{
    public class ProcessModuleIterator : Iterator<ModuleInfo>
    {
        private const int DEFAULT_MODULE_COUNT = 1024;

        readonly IntPtr _processHandle;
        readonly uint _moduleCount;

        GCHandle _gcHandle;
        IntPtr[] _arrModules;
        IntPtr _moduleArrayPointer;

        uint _currIndex = 0;

        public ProcessModuleIterator(IntPtr processHandle)
        {
            _processHandle = processHandle;

            if(processHandle == IntPtr.Zero)
            {
                return;
            }

            _moduleCount = EnumModules(DEFAULT_MODULE_COUNT);

            if (_moduleCount <= DEFAULT_MODULE_COUNT)
                return;

            _moduleCount = EnumModules(_moduleCount); // Retry again with a bigger module array

            if (_moduleCount > _arrModules.LongLength) // If we still need more
                _moduleCount = (uint)_arrModules.LongLength; // I uhh, no recursion
        }

        private uint EnumModules(uint moduleCount)
        {
            // Setting up the parameters for EnumProcessModules
            uint uiSize = InitModuleArray(moduleCount);

            if (EnumProcessModulesEx(_processHandle, _moduleArrayPointer, uiSize, out var cbNeeded, LIST_MODULES_ALL) == false)
                return 0;

            return cbNeeded / (uint)Marshal.SizeOf(typeof(IntPtr));
        }

        private uint InitModuleArray(uint moduleArraySize)
        {
            if(_arrModules != null)
                _gcHandle.Free();

            // Setting up the array to 
            _arrModules = new IntPtr[moduleArraySize];

            _gcHandle = GCHandle.Alloc(_arrModules, GCHandleType.Pinned); // Don't forget to free this later
            _moduleArrayPointer = _gcHandle.AddrOfPinnedObject();

            // Setting up the rest of the parameters for EnumProcessModules
            return (uint)(Marshal.SizeOf(typeof(IntPtr)) * _arrModules.LongLength);
        }

        public override bool MoveNext()
        {
            if (_processHandle == IntPtr.Zero)
                return false;

            while (_currIndex < _moduleCount)
            {
                var moduleAddress = _arrModules[_currIndex];
                _currIndex++;

                var pathStringBuilder = new StringBuilder((int)MAX_PATH);

                if (GetMappedFileNameW(_processHandle, moduleAddress, pathStringBuilder, MAX_PATH) == false) // I have to use this instead of GetModuleFileName because Microsoft broke a thing
                    continue;

                var path = $@"\\.\globalroot{pathStringBuilder}"; // Mmm, NT paths
                var fileHandle = CreateFileW(path, 0, 0, 0, OPEN_EXISTING, 0, IntPtr.Zero);

                if (fileHandle == INVALID_HANDLE_VALUE)
                    continue;

                pathStringBuilder.Clear();
                var stringLength = GetFinalPathNameByHandleW(fileHandle, pathStringBuilder, MAX_PATH, VOLUME_NAME_DOS);
                var gotFileSize = GetFileSizeEx(fileHandle, out long fileSize);
                CloseHandle(fileHandle);

                if (stringLength > MAX_PATH)
                    throw new Exception("How the hell did you get a longer path");

                if (gotFileSize == false)
                    continue;

                path = pathStringBuilder.ToString().Substring(4); // Remove the \\?\ from path string

                current = new ModuleInfo() { Path = path, Size = fileSize, MemoryAddress = moduleAddress }; 
                return true;
            }

            return false;
        }

        protected override Iterator<ModuleInfo> Clone()
        {
            return new ProcessModuleIterator(_processHandle);
        }

        protected override void Dispose(bool disposing)
        {
            if (_processHandle != IntPtr.Zero)
                _gcHandle.Free(); // Must free the GCHandle

            base.Dispose(disposing);
        }
    }

    // TODO: don't publish 

    // Abstract Iterator, borrowed from FileSystemEnumerable. Used in anticipation of whatever
    // Microsoft comes up with next
    abstract public class Iterator<TSource> : IEnumerable<TSource>, IEnumerator<TSource>
    {
        int threadId;
        internal int state;
        internal TSource current;

        public Iterator()
        {
            threadId = Thread.CurrentThread.ManagedThreadId;
        }

        public TSource Current
        {
            get { return current; }
        }

        protected abstract Iterator<TSource> Clone();

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            current = default;
            state = -1;
        }

        public IEnumerator<TSource> GetEnumerator()
        {
            if (threadId == Thread.CurrentThread.ManagedThreadId && state == 0)
            {
                state = 1;
                return this;
            }

            Iterator<TSource> duplicate = Clone();
            duplicate.state = 1;
            return duplicate;
        }

        public abstract bool MoveNext();

        object IEnumerator.Current
        {
            get { return Current; }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        void IEnumerator.Reset()
        {
            throw new NotSupportedException();
        }

    }
}
