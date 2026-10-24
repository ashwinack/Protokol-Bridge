# Protokol Bridge

A Unity-based protocol bridge application. It receives and sends on **Serial, OSC, UDP and
TCP** simultaneously, and routes between them with user-editable rules: e.g. a line arriving
on a COM port triggers a *different*, templated OSC message going out. The operator
configures connections and rules from a runtime UI in a standalone build — not from the
Unity Inspector.

Unity **6000.6.0f1**, URP 2D template, new Input System.

## Critical project setting

**API Compatibility Level must stay on .NET Framework** (`ApiCompatibilityLevel.NET_Unity_4_8`).
`System.IO.Ports.SerialPort` does not exist in the .NET Standard 2.1 profile, so serial
support silently stops compiling if anyone switches it back. The project shipped as
.NET Standard 2.1 and was changed deliberately.

## Architecture

All code lives in `Assets/ProtokolBridge/Runtime` under the `ProtokolBridge` namespace
(assembly `ProtokolBridge.Runtime`).

```
Core/       BridgeMessage, ITransport, TransportBase, StreamFramer, TrafficLog
Transports/ Serial, Udp, Osc (extends Udp), TcpClient, TcpServer
Osc/        OscCodec — dependency-free OSC 1.0 encode/decode, incl. #bundle
Routing/    RouteRule, TemplateResolver, RouteEngine
Config/     EndpointConfig, BridgeConfig, ConfigStore
BridgeManager.cs — MonoBehaviour that owns everything
```

### Threading contract

This is the rule that matters most. Every transport owns a background worker thread
(`TransportBase`) and **must never touch the Unity API**. Inbound messages are published to
`BridgeManager`'s `ConcurrentQueue`, which drains in `Update()`. All routing, logging and UI
work therefore happens on the main thread.

Subclassing a transport means implementing `OpenConnection`, `PumpOnce`, `WriteMessage` and
`CloseConnection`. `PumpOnce` must return promptly (block ~100 ms at most) so `Stop()` stays
responsive.

### Routing

`RouteRule` is "when X arrives, send these". The condition is matched once, then **every
entry in `rule.outputs` fires** — so one serial line can go out as OSC *and* UDP *and* TCP
in a single rule. Each `RuleOutput` carries its own target, payload mode, templates and
`delayMs`, which is what lets a rule stagger its sends.

`HitCount` counts rule firings, not individual outputs. An output aimed back at the rule's
own source is skipped per-output, not per-rule.

Rules written before multi-output stored one target inline. Those fields are still on
`RouteRule` under a "Legacy" heading purely so old JSON parses; `MigrateLegacy()` (called
from `ConfigStore.TryLoad`) folds them into `outputs` and blanks the inline target. Do not
read the legacy fields anywhere else, and do not delete them — a saved config from an
earlier build would silently lose its routing.

The output side is templated, which is what makes the forwarded message editable rather
than a copy. `TemplateResolver` expands tokens:
`{text}`, `{address}`, `{arg0}`, `{field0}` (input split on `fieldSeparator`), `{g1}` (regex
groups), `{remote}`, `{time}`, `{hex}`.

Verified example: serial line `TEMP,kitchen,21.7` with address template `/room/{field1}/temp`
and args `[Float "{field2}", String "from-{field1}"]` produces
`/room/kitchen/temp 21.7 from-kitchen`.

A rule whose target equals its source is skipped, to prevent feedback loops.

`RuleOutput.delayMs` holds that one outgoing message back by that many milliseconds, so a
single rule can fan one input out to OSC and UDP at different times. Delayed sends park in
`BridgeManager`'s
`_delayed` list and go out from `FlushDelayed()` in `Update()`, so resolution is one frame
and insertion order is preserved. The list is capped at `MaxDelayedMessages` and cleared by
`StopAll()`. Note `delayMs` (hold this message back) is not `throttleMs` (drop messages that
arrive too close together) — both exist and do different jobs.

A delayed message is timestamped when it actually leaves, not when its trigger arrived, so
the traffic log shows real send times.

### Config

JSON at `Application.persistentDataPath/protokol-bridge.json`, via Unity's `JsonUtility`.
That means **no dictionaries, no polymorphism, no auto-properties** in config classes —
keep `EndpointConfig` / `RouteRule` / `BridgeConfig` as flat `[Serializable]` classes with
public fields. `JsonUtility` also leaves lists null when a key is absent, so `ConfigStore`
re-initialises them on load.

## UI

Runtime UI Toolkit, in `Runtime/UI`, all built programmatically — the endpoint and rule
lists are dynamic and change shape per transport kind, so UXML templates would only have
moved the work into C# anyway. Styling lives in `Resources/ProtokolBridge.uss`, loaded at
runtime via `Resources.Load<StyleSheet>("ProtokolBridge")`, with
`ProtokolBridgeTheme.tss` (`@import url("unity-theme://default")`) supplying Unity's
built-in control styling through the PanelSettings asset.

`DropdownField` is used everywhere instead of `EnumField`, and numeric inputs are
`TextField` + parse instead of `IntegerField`, so nothing depends on editor-only controls
and a half-typed number never snaps back under the operator.

**Unity's default runtime theme is a light theme.** It draws every input
(`TextField`, `DropdownField`, `Toggle`, scrollbars) on a near-white background. On this
dark panel the inherited light body text landed on near-white and every field was
unreadable, even though the panel's own labels were fine. The USS therefore has a
"Built-in controls" block at the end that restyles those `.unity-*` classes dark, using
literal colours instead of `var()` so the theme cannot win on load order. Any new built-in
control type needs a rule added there — check it against a real screenshot, because the
resolved colours on the panel's own elements look correct either way.

**`overflow: hidden` clips vertically as well as horizontally.** The traffic log needs it so
a long message truncates instead of shoving the RULE column off the edge, but with no
explicit height the columns had their glyph tops and bottoms sliced off. Three numbers must
agree: `TrafficPanel.RowHeight`, the `.log-row` height in USS, and the column height inside
it (currently 22 / 22 / 20, with `-unity-text-align: middle-left`). Change one and change
all three.

The scene is `Assets/Scenes/Bridge.unity` (build scene 0): one GameObject carrying
`BridgeManager`, `UIDocument` and `BridgeUIController`.

**Component order is not guaranteed.** `BridgeUIController.OnEnable` ran before
`BridgeManager.Awake` and threw a NullReferenceException on first run. The manager now
exposes an idempotent `EnsureInitialized()` that both `Awake` and the UI call. Do not
reintroduce an ordering assumption here.

**Connection state cannot be baked into the panel at build time.** `Start()` sets a
transport to `Connecting`; the worker thread flips it to `Running` a few milliseconds later
after `OpenConnection()` succeeds. Clicking Start rebuilds the editor inside that window, so
anything rendered from the state variable froze on "Connecting" forever while the list dot —
which *was* refreshed on a timer — went green. The editor header, its status dot, its
Start/Stop buttons, the error line, the TCP client count and the "start the connection
first" warning are therefore held as fields on `EndpointsPanel` and updated in place by
`RefreshEditorStatus()`. Anything new that displays live connection state belongs there, not
in `BuildEditor`.

## Performance

Measured on this project, not assumed. Re-measure before changing any of it.

**Idle render cost was the dominant waste.** This is a tool whose panel changes about four
times a second, but it rendered continuously: measured **834 FPS while completely idle**
with zero messages. `BridgeManager.ApplyFrameRate()` now caps it via
`Config.targetFrameRate` (default 60), giving **59.4 FPS idle** — 14x fewer frames. Two
things matter here: `vSyncCount` **must** be 0 or Unity ignores `targetFrameRate`
entirely, and the frame rate sets the resolution of rule delays, because `FlushDelayed()`
runs in `Update()`. Below ~30 FPS short delays get coarse. Receive speed is unaffected —
transports run on their own threads.

**Line framing was quadratic.** `StreamFramer.Append` and the old
`SerialTransport.ExtractLines` both called `buffer.ToString()` *inside* the extraction
loop plus `Remove(0, n)` per line, so a chunk holding k lines cost O(k*n). Measured: 16x
the lines cost **122x** the time (0.0052 ms -> 0.6362 ms going from 20 to 320 lines), and
11.8x the allocation. Both now scan the StringBuilder by index and compact once per chunk.
`SerialTransport` was changed to use the shared `StreamFramer` so there is one
implementation rather than two copies of the same bug. Verified over a real TCP connection
including the partial-line-across-writes case.

**Per-message GC is fine — do not "optimize" it.** Measured 5 gen0 collections per 200,000
routed messages, and 0 per 200,000 `Describe()` or `Split`+`Trim` calls. At 1000 msg/s
that is one cheap gen0 roughly every 40 seconds. Pooling `BridgeMessage`, de-boxing OSC
arguments, or reusing the UDP receive buffer would all add real risk — `BridgeMessage.Raw`
aliases the receive array, so a reused buffer would corrupt in-flight messages — for
savings the measurements do not justify.

**UI callbacks must not rebuild the panel.** `UiFactory.IntRow` is a `TextField` with
`RegisterValueChangedCallback`, which fires on *every keystroke*. The Delay field used to
call `BuildEditor()` from that callback, which cleared and rebuilt the panel and destroyed
the field being typed into — only one digit could ever be entered. Hints are now updated
in place. Never call `BuildEditor()` / `Rebuild()` from a text field's value-changed
callback; dropdowns, toggles and buttons are fine because they commit discretely.

## Testing without hardware

`Unity_RunCommand` exercises the whole stack — construct transports directly, bind two OSC
endpoints on loopback, send between them. This verified the engine before any UI existed,
with no COM port attached.

Two limits of that tool are worth knowing:

- **The command assembly cannot reference `System.dll`**, so `UdpClient`, `IPEndPoint` and
  friends fail to compile inside a command. Construct a `UdpTransport`/`OscTransport` from
  this project instead — those live in `ProtokolBridge.Runtime`, which does reference it.
  `System.Reflection` is also blocked.
- **The Editor does not tick the player loop while its window is unfocused.** `Time.frameCount`
  stays at 1, `Update()` never runs, and `EditorApplication.QueuePlayerLoopUpdate()` does not
  help. Transport worker threads keep running on their own, so inbound messages still arrive
  and sit in `PendingCount` — anything that depends on `Update()` (routing, logging, the
  traffic view) needs the Unity window focused to verify.
