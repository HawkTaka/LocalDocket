using System.Text.Json.Nodes;
using LocalDocket.Core;

namespace LocalDocket.Llm;

/// <summary>Free-form and JSON-schema chat over Ollama for the chat window. Uses <c>chatModel</c>, falling back to the classifier model.</summary>
public sealed class OllamaChat : IChatBackend
{
    readonly OllamaClient _client;
    readonly DocketSettings _s;

    public OllamaChat(OllamaClient client, DocketSettings settings) { _client = client; _s = settings; }

    public string ModelName => string.IsNullOrWhiteSpace(_s.ChatModel) ? _s.Model : _s.ChatModel!;

    public IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, int? numCtx = null, CancellationToken ct = default) =>
        _client.ChatStreamAsync(ModelName, messages, _s.KeepAlive, think: false, numCtx ?? _s.ChatNumCtx, temperature: 0.3, ct: ct);

    public async Task<string> CompleteJsonAsync(string system, string user, JsonNode schema, CancellationToken ct = default)
    {
        var (content, _) = await _client.ChatJsonAsync(ModelName, system, user, schema, think: false, _s.KeepAlive, ct);
        return content;
    }
}
