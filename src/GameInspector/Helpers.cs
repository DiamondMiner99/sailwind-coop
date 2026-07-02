using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace GameInspector
{
    /// <summary>
    /// Helper functions available in eval context
    /// </summary>
    public static class Helpers
    {
        /// <summary>
        /// Find all instances of a type with limit
        /// </summary>
        public static T[] FindAll<T>(int limit = 20) where T : UnityEngine.Object
        {
            return UnityEngine.Object.FindObjectsOfType<T>().Take(limit).ToArray();
        }

        /// <summary>
        /// Find all instances with count info
        /// </summary>
        public static FindResult<T> FindAllWithCount<T>(int limit = 20) where T : UnityEngine.Object
        {
            var all = UnityEngine.Object.FindObjectsOfType<T>();
            return new FindResult<T>
            {
                Items = all.Take(limit).ToArray(),
                Count = all.Length,
                Showing = Math.Min(limit, all.Length),
                Truncated = all.Length > limit
            };
        }

        /// <summary>
        /// Find GameObject by name
        /// </summary>
        public static GameObject Find(string name)
        {
            return GameObject.Find(name);
        }

        /// <summary>
        /// Get field value (including private)
        /// </summary>
        public static object GetField(object obj, string fieldName)
        {
            if (obj == null) return null;
            var type = obj.GetType();
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
            {
                // Try property
                var prop = type.GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null)
                    return prop.GetValue(obj);
                throw new Exception($"Field/property '{fieldName}' not found on {type.Name}");
            }
            return field.GetValue(obj);
        }

        /// <summary>
        /// Set field value (including private)
        /// </summary>
        public static void SetField(object obj, string fieldName, object value)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            var type = obj.GetType();
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
            {
                var prop = type.GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(obj, Convert.ChangeType(value, prop.PropertyType));
                    return;
                }
                throw new Exception($"Writable field/property '{fieldName}' not found on {type.Name}");
            }
            field.SetValue(obj, Convert.ChangeType(value, field.FieldType));
        }

        /// <summary>
        /// Call method (including private)
        /// </summary>
        public static object CallMethod(object obj, string methodName, params object[] args)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            var type = obj.GetType();
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
                throw new Exception($"Method '{methodName}' not found on {type.Name}");
            return method.Invoke(obj, args);
        }

        /// <summary>
        /// Get type info with fields, properties, methods
        /// </summary>
        public static TypeInfo GetTypeInfo(string typeName)
        {
            Type type = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType(typeName);
                if (type != null) break;

                // Try without namespace
                type = asm.GetTypes().FirstOrDefault(t => t.Name == typeName);
                if (type != null) break;
            }

            if (type == null)
                throw new Exception($"Type '{typeName}' not found");

            return new TypeInfo
            {
                Name = type.Name,
                FullName = type.FullName,
                BaseType = type.BaseType?.Name,
                Fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Select(f => new MemberInfo { Name = f.Name, Type = f.FieldType.Name, IsStatic = f.IsStatic, IsPublic = f.IsPublic })
                    .ToArray(),
                Properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Select(p => new MemberInfo { Name = p.Name, Type = p.PropertyType.Name, IsStatic = p.GetMethod?.IsStatic ?? false, IsPublic = p.GetMethod?.IsPublic ?? false })
                    .ToArray(),
                Methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsSpecialName)
                    .Select(m => new MethodInfo { Name = m.Name, ReturnType = m.ReturnType.Name, Parameters = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")), IsStatic = m.IsStatic, IsPublic = m.IsPublic })
                    .ToArray()
            };
        }

        /// <summary>
        /// Search for types by name pattern
        /// </summary>
        public static string[] FindTypes(string pattern, int limit = 50)
        {
            var results = new List<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            results.Add(type.FullName);
                            if (results.Count >= limit) return results.ToArray();
                        }
                    }
                }
                catch { }
            }
            return results.ToArray();
        }

        /// <summary>
        /// Log to BepInEx console
        /// </summary>
        public static void Log(string message)
        {
            GameInspectorPlugin.Log.LogInfo($"[EVAL] {message}");
        }

        /// <summary>
        /// Search prefabs by name pattern (case-insensitive)
        /// </summary>
        public static PrefabMatch[] SearchPrefabs(string pattern, int limit = 20)
        {
            var results = new List<PrefabMatch>();
            var prefabsDir = UnityEngine.Object.FindObjectOfType<PrefabsDirectory>();
            if (prefabsDir == null)
                throw new Exception("PrefabsDirectory not found");

            var directory = (GameObject[])GetField(prefabsDir, "directory");
            if (directory == null)
                throw new Exception("PrefabsDirectory.directory is null");

            for (int i = 0; i < directory.Length && results.Count < limit; i++)
            {
                if (directory[i] != null &&
                    directory[i].name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    results.Add(new PrefabMatch { Index = i, Name = directory[i].name });
                }
            }
            return results.ToArray();
        }

        /// <summary>
        /// Spawn a prefab 2m in front of the camera
        /// </summary>
        public static SpawnResult SpawnPrefab(int index)
        {
            var prefabsDir = UnityEngine.Object.FindObjectOfType<PrefabsDirectory>();
            if (prefabsDir == null)
                throw new Exception("PrefabsDirectory not found");

            var directory = (GameObject[])GetField(prefabsDir, "directory");
            if (directory == null)
                throw new Exception("PrefabsDirectory.directory is null");

            if (index < 0 || index >= directory.Length)
                throw new Exception($"Index {index} out of range (0-{directory.Length - 1})");

            var prefab = directory[index];
            if (prefab == null)
                throw new Exception($"Prefab at index {index} is null");

            var cam = Camera.main;
            if (cam == null)
                throw new Exception("Camera.main not found");

            var spawnPos = cam.transform.position + cam.transform.forward * 2f;
            var spawned = UnityEngine.Object.Instantiate(prefab, spawnPos, Quaternion.identity);

            // Mark as "sold" (in world, not in shop inventory)
            // Note: layer stays at 0 (Default) for raycasting - layer 2 is only when held
            var shipItem = spawned.GetComponent<ShipItem>();
            if (shipItem != null)
            {
                shipItem.sold = true;
            }

            return new SpawnResult
            {
                Name = spawned.name,
                Position = spawnPos
            };
        }

        public class FindResult<T>
        {
            public T[] Items { get; set; }
            public int Count { get; set; }
            public int Showing { get; set; }
            public bool Truncated { get; set; }
        }

        public class TypeInfo
        {
            public string Name { get; set; }
            public string FullName { get; set; }
            public string BaseType { get; set; }
            public MemberInfo[] Fields { get; set; }
            public MemberInfo[] Properties { get; set; }
            public MethodInfo[] Methods { get; set; }
        }

        public class MemberInfo
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public bool IsStatic { get; set; }
            public bool IsPublic { get; set; }
        }

        public class MethodInfo
        {
            public string Name { get; set; }
            public string ReturnType { get; set; }
            public string Parameters { get; set; }
            public bool IsStatic { get; set; }
            public bool IsPublic { get; set; }
        }

        public class PrefabMatch
        {
            public int Index { get; set; }
            public string Name { get; set; }
        }

        public class SpawnResult
        {
            public string Name { get; set; }
            public Vector3 Position { get; set; }
        }
    }
}
