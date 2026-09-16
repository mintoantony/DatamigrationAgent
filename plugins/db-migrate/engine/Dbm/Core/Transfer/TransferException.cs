namespace Dbm.Core.Transfer;

/// <summary>Expected transfer failure with a stable machine code (API: 409 {error: Code, message, details}).</summary>
public sealed class TransferException(string code, string message, IReadOnlyList<string>? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<string> Details { get; } = details ?? [];
}
