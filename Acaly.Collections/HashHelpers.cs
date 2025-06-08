using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Acaly.Collections
{
    internal static class HashHelpers
    {
        public static ulong GetFastModMultiplier(uint divisor)
        {
            return ulong.MaxValue / divisor + 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint FastMod(uint value, uint divisor, ulong multiplier)
        {
            Debug.Assert(divisor <= int.MaxValue);
            uint highbits = (uint)(((((multiplier * value) >> 32) + 1) * divisor) >> 32);
            Debug.Assert(highbits == value % divisor);
            return highbits;
        }
    }
}
