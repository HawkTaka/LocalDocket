using System.Numerics;

namespace LocalDocket.Core;

/// <summary>
/// In-memory cosine index over chunk embeddings. Vectors only; every other fact about a chunk lives in SQLite, so moves
/// and re-classification never need to touch it. Unit-normalised on insert, so similarity is a dot product.
/// Sized for tens of thousands of 768-float vectors (about 3 KB each); a brute-force scan is a few milliseconds.
/// </summary>
public sealed class VectorIndex
{
    public readonly record struct Hit(string FileId, int Ordinal, double Similarity);

    readonly object _lock = new();
    float[] _vecs = Array.Empty<float>();
    string?[] _fileIds = Array.Empty<string?>();
    int[] _ordinals = Array.Empty<int>();
    int _slots;                                   // slots in use (including tombstones)
    readonly Stack<int> _free = new();
    readonly Dictionary<string, List<int>> _byFile = new(StringComparer.Ordinal);

    public int Dim { get; private set; }
    public int Count { get { lock (_lock) return _byFile.Values.Sum(l => l.Count); } }
    public int Files { get { lock (_lock) return _byFile.Count; } }
    /// <summary>False until the startup load has finished (queries before that see a partial index).</summary>
    public bool Ready { get; set; }

    public void Add(string fileId, int ordinal, float[] vector)
    {
        lock (_lock) AddUnlocked(fileId, ordinal, vector);
    }

    /// <summary>Drop every vector for the file and insert the given ones (ordinal = position in the list).</summary>
    public void ReplaceFile(string fileId, IReadOnlyList<float[]> vectors)
    {
        lock (_lock)
        {
            RemoveUnlocked(fileId);
            for (int i = 0; i < vectors.Count; i++) AddUnlocked(fileId, i, vectors[i]);
        }
    }

    public void RemoveFile(string fileId)
    {
        lock (_lock) RemoveUnlocked(fileId);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _vecs = Array.Empty<float>(); _fileIds = Array.Empty<string?>(); _ordinals = Array.Empty<int>();
            _slots = 0; _free.Clear(); _byFile.Clear(); Dim = 0;
        }
    }

    /// <summary>Best k chunks by cosine similarity, optionally restricted to a set of file ids.</summary>
    public List<Hit> TopK(float[] query, int k = 24, ISet<string>? allowedFileIds = null, double minSimilarity = 0.3)
    {
        if (k <= 0) return new();
        lock (_lock)
        {
            if (_slots == 0 || query.Length != Dim) return new();
            var q = Normalise(query);
            var heap = new PriorityQueue<Hit, double>(k + 1);
            var vecs = _vecs.AsSpan();
            for (int s = 0; s < _slots; s++)
            {
                var id = _fileIds[s];
                if (id == null) continue;
                if (allowedFileIds != null && !allowedFileIds.Contains(id)) continue;
                var sim = Dot(q, vecs.Slice(s * Dim, Dim));
                if (sim < minSimilarity) continue;
                heap.Enqueue(new Hit(id, _ordinals[s], sim), sim);
                if (heap.Count > k) heap.Dequeue();
            }
            var list = new List<Hit>(heap.Count);
            while (heap.Count > 0) list.Add(heap.Dequeue());
            list.Reverse();
            return list;
        }
    }

    /// <summary>Populate from the store's chunk table. Safe to call on a live index (adds are idempotent per file/ordinal via ReplaceFile semantics elsewhere).</summary>
    public static VectorIndex Load(Store store)
    {
        var ix = new VectorIndex();
        store.LoadChunkVectors((fileId, ordinal, vec) => ix.Add(fileId, ordinal, vec));
        ix.Ready = true;
        return ix;
    }

    void AddUnlocked(string fileId, int ordinal, float[] vector)
    {
        if (vector.Length == 0) return;
        if (Dim == 0) Dim = vector.Length;
        if (vector.Length != Dim) throw new ArgumentException($"vector has {vector.Length} dims, index has {Dim}");
        int slot;
        if (_free.Count > 0) slot = _free.Pop();
        else
        {
            if ((_slots + 1) * Dim > _vecs.Length) Grow();
            slot = _slots++;
        }
        var n = Normalise(vector);
        n.CopyTo(_vecs.AsSpan(slot * Dim, Dim));
        _fileIds[slot] = fileId;
        _ordinals[slot] = ordinal;
        if (!_byFile.TryGetValue(fileId, out var slots)) _byFile[fileId] = slots = new List<int>();
        slots.Add(slot);
    }

    void RemoveUnlocked(string fileId)
    {
        if (!_byFile.Remove(fileId, out var slots)) return;
        foreach (var s in slots)
        {
            _fileIds[s] = null;
            _vecs.AsSpan(s * Dim, Dim).Clear();
            _free.Push(s);
        }
    }

    void Grow()
    {
        var cap = Math.Max(1024, (_vecs.Length / Math.Max(Dim, 1)) * 3 / 2);
        Array.Resize(ref _vecs, cap * Dim);
        Array.Resize(ref _fileIds, cap);
        Array.Resize(ref _ordinals, cap);
    }

    static float[] Normalise(float[] v)
    {
        double n = 0;
        foreach (var x in v) n += x * x;
        if (n == 0) return (float[])v.Clone();
        var inv = (float)(1 / Math.Sqrt(n));
        var r = new float[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = v[i] * inv;
        return r;
    }

    static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int w = Vector<float>.Count, i = 0;
        var acc = Vector<float>.Zero;
        for (; i + w <= a.Length; i += w) acc += new Vector<float>(a.Slice(i, w)) * new Vector<float>(b.Slice(i, w));
        double sum = Vector.Dot(acc, Vector<float>.One);
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}
