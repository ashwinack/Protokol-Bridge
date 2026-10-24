using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine.UIElements;

namespace ProtokolBridge.UI
{
    /// <summary>
    /// Small builders for the repetitive parts of the panel. Keeps the view code
    /// about layout and behaviour instead of VisualElement boilerplate.
    /// </summary>
    internal static class UiFactory
    {
        public static VisualElement Row(params string[] classes)
        {
            var element = new VisualElement();
            element.style.flexDirection = FlexDirection.Row;
            foreach (var c in classes) element.AddToClassList(c);
            return element;
        }

        public static VisualElement Column(params string[] classes)
        {
            var element = new VisualElement();
            foreach (var c in classes) element.AddToClassList(c);
            return element;
        }

        public static Label Text(string text, params string[] classes)
        {
            var label = new Label(text);
            foreach (var c in classes) label.AddToClassList(c);
            return label;
        }

        public static Button Btn(string text, Action onClick, params string[] classes)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("btn");
            foreach (var c in classes) button.AddToClassList(c);
            return button;
        }

        public static VisualElement Section(string title, out VisualElement body)
        {
            var section = new VisualElement();
            section.AddToClassList("section");
            section.Add(Text(title, "section__title"));

            body = new VisualElement();
            section.Add(body);
            return section;
        }

        public static TextField TextRow(string label, string value, Action<string> onChanged)
        {
            var field = new TextField(label) { value = value ?? string.Empty };
            field.AddToClassList("field");
            field.RegisterValueChangedCallback(evt => onChanged(evt.newValue));
            return field;
        }

        /// <summary>
        /// Integer input built on a TextField rather than IntegerField so that a
        /// half-typed value (like "" or "-") never snaps back while the operator types.
        /// </summary>
        public static TextField IntRow(string label, int value, Action<int> onChanged, int min = 0, int max = int.MaxValue)
        {
            var field = new TextField(label) { value = value.ToString(CultureInfo.InvariantCulture) };
            field.AddToClassList("field");
            field.RegisterValueChangedCallback(evt =>
            {
                int parsed;
                if (!int.TryParse(evt.newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return;
                if (parsed < min) parsed = min;
                if (parsed > max) parsed = max;
                onChanged(parsed);
            });
            return field;
        }

        public static Toggle ToggleRow(string label, bool value, Action<bool> onChanged)
        {
            var toggle = new Toggle(label) { value = value };
            toggle.AddToClassList("field");
            toggle.RegisterValueChangedCallback(evt => onChanged(evt.newValue));
            return toggle;
        }

        /// <summary>
        /// DropdownField is used everywhere instead of EnumField because it is
        /// guaranteed available in runtime builds and takes plain string choices.
        /// </summary>
        public static DropdownField DropdownRow(string label, List<string> choices, string current, Action<string> onChanged)
        {
            if (choices.Count == 0) choices.Add(string.Empty);

            var index = choices.IndexOf(current ?? string.Empty);
            if (index < 0) index = 0;

            var dropdown = new DropdownField(label, choices, index);
            dropdown.AddToClassList("field");
            dropdown.RegisterValueChangedCallback(evt => onChanged(evt.newValue));
            return dropdown;
        }

        public static DropdownField EnumRow<T>(string label, T current, Action<T> onChanged) where T : struct
        {
            var names = new List<string>(Enum.GetNames(typeof(T)));
            return DropdownRow(label, names, current.ToString(), value =>
            {
                T parsed;
                if (Enum.TryParse(value, out parsed)) onChanged(parsed);
            });
        }

        public static string StateClass(TransportState state)
        {
            switch (state)
            {
                case TransportState.Running: return "dot--running";
                case TransportState.Connecting: return "dot--connecting";
                case TransportState.Faulted: return "dot--faulted";
                default: return "dot--stopped";
            }
        }

        public static VisualElement StatusDot(TransportState state)
        {
            var dot = new VisualElement();
            dot.AddToClassList("dot");
            dot.AddToClassList(StateClass(state));
            return dot;
        }

        /// <summary>Repoints an existing dot at a new state without rebuilding it.</summary>
        public static void ApplyState(VisualElement dot, TransportState state)
        {
            if (dot == null) return;

            dot.RemoveFromClassList("dot--running");
            dot.RemoveFromClassList("dot--connecting");
            dot.RemoveFromClassList("dot--faulted");
            dot.RemoveFromClassList("dot--stopped");
            dot.AddToClassList(StateClass(state));
        }
    }
}
