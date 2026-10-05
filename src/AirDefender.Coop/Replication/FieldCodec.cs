using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using AirDefender;
using AirDefenderCoop.Net;
using UnityEngine;

namespace AirDefenderCoop.Replication
{
    /// <summary>How a single field's value is written to / read from the wire.</summary>
    internal enum ValueKind : byte
    {
        Unsupported, Bool, Byte, SByte, Int16, UInt16, Int32, UInt32, Int64, UInt64, Single, Double, String, Enum,
        Vector2, Vector3, Vector4, Quaternion, Color, Rect, Vector2Int,
        ContactRef,         // Transform / GameObject / Component on a contact -> contact id
        List, Array, StringDictionary, StringSet,
    }

    internal sealed class FieldSpec
    {
        public FieldInfo Field;
        public ValueKind Kind;
        public ValueKind Element;     // for collections
        public Type ElementType;      // for collections / enum
        public ValueKind DictValue;   // for StringDictionary
        public Type DictValueType;
        public ushort Index;
        public string Name => Field.Name;
    }

    /// <summary>Per-type list of replicable fields, identical on both peers for the same game build.</summary>
    internal sealed class TypeSchema
    {
        public Type Type;
        public FieldSpec[] Fields;
        public ushort Id;

        private static readonly Dictionary<(Type, bool), TypeSchema> Cache = new Dictionary<(Type, bool), TypeSchema>();

        public static TypeSchema Get(Type t, bool statics)
        {
            if (Cache.TryGetValue((t, statics), out var s)) return s;
            s = Build(t, statics);
            Cache[(t, statics)] = s;
            return s;
        }

        private static TypeSchema Build(Type t, bool statics)
        {
            var list = new List<FieldSpec>();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly |
                        (statics ? BindingFlags.Static : BindingFlags.Instance);
            var chain = new List<Type>();
            for (Type x = t; x != null && x != typeof(MonoBehaviour) && x != typeof(Behaviour) && x != typeof(Component) &&
                             x != typeof(UnityEngine.Object) && x != typeof(object); x = statics ? null : x.BaseType)
                chain.Add(x);
            chain.Reverse();
            foreach (var ct in chain)
            {
                var fields = ct.GetFields(flags);
                Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
                foreach (var f in fields)
                {
                    if (f.IsLiteral || (statics && f.IsInitOnly && !IsMutableCollection(f.FieldType))) continue;
                    if (f.IsDefined(typeof(CompilerGeneratedAttribute), false) && f.Name.Contains("k__BackingField") == false) continue;
                    if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    if (FieldFilter.IsExcluded(t, f)) continue;
                    var spec = Describe(f);
                    if (spec == null) continue;
                    spec.Index = (ushort)list.Count;
                    list.Add(spec);
                }
            }
            return new TypeSchema { Type = t, Fields = list.ToArray() };
        }

        private static bool IsMutableCollection(Type t) =>
            t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(List<>) || t.GetGenericTypeDefinition() == typeof(Dictionary<,>) || t.GetGenericTypeDefinition() == typeof(HashSet<>));

        private static FieldSpec Describe(FieldInfo f)
        {
            Type t = f.FieldType;
            var k = Scalar(t);
            if (k != ValueKind.Unsupported) return new FieldSpec { Field = f, Kind = k, ElementType = t };
            if (IsContactRefType(t)) return new FieldSpec { Field = f, Kind = ValueKind.ContactRef, ElementType = t };
            if (t.IsArray && t.GetArrayRank() == 1)
            {
                var e = Scalar(t.GetElementType());
                if (e != ValueKind.Unsupported) return new FieldSpec { Field = f, Kind = ValueKind.Array, Element = e, ElementType = t.GetElementType() };
                return null;
            }
            if (t.IsGenericType)
            {
                var g = t.GetGenericTypeDefinition();
                var args = t.GetGenericArguments();
                if (g == typeof(List<>))
                {
                    var e = Scalar(args[0]);
                    if (e != ValueKind.Unsupported) return new FieldSpec { Field = f, Kind = ValueKind.List, Element = e, ElementType = args[0] };
                }
                else if (g == typeof(Dictionary<,>) && args[0] == typeof(string))
                {
                    var v = Scalar(args[1]);
                    if (v != ValueKind.Unsupported) return new FieldSpec { Field = f, Kind = ValueKind.StringDictionary, DictValue = v, DictValueType = args[1] };
                }
                else if (g == typeof(HashSet<>) && args[0] == typeof(string))
                {
                    return new FieldSpec { Field = f, Kind = ValueKind.StringSet };
                }
            }
            return null;
        }

        private static bool IsContactRefType(Type t) =>
            t == typeof(Transform) || t == typeof(GameObject) ||
            (typeof(MonoBehaviour).IsAssignableFrom(t) && t.Assembly == typeof(TrackManager).Assembly && typeof(IContact).IsAssignableFrom(t));

        internal static ValueKind Scalar(Type t)
        {
            if (t == typeof(bool)) return ValueKind.Bool;
            if (t == typeof(byte)) return ValueKind.Byte;
            if (t == typeof(sbyte)) return ValueKind.SByte;
            if (t == typeof(short)) return ValueKind.Int16;
            if (t == typeof(ushort)) return ValueKind.UInt16;
            if (t == typeof(int)) return ValueKind.Int32;
            if (t == typeof(uint)) return ValueKind.UInt32;
            if (t == typeof(long)) return ValueKind.Int64;
            if (t == typeof(ulong)) return ValueKind.UInt64;
            if (t == typeof(float)) return ValueKind.Single;
            if (t == typeof(double)) return ValueKind.Double;
            if (t == typeof(string)) return ValueKind.String;
            if (t.IsEnum) return ValueKind.Enum;
            if (t == typeof(Vector2)) return ValueKind.Vector2;
            if (t == typeof(Vector3)) return ValueKind.Vector3;
            if (t == typeof(Vector4)) return ValueKind.Vector4;
            if (t == typeof(Quaternion)) return ValueKind.Quaternion;
            if (t == typeof(Color)) return ValueKind.Color;
            if (t == typeof(Rect)) return ValueKind.Rect;
            if (t == typeof(Vector2Int)) return ValueKind.Vector2Int;
            return ValueKind.Unsupported;
        }
    }

    /// <summary>Fields that must never cross the wire (purely local presentation/caches).</summary>
    internal static class FieldFilter
    {
        // Unity object references to non-contacts are skipped by type already; these catch the
        // plain-value caches and diagnostics that are meaningless on another machine.
        private static readonly string[] NameFragments =
        {
            "cache", "Cache", "debug", "Debug", "scratch", "Scratch", "lastLog", "nextLog", "_tmp",
        };

        public static bool IsExcluded(Type owner, FieldInfo f)
        {
            string n = f.Name;
            if (GlobalTargets.Allow.TryGetValue(owner, out var allow)) return !allow.Contains(n);
            foreach (var frag in NameFragments)
                if (n.IndexOf(frag, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }

    internal static class FieldCodec
    {
        // ---------------------------------------------------------------- write

        public static void Write(NetWriter w, FieldSpec spec, object value)
        {
            switch (spec.Kind)
            {
                case ValueKind.ContactRef: WriteRef(w, value); return;
                case ValueKind.List:
                case ValueKind.Array:
                    if (!(value is IList list)) { w.I32(-1); return; }
                    w.I32(list.Count);
                    for (int i = 0; i < list.Count; i++) WriteScalar(w, spec.Element, list[i]);
                    return;
                case ValueKind.StringDictionary:
                    if (!(value is IDictionary dict)) { w.I32(-1); return; }
                    // Sorted so the encoding (and hash) is stable.
                    var keys = new List<string>(dict.Count);
                    foreach (var k in dict.Keys) keys.Add((string)k);
                    keys.Sort(StringComparer.Ordinal);
                    w.I32(keys.Count);
                    foreach (var k in keys) { w.Str(k); WriteScalar(w, spec.DictValue, dict[k]); }
                    return;
                case ValueKind.StringSet:
                    if (!(value is HashSet<string> set)) { w.I32(-1); return; }
                    var items = new List<string>(set);
                    items.Sort(StringComparer.Ordinal);
                    w.I32(items.Count);
                    foreach (var s in items) w.Str(s);
                    return;
                default:
                    WriteScalar(w, spec.Kind, value);
                    return;
            }
        }

        private static void WriteRef(NetWriter w, object value)
        {
            var obj = value as UnityEngine.Object;
            if (obj == null) { w.U8(0); return; }
            GameObject go = obj is GameObject g ? g : obj is Component c ? c.gameObject : null;
            var contact = go != null ? go.GetComponent<IContact>() : null;
            string id = null;
            try { id = contact?.ContactId; } catch { }
            if (string.IsNullOrEmpty(id)) { w.U8(2); return; } // not a contact: leave the client's value alone
            w.U8(1);
            w.Str(id);
        }

        public static void WriteScalar(NetWriter w, ValueKind k, object v)
        {
            switch (k)
            {
                case ValueKind.Bool: w.Bool((bool)v); break;
                case ValueKind.Byte: w.U8((byte)v); break;
                case ValueKind.SByte: w.U8((byte)(sbyte)v); break;
                case ValueKind.Int16: w.U16((ushort)(short)v); break;
                case ValueKind.UInt16: w.U16((ushort)v); break;
                case ValueKind.Int32: w.I32((int)v); break;
                case ValueKind.UInt32: w.U32((uint)v); break;
                case ValueKind.Int64: w.I64((long)v); break;
                case ValueKind.UInt64: w.U64((ulong)v); break;
                case ValueKind.Single: w.F32((float)v); break;
                case ValueKind.Double: w.F64((double)v); break;
                case ValueKind.String: w.Str((string)v); break;
                case ValueKind.Enum: w.I64(Convert.ToInt64(v)); break;
                case ValueKind.Vector2: w.Vec2((Vector2)v); break;
                case ValueKind.Vector3: w.Vec3((Vector3)v); break;
                case ValueKind.Vector4: { var x = (Vector4)v; w.F32(x.x); w.F32(x.y); w.F32(x.z); w.F32(x.w); break; }
                case ValueKind.Quaternion: { var q = (Quaternion)v; w.F32(q.x); w.F32(q.y); w.F32(q.z); w.F32(q.w); break; }
                case ValueKind.Color: w.Color((Color)v); break;
                case ValueKind.Rect: { var r = (Rect)v; w.F32(r.x); w.F32(r.y); w.F32(r.width); w.F32(r.height); break; }
                case ValueKind.Vector2Int: { var x = (Vector2Int)v; w.I32(x.x); w.I32(x.y); break; }
                default: throw new NotSupportedException(k.ToString());
            }
        }

        // ---------------------------------------------------------------- read

        /// <summary>Reads a value. <paramref name="skip"/> is true when the field must be left untouched.</summary>
        public static object Read(NetReader r, FieldSpec spec, out bool skip)
        {
            skip = false;
            switch (spec.Kind)
            {
                case ValueKind.ContactRef:
                    byte tag = r.U8();
                    if (tag == 0) return null;
                    if (tag == 2) { skip = true; return null; }
                    return ResolveRef(r.Str(), spec.ElementType);
                case ValueKind.List:
                {
                    int n = r.I32();
                    if (n < 0) return null;
                    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(spec.ElementType), n);
                    for (int i = 0; i < n; i++) list.Add(ReadScalar(r, spec.Element, spec.ElementType));
                    return list;
                }
                case ValueKind.Array:
                {
                    int n = r.I32();
                    if (n < 0) return null;
                    var arr = Array.CreateInstance(spec.ElementType, n);
                    for (int i = 0; i < n; i++) arr.SetValue(ReadScalar(r, spec.Element, spec.ElementType), i);
                    return arr;
                }
                case ValueKind.StringDictionary:
                {
                    int n = r.I32();
                    if (n < 0) return null;
                    var dict = (IDictionary)Activator.CreateInstance(spec.Field.FieldType);
                    for (int i = 0; i < n; i++) { string k = r.Str(); dict[k] = ReadScalar(r, spec.DictValue, spec.DictValueType); }
                    return dict;
                }
                case ValueKind.StringSet:
                {
                    int n = r.I32();
                    if (n < 0) return null;
                    var set = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < n; i++) set.Add(r.Str());
                    return set;
                }
                default:
                    return ReadScalar(r, spec.Kind, spec.ElementType);
            }
        }

        public static object ReadScalar(NetReader r, ValueKind k, Type t)
        {
            switch (k)
            {
                case ValueKind.Bool: return r.Bool();
                case ValueKind.Byte: return r.U8();
                case ValueKind.SByte: return (sbyte)r.U8();
                case ValueKind.Int16: return (short)r.U16();
                case ValueKind.UInt16: return r.U16();
                case ValueKind.Int32: return r.I32();
                case ValueKind.UInt32: return r.U32();
                case ValueKind.Int64: return r.I64();
                case ValueKind.UInt64: return r.U64();
                case ValueKind.Single: return r.F32();
                case ValueKind.Double: return r.F64();
                case ValueKind.String: return r.Str();
                case ValueKind.Enum: return Enum.ToObject(t, r.I64());
                case ValueKind.Vector2: return r.Vec2();
                case ValueKind.Vector3: return r.Vec3();
                case ValueKind.Vector4: return new Vector4(r.F32(), r.F32(), r.F32(), r.F32());
                case ValueKind.Quaternion: return new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
                case ValueKind.Color: return r.Color();
                case ValueKind.Rect: return new Rect(r.F32(), r.F32(), r.F32(), r.F32());
                case ValueKind.Vector2Int: return new Vector2Int(r.I32(), r.I32());
                default: throw new NotSupportedException(k.ToString());
            }
        }

        private static object ResolveRef(string id, Type wanted)
        {
            if (!ContactRegistry.TryGetContact(id, out var c)) return null;
            var mb = c as MonoBehaviour;
            if (mb == null || mb.Equals(null)) return null;
            if (wanted == typeof(Transform)) return mb.transform;
            if (wanted == typeof(GameObject)) return mb.gameObject;
            var comp = mb.GetComponent(wanted);
            return comp == null ? null : comp;
        }

        // ---------------------------------------------------------------- hashing

        public static ulong Hash(byte[] buf, int offset, int count)
        {
            ulong h = 14695981039346656037UL;
            for (int i = offset; i < offset + count; i++) { h ^= buf[i]; h *= 1099511628211UL; }
            return h;
        }
    }
}
