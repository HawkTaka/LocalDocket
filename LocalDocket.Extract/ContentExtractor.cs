using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using LocalDocket.Core;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace LocalDocket.Extract;

/// <summary>Fills FileItem.ContentText / ContentKind / Meta from the file itself. Everything is capped and best-effort.</summary>
public sealed class ContentExtractor : IContentExtractor
{
    static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".sql", ".json", ".csv", ".yaml", ".yml", ".xml", ".py", ".cs", ".razor", ".html", ".htm", ".log",
        ".ps1", ".sh", ".bat", ".cmd", ".ini", ".cfg", ".config", ".ts", ".js", ".css", ".canvas", ".base", ".patch", ".diff", ".opex", ".jsonl", ".env"
    };
    static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff" };
    static readonly HashSet<string> ZipExts = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".nupkg" };

    public async Task ExtractAsync(FileItem item, DocketSettings settings, CancellationToken ct = default, int? capBytes = null, bool attachImage = true)
    {
        var cap = capBytes ?? settings.ContentCapBytesPerFile;
        if (item.IsDirectory) { ExtractDirectory(item, settings); return; }
        var ext = item.Extension;
        try
        {
            if (TextExts.Contains(ext)) { item.ContentText = await ReadHeadAsync(item.Path, cap, ct); item.ContentKind = "text"; }
            else if (ext == ".pdf") { item.ContentText = OfficeExtractors.Pdf(item.Path, cap, item.Meta); item.ContentKind = "pdf"; }
            else if (ext == ".docx") { item.ContentText = OfficeExtractors.Docx(item.Path, cap, item.Meta); item.ContentKind = "docx"; }
            else if (ext == ".xlsx" || ext == ".xlsm") { item.ContentText = OfficeExtractors.Xlsx(item.Path, cap, item.Meta); item.ContentKind = "xlsx"; }
            else if (ext == ".pptx") { item.ContentText = OfficeExtractors.Pptx(item.Path, cap, item.Meta); item.ContentKind = "pptx"; }
            else if (ZipExts.Contains(ext)) { item.ContentText = ZipListing(item.Path, cap); item.ContentKind = "zip-listing"; }
            else if (ext == ".bak" || ext == ".trn") { item.ContentText = BakHeader(item.Path, item.Meta); item.ContentKind = "bak-header"; }
            else if (ImageExts.Contains(ext))
            {
                item.ContentText = ImageInfo(item.Path, item.Meta); item.ContentKind = "image-meta";
                if (attachImage && settings.ImageVision) { var b64 = DownscaleToJpegBase64(item.Path, settings.ImageMaxEdge); if (b64 != null) item.Meta[IClassifierBackend.ImageMetaKey] = b64; }
            }
            else if (ext == ".lnk" || ext == ".url") { item.ContentKind = "shortcut"; }
            else if (ext == ".mp4" || ext == ".mkv" || ext == ".mov") { item.ContentKind = "video"; }
            else if (LooksLikeText(item.Path)) { item.ContentText = await ReadHeadAsync(item.Path, cap, ct); item.ContentKind = "text?"; }
            else item.ContentKind = "binary";
        }
        catch (Exception ex)
        {
            item.ContentKind ??= "error";
            item.Meta["extract.error"] = ex.Message;
        }
        if (item.ContentText != null)
        {
            item.ContentText = Regex.Replace(item.ContentText, @"[ \t]+", " ");
            item.ContentText = Regex.Replace(item.ContentText, @"(\r?\n){3,}", "\n\n").Trim();
        }
    }

    static void ExtractDirectory(FileItem item, DocketSettings settings)
    {
        var sb = new StringBuilder();
        int files = 0, dirs = 0; long bytes = 0;
        var exts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var markers = new List<string>();
        try
        {
            foreach (var e in System.IO.Directory.EnumerateFileSystemEntries(item.Path, "*", SearchOption.AllDirectories))
            {
                if (System.IO.Directory.Exists(e)) { dirs++; if (Path.GetFileName(e) == ".git") markers.Add(".git"); continue; }
                files++;
                var fi = new FileInfo(e); bytes += fi.Length;
                var ext = fi.Extension.ToLowerInvariant();
                exts[ext] = exts.GetValueOrDefault(ext) + 1;
                foreach (var m in settings.AtomicMarkers)
                    if (!m.StartsWith('.') || m.Contains('*')) { if (PathTemplate.GlobToRegex(m).IsMatch(fi.Name) && !markers.Contains(m)) markers.Add(m); }
                if (files > 20000) break;
            }
        }
        catch { }
        item.Size = bytes;
        item.Meta["dir.files"] = files.ToString();
        item.Meta["dir.dirs"] = dirs.ToString();
        if (markers.Count > 0) item.Meta["dir.markers"] = string.Join(",", markers);
        sb.AppendLine($"Folder with {files} files, {dirs} subfolders, {bytes / 1024 / 1024} MB.");
        if (markers.Count > 0) sb.AppendLine($"Project markers: {string.Join(", ", markers)} (atomic: file as one unit).");
        sb.AppendLine("Extensions: " + string.Join(", ", exts.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{(kv.Key == "" ? "(none)" : kv.Key)}={kv.Value}")));
        sb.AppendLine("Top entries: " + string.Join(", ", System.IO.Directory.EnumerateFileSystemEntries(item.Path).Take(25).Select(Path.GetFileName)));
        var readme = System.IO.Directory.EnumerateFiles(item.Path).FirstOrDefault(f => Path.GetFileName(f).StartsWith("README", StringComparison.OrdinalIgnoreCase));
        if (readme != null)
        {
            try { sb.AppendLine("README: " + ReadHeadAsync(readme, 1000, CancellationToken.None).GetAwaiter().GetResult()); } catch { }
        }
        item.ContentText = sb.ToString();
        item.ContentKind = "folder";
    }

    public static async Task<string> ReadHeadAsync(string path, int cap, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[Math.Min(cap, fs.Length)];
        var n = await fs.ReadAsync(buf, ct);
        var text = DecodeText(buf.AsSpan(0, n));
        return text;
    }

    static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes[3..]);
        // UTF-16LE without BOM: every other byte zero
        int zeros = 0; for (int i = 1; i < Math.Min(bytes.Length, 200); i += 2) if (bytes[i] == 0) zeros++;
        if (bytes.Length > 20 && zeros > Math.Min(bytes.Length, 200) / 4) return Encoding.Unicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    static bool LooksLikeText(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[512]; var n = fs.Read(buf, 0, buf.Length);
            if (n == 0) return false;
            int control = 0;
            for (int i = 0; i < n; i++) if (buf[i] == 0 || (buf[i] < 7) || (buf[i] > 13 && buf[i] < 32)) control++;
            return control < n / 20;
        }
        catch { return false; }
    }

    static string ZipListing(string path, int cap)
    {
        using var zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        sb.AppendLine($"Zip with {zip.Entries.Count} entries:");
        var exts = zip.Entries.Where(e => !e.FullName.EndsWith('/')).GroupBy(e => Path.GetExtension(e.Name).ToLowerInvariant())
            .OrderByDescending(g => g.Count()).Take(8).Select(g => $"{(g.Key == "" ? "(none)" : g.Key)}={g.Count()}");
        sb.AppendLine("Extensions: " + string.Join(", ", exts));
        foreach (var e in zip.Entries.Take(60))
        {
            sb.AppendLine(e.FullName);
            if (sb.Length > cap) break;
        }
        return sb.ToString();
    }

    /// <summary>Pull the UTF-16 strings out of the MTF header: backup name, server, database, logical files.</summary>
    public static string BakHeader(string path, Dictionary<string, string> meta)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[Math.Min(65536, fs.Length)];
        var n = fs.Read(buf, 0, buf.Length);
        var strings = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i + 1 < n; i += 2)
        {
            var c = (char)(buf[i] | (buf[i + 1] << 8));
            if (c >= 0x20 && c < 0x7f) sb.Append(c);
            else { if (sb.Length >= 6) strings.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length >= 6) strings.Add(sb.ToString());
        // Also try odd alignment.
        sb.Clear();
        for (int i = 1; i + 1 < n; i += 2)
        {
            var c = (char)(buf[i] | (buf[i + 1] << 8));
            if (c >= 0x20 && c < 0x7f) sb.Append(c);
            else { if (sb.Length >= 6) strings.Add(sb.ToString()); sb.Clear(); }
        }
        var distinct = strings.Distinct().ToList();
        var backupName = distinct.FirstOrDefault(s => s.Contains("Database Backup", StringComparison.OrdinalIgnoreCase) || s.Contains("-Full", StringComparison.OrdinalIgnoreCase));
        if (backupName != null)
        {
            meta["bak.backupName"] = backupName;
            var dbm = Regex.Match(backupName, @"^(.+?)-(Full|Differential|Transaction)", RegexOptions.IgnoreCase);
            if (dbm.Success) meta["bak.database"] = dbm.Groups[1].Value;
        }
        var server = distinct.Select(s => Regex.Match(s, @"(WIN-[A-Z0-9]{8,}|[A-Z][A-Z0-9\-]{4,}(?=\\|$))")).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        if (server == null && meta.TryGetValue("bak.database", out var db))
        {
            var withDb = distinct.FirstOrDefault(s => s.StartsWith(db) && s.Length > db.Length + 3);
            if (withDb != null) server = withDb[db.Length..];
        }
        if (server != null) meta["bak.server"] = server;
        var logical = distinct.Where(s => Regex.IsMatch(s, @"^E?[A-Za-z][A-Za-z0-9_]+(_log)?(C:\\|$)")).Select(s => Regex.Replace(s, @"C:\\.*$", "").TrimStart('E')).Where(s => s.Length > 3).Distinct().Take(6).ToList();
        if (logical.Count > 0) meta["bak.logicalNames"] = string.Join(",", logical);
        var mdf = distinct.FirstOrDefault(s => s.EndsWith(".mdf", StringComparison.OrdinalIgnoreCase));
        if (mdf != null) meta["bak.mdf"] = Path.GetFileName(mdf);
        return "SQL backup header strings:\n" + string.Join("\n", distinct.Take(30));
    }

    /// <summary>JPEG (quality 80) no larger than maxEdge on its long side, as base64, for the vision model. GDI+ formats only.</summary>
    public static string? DownscaleToJpegBase64(string path, int maxEdge)
    {
        try
        {
#pragma warning disable CA1416
            using var src = System.Drawing.Image.FromFile(path);
            var scale = Math.Min(1.0, (double)maxEdge / Math.Max(src.Width, src.Height));
            var w = Math.Max(1, (int)(src.Width * scale)); var h = Math.Max(1, (int)(src.Height * scale));
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.White);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            var prm = new System.Drawing.Imaging.EncoderParameters(1);
            prm.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
            using var ms = new MemoryStream();
            bmp.Save(ms, codec, prm);
            return Convert.ToBase64String(ms.ToArray());
#pragma warning restore CA1416
        }
        catch { return null; }
    }

    static string ImageInfo(string path, Dictionary<string, string> meta)
    {
        var sb = new StringBuilder();
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(path);
            foreach (var d in dirs)
            {
                if (d is ExifIfd0Directory || d is ExifSubIfdDirectory)
                {
                    foreach (var t in d.Tags)
                    {
                        if (t.Name is "Make" or "Model" or "Date/Time Original" or "Software" or "Image Width" or "Image Height" or "Exif Image Width" or "Exif Image Height")
                        {
                            meta["img." + t.Name.Replace(" ", "").Replace("/", "")] = t.Description ?? "";
                            sb.AppendLine($"{t.Name}: {t.Description}");
                        }
                    }
                }
                else if (d.Name is "PNG-IHDR" or "JPEG" or "GIF Header")
                {
                    foreach (var t in d.Tags.Where(t => t.Name.Contains("Width") || t.Name.Contains("Height")))
                        sb.AppendLine($"{t.Name}: {t.Description}");
                }
            }
        }
        catch { }
        var hasCamera = meta.ContainsKey("img.Make") || meta.ContainsKey("img.Model");
        meta["img.kind"] = hasCamera ? "photo" : "screenshot-or-graphic";
        sb.AppendLine(hasCamera ? "Camera photo (EXIF camera present)." : "No camera EXIF: likely a screenshot, export or graphic.");
        return sb.ToString();
    }
}
