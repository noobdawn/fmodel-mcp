using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace FModel.Mcp.Tools;

/// <summary>
/// 工具执行包装。
///
/// 存在的理由：MCP SDK 默认把工具抛出的异常压成 "An error occurred invoking 'x'."，
/// 详细信息不会传给调用方。而本服务器的设计大量依赖**用错误信息引导 Agent 自我纠正**
/// （例如 "只允许 SELECT"、"会话已过期，请重新 open_game"、"exportIndex 越界，该包有 N 个 export"）。
/// 因此统一由工具自己捕获并构造带完整消息的 CallToolResult。
/// </summary>
public static class ToolResponse
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        // 统一 camelCase —— 否则匿名对象是 camelCase 而 record 是 PascalCase，Agent 取字段会取错
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static CallToolResult Run(string toolName, Func<object> body)
    {
        try
        {
            return Ok(body());
        }
        catch (Exception e)
        {
            return Error(toolName, e);
        }
    }

    public static async Task<CallToolResult> RunAsync(string toolName, Func<Task<object>> body)
    {
        try
        {
            return Ok(await body().ConfigureAwait(false));
        }
        catch (Exception e)
        {
            return Error(toolName, e);
        }
    }

    public static CallToolResult Ok(object payload) => new()
    {
        IsError = false,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, Json) }]
    };

    private static CallToolResult Error(string toolName, Exception e)
    {
        var kind = e switch
        {
            ArgumentException => "invalid_argument",
            InvalidOperationException => "invalid_state",
            FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException => "not_found",
            OperationCanceledException => "cancelled",
            _ => "internal_error"
        };

        var payload = new
        {
            error = new
            {
                tool = toolName,
                kind,
                message = e.Message,
                exceptionType = e.GetType().Name,
                inner = e.InnerException?.Message
            }
        };

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, Json) }]
        };
    }
}
