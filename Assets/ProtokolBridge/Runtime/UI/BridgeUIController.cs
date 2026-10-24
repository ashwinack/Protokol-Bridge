using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ProtokolBridge.UI
{
    /// <summary>
    /// Builds the whole operator panel and owns the refresh cadence.
    /// Lives on the same GameObject as <see cref="BridgeManager"/> and a UIDocument.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class BridgeUIController : MonoBehaviour
    {
        public enum Tab
        {
            Connections,
            Rules,
            Traffic
        }

        [SerializeField] private BridgeManager bridge;

        /// <summary>The log and status dots refresh on a timer; every frame would be wasteful.</summary>
        [SerializeField] private float refreshInterval = 0.25f;

        private EndpointsPanel _endpoints;
        private RulesPanel _rules;
        private TrafficPanel _traffic;

        private VisualElement _contentHost;
        private readonly Dictionary<Tab, Button> _tabButtons = new Dictionary<Tab, Button>();
        private Tab _activeTab = Tab.Connections;

        private Label _statsLabel;
        private Label _statusLabel;
        private float _nextRefresh;

        private void Awake()
        {
            if (bridge == null) bridge = GetComponent<BridgeManager>();
        }

        private void OnEnable()
        {
            if (bridge == null) bridge = GetComponent<BridgeManager>();
            if (bridge == null)
            {
                Debug.LogError("[ProtokolBridge] BridgeUIController needs a BridgeManager on the same GameObject.");
                enabled = false;
                return;
            }

            // Component Awake/OnEnable order is not guaranteed, so make sure the
            // manager has loaded its config before we read any of it.
            bridge.EnsureInitialized();

            var document = GetComponent<UIDocument>();
            var root = document.rootVisualElement;
            root.Clear();

            var styleSheet = Resources.Load<StyleSheet>("ProtokolBridge");
            if (styleSheet != null) root.styleSheets.Add(styleSheet);
            else Debug.LogWarning("[ProtokolBridge] ProtokolBridge.uss not found in Resources; the panel will be unstyled.");

            root.AddToClassList("root");

            _endpoints = new EndpointsPanel(bridge);
            _rules = new RulesPanel(bridge);
            _traffic = new TrafficPanel(bridge);

            root.Add(BuildToolbar());
            root.Add(BuildTabs());

            _contentHost = new VisualElement();
            _contentHost.style.flexGrow = 1;
            root.Add(_contentHost);

            bridge.EndpointsChanged += OnEndpointsChanged;

            SelectTab(Tab.Connections);
        }

        private void OnDisable()
        {
            if (bridge != null) bridge.EndpointsChanged -= OnEndpointsChanged;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + refreshInterval;

            _statsLabel.text = string.Format("in {0}   out {1}   dropped {2}   queued {3}   waiting {4}",
                bridge.InboundCount, bridge.OutboundCount, bridge.DroppedCount,
                bridge.PendingCount, bridge.DelayedCount);

            if (_activeTab == Tab.Traffic) _traffic.Refresh();
            else if (_activeTab == Tab.Connections) _endpoints.RefreshStatus();
        }

        private VisualElement BuildToolbar()
        {
            var toolbar = UiFactory.Row("toolbar");
            toolbar.Add(UiFactory.Text("Protokol Bridge", "toolbar__title"));

            toolbar.Add(UiFactory.Btn("Start All", () => { bridge.StartAll(); Refresh(); }, "btn--primary"));
            toolbar.Add(UiFactory.Btn("Stop All", () => { bridge.StopAll(); Refresh(); }));

            toolbar.Add(UiFactory.Text(string.Empty, "toolbar__spacer"));

            _statusLabel = UiFactory.Text(string.Empty, "toolbar__stat");
            toolbar.Add(_statusLabel);

            toolbar.Add(UiFactory.Btn("Save Config", () =>
            {
                string error;
                _statusLabel.text = bridge.SaveConfig(out error)
                    ? "Saved to " + bridge.ConfigPath
                    : "Save failed: " + error;
            }));

            toolbar.Add(UiFactory.Btn("Reload", () =>
            {
                string error;
                _statusLabel.text = bridge.ReloadConfig(out error) ? "Reloaded." : "Reload failed: " + error;
                Refresh();
            }));

            _statsLabel = UiFactory.Text(string.Empty, "toolbar__stat");
            toolbar.Add(_statsLabel);

            return toolbar;
        }

        private VisualElement BuildTabs()
        {
            var tabs = UiFactory.Row("tabs");

            AddTab(tabs, Tab.Connections, "Connections");
            AddTab(tabs, Tab.Rules, "Rules");
            AddTab(tabs, Tab.Traffic, "Traffic");

            return tabs;
        }

        private void AddTab(VisualElement parent, Tab tab, string label)
        {
            var button = UiFactory.Btn(label, () => SelectTab(tab), "tab");
            _tabButtons[tab] = button;
            parent.Add(button);
        }

        /// <summary>Switches the visible panel. Public so other tooling can drive it.</summary>
        public void SelectTab(Tab tab)
        {
            _activeTab = tab;

            foreach (var pair in _tabButtons)
            {
                if (pair.Key == tab) pair.Value.AddToClassList("tab--active");
                else pair.Value.RemoveFromClassList("tab--active");
            }

            _contentHost.Clear();

            switch (tab)
            {
                case Tab.Connections:
                    _endpoints.Rebuild();
                    _contentHost.Add(_endpoints.Root);
                    break;
                case Tab.Rules:
                    _rules.Rebuild();
                    _contentHost.Add(_rules.Root);
                    break;
                case Tab.Traffic:
                    _contentHost.Add(_traffic.Root);
                    _traffic.Refresh(true);
                    break;
            }
        }

        private void OnEndpointsChanged()
        {
            // The panel that is on screen owns its own layout; just re-run its build.
            if (_activeTab == Tab.Connections) _endpoints.Rebuild();
            else if (_activeTab == Tab.Rules) _rules.Rebuild();
        }

        private void Refresh()
        {
            SelectTab(_activeTab);
        }
    }
}
