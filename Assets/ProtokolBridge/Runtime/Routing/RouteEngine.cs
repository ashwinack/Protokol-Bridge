using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ProtokolBridge
{
    /// <summary>
    /// Evaluates every enabled rule against an inbound message and produces the
    /// outbound messages it should turn into. Pure logic: it opens no sockets and
    /// touches no Unity objects, so it is straightforward to unit-test.
    /// </summary>
    public sealed class RouteEngine
    {
        public struct Dispatch
        {
            public string TargetEndpointId;
            public BridgeMessage Message;
            public RouteRule Rule;
            /// <summary>Taken from the individual output, not the rule.</summary>
            public int DelayMs;
        }

        private readonly List<RouteRule> _rules = new List<RouteRule>();
        private readonly Dictionary<string, Regex> _regexCache = new Dictionary<string, Regex>();
        private readonly TemplateResolver _resolver = new TemplateResolver();

        /// <summary>Looks up a target endpoint's kind so OSC targets get address+args
        /// and everything else gets a text payload.</summary>
        public Func<string, TransportKind?> TargetKindLookup;

        public IReadOnlyList<RouteRule> Rules { get { return _rules; } }

        public void SetRules(IEnumerable<RouteRule> rules)
        {
            _rules.Clear();
            _regexCache.Clear();
            if (rules != null) _rules.AddRange(rules);
        }

        public void Add(RouteRule rule)
        {
            if (rule != null) _rules.Add(rule);
        }

        public void Remove(RouteRule rule)
        {
            _rules.Remove(rule);
        }

        public void Evaluate(BridgeMessage inbound, List<Dispatch> output)
        {
            if (inbound == null || output == null) return;

            var nowUtc = DateTime.UtcNow;

            foreach (var rule in _rules)
            {
                if (!rule.enabled) continue;
                if (rule.outputs == null || rule.outputs.Count == 0) continue;

                if (rule.sourceEndpointId != RouteRule.AnyEndpoint &&
                    !string.Equals(rule.sourceEndpointId, inbound.EndpointId, StringComparison.Ordinal))
                    continue;

                Match regexMatch;
                if (!Matches(rule, inbound, out regexMatch)) continue;

                if (rule.throttleMs > 0 && rule.LastFiredUtc != default(DateTime) &&
                    (nowUtc - rule.LastFiredUtc).TotalMilliseconds < rule.throttleMs)
                    continue;

                var fired = false;

                foreach (var ruleOutput in rule.outputs)
                {
                    if (string.IsNullOrEmpty(ruleOutput.targetEndpointId)) continue;

                    // An output aimed back at its own source would loop forever.
                    if (string.Equals(ruleOutput.targetEndpointId, inbound.EndpointId, StringComparison.Ordinal)) continue;

                    var message = Build(rule, ruleOutput, inbound, regexMatch);
                    if (message == null) continue;

                    fired = true;

                    output.Add(new Dispatch
                    {
                        TargetEndpointId = ruleOutput.targetEndpointId,
                        Message = message,
                        Rule = rule,
                        DelayMs = ruleOutput.delayMs
                    });
                }

                if (!fired) continue;

                rule.HitCount++;
                rule.LastFiredUtc = nowUtc;

                if (rule.stopOnMatch) break;
            }
        }

        /// <summary>
        /// Runs one rule against a sample message without firing it, so the rule
        /// editor can show the operator exactly what their template will produce.
        /// Never touches HitCount or throttle state.
        /// </summary>
        public bool Preview(RouteRule rule, RuleOutput ruleOutput, BridgeMessage sample,
                            out BridgeMessage outbound, out string reason)
        {
            outbound = null;
            reason = null;

            if (rule == null || ruleOutput == null || sample == null)
            {
                reason = "Nothing to preview.";
                return false;
            }

            Match regexMatch;
            if (!Matches(rule, sample, out regexMatch))
            {
                reason = "This input does not match the rule's condition.";
                return false;
            }

            if (string.IsNullOrEmpty(ruleOutput.targetEndpointId))
            {
                reason = "Pick a target connection first.";
                return false;
            }

            outbound = Build(rule, ruleOutput, sample, regexMatch);
            if (outbound == null)
            {
                reason = "The target connection no longer exists.";
                return false;
            }

            return true;
        }

        private bool Matches(RouteRule rule, BridgeMessage message, out Match regexMatch)
        {
            regexMatch = null;

            if (rule.matchMode == MatchMode.Any) return true;

            var comparison = rule.caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var pattern = rule.matchValue ?? string.Empty;

            if (rule.matchMode == MatchMode.OscAddress)
            {
                var address = message.Address ?? string.Empty;
                if (address.Length == 0) return false;
                return WildcardMatch(address, pattern, rule.caseSensitive);
            }

            var subject = message.Text ?? string.Empty;

            switch (rule.matchMode)
            {
                case MatchMode.Equals:
                    return string.Equals(subject, pattern, comparison);
                case MatchMode.Contains:
                    return pattern.Length == 0 || subject.IndexOf(pattern, comparison) >= 0;
                case MatchMode.StartsWith:
                    return subject.StartsWith(pattern, comparison);
                case MatchMode.Regex:
                {
                    var regex = GetRegex(pattern, rule.caseSensitive);
                    if (regex == null) return false;
                    regexMatch = regex.Match(subject);
                    return regexMatch.Success;
                }
                default:
                    return false;
            }
        }

        private Regex GetRegex(string pattern, bool caseSensitive)
        {
            if (string.IsNullOrEmpty(pattern)) return null;

            var key = (caseSensitive ? "cs:" : "ci:") + pattern;
            Regex cached;
            if (_regexCache.TryGetValue(key, out cached)) return cached;

            try
            {
                var options = RegexOptions.CultureInvariant;
                if (!caseSensitive) options |= RegexOptions.IgnoreCase;
                cached = new Regex(pattern, options);
            }
            catch (ArgumentException)
            {
                cached = null; // invalid pattern: cache the failure so we do not retry every message
            }

            _regexCache[key] = cached;
            return cached;
        }

        /// <summary>OSC-style wildcard match: * for any run, ? for one character.</summary>
        public static bool WildcardMatch(string input, string pattern, bool caseSensitive)
        {
            if (string.IsNullOrEmpty(pattern)) return true;

            if (!caseSensitive)
            {
                input = input.ToLowerInvariant();
                pattern = pattern.ToLowerInvariant();
            }

            int i = 0, p = 0, starIndex = -1, match = 0;

            while (i < input.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == input[i]))
                {
                    i++;
                    p++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    starIndex = p;
                    match = i;
                    p++;
                }
                else if (starIndex != -1)
                {
                    p = starIndex + 1;
                    match++;
                    i = match;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        private BridgeMessage Build(RouteRule rule, RuleOutput ruleOutput, BridgeMessage inbound, Match regexMatch)
        {
            if (ruleOutput.outputMode == OutputMode.Passthrough)
            {
                var copy = new BridgeMessage
                {
                    Address = inbound.Address,
                    Text = inbound.Text,
                    Raw = inbound.Raw,
                    Remote = inbound.Remote,
                    TimestampUtc = DateTime.UtcNow
                };
                copy.Args.AddRange(inbound.Args);
                return copy;
            }

            _resolver.Prepare(inbound, rule.fieldSeparator, regexMatch);

            var targetKind = TargetKindLookup != null ? TargetKindLookup(ruleOutput.targetEndpointId) : null;
            if (targetKind == null) return null;

            var outbound = new BridgeMessage
            {
                TimestampUtc = DateTime.UtcNow,
                Remote = inbound.Remote
            };

            if (targetKind.Value == TransportKind.Osc)
            {
                outbound.Address = _resolver.Resolve(ruleOutput.outputAddress);
                foreach (var arg in ruleOutput.outputArgs)
                {
                    outbound.Args.Add(TemplateResolver.Coerce(_resolver.Resolve(arg.value), arg.type));
                }
                outbound.Text = outbound.Describe();
            }
            else
            {
                outbound.Address = string.Empty;
                outbound.Text = _resolver.Resolve(ruleOutput.outputText);
            }

            return outbound;
        }
    }
}
