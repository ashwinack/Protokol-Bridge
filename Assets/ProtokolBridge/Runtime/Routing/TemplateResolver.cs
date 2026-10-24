using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ProtokolBridge
{
    /// <summary>
    /// Expands {placeholder} tokens in a rule's output templates against the
    /// message that triggered it. This is what lets an operator type
    /// "/light/{field0}" and have it become "/light/red" at runtime.
    /// </summary>
    public sealed class TemplateResolver
    {
        private readonly StringBuilder _sb = new StringBuilder(128);
        private string[] _fields = Array.Empty<string>();
        private BridgeMessage _message;
        private Match _regexMatch;

        /// <summary>Every token the UI should offer as help text.</summary>
        public static readonly string[] KnownTokens =
        {
            "{text}", "{address}", "{remote}", "{source}", "{time}", "{count}",
            "{arg0}", "{arg1}", "{arg2}",
            "{field0}", "{field1}", "{field2}",
            "{g1}", "{g2}",
            "{hex}"
        };

        private long _counter;

        public void Prepare(BridgeMessage message, string fieldSeparator, Match regexMatch)
        {
            _message = message;
            _regexMatch = regexMatch;
            _counter++;

            var source = message.Text ?? string.Empty;
            if (string.IsNullOrEmpty(fieldSeparator))
            {
                _fields = source.Length > 0 ? new[] { source } : Array.Empty<string>();
            }
            else
            {
                _fields = source.Split(new[] { fieldSeparator }, StringSplitOptions.None);
                for (var i = 0; i < _fields.Length; i++) _fields[i] = _fields[i].Trim();
            }
        }

        public string Resolve(string template)
        {
            if (string.IsNullOrEmpty(template)) return string.Empty;
            if (template.IndexOf('{') < 0) return template;

            _sb.Length = 0;

            for (var i = 0; i < template.Length; i++)
            {
                var c = template[i];
                if (c != '{')
                {
                    _sb.Append(c);
                    continue;
                }

                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    _sb.Append(template, i, template.Length - i);
                    break;
                }

                var token = template.Substring(i + 1, close - i - 1);
                _sb.Append(Lookup(token));
                i = close;
            }

            return _sb.ToString();
        }

        private string Lookup(string token)
        {
            if (string.IsNullOrEmpty(token)) return string.Empty;
            if (_message == null) return string.Empty;

            switch (token.ToLowerInvariant())
            {
                case "text": return _message.Text ?? string.Empty;
                case "address": return _message.Address ?? string.Empty;
                case "remote": return _message.Remote ?? string.Empty;
                case "source": return _message.EndpointId ?? string.Empty;
                case "time": return _message.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                case "count": return _counter.ToString(CultureInfo.InvariantCulture);
                case "hex": return _message.ToHex();
            }

            if (TryIndexed(token, "arg", out var argIndex)) return _message.ArgAsString(argIndex);

            if (TryIndexed(token, "field", out var fieldIndex))
                return fieldIndex >= 0 && fieldIndex < _fields.Length ? _fields[fieldIndex] : string.Empty;

            if (TryIndexed(token, "g", out var groupIndex))
            {
                if (_regexMatch == null || !_regexMatch.Success) return string.Empty;
                if (groupIndex < 0 || groupIndex >= _regexMatch.Groups.Count) return string.Empty;
                return _regexMatch.Groups[groupIndex].Value;
            }

            // Unknown token: leave it visible so the operator can see the typo.
            return "{" + token + "}";
        }

        private static bool TryIndexed(string token, string prefix, out int index)
        {
            index = -1;
            if (token.Length <= prefix.Length) return false;
            if (!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return int.TryParse(token.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
        }

        /// <summary>Converts a resolved string into the type the rule asked for.</summary>
        public static object Coerce(string resolved, ArgType type)
        {
            resolved = resolved ?? string.Empty;

            switch (type)
            {
                case ArgType.Int:
                {
                    int i;
                    if (int.TryParse(resolved, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
                    float f;
                    if (float.TryParse(resolved, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return (int)f;
                    return 0;
                }
                case ArgType.Float:
                {
                    float f;
                    return float.TryParse(resolved, NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : 0f;
                }
                case ArgType.Bool:
                {
                    if (resolved.Equals("true", StringComparison.OrdinalIgnoreCase) || resolved == "1") return true;
                    if (resolved.Equals("false", StringComparison.OrdinalIgnoreCase) || resolved == "0") return false;
                    return resolved.Length > 0;
                }
                case ArgType.String:
                    return resolved;
                default:
                {
                    int i;
                    if (int.TryParse(resolved, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
                    float f;
                    if (float.TryParse(resolved, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f;
                    return resolved;
                }
            }
        }
    }
}
