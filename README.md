# PhantomPlay

PhantomPlay keeps games and apps running normally when they go to the background. No more auto pausing, muted audio or dead controller input just because the window lost focus. It works by injecting a small DLL that keeps the target window believing it is always the active one.

It is mostly useful on multi-monitor setups, where you want a game to keep going on one screen while you do something else on another.

On top of the core hook, PhantomPlay can save games as auto-inject presets so they get hooked the moment they launch, ships with light and dark themes, and minimizes to the system tray to stay out of your way.

## Features

* Keep a game fully active in the background, with no forced pause and no muted audio
* Keep controller input alive while the game is not focused (works with XInput based games)
* Block mouse recapture that some games do through SetCursorPos
* Search box to filter the process list quickly
* Auto inject presets: save a game once and PhantomPlay hooks it automatically the next time it launches
* Minimize to the system tray with a tray icon and notifications
* Light and dark themes, and the window remembers its size and position between runs

## How to use

1. Run `PhantomPlayGUI.exe` as administrator. Injection needs elevated privileges.
2. Find the game in the top list (use the search box to filter by window title or executable).
3. Click **Inject**.
4. To revert, select the process in the **Injected** list and click **Unload**.

To make it hands free, select a process, click **Save preset**, and tick **Enable auto-inject**. From then on PhantomPlay injects into that game as soon as its window shows up.

## Building from source

Open `PhantomPlay.sln` in Visual Studio (2022 or newer) with the "Desktop development with C++" and ".NET desktop development" workloads installed, then build the solution. The native project produces both the 32-bit and 64-bit payload DLLs and copies them next to the GUI automatically.

## How it works

PhantomPlay replaces the target window procedure so the messages that announce a lost focus never reach the game, and it detours `GetForegroundWindow` and `SetCursorPos`. Together that keeps audio, gameplay and controller input running while the window sits in the background.

## Warning

This tool relies on process injection, so use it at your own risk. Stick to single-player games that actually need it. Never use it in online or competitive titles that run anti-cheat, since injection can get your account banned. Injecting into the wrong process can also freeze a window and force you to close it.

## Known limits

* Only the main window of the target is handled for now.
* While active it can be tricky to drag, minimize or close the window if the game has captured the mouse.

## Credits

PhantomPlay uses [MinHook](https://github.com/TsudaKageyu/minhook) for API hooking.
