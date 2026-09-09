// MiniJSON������棬֧��Dictionary��
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System;

public static class MiniJSON
{
    public static object Deserialize(string json)
    {
        return Json.Deserialize(json);
    }

    sealed class Json
    {
        public static object Deserialize(string json)
        {
            if (json == null) return null;
            return Parser.Parse(json);
        }

        sealed class Parser : IDisposable
        {
            const string WORD_BREAK = "{}[],:\"";

            public static object Parse(string json)
            {
                using (var instance = new Parser(json))
                {
                    return instance.ParseValue();
                }
            }

            private string json;
            private int index;

            private Parser(string json)
            {
                this.json = json;
            }

            public void Dispose() { }

            public object ParseValue()
            {
                SkipWhitespace();
                if (index >= json.Length) return null;

                char c = json[index];

                if (c == '{') return ParseObject();
                if (c == '[') return ParseArray();
                if (c == '"') return ParseString();
                if (c == 't') { index += 4; return true; }
                if (c == 'f') { index += 5; return false; }
                if (c == 'n') { index += 4; return null; }

                return ParseNumber();
            }

            Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>();
                index++; // skip {

                while (true)
                {
                    SkipWhitespace();
                    if (json[index] == '}') { index++; break; }

                    string key = ParseString();
                    SkipWhitespace();
                    index++; // skip :

                    object value = ParseValue();
                    dict[key] = value;

                    SkipWhitespace();
                    if (json[index] == ',') index++;
                    else if (json[index] == '}') { index++; break; }
                }
                return dict;
            }

            List<object> ParseArray()
            {
                var list = new List<object>();
                index++; // skip [

                while (true)
                {
                    SkipWhitespace();
                    if (json[index] == ']') { index++; break; }

                    list.Add(ParseValue());

                    SkipWhitespace();
                    if (json[index] == ',') index++;
                }
                return list;
            }

            string ParseString()
            {
                var sb = new StringBuilder();
                index++; // skip "

                while (true)
                {
                    if (index >= json.Length) throw new Exception("Unterminated string");
                    char c = json[index++];
                    if (c == '"') break;
                    if (c == '\\')
                    {
                        if (index >= json.Length) throw new Exception("Unterminated escape");
                        char e = json[index++];
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
                                if (index + 4 > json.Length) throw new Exception("Bad \\u escape");
                                sb.Append((char)Convert.ToInt32(json.Substring(index, 4), 16));
                                index += 4;
                                break;
                            default: throw new Exception("Bad escape: \\" + e);
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                return sb.ToString();
            }

            object ParseNumber()
            {
                int start = index;

                while (index < json.Length &&
                       "0123456789+-.eE".IndexOf(json[index]) != -1)
                {
                    index++;
                }

                string num = json.Substring(start, index - start);
                return double.Parse(num, System.Globalization.CultureInfo.InvariantCulture);
            }

            void SkipWhitespace()
            {
                while (index < json.Length && char.IsWhiteSpace(json[index]))
                    index++;
            }
        }
    }
}