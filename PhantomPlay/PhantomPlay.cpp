#define WIN32_LEAN_AND_MEAN             // Exclude rarely-used stuff from Windows headers
#define NOMINMAX
#include <Windows.h>
#include "MinHook.h"
#include <TlHelp32.h>
#include <cstdarg>
#include <cstdio>

// Finds the process's main window handle by enumerating the windows of each of its threads
// and picking the largest one. Used to know which window's messages to filter.
#pragma region GetMainWindow

struct EnumWindowsCallbackArgs
{
	HWND hwnd = nullptr;
	int area = -1;
};

static BOOL CALLBACK EnumWindowsCallback(HWND hnd, LPARAM lParam)
{
	EnumWindowsCallbackArgs* args = (EnumWindowsCallbackArgs*)lParam;

	if (!::IsWindowVisible(hnd))
		return true; // Skip hidden helper windows (e.g. offscreen CEF render surfaces --
		             // confirmed present alongside the real window in Steam's Big Picture
		             // process, where the real window is not otherwise the largest by area).

	RECT rect = { 0 };
	::GetWindowRect(hnd, &rect);
	int area = (rect.right - rect.left) * (rect.bottom - rect.top);
	if (area > args->area)
	{
		args->area = area;
		args->hwnd = hnd;
	}

	return true;
}

template<class _CB>
bool VisitProcessThreads(_CB visitor)
{
	HANDLE hThreadSnap = INVALID_HANDLE_VALUE;
	THREADENTRY32 te32;

	hThreadSnap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
	if (hThreadSnap == INVALID_HANDLE_VALUE)
		return false;

	te32.dwSize = sizeof(THREADENTRY32);

	if (!Thread32First(hThreadSnap, &te32))
	{
		CloseHandle(hThreadSnap);
		return false;
	}

	do
	{
		visitor(te32);
	} while (Thread32Next(hThreadSnap, &te32));

	CloseHandle(hThreadSnap);
	return true;
}


HWND GetMainWindow()
{
	DWORD currentProcessId = ::GetCurrentProcessId();
	EnumWindowsCallbackArgs args{};

	VisitProcessThreads([&](THREADENTRY32 threadEntry) {
		if (threadEntry.th32OwnerProcessID != currentProcessId)
			return;

		EnumThreadWindows(threadEntry.th32ThreadID, &EnumWindowsCallback, (LPARAM)&args);
		});

	return args.hwnd;
}

#pragma endregion


// Reads an optional override window title from a fixed per-user config file and does a
// system-wide FindWindowW for it. Lets a copy of this DLL be pointed at a window owned by a
// DIFFERENT process than the one it's injected into -- needed for processes that own no
// window of their own, like Steam's main `steam.exe` client. Confirmed 2026-09-12: injecting
// only into the `steamwebhelper.exe` process that owns "Steam Big Picture Mode" was NOT enough
// to keep controller input reaching Big Picture once real OS focus moved to an unrelated
// window -- `steam.exe` is presumably where Steam Input's own routing checks the real
// foreground window, and that check is untouched by hooking a different process. The override
// always wins over GetMainWindow() when present, since a process with no window of its own has
// nothing for GetMainWindow() to find anyway.
#pragma region ResolveTargetWindow

HWND ResolveOverrideTargetWindow()
{
	wchar_t path[MAX_PATH];
	DWORD len = ::GetTempPathW(MAX_PATH, path);
	if (len == 0 || len >= MAX_PATH)
		return nullptr;

	if (wcscat_s(path, MAX_PATH, L"phantomplay_target.txt") != 0)
		return nullptr;

	HANDLE hFile = ::CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
	if (hFile == INVALID_HANDLE_VALUE)
		return nullptr; // No override configured -- normal case, not an error.

	char buf[512] = { 0 };
	DWORD read = 0;
	BOOL ok = ::ReadFile(hFile, buf, sizeof(buf) - 1, &read, nullptr);
	::CloseHandle(hFile);
	if (!ok || read == 0)
		return nullptr;

	while (read > 0 && (buf[read - 1] == '\n' || buf[read - 1] == '\r' || buf[read - 1] == ' '))
		buf[--read] = '\0';
	if (read == 0)
		return nullptr;

	wchar_t wtitle[512] = { 0 };
	if (::MultiByteToWideChar(CP_UTF8, 0, buf, -1, wtitle, 512) == 0)
		return nullptr;

	return ::FindWindowW(nullptr, wtitle); // System-wide lookup -- works across processes, no special rights needed.
}

// Temporary diagnostic for the multi-process Big Picture experiment (2026-09-12) -- appends one
// line per injection so results can be checked externally (a plain file read, no elevation)
// instead of guessed at. Safe to leave in: negligible cost, and useful any time this needs
// re-verifying against a different game/process.
void DebugLog(const wchar_t* fmt, ...)
{
	wchar_t path[MAX_PATH];
	if (::GetTempPathW(MAX_PATH, path) == 0)
		return;
	wcscat_s(path, MAX_PATH, L"phantomplay_debug.log");

	wchar_t msg[1024];
	va_list args;
	va_start(args, fmt);
	vswprintf_s(msg, 1024, fmt, args);
	va_end(args);

	HANDLE hFile = ::CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
	if (hFile == INVALID_HANDLE_VALUE)
		return;
	DWORD written;
	::WriteFile(hFile, msg, (DWORD)(wcslen(msg) * sizeof(wchar_t)), &written, nullptr);
	::WriteFile(hFile, L"\r\n", 4, &written, nullptr);
	::CloseHandle(hFile);
}

HWND ResolveTargetWindow()
{
	HWND overrideHwnd = ResolveOverrideTargetWindow();
	if (overrideHwnd != nullptr) {
		DebugLog(L"pid=%lu ResolveTargetWindow: override hit, hwnd=0x%p", ::GetCurrentProcessId(), overrideHwnd);
		return overrideHwnd;
	}

	HWND local = GetMainWindow();
	DebugLog(L"pid=%lu ResolveTargetWindow: no override, GetMainWindow()=0x%p", ::GetCurrentProcessId(), local);
	return local;
}

#pragma endregion


// wrapper for easier setting up hooks for MinHook
template <typename T>
inline MH_STATUS MH_CreateHookEx(LPVOID pTarget, LPVOID pDetour, T** ppOriginal)
{
	return MH_CreateHook(pTarget, pDetour, reinterpret_cast<LPVOID*>(ppOriginal));
}

HWND hwnd; // Main window handle
BOOL unfocused = FALSE;
WNDPROC OldWndProc; // Original window procedure, restored on detach

static decltype(GetForegroundWindow)* real_GetForegroundWindow = GetForegroundWindow;
static decltype(SetCursorPos)* real_SetCursorPos = SetCursorPos;

HWND WINAPI DetourGetForegroundWindow()
{
	return hwnd; // Some games mute their audio when this returns a different window; pausing is handled separately in the window procedure below.
}

BOOL WINAPI DetourSetCursorPos(int X, int Y)
{
	if (unfocused) {
		// Swallow cursor repositioning while unfocused so games can't recapture the mouse.
		return TRUE;
	}
	return real_SetCursorPos(X, Y);
}


LRESULT CALLBACK NewWndProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
{
	if (message == WM_NCACTIVATE && wParam == TRUE) {
		unfocused = FALSE;
	}
	else if (message == WM_NCACTIVATE && wParam == FALSE) {
		unfocused = TRUE;
		return 0; // Swallowing this deactivation message keeps the window treated as active.
	}
	else if (message == WM_ACTIVATE && wParam == WA_INACTIVE) {
		return 0;
	}
	else if (message == WM_ACTIVATEAPP && wParam == FALSE) {
		return 0;
	}
	else if (message == WM_KILLFOCUS) {
		return 0;
	}
	else if (message == WM_IME_SETCONTEXT && wParam == FALSE) {
		return 0;
	}

	// Call the original window procedure
	return CallWindowProc(OldWndProc, hwnd, message, wParam, lParam);
}


BOOL APIENTRY DllMain(HMODULE hModule, DWORD  fdwReason, LPVOID lpReserved)
{
	switch (fdwReason) {
		case DLL_PROCESS_ATTACH:
		{
			MH_Initialize();

			hwnd = ResolveTargetWindow();

			MH_CreateHookEx(GetForegroundWindow, DetourGetForegroundWindow, &real_GetForegroundWindow);
			MH_CreateHookEx(SetCursorPos, DetourSetCursorPos, &real_SetCursorPos);

			if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK) {
				return FALSE;
			}

			// Subclassing only works for a window owned by this process. When hwnd came from
			// the cross-process override (e.g. this copy is injected into steam.exe but points
			// at Big Picture's window, which belongs to steamwebhelper.exe), SetWindowLongPtr
			// fails harmlessly here -- that's fine, the GetForegroundWindow/SetCursorPos
			// detours above are the part that matters for a process with no window of its own.
			if (hwnd != nullptr) {
				OldWndProc = (WNDPROC)SetWindowLongPtr(hwnd, GWLP_WNDPROC, (LONG_PTR)NewWndProc);
			}

			break;
		}

		case DLL_PROCESS_DETACH:
		{
			if (hwnd != nullptr && OldWndProc != nullptr) {
				SetWindowLongPtr(hwnd, GWLP_WNDPROC, (LONG_PTR)OldWndProc); // Restore the original window procedure before unloading.
			}

			MH_DisableHook(MH_ALL_HOOKS);
			MH_Uninitialize();
			break;
		}
	}
	return TRUE;
}