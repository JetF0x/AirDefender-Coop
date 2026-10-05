using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AirDefenderCoop.Net
{
    /// <summary>Growable little-endian binary writer used for every network message.</summary>
    public sealed class NetWriter
    {
        private byte[] _buf;
        private int _len;

        public NetWriter(int capacity = 256) { _buf = new byte[capacity]; }

        public int Length => _len;
        public byte[] Buffer => _buf;

        public void Reset() { _len = 0; }
        public void Truncate(int length) { if (length >= 0 && length <= _len) _len = length; }

        public byte[] ToArray()
        {
            var a = new byte[_len];
            System.Buffer.BlockCopy(_buf, 0, a, 0, _len);
            return a;
        }

        private void Ensure(int extra)
        {
            if (_len + extra <= _buf.Length) return;
            int n = Math.Max(_buf.Length * 2, _len + extra);
            Array.Resize(ref _buf, n);
        }

        public void U8(byte v) { Ensure(1); _buf[_len++] = v; }
        public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);
        public void U16(ushort v) { Ensure(2); _buf[_len++] = (byte)v; _buf[_len++] = (byte)(v >> 8); }
        public void I32(int v) { Ensure(4); for (int i = 0; i < 4; i++) _buf[_len++] = (byte)(v >> (8 * i)); }
        public void U32(uint v) => I32((int)v);
        public void I64(long v) { Ensure(8); for (int i = 0; i < 8; i++) _buf[_len++] = (byte)(v >> (8 * i)); }
        public void U64(ulong v) => I64((long)v);
        public unsafe void F32(float v) => I32(*(int*)&v);
        public unsafe void F64(double v) => I64(*(long*)&v);

        public void Str(string s)
        {
            if (s == null) { I32(-1); return; }
            int n = Encoding.UTF8.GetByteCount(s);
            I32(n);
            Ensure(n);
            Encoding.UTF8.GetBytes(s, 0, s.Length, _buf, _len);
            _len += n;
        }

        public void Bytes(byte[] b, int offset, int count)
        {
            I32(count);
            Ensure(count);
            System.Buffer.BlockCopy(b, offset, _buf, _len, count);
            _len += count;
        }

        public void Vec3(Vector3 v) { F32(v.x); F32(v.y); F32(v.z); }
        public void Vec2(Vector2 v) { F32(v.x); F32(v.y); }
        public void Color(Color c) { F32(c.r); F32(c.g); F32(c.b); F32(c.a); }

        public void StrList(IList<string> list)
        {
            if (list == null) { I32(-1); return; }
            I32(list.Count);
            for (int i = 0; i < list.Count; i++) Str(list[i]);
        }
    }

    /// <summary>Reader matching <see cref="NetWriter"/>. Throws on truncated input.</summary>
    public sealed class NetReader
    {
        private readonly byte[] _buf;
        private int _pos;
        private readonly int _end;

        public NetReader(byte[] buf, int offset, int count) { _buf = buf; _pos = offset; _end = offset + count; }
        public NetReader(byte[] buf) : this(buf, 0, buf.Length) { }

        public int Remaining => _end - _pos;

        private void Need(int n)
        {
            if (_pos + n > _end) throw new FormatException("network message truncated");
        }

        public byte U8() { Need(1); return _buf[_pos++]; }
        public bool Bool() => U8() != 0;
        public ushort U16() { Need(2); ushort v = (ushort)(_buf[_pos] | (_buf[_pos + 1] << 8)); _pos += 2; return v; }
        public int I32() { Need(4); int v = _buf[_pos] | (_buf[_pos + 1] << 8) | (_buf[_pos + 2] << 16) | (_buf[_pos + 3] << 24); _pos += 4; return v; }
        public uint U32() => (uint)I32();
        public long I64() { Need(8); long v = 0; for (int i = 0; i < 8; i++) v |= (long)_buf[_pos + i] << (8 * i); _pos += 8; return v; }
        public ulong U64() => (ulong)I64();
        public unsafe float F32() { int i = I32(); return *(float*)&i; }
        public unsafe double F64() { long l = I64(); return *(double*)&l; }

        public string Str()
        {
            int n = I32();
            if (n < 0) return null;
            Need(n);
            string s = Encoding.UTF8.GetString(_buf, _pos, n);
            _pos += n;
            return s;
        }

        public byte[] Bytes()
        {
            int n = I32();
            Need(n);
            var b = new byte[n];
            System.Buffer.BlockCopy(_buf, _pos, b, 0, n);
            _pos += n;
            return b;
        }

        public Vector3 Vec3() => new Vector3(F32(), F32(), F32());
        public Vector2 Vec2() => new Vector2(F32(), F32());
        public Color Color() => new Color(F32(), F32(), F32(), F32());

        public List<string> StrList()
        {
            int n = I32();
            if (n < 0) return null;
            var l = new List<string>(n);
            for (int i = 0; i < n; i++) l.Add(Str());
            return l;
        }
    }
}
