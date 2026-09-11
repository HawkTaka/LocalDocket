namespace LocalDocket.Core;

/// <summary>Splits extracted text into overlapping pieces for embedding. Pure; prefers paragraph, then line, then sentence boundaries.</summary>
public static class Chunker
{
    public static List<string> Split(string? text, int size = 3200, int overlap = 480)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;
        if (size < 200) size = 200;
        if (overlap < 0) overlap = 0;
        if (overlap > size / 2) overlap = size / 2;
        var t = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (t.Length <= size) { chunks.Add(t); return chunks; }

        int start = 0;
        while (start < t.Length)
        {
            int end = Math.Min(start + size, t.Length);
            if (end < t.Length)
            {
                int floor = start + size - size / 5; // look for a boundary in the last 20%
                int cut = LastIndexOf(t, "\n\n", floor, end);
                if (cut < 0) cut = LastIndexOf(t, "\n", floor, end);
                if (cut < 0) cut = LastSentenceEnd(t, floor, end);
                if (cut > start) end = cut;
            }
            var piece = t[start..end].Trim();
            if (piece.Length > 0)
            {
                // A short tail is folded into the previous chunk rather than embedded alone.
                if (end >= t.Length && chunks.Count > 0 && piece.Length < size / 4)
                    chunks[^1] = chunks[^1] + "\n" + piece;
                else chunks.Add(piece);
            }
            if (end >= t.Length) break;
            int next = end - overlap;
            if (next <= start) next = end;
            // Snap forward to whitespace so a chunk never starts mid-word.
            while (next < t.Length && next > start && !char.IsWhiteSpace(t[next - 1]) && next < end) next++;
            start = next;
        }
        return chunks;
    }

    static int LastIndexOf(string t, string needle, int floor, int end)
    {
        var i = t.LastIndexOf(needle, end - 1, end - floor, StringComparison.Ordinal);
        return i < 0 ? -1 : i + needle.Length;
    }

    static int LastSentenceEnd(string t, int floor, int end)
    {
        for (int i = end - 1; i > floor; i--)
            if ((t[i] == ' ' || t[i] == '\n') && i > 0 && (t[i - 1] == '.' || t[i - 1] == '?' || t[i - 1] == '!'))
                return i + 1;
        return -1;
    }
}
