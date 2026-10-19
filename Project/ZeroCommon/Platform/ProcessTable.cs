using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Agent.Common.Platform;

/// <summary>
/// One snapshot of the system's (pid, parent pid) pairs, and the descendants of a process in it.
/// Agent-state detection uses it to find the agent CLI inside a terminal tab: the PTY starts a
/// shell, the shell starts <c>claude.exe</c>, and only the shell's pid is known.
///
/// <para>Windows reads one Toolhelp32 snapshot (no process started, a few milliseconds); macOS
/// and Linux run <c>ps</c>, which costs a process launch — callers cache it (see
/// <see cref="Agents.ClaudeLiveStatusSource"/>). AgentOne has its own copy of the same walk
/// (<c>Processes/ProcessTree</c>) because it references nothing in this solution.</para>
/// </summary>
public sealed class ProcessTable
{
    private readonly Dictionary<int, List<int>> _children = new();
    private readonly HashSet<int> _alive = new();
    private readonly Dictionary<int, string> _names = new();
    private readonly Dictionary<int, int> _parent = new();

    public ProcessTable(IEnumerable<(int Pid, int Parent)> rows)
        : this(rows.Select(r => (r.Pid, r.Parent, "")))
    {
    }

    /// <param name="rows">pid, parent pid, and the executable's file name ("codex.exe", "codex").</param>
    public ProcessTable(IEnumerable<(int Pid, int Parent, string Name)> rows)
    {
        foreach (var (pid, parent, name) in rows)
        {
            _alive.Add(pid);
            if (!string.IsNullOrEmpty(name)) _names[pid] = name;
            if (pid == parent) continue;   // the idle process on Windows is its own parent
            _parent[pid] = parent;
            if (!_children.TryGetValue(parent, out var list)) _children[parent] = list = new List<int>();
            list.Add(pid);
        }
    }

    /// <summary>The system's processes now; empty when the table cannot be read.</summary>
    public static ProcessTable Snapshot() =>
        new(OperatingSystem.IsWindows() ? WindowsRows() : PosixRows());

    public bool IsAlive(int pid) => _alive.Contains(pid);

    /// <summary>The executable's name without extension, lower case ("codex"), or "" when unknown.</summary>
    public string NameOf(int pid) =>
        _names.TryGetValue(pid, out var n) ? Path.GetFileNameWithoutExtension(n).ToLowerInvariant() : "";

    /// <summary>The chain of parents above <paramref name="pid"/>, nearest first (stops at a loop or an unknown parent).</summary>
    public IReadOnlyList<int> Ancestors(int pid)
    {
        var chain = new List<int>();
        var seen = new HashSet<int> { pid };
        while (_parent.TryGetValue(pid, out var parent) && seen.Add(parent))
        {
            chain.Add(parent);
            pid = parent;
        }
        return chain;
    }

    /// <summary>Every process below <paramref name="root"/> (not including it), breadth first.</summary>
    public IReadOnlyList<int> Descendants(int root)
    {
        var found = new List<int>();
        var seen = new HashSet<int> { root };
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            if (!_children.TryGetValue(queue.Dequeue(), out var kids)) continue;
            foreach (var kid in kids)
            {
                if (!seen.Add(kid)) continue;
                found.Add(kid);
                queue.Enqueue(kid);
            }
        }
        return found;
    }

    private static List<(int, int, string)> WindowsRows()
    {
        var rows = new List<(int, int, string)>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return rows;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snapshot, ref entry) == 0) return rows;
            do rows.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? ""));
            while (Process32NextW(snapshot, ref entry) != 0);
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
        return rows;
    }

    /// <summary><c>ps</c> is on every macOS and Linux this ships for; /proc is not on macOS.</summary>
    private static List<(int, int, string)> PosixRows()
    {
        var rows = new List<(int, int, string)>();
        try
        {
            // comm last: on macOS it is the full path and may contain spaces.
            var start = new ProcessStartInfo("ps", "-A -o pid= -o ppid= -o comm=")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            using var ps = Process.Start(start);
            if (ps is null) return rows;
            var text = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(5_000);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var ppid))
                    rows.Add((pid, ppid, parts.Length == 3 ? Path.GetFileName(parts[2].Trim()) : ""));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        return rows;
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;

    // PROCESSENTRY32W (568 bytes on 64-bit): the exe name is read — agent detection looks for
    // "codex" below a terminal's shell.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CloseHandle(IntPtr handle);
}
