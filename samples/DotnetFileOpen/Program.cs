using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.UProbe;

namespace DotnetFileOpen
{
    // https://github.com/dotnet/runtime/blob/master/src/libraries/Common/src/Interop/Unix/System.Native/Interop.OpenFlags.cs
    [Flags]
    internal enum OpenFlags
    {
        // Access modes (mutually exclusive)
        O_RDONLY = 0x0000,
        O_WRONLY = 0x0001,
        O_RDWR   = 0x0002,

        // Flags (combinable)
        O_CLOEXEC = 0x0010,
        O_CREAT   = 0x0020,
        O_EXCL    = 0x0040,
        O_TRUNC   = 0x0080,
        O_SYNC    = 0x0100,
    }

    class Program
    {
        const string OpenEvent = "Open";
        const string OpenRetEvent = "OpenRet";
        const string LibSystemNative = "/usr/lib64/dotnet/shared/Microsoft.NETCore.App/3.1.5/System.Native.so";
        const string SystemNative_Open = "SystemNative_Open";

        static FetchArg X64_Arg1 => FetchArg.Register("di");
        static FetchArg X64_Arg2 => FetchArg.Register("si");

        static async Task Main(string[] args)
        {
            // Track what thread is opening a file, to match it with the return value.
            Dictionary<long, (string, OpenFlags)> opening = new Dictionary<long, (string, OpenFlags)>();

            using var session = new UProbeSession("dotnetfileopen"); // system wide name that identifies probes.

            // Probe SystemNative_Open call and return.
            //    [DllImport(Libraries.SystemNative, EntryPoint = "SystemNative_Open", SetLastError = true)]
            //    internal static extern SafeFileHandle Open(string filename, OpenFlags flags, int mode);
            session.AddProbe(OpenEvent, LibSystemNative, SystemNative_Open,
                new[] { FetchArg.MemoryAt(X64_Arg1).AsString(), // filename
                        X64_Arg2 });                               // OpenFlags
            session.AddReturnProbe(OpenRetEvent, LibSystemNative, SystemNative_Open,
                new[] { FetchArg.ReturnValue });                   // fd

            session.Enable();

            await foreach(var entry in session.GetEntriesAsync())
            {
                int tid = entry.GetTid();

                if (entry.IsEvent(OpenEvent))
                {
                    string filename = entry.GetStringArg(0);
                    OpenFlags flags = (OpenFlags)entry.GetLongArg(1);
                    flags &= (OpenFlags.O_WRONLY | OpenFlags.O_RDWR); // only keep access flags

                    opening.Add(tid, (filename, flags));
                }
                else if (entry.IsEvent(OpenRetEvent))
                {
                    long fd = entry.GetLongArg(0);

                    if (fd != -1 && // Open didn't fail.
                        opening.TryGetValue(tid, out (string filename, OpenFlags flags) v))
                    {
                        Console.WriteLine($"Thread {tid} opened {v.filename} as {v.flags}");
                    }
                    opening.Remove(tid);
                }
            }
        }
    }
}
