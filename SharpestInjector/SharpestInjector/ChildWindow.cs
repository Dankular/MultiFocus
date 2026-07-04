using System.Collections.Generic;
using System;

namespace SharpestInjector
{
    public class ChildWindow
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; }
        public List<ChildWindow> Children { get; set; }
    }
}