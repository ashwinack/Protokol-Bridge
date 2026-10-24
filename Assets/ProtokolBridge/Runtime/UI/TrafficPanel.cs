using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace ProtokolBridge.UI
{
    /// <summary>
    /// Live view of everything crossing the bridge. Backed by a virtualising
    /// ListView over a snapshot of the ring buffer, so a busy bridge does not
    /// build thousands of VisualElements.
    /// </summary>
    internal sealed class TrafficPanel
    {
        /// <summary>Must match the .log-row height in ProtokolBridge.uss, or rows clip.</summary>
        private const int RowHeight = 22;

        private readonly BridgeManager _bridge;
        private readonly VisualElement _root;
        private readonly ListView _listView;
        private readonly List<TrafficEntry> _snapshot = new List<TrafficEntry>(512);
        private readonly List<TrafficEntry> _filtered = new List<TrafficEntry>(512);
        private readonly Label _summary;

        private bool _paused;
        private string _filter = string.Empty;
        private bool _autoScroll = true;

        public TrafficPanel(BridgeManager bridge)
        {
            _bridge = bridge;

            _root = UiFactory.Column("pane-right");
            _root.style.flexGrow = 1;

            // --- Controls ---
            var controls = UiFactory.Row();
            controls.style.marginBottom = 10;
            controls.style.alignItems = Align.Center;

            var pauseButton = UiFactory.Btn("Pause", null);
            pauseButton.clicked += () =>
            {
                _paused = !_paused;
                pauseButton.text = _paused ? "Resume" : "Pause";
            };
            controls.Add(pauseButton);

            controls.Add(UiFactory.Btn("Clear", () =>
            {
                _bridge.Log.Clear();
                Refresh(true);
            }));

            var autoScrollToggle = new Toggle("Follow") { value = true };
            autoScrollToggle.style.marginLeft = 8;
            autoScrollToggle.style.marginRight = 8;
            autoScrollToggle.RegisterValueChangedCallback(evt => _autoScroll = evt.newValue);
            controls.Add(autoScrollToggle);

            var filterField = new TextField { value = string.Empty };
            filterField.style.flexGrow = 1;
            filterField.style.marginRight = 8;
            filterField.RegisterValueChangedCallback(evt =>
            {
                _filter = evt.newValue ?? string.Empty;
                Refresh(true);
            });
            controls.Add(UiFactory.Text("Filter", "field__label"));
            controls.Add(filterField);

            _root.Add(controls);

            // --- Column headings ---
            var headings = UiFactory.Row("log-row");
            headings.style.marginBottom = 2;
            headings.Add(UiFactory.Text("TIME", "log-row__time"));
            headings.Add(UiFactory.Text("DIR", "log-row__dir"));
            headings.Add(UiFactory.Text("CONNECTION", "log-row__endpoint"));
            headings.Add(UiFactory.Text("MESSAGE", "log-row__summary"));
            headings.Add(UiFactory.Text("RULE", "log-row__rule"));
            _root.Add(headings);

            // --- List ---
            _listView = new ListView
            {
                fixedItemHeight = RowHeight,
                selectionType = SelectionType.None,
                itemsSource = _filtered,
                makeItem = MakeRow,
                bindItem = BindRow
            };
            _listView.AddToClassList("log-list");
            _root.Add(_listView);

            _summary = UiFactory.Text(string.Empty, "hint");
            _root.Add(_summary);
        }

        public VisualElement Root { get { return _root; } }

        private static VisualElement MakeRow()
        {
            var row = UiFactory.Row("log-row");
            row.Add(UiFactory.Text(string.Empty, "log-row__time"));
            row.Add(UiFactory.Text(string.Empty, "log-row__dir"));
            row.Add(UiFactory.Text(string.Empty, "log-row__endpoint"));
            row.Add(UiFactory.Text(string.Empty, "log-row__summary"));
            row.Add(UiFactory.Text(string.Empty, "log-row__rule"));
            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            if (index < 0 || index >= _filtered.Count) return;
            var entry = _filtered[index];

            ((Label)element[0]).text = entry.TimeText;

            var direction = (Label)element[1];
            direction.text = entry.DirectionArrow;
            direction.RemoveFromClassList("log-row__dir--in");
            direction.RemoveFromClassList("log-row__dir--out");
            direction.AddToClassList(entry.Direction == MessageDirection.Inbound ? "log-row__dir--in" : "log-row__dir--out");

            ((Label)element[2]).text = entry.EndpointName;
            ((Label)element[3]).text = entry.Summary;
            ((Label)element[4]).text = entry.RuleName;
        }

        /// <summary>Called on a timer by the controller rather than every frame.</summary>
        public void Refresh(bool force = false)
        {
            if (_paused && !force) return;

            _bridge.Log.CopyRecent(_snapshot, _bridge.Log.Capacity);

            _filtered.Clear();
            if (string.IsNullOrEmpty(_filter))
            {
                _filtered.AddRange(_snapshot);
            }
            else
            {
                foreach (var entry in _snapshot)
                {
                    if (Contains(entry.Summary, _filter) ||
                        Contains(entry.EndpointName, _filter) ||
                        Contains(entry.RuleName, _filter) ||
                        Contains(entry.Remote, _filter))
                    {
                        _filtered.Add(entry);
                    }
                }
            }

            _listView.RefreshItems();

            if (_autoScroll && _filtered.Count > 0) _listView.ScrollToItem(_filtered.Count - 1);

            _summary.text = string.Format("{0} shown  -  {1} total seen  -  in {2} / out {3} / dropped {4}",
                _filtered.Count, _bridge.Log.TotalSeen, _bridge.InboundCount, _bridge.OutboundCount, _bridge.DroppedCount);
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                   haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
