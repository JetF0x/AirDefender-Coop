using System;
using System.Collections.Generic;
using AirDefender;
using AirDefenderCoop.Net;
using AirDefenderCoop.Replication;
using UnityEngine;

namespace AirDefenderCoop.Commands
{
    /// <summary>
    /// Encodes method arguments, instances and results by their declared type. Entities travel as
    /// contact ids, radar tracks as their contact id, airfields as their map feature identity.
    /// </summary>
    internal static class ArgCodec
    {
        public static bool CanEncode(Type t)
        {
            if (t.IsByRef) t = t.GetElementType();
            if (FieldCodec_Scalar(t) != ValueKind.Unsupported) return true;
            if (IsEntityType(t) || t == typeof(TrackRenderer) || t == typeof(MapFeaturesStorage.Feature)) return true;
            if (t == typeof(GameObject) || t == typeof(Transform)) return true;
            if (t == typeof(List<string>) || t == typeof(string[]) || t == typeof(float[]) || t == typeof(int[])) return true;
            if (t == typeof(object)) return false;
            return false;
        }

        private static ValueKind FieldCodec_Scalar(Type t) => TypeSchema.Scalar(t);

        internal static bool IsEntityType(Type t) =>
            t == typeof(MonoBehaviour) ||
            typeof(IContact).IsAssignableFrom(t) ||
            (typeof(Component).IsAssignableFrom(t) && t.Assembly == typeof(TrackManager).Assembly && t != typeof(TrackRenderer));

        public static void Write(NetWriter w, Type t, object v)
        {
            if (t.IsByRef) t = t.GetElementType();
            var k = TypeSchema.Scalar(t);
            if (k != ValueKind.Unsupported)
            {
                if (v == null) { w.U8(0); return; }
                w.U8(1);
                FieldCodec.WriteScalar(w, k, v);
                return;
            }
            if (v == null || (v is UnityEngine.Object uo && uo == null)) { w.U8(0); return; }
            w.U8(1);
            if (t == typeof(TrackRenderer)) { w.Str(((TrackRenderer)v).ContactId); return; }
            if (t == typeof(MapFeaturesStorage.Feature))
            {
                var f = (MapFeaturesStorage.Feature)v;
                w.Str(f.id); w.Str(f.name); w.I32((int)f.type); w.F32(f.worldX); w.F32(f.worldY); w.I64(f.population); w.F32(f.normX); w.F32(f.normY);
                return;
            }
            if (t == typeof(GameObject) || t == typeof(Transform) || IsEntityType(t))
            {
                w.Str(ContactIdOf(v));
                return;
            }
            if (v is List<string> ls) { w.StrList(ls); return; }
            if (v is string[] sa) { w.StrList(sa); return; }
            if (v is float[] fa) { w.I32(fa.Length); foreach (var x in fa) w.F32(x); return; }
            if (v is int[] ia) { w.I32(ia.Length); foreach (var x in ia) w.I32(x); return; }
            throw new NotSupportedException("cannot encode " + t);
        }

        public static object Read(NetReader r, Type t)
        {
            if (t.IsByRef) t = t.GetElementType();
            if (r.U8() == 0) return t.IsValueType ? Activator.CreateInstance(t) : null;
            var k = TypeSchema.Scalar(t);
            if (k != ValueKind.Unsupported) return FieldCodec.ReadScalar(r, k, t);
            if (t == typeof(TrackRenderer)) return TrackManager.GetRendererForContact(r.Str());
            if (t == typeof(MapFeaturesStorage.Feature))
            {
                var f = new MapFeaturesStorage.Feature
                {
                    id = r.Str(), name = r.Str(), type = (MapFeatureType)r.I32(), worldX = r.F32(), worldY = r.F32(),
                    population = r.I64(), normX = r.F32(), normY = r.F32()
                };
                return ResolveFeature(f);
            }
            if (t == typeof(GameObject) || t == typeof(Transform) || IsEntityType(t))
            {
                string id = r.Str();
                return ResolveEntity(id, t);
            }
            if (t == typeof(List<string>)) return r.StrList();
            if (t == typeof(string[])) return r.StrList()?.ToArray();
            if (t == typeof(float[])) { int n = r.I32(); var a = new float[n]; for (int i = 0; i < n; i++) a[i] = r.F32(); return a; }
            if (t == typeof(int[])) { int n = r.I32(); var a = new int[n]; for (int i = 0; i < n; i++) a[i] = r.I32(); return a; }
            throw new NotSupportedException("cannot decode " + t);
        }

        public static string ContactIdOf(object v)
        {
            if (v is IContact c) { try { return c.ContactId; } catch { return null; } }
            GameObject go = v is GameObject g ? g : v is Component comp ? comp.gameObject : null;
            if (go == null) return null;
            var ic = go.GetComponent<IContact>();
            try { return ic != null ? ic.ContactId : go.name; } catch { return go.name; }
        }

        public static object ResolveEntity(string id, Type t)
        {
            if (string.IsNullOrEmpty(id)) return null;
            GameObject go = null;
            if (ContactRegistry.TryGetContact(id, out var c) && c is MonoBehaviour mb && mb != null) go = mb.gameObject;
            if (go == null)
            {
                // Freshly spawned entities may not have registered yet.
                foreach (var m in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude))
                {
                    if (m is IContact ic && SafeId(ic) == id) { go = m.gameObject; break; }
                }
            }
            // Non-contact game objects (radar stations, CAP controllers) are identified by name.
            if (go == null) go = GameObject.Find(id);
            if (go == null) return null;
            if (t == typeof(GameObject)) return go;
            if (t == typeof(MonoBehaviour)) return go.GetComponent<IContact>() as MonoBehaviour;
            if (t == typeof(Transform)) return go.transform;
            if (t.IsInterface) return go.GetComponent(t);
            var comp = go.GetComponent(t);
            return comp == null ? null : comp;
        }

        private static string SafeId(IContact c) { try { return c.ContactId; } catch { return null; } }

        private static MapFeaturesStorage.Feature ResolveFeature(MapFeaturesStorage.Feature f)
        {
            try
            {
                foreach (var x in MapFeaturesStorage.Load())
                    if (x != null && x.id == f.id) return x;
            }
            catch { }
            return f;
        }
    }
}
