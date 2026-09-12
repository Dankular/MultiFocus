using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SharpestInjector;

namespace PhantomPlayOrchestrator
{
    // Watches for the Big Picture <-> launched-game handoff and keeps the PhantomPlay hook
    // pointed at whichever one should currently be treated as focused, so a controller keeps
    // driving it while mouse/keyboard are used elsewhere on the desktop.
    //
    // Why this exists (see CLAUDE.md, project root, for the full investigation): the process
    // that actually decides where Steam routes controller input (`steam.exe`) owns no window of
    // its own, so it has to be told -- via the DLL's cross-process override file -- which window
    // to treat as focused. That target changes over time (Big Picture, then a launched game,
    // then back to Big Picture when the game closes), and PIDs are not stable across relaunches,
    // so this has to be watched for rather than set up once by hand.
    //
    // Game detection is deliberately NOT install-path-based (e.g. "steamapps\common\") --
    // confirmed on this machine that a real Steam-launched game can live somewhere else
    // entirely (GTA V here is at D:\Games\..., not under any Steam library folder). Instead a
    // candidate "game" is any process with a visible, titled window whose ancestor chain
    // (parent, grandparent, ...) includes steam.exe -- how Steam actually launches games,
    // regardless of where they're installed.
    class Program
    {
        static string LogPath = Path.Combine(Path.GetTempPath(), "phantomplay_orchestrator.log");
        // Payload DLLs land here via PhantomPlay.vcxproj's post-build step -- next to the
        // solution, not any particular consumer project's own output folder.
        static string DllDir = @"D:\Dev Proj\MultiFocus\PhantomPlay\Payload\Release";
        static string TargetConfigPath => Path.Combine(Path.GetTempPath(), "phantomplay_target.txt");

        static readonly HashSet<string> SteamHelperNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "steamwebhelper", "steamservice", "steamerrorreporter", "steamerrorreporter64",
            "gameoverlayui", "gameoverlayui64", "streaming_client", "steam.exe",
        };

        static void Log(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Console.WriteLine(line);
            try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
        }

        #region Win32 process-tree helpers

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
        [DllImport("kernel32.dll")]
        static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
        [DllImport("kernel32.dll")]
        static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);
        const uint TH32CS_SNAPPROCESS = 0x00000002;

        // pid -> parent pid, for every process in the system right now.
        static Dictionary<uint, uint> SnapshotParentMap()
        {
            var map = new Dictionary<uint, uint>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap.ToInt64() == -1)
                return map;

            try
            {
                var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (!Process32First(snap, ref pe))
                    return map;
                do
                {
                    map[pe.th32ProcessID] = pe.th32ParentProcessID;
                } while (Process32Next(snap, ref pe));
            }
            finally
            {
                CloseHandle(snap);
            }
            return map;
        }

        static bool IsDescendantOf(uint pid, uint ancestorPid, Dictionary<uint, uint> parentMap, int maxDepth = 8)
        {
            uint current = pid;
            for (int i = 0; i < maxDepth; i++)
            {
                if (!parentMap.TryGetValue(current, out uint parent) || parent == 0)
                    return false;
                if (parent == ancestorPid)
                    return true;
                current = parent;
            }
            return false;
        }

        #endregion

        #region Target discovery

        static Process FindBigPicture()
        {
            return Process.GetProcesses().FirstOrDefault(p =>
            {
                try { return p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.IndexOf("Steam Big Picture Mode", StringComparison.OrdinalIgnoreCase) >= 0; }
                catch { return false; }
            });
        }

        static Process FindSteamClient()
        {
            return Process.GetProcessesByName("steam").FirstOrDefault();
        }

        // Any visibly-windowed process descended from steam.exe that isn't one of Steam's own
        // known helper processes. Not path-based -- see the class comment for why.
        static Process FindLaunchedGame(uint steamPid, Dictionary<uint, uint> parentMap)
        {
            Process best = null;
            DateTime bestStart = DateTime.MinValue;

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrEmpty(p.MainWindowTitle))
                        continue;
                    if (SteamHelperNames.Contains(p.ProcessName))
                        continue;
                    if (!IsDescendantOf((uint)p.Id, steamPid, parentMap))
                        continue;

                    // Prefer the most recently started candidate if more than one somehow
                    // qualifies (e.g. a launcher stub plus the real game window).
                    DateTime start;
                    try { start = p.StartTime; } catch { start = DateTime.MinValue; }
                    if (best == null || start > bestStart)
                    {
                        best = p;
                        bestStart = start;
                    }
                }
                catch { /* process exited mid-enumeration, access denied, etc. */ }
            }
            return best;
        }

        #endregion

        #region Injection primitives (mirrors the manual flow already proven against Big Picture/GTA)

        static bool IsLoaded(Process p)
        {
            try
            {
                var info = Injector.GetProcessInfo(p);
                return info.Modules.Keys.Any(k => k.Contains("PHANTOMPLAY"));
            }
            catch { return false; }
        }

        static void SetOverride(string title)
        {
            File.WriteAllText(TargetConfigPath, title);
            Log($"Override -> '{title}'");
        }

        static void ClearOverride()
        {
            if (File.Exists(TargetConfigPath)) File.Delete(TargetConfigPath);
        }

        // Injects into `proc`. If a copy is already loaded, unloads first so the DLL re-reads
        // the override file fresh instead of keeping whatever HWND it originally resolved --
        // this is the piece that lets steam.exe be re-pointed at a new target over time.
        static bool EnsureFreshlyInjected(Process proc, string label)
        {
            try
            {
                var info = Injector.GetProcessInfo(proc);
                if (info.Modules.Count == 0)
                {
                    Log($"ERROR: can't read modules for {label} (pid={proc.Id})");
                    return false;
                }

                string dllPath = info.Is64Bit ? Path.Combine(DllDir, "PhantomPlay64.dll") : Path.Combine(DllDir, "PhantomPlay.dll");
                if (!File.Exists(dllPath))
                {
                    Log($"ERROR: payload dll missing at {dllPath}");
                    return false;
                }
                var pe = PeFile.Parse(dllPath);
                if (pe.Is64Bit != info.Is64Bit)
                {
                    Log($"ERROR: bitness mismatch injecting {label}");
                    return false;
                }

                bool alreadyLoaded = info.Modules.Keys.Any(k => k.Contains("PHANTOMPLAY"));
                if (alreadyLoaded)
                {
                    Injector.Unload(info, pe);
                    System.Threading.Thread.Sleep(300);
                }

                Injector.Inject(info, pe);
                System.Threading.Thread.Sleep(400);

                bool loaded = IsLoaded(Process.GetProcessById(proc.Id));
                Log($"Injected {label} (pid={proc.Id}): {(loaded ? "OK" : "FAILED")}");
                return loaded;
            }
            catch (Exception ex)
            {
                Log($"EXCEPTION injecting {label}: {ex.Message}");
                return false;
            }
        }

        #endregion

        enum TargetKind { None, BigPicture, Game }

        static void Main()
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); // needed for cp1252, used by SharpestInjector.Constants

            Console.WriteLine("=== PhantomPlay Orchestrator ===");
            Console.WriteLine($"log: {LogPath}");

            bool isAdmin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            Log($"Started, elevated={isAdmin}");
            if (!isAdmin)
                Log("WARNING: not elevated -- injection will fail.");

            var kind = TargetKind.None;
            int? currentGamePid = null;
            int? bigPicturePid = null;
            int? steamExePid = null; // if this PID changes, steam.exe restarted and lost all hooks.

            while (true)
            {
                try
                {
                    Tick(ref kind, ref currentGamePid, ref bigPicturePid, ref steamExePid);
                }
                catch (Exception ex)
                {
                    Log($"EXCEPTION in tick: {ex}");
                }
                System.Threading.Thread.Sleep(2000);
            }
        }

        static void Tick(ref TargetKind kind, ref int? currentGamePid, ref int? bigPicturePid, ref int? steamExePid)
        {
            var steam = FindSteamClient();
            if (steam == null)
            {
                if (kind != TargetKind.None) Log("steam.exe not found -- Steam appears to be closed. Idle.");
                kind = TargetKind.None;
                currentGamePid = null;
                bigPicturePid = null;
                steamExePid = null;
                return;
            }

            bool steamRestarted = steamExePid.HasValue && steamExePid.Value != steam.Id;
            if (steamRestarted)
            {
                Log($"steam.exe restarted (pid {steamExePid} -> {steam.Id}) -- all prior hooks are gone, resetting state.");
                kind = TargetKind.None;
                currentGamePid = null;
                bigPicturePid = null;
            }
            steamExePid = steam.Id;

            var parentMap = SnapshotParentMap();
            var bp = FindBigPicture();
            var game = FindLaunchedGame((uint)steam.Id, parentMap);

            if (game != null)
            {
                bool isNewGame = kind != TargetKind.Game || currentGamePid != game.Id;
                if (isNewGame)
                {
                    Log($"Game detected: '{game.MainWindowTitle}' ({game.ProcessName}, pid={game.Id})");

                    // The game's own copy must self-discover its own window -- clear any
                    // leftover override first (see the commit history for why this bit us).
                    ClearOverride();
                    bool gameOk = EnsureFreshlyInjected(game, "game");
                    if (!gameOk)
                    {
                        Log("Failed to inject the game; will retry next tick.");
                        return;
                    }

                    SetOverride(game.MainWindowTitle);
                    bool steamOk = EnsureFreshlyInjected(steam, "steam.exe");
                    if (!steamOk)
                    {
                        Log("Failed to (re)inject steam.exe for the game; will retry next tick.");
                        return;
                    }

                    kind = TargetKind.Game;
                    currentGamePid = game.Id;
                    Log($"Now pointed at the game. Controller should keep driving it while mouse/keyboard are used elsewhere.");
                }
                return;
            }

            // No game running right now.
            if (kind == TargetKind.Game)
            {
                Log("Game no longer running -- reverting focus target to Big Picture.");
                currentGamePid = null;
                kind = TargetKind.None; // fall through to the Big Picture handling below
            }

            if (bp == null)
            {
                if (kind != TargetKind.None) Log("Big Picture not running either. Idle.");
                kind = TargetKind.None;
                bigPicturePid = null;
                return;
            }

            bool bpIsNewOrUnhooked = kind != TargetKind.BigPicture || bigPicturePid != bp.Id || !IsLoaded(bp);
            if (bpIsNewOrUnhooked)
            {
                Log($"(Re)pointing at Big Picture (pid={bp.Id}).");
                ClearOverride();
                if (!EnsureFreshlyInjected(bp, "Big Picture"))
                {
                    Log("Failed to inject Big Picture; will retry next tick.");
                    return;
                }

                SetOverride(bp.MainWindowTitle);
                if (!EnsureFreshlyInjected(steam, "steam.exe"))
                {
                    Log("Failed to (re)inject steam.exe for Big Picture; will retry next tick.");
                    return;
                }

                kind = TargetKind.BigPicture;
                bigPicturePid = bp.Id;
                Log("Back on Big Picture as the focus target.");
            }
        }
    }
}
