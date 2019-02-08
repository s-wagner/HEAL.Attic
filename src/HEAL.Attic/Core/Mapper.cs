﻿#region License Information
/*
 * This file is part of HEAL.Attic which is licensed under the MIT license.
 * See the LICENSE file in the project root for more information.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Google.Protobuf;

namespace HEAL.Attic {
  public sealed class Mapper {
    internal class MappingEqualityComparer : IEqualityComparer<object> {
      bool IEqualityComparer<object>.Equals(object x, object y) {
        if (x == y) return true;

        // for ValueTypes and strings also check Equals
        if (x is ValueType xVal && y is ValueType yVal) return xVal.Equals(yVal);
        if (x is string xStr && y is string yStr) return xStr.Equals(yStr);

        return false;
      }

      int IEqualityComparer<object>.GetHashCode(object obj) {
        return obj == null ? 0 : obj.GetHashCode();
      }
    }

    private static StaticCache staticCache;
    private static object locker = new object();
    public static StaticCache StaticCache {
      get {
        lock (locker) {
          if (staticCache == null) staticCache = new StaticCache();
          return staticCache;
        }
      }
    }

    private Index<ITransformer> transformers;
    private Index<Type> types;

    private Stack<Tuple<object, Box>> objectsToProcess = new Stack<Tuple<object, Box>>();

    private Dictionary<uint, Box> boxId2Box;
    private Dictionary<object, uint> object2BoxId;
    private Dictionary<uint, object> boxId2Object;
    private Index<string> strings;
    private Dictionary<uint, Dictionary<uint, string>> componentInfoKeys; // cache for strings <TypeGUID>.<MemberName> for accessing fields and properties of StorableTypes

    public CancellationToken CancellationToken { get; private set; }

    public uint BoxCount { get; private set; }

    public Mapper() {
      transformers = new Index<ITransformer>();
      types = new Index<Type>();

      boxId2Box = new Dictionary<uint, Box>();
      object2BoxId = new Dictionary<object, uint>(new MappingEqualityComparer());
      boxId2Object = new Dictionary<uint, object>();
      strings = new Index<string>();
      componentInfoKeys = new Dictionary<uint, Dictionary<uint, string>>();

      BoxCount = 0;
    }

    #region Transformers
    public uint GetTransformerId(ITransformer transformer) {
      return transformers.GetIndex(transformer);
    }

    public ITransformer GetTransformer(uint transformerId) {
      return transformers.GetValue(transformerId);
    }
    #endregion

    #region Types
    public uint GetTypeId(Type type) {
      return types.GetIndex(type);
    }

    public Type GetType(uint typeId) {
      return types.GetValue(typeId);
    }

    public bool TryGetType(uint typeId, out Type type) {
      return types.TryGetValue(typeId, out type);
    }
    #endregion

    #region Boxes
    public uint GetBoxId(object o) {
      uint boxId;

      if (o == null)
        boxId = 0;
      else {
        if (object2BoxId.TryGetValue(o, out boxId)) return boxId;
        var type = o.GetType();
        var typeInfo = StaticCache.GetTypeInfo(type);
        if (typeInfo.Transformer == null) throw new ArgumentException("Cannot serialize object of type " + o.GetType());
        boxId = ++BoxCount;
        typeInfo.Used++;
        object2BoxId.Add(o, boxId);
        var box = typeInfo.Transformer.CreateBox(o, this);
        boxId2Box.Add(boxId, box);
        objectsToProcess.Push(Tuple.Create(o, box));
      }
      return boxId;
    }

    public Box GetBox(uint boxId) {
      return boxId2Box[boxId];
    }

    public object GetObject(uint boxId) {
      object o;
      if (boxId2Object.TryGetValue(boxId, out o)) return o;

      Box box;
      boxId2Box.TryGetValue(boxId, out box);

      if (box == null)
        o = null;
      else {
        var transformer = transformers.GetValue(box.TransformerId);
        o = transformer.ToObject(box, this);
        boxId2Object.Add(boxId, o);
      }

      return o;
    }
    #endregion

    #region Strings
    public uint GetStringId(string str) {
      return strings.GetIndex(str);
    }
    public string GetString(uint stringId) {
      return strings.GetValue(stringId);
    }
    public string GetComponentInfoKey(uint typeId, uint memberId) {
      if (!componentInfoKeys.TryGetValue(typeId, out Dictionary<uint, string> dict)) {
        dict = new Dictionary<uint, string>();
        componentInfoKeys.Add(typeId, dict);
      }
      if (!dict.TryGetValue(memberId, out string componentInfoKey)) {
        componentInfoKey = GetString(typeId) + "." + GetString(memberId);
        dict.Add(memberId, componentInfoKey);
      }
      return componentInfoKey;
    }
    #endregion

    public object CreateInstance(Type type) {
      try {
        return StaticCache.GetTypeInfo(type).GetConstructor()();
      } catch (Exception e) {
        throw new PersistenceException("Deserialization failed.", e);
      }
    }

    public static Bundle ToBundle(object root, out SerializationInfo info, CancellationToken cancellationToken = default(CancellationToken)) {
      var mapper = new Mapper();
      var bundle = new Bundle();
      mapper.CancellationToken = cancellationToken;

      info = new SerializationInfo();

      var sw = new Stopwatch();
      sw.Start();

      bundle.RootBoxId = mapper.GetBoxId(root);

      while (mapper.objectsToProcess.Any()) {
        var tuple = mapper.objectsToProcess.Pop();
        var o = tuple.Item1;
        var box = tuple.Item2;
        var transformer = mapper.transformers.GetValue(box.TransformerId);
        transformer.FillBox(box, o, mapper);
      }

      bundle.TransformerGuids.AddRange(mapper.transformers.GetValues().Select(x => x.Guid).Select(x => ByteString.CopyFrom(x.ToByteArray())));
      bundle.TypeGuids.AddRange(mapper.types.GetValues().Select(x => ByteString.CopyFrom(StaticCache.GetGuid(x).ToByteArray())));
      bundle.Boxes.AddRange(mapper.boxId2Box.OrderBy(x => x.Key).Select(x => x.Value));
      bundle.Strings.AddRange(mapper.strings.GetValues());

      sw.Stop();

      info.Duration = sw.Elapsed;
      info.NumberOfSerializedObjects = mapper.object2BoxId.Keys.Count;
      info.SerializedTypes = mapper.types.GetValues();

      return bundle;
    }
    public static object ToObject(Bundle bundle, out SerializationInfo info) {
      var mapper = new Mapper();
      info = new SerializationInfo();

      var sw = new Stopwatch();
      sw.Start();

      mapper.transformers = new Index<ITransformer>(bundle.TransformerGuids.Select(x => new Guid(x.ToByteArray())).Select(StaticCache.GetTransformer));

      var types = new List<Type>();
      var unknownTypeGuids = new List<Guid>();
      foreach (var x in bundle.TypeGuids) {
        var guid = new Guid(x.ToByteArray());
        if (StaticCache.TryGetType(guid, out Type type)) {
          types.Add(type);
        } else {
          unknownTypeGuids.Add(guid);
          types.Add(null);
        }
      }

      mapper.types = new Index<Type>(types);
      mapper.boxId2Box = bundle.Boxes.Select((b, i) => new { Box = b, Index = i }).ToDictionary(k => (uint)k.Index + 1, v => v.Box);
      mapper.strings = new Index<string>(bundle.Strings);

      var boxes = bundle.Boxes;

      for (int i = boxes.Count - 1; i >= 0; i--) {
        mapper.GetObject((uint)i + 1);
      }

      for (int i = boxes.Count; i > 0; i--) {
        var box = mapper.boxId2Box[(uint)i];
        var o = mapper.boxId2Object[(uint)i];
        if (o == null) continue;

        var transformer = mapper.transformers.GetValue(box.TransformerId);
        transformer.FillFromBox(o, box, mapper);
      }

      var root = mapper.GetObject(bundle.RootBoxId);

      ExecuteAfterDeserializationHooks(mapper.boxId2Object.Values.GetEnumerator());

      sw.Stop();

      info.Duration = sw.Elapsed;
      info.NumberOfSerializedObjects = mapper.boxId2Object.Values.Count;
      info.SerializedTypes = types;
      info.UnknownTypeGuids = unknownTypeGuids;

      return root;
    }

    private static void ExecuteAfterDeserializationHooks(IEnumerator<object> e) {
      var emptyArgs = new object[0];

      while (e.MoveNext()) {
        var obj = e.Current;

        if (obj == null || !StorableTypeAttribute.IsStorableType(obj.GetType())) continue;

        var typeList = new LinkedList<Type>();
        for (var type = obj.GetType(); type != null; type = type.BaseType) {
          typeList.AddFirst(type);
        }

        foreach (var type in typeList) {
          if (!StorableTypeAttribute.IsStorableType(type)) continue;

          var typeInfo = StaticCache.GetTypeInfo(type);
          foreach (var hook in typeInfo.AfterDeserializationHooks) {
            try {
              hook.Invoke(obj, emptyArgs);
            } catch (TargetInvocationException t) {
              throw t.InnerException;
            }
          }
        }
      }
    }
  }
}
