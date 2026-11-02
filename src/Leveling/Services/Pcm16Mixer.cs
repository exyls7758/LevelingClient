using System;
using System.Collections.Generic;

namespace Multiverse.Services
{
    internal static class Pcm16Mixer
    {
        internal static void Mix(
            IReadOnlyList<short[]> sources,
            Span<short> destination)
        {
            destination.Clear();
            if (sources.Count == 0)
                return;

            for (int sampleIndex = 0; sampleIndex < destination.Length; sampleIndex++)
            {
                int sum = 0;
                for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
                {
                    short[] source = sources[sourceIndex];
                    if (sampleIndex < source.Length)
                        sum += source[sampleIndex];
                }

                destination[sampleIndex] = (short)Math.Clamp(
                    sum,
                    short.MinValue,
                    short.MaxValue);
            }
        }
    }
}
