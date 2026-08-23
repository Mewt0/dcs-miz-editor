using MizEdit.Core;

namespace MizEdit.Services;

public interface ITranslationProvider
{
    Task<string> TranslateAsync(string source, CancellationToken cancellationToken = default);
}

public sealed class ProtectedTokenException : InvalidOperationException
{
    public ProtectedTokenException(string token, string? reason = null)
        : base(reason == null
            ? UserMessages.Get("ProtectedTokenChanged", token)
            : UserMessages.Get("ProtectedTokenChangedReason", token, reason))
    {
        Token = token;
    }

    public string Token { get; }
}
