# Key Inject — Stream Deck keyboard-injection plugin (Windows)

Turns Stream Deck buttons into real keyboard input for games: single presses,
modifier combos, hold-while-pressed, and timed sequences. Keys are injected
with the Win32 `SendInput` API into whatever window has focus.

## Install

1. Double-click `KeyInject.streamDeckPlugin` (or drag it into the Stream Deck app).
2. Because it is unsigned, Stream Deck asks you to enable
   *Allow plugins from community* in Developer settings — accept it.
3. The bridge (`windows/keyinject-bridge.exe`) starts automatically with
   Stream Deck; it registers as the plugin process and listens on
   `ws://127.0.0.1:12345` for its Property Inspectors.

## Action modes

| Mode | Behaviour |
|---|---|
| **Press & release** | Taps the key/combo once when you press the button. |
| **Hold while pressed** | Key goes down on button press, up on release. A configurable safety timer force-releases if the up-event is ever lost. |
| **Sequence / macro** | Timed script run on one press. |

### Key tokens
`a-z`, `0-9`, punctuation, `F1–F12`, `Esc Tab Enter Space Backspace Insert
Delete Home End PageUp PageDown Up Down Left Right PrtSc Pause CapsLock
NumLock ScrollLock`, numpad `num0–num9 numadd numsub nummultiply numdec luent`,
modifiers `Ctrl Alt Shift Win` (and `RCtrl RAlt RShift RWin`).

Combos: last key is the one pressed, e.g. `ctrl+shift+a`.
The Property Inspector has a **Capture** button — click it, press the key.

### Sequence grammar (comma-separated)
- `key` — tap
- `+key` / `-key` — hold down / release
- `200` or `200ms` — wait

Example (strafe then continue): `w, 200, +s, 300, -s, 200, -w`

## Cooldown animation

Per-button **Cooldown** field (seconds, `0` = off). After a press the key plays a
recharge animation and ignores further presses until it is ready again.

- The ring is drawn by the bridge: one **PNG** frame per 100 ms pushed with
  `setImage`. Stream Deck has no animation support for key images (GIF and
  friends are refused), so the plugin makes the film itself. SVG data URIs are
  *not* refused — the socket accepts them and the app simply never draws them,
  which is why an earlier version animated the title but showed no ring. The
  frames are 144x144 GDI+ bitmaps, cached per remaining percent (~4.5 kB of
  base64 each).
- The ring alone carries the information: the key's title is never touched, so
  whatever the user named the button is exactly what is on it before, during and
  after the cooldown. Two bars sit in the middle of every frame, so the key reads
  as waiting rather than ready, and the arc turns amber in the last quarter so
  you can see it is about to come back.
- When it recharges, the bridge sends `setState` and then `setImage` **with the
  image field omitted** — per the protocol that resets the key to whatever
  artwork the user had configured. No green tick, no extra flourish.
- A key whose **icon was picked in Stream Deck itself** is drawn by the app, not
  by the plugin, and the app lets that icon win: every frame we push at such a
  key is ignored, and so is a manifest state switch. The press and its cooldown
  still happen, only the animation is missing there.
- Presses during the cooldown are dropped, not queued, and logged as
  `Deck press ignored (still cooling down)`.
- **Preview** in the Property Inspector runs one cycle on the real key without
  sending any keystroke, so you can tune the length outside the game.

Implementation notes: each key's cooldown is independent, tracked by the
instance `context`. The animation deliberately never writes to the key's title —
earlier builds borrowed the title cell for a countdown and had to restore it
afterwards, and restoring the wrong thing left keys blank (`titleParameters`
holds only font settings, not the title). `tools/PiProbe.exe <context> <device>
[seconds]` drives the same path from the command line — it speaks the Property
Inspector protocol against the bridge's own server, which is how the animation
was tested without pressing anything. Add `raw @file.json` to send any other PI
command (the settings mirror is verified that way).

## Settings mirror

Stream Deck sometimes hands the plugin an **empty** settings object for a
configured button, and the Property Inspector can then save that emptiness back,
silently killing the button (the settings also arrive double-nested as
`"settings":{"settings":{…}}`, which is what made the inspector read them as
empty). So:

- the inspector unwraps the nesting and treats an empty object as *unknown*, not
  as *empty*, healing from its own `localStorage` copy instead of wiping;
- the bridge keeps its own mirror per instance in
  `%LOCALAPPDATA%\KeyInject\settings.store`, fed by every inspector save
  (`{"type":"settings",…}`) and every press. If a press arrives with empty
  settings the mirror is used and logged as
  `Empty settings from Stream Deck; using bridge mirror`;
- a button with no key, or with a token that does not resolve, flashes red
  (`showAlert`) and logs the reason instead of doing nothing silently.

Two instances of the same action are easy to mix up, so the inspector prints
`editing: Keypad col C, row R (id xxxxxxxx)` and the press log prints the slot
coordinates.

## Hot-swap gotcha

Replacing the running exe means renaming it first (Windows locks the image), but
Stream Deck respawns the plugin within ~2 s of it dying and can open the file a
moment before the rename lands — the process then keeps running *under the new
file name* (`keyinject-bridge.prev`) and the fresh build never starts. After a
swap, check `Get-Process | Where-Object { $_.ProcessName -like '*keyinject*' }`
and kill any `.prev` remnant so the real file is launched.

## Safety

- Any held keys are force-released when a Property Inspector connection drops.
- The bridge releases everything it holds on process exit.
- Sequences end with a global release of everything they pressed.

## Game input (why Notepad worked and games did not)

Windows keyboard events carry two identities: a **virtual-key code** (what text
input uses) and a **hardware scan code** (what Raw Input / DirectInput readers
use). Notepad and browsers only look at the virtual key, so a vk-only event
works there while a game sees `MakeCode = 0` and ignores it.

The bridge therefore sends **one event carrying both** `wVk` and `wScan`
(`wScan` from `MapVirtualKey` for the active layout). Verified with
`tools/RawProbe.exe`, which registers for Raw Input in a focused window:

| injection shape            | Raw Input MakeCode | cooked keydowns |
| -------------------------- | ------------------ | --------------- |
| vk only (`wScan = 0`)      | 0 — invisible      | 1               |
| vk + scan (current bridge) | 38 for `L`         | 1 (no duplicate) |

`tools/InjectProbe.exe` is the no-duplication check (counts key state
transitions; 2 = single tap).

### Diagnosing a game that still ignores presses

Every press logs the real target:

```
Deck press: mode=tap keys="l" | target: win='Star Citizen' proc=Star Citizen.exe pid=1234 target=normal
```

- `target=ELEVATED` — the game runs as Administrator and Windows (UIPI) silently
  drops input from the non-elevated bridge. Fix: launch the game without
  "Run as administrator", or start the Stream Deck app elevated (children
  inherit elevation).
- `target=normal` but still nothing — the game filters injected keystrokes
  itself; that needs HID-level input rather than `SendInput`.
- Some titles (Star Citizen with Easy Anti-Cheat) restrict synthetic **mouse**
  events but accept keyboard ones, so prefer keyboard bindings.

## Build from source

```powershell
.\build.ps1     # uses only Windows built-ins (.NET Framework csc + System.Drawing)
```

Output: `KeyInject.streamDeckPlugin` (everything under `com.sundowner.keyinject.sdPlugin/`).
Replace an installed build without restarting Stream Deck by renaming the
running exe, copying the new one in, then killing the bridge process so the app
respawns it.

## Layout

- `src/Bridge.cs` — plugin process: Stream Deck registration client, WebSocket
  server for Property Inspectors, SendInput executor, key parser.
- `plugin/manifest.json` — v2 SDK manifest (`SDKVersion: 2`).
- `plugin/property-inspector/` — UI; talks to the bridge directly and includes
  a fallback shim so it works even when the Elgato CDN is unreachable.

## Known limits

- Keys always go to the **focused window**; Stream Deck itself must not hold
  focus mid-press (it doesn't — input is sent from the bridge process).
- A title that filters injected keystrokes (marked by the low-level input
  stack) cannot be reached with `SendInput` at all; that needs a virtual HID
  device, which requires a driver.


## License

GPL-3.0-only, full text in `LICENSE`; the source files carry the
`SPDX-License-Identifier: GPL-3.0-only` header. In short: use it, change it,
redistribute it, sell it if you like — but anything you distribute that builds
on this has to ship its source under the same licence and be marked as changed.
Copyright 2026 Sundowner.
