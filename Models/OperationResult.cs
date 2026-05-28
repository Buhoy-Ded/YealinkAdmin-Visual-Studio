namespace YealinkAdmin.Models;

public sealed class OperationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? FilePath { get; init; }
    public string? RawResponse { get; init; }

    public static OperationResult Ok(string message, string? filePath = null, string? rawResponse = null)
        => new() { Success = true, Message = message, FilePath = filePath, RawResponse = rawResponse };

    public static OperationResult Fail(string message, string? rawResponse = null)
        => new() { Success = false, Message = message, RawResponse = rawResponse };
}
