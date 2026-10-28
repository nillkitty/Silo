using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Silo.Connectors.Web.Mcp;

#region Base RPC Framing & Discriminated Unions

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
public abstract record McpMessage
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";
}

public sealed record McpRequest : McpMessage
{
    [JsonPropertyName("id")]
    public RequestId Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Params { get; init; }
}

public sealed record McpResponse : McpMessage
{
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpError? Error { get; init; }

    [JsonPropertyName("id")]
    public RequestId Id { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Result { get; init; }
}

public sealed record McpNotification : McpMessage
{
    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Params { get; init; }
}

public sealed record McpError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

[JsonConverter(typeof(RequestIdConverter))]
public readonly record struct RequestId
{
    public long?   NumericValue { get; }
    public string? StringValue  { get; }

    public static implicit operator RequestId(string value)
    {
        return new RequestId(value);
    }

    public static implicit operator RequestId(long value)
    {
        return new RequestId(value);
    }

    public override string ToString()
    {
        return StringValue ?? NumericValue?.ToString() ?? string.Empty;
    }

    public RequestId(string value)
    {
        StringValue = value;
    }

    public RequestId(long value)
    {
        NumericValue = value;
    }
}

public sealed class RequestIdConverter : JsonConverter<RequestId>
{
    public override RequestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
               {
                   JsonTokenType.String => new RequestId(reader.GetString()!),
                   JsonTokenType.Number => new RequestId(reader.GetInt64()),
                   _                    => throw new JsonException($"Unexpected token type {reader.TokenType} for RequestId.")
               };
    }

    public override void Write(Utf8JsonWriter writer, RequestId value, JsonSerializerOptions options)
    {
        if (value.StringValue != null)
            writer.WriteStringValue(value.StringValue);
        else if (value.NumericValue.HasValue)
            writer.WriteNumberValue(value.NumericValue.Value);
        else
            writer.WriteNullValue();
    }
}

#endregion

#region Handshake & Capabilities

public sealed record ImplementationInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

public sealed record InitializeParams
{
    [JsonPropertyName("capabilities")]
    public ClientCapabilities Capabilities { get; init; } = new();

    [JsonPropertyName("clientInfo")]
    public required ImplementationInfo ClientInfo { get; init; }

    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; init; } = "2024-11-05";
}

public sealed record InitializeResult
{
    [JsonPropertyName("capabilities")]
    public required ServerCapabilities Capabilities { get; init; }

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; init; }

    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }

    [JsonPropertyName("serverInfo")]
    public required ImplementationInfo ServerInfo { get; init; }
}

public sealed record ClientCapabilities
{
    [JsonPropertyName("experimental")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Experimental { get; init; }

    [JsonPropertyName("roots")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Roots { get; init; }

    [JsonPropertyName("sampling")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Sampling { get; init; }
}

public sealed record ServerCapabilities
{
    [JsonPropertyName("logging")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Logging { get; init; }

    [JsonPropertyName("prompts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CapabilityWithListChanged? Prompts { get; init; }

    [JsonPropertyName("resources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResourceCapability? Resources { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CapabilityWithListChanged? Tools { get; init; }
}

public sealed record CapabilityWithListChanged
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; init; }
}

public sealed record ResourceCapability
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; init; }

    [JsonPropertyName("subscribe")]
    public bool Subscribe { get; init; }
}

#endregion

#region Tools

public sealed record Tool
{
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("inputSchema")]
    public required JsonObject InputSchema { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed record ListToolsResult
{
    [JsonPropertyName("nextCursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; init; }

    [JsonPropertyName("tools")]
    public required List<Tool> Tools { get; init; } = [];
}

public sealed record CallToolParams
{
    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Arguments { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed record CallToolResult
{
    [JsonPropertyName("content")]
    public required List<ContentBlock> Content { get; init; } = [];

    [JsonPropertyName("isError")]
    public bool IsError { get; init; }
}

#endregion

#region Resources & Prompts

public sealed record Resource
{
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Size { get; init; }

    [JsonPropertyName("uri")]
    public required string Uri { get; init; }
}

public sealed record Prompt
{
    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PromptArgument>? Arguments { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed record PromptArgument
{
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("required")]
    public bool Required { get; init; }
}

#endregion

#region Content & Sampling

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent),      "text")]
[JsonDerivedType(typeof(ImageContent),     "image")]
[JsonDerivedType(typeof(EmbeddedResource), "resource")]
public abstract record ContentBlock;

public sealed record TextContent : ContentBlock
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed record ImageContent : ContentBlock
{
    [JsonPropertyName("data")]
    public required string Data { get; init; } // Base64

    [JsonPropertyName("mimeType")]
    public required string MimeType { get; init; }
}

public sealed record EmbeddedResource : ContentBlock
{
    [JsonPropertyName("resource")]
    public required ResourceContent Resource { get; init; }
}

public sealed record ResourceContent
{
    [JsonPropertyName("blob")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Blob { get; init; } // Base64

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("uri")]
    public required string Uri { get; init; }
}

public sealed record CreateMessageParams
{
    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; init; }

    [JsonPropertyName("messages")]
    public required List<SamplingMessage> Messages { get; init; } = [];

    [JsonPropertyName("modelPreferences")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? ModelPreferences { get; init; }
}

public sealed record SamplingMessage
{
    [JsonPropertyName("content")]
    public required ContentBlock Content { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; } // "user" | "assistant"
}

#endregion