using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GameInspector
{
    public class SafeSerializer
    {
        public int MaxDepth { get; set; } = 2;
        public int MaxItems { get; set; } = 20;
        public int MaxStringLength { get; set; } = 2000;
        public int MaxResponseBytes { get; set; } = 30 * 1024; // 30KB

        private readonly HashSet<object> _visited = new HashSet<object>(new ReferenceEqualityComparer());

        public JToken Serialize(object obj)
        {
            _visited.Clear();
            return SerializeInternal(obj, 0);
        }

        private JToken SerializeInternal(object obj, int depth)
        {
            if (obj == null)
                return JValue.CreateNull();

            var type = obj.GetType();

            // Primitives
            if (type.IsPrimitive || obj is string || obj is decimal)
            {
                if (obj is string s && s.Length > MaxStringLength)
                    return s.Substring(0, MaxStringLength) + $"... (truncated, {s.Length} total)";
                return JToken.FromObject(obj);
            }

            // Enums
            if (type.IsEnum)
                return obj.ToString();

            // Depth check
            if (depth >= MaxDepth)
            {
                return JObject.FromObject(new
                {
                    __type = type.Name,
                    __truncated = "max depth reached",
                    __preview = GetPreview(obj)
                });
            }

            // Circular reference check
            if (!type.IsValueType && _visited.Contains(obj))
            {
                return JObject.FromObject(new
                {
                    __circular = type.Name,
                    __instanceId = GetInstanceId(obj)
                });
            }

            if (!type.IsValueType)
                _visited.Add(obj);

            // Unity types
            if (obj is Vector3 v3)
                return JObject.FromObject(new { x = v3.x, y = v3.y, z = v3.z });

            if (obj is Vector2 v2)
                return JObject.FromObject(new { x = v2.x, y = v2.y });

            if (obj is Quaternion q)
            {
                var euler = q.eulerAngles;
                return JObject.FromObject(new { euler = new { x = euler.x, y = euler.y, z = euler.z } });
            }

            if (obj is Color c)
                return JObject.FromObject(new { r = c.r, g = c.g, b = c.b, a = c.a });

            if (obj is UnityEngine.Object unityObj)
            {
                return JObject.FromObject(new
                {
                    __type = type.Name,
                    __instanceId = unityObj.GetInstanceID(),
                    name = unityObj.name,
                    __preview = GetPreview(obj)
                });
            }

            // Collections
            if (obj is IEnumerable enumerable && !(obj is string))
            {
                var array = new JArray();
                int count = 0;
                int total = 0;

                foreach (var item in enumerable)
                {
                    total++;
                    if (count < MaxItems)
                    {
                        array.Add(SerializeInternal(item, depth + 1));
                        count++;
                    }
                }

                if (total > MaxItems)
                {
                    return JObject.FromObject(new
                    {
                        __type = type.Name,
                        __count = total,
                        __showing = MaxItems,
                        __truncated = true,
                        items = array
                    });
                }

                return array;
            }

            // Regular objects - serialize fields
            var result = new JObject
            {
                ["__type"] = type.Name
            };

            if (obj is UnityEngine.Object uo)
                result["__instanceId"] = uo.GetInstanceID();

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            int fieldCount = 0;

            foreach (var field in fields)
            {
                if (fieldCount >= 50)
                {
                    result["__fieldsOmitted"] = fields.Length - 50;
                    break;
                }

                try
                {
                    var value = field.GetValue(obj);
                    result[field.Name] = SerializeInternal(value, depth + 1);
                    fieldCount++;
                }
                catch (Exception ex)
                {
                    result[field.Name] = $"<error: {ex.Message}>";
                }
            }

            return result;
        }

        private string GetPreview(object obj)
        {
            try
            {
                if (obj is Component c)
                    return $"{c.gameObject.name}.{c.GetType().Name}";
                if (obj is GameObject go)
                    return go.name;
                var str = obj.ToString();
                if (str.Length > 50)
                    str = str.Substring(0, 50) + "...";
                return str;
            }
            catch
            {
                return obj.GetType().Name;
            }
        }

        private int GetInstanceId(object obj)
        {
            if (obj is UnityEngine.Object uo)
                return uo.GetInstanceID();
            return obj.GetHashCode();
        }

        private class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
