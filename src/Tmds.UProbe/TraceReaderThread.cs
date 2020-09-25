// This software is made available under the MIT License
// See LICENSE for details

using System;
using System.Buffers;
using System.Threading;
using System.IO;
using System.IO.Pipelines;
using System.Collections.Generic;
using System.Text;

namespace Tmds.UProbe
{
    class TraceReaderThread
    {
        const string TracePipePath = "/sys/kernel/debug/tracing/trace_pipe";

        private readonly Pipe _pipe = new Pipe();
        private FileStream _eventFile;
        private Thread _thread;
        private readonly byte[] _sessionPrefix;
        private readonly byte[] _headerBuffer;
        private readonly TraceEntry _entry;
        private volatile bool _isStopped;

        public TraceReaderThread(string sessionPrefix, int maxArgs)
        {
            _entry = new TraceEntry(new ArgPosition[maxArgs]);
            _headerBuffer = new byte[64];
            _sessionPrefix = Encoding.UTF8.GetBytes(sessionPrefix);
        }

        public void Start()
        {
            _eventFile = File.OpenRead(TracePipePath);
            _thread = new Thread(ReadEvents);
            _thread.IsBackground = true;
            _thread.Start();
        }

        public void Stop()
        {
            _isStopped = true;
            _pipe.Writer.Complete();
        }

        private async void ReadEvents(object o)
        {
            const int MinimumBufferSize = 512;
            PipeWriter writer = _pipe.Writer;
            while (true)
            {
                Memory<byte> memory = writer.GetMemory(MinimumBufferSize);
                int bytesRead = _eventFile.Read(memory.Span); // TODO: Stop should cause this to return.
                if (_isStopped)
                {
                    break;
                }
                writer.Advance(bytesRead);
                await writer.FlushAsync();
            }
        }

        public async IAsyncEnumerable<TraceEntry> GetEntries()
        {
            var reader = _pipe.Reader;
            while (true)
            {
                ReadResult result = await reader.ReadAsync();
                if (result.IsCompleted)
                {
                    break;
                }
                ReadOnlySequence<byte> buffer = result.Buffer;
                while (TryReadLine(ref buffer, out ReadOnlySequence<byte> line))
                {
                    if (TryReadSessionEvent(ref line, _headerBuffer, out ReadOnlySequence<byte> body))
                    {
                        _entry.Init(_headerBuffer, body);
                        yield return _entry;
                    }
                }
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }

        private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
        {
            SequencePosition? position = buffer.PositionOf((byte)'\n');

            if (position == null)
            {
                line = default;
                return false;
            }

            line = buffer.Slice(0, position.Value);
            buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
            return true;
        }

        private bool TryReadSessionEvent(ref ReadOnlySequence<byte> line, byte[] header, out ReadOnlySequence<byte> body)
        {
            var reader = new SequenceReader<byte>(line);
            if (reader.TryReadTo(out ReadOnlySequence<byte> lineStart, _sessionPrefix))
            {
                body = reader.Sequence.Slice(reader.Position);
                int headerLength = (int)reader.Consumed - _sessionPrefix.Length;
                lineStart.CopyTo(header);
                if (lineStart.Length < header.Length)
                {
                    header[lineStart.Length] = (byte)' ';
                }
                return true;
            }
            else
            {
                body = default;
                return false;
            }
        }
    }
}