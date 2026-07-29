// MiniJSON£¨¾«¼ò°æ£¬Ö§³ÖDictionary£©
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
                    char c = json[index++];
                    if (c == '"') break;
                    sb.Append(c);
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
                return double.Parse(num);
            }

            void SkipWhitespace()
            {
                while (index < json.Length && char.IsWhiteSpace(json[index]))
                    index++;
            }
        }
    }
}