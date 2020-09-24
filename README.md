# Tmds.UProbe

This library allows to use the [Linux uprobe-tracer](https://www.kernel.org/doc/html/latest/trace/uprobetracer.html) from .NET.

## Example

The following sample traces the lines that are read by all 'bash' instances running on the system.
You need to run the code as `root`.

```cs
using System;
using System.Threading.Tasks;
using Tmds.UProbe;

namespace BashReadline
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var session = new UProbeSession("bashreadline");

            session.AddReturnProbe("myprobe", "/usr/bin/bash", "readline", new [] { FetchArg.MemoryAt(FetchArg.ReturnValue).AsString() });

            session.Enable();

            await foreach(var entry in session.GetEntriesAsync())
            {
                int tid = entry.GetTid();
                string line = entry.GetStringArg(0);
                Console.WriteLine($"bash [{tid}]: {line}");
            }

            session.Dispose();
        }
    }
}

```