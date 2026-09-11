using System.Text.RegularExpressions;

namespace LocalDocket.Core;

/// <summary>Cheap credential detector for chunk text. A hit withholds the chunk from the index; the file row itself stays.</summary>
public static class SecretScan
{
    static readonly Regex[] Patterns =
    {
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled),
        new(@"(?im)^\s*(password|passwd|pwd|secret|client_secret|api[_-]?key|access[_-]?key|auth[_-]?token|private[_-]?key)\s*[=:]\s*\S{6,}", RegexOptions.Compiled),
        new(@"(?i)(password|pwd)\s*=\s*[^;\s]{4,}\s*;", RegexOptions.Compiled),                 // connection strings
        new(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled),                                     // AWS access key id
        new(@"\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}\b", RegexOptions.Compiled),               // GitHub tokens
        new(@"\bxox[abpr]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled),                          // Slack tokens
        new(@"\bsk-[A-Za-z0-9]{20,}\b", RegexOptions.Compiled),                                  // OpenAI-style keys
        new(@"\beyJ[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled), // JWT
        new(@"(?i)\bSharedAccessKey\s*=\s*\S{20,}", RegexOptions.Compiled),                      // Azure connection strings
        new(@"(?i)\bAccountKey\s*=\s*[A-Za-z0-9+/=]{40,}", RegexOptions.Compiled),
    };

    public static bool IsSensitive(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var rx in Patterns) if (rx.IsMatch(text)) return true;
        return false;
    }
}
