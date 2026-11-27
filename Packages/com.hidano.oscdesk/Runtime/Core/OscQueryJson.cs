// OscQueryJson.cs — OSCQuery の JSON の書き出しと、WebSocket の命令の読み取りに要る最小の JSON(HID-490)。
// 外部の JSON ライブラリを使わない(D-095)。UnityEngine に依存しない。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OscDesk.Receiver
{
    /// <summary>JSON の値の書き出し。数は InvariantCulture、文字列は JSON の規則でエスケープする。</summary>
    public static class OscQueryJson
    {
        public static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            foreach (var c in text ?? string.Empty)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        // 制御文字と U+2028 / U+2029(JavaScript の文字列に入れると壊れる)は \uXXXX にする
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
        }

        /// <summary>int / float / double / bool / string を JSON の値として書く(それ以外は null)。</summary>
        public static void WriteValue(StringBuilder builder, object value)
        {
            switch (value)
            {
                case null: builder.Append("null"); break;
                case string s: WriteString(builder, s); break;
                case bool b: builder.Append(b ? "true" : "false"); break;
                case int i: builder.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case float f: WriteNumber(builder, f); break;
                case double d: WriteNumber(builder, d); break;
                default: builder.Append("null"); break;
            }
        }

        public static void WriteNumber(StringBuilder builder, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                builder.Append("null"); // JSON に NaN・無限大は書けない
                return;
            }

            builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void WriteNumber(StringBuilder builder, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                builder.Append("null");
                return;
            }

            // float の最短表記(0.1f を 0.100000001490116 と書かない)
            builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 値が文字列だけの平たいオブジェクト(<c>{"COMMAND":"LISTEN","DATA":"/a"}</c>)を読む。入れ子・配列・文字列以外の値は
        /// 読み飛ばさずに失敗にする(OSCQuery の WebSocket の命令はこの形だけ)。同じキーは後の値が勝つ。
        /// </summary>
        public static bool TryParseFlatObject(string text, out Dictionary<string, string> result)
        {
            result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (text == null)
            {
                return false;
            }

            var position = 0;
            SkipSpace(text, ref position);
            if (!Expect(text, ref position, '{'))
            {
                return false;
            }

            SkipSpace(text, ref position);
            if (Expect(text, ref position, '}'))
            {
                return AtEnd(text, ref position);
            }

            while (true)
            {
                SkipSpace(text, ref position);
                if (!TryReadString(text, ref position, out var key))
                {
                    return false;
                }

                SkipSpace(text, ref position);
                if (!Expect(text, ref position, ':'))
                {
                    return false;
                }

                SkipSpace(text, ref position);
                if (!TryReadString(text, ref position, out var value))
                {
                    return false;
                }

                result[key] = value;
                SkipSpace(text, ref position);
                if (Expect(text, ref position, ','))
                {
                    continue;
                }

                return Expect(text, ref position, '}') && AtEnd(text, ref position);
            }
        }

        private static bool AtEnd(string text, ref int position)
        {
            SkipSpace(text, ref position);
            return position == text.Length;
        }

        private static void SkipSpace(string text, ref int position)
        {
            while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' || text[position] == '\n'))
            {
                position += 1;
            }
        }

        private static bool Expect(string text, ref int position, char c)
        {
            if (position < text.Length && text[position] == c)
            {
                position += 1;
                return true;
            }

            return false;
        }

        private static bool TryReadString(string text, ref int position, out string value)
        {
            value = null;
            if (!Expect(text, ref position, '"'))
            {
                return false;
            }

            var builder = new StringBuilder();
            while (position < text.Length)
            {
                var c = text[position++];
                if (c == '"')
                {
                    value = builder.ToString();
                    return true;
                }

                if (c < 0x20)
                {
                    return false;
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (position >= text.Length)
                {
                    return false;
                }

                var escape = text[position++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (position + 4 > text.Length
                            || !int.TryParse(text.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            return false;
                        }

                        builder.Append((char)code);
                        position += 4;
                        break;
                    default:
                        return false;
                }
            }

            return false;
        }
    }
}
