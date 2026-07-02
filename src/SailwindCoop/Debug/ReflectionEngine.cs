using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SailwindCoop.Debug
{
    public static class ReflectionEngine
    {
        // Cache of known types for faster lookup
        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        // Common game assemblies to search
        private static readonly Assembly[] GameAssemblies;

        static ReflectionEngine()
        {
            var assemblies = new List<Assembly>();

            // Add Assembly-CSharp (main game code)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "Assembly-CSharp" ||
                    asm.GetName().Name == "UnityEngine" ||
                    asm.GetName().Name == "UnityEngine.CoreModule")
                {
                    assemblies.Add(asm);
                }
            }

            GameAssemblies = assemblies.ToArray();
        }

        private const BindingFlags AllFields = BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Static | BindingFlags.Instance;

        public static Type FindType(string typeName)
        {
            if (TypeCache.TryGetValue(typeName, out var cached))
                return cached;

            // Search in game assemblies
            foreach (var asm in GameAssemblies)
            {
                // Try direct match
                var type = asm.GetType(typeName);
                if (type != null)
                {
                    TypeCache[typeName] = type;
                    return type;
                }

                // Try searching all types (handles cases without namespace)
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == typeName)
                    {
                        TypeCache[typeName] = t;
                        return t;
                    }
                }
            }

            return null;
        }

        public static (object value, string error) GetValue(string path)
        {
            try
            {
                var (obj, member, err) = ResolvePath(path);
                if (err != null)
                    return (null, err);

                object value = GetMemberValue(obj, member);
                return (value, null);
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
        }

        public static string SetValue(string path, string valueStr)
        {
            try
            {
                var (obj, member, err) = ResolvePath(path);
                if (err != null)
                    return err;

                Type targetType = GetMemberType(member);
                object value = ParseValue(valueStr, targetType);
                SetMemberValue(obj, member, value);

                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static (object obj, MemberInfo member, string error) ResolvePath(string path)
        {
            string[] parts = path.Split('.');
            if (parts.Length < 2)
                return (null, null, $"Invalid path '{path}': need at least Type.field");

            // First part is type name
            Type type = FindType(parts[0]);
            if (type == null)
                return (null, null, $"Type '{parts[0]}' not found");

            object current = null;
            Type currentType = type;

            // Walk the path, stopping at second-to-last
            for (int i = 1; i < parts.Length - 1; i++)
            {
                var member = FindMember(currentType, parts[i]);
                if (member == null)
                    return (null, null, $"Member '{parts[i]}' not found on {currentType.Name}");

                current = GetMemberValue(current, member);
                if (current == null)
                    return (null, null, $"{string.Join(".", parts, 0, i + 1)} is null");

                currentType = current.GetType();
            }

            // Last part is the target member
            string lastPart = parts[parts.Length - 1];
            var targetMember = FindMember(currentType, lastPart);
            if (targetMember == null)
                return (null, null, $"Member '{lastPart}' not found on {currentType.Name}");

            return (current, targetMember, null);
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            // Try field first
            var field = type.GetField(name, AllFields);
            if (field != null) return field;

            // Try property
            var prop = type.GetProperty(name, AllFields);
            if (prop != null) return prop;

            return null;
        }

        private static object GetMemberValue(object obj, MemberInfo member)
        {
            return member switch
            {
                FieldInfo f => f.GetValue(obj),
                PropertyInfo p => p.GetValue(obj),
                _ => throw new InvalidOperationException($"Unknown member type: {member.GetType()}")
            };
        }

        private static void SetMemberValue(object obj, MemberInfo member, object value)
        {
            switch (member)
            {
                case FieldInfo f:
                    f.SetValue(obj, value);
                    break;
                case PropertyInfo p:
                    p.SetValue(obj, value);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown member type: {member.GetType()}");
            }
        }

        private static Type GetMemberType(MemberInfo member)
        {
            return member switch
            {
                FieldInfo f => f.FieldType,
                PropertyInfo p => p.PropertyType,
                _ => throw new InvalidOperationException($"Unknown member type: {member.GetType()}")
            };
        }

        private static object ParseValue(string valueStr, Type targetType)
        {
            // Handle nullable
            Type underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
                targetType = underlying;

            // Primitives
            if (targetType == typeof(float))
                return float.Parse(valueStr);
            if (targetType == typeof(double))
                return double.Parse(valueStr);
            if (targetType == typeof(int))
                return int.Parse(valueStr);
            if (targetType == typeof(bool))
                return bool.Parse(valueStr);
            if (targetType == typeof(string))
                return valueStr;

            // Vector3: (x, y, z)
            if (targetType == typeof(Vector3))
            {
                string inner = valueStr.Trim('(', ')');
                string[] parts = inner.Split(',');
                return new Vector3(
                    float.Parse(parts[0].Trim()),
                    float.Parse(parts[1].Trim()),
                    float.Parse(parts[2].Trim())
                );
            }

            // Quaternion: euler angles (x, y, z)
            if (targetType == typeof(Quaternion))
            {
                string inner = valueStr.Trim('(', ')');
                string[] parts = inner.Split(',');
                return Quaternion.Euler(
                    float.Parse(parts[0].Trim()),
                    float.Parse(parts[1].Trim()),
                    float.Parse(parts[2].Trim())
                );
            }

            // Enum
            if (targetType.IsEnum)
            {
                return Enum.Parse(targetType, valueStr, ignoreCase: true);
            }

            throw new ArgumentException($"Cannot parse '{valueStr}' as {targetType.Name}");
        }

        public static string FormatValue(object value)
        {
            if (value == null)
                return "null";

            if (value is Vector3 v3)
                return $"({v3.x:F2}, {v3.y:F2}, {v3.z:F2})";

            if (value is Quaternion q)
            {
                var euler = q.eulerAngles;
                return $"({euler.x:F1}, {euler.y:F1}, {euler.z:F1})";
            }

            if (value is float f)
                return f.ToString("F2");

            if (value is double d)
                return d.ToString("F2");

            return value.ToString();
        }
    }
}
