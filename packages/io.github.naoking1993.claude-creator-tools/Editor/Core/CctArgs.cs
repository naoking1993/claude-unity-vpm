using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Tool arguments as plain .NET values (string, long, double, bool, List&lt;object&gt;,
    /// Dictionary&lt;string, object&gt;). Keys are matched as given, then as snake_case and camelCase,
    /// so "include_inactive" and "includeInactive" both work.
    /// </summary>
    public sealed class CctArgs
    {
        readonly Dictionary<string, object> _values;

        public CctArgs(IDictionary<string, object> values = null)
        {
            _values = new Dictionary<string, object>(StringComparer.Ordinal);
            if (values == null) return;
            foreach (var kv in values) _values[kv.Key] = kv.Value;
        }

        public static CctArgs FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new CctArgs();
            return new CctArgs(CctJson.Parse(json) as Dictionary<string, object>);
        }

        public CctArgs Set(string key, object value)
        {
            _values[key] = value;
            return this;
        }

        public IReadOnlyDictionary<string, object> Raw => _values;

        public bool Has(string key) => TryGetRaw(key, out var v) && v != null;

        public bool TryGetRaw(string key, out object value)
        {
            if (_values.TryGetValue(key, out value)) return true;
            var snake = ToSnake(key);
            if (_values.TryGetValue(snake, out value)) return true;
            var camel = ToCamel(snake);
            if (_values.TryGetValue(camel, out value)) return true;
            value = null;
            return false;
        }

        public string GetString(string key, string fallback = null)
        {
            if (!TryGetRaw(key, out var v) || v == null) return fallback;
            if (v is string s) return s;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public bool GetBool(string key, bool fallback)
        {
            if (!TryGetRaw(key, out var v) || v == null) return fallback;
            switch (v)
            {
                case bool b: return b;
                case string s:
                    s = s.Trim().ToLowerInvariant();
                    if (s == "true" || s == "1" || s == "yes" || s == "on") return true;
                    if (s == "false" || s == "0" || s == "no" || s == "off") return false;
                    return fallback;
                case long l: return l != 0;
                case int i: return i != 0;
                case double d: return Math.Abs(d) > double.Epsilon;
            }
            return fallback;
        }

        public long GetLong(string key, long fallback)
        {
            if (!TryGetRaw(key, out var v) || v == null) return fallback;
            try
            {
                if (v is string s)
                    return long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : fallback;
                return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }
            catch (Exception) { return fallback; }
        }

        public int GetInt(string key, int fallback, int min = int.MinValue, int max = int.MaxValue)
        {
            long l = GetLong(key, fallback);
            if (l < min) l = min;
            if (l > max) l = max;
            return (int)l;
        }

        public double GetDouble(string key, double fallback)
        {
            if (!TryGetRaw(key, out var v) || v == null) return fallback;
            try
            {
                if (v is string s)
                    return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : fallback;
                return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch (Exception) { return fallback; }
        }

        /// <summary>Accepts a JSON array, a single string, or a comma/newline separated string.</summary>
        public List<string> GetStringList(string key)
        {
            var result = new List<string>();
            if (!TryGetRaw(key, out var v) || v == null) return result;
            if (v is string s)
            {
                var trimmed = s.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    try { v = CctJson.Parse(trimmed); }
                    catch (FormatException) { /* fall through to splitting */ }
                }
                if (v is string)
                {
                    result.AddRange(s.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim()).Where(x => x.Length > 0));
                    return result;
                }
            }
            if (v is IEnumerable e && !(v is string))
            {
                foreach (var item in e)
                {
                    if (item == null) continue;
                    var str = Convert.ToString(item, CultureInfo.InvariantCulture)?.Trim();
                    if (!string.IsNullOrEmpty(str)) result.Add(str);
                }
            }
            return result;
        }

        /// <summary>Returns the value if it is one of <paramref name="allowed"/> (case-insensitive), else fallback.</summary>
        public string GetEnum(string key, string fallback, params string[] allowed)
        {
            var s = GetString(key, null);
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            s = s.Trim().ToLowerInvariant();
            foreach (var a in allowed)
                if (string.Equals(a, s, StringComparison.OrdinalIgnoreCase)) return a;
            return fallback;
        }

        public static string ToSnake(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            var sb = new StringBuilder(key.Length + 4);
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (char.IsUpper(c))
                {
                    if (i > 0 && key[i - 1] != '_') sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string ToCamel(string snake)
        {
            if (string.IsNullOrEmpty(snake)) return snake;
            var parts = snake.Split('_');
            var sb = new StringBuilder(parts[0]);
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                sb.Append(char.ToUpperInvariant(parts[i][0])).Append(parts[i].Substring(1));
            }
            return sb.ToString();
        }

        public override string ToString() => CctJson.Serialize(_values, false);
    }
}
