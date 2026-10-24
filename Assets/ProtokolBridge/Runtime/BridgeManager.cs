using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace ProtokolBridge
{
    /// <summary>
    /// Owns every endpoint and the routing engine. Transports run on their own
    /// threads and drop inbound messages into a queue; this component drains that
    /// queue in <see cref="Update"/> so all routing, logging and UI work happens on
    /// Unity's main thread.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BridgeManager : MonoBehaviour
    {
        /// <summary>Upper bound on messages processed per frame, so a flood cannot stall the app.</summary>
        [SerializeField] private int maxMessagesPerFrame = 512;

        /// <summary>An outgoing message waiting out its rule's delay.</summary>
        private struct PendingSend
        {
            public DateTime DueUtc;
            public string TargetEndpointId;
            public BridgeMessage Message;
            public string RuleName;
        }

        private const int MaxDelayedMessages = 20000;

        private readonly List<PendingSend> _delayed = new List<PendingSend>();
        private readonly List<PendingSend> _delayedKeep = new List<PendingSend>();

        private readonly ConcurrentQueue<BridgeMessage> _inbox = new ConcurrentQueue<BridgeMessage>();
        private readonly Dictionary<string, ITransport> _transports = new Dictionary<string, ITransport>();
        private readonly List<RouteEngine.Dispatch> _dispatchScratch = new List<RouteEngine.Dispatch>(8);
        private readonly RouteEngine _engine = new RouteEngine();

        public BridgeConfig Config { get; private set; }
        public TrafficLog Log { get; private set; }
        public string ConfigPath { get; private set; }

        /// <summary>Raised when endpoints are added, removed or reconfigured.</summary>
        public event Action EndpointsChanged;

        /// <summary>Raised for every logged message, inbound and outbound.</summary>
        public event Action<TrafficEntry> Logged;

        /// <summary>
        /// Messages received by a transport but not yet processed. This should sit at
        /// zero; a number that keeps climbing means the app is not draining fast enough.
        /// </summary>
        public int PendingCount { get { return _inbox.Count; } }

        /// <summary>Outgoing messages currently waiting out a rule's delay.</summary>
        public int DelayedCount { get { return _delayed.Count; } }

        public long InboundCount { get; private set; }
        public long OutboundCount { get; private set; }
        public long DroppedCount { get; private set; }

        private bool _initialized;

        private void Awake()
        {
            EnsureInitialized();
        }

        /// <summary>
        /// Loads the config and prepares the engine. Safe to call more than once.
        /// The UI calls this before building, because Unity does not guarantee that
        /// this component's Awake runs before another component's OnEnable.
        /// </summary>
        public void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            ConfigPath = ConfigStore.DefaultPath;

            BridgeConfig loaded;
            string error;
            if (!ConfigStore.TryLoad(ConfigPath, out loaded, out error))
            {
                loaded = BridgeConfig.CreateDefault();
                Debug.Log("[ProtokolBridge] " + error + " - starting from the default config.");
            }

            Config = loaded;
            Log = new TrafficLog(Config.logCapacity);

            ApplyFrameRate();

            _engine.TargetKindLookup = LookupTargetKind;
            _engine.SetRules(Config.rules);
        }

        /// <summary>
        /// Caps the render rate. vSync has to be switched off first: while
        /// vSyncCount is non-zero Unity ignores targetFrameRate entirely and the
        /// app keeps rendering at the monitor's refresh rate.
        /// </summary>
        public void ApplyFrameRate()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = Config.targetFrameRate > 0 ? Config.targetFrameRate : -1;
        }

        private void Start()
        {
            foreach (var endpoint in Config.endpoints)
            {
                if (endpoint.autoStart) StartEndpoint(endpoint.id);
            }
        }

        private void Update()
        {
            var budget = maxMessagesPerFrame;

            BridgeMessage inbound;
            while (budget-- > 0 && _inbox.TryDequeue(out inbound))
            {
                InboundCount++;
                Record(inbound, null);
                Route(inbound);
            }

            FlushDelayed();
        }

        private void OnDestroy()
        {
            StopAll();
        }

        private void OnApplicationQuit()
        {
            StopAll();
        }

        // --- Endpoint lifecycle ---------------------------------------------

        public IEnumerable<EndpointConfig> Endpoints { get { return Config.endpoints; } }

        public ITransport GetTransport(string endpointId)
        {
            ITransport transport;
            return _transports.TryGetValue(endpointId ?? string.Empty, out transport) ? transport : null;
        }

        public EndpointConfig GetEndpoint(string endpointId)
        {
            foreach (var endpoint in Config.endpoints)
            {
                if (endpoint.id == endpointId) return endpoint;
            }
            return null;
        }

        public TransportState GetState(string endpointId)
        {
            var transport = GetTransport(endpointId);
            return transport != null ? transport.State : TransportState.Stopped;
        }

        public EndpointConfig AddEndpoint(TransportKind kind)
        {
            var endpoint = new EndpointConfig
            {
                kind = kind,
                name = kind + " " + (Config.endpoints.Count + 1),
                autoStart = false
            };

            Config.endpoints.Add(endpoint);
            RaiseEndpointsChanged();
            return endpoint;
        }

        public void RemoveEndpoint(string endpointId)
        {
            StopEndpoint(endpointId);
            Config.endpoints.RemoveAll(e => e.id == endpointId);

            // Drop outputs aimed at the removed endpoint, then drop rules that are
            // left with nothing to send or no source to listen to.
            foreach (var rule in Config.rules)
            {
                rule.outputs.RemoveAll(o => o.targetEndpointId == endpointId);
            }
            Config.rules.RemoveAll(r => r.sourceEndpointId == endpointId || r.outputs.Count == 0);

            _engine.SetRules(Config.rules);
            RaiseEndpointsChanged();
        }

        public void StartEndpoint(string endpointId)
        {
            var endpoint = GetEndpoint(endpointId);
            if (endpoint == null) return;

            StopEndpoint(endpointId);

            ITransport transport;
            try
            {
                transport = CreateTransport(endpoint);
            }
            catch (Exception ex)
            {
                Debug.LogError("[ProtokolBridge] Could not create '" + endpoint.name + "': " + ex.Message);
                return;
            }

            transport.MessageReceived += OnTransportMessage;
            _transports[endpointId] = transport;
            transport.Start();
            RaiseEndpointsChanged();
        }

        public void StopEndpoint(string endpointId)
        {
            ITransport transport;
            if (!_transports.TryGetValue(endpointId ?? string.Empty, out transport)) return;

            transport.MessageReceived -= OnTransportMessage;
            transport.Dispose();
            _transports.Remove(endpointId);
            RaiseEndpointsChanged();
        }

        public void RestartEndpoint(string endpointId)
        {
            StartEndpoint(endpointId);
        }

        public void StartAll()
        {
            foreach (var endpoint in Config.endpoints) StartEndpoint(endpoint.id);
        }

        public void StopAll()
        {
            var ids = new List<string>(_transports.Keys);
            foreach (var id in ids) StopEndpoint(id);

            // Anything still waiting out a delay has nowhere to go now.
            _delayed.Clear();
        }

        private static ITransport CreateTransport(EndpointConfig endpoint)
        {
            switch (endpoint.kind)
            {
                case TransportKind.Serial: return new SerialTransport(endpoint);
                case TransportKind.Udp: return new UdpTransport(endpoint);
                case TransportKind.Osc: return new OscTransport(endpoint);
                case TransportKind.TcpClient: return new TcpClientTransport(endpoint);
                case TransportKind.TcpServer: return new TcpServerTransport(endpoint);
                default: throw new NotSupportedException("Unknown transport kind: " + endpoint.kind);
            }
        }

        // --- Rules ----------------------------------------------------------

        public IReadOnlyList<RouteRule> Rules { get { return _engine.Rules; } }

        public RouteRule AddRule()
        {
            var rule = new RouteRule { name = "Rule " + (Config.rules.Count + 1) };
            rule.outputs.Add(new RuleOutput());
            Config.rules.Add(rule);
            _engine.SetRules(Config.rules);
            return rule;
        }

        /// <summary>Adds another thing for this rule to send when it fires.</summary>
        public RuleOutput AddRuleOutput(RouteRule rule)
        {
            var ruleOutput = new RuleOutput();
            rule.outputs.Add(ruleOutput);
            _engine.SetRules(Config.rules);
            return ruleOutput;
        }

        public void RemoveRuleOutput(RouteRule rule, string outputId)
        {
            rule.outputs.RemoveAll(o => o.id == outputId);
            _engine.SetRules(Config.rules);
        }

        public void RemoveRule(string ruleId)
        {
            Config.rules.RemoveAll(r => r.id == ruleId);
            _engine.SetRules(Config.rules);
        }

        public void RefreshRules()
        {
            _engine.SetRules(Config.rules);
        }

        /// <summary>
        /// Dry-runs a rule against text the operator typed, so the rule editor can
        /// show the resulting message before any hardware is connected.
        /// </summary>
        public bool PreviewRule(RouteRule rule, RuleOutput ruleOutput, string sampleText, string sampleAddress,
                                out BridgeMessage outbound, out string reason)
        {
            var sample = BridgeMessage.FromText(rule != null ? rule.sourceEndpointId : string.Empty, sampleText, "preview");
            sample.Address = sampleAddress ?? string.Empty;

            // An OSC sample also needs its arguments populated so {arg0} resolves.
            if (!string.IsNullOrEmpty(sample.Address))
            {
                foreach (var token in SplitArgs(sampleText))
                {
                    sample.Args.Add(TemplateResolver.Coerce(token, ArgType.Auto));
                }
            }

            return _engine.Preview(rule, ruleOutput, sample, out outbound, out reason);
        }

        // --- Message flow ---------------------------------------------------

        /// <summary>Called on a transport's worker thread; must stay allocation-light and Unity-free.</summary>
        private void OnTransportMessage(BridgeMessage message)
        {
            _inbox.Enqueue(message);
        }

        private void Route(BridgeMessage inbound)
        {
            _dispatchScratch.Clear();
            _engine.Evaluate(inbound, _dispatchScratch);

            foreach (var dispatch in _dispatchScratch)
            {
                var ruleName = dispatch.Rule != null ? dispatch.Rule.name : null;
                var delayMs = dispatch.DelayMs;

                if (delayMs <= 0)
                {
                    SendToEndpoint(dispatch.TargetEndpointId, dispatch.Message, ruleName);
                    continue;
                }

                // A fast source with a long delay would otherwise grow this without bound.
                if (_delayed.Count >= MaxDelayedMessages)
                {
                    DroppedCount++;
                    continue;
                }

                _delayed.Add(new PendingSend
                {
                    DueUtc = DateTime.UtcNow.AddMilliseconds(delayMs),
                    TargetEndpointId = dispatch.TargetEndpointId,
                    Message = dispatch.Message,
                    RuleName = ruleName
                });
            }
        }

        /// <summary>
        /// Sends messages whose delay has elapsed. Insertion order is preserved, so a
        /// burst of serial lines arrives at the far end in the order it was read.
        /// </summary>
        private void FlushDelayed()
        {
            if (_delayed.Count == 0) return;

            var now = DateTime.UtcNow;
            _delayedKeep.Clear();

            foreach (var pending in _delayed)
            {
                if (pending.DueUtc <= now) SendToEndpoint(pending.TargetEndpointId, pending.Message, pending.RuleName);
                else _delayedKeep.Add(pending);
            }

            _delayed.Clear();
            _delayed.AddRange(_delayedKeep);
        }

        private bool SendToEndpoint(string endpointId, BridgeMessage message, string ruleName)
        {
            var transport = GetTransport(endpointId);
            if (transport == null || transport.State != TransportState.Running)
            {
                DroppedCount++;
                return false;
            }

            // Stamp at the moment it actually leaves, so a delayed message is logged
            // at its send time rather than when its trigger arrived.
            message.TimestampUtc = DateTime.UtcNow;

            if (!transport.Send(message))
            {
                DroppedCount++;
                return false;
            }

            OutboundCount++;
            Record(message, ruleName);
            return true;
        }

        /// <summary>Sends a message an operator typed into the UI, bypassing the rules.</summary>
        public bool SendManual(string endpointId, string addressOrEmpty, string payload)
        {
            var transport = GetTransport(endpointId);
            if (transport == null || transport.State != TransportState.Running) return false;

            var message = new BridgeMessage
            {
                TimestampUtc = DateTime.UtcNow,
                Address = addressOrEmpty ?? string.Empty,
                Text = payload ?? string.Empty
            };

            if (!string.IsNullOrEmpty(message.Address))
            {
                foreach (var token in SplitArgs(payload))
                {
                    message.Args.Add(TemplateResolver.Coerce(token, ArgType.Auto));
                }
                message.Text = message.Describe();
            }

            if (!transport.Send(message)) return false;

            OutboundCount++;
            Record(message, "manual");
            return true;
        }

        private static IEnumerable<string> SplitArgs(string payload)
        {
            if (string.IsNullOrEmpty(payload)) yield break;
            foreach (var token in payload.Split(' '))
            {
                if (token.Length > 0) yield return token;
            }
        }

        private void Record(BridgeMessage message, string ruleName)
        {
            var endpoint = GetEndpoint(message.EndpointId);

            var entry = new TrafficEntry
            {
                TimeUtc = message.TimestampUtc,
                EndpointName = endpoint != null ? endpoint.name : "(unknown)",
                Direction = message.Direction,
                Summary = message.Describe(),
                Remote = message.Remote ?? string.Empty,
                RuleName = ruleName ?? string.Empty
            };

            Log.Add(entry);

            var handler = Logged;
            if (handler != null) handler(entry);
        }

        private TransportKind? LookupTargetKind(string endpointId)
        {
            var endpoint = GetEndpoint(endpointId);
            return endpoint != null ? endpoint.kind : (TransportKind?)null;
        }

        private void RaiseEndpointsChanged()
        {
            var handler = EndpointsChanged;
            if (handler != null) handler();
        }

        // --- Persistence ----------------------------------------------------

        public bool SaveConfig(out string error)
        {
            Config.logCapacity = Log.Capacity;
            return ConfigStore.TrySave(ConfigPath, Config, out error);
        }

        public bool ReloadConfig(out string error)
        {
            BridgeConfig loaded;
            if (!ConfigStore.TryLoad(ConfigPath, out loaded, out error)) return false;

            StopAll();
            Config = loaded;
            Log.Resize(Config.logCapacity);
            _engine.SetRules(Config.rules);
            RaiseEndpointsChanged();

            foreach (var endpoint in Config.endpoints)
            {
                if (endpoint.autoStart) StartEndpoint(endpoint.id);
            }

            return true;
        }
    }
}
