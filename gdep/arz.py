"""Reader/writer for Grim Dawn .arz database archives.

Layout (little endian):
  header (24 bytes): u16 unknown(=2), u16 version(=3), u32 recTableStart,
                     u32 recTableSize, u32 recCount, u32 strTableStart, u32 strTableSize
  record data blobs (LZ4 block-compressed)
  record table: per record u32 nameIdx, u32 typeLen, type bytes, u32 dataOffset
                (relative to end of header), u32 compSize, u32 rawSize, u64 filetime
  string table: u32 count, then (u32 len, bytes) * count
  footer (16 bytes): adler32 of [file before footer, string table, record data, record table]

Decompressed record data: repeated entries of
  u16 type (0=int, 1=float, 2=string, 3=bool), u16 count, u32 keyStrIdx, count * 4-byte values
"""
import struct
import time
import zlib

import lz4.block

T_INT, T_FLOAT, T_STRING, T_BOOL = 0, 1, 2, 3


class Field:
    __slots__ = ("type", "values")

    def __init__(self, type_, values):
        self.type = type_
        self.values = values

    def copy(self):
        return Field(self.type, list(self.values))

    def __repr__(self):
        return f"Field({self.type}, {self.values})"


class Record:
    """Ordered mapping key -> Field. Keys are case-preserving."""

    __slots__ = ("name", "rtype", "fields", "filetime")

    def __init__(self, name, rtype, fields, filetime=0):
        self.name = name
        self.rtype = rtype
        self.fields = fields  # dict[str, Field]
        self.filetime = filetime

    def get(self, key, default=None):
        f = self.fields.get(key)
        if f is None or not f.values:
            return default
        return f.values[0]

    def getall(self, key):
        f = self.fields.get(key)
        return list(f.values) if f else []

    def set(self, key, value, type_=None):
        f = self.fields.get(key)
        vals = value if isinstance(value, list) else [value]
        if f is None:
            if type_ is None:
                v0 = vals[0]
                type_ = T_STRING if isinstance(v0, str) else T_FLOAT if isinstance(v0, float) else T_INT
            self.fields[key] = Field(type_, vals)
        else:
            f.values = vals

    def copy(self):
        return Record(self.name, self.rtype, {k: f.copy() for k, f in self.fields.items()}, self.filetime)


def _read_strings(buf, start, size):
    (count,) = struct.unpack_from("<I", buf, start)
    pos = start + 4
    out = []
    for _ in range(count):
        (n,) = struct.unpack_from("<I", buf, pos)
        pos += 4
        out.append(buf[pos:pos + n].decode("latin-1"))
        pos += n
    return out


def read_arz(path, name_filter=None):
    """Return dict lowercase-record-name -> Record.

    name_filter: optional callable(lowercase_name) -> bool to limit which records are decoded.
    """
    with open(path, "rb") as fh:
        buf = fh.read()
    _, _, rt_start, rt_size, rcount, st_start, st_size = struct.unpack_from("<HHIIIII", buf, 0)
    strings = _read_strings(buf, st_start, st_size)
    records = {}
    pos = rt_start
    unpack_entry = struct.Struct("<HHI").unpack_from
    for _ in range(rcount):
        name_idx, tlen = struct.unpack_from("<II", buf, pos)
        pos += 8
        rtype = buf[pos:pos + tlen].decode("latin-1")
        pos += tlen
        off, csize, rsize, ftime = struct.unpack_from("<IIIQ", buf, pos)
        pos += 20
        name = strings[name_idx]
        lname = name.lower().replace("\\", "/")
        if name_filter is not None and not name_filter(lname):
            continue
        data = lz4.block.decompress(buf[24 + off:24 + off + csize], uncompressed_size=rsize)
        fields = {}
        p = 0
        n = len(data)
        while p < n:
            t, cnt, kidx = unpack_entry(data, p)
            p += 8
            if t == T_FLOAT:
                vals = list(struct.unpack_from(f"<{cnt}f", data, p))
            elif t == T_STRING:
                vals = [strings[i] for i in struct.unpack_from(f"<{cnt}I", data, p)]
            else:
                vals = list(struct.unpack_from(f"<{cnt}i", data, p))
            p += 4 * cnt
            fields[strings[kidx]] = Field(t, vals)
        records[lname] = Record(name, rtype, fields, ftime)
    return records


def write_arz(path, records):
    """Write an iterable of Record objects to an .arz file."""
    strings = []
    index = {}

    def sidx(s):
        i = index.get(s)
        if i is None:
            i = len(strings)
            index[s] = i
            strings.append(s)
        return i

    blobs = []
    table = bytearray()
    data_off = 0
    now = int((time.time() + 11644473600) * 10_000_000)  # FILETIME
    for rec in records:
        raw = bytearray()
        for key, f in rec.fields.items():
            cnt = len(f.values)
            if cnt == 0:
                continue
            raw += struct.pack("<HHI", f.type, cnt, sidx(key))
            if f.type == T_FLOAT:
                raw += struct.pack(f"<{cnt}f", *[float(v) for v in f.values])
            elif f.type == T_STRING:
                raw += struct.pack(f"<{cnt}I", *[sidx(v) for v in f.values])
            else:
                raw += struct.pack(f"<{cnt}i", *[int(v) for v in f.values])
        comp = lz4.block.compress(bytes(raw), store_size=False)
        name_i = sidx(rec.name)
        rtype = rec.rtype.encode("latin-1")
        table += struct.pack("<II", name_i, len(rtype)) + rtype
        table += struct.pack("<IIIQ", data_off, len(comp), len(raw), now)
        blobs.append(comp)
        data_off += len(comp)

    data = b"".join(blobs)
    st = bytearray(struct.pack("<I", len(strings)))
    for s in strings:
        b = s.encode("latin-1")
        st += struct.pack("<I", len(b)) + b

    rt_start = 24 + len(data)
    st_start = rt_start + len(table)
    header = struct.pack("<HHIIIII", 2, 3, rt_start, len(table), len(blobs), st_start, len(st))
    body = header + data + bytes(table) + bytes(st)
    footer = struct.pack(
        "<IIII",
        zlib.adler32(body) & 0xFFFFFFFF,
        zlib.adler32(bytes(st)) & 0xFFFFFFFF,
        zlib.adler32(data) & 0xFFFFFFFF,
        zlib.adler32(bytes(table)) & 0xFFFFFFFF,
    )
    with open(path, "wb") as fh:
        fh.write(body + footer)
