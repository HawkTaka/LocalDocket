using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LocalDocket.Core;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace LocalDocket.App;

/// <summary>Tray icon + context menu. Also the bridge between the background host and the WPF windows.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly DocketHost _host;
    readonly WF.NotifyIcon _icon;
    readonly Dispatcher _ui = Application.Current.Dispatcher;
    readonly WF.ToolStripMenuItem _recent;
    readonly WF.ToolStripMenuItem _pause;
    readonly WF.ToolStripMenuItem _status;
    readonly List<ToastWindow> _toasts = new();
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>Where "Start with Windows" installs a copy, so login never depends on a bin\Debug folder that a rebuild may clean.</summary>
    static readonly string InstallDir = Path.Combine(DocketPaths.DataDir, "app");

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr handle);

    public TrayIcon(DocketHost host)
    {
        _host = host;
        var menu = new WF.ContextMenuStrip();
        _status = new WF.ToolStripMenuItem("Local Docket — idle") { Enabled = false };
        menu.Items.Add(_status);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Open _Inbox folder", null, (_, _) => Open(_host.Taxonomy.InboxPath));
        menu.Items.Add("Process inbox now", null, (_, _) => _host.ProcessNow());
        menu.Items.Add("Scan a folder…", null, (_, _) => _ui.Invoke(() => new ScanWindow(_host).Show()));
        menu.Items.Add("Search filed items…", null, (_, _) => _ui.Invoke(() => new SearchWindow(_host).Show()));
        menu.Items.Add("Chat with your files…", null, (_, _) => _ui.Invoke(() => new ChatWindow(_host).Show()));
        menu.Items.Add("Indexing…", null, (_, _) => _ui.Invoke(() => new IndexWindow(_host).Show()));
        _recent = new WF.ToolStripMenuItem("Recent moves");
        _recent.DropDownOpening += (_, _) => FillRecent();
        _recent.DropDownItems.Add("(none yet)");
        menu.Items.Add(_recent);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Edit taxonomy.yaml", null, (_, _) => Open(_host.Taxonomy.SourcePath));
        menu.Items.Add("Reload taxonomy", null, (_, _) => { try { _host.ReloadTaxonomy(); Balloon("Taxonomy reloaded", _host.Taxonomy.SourcePath); } catch (Exception ex) { Balloon("Taxonomy error", ex.Message, WF.ToolTipIcon.Error); } });
        menu.Items.Add("Open log", null, (_, _) => Open(DocketPaths.LogPath));
        var startup = new WF.ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = IsStartup() };
        startup.Click += (_, _) =>
        {
            try
            {
                SetStartup(startup.Checked);
                if (startup.Checked) Balloon("Start with Windows", "Local Docket will start from " + RegisteredPath());
            }
            catch (Exception ex) { Log.Error("startup", ex); Balloon("Start with Windows failed", ex.Message, WF.ToolTipIcon.Error); startup.Checked = IsStartup(); }
        };
        menu.Items.Add(startup);
        if (IsBuildOutput(Environment.ProcessPath))
        {
            var update = new WF.ToolStripMenuItem("Update installed copy") { ToolTipText = "Copy this build to " + InstallDir + " (what Start with Windows launches)" };
            update.Click += (_, _) =>
            {
                try { var exe = Install(); if (IsStartup()) Register(exe); Balloon("Installed copy updated", exe); }
                catch (Exception ex) { Log.Error("install", ex); Balloon("Update failed", ex.Message, WF.ToolTipIcon.Error); }
            };
            menu.Items.Add(update);
        }
        _pause = new WF.ToolStripMenuItem("Pause watching") { CheckOnClick = true };
        _pause.Click += (_, _) => { _host.Paused = _pause.Checked; UpdateIcon(); };
        menu.Items.Add(_pause);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Current.Shutdown());

        _icon = new WF.NotifyIcon { Icon = MakeIcon(false), Text = Trunc("Local Docket — watching " + _host.Taxonomy.InboxPath, 63), ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => Open(_host.Taxonomy.InboxPath);
        _host.Notify = (title, text, warn) => { try { _ui.BeginInvoke(() => Balloon(title, text, warn ? WF.ToolTipIcon.Warning : WF.ToolTipIcon.Info)); } catch { } };
        if (_host.StartupWarning != null) Balloon("Local Docket", _host.StartupWarning, WF.ToolTipIcon.Warning);
        CheckStartupEntry();

        _host.Status = s => { try { _ui.BeginInvoke(() => { _inboxStatus = s; ShowStatus(); }); } catch { } };
        _host.IndexStatus = s => { try { _ui.BeginInvoke(() => { _indexStatus = s; ShowStatus(); }); } catch { } };
        _host.Filed = (d, r) => _ui.BeginInvoke(() => ShowToast(d, r));
        _host.AskUser = (g, decisions) =>
        {
            var tcs = new TaskCompletionSource();
            _ui.BeginInvoke(() =>
            {
                try
                {
                    var w = new PopupWindow(_host, g, decisions);
                    w.Closed += (_, _) => tcs.TrySetResult();
                    w.Show();
                    w.Activate();
                }
                catch (Exception ex) { Log.Error("popup", ex); tcs.TrySetResult(); }
            });
            return tcs.Task;
        };
    }

    string _inboxStatus = "idle";
    IndexStatus? _indexStatus;

    /// <summary>Inbox work owns the status line; while it is idle the indexer's progress shows instead.</summary>
    void ShowStatus()
    {
        var s = _inboxStatus;
        if (s == "idle" && _indexStatus is { } ix && ix.Phase != "idle")
            s = ix.Phase == "indexing" ? $"indexing {ix.Done + 1}/{ix.Total} {ix.Current}" : ix.Phase == "scanning" ? "checking index" : ix.Phase;
        _status.Text = "Local Docket — " + s;
        _icon.Text = Trunc("Local Docket — " + s, 63);
    }

    void ShowToast(Decision d, MoveResult r)
    {
        var t = new ToastWindow(_host, d, r);
        _toasts.RemoveAll(x => !x.IsVisible);
        t.Slot = _toasts.Count;
        _toasts.Add(t);
        t.Closed += (_, _) => _toasts.Remove(t);
        t.Show();
    }

    void FillRecent()
    {
        _recent.DropDownItems.Clear();
        var moves = _host.Store.RecentMoves(12);
        if (moves.Count == 0) { _recent.DropDownItems.Add("(none yet)"); return; }
        foreach (var m in moves)
        {
            var name = Path.GetFileName(m.ToPath);
            var label = m.Undone ? $"↩ {name} (undone)" : $"{m.MovedAt:HH:mm} {name} → …\\{Path.GetFileName(Path.GetDirectoryName(m.ToPath))}";
            var mi = new WF.ToolStripMenuItem(Trunc(label, 70)) { Enabled = !m.Undone, ToolTipText = m.ToPath };
            var id = m.Id;
            mi.Click += async (_, _) =>
            {
                try { var back = await Task.Run(() => _host.Undo(id)); Balloon("Undone", "Back in the inbox: " + Path.GetFileName(back)); }
                catch (Exception ex) { Balloon("Undo failed", ex.Message, WF.ToolTipIcon.Error); }
            };
            _recent.DropDownItems.Add(mi);
        }
    }

    public void Balloon(string title, string text, WF.ToolTipIcon icon = WF.ToolTipIcon.Info)
    {
        _icon.BalloonTipTitle = title; _icon.BalloonTipText = text; _icon.BalloonTipIcon = icon;
        _icon.ShowBalloonTip(5000);
    }

    void UpdateIcon()
    {
        var old = _icon.Icon;
        _icon.Icon = MakeIcon(_host.Paused);
        old?.Dispose();
    }

    /// <summary>A registered path that no longer exists (cleaned build folder) means login silently starts nothing; say so. A newer build refreshes the installed copy.</summary>
    void CheckStartupEntry()
    {
        try
        {
            if (!IsStartup()) return;
            var registered = RegisteredPath();
            bool legacyEntry;
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) legacyEntry = k?.GetValue(RunValue) == null && k?.GetValue(LegacyRunValue) != null;
            if (legacyEntry || (registered != null && !File.Exists(registered)))
            {
                var exe = IsBuildOutput(Environment.ProcessPath) ? Install() : Environment.ProcessPath!;
                Register(exe);
                Balloon("Start with Windows updated", "Now starting from " + exe, WF.ToolTipIcon.Info);
                return;
            }
            if (IsBuildOutput(Environment.ProcessPath) && registered != null && registered.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase)
                && File.GetLastWriteTimeUtc(Environment.ProcessPath!) > File.GetLastWriteTimeUtc(registered).AddSeconds(5))
            {
                _ = Task.Run(() => { try { Install(); Log.Info("installed copy refreshed from this build"); } catch (Exception ex) { Log.Error("install", ex); } });
            }
        }
        catch (Exception ex) { Log.Error("startup check", ex); }
    }

    static bool IsBuildOutput(string? path) => path != null && path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);

    const string RunValue = "LocalDocket";
    const string LegacyRunValue = "Filer";

    static string? RegisteredPath()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        var v = (k?.GetValue(RunValue) ?? k?.GetValue(LegacyRunValue)) as string;
        return v?.Trim().Trim('"');
    }

    /// <summary>Copy this build's output folder to the install folder. Files that are locked (an installed instance running) are skipped, not fatal.</summary>
    static string Install()
    {
        var src = AppContext.BaseDirectory.TrimEnd('\\');
        Directory.CreateDirectory(InstallDir);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            if (rel.StartsWith("taxonomy.yaml", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(InstallDir, rel))) continue; // never clobber a user copy
            var dest = Path.Combine(InstallDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try { File.Copy(f, dest, true); } catch (IOException) { /* in use by the installed instance */ }
        }
        return Path.Combine(InstallDir, "LocalDocket.exe");
    }

    static void Register(string exe)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        k.SetValue(RunValue, $"\"{exe}\"");
        k.DeleteValue(LegacyRunValue, false); // the pre-rename entry
    }

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { Log.Error("open " + path, ex); }
    }

    static bool IsStartup()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) != null || k?.GetValue(LegacyRunValue) != null;
    }

    static void SetStartup(bool on)
    {
        if (on)
        {
            // A dev build is copied to a stable folder first; an installed copy registers itself.
            var exe = IsBuildOutput(Environment.ProcessPath) ? Install() : Environment.ProcessPath!;
            Register(exe);
            return;
        }
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        k.DeleteValue(RunValue, false);
        k.DeleteValue(LegacyRunValue, false);
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>A folder glyph drawn at runtime, so no .ico asset is needed. Grey when paused.</summary>
    static Icon MakeIcon(bool paused)
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        var body = paused ? Color.FromArgb(150, 150, 150) : Color.FromArgb(37, 99, 235);
        var tab = paused ? Color.FromArgb(120, 120, 120) : Color.FromArgb(29, 78, 216);
        using var pb = new SolidBrush(body);
        using var pt = new SolidBrush(tab);
        g.FillRectangle(pt, new Rectangle(3, 6, 12, 6));
        using (var path = new GraphicsPath())
        {
            path.AddArc(3, 9, 6, 6, 180, 90); path.AddArc(23, 9, 6, 6, 270, 90);
            path.AddArc(23, 22, 6, 6, 0, 90); path.AddArc(3, 22, 6, 6, 90, 90);
            path.CloseFigure();
            g.FillPath(pb, path);
        }
        using var pen = new Pen(Color.White, 2.2f);
        g.DrawLine(pen, 10, 14, 22, 14);
        g.DrawLine(pen, 10, 18, 19, 18);
        g.DrawLine(pen, 10, 22, 16, 22);
        var h = bmp.GetHicon();
        try { using var tmp = Icon.FromHandle(h); return (Icon)tmp.Clone(); }
        finally { DestroyIcon(h); } // Icon.FromHandle does not own the GDI handle
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
