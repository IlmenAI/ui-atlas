namespace UIMaps.Map.Core;

public sealed class UiMapException(string code, string message, bool retryable = false, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public sealed class StableMapRefAllocator
{
    public string Allocate(string kind) => $"uimaps:{kind}:{Guid.NewGuid():N}";
}
