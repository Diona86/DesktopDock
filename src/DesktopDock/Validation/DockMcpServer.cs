using System.Text;
using System.Text.Json;

namespace DesktopDock;

internal sealed record DockMcpTool(string Name, string Description, string InputSchema);

internal static class DockMcpServer
{
    private const string ProtocolVersion = "2025-11-25";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new { });

    private enum ConnectionState { New, AwaitingInitialized, Ready }

    internal static async Task RunAsync(Stream input, Stream output, IReadOnlyList<DockMcpTool> tools,
        Func<string, JsonElement, Task<object>> invoke)
    {
        var publishedTools = tools.Select(tool => new
        {
            name = tool.Name,
            description = tool.Description,
            inputSchema = JsonSerializer.Deserialize<JsonElement>(tool.InputSchema)
        }).ToArray();
        var toolNames = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var state = ConnectionState.New;

        using var frame = new MemoryStream();
        await using var writer = new StreamWriter(output, Utf8, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        byte[] buffer = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            int start = 0;
            int newline;
            while ((newline = Array.IndexOf(buffer, (byte)'\n', start, count - start)) >= 0)
            {
                frame.Write(buffer, start, newline - start);
                await ReadFrame().ConfigureAwait(false);
                frame.SetLength(0);
                start = newline + 1;
            }
            frame.Write(buffer, start, count - start);
        }

        if (frame.Length != 0)
            await ReadFrame().ConfigureAwait(false);

        async Task ReadFrame()
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(Utf8.GetString(frame.GetBuffer(), 0, (int)frame.Length));
            }
            catch (Exception error) when (error is JsonException or DecoderFallbackException)
            {
                await Write(Error(null, -32700, "Parse error")).ConfigureAwait(false);
                return;
            }

            using (document)
            {
                object? response = await Dispatch(document.RootElement).ConfigureAwait(false);
                if (response is not null)
                    await Write(response).ConfigureAwait(false);
            }
        }

        Task Write(object response) => writer.WriteLineAsync(JsonSerializer.Serialize(response));

        async Task<object?> Dispatch(JsonElement request)
        {
            if (request.ValueKind != JsonValueKind.Object)
                return Error(null, -32600, "Invalid request");

            bool hasId = request.TryGetProperty("id", out JsonElement id);
            if (hasId && !IsRequestId(id))
                return Error(null, -32600, "The request id must be a string or integer.");

            if (!TryGetText(request, "jsonrpc", out string jsonrpc) || jsonrpc != "2.0" ||
                !TryGetText(request, "method", out string method))
                return Error(hasId ? id : null, -32600, "Invalid request");

            bool hasParams = request.TryGetProperty("params", out JsonElement parameters);
            if (hasParams && parameters.ValueKind != JsonValueKind.Object)
                return hasId ? Error(id, -32602, "Request parameters must be an object.") : null;
            if (!hasParams)
                parameters = EmptyObject;

            if (!hasId)
            {
                if (method == "notifications/initialized" && state == ConnectionState.AwaitingInitialized)
                    state = ConnectionState.Ready;
                return null;
            }

            if (method == "ping")
                return Success(id, EmptyObject);

            if (method == "initialize")
            {
                if (state != ConnectionState.New)
                    return Error(id, -32600, "This connection has already received initialize.");

                if (!TryGetText(parameters, "protocolVersion", out _) ||
                    !parameters.TryGetProperty("capabilities", out JsonElement capabilities) || capabilities.ValueKind != JsonValueKind.Object ||
                    !parameters.TryGetProperty("clientInfo", out JsonElement clientInfo) || clientInfo.ValueKind != JsonValueKind.Object ||
                    !TryGetText(clientInfo, "name", out _) || !TryGetText(clientInfo, "version", out _))
                    return Error(id, -32602, "Initialize requires protocolVersion, capabilities and clientInfo.");

                state = ConnectionState.AwaitingInitialized;
                return Success(id, new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "desktop-dock-p1", version = "1.0.0" }
                });
            }

            if (state != ConnectionState.Ready)
                return Error(id, -32002, "Initialize the connection and send notifications/initialized first.");

            if (method == "tools/list")
                return parameters.TryGetProperty("cursor", out _)
                    ? Error(id, -32602, "The tool list has no further page.")
                    : Success(id, new { tools = publishedTools });

            if (method != "tools/call")
                return Error(id, -32601, "Method not found");

            if (!TryGetText(parameters, "name", out string name))
                return Error(id, -32602, "A tool name is required.");
            if (!toolNames.Contains(name))
                return Error(id, -32602, $"Unknown tool: {name}");

            if (!parameters.TryGetProperty("arguments", out JsonElement arguments))
                arguments = EmptyObject;
            if (arguments.ValueKind != JsonValueKind.Object)
                return Error(id, -32602, "Tool arguments must be an object.");

            try
            {
                object toolResult = await invoke(name, arguments).ConfigureAwait(false);
                return Success(id, JsonSerializer.SerializeToElement(toolResult));
            }
            catch (Exception error)
            {
                return Success(id, new { content = new[] { new { type = "text", text = error.Message } }, isError = true });
            }
        }
    }

    private static bool IsRequestId(JsonElement id) => ReadText(id) is not null ||
        id.ValueKind == JsonValueKind.Number && id.GetRawText().AsSpan().IndexOfAny('.', 'e', 'E') < 0;

    private static bool TryGetText(JsonElement source, string name, out string value)
    {
        value = source.TryGetProperty(name, out JsonElement item)
            ? ReadText(item) ?? string.Empty
            : string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? ReadText(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return null;

        // JSON escapes can encode an unpaired surrogate even after strict UTF-8 decoding.
        try { return value.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    private static object Success(JsonElement id, object result) => new { jsonrpc = "2.0", id, result };

    private static object Error(JsonElement? id, int code, string message) => new { jsonrpc = "2.0", id, error = new { code, message } };
}
