using System;
using System.Collections.Generic;

namespace CeeFind.Utils
{
    /// <summary>
    /// A compact membership sketch over the character trigrams of the filenames in a
    /// directory subtree.
    ///
    /// Answers one question: does a name containing this literal appear to exist below
    /// here? It is only ever used to order the queue, never to prune it - everything is
    /// still scanned eventually, because anything else could miss a result. So the sketch
    /// does not have to be complete or current. Built from whatever has been observed so
    /// far, a gap or a stale entry costs a directory its place in the queue and nothing
    /// more.
    ///
    /// Only worth carrying for subtrees large enough that scanning them is expensive.
    /// Measured over real filenames, distinct trigrams run at one to two percent of file
    /// count - 200,000 files yielded 8,522 - so these stay small and precise.
    ///
    /// Fixed width, so two sketches for the same subtree merge with a bitwise or and can
    /// be extended as more of the tree is seen.
    /// </summary>
    internal sealed class TrigramFilter
    {
        private const int HashCount = 3;

        /// <summary>
        /// 128k bits. Holds the largest subtree observed (17,508 trigrams) at under four
        /// percent false positives, and the handful of subtrees big enough to warrant one
        /// makes the total negligible.
        /// </summary>
        internal const int FilterBytes = 16 * 1024;

        private readonly byte[] bits;

        private TrigramFilter(byte[] bits)
        {
            this.bits = bits;
        }

        internal int SizeInBytes => bits.Length;

        internal static TrigramFilter Build(IEnumerable<string> names)
        {
            byte[] bits = new byte[FilterBytes];
            int bitCount = FilterBytes * 8;

            HashSet<int> trigrams = new HashSet<int>();
            foreach (string name in names)
            {
                CollectTrigrams(name, trigrams);
            }

            foreach (int trigram in trigrams)
            {
                Set(bits, bitCount, trigram);
            }

            return new TrigramFilter(bits);
        }

        internal static TrigramFilter FromTrigrams(IEnumerable<int> trigrams, byte[] existing)
        {
            byte[] bits = existing != null && existing.Length == FilterBytes
                ? (byte[])existing.Clone()
                : new byte[FilterBytes];

            int bitCount = FilterBytes * 8;
            foreach (int trigram in trigrams)
            {
                Set(bits, bitCount, trigram);
            }

            return new TrigramFilter(bits);
        }

        internal static IEnumerable<int> TrigramsOf(string value)
        {
            HashSet<int> set = new HashSet<int>();
            CollectTrigrams(value, set);
            return set;
        }

        internal static TrigramFilter FromBytes(byte[] raw)
        {
            return raw == null || raw.Length != FilterBytes ? null : new TrigramFilter(raw);
        }

        internal byte[] ToBytes()
        {
            return bits;
        }

        /// <summary>
        /// False when no name observed so far beneath this directory contains one of the
        /// required literals. A reason to look elsewhere first, not a reason to skip.
        /// </summary>
        internal bool MightContainAll(IReadOnlyList<string> requiredLiterals)
        {
            if (requiredLiterals == null || requiredLiterals.Count == 0)
            {
                return true;
            }

            int bitCount = bits.Length * 8;

            foreach (string literal in requiredLiterals)
            {
                HashSet<int> trigrams = new HashSet<int>();
                CollectTrigrams(literal, trigrams);

                // Literals shorter than a trigram carry no evidence either way.
                if (trigrams.Count == 0)
                {
                    continue;
                }

                foreach (int trigram in trigrams)
                {
                    if (!IsSet(bits, bitCount, trigram))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void CollectTrigrams(string value, HashSet<int> into)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 3)
            {
                return;
            }

            for (int i = 0; i + 2 < value.Length; i++)
            {
                int h = (char.ToLowerInvariant(value[i]) << 16)
                      ^ (char.ToLowerInvariant(value[i + 1]) << 8)
                      ^ char.ToLowerInvariant(value[i + 2]);
                into.Add(h);
            }
        }

        private static void Set(byte[] bits, int bitCount, int trigram)
        {
            foreach (int index in Indexes(trigram, bitCount))
            {
                bits[index >> 3] |= (byte)(1 << (index & 7));
            }
        }

        private static bool IsSet(byte[] bits, int bitCount, int trigram)
        {
            foreach (int index in Indexes(trigram, bitCount))
            {
                if ((bits[index >> 3] & (1 << (index & 7))) == 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<int> Indexes(int trigram, int bitCount)
        {
            // Double hashing: two independent mixes generate k positions cheaply.
            uint h1 = Mix((uint)trigram);
            uint h2 = Mix(h1 ^ 0x9E3779B9u) | 1u;

            for (int i = 0; i < HashCount; i++)
            {
                yield return (int)(((h1 + (uint)i * h2) % (uint)bitCount));
            }
        }

        private static uint Mix(uint x)
        {
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }
    }
}
