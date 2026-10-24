using System;
using System.Collections.Generic;

namespace ProtokolBridge
{
    public enum MatchMode
    {
        /// <summary>Every message from the source endpoint fires the rule.</summary>
        Any,
        Equals,
        Contains,
        StartsWith,
        Regex,
        /// <summary>Matches the OSC address, with * and ? wildcards.</summary>
        OscAddress
    }

    public enum OutputMode
    {
        /// <summary>Forward the incoming payload unchanged.</summary>
        Passthrough,
        /// <summary>Build a new payload from the templates below. This is the editable case.</summary>
        Template
    }

    public enum ArgType
    {
        /// <summary>Send as int if it parses as one, else float, else string.</summary>
        Auto,
        Int,
        Float,
        String,
        Bool
    }

    [Serializable]
    public class OutputArg
    {
        public ArgType type = ArgType.Auto;
        /// <summary>Literal text, or a template such as "{field1}".</summary>
        public string value = string.Empty;

        public OutputArg() { }

        public OutputArg(ArgType argType, string template)
        {
            type = argType;
            value = template;
        }
    }

    /// <summary>
    /// One thing a rule sends when it fires. A rule holds a list of these, so a
    /// single incoming serial line can go out as OSC and as UDP/TCP at the same
    /// time, each with its own payload and its own delay.
    /// </summary>
    [Serializable]
    public class RuleOutput
    {
        public string id = Guid.NewGuid().ToString("N");

        public string targetEndpointId = string.Empty;
        public OutputMode outputMode = OutputMode.Template;

        /// <summary>OSC address to send on. Supports templates, e.g. "/sensor/{field0}".</summary>
        public string outputAddress = "/protokol/value";

        /// <summary>Payload for non-OSC targets (serial, raw UDP, TCP).</summary>
        public string outputText = "{text}";

        /// <summary>OSC arguments, in order.</summary>
        public List<OutputArg> outputArgs = new List<OutputArg>();

        /// <summary>
        /// Wait this long after the input arrives before sending. 0 sends immediately.
        /// Each output delays independently, so one rule can stagger its sends.
        /// </summary>
        public int delayMs;

        public RuleOutput Clone()
        {
            var copy = (RuleOutput)MemberwiseClone();
            copy.id = Guid.NewGuid().ToString("N");
            copy.outputArgs = new List<OutputArg>();
            foreach (var arg in outputArgs) copy.outputArgs.Add(new OutputArg(arg.type, arg.value));
            return copy;
        }
    }

    /// <summary>
    /// One "when this comes in, send these out" mapping. The condition is matched
    /// once; every entry in <see cref="outputs"/> then fires.
    /// </summary>
    [Serializable]
    public class RouteRule
    {
        public const string AnyEndpoint = "*";

        public string id = Guid.NewGuid().ToString("N");
        public string name = "New Rule";
        public bool enabled = true;

        // --- When ---------------------------------------------------------
        /// <summary>Endpoint id, or "*" for any source.</summary>
        public string sourceEndpointId = AnyEndpoint;
        public MatchMode matchMode = MatchMode.Any;
        public string matchValue = string.Empty;
        public bool caseSensitive;

        /// <summary>Splits the incoming text so templates can use {field0}, {field1}, ...</summary>
        public string fieldSeparator = ",";

        // --- Then ---------------------------------------------------------
        /// <summary>Everything this rule sends. Each entry has its own target and delay.</summary>
        public List<RuleOutput> outputs = new List<RuleOutput>();

        // --- Options ------------------------------------------------------
        /// <summary>Drop messages that arrive sooner than this after the last one fired. 0 disables.</summary>
        public int throttleMs;

        /// <summary>Stop evaluating later rules once this one matches.</summary>
        public bool stopOnMatch;

        // --- Legacy -------------------------------------------------------
        // Rules written before a rule could have several outputs stored a single
        // target inline. These fields exist only so those configs still load;
        // MigrateLegacy() folds them into `outputs` and blanks the target.
        public string targetEndpointId = string.Empty;
        public OutputMode outputMode = OutputMode.Template;
        public string outputAddress = "/protokol/value";
        public string outputText = "{text}";
        public List<OutputArg> outputArgs = new List<OutputArg>();
        public int delayMs;

        [NonSerialized] public long HitCount;
        [NonSerialized] public DateTime LastFiredUtc;

        /// <summary>Converts a single-output rule loaded from an older config. Idempotent.</summary>
        public void MigrateLegacy()
        {
            if (outputs == null) outputs = new List<RuleOutput>();
            foreach (var output in outputs)
            {
                if (output.outputArgs == null) output.outputArgs = new List<OutputArg>();
            }

            if (outputs.Count > 0 || string.IsNullOrEmpty(targetEndpointId)) return;

            outputs.Add(new RuleOutput
            {
                targetEndpointId = targetEndpointId,
                outputMode = outputMode,
                outputAddress = outputAddress,
                outputText = outputText,
                outputArgs = outputArgs ?? new List<OutputArg>(),
                delayMs = delayMs
            });

            targetEndpointId = string.Empty;
            outputArgs = new List<OutputArg>();
            delayMs = 0;
        }

        public RouteRule Clone()
        {
            var copy = (RouteRule)MemberwiseClone();
            copy.outputs = new List<RuleOutput>();
            foreach (var output in outputs) copy.outputs.Add(output.Clone());
            copy.outputArgs = new List<OutputArg>();
            copy.HitCount = 0;
            copy.LastFiredUtc = default(DateTime);
            return copy;
        }
    }
}
