using System;
using System.Collections.Generic;
using FfiSharp.Abi;

namespace FfiSharp
{
    /// <summary>
    /// A boxed C struct value, accessed by field name. This is the managed
    /// representation returned by struct-returning functions and accepted by struct
    /// (and struct-pointer) parameters. It does NOT rely on CLR struct layout.
    /// </summary>
    public sealed class FfiStruct
    {
        private readonly Dictionary<string, object> _fields;

        internal FfiStruct(FfiStructType type)
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
            _fields = new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public FfiStructType Type { get; }

        public object this[string name]
        {
            get => GetField(name);
            set => SetField(name, value);
        }

        public void SetField(string name, object value)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            _fields[name] = value;
        }

        public object GetField(string name)
        {
            if (_fields.TryGetValue(name, out object value))
                return value;
            throw new KeyNotFoundException($"Struct '{Type.Name}' has no value for field '{name}'");
        }

        public bool TryGetField(string name, out object value)
            => _fields.TryGetValue(name, out value);

        public IReadOnlyDictionary<string, object> Fields => _fields;

        public override string ToString()
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, object> kv in _fields)
                parts.Add(kv.Key + "=" + (kv.Value?.ToString() ?? "null"));
            return "{" + string.Join(", ", parts) + "}";
        }
    }
}
