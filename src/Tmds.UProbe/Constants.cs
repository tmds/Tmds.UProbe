// This software is made available under the MIT License
// See LICENSE for details

using System;

namespace Tmds.UProbe
{
    static class TraceConstants
    {
        public static ReadOnlySpan<byte> SpaceArgNamePrefix => new byte[] { (byte)' ', (byte)'_', (byte)'a' };
        public static ReadOnlySpan<byte> ArgNameSuffix => new byte[] { (byte)'r', (byte)'g', (byte)'=' };
    }
}