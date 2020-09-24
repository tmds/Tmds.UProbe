// This software is made available under the MIT License
// See LICENSE for details

namespace Tmds.UProbe
{
    public struct FetchArg
    {
        private string _arg;

        public static FetchArg MemoryAt(ulong address)
            => new FetchArg($"@{address}");

        public static FetchArg MemoryAt(FetchArg arg, long offset = 0)
            => new FetchArg($"+{offset}({arg})");

        public static FetchArg Register(string name)
            => new FetchArg($"%{name}");

        public static FetchArg ReturnValue
            => new FetchArg("$retval");

        public static FetchArg Stack
            => new FetchArg("$stack");

        public static FetchArg Comm
            => new FetchArg("$comm");

        public static FetchArg StackEntry(int index)
            => new FetchArg($"$stack{index}");

        public FetchArg AsString()
            => new FetchArg($"{_arg}:string");

        // TODO: add more types.

        public FetchArg(string arg)
        {
            _arg = arg;
        }

        public override string ToString()
            => _arg;
    }
}