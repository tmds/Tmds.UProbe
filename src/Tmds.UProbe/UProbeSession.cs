// This software is made available under the MIT License
// See LICENSE for details

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Runtime.CompilerServices;
using LibObjectFile.Elf;

namespace Tmds.UProbe
{
    public class UProbeSession : IDisposable
    {
        const string UProbeEventsPath = "/sys/kernel/debug/tracing/uprobe_events";
        const string UProbesPath = "/sys/kernel/debug/tracing/events/uprobes";

        private static string ProbeEnablePath(string session, string ev)
            => $"{UProbesPath}/{session}{ev}/enable";

        private static int s_sessionIdentifier = 0;

        public UProbeSession(string name)
        {
            _sessionName = name;
            int id = Interlocked.Increment(ref s_sessionIdentifier);
            int pid = Process.GetCurrentProcess().Id;
            _sessionPrefix = $"{name}_{pid}_{id}_";
        }

        struct Probe
        {
            public string Event;
            public string Path;
            public string Symbol;
            public FetchArg[] FetchArgs;
            public bool IsReturnProbe;
        }

        private readonly List<Probe> _probes = new List<Probe>();
        private string _sessionName;
        private string _sessionPrefix = string.Empty;
        private TraceReaderThread _readerThread;
        private CancellationTokenRegistration _ctr;

        public void AddProbe(string ev, string path, string symbol, FetchArg[] fetchArguments)
        {
            _probes.Add(new Probe
            {
                Event = ev,
                Path = path,
                Symbol = symbol,
                FetchArgs = fetchArguments,
                IsReturnProbe = false
            });
        }

        public void AddReturnProbe(string ev, string path, string symbol, FetchArg[] fetchArguments)
        {
            _probes.Add(new Probe
            {
                Event = ev,
                Path = path,
                Symbol = symbol,
                FetchArgs = fetchArguments,
                IsReturnProbe = true
            });
        }

        public void Enable()
        {
            RemovePreviousProbes();

            DefineProbes();
            EnableProbes();

            _readerThread = new TraceReaderThread(_sessionPrefix, _probes.Select(p => p.FetchArgs.Length).Max());
            _readerThread.Start();
        }

        public void Dispose()
        {
            _ctr.Dispose();
            _readerThread?.Stop();
            List<string> probeNames = _probes.Select(s => s.Event).ToList();
            DisableProbes(_sessionPrefix, probeNames);
            RemoveProbes(_sessionPrefix, probeNames);
        }

        public IAsyncEnumerable<TraceEntry> GetEntriesAsync([EnumeratorCancellation]CancellationToken ct = default)
        {
            _ctr = ct.Register(o => ((TraceReaderThread)o).Stop(), _readerThread);
            return _readerThread.GetEntries();
        }

        private void RemovePreviousProbes()
        {
            if (Directory.Exists(UProbesPath))
            {
                List<string> probeNames = Directory.EnumerateDirectories(UProbesPath, $"{_sessionName}_*")
                                                    .Select(path => Path.GetFileName(path))
                                                    .ToList();
                DisableProbes("", probeNames);
                RemoveProbes("", probeNames);
            }
        }

        private void DefineProbes()
        {
            Dictionary<string, ElfObjectFile> objectFiles = new Dictionary<string, ElfObjectFile>();
            using FileStream uprobeEventsFile = File.OpenWrite(UProbeEventsPath);
            Span<byte> buffer = stackalloc byte[4096];
            foreach (var probe in _probes)
            {
                SpanWriter writer = new SpanWriter(buffer);
                writer.WriteAsciiChar(probe.IsReturnProbe ? 'r' : 'p');
                writer.WriteAsciiChar(':');
                writer.WriteString(_sessionPrefix);
                writer.WriteString(probe.Event);
                writer.WriteAsciiChar(' ');
                writer.WriteString(probe.Path);
                writer.WriteAsciiChar(':');
                if (!objectFiles.TryGetValue(probe.Path, out ElfObjectFile elfFile))
                {
                    using Stream file = File.OpenRead(probe.Path);
                    elfFile = ElfObjectFile.Read(file);
                    objectFiles.Add(probe.Path, elfFile);
                }
                var symbolTable = elfFile.Sections.FirstOrDefault(s => s.Type == ElfSectionType.DynamicLinkerSymbolTable) as ElfSymbolTable;
                var symbol = symbolTable.Entries.FirstOrDefault(s => s.Name == probe.Symbol);
                ulong offset = symbol.Value;
                writer.WriteAsciiChar('0');
                writer.WriteAsciiChar('x');
                writer.WriteHexInt(offset);
                int argIdx = 0;
                foreach (FetchArg arg in probe.FetchArgs)
                {
                    writer.WriteSpan(TraceConstants.SpaceArgNamePrefix);
                    writer.WriteInt32(argIdx); // argument names must be unique.
                    writer.WriteSpan(TraceConstants.ArgNameSuffix);
                    writer.WriteString(arg.ToString());
                    argIdx++;
                }
                writer.WriteAsciiChar('\n');
                uprobeEventsFile.Write(buffer.Slice(0, writer.Length));
            }
        }

        private void EnableProbes()
        {
            foreach (var probe in _probes)
            {
                File.WriteAllText(ProbeEnablePath(_sessionPrefix, probe.Event), "1");
            }
        }

        private static void DisableProbes(string sessionPrefix, List<string> probeNames)
        {
            foreach (var probeName in probeNames)
            {
                try
                {
                    File.WriteAllText(ProbeEnablePath(sessionPrefix, probeName), "0");
                }
                catch
                { }
            }
        }

        private static void RemoveProbes(string sessionPrefix, List<string> probeNames)
        {
            using FileStream uprobeEventsFile = File.OpenWrite(UProbeEventsPath);
            Span<byte> buffer = stackalloc byte[4096];
            foreach (var probeName in probeNames)
            {
                SpanWriter writer = new SpanWriter(buffer);
                writer.WriteAsciiChar('-');
                writer.WriteAsciiChar(':');
                writer.WriteString(sessionPrefix);
                writer.WriteString(probeName);
                writer.WriteAsciiChar('\n');
                try
                {
                    uprobeEventsFile.Write(buffer.Slice(0, writer.Length));
                }
                catch
                { }
            }
        }

        private ref struct SpanWriter
        {
            private Span<byte> _span;
            private int _offset;

            public int Length => _offset;

            public SpanWriter(Span<byte> span)
            {
                _span = span;
                _offset = 0;
            }

            public void WriteAsciiChar(char c)
            {
                _span[_offset] = (byte)c;
                _offset++;
            }

            public void WriteString(string s)
            {
                int encodedBytes = Encoding.UTF8.GetBytes(s, _span.Slice(_offset));
                _offset += encodedBytes;
            }

            public void WriteInt32(int value)
            {
                if (!Utf8Formatter.TryFormat(value, _span.Slice(_offset), out int bytesWritten))
                {
                    ThrowBufferTooSmall();
                }
                _offset += bytesWritten;
            }

            public void WriteSpan(ReadOnlySpan<byte> span)
            {
                span.CopyTo(_span.Slice(_offset));
                _offset += span.Length;
            }

            public void WriteHexInt(ulong value)
            {
                if (!Utf8Formatter.TryFormat(value, _span.Slice(_offset), out int bytesWritten, new StandardFormat('X')))
                {
                    ThrowBufferTooSmall();
                }
                _offset += bytesWritten;
            }

            private static void ThrowBufferTooSmall()
            {
                throw new IndexOutOfRangeException();
            }
        }
    }
}