using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Naoking.ClaudeCreatorTools
{
    /// <summary>
    /// Minimal JSON writer/reader so the core assembly has no Newtonsoft dependency
    /// (avatar projects without MCP for Unity may not have com.unity.nuget.newtonsoft-json).
    /// Values: null, bool, string, char, enums (as name), integral and floating numbers,
    /// IDictionary (keys stringified), IEnumerable, and a few Unity structs via CctJson.Convert hooks.
    /// </summary>
    public static class CctJson
    {
        /// <summary>Optional converter for non-primitive values (set by Unity-aware code).</summary>
        public static Func<object, object> ValueConverter;

        public static string Serialize(object value, bool pretty = true)
        {
            var sb = new StringBuilder(4096);
            Write(sb, value, pretty, 0, 0);
            return sb.ToString();
        }

        const int MaxDepth = 64;

        static void Write(StringBuilder sb, object v, bool pretty, int indent, int depth)
        {
            if (depth > MaxDepth) { WriteString(sb, "<max depth>"); return; }
            if (v == null) { sb.Append("null"); return; }
            switch (v)
            {
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case char c: WriteString(sb, c.ToString()); return;
                case Enum e: WriteString(sb, e.ToString()); return;
                case int or long or short or byte or sbyte or uint or ushort or ulong:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
                case float f: WriteDouble(sb, f); return;
                case double d: WriteDouble(sb, d); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
            }
            if (ValueConverter != null)
            {
                var converted = ValueConverter(v);
                if (!ReferenceEquals(converted, v)) { Write(sb, converted, pretty, indent, depth + 1); return; }
            }
            if (v is IDictionary dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, pretty, indent + 1);
                    WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(pretty ? ": " : ":");
                    Write(sb, kv.Value, pretty, indent + 1, depth + 1);
                }
                if (!first) NewLine(sb, pretty, indent);
                sb.Append('}');
                return;
            }
            if (v is IEnumerable enumerable)
            {
                var list = new List<object>();
                foreach (var item in enumerable) list.Add(item); // materialize once (may be a lazy sequence)
                sb.Append('[');
                bool first = true;
                bool scalarList = true;
                foreach (var item in list)
                {
                    if (!(item == null || item is string || item is bool || item is IConvertible)) { scalarList = false; break; }
                }
                foreach (var item in list)
                {
                    if (!first) sb.Append(pretty && scalarList ? ", " : ",");
                    first = false;
                    if (!scalarList) NewLine(sb, pretty, indent + 1);
                    Write(sb, item, pretty, indent + 1, depth + 1);
                }
                if (!first && !scalarList) NewLine(sb, pretty, indent);
                sb.Append(']');
                return;
            }
            WriteString(sb, v.ToString());
        }

        static void NewLine(StringBuilder sb, bool pretty, int indent)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', indent * 2);
        }

        static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            if (Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 1e15)
            {
                sb.Append(((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture));
                return;
            }
            sb.Append(Math.Round(d, 6).ToString("R", CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>
        /// Deep-copies a value into only Dictionary&lt;string, object&gt;, List&lt;object&gt; and primitives,
        /// applying <see cref="ValueConverter"/>. Used before handing data to another serializer
        /// (Newtonsoft in the MCP bridge) so both outputs are identical and never walk Unity object graphs.
        /// </summary>
        public static object ToPlain(object v, int depth = 0)
        {
            if (depth > MaxDepth) return "<max depth>";
            switch (v)
            {
                case null: return null;
                case string _: case bool _: case char _:
                case int _: case long _: case short _: case byte _: case sbyte _:
                case uint _: case ushort _: case ulong _: case decimal _:
                    return v;
                case float f: return double.IsNaN(f) || double.IsInfinity(f) ? null : (object)Math.Round((double)f, 6);
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? null : (object)d;
                case Enum e: return e.ToString();
            }
            if (ValueConverter != null)
            {
                var converted = ValueConverter(v);
                if (!ReferenceEquals(converted, v)) return ToPlain(converted, depth + 1);
            }
            if (v is IDictionary dict)
            {
                var d = new Dictionary<string, object>();
                foreach (DictionaryEntry kv in dict)
                    d[Convert.ToString(kv.Key, CultureInfo.InvariantCulture)] = ToPlain(kv.Value, depth + 1);
                return d;
            }
            if (v is IEnumerable list)
            {
                var l = new List<object>();
                foreach (var item in list) l.Add(ToPlain(item, depth + 1));
                return l;
            }
            return v.ToString();
        }

        // ---------------- reader (for snapshot manifests and menu-provided args) ----------------

        /// <summary>Parses JSON into Dictionary&lt;string, object&gt; / List&lt;object&gt; / string / double / long / bool / null.</summary>
        public static object Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            int i = 0;
            var v = ParseValue(json, ref i);
            SkipWs(json, ref i);
            if (i != json.Length) throw new FormatException($"Unexpected trailing characters at {i}");
            return v;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (c == 'f' && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (c == 'n' && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            return ParseNumber(s, ref i);
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException($"Expected string key at {i}");
                var key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException($"Expected ':' at {i}");
                i++;
                d[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return d; }
                throw new FormatException($"Expected ',' or '}}' at {i}");
            }
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var l = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return l; }
                throw new FormatException($"Expected ',' or ']' at {i}");
            }
        }

        static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("Bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException($"Bad escape \\{e}");
                }
            }
            throw new FormatException("Unterminated string");
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            var tok = s.Substring(start, i - start);
            if (tok.Length == 0) throw new FormatException($"Unexpected character '{s[start]}' at {start}");
            if (tok.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 &&
                long.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            return double.Parse(tok, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
