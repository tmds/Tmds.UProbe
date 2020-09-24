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
