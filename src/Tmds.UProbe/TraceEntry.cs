// This software is made available under the MIT License
// See LICENSE for details

using System;
using System.Text;
using System.Buffers;
using System.Buffers.Text;

namespace Tmds.UProbe
{
    struct ArgPosition
    {
        public int Offset;
        public int Length;
    }

    public class TraceEntry
    {
        private readonly ArgPosition[] _argPositions; // Tracks position of arguments in the _body.
        private byte[] _header;               // Contains data before function name:              zsh-24842 [006] 258544.995456
        private ReadOnlySequence<byte> _body; // Contains data starting with function name, e.g.: zfree_entry: (0x446420) arg1=446420 arg2=79

        internal TraceEntry(ArgPosition[] argPositions)
        {
            for (int i = 0; i < argPositions.Length; i++)
            {
                argPositions[i] = new ArgPosition { Offset = -1, Length = -1 };
            }
            _argPositions = argPositions;
        }

        internal void Init(byte[] header, ReadOnlySequence<byte> body)
        {
            _header = header;
            _body = body;
            for (int i = 0; i < _argPositions.Length && _argPositions[i].Offset != -1; i++)
            {
                _argPositions[i] = new ArgPosition { Offset = -1, Length = -1 };
            }
        }

        public int GetTid()
        {
            if (!Utf8Parser.TryParse(_header.AsSpan(17), out int value, out _))
            {
                ThrowFormatException();
            }
            return value;
        }

        public string Event
        {
            get
            {
                ReadOnlySequence<byte> data = _body;
                SequencePosition? position = data.PositionOf((byte)':');
                data = data.Slice(0, position.Value);
                ReadOnlySpan<byte> span = data.IsSingleSegment ? data.FirstSpan : data.ToArray();
                return Encoding.UTF8.GetString(span);
            }
        }

        public bool IsEvent(ReadOnlySpan<char> name)
        {
            Span<byte> bytes = stackalloc byte[512]; // MAYDO, handle larger names
            int bytesWritten = Encoding.UTF8.GetBytes(name, bytes); // TODO this throws ArgumentException
            bytes[bytesWritten++] = (byte)':';
            bytes = bytes.Slice(0, bytesWritten);
            ReadOnlySequence<byte> data = _body;
            if (data.IsSingleSegment)
            {
                return data.FirstSpan.StartsWith(bytes);
            }
            else
            {
                int matchedSoFar = 0;
                foreach (ReadOnlyMemory<byte> memory in data)
                {
                    ReadOnlySpan<byte> span = memory.Span;

                    if (bytes.Slice(matchedSoFar).StartsWith(span))
                    {
                        matchedSoFar += span.Length;
                    }
                    else
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        public string GetStringArg(int index)
        {
            ReadOnlySpan<byte> span = GetArgSpan(index);

            // Trim \" of string arguments.
            if (span.Length > 1 && span[0] == '\"' && span[span.Length - 1] == '\"')
            {
                span = span.Slice(1, span.Length - 2);
            }

            return Encoding.UTF8.GetString(span);
        }

        public long GetLongArg(int index)
        {
            ReadOnlySpan<byte> span = GetArgSpan(index);

            char standardFormat = '\0';
            if (span.Length > 2 && span[0] == (byte)'0' && span[1] == (byte)'x')
            {
                standardFormat = 'x';
            }

            if (!Utf8Parser.TryParse(span.Slice(2), out long value, out _, standardFormat))
            {
                ThrowFormatException();
            }

            return value;
        }

        private ReadOnlySpan<byte> GetArgSpan(int index)
        {
            ReadOnlySequence<byte> sequence = GetArgSequence(index);
            return sequence.IsSingleSegment ? sequence.FirstSpan : sequence.ToArray();
        }

        private ReadOnlySequence<byte> GetArgSequence(int index)
        {
            ArgPosition position = _argPositions[index];
            if (position.Offset == -1)
            {
                int lastKnown = index;
                while (lastKnown >= 0 && _argPositions[lastKnown].Offset == -1)
                {
                    lastKnown--;
                }
                var reader = new SequenceReader<byte>(_body);
                if (lastKnown != -1)
                {
                    reader.Advance(_argPositions[lastKnown].Offset + _argPositions[lastKnown].Length);
                }
                for (int i = lastKnown + 1; i <= index; i++)
                {
                    if (reader.TryReadTo(out ReadOnlySequence<byte> _, TraceConstants.ArgNameSuffix))
                    {
                        int offset = (int)reader.Consumed;
                        _argPositions[i].Offset = offset;
                        if (reader.TryReadTo(out ReadOnlySequence<byte> _, TraceConstants.SpaceArgNamePrefix))
                        {
                            _argPositions[i].Length = (int)reader.Consumed - offset - TraceConstants.SpaceArgNamePrefix.Length;
                        }
                        else
                        {
                            _argPositions[i].Length = (int)reader.Remaining;
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
                position = _argPositions[index];
            }
            return _body.Slice(position.Offset, position.Length);
        }

        private void ThrowFormatException()
        {
            throw new FormatException();
        }
    }
}