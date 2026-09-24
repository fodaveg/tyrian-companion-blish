using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TyrianCompanion.BlishBridge {

    /// <summary>
    /// Hand-rolled JSON for exactly what `docs/SPEC-puente-ingame.md` protocol v2 needs: one flat
    /// object per line, whose values are a string, an integer, or <c>null</c> — never nested
    /// objects or arrays, never a float. No NuGet dependency for this: every message the wire
    /// contract defines fits that shape, and the module already hand-rolled base64url and a
    /// dedupe scan in its v1 predecessor (<c>AlertWireMessages.cs</c>). Encoding is this module's
    /// own concern (it only ever writes what it means to send); decoding has to be tolerant of
    /// whatever the plugin sends, per the spec's "el addon ignora... y descarta... sin cerrar".
    /// </summary>
    internal static class FlatJsonLine {

        /// <summary>
        /// Builds <c>{"key1":value1,"key2":value2,...}</c> in the given order. A <see cref="string"/>
        /// member is JSON-escaped and quoted; a <see cref="long"/> or <see cref="int"/> is rendered
        /// as a bare integer; <c>null</c> is rendered as the JSON literal. Any other value type is a
        /// programming error in this module, not a wire concern, so it throws rather than silently
        /// emitting something the plugin's strict parser would reject.
        /// </summary>
        public static string Encode(IReadOnlyList<KeyValuePair<string, object>> members) {
            var builder = new StringBuilder();
            builder.Append('{');
            for (var index = 0; index < members.Count; index++) {
                if (index > 0) builder.Append(',');
                builder.Append(EncodeString(members[index].Key));
                builder.Append(':');
                AppendValue(builder, members[index].Value);
            }
            builder.Append('}');
            return builder.ToString();
        }

        private static void AppendValue(StringBuilder builder, object value) {
            switch (value) {
                case null:
                    builder.Append("null");
                    return;
                case string text:
                    builder.Append(EncodeString(text));
                    return;
                case int number:
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    return;
                case long number:
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    return;
                default:
                    throw new ArgumentException($"FlatJsonLine.Encode does not know how to render a {value.GetType()}.");
            }
        }

        /// <summary>JSON-escapes and quotes one string, per RFC 8259 section 7.</summary>
        public static string EncodeString(string value) {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (var character in value) {
                switch (character) {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 0x20) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else builder.Append(character);
                        break;
                }
            }
            builder.Append('"');
            return builder.ToString();
        }

        /// <summary>
        /// Parses one flat JSON object: a string, an integer literal, <c>true</c>/<c>false</c> or
        /// <c>null</c> per member, no nesting. Returns <c>false</c> — never throws — on anything
        /// this shape does not cover (nested objects/arrays, a float, nothing after the closing
        /// brace but trailing garbage, a duplicate key), which is exactly "not a message this
        /// contract defines" from the addon's point of view: the caller drops the line.
        /// </summary>
        public static bool TryParse(string text, out Dictionary<string, object> value) {
            value = null;
            var position = 0;
            SkipWhitespace(text, ref position);
            if (!TryExpect(text, ref position, '{')) return false;
            var result = new Dictionary<string, object>();
            SkipWhitespace(text, ref position);
            if (Peek(text, position) != '}') {
                while (true) {
                    SkipWhitespace(text, ref position);
                    if (!TryParseString(text, ref position, out var key)) return false;
                    SkipWhitespace(text, ref position);
                    if (!TryExpect(text, ref position, ':')) return false;
                    SkipWhitespace(text, ref position);
                    if (!TryParseValue(text, ref position, out var member)) return false;
                    if (result.ContainsKey(key)) return false; // duplicate key: same rule the plugin enforces
                    result[key] = member;
                    SkipWhitespace(text, ref position);
                    var next = Peek(text, position);
                    if (next == ',') { position++; continue; }
                    if (next == '}') { position++; break; }
                    return false;
                }
            } else {
                position++;
            }
            SkipWhitespace(text, ref position);
            if (position != text.Length) return false; // trailing content after the object
            value = result;
            return true;
        }

        private static bool TryParseValue(string text, ref int position, out object value) {
            value = null;
            var next = Peek(text, position);
            if (next == '"') {
                var parsed = TryParseString(text, ref position, out var stringValue);
                value = stringValue;
                return parsed;
            }
            if (next == '{' || next == '[') return false; // out of this wire's scope: never nested
            if (string.CompareOrdinal(text, position, "null", 0, 4) == 0 && Matches(text, position, "null")) {
                position += 4;
                value = null;
                return true;
            }
            if (Matches(text, position, "true")) { position += 4; value = true; return true; }
            if (Matches(text, position, "false")) { position += 5; value = false; return true; }
            return TryParseInteger(text, ref position, out value);
        }

        private static bool Matches(string text, int position, string literal) {
            return position + literal.Length <= text.Length && string.CompareOrdinal(text, position, literal, 0, literal.Length) == 0;
        }

        /// <summary>Integers only, optional leading <c>-</c>, no fraction and no exponent — this wire never sends either.</summary>
        private static bool TryParseInteger(string text, ref int position, out object value) {
            value = null;
            var start = position;
            if (position < text.Length && text[position] == '-') position++;
            var digitsStart = position;
            while (position < text.Length && text[position] >= '0' && text[position] <= '9') position++;
            if (position == digitsStart) return false;
            if (position < text.Length && (text[position] == '.' || text[position] == 'e' || text[position] == 'E')) return false;
            if (!long.TryParse(text.Substring(start, position - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)) return false;
            value = parsed;
            return true;
        }

        private static bool TryParseString(string text, ref int position, out string value) {
            value = null;
            if (!TryExpect(text, ref position, '"')) return false;
            var builder = new StringBuilder();
            while (true) {
                if (position >= text.Length) return false;
                var character = text[position];
                if (character == '"') { position++; break; }
                if (character == '\\') {
                    position++;
                    if (position >= text.Length) return false;
                    switch (text[position]) {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (position + 4 >= text.Length) return false;
                            if (!ushort.TryParse(text.Substring(position + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)) return false;
                            builder.Append((char)code);
                            position += 4;
                            break;
                        default: return false;
                    }
                    position++;
                } else if (character < 0x20) {
                    return false; // a raw control character must be escaped, same rule as the plugin's frame_utf8/frame_json checks
                } else {
                    builder.Append(character);
                    position++;
                }
            }
            value = builder.ToString();
            return true;
        }

        private static char Peek(string text, int position) => position < text.Length ? text[position] : '\0';

        private static bool TryExpect(string text, ref int position, char expected) {
            if (position >= text.Length || text[position] != expected) return false;
            position++;
            return true;
        }

        private static void SkipWhitespace(string text, ref int position) {
            while (position < text.Length) {
                var character = text[position];
                if (character != ' ' && character != '\t' && character != '\r' && character != '\n') break;
                position++;
            }
        }
    }
}
