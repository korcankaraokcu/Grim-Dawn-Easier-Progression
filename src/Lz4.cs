using System;
using System.IO;

namespace EasierProgression
{
    /// <summary>LZ4 block format (no frame header), as used for .arz record data.</summary>
    public static class Lz4
    {
        public static byte[] Decompress(byte[] src, int start, int length, int rawSize)
        {
            var dst = new byte[rawSize];
            int ip = start, end = start + length, op = 0;
            while (ip < end)
            {
                int token = src[ip++];
                int lit = token >> 4;
                if (lit == 15)
                {
                    int b;
                    do { b = src[ip++]; lit += b; } while (b == 255);
                }
                if (op + lit > rawSize || ip + lit > end) throw new InvalidDataException("corrupt LZ4 block");
                Buffer.BlockCopy(src, ip, dst, op, lit);
                ip += lit;
                op += lit;
                if (ip >= end) break; // last sequence has literals only

                int offset = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                int len = token & 15;
                if (len == 15)
                {
                    int b;
                    do { b = src[ip++]; len += b; } while (b == 255);
                }
                len += 4;
                int m = op - offset;
                if (offset == 0 || m < 0 || op + len > rawSize) throw new InvalidDataException("corrupt LZ4 block");
                // Byte by byte: matches may overlap their own output.
                for (int i = 0; i < len; i++) dst[op++] = dst[m++];
            }
            if (op != rawSize) throw new InvalidDataException("LZ4 size mismatch");
            return dst;
        }

        const int MinMatch = 4, LastLiterals = 5, MfLimit = 12, HashBits = 16, MaxOffset = 65535;

        // Hash table reused across calls. Entries left over from earlier inputs are harmless: a candidate
        // is only used if it lies before the current position and its 4 bytes really match.
        [ThreadStatic] static int[] table;

        /// <summary>Greedy single-candidate compressor (like LZ4's fast mode). Output is valid LZ4.</summary>
        public static byte[] Compress(byte[] src, int n)
        {
            var dst = new byte[n + n / 255 + 16];
            int op = 0, anchor = 0;
            if (n > MfLimit)
            {
                if (table == null) table = new int[1 << HashBits]; // position + 1; 0 = empty
                int i = 0, mfLimit = n - MfLimit, matchLimit = n - LastLiterals;
                while (i < mfLimit)
                {
                    uint seq = Read32(src, i);
                    int h = (int)((seq * 2654435761u) >> (32 - HashBits));
                    int cand = table[h] - 1;
                    table[h] = i + 1;
                    if (cand < 0 || cand >= i || i - cand > MaxOffset || Read32(src, cand) != seq)
                    {
                        i++;
                        continue;
                    }
                    while (i > anchor && cand > 0 && src[i - 1] == src[cand - 1]) { i--; cand--; }
                    int len = MinMatch;
                    while (i + len < matchLimit && src[i + len] == src[cand + len]) len++;
                    op = Sequence(dst, op, src, anchor, i - anchor, i - cand, len);
                    i += len;
                    anchor = i;
                }
            }
            op = Literals(dst, op, src, anchor, n - anchor);
            Array.Resize(ref dst, op);
            return dst;
        }

        static uint Read32(byte[] b, int i) =>
            (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));

        static int Sequence(byte[] dst, int op, byte[] src, int litStart, int lit, int offset, int len)
        {
            int ml = len - MinMatch;
            dst[op++] = (byte)((Math.Min(lit, 15) << 4) | Math.Min(ml, 15));
            op = Length(dst, op, lit);
            Buffer.BlockCopy(src, litStart, dst, op, lit);
            op += lit;
            dst[op++] = (byte)offset;
            dst[op++] = (byte)(offset >> 8);
            return Length(dst, op, ml);
        }

        static int Literals(byte[] dst, int op, byte[] src, int litStart, int lit)
        {
            dst[op++] = (byte)(Math.Min(lit, 15) << 4);
            op = Length(dst, op, lit);
            Buffer.BlockCopy(src, litStart, dst, op, lit);
            return op + lit;
        }

        static int Length(byte[] dst, int op, int value)
        {
            if (value < 15) return op;
            value -= 15;
            while (value >= 255) { dst[op++] = 255; value -= 255; }
            dst[op++] = (byte)value;
            return op;
        }
    }
}
