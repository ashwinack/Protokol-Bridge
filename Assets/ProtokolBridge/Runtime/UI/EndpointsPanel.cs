using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace ProtokolBridge.UI
{
    /// <summary>
    /// Connection manager: the list of endpoints on the left, the editor for the
    /// selected one on the right, plus a manual send box for testing wiring
    /// without needing the far end to be configured yet.
    /// </summary>
    internal sealed class EndpointsPanel
    {
        private readonly BridgeManager _bridge;
        private readonly VisualElement _root;
        private readonly VisualElement _list;
        private readonly VisualElement _editor;

        private string _selectedId;

        // Live parts of the editor, updated by RefreshEditorStatus rather than rebuilt.
        private VisualElement _headerDot;
        private Label _headerLabel;
        private VisualElement _headerButtons;
        private Label _headerError;
        private Label _clientsLabel;
        private Label _sendWarning;
        private TransportState _renderedState;
        private bool _hasEditor;

        public EndpointsPanel(BridgeManager bridge)
        {
            _bridge = bridge;

            _root = UiFactory.Row("content");

            var left = UiFactory.Column("pane-left");

            var header = UiFactory.Row("pane-header");
            header.Add(UiFactory.Text("CONNECTIONS", "pane-header__label"));
            left.Add(header);

            _list = new ScrollView(ScrollViewMode.Vertical);
            _list.style.flexGrow = 1;
            left.Add(_list);

            var addRowA = UiFactory.Row();
            addRowA.Add(UiFactory.Btn("+ Serial", () => Add(TransportKind.Serial), "btn--add", "btn--small"));
            addRowA.Add(UiFactory.Btn("+ OSC", () => Add(TransportKind.Osc), "btn--add", "btn--small"));
            left.Add(addRowA);

            var addRowB = UiFactory.Row();
            addRowB.Add(UiFactory.Btn("+ UDP", () => Add(TransportKind.Udp), "btn--add", "btn--small"));
            addRowB.Add(UiFactory.Btn("+ TCP Client", () => Add(TransportKind.TcpClient), "btn--add", "btn--small"));
            addRowB.Add(UiFactory.Btn("+ TCP Server", () => Add(TransportKind.TcpServer), "btn--add", "btn--small"));
            left.Add(addRowB);

            _editor = new ScrollView(ScrollViewMode.Vertical);
            _editor.AddToClassList("pane-right");

            _root.Add(left);
            _root.Add(_editor);
        }

        public VisualElement Root { get { return _root; } }

        private void Add(TransportKind kind)
        {
            var endpoint = _bridge.AddEndpoint(kind);
            _selectedId = endpoint.id;
            Rebuild();
        }

        /// <summary>Full rebuild. The lists are short, so this stays cheap and keeps state simple.</summary>
        public void Rebuild()
        {
            EnsureSelection();
            BuildList();
            BuildEditor();
        }

        /// <summary>Keeps something selected so the editor is never pointlessly blank.</summary>
        private void EnsureSelection()
        {
            if (_bridge.GetEndpoint(_selectedId) != null) return;

            _selectedId = null;
            foreach (var endpoint in _bridge.Endpoints)
            {
                _selectedId = endpoint.id;
                break;
            }
        }

        /// <summary>
        /// Periodic refresh of everything that reflects live connection state. The
        /// editor is built once when a connection is selected, but a transport reaches
        /// Running on its worker thread a moment later, so the header has to be
        /// updated in place or it stays stuck on whatever it was built with.
        /// </summary>
        public void RefreshStatus()
        {
            foreach (var child in _list.Children())
            {
                var id = child.userData as string;
                if (id == null) continue;

                UiFactory.ApplyState(child.Q(className: "dot"), _bridge.GetState(id));
            }

            RefreshEditorStatus();
        }

        private void RefreshEditorStatus()
        {
            if (!_hasEditor) return;

            var endpoint = _bridge.GetEndpoint(_selectedId);
            if (endpoint == null) return;

            var transport = _bridge.GetTransport(endpoint.id);
            var state = _bridge.GetState(endpoint.id);

            if (state != _renderedState)
            {
                _renderedState = state;

                UiFactory.ApplyState(_headerDot, state);
                if (_headerLabel != null) _headerLabel.text = endpoint.name + "  (" + state + ")";

                // Running and Stopped need different buttons, so this row is rebuilt.
                BuildStateButtons(endpoint, state);

                if (_sendWarning != null)
                {
                    _sendWarning.style.display = state == TransportState.Running
                        ? DisplayStyle.None
                        : DisplayStyle.Flex;
                }
            }

            // The error text can change without the state changing, e.g. a TCP client
            // retrying, so it is refreshed every tick rather than only on transition.
            SetErrorText(transport != null ? transport.LastError : string.Empty);

            if (_clientsLabel != null)
            {
                var server = transport as TcpServerTransport;
                _clientsLabel.text = "Connected clients: " + (server != null ? server.ConnectedCount : 0);
            }
        }

        private void SetErrorText(string message)
        {
            if (_headerError == null) return;

            var hasError = !string.IsNullOrEmpty(message);
            _headerError.text = hasError ? "! " + message : string.Empty;
            _headerError.style.display = hasError ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void BuildStateButtons(EndpointConfig endpoint, TransportState state)
        {
            if (_headerButtons == null) return;

            _headerButtons.Clear();

            if (state == TransportState.Running || state == TransportState.Connecting)
            {
                _headerButtons.Add(UiFactory.Btn("Stop", () => { _bridge.StopEndpoint(endpoint.id); Rebuild(); }));
                _headerButtons.Add(UiFactory.Btn("Restart", () => { _bridge.RestartEndpoint(endpoint.id); Rebuild(); }));
            }
            else
            {
                _headerButtons.Add(UiFactory.Btn("Start", () => { _bridge.StartEndpoint(endpoint.id); Rebuild(); }, "btn--primary"));
            }

            _headerButtons.Add(UiFactory.Btn("Remove", () =>
            {
                _bridge.RemoveEndpoint(endpoint.id);
                _selectedId = null;
                Rebuild();
            }, "btn--danger"));
        }

        private void BuildList()
        {
            _list.Clear();

            var any = false;
            foreach (var endpoint in _bridge.Endpoints)
            {
                any = true;
                var captured = endpoint;

                var row = UiFactory.Row("row");
                row.userData = captured.id;
                if (captured.id == _selectedId) row.AddToClassList("row--selected");

                row.Add(UiFactory.StatusDot(_bridge.GetState(captured.id)));

                var body = UiFactory.Column("row__body");
                body.Add(UiFactory.Text(captured.name, "row__title"));
                body.Add(UiFactory.Text(captured.kind + "  -  " + captured.Summary(), "row__sub"));
                row.Add(body);

                row.RegisterCallback<ClickEvent>(_ =>
                {
                    _selectedId = captured.id;
                    Rebuild();
                });

                _list.Add(row);
            }

            if (!any)
            {
                _list.Add(UiFactory.Text("No connections yet.\nAdd one with the buttons below.", "empty"));
            }
        }

        private void BuildEditor()
        {
            _editor.Clear();

            _hasEditor = false;
            _headerDot = null;
            _headerLabel = null;
            _headerButtons = null;
            _headerError = null;
            _clientsLabel = null;
            _sendWarning = null;

            var endpoint = _bridge.GetEndpoint(_selectedId);
            if (endpoint == null)
            {
                _editor.Add(UiFactory.Text("Select a connection to configure it.", "empty"));
                return;
            }

            var state = _bridge.GetState(endpoint.id);
            var transport = _bridge.GetTransport(endpoint.id);

            // --- Control strip ---
            // These pieces are kept as fields so RefreshEditorStatus can update them
            // as the connection actually comes up, instead of freezing at build time.
            var controls = UiFactory.Row();
            controls.style.marginBottom = 12;

            _headerDot = UiFactory.StatusDot(state);
            controls.Add(_headerDot);

            _headerLabel = UiFactory.Text(endpoint.name + "  (" + state + ")", "row__title");
            controls.Add(_headerLabel);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            controls.Add(spacer);

            _headerButtons = UiFactory.Row();
            BuildStateButtons(endpoint, state);
            controls.Add(_headerButtons);

            _editor.Add(controls);

            _headerError = UiFactory.Text(string.Empty, "error-text");
            _editor.Add(_headerError);
            SetErrorText(transport != null ? transport.LastError : string.Empty);

            _renderedState = state;
            _hasEditor = true;

            // --- General ---
            VisualElement general;
            var generalSection = UiFactory.Section("GENERAL", out general);
            general.Add(UiFactory.TextRow("Name", endpoint.name, v => { endpoint.name = v; BuildList(); }));
            general.Add(UiFactory.ToggleRow("Start automatically", endpoint.autoStart, v => endpoint.autoStart = v));
            _editor.Add(generalSection);

            // --- Per-kind settings ---
            VisualElement settings;
            var settingsSection = UiFactory.Section(endpoint.kind.ToString().ToUpperInvariant() + " SETTINGS", out settings);

            switch (endpoint.kind)
            {
                case TransportKind.Serial:
                    BuildSerialFields(endpoint, settings);
                    break;
                case TransportKind.Udp:
                case TransportKind.Osc:
                    BuildUdpFields(endpoint, settings);
                    break;
                case TransportKind.TcpClient:
                    settings.Add(UiFactory.TextRow("Host", endpoint.remoteHost, v => endpoint.remoteHost = v));
                    settings.Add(UiFactory.IntRow("Port", endpoint.remotePort, v => endpoint.remotePort = v, 1, 65535));
                    break;
                case TransportKind.TcpServer:
                    settings.Add(UiFactory.IntRow("Listen port", endpoint.listenPort, v => endpoint.listenPort = v, 1, 65535));
                    var server = transport as TcpServerTransport;
                    _clientsLabel = UiFactory.Text("Connected clients: " + (server != null ? server.ConnectedCount : 0), "hint");
                    settings.Add(_clientsLabel);
                    break;
            }

            if (endpoint.kind != TransportKind.Osc && endpoint.kind != TransportKind.Udp)
            {
                settings.Add(UiFactory.ToggleRow("Split into lines", endpoint.useLineFraming, v => { endpoint.useLineFraming = v; BuildEditor(); }));
                if (endpoint.useLineFraming)
                {
                    settings.Add(UiFactory.TextRow("Line ends with", endpoint.lineTerminator, v => endpoint.lineTerminator = v));
                    settings.Add(UiFactory.ToggleRow("Append on send", endpoint.appendTerminatorOnSend, v => endpoint.appendTerminatorOnSend = v));
                    settings.Add(UiFactory.Text("Use \\n, \\r\\n or \\t for invisible characters.", "hint"));
                }
            }

            settings.Add(UiFactory.Text("Changes apply when the connection is restarted.", "hint"));
            _editor.Add(settingsSection);

            _editor.Add(BuildManualSend(endpoint, state));
        }

        private static void BuildSerialFields(EndpointConfig endpoint, VisualElement parent)
        {
            var ports = new List<string>(SerialTransport.AvailablePorts());
            if (!ports.Contains(endpoint.portName)) ports.Insert(0, endpoint.portName);

            parent.Add(UiFactory.DropdownRow("COM port", ports, endpoint.portName, v => endpoint.portName = v));

            if (SerialTransport.AvailablePorts().Length == 0)
            {
                parent.Add(UiFactory.Text("No COM ports detected. Plug the device in, then reopen this panel.", "hint"));
            }

            var bauds = new List<string> { "9600", "19200", "38400", "57600", "115200", "230400", "250000", "460800", "921600" };
            var currentBaud = endpoint.baudRate.ToString();
            if (!bauds.Contains(currentBaud)) bauds.Insert(0, currentBaud);
            parent.Add(UiFactory.DropdownRow("Baud rate", bauds, currentBaud, v =>
            {
                int parsed;
                if (int.TryParse(v, out parsed)) endpoint.baudRate = parsed;
            }));

            parent.Add(UiFactory.DropdownRow("Data bits", new List<string> { "5", "6", "7", "8" }, endpoint.dataBits.ToString(), v =>
            {
                int parsed;
                if (int.TryParse(v, out parsed)) endpoint.dataBits = parsed;
            }));

            parent.Add(UiFactory.DropdownRow("Parity", new List<string> { "None", "Odd", "Even", "Mark", "Space" },
                endpoint.parity, v => endpoint.parity = v));

            parent.Add(UiFactory.DropdownRow("Stop bits", new List<string> { "One", "OnePointFive", "Two" },
                endpoint.stopBits, v => endpoint.stopBits = v));
        }

        private static void BuildUdpFields(EndpointConfig endpoint, VisualElement parent)
        {
            parent.Add(UiFactory.IntRow("Listen port", endpoint.listenPort, v => endpoint.listenPort = v, 0, 65535));
            parent.Add(UiFactory.Text("0 means send-only (no listening socket).", "hint"));
            parent.Add(UiFactory.TextRow("Send to host", endpoint.remoteHost, v => endpoint.remoteHost = v));
            parent.Add(UiFactory.IntRow("Send to port", endpoint.remotePort, v => endpoint.remotePort = v, 1, 65535));
            parent.Add(UiFactory.ToggleRow("Allow broadcast", endpoint.enableBroadcast, v => endpoint.enableBroadcast = v));
        }

        private VisualElement BuildManualSend(EndpointConfig endpoint, TransportState state)
        {
            VisualElement body;
            var section = UiFactory.Section("SEND A TEST MESSAGE", out body);

            var isOsc = endpoint.kind == TransportKind.Osc;

            var addressField = new TextField("OSC address") { value = "/test" };
            addressField.AddToClassList("field");
            if (isOsc) body.Add(addressField);

            var payloadField = new TextField(isOsc ? "Arguments" : "Payload") { value = isOsc ? "1 2.5 hello" : "hello" };
            payloadField.AddToClassList("field");
            body.Add(payloadField);

            var status = UiFactory.Text(string.Empty, "hint");

            var sendRow = UiFactory.Row();
            sendRow.Add(UiFactory.Btn("Send", () =>
            {
                var sent = _bridge.SendManual(endpoint.id,
                    isOsc ? addressField.value : string.Empty,
                    payloadField.value);

                status.text = sent
                    ? "Sent."
                    : "Not sent - the connection is not running.";
            }, "btn--primary"));
            body.Add(sendRow);
            body.Add(status);

            if (isOsc) body.Add(UiFactory.Text("Arguments are space-separated and typed automatically: 1 becomes an int, 2.5 a float, hello a string.", "hint"));

            // Always created so RefreshEditorStatus can show/hide it as the connection
            // comes up, rather than leaving a stale warning on a running endpoint.
            _sendWarning = UiFactory.Text("Start the connection before sending.", "error-text");
            _sendWarning.style.display = state == TransportState.Running ? DisplayStyle.None : DisplayStyle.Flex;
            body.Add(_sendWarning);

            return section;
        }
    }
}
