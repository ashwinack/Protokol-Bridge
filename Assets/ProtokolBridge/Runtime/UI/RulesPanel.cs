using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace ProtokolBridge.UI
{
    /// <summary>
    /// The rule editor: "when this arrives, send that". The output side is a set of
    /// templates, and a live preview shows what those templates produce for a sample
    /// input so the mapping can be built before any hardware is attached.
    /// </summary>
    internal sealed class RulesPanel
    {
        private readonly BridgeManager _bridge;
        private readonly VisualElement _root;
        private readonly VisualElement _list;
        private readonly ScrollView _editor;

        private string _selectedId;
        private string _sampleInput = "TEMP,kitchen,21.7";

        public RulesPanel(BridgeManager bridge)
        {
            _bridge = bridge;

            _root = UiFactory.Row("content");

            var left = UiFactory.Column("pane-left");
            var header = UiFactory.Row("pane-header");
            header.Add(UiFactory.Text("RULES", "pane-header__label"));
            left.Add(header);

            _list = new ScrollView(ScrollViewMode.Vertical);
            _list.style.flexGrow = 1;
            left.Add(_list);

            var addRow = UiFactory.Row();
            addRow.Add(UiFactory.Btn("+ Add Rule", () =>
            {
                var rule = _bridge.AddRule();
                _selectedId = rule.id;
                Rebuild();
            }, "btn--add"));
            left.Add(addRow);

            _editor = new ScrollView(ScrollViewMode.Vertical);
            _editor.AddToClassList("pane-right");

            _root.Add(left);
            _root.Add(_editor);
        }

        public VisualElement Root { get { return _root; } }

        public void Rebuild()
        {
            EnsureSelection();
            BuildList();
            BuildEditor();
        }

        /// <summary>Keeps something selected so the editor is never pointlessly blank.</summary>
        private void EnsureSelection()
        {
            if (Selected() != null) return;

            _selectedId = null;
            foreach (var rule in _bridge.Rules)
            {
                _selectedId = rule.id;
                break;
            }
        }

        private RouteRule Selected()
        {
            foreach (var rule in _bridge.Rules)
            {
                if (rule.id == _selectedId) return rule;
            }
            return null;
        }

        private void BuildList()
        {
            _list.Clear();

            var any = false;
            foreach (var rule in _bridge.Rules)
            {
                any = true;
                var captured = rule;

                var row = UiFactory.Row("row");
                if (captured.id == _selectedId) row.AddToClassList("row--selected");

                var toggle = new Toggle { value = captured.enabled };
                toggle.style.marginRight = 8;
                toggle.RegisterValueChangedCallback(evt =>
                {
                    captured.enabled = evt.newValue;
                    evt.StopPropagation();
                });
                row.Add(toggle);

                var body = UiFactory.Column("row__body");
                body.Add(UiFactory.Text(captured.name, "row__title"));
                body.Add(UiFactory.Text(DescribeRule(captured), "row__sub"));
                row.Add(body);

                row.Add(UiFactory.Text(captured.HitCount.ToString(), "row__sub"));

                row.RegisterCallback<ClickEvent>(_ =>
                {
                    _selectedId = captured.id;
                    Rebuild();
                });

                _list.Add(row);
            }

            if (!any)
            {
                _list.Add(UiFactory.Text("No rules yet.\n\nA rule forwards data from one connection to another, and lets you rewrite the message on the way.", "empty"));
            }
        }

        private string DescribeRule(RouteRule rule)
        {
            var source = NameOf(rule.sourceEndpointId);
            if (rule.outputs.Count == 0) return source + "  ->  (nothing)";

            var targets = new List<string>(rule.outputs.Count);
            foreach (var ruleOutput in rule.outputs) targets.Add(NameOf(ruleOutput.targetEndpointId));

            return source + "  ->  " + string.Join(" + ", targets.ToArray());
        }

        private string NameOf(string endpointId)
        {
            if (endpointId == RouteRule.AnyEndpoint) return "Any";
            var endpoint = _bridge.GetEndpoint(endpointId);
            return endpoint != null ? endpoint.name : "(none)";
        }

        /// <summary>Dropdown over endpoints that resolves by index, so duplicate names stay safe.</summary>
        private DropdownField EndpointDropdown(string label, string currentId, bool includeAny, Action<string> onChanged)
        {
            var ids = new List<string>();
            var labels = new List<string>();

            if (includeAny)
            {
                ids.Add(RouteRule.AnyEndpoint);
                labels.Add("Any connection");
            }

            foreach (var endpoint in _bridge.Endpoints)
            {
                ids.Add(endpoint.id);
                labels.Add(endpoint.name + "  (" + endpoint.kind + ")");
            }

            if (labels.Count == 0)
            {
                ids.Add(string.Empty);
                labels.Add("(no connections)");
            }

            var index = ids.IndexOf(currentId ?? string.Empty);
            if (index < 0) index = 0;

            var dropdown = new DropdownField(label, labels, index);
            dropdown.AddToClassList("field");
            dropdown.RegisterValueChangedCallback(_ =>
            {
                var picked = dropdown.index;
                if (picked >= 0 && picked < ids.Count) onChanged(ids[picked]);
            });
            return dropdown;
        }

        private void BuildEditor()
        {
            _editor.Clear();

            var rule = Selected();
            if (rule == null)
            {
                _editor.Add(UiFactory.Text("Select a rule, or add one.", "empty"));
                return;
            }

            // --- Header ---
            var controls = UiFactory.Row();
            controls.style.marginBottom = 12;
            controls.Add(UiFactory.Text(rule.name, "row__title"));
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            controls.Add(spacer);
            controls.Add(UiFactory.Text("fired " + rule.HitCount + "x", "row__sub"));
            controls.Add(UiFactory.Btn("Delete", () =>
            {
                _bridge.RemoveRule(rule.id);
                _selectedId = null;
                Rebuild();
            }, "btn--danger"));
            _editor.Add(controls);

            // --- General ---
            VisualElement general;
            var generalSection = UiFactory.Section("GENERAL", out general);
            general.Add(UiFactory.TextRow("Rule name", rule.name, v => { rule.name = v; BuildList(); }));
            general.Add(UiFactory.ToggleRow("Enabled", rule.enabled, v => rule.enabled = v));
            _editor.Add(generalSection);

            // --- WHEN ---
            VisualElement when;
            var whenSection = UiFactory.Section("WHEN  -  incoming message", out when);

            when.Add(EndpointDropdown("Comes from", rule.sourceEndpointId, true, v =>
            {
                rule.sourceEndpointId = v;
                BuildList();
            }));

            when.Add(UiFactory.EnumRow("Condition", rule.matchMode, (MatchMode v) =>
            {
                rule.matchMode = v;
                BuildEditor();
            }));

            if (rule.matchMode != MatchMode.Any)
            {
                var matchLabel = rule.matchMode == MatchMode.OscAddress ? "Address pattern" : "Text";
                when.Add(UiFactory.TextRow(matchLabel, rule.matchValue, v => rule.matchValue = v));
                when.Add(UiFactory.ToggleRow("Case sensitive", rule.caseSensitive, v => rule.caseSensitive = v));

                if (rule.matchMode == MatchMode.OscAddress)
                    when.Add(UiFactory.Text("Wildcards: * matches any run of characters, ? matches one. Example: /light/*/level", "hint"));
                else if (rule.matchMode == MatchMode.Regex)
                    when.Add(UiFactory.Text("Capture groups are available in the output as {g1}, {g2}, ...", "hint"));
            }

            when.Add(UiFactory.TextRow("Split input on", rule.fieldSeparator, v => rule.fieldSeparator = v));
            when.Add(UiFactory.Text("The incoming text is split on this, giving you {field0}, {field1}, ... to use below.", "hint"));

            _editor.Add(whenSection);

            // --- THEN ---
            // A rule can send several things at once, so this is a list of outputs
            // rather than a single target: one serial line can go out as OSC and as
            // UDP/TCP, each with its own payload and its own delay.
            VisualElement then;
            var label = rule.outputs.Count > 1
                ? "THEN  -  send " + rule.outputs.Count + " messages"
                : "THEN  -  outgoing message";
            var thenSection = UiFactory.Section(label, out then);

            for (var i = 0; i < rule.outputs.Count; i++)
            {
                BuildOutputEditor(rule, rule.outputs[i], i, then);
            }

            if (rule.outputs.Count == 0)
            {
                then.Add(UiFactory.Text("This rule sends nothing yet. Add an output below.", "hint"));
            }

            var addOutputRow = UiFactory.Row();
            addOutputRow.style.marginTop = 4;
            addOutputRow.Add(UiFactory.Btn("+ Add another output", () =>
            {
                _bridge.AddRuleOutput(rule);
                BuildEditor();
                BuildList();
            }, "btn--small"));
            then.Add(addOutputRow);

            _editor.Add(thenSection);

            // --- Options ---
            VisualElement options;
            var optionsSection = UiFactory.Section("OPTIONS", out options);
            options.Add(UiFactory.IntRow("Throttle (ms)", rule.throttleMs, v => rule.throttleMs = v, 0, 60000));
            options.Add(UiFactory.Text("Ignores messages arriving faster than this. 0 sends every one.", "hint"));
            options.Add(UiFactory.ToggleRow("Stop after this rule", rule.stopOnMatch, v => rule.stopOnMatch = v));
            _editor.Add(optionsSection);

            _editor.Add(BuildPreview(rule));
            _editor.Add(BuildTokenHelp());
        }

        /// <summary>One entry in the THEN list: where it goes, when, and what it says.</summary>
        private void BuildOutputEditor(RouteRule rule, RuleOutput ruleOutput, int index, VisualElement parent)
        {
            var block = UiFactory.Column("output-block");

            var header = UiFactory.Row();
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 6;
            header.Add(UiFactory.Text("OUTPUT " + (index + 1), "output-block__title"));

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            header.Add(spacer);

            // Keep at least one output; removing the last would leave a rule that
            // matches but sends nothing.
            if (rule.outputs.Count > 1)
            {
                header.Add(UiFactory.Btn("Remove", () =>
                {
                    _bridge.RemoveRuleOutput(rule, ruleOutput.id);
                    BuildEditor();
                    BuildList();
                }, "btn--small", "btn--danger"));
            }

            block.Add(header);

            block.Add(EndpointDropdown("Send to", ruleOutput.targetEndpointId, false, v =>
            {
                ruleOutput.targetEndpointId = v;
                BuildEditor();
                BuildList();
            }));

            block.Add(UiFactory.IntRow("Delay (ms)", ruleOutput.delayMs, v =>
            {
                ruleOutput.delayMs = v;
                BuildEditor();
            }, 0, 600000));
            block.Add(UiFactory.Text(
                ruleOutput.delayMs > 0
                    ? "Waits " + ruleOutput.delayMs + " ms after the message arrives, then sends."
                    : "Sends immediately.",
                "hint"));

            block.Add(UiFactory.EnumRow("Payload", ruleOutput.outputMode, (OutputMode v) =>
            {
                ruleOutput.outputMode = v;
                BuildEditor();
            }));

            if (ruleOutput.outputMode == OutputMode.Passthrough)
            {
                block.Add(UiFactory.Text("The incoming message is forwarded unchanged.", "hint"));
            }
            else
            {
                var target = _bridge.GetEndpoint(ruleOutput.targetEndpointId);

                if (target != null && target.kind == TransportKind.Osc)
                {
                    block.Add(UiFactory.TextRow("OSC address", ruleOutput.outputAddress,
                        v => { ruleOutput.outputAddress = v; RefreshPreview(); }));
                    BuildArgEditor(ruleOutput, block);
                }
                else if (target != null)
                {
                    block.Add(UiFactory.TextRow("Text to send", ruleOutput.outputText,
                        v => { ruleOutput.outputText = v; RefreshPreview(); }));
                }
                else
                {
                    block.Add(UiFactory.Text("Pick a target connection to configure the payload.", "hint"));
                }
            }

            parent.Add(block);
        }

        private void BuildArgEditor(RuleOutput ruleOutput, VisualElement parent)
        {
            parent.Add(UiFactory.Text("OSC ARGUMENTS", "section__title"));

            for (var i = 0; i < ruleOutput.outputArgs.Count; i++)
            {
                var index = i;
                var arg = ruleOutput.outputArgs[index];

                var row = UiFactory.Row("arg-row");
                row.Add(UiFactory.Text("arg " + index, "arg-row__index"));

                var typeNames = new List<string>(Enum.GetNames(typeof(ArgType)));
                var typeDropdown = new DropdownField(typeNames, Math.Max(0, typeNames.IndexOf(arg.type.ToString())));
                typeDropdown.style.width = 118;
                typeDropdown.style.marginRight = 6;
                typeDropdown.RegisterValueChangedCallback(evt =>
                {
                    ArgType parsed;
                    if (Enum.TryParse(evt.newValue, out parsed))
                    {
                        arg.type = parsed;
                        RefreshPreview();
                    }
                });
                row.Add(typeDropdown);

                var valueField = new TextField { value = arg.value };
                valueField.style.flexGrow = 1;
                valueField.style.marginRight = 6;
                valueField.RegisterValueChangedCallback(evt => { arg.value = evt.newValue; RefreshPreview(); });
                row.Add(valueField);

                row.Add(UiFactory.Btn("x", () =>
                {
                    ruleOutput.outputArgs.RemoveAt(index);
                    BuildEditor();
                }, "btn--small", "btn--danger"));

                parent.Add(row);
            }

            var addRow = UiFactory.Row();
            addRow.Add(UiFactory.Btn("+ Add argument", () =>
            {
                ruleOutput.outputArgs.Add(new OutputArg(ArgType.Auto, string.Empty));
                BuildEditor();
            }, "btn--small"));
            parent.Add(addRow);

            if (ruleOutput.outputArgs.Count == 0)
            {
                parent.Add(UiFactory.Text("No arguments - only the address will be sent.", "hint"));
            }
        }

        private VisualElement _previewList;
        private RouteRule _previewRule;

        private VisualElement BuildPreview(RouteRule rule)
        {
            _previewRule = rule;

            VisualElement body;
            var section = UiFactory.Section("PREVIEW", out body);

            var sampleField = new TextField("If this comes in") { value = _sampleInput };
            sampleField.AddToClassList("field");
            sampleField.RegisterValueChangedCallback(evt =>
            {
                _sampleInput = evt.newValue;
                RefreshPreview();
            });
            body.Add(sampleField);

            _previewList = new VisualElement();
            _previewList.style.marginTop = 8;
            body.Add(_previewList);

            RefreshPreview();
            return section;
        }

        /// <summary>Shows what every output would send, so a fan-out is visible at a glance.</summary>
        private void RefreshPreview()
        {
            if (_previewList == null || _previewRule == null) return;

            _previewList.Clear();

            if (_previewRule.outputs.Count == 0)
            {
                _previewList.Add(UiFactory.Text("This rule has no outputs yet.", "hint"));
                return;
            }

            for (var i = 0; i < _previewRule.outputs.Count; i++)
            {
                var ruleOutput = _previewRule.outputs[i];
                var prefix = "out " + (i + 1) + ":  ";

                BridgeMessage outbound;
                string reason;

                Label line;
                if (_bridge.PreviewRule(_previewRule, ruleOutput, _sampleInput, string.Empty, out outbound, out reason))
                {
                    var timing = ruleOutput.delayMs > 0 ? "  (after " + ruleOutput.delayMs + " ms)" : "  (immediately)";
                    line = UiFactory.Text(prefix + NameOf(ruleOutput.targetEndpointId) + "  <-  " + outbound.Describe() + timing, "hint");
                }
                else
                {
                    line = UiFactory.Text(prefix + reason, "hint");
                    line.AddToClassList("error-text");
                }

                line.style.marginBottom = 3;
                _previewList.Add(line);
            }
        }

        private static VisualElement BuildTokenHelp()
        {
            VisualElement body;
            var section = UiFactory.Section("TOKENS YOU CAN USE", out body);

            body.Add(UiFactory.Text(
                "{text}     the whole incoming message\n" +
                "{field0}   first piece after splitting, {field1} second, and so on\n" +
                "{arg0}     first OSC argument of the incoming message\n" +
                "{address}  incoming OSC address\n" +
                "{g1}       first regex capture group\n" +
                "{remote}   who sent it (IP:port, or COM port)\n" +
                "{time}     time it arrived\n" +
                "{hex}      raw bytes as hex",
                "hint"));

            return section;
        }
    }
}
