using System.Text.Json.Nodes;

namespace LocalDocket.Core;

/// <summary>One thing that landed in the inbox: a file, or a whole dropped folder (filed atomically).</summary>
public sealed class FileItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public string Extension => IsDirectory ? "" : System.IO.Path.GetExtension(Name).ToLowerInvariant();
    public string Stem => IsDirectory ? Name : System.IO.Path.GetFileNameWithoutExtension(Name);
    public bool IsDirectory { get; init; }
    public long Size { get; set; }
    public DateTime Created { get; set; }
    public DateTime Modified { get; set; }
    /// <summary>When the item was noticed in the inbox (used for grouping).</summary>
    public DateTime DroppedAt { get; set; } = DateTime.Now;

    public string? ContentText { get; set; }
    public string? ContentKind { get; set; }
    public Dictionary<string, string> Meta { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Sha256 { get; set; }

    public static FileItem FromPath(string path)
    {
        var isDir = Directory.Exists(path);
        var item = new FileItem { Path = path, Name = System.IO.Path.GetFileName(path), IsDirectory = isDir };
        if (isDir)
        {
            var di = new DirectoryInfo(path);
            item.Created = di.CreationTime;
            item.Modified = di.LastWriteTime;
        }
        else
        {
            var fi = new FileInfo(path);
            item.Size = fi.Length;
            item.Created = fi.CreationTime;
            item.Modified = fi.LastWriteTime;
        }
        return item;
    }
}

/// <summary>Items that share one popup. Each item is still filed on its own classification.</summary>
public sealed class FileGroup
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public List<FileItem> Items { get; init; } = new();
    public string Reason { get; init; } = "";
    public DateTime DroppedAt => Items.Count == 0 ? DateTime.Now : Items.Min(i => i.DroppedAt);
}

public sealed class Classification
{
    public string Domain { get; set; } = "Work";
    public string Category { get; set; } = "";
    public string? Environment { get; set; }
    public string? Client { get; set; }
    public string? Ticket { get; set; }
    public string? Project { get; set; }
    public double Confidence { get; set; }
    public string Reasoning { get; set; } = "";
    /// <summary>One-line description of an image, from the vision model.</summary>
    public string? ImageDescription { get; set; }
    /// <summary>Question keys from taxonomy.questions that still need an answer.</summary>
    public List<string> Questions { get; set; } = new();
    /// <summary>"rule:name", "llm:model", "hint:category", "user".</summary>
    public string DecidedBy { get; set; } = "";
    /// <summary>"file" or "skip".</summary>
    public string Action { get; set; } = "file";

    public bool NeedsUser(double autoThreshold) => Questions.Count > 0 || Confidence < autoThreshold || string.IsNullOrEmpty(Category);

    public Classification Clone()
    {
        var c = (Classification)MemberwiseClone();
        c.Questions = new List<string>(Questions);
        return c;
    }
}

public sealed class Decision
{
    public required FileItem Item { get; init; }
    public required Classification Result { get; set; }
    public string TargetDirectory { get; set; } = "";
    public string TargetPath => string.IsNullOrEmpty(TargetDirectory) ? "" : System.IO.Path.Combine(TargetDirectory, Item.Name);
}

/// <summary>Content extraction is implemented in LocalDocket.Extract; Core only knows the contract.</summary>
public interface IContentExtractor
{
    /// <param name="capBytes">Override for <see cref="DocketSettings.ContentCapBytesPerFile"/> (the indexer reads far more than the classifier).</param>
    /// <param name="attachImage">Prepare the downscaled image for vision models (classification only).</param>
    Task ExtractAsync(FileItem item, DocketSettings settings, CancellationToken ct = default, int? capBytes = null, bool attachImage = true);
}

/// <summary>Text → vector, implemented in LocalDocket.Llm. Core uses it for few-shot retrieval, the indexer and chat.</summary>
public interface IEmbedder
{
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);
    /// <summary>One vector per input, in order. Throws when the model is unreachable.</summary>
    Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>LLM classification is implemented in LocalDocket.Llm; Core only knows the contract.</summary>
public interface IClassifierBackend : IEmbedder
{
    Task<Classification?> ClassifyAsync(FileItem item, Taxonomy taxonomy, Classification? hint, IReadOnlyList<PastDecision> examples, CancellationToken ct = default);
    /// <summary>Base64 JPEG the extractor prepared for vision models; never persisted.</summary>
    const string ImageMetaKey = "_imageB64";
    string ModelName { get; }
}

public sealed record PastDecision(string Name, string Category, string? Environment, string? Client, string Snippet, double Similarity);

public sealed record ChatMessage(string Role, string Content);

/// <summary>Free-form chat, implemented in LocalDocket.Llm. Core's ChatService builds the prompts; the backend only transports them.</summary>
public interface IChatBackend
{
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, int? numCtx = null, CancellationToken ct = default);
    /// <summary>Single non-streamed answer constrained to a JSON schema (used to pull filters out of a question).</summary>
    Task<string> CompleteJsonAsync(string system, string user, JsonNode schema, CancellationToken ct = default);
    string ModelName { get; }
}
