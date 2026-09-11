using System.IO;
namespace LocalDocket.App;

public static class Log
{
    static string? _path;
    static readonly object Gate = new();

    public static void Init(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                File.Move(path, path + ".1", true);
        }
        catch { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string where, Exception? ex) => Write("ERR ", $"{where}: {ex}");

    static void Write(string level, string message)
    {
        if (_path == null) return;
        lock (Gate)
        {
            try { File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}"); } catch { }
        }
    }
}
