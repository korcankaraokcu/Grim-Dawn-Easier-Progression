using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EasierProgression
{
    /// <summary>Minimal JSON for settings files: objects, arrays, strings, numbers, bools, null.
    /// Objects are read as ordered lists of key/value pairs; numbers as double.</summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            int i = 0;
            object v = Value(text, ref i);
            Ws(text, ref i);
            if (i != text.Length) throw new FormatException("trailing characters in JSON");
            return v;
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                var obj = new List<KeyValuePair<string, object>>();
                i++;
                Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                while (true)
                {
                    Ws(s, ref i);
                    string key = Str(s, ref i);
                    Ws(s, ref i);
                    Expect(s, ref i, ':');
                    obj.Add(new KeyValuePair<string, object>(key, Value(s, ref i)));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return obj;
                }
            }
            if (c == '[')
            {
                var arr = new List<object>();
                i++;
                Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return arr; }
                while (true)
                {
                    arr.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return arr;
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i) throw new FormatException($"unexpected '{c}' in JSON");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static bool Word(string s, ref int i, string w)
        {
            if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
            i += w.Length;
            return true;
        }

        static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException($"expected '{c}' in JSON");
            i++;
        }

        static string Str(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated JSON string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber));
                        i += 4;
                        break;
                    default: sb.Append(e); break; // \" \\ \/
                }
            }
        }

        /// <summary>Serialize with 2-space indentation. Objects are lists of key/value pairs.</summary>
        public static string Write(object v)
        {
            var sb = new StringBuilder();
            Emit(sb, v, 0);
            return sb.ToString();
        }

        static void Emit(StringBuilder sb, object v, int depth)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: Quote(sb, s); break;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case long n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case double d:
                    string t = d.ToString("R", CultureInfo.InvariantCulture);
                    if (t.IndexOfAny(new[] { '.', 'E', 'N', 'I' }) < 0) t += ".0"; // 2 -> 2.0
                    sb.Append(t);
                    break;
                case IEnumerable<KeyValuePair<string, object>> obj:
                    var items = new List<KeyValuePair<string, object>>(obj);
                    if (items.Count == 0) { sb.Append("{}"); break; }
                    sb.Append("{\n");
                    for (int k = 0; k < items.Count; k++)
                    {
                        sb.Append(' ', 2 * (depth + 1));
                        Quote(sb, items[k].Key);
                        sb.Append(": ");
                        Emit(sb, items[k].Value, depth + 1);
                        sb.Append(k + 1 < items.Count ? ",\n" : "\n");
                    }
                    sb.Append(' ', 2 * depth).Append('}');
                    break;
                default: throw new ArgumentException("cannot serialize " + v.GetType());
            }
        }

        static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
