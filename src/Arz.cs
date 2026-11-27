using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EasierProgression
{
    // Grim Dawn .arz database archive.
    //
    // Layout (little endian):
    //   header (24 bytes): u16 unknown(=2), u16 version(=3), u32 recTableStart,
    //                      u32 recTableSize, u32 recCount, u32 strTableStart, u32 strTableSize
    //   record data blobs (LZ4 block-compressed)
    //   record table: per record u32 nameIdx, u32 typeLen, type bytes, u32 dataOffset
    //                 (relative to end of header), u32 compSize, u32 rawSize, u64 filetime
    //   string table: u32 count, then (u32 len, bytes) * count
    //   footer (16 bytes): adler32 of [file before footer, string table, record data, record table]
    //
    // Decompressed record data: repeated entries of
    //   u16 type (0=int, 1=float, 2=string, 3=bool), u16 count, u32 keyStrIdx, count * 4-byte values

    public static class FieldType
    {
        public const int Int = 0, Float = 1, String = 2, Bool = 3;
    }

    /// <summary>Read-only view of one field. Numbers (int/float/bool) are in Nums, strings in Strs.</summary>
    public sealed class Field
    {
        public readonly int Type;
        public readonly IReadOnlyList<double> Nums;
        public readonly IReadOnlyList<string> Strs;

        internal Field(int type, IReadOnlyList<double> nums, IReadOnlyList<string> strs)
        {
            Type = type;
            Nums = nums;
            Strs = strs;
        }

        public bool IsString => Type == FieldType.String;
        public int Count => IsString ? Strs.Count : Nums.Count;
    }

    /// <summary>Ordered key -> values record. Keys are case-sensitive and case-preserving.
    ///
    /// Stored compactly (the game database has ~10 million fields): parallel per-field arrays that
    /// point into shared value pools. The pools are never written; a copy shares them and keeps the
    /// values it changes in override tables. Keys are interned, so lookups compare references.</summary>
    public sealed class Record
    {
        public string Name, RType;
        public ulong FileTime;

        string[] keys;
        byte[] types;
        int[] start, count;
        int n;
        readonly double[] nums;
        readonly string[] strs;
        Dictionary<int, double[]> numOv;
        Dictionary<int, string[]> strOv;

        static readonly double[] NoNums = new double[0];
        static readonly string[] NoStrs = new string[0];

        internal Record(string name, string rtype, ulong fileTime, string[] keys, byte[] types, int[] start,
                        int[] count, int n, double[] nums, string[] strs)
        {
            Name = name;
            RType = rtype;
            FileTime = fileTime;
            this.keys = keys;
            this.types = types;
            this.start = start;
            this.count = count;
            this.n = n;
            this.nums = nums;
            this.strs = strs;
        }

        public int Count => n;
        public string KeyAt(int i) => keys[i];
        public IReadOnlyList<string> Keys => new ArraySegment<string>(keys, 0, n);

        /// <summary>Index of a field, or -1.</summary>
        public int Find(string key)
        {
            string k = string.IsInterned(key); // every key read from a database is interned
            if (k == null) return -1;
            for (int i = 0; i < n; i++)
                if ((object)keys[i] == k) return i;
            return -1;
        }

        ArraySegment<double> NumSeg(int i)
        {
            if (numOv != null && numOv.TryGetValue(i, out var a)) return new ArraySegment<double>(a);
            return types[i] == FieldType.String ? new ArraySegment<double>(NoNums)
                                                : new ArraySegment<double>(nums, start[i], count[i]);
        }

        ArraySegment<string> StrSeg(int i)
        {
            if (strOv != null && strOv.TryGetValue(i, out var a)) return new ArraySegment<string>(a);
            return types[i] == FieldType.String ? new ArraySegment<string>(strs, start[i], count[i])
                                                : new ArraySegment<string>(NoStrs);
        }

        public Field FieldAt(int i) => new Field(types[i], NumSeg(i), StrSeg(i));

        public Field this[string key]
        {
            get
            {
                int i = Find(key);
                return i < 0 ? null : FieldAt(i);
            }
        }

        /// <summary>First value as a string, or null if missing, empty or not a string field.</summary>
        public string Str(string key)
        {
            int i = Find(key);
            if (i < 0 || types[i] != FieldType.String) return null;
            var s = StrSeg(i);
            return s.Count > 0 ? s.Array[s.Offset] : null;
        }

        /// <summary>First value as a number; `def` if missing, empty or not numeric.</summary>
        public double Num(string key, double def = 0)
        {
            int i = Find(key);
            if (i < 0 || types[i] == FieldType.String) return def;
            var s = NumSeg(i);
            return s.Count > 0 ? s.Array[s.Offset] : def;
        }

        /// <summary>True if the first value is a non-zero number or a non-empty string.</summary>
        public bool Truthy(string key)
        {
            int i = Find(key);
            if (i < 0) return false;
            if (types[i] == FieldType.String)
            {
                var s = StrSeg(i);
                return s.Count > 0 && s.Array[s.Offset].Length > 0;
            }
            var d = NumSeg(i);
            return d.Count > 0 && d.Array[d.Offset] != 0;
        }

        /// <summary>All values as strings (non-string values become "").</summary>
        public List<string> AllStr(string key)
        {
            int i = Find(key);
            if (i < 0) return new List<string>();
            return types[i] == FieldType.String ? new List<string>(StrSeg(i))
                                                : Enumerable.Repeat("", NumSeg(i).Count).ToList();
        }

        public List<double> AllNum(string key)
        {
            int i = Find(key);
            return i >= 0 && types[i] != FieldType.String ? new List<double>(NumSeg(i)) : new List<double>();
        }

        /// <summary>Replace a field's values (keeping its type), or add it with `newType`.</summary>
        public void SetNums(string key, IEnumerable<double> values, int newType)
        {
            int i = Find(key);
            if (i < 0) i = AddField(key, newType);
            SetNumsAt(i, values);
        }

        public void SetNumsAt(int i, IEnumerable<double> values) =>
            (numOv ?? (numOv = new Dictionary<int, double[]>()))[i] = values.ToArray();

        public void SetStrs(string key, IEnumerable<string> values)
        {
            int i = Find(key);
            if (i < 0) i = AddField(key, FieldType.String);
            (strOv ?? (strOv = new Dictionary<int, string[]>()))[i] = values.ToArray();
        }

        public void SetStr(string key, string value) => SetStrs(key, new[] { value });

        int AddField(string key, int type)
        {
            if (n == keys.Length)
            {
                int cap = Math.Max(4, n * 2);
                Array.Resize(ref keys, cap);
                Array.Resize(ref types, cap);
                Array.Resize(ref start, cap);
                Array.Resize(ref count, cap);
            }
            keys[n] = string.Intern(key);
            types[n] = (byte)type;
            start[n] = 0;
            count[n] = 0;
            return n++;
        }

        public Record Copy() => new Record(Name, RType, FileTime, (string[])keys.Clone(), (byte[])types.Clone(),
                                           (int[])start.Clone(), (int[])count.Clone(), n, nums, strs)
        {
            numOv = numOv == null ? null : new Dictionary<int, double[]>(numOv),
            strOv = strOv == null ? null : new Dictionary<int, string[]>(strOv),
        };

        /// <summary>Serialize the fields (empty ones are left out); `sidx` maps strings to table indices.</summary>
        internal void WriteFields(BinaryWriter w, Func<string, int> sidx)
        {
            for (int i = 0; i < n; i++)
            {
                int t = types[i];
                if (t == FieldType.String)
                {
                    var s = StrSeg(i);
                    if (s.Count == 0) continue;
                    w.Write((ushort)t);
                    w.Write((ushort)s.Count);
                    w.Write(sidx(keys[i]));
                    for (int j = 0; j < s.Count; j++) w.Write(sidx(s.Array[s.Offset + j]));
                }
                else
                {
                    var d = NumSeg(i);
                    if (d.Count == 0) continue;
                    w.Write((ushort)t);
                    w.Write((ushort)d.Count);
                    w.Write(sidx(keys[i]));
                    if (t == FieldType.Float)
                        for (int j = 0; j < d.Count; j++) w.Write((float)d.Array[d.Offset + j]);
                    else
                        for (int j = 0; j < d.Count; j++) w.Write((int)d.Array[d.Offset + j]); // truncates toward zero
                }
            }
        }
    }

    /// <summary>Records by lowercase name, iterated in first-insertion order (stable build output).</summary>
    public sealed class Db : IEnumerable<KeyValuePair<string, Record>>
    {
        readonly List<string> order = new List<string>();
        readonly Dictionary<string, Record> map = new Dictionary<string, Record>(StringComparer.Ordinal);

        public int Count => map.Count;
        public bool Contains(string name) => map.ContainsKey(name);
        public Record Get(string name) => name != null && map.TryGetValue(name, out var r) ? r : null;

        public void Set(string name, Record r)
        {
            if (!map.ContainsKey(name)) order.Add(name);
            map[name] = r;
        }

        public IEnumerator<KeyValuePair<string, Record>> GetEnumerator()
        {
            foreach (var k in order) yield return new KeyValuePair<string, Record>(k, map[k]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public static class Arz
    {
        public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        static readonly double[] NoDoubles = new double[0];
        static readonly string[] NoStrings = new string[0];

        public static string NormName(string name) =>
            name == null ? "" : name.ToLowerInvariant().Replace('\\', '/');

        /// <summary>Read records into `into`; `filter(lowercaseName)` limits which are decoded.</summary>
        public static void Read(string path, Func<string, bool> filter, Db into)
        {
            byte[] buf = File.ReadAllBytes(path);
            int rtStart = (int)U32(buf, 4);
            int rcount = (int)U32(buf, 12);
            int stStart = (int)U32(buf, 16);

            int count = (int)U32(buf, stStart);
            var strings = new string[count];
            int pos = stStart + 4;
            for (int i = 0; i < count; i++)
            {
                int n = (int)U32(buf, pos);
                strings[i] = Latin1.GetString(buf, pos + 4, n);
                pos += 4 + n;
            }

            var keyCache = new string[count];
            var seen = new int[count]; // record number + 1 that last used each key
            pos = rtStart;
            for (int r = 0; r < rcount; r++)
            {
                uint nameIdx = U32(buf, pos);
                int tlen = (int)U32(buf, pos + 4);
                string rtype = Latin1.GetString(buf, pos + 8, tlen);
                pos += 8 + tlen;
                int off = (int)U32(buf, pos);
                int csize = (int)U32(buf, pos + 4);
                int rsize = (int)U32(buf, pos + 8);
                ulong ftime = BitConverter.ToUInt64(buf, pos + 12);
                pos += 20;

                string name = strings[nameIdx];
                string lname = NormName(name);
                if (filter != null && !filter(lname)) continue;

                byte[] data = Lz4.Decompress(buf, 24 + off, csize, rsize);
                int nf = 0, nn = 0, ns = 0;
                for (int p = 0; p < data.Length; nf++)
                {
                    int cnt = data[p + 2] | (data[p + 3] << 8);
                    if ((data[p] | (data[p + 1] << 8)) == FieldType.String) ns += cnt; else nn += cnt;
                    p += 8 + 4 * cnt;
                }
                var keys = new string[nf];
                var types = new byte[nf];
                var starts = new int[nf];
                var counts = new int[nf];
                var nums = nn == 0 ? NoDoubles : new double[nn];
                var strs = ns == 0 ? NoStrings : new string[ns];
                int fi = 0, ni = 0, si = 0;
                for (int p = 0; p < data.Length;)
                {
                    int t = data[p] | (data[p + 1] << 8);
                    int cnt = data[p + 2] | (data[p + 3] << 8);
                    int kidx = (int)U32(data, p + 4);
                    p += 8;
                    string key = keyCache[kidx] ?? (keyCache[kidx] = string.Intern(strings[kidx]));
                    int slot = fi;
                    if (seen[kidx] == r + 1) // duplicate key: the later value wins, the first position stays
                        slot = Array.IndexOf(keys, key, 0, fi);
                    seen[kidx] = r + 1;
                    keys[slot] = key;
                    types[slot] = (byte)t;
                    counts[slot] = cnt;
                    if (t == FieldType.String)
                    {
                        starts[slot] = si;
                        for (int i = 0; i < cnt; i++) strs[si++] = strings[U32(data, p + 4 * i)];
                    }
                    else
                    {
                        starts[slot] = ni;
                        for (int i = 0; i < cnt; i++)
                            nums[ni++] = t == FieldType.Float ? BitConverter.ToSingle(data, p + 4 * i)
                                                              : BitConverter.ToInt32(data, p + 4 * i);
                    }
                    p += 4 * cnt;
                    if (slot == fi) fi++;
                }
                var rec = new Record(name, rtype, ftime, keys, types, starts, counts, fi, nums, strs);
                into.Set(lname, rec);
            }
        }

        public static void Write(string path, IEnumerable<Record> records)
        {
            var strings = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            int Sidx(string s)
            {
                if (!index.TryGetValue(s, out int i))
                {
                    i = strings.Count;
                    index[s] = i;
                    strings.Add(s);
                }
                return i;
            }

            var data = new MemoryStream();
            var table = new MemoryStream();
            var tw = new BinaryWriter(table);
            var raw = new MemoryStream();
            var rw = new BinaryWriter(raw);
            int count = 0;
            ulong now = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            foreach (var rec in records)
            {
                raw.SetLength(0);
                rec.WriteFields(rw, Sidx);
                rw.Flush();
                byte[] comp = Lz4.Compress(raw.GetBuffer(), (int)raw.Length);
                byte[] rtype = Latin1.GetBytes(rec.RType);
                tw.Write(Sidx(rec.Name));
                tw.Write(rtype.Length);
                tw.Write(rtype);
                tw.Write((uint)data.Length);
                tw.Write(comp.Length);
                tw.Write((int)raw.Length);
                tw.Write(now);
                data.Write(comp, 0, comp.Length);
                count++;
            }
            tw.Flush();

            var st = new MemoryStream();
            var sw = new BinaryWriter(st);
            sw.Write(strings.Count);
            foreach (var s in strings)
            {
                byte[] b = Latin1.GetBytes(s);
                sw.Write(b.Length);
                sw.Write(b);
            }
            sw.Flush();

            int rtStart = 24 + (int)data.Length;
            int stStart = rtStart + (int)table.Length;
            var header = new MemoryStream();
            var hw = new BinaryWriter(header);
            hw.Write((ushort)2);
            hw.Write((ushort)3);
            hw.Write(rtStart);
            hw.Write((int)table.Length);
            hw.Write(count);
            hw.Write(stStart);
            hw.Write((int)st.Length);
            hw.Flush();

            var whole = new Adler32();
            var parts = new[] { header, data, table, st };
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                foreach (var part in parts)
                {
                    whole.Update(part.GetBuffer(), (int)part.Length);
                    fs.Write(part.GetBuffer(), 0, (int)part.Length);
                }
                var fw = new BinaryWriter(fs);
                fw.Write(whole.Value);
                fw.Write(Adler32.Of(st));
                fw.Write(Adler32.Of(data));
                fw.Write(Adler32.Of(table));
                fw.Flush();
            }
        }

        static uint U32(byte[] b, int i) => BitConverter.ToUInt32(b, i);
    }

    sealed class Adler32
    {
        uint a = 1, b;

        public uint Value => (b << 16) | a;

        public void Update(byte[] buf, int length)
        {
            int i = 0;
            while (i < length)
            {
                int end = Math.Min(length, i + 5552); // largest block before the sums can overflow
                for (; i < end; i++)
                {
                    a += buf[i];
                    b += a;
                }
                a %= 65521;
                b %= 65521;
            }
        }

        public static uint Of(MemoryStream ms)
        {
            var x = new Adler32();
            x.Update(ms.GetBuffer(), (int)ms.Length);
            return x.Value;
        }
    }
}
