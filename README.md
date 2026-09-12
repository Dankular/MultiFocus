# MultiFocus

Keeps a game (or Steam Big Picture) fully active — no auto-pause, no muted
audio, no dead controller input, no mouse getting stuck — while you use the
mouse and keyboard on another window or monitor, and automatically carries
that over when Big Picture hands off to a launched game and back.

Single-mouse, multi-monitor setup: game on one screen, something else (a
browser, chat, whatever) on the other, one mouse shared between them.
Windows normally pauses/mutes/disconnects input from whichever window isn't
the real foreground one — this works around that.

## Built on PhantomPlay

This is a fork of [PhantomPlay](https://github.com/K1tsune12/PhantomPlay),
which supplied the core technique and is still exactly what does the actual
hooking:

- the hook DLL (`PhantomPlay/`) — subclassing the target window's `WNDPROC`
  to intercept and swallow the messages that announce lost focus
  (`WM_NCACTIVATE`, `WM_ACTIVATE`, `WM_ACTIVATEAPP`, `WM_KILLFOCUS`,
  `WM_IME_SETCONTEXT`) before the game's own handler ever sees them, plus
  MinHook inline hooks on `GetForegroundWindow` and `SetCursorPos` so the
  game can't tell it lost focus or recapture the mouse
- the injector (`SharpestInjector/`) — manual-maps the DLL into the target
  process via `LoadLibraryW` through a remote thread
- [MinHook](https://github.com/TsudaKageyu/minhook) for the inline API hooks

PhantomPlay's own GUI (`PhantomPlayGUI`) is **not** part of this fork
anymore — everything here is driven by the orchestrator below instead of
manual point-and-click injection, so it was removed.

## What this fork adds

Built to also cover Steam Big Picture and games launched from it, which
turned out to need more than the original single-process design:

- **Visible-window filtering** in the hook's window discovery
  (`GetMainWindow()`). Big Picture's process also owns a larger, invisible
  off-screen CEF helper window that the original "largest window, any
  visibility" heuristic would pick instead of the real UI window.
- **A cross-process target override** — the DLL can be pointed, via a small
  config file, at a window owned by a *different* process than the one it's
  injected into. Needed because the process that actually decides where
  Steam routes controller input (`steam.exe`) owns no window of its own to
  discover locally.
- **A `ClipCursor` hook** — once a game stops seeing real deactivation, it
  also never releases a cursor clip (`ClipCursor`) it set while genuinely
  focused, so the mouse gets stuck inside the game window even though the
  window itself correctly stops pausing. Swallows new clip requests while
  unfocused and force-releases whatever's already active the moment real
  deactivation is intercepted.
- **`Orchestrator/`** — a persistent, self-elevating watcher that replaces
  the old GUI entirely. Every couple of seconds it finds Steam, Big Picture,
  and any currently launched game (by walking the parent-process chain from
  `steam.exe` — not by install path, since a Steam-launched game doesn't
  have to live under a Steam library folder), and keeps the hook and the
  cross-process override pointed at whichever one should currently be
  treated as focused. Confirmed working, unattended, across multiple
  different titles in one session with zero manual commands after it's
  started.

See `CLAUDE.md` (one level up, not part of this repo) for the full
investigation log this was built from — what was verified by direct
testing vs. assumed, and what's still open.

## Scope

Single-player / non-competitive only. Never point this at an
anti-cheat-protected online session — DLL injection into a multiplayer
game's process is how accounts get banned. Scoped to singleplayer titles
and to Steam's own client process, not to any online/competitive title.

## Building

Open `PhantomPlay.sln` in Visual Studio 2022+ ("Desktop development with
C++" and ".NET desktop development" workloads) and build the solution, or:

```
MSBuild PhantomPlay.sln -p:Configuration=Release -p:Platform="Any CPU"
```

This produces the payload DLLs at `Payload\Release\PhantomPlay.dll` (32-bit)
and `Payload\Release\PhantomPlay64.dll` (64-bit), and the orchestrator at
`Orchestrator\bin\Release\net8.0-windows\PhantomPlayOrchestrator.exe`.
Windows-only — this will not build in a Linux/sandbox environment.

## Running

Run `PhantomPlayOrchestrator.exe`. It self-elevates (needs admin for
injection) and then runs in the background — no further interaction needed.
It finds Big Picture and any launched game on its own and keeps them hooked
and pointed correctly as you move between them.

## Warning

This relies on process injection. Stick to single-player games that
actually need it, and to Steam's own client process — never an online or
competitive title running anti-cheat. Injecting into the wrong process can
freeze a window and force you to close it.

## Known limits

- Only each target's main window is handled.
- Audio behavior for a launched game hasn't been specifically verified —
  only that the window keeps rendering/updating and doesn't pause.
