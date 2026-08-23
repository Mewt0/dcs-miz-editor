using System.Net.Http;
using MizEdit.Core;

namespace MizEdit.Services;

public sealed class TranslationQueueRunner
{
    private readonly ITranslationProvider _provider;
    private CancellationTokenSource? _cancellation;

    public TranslationQueueRunner(ITranslationProvider provider)
    {
        _provider = provider;
    }

    public TranslationQueueState State { get; } = new();

    public async Task RunAsync(
        IReadOnlyList<TranslationEntry> targets,
        bool overwriteExisting,
        bool clearErrors = true,
        CancellationToken cancellationToken = default)
    {
        if (State.IsActive)
            throw new InvalidOperationException(UserMessages.Get("TranslationQueueActive"));

        _cancellation?.Dispose();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cancellation.Token;
        State.Begin(targets.Count, clearErrors);

        foreach (var entry in targets)
        {
            if (token.IsCancellationRequested)
            {
                State.MarkCancelled();
                return;
            }

            if (!overwriteExisting && !entry.IsMissing)
            {
                entry.SetWorkStatus(TranslationWorkStatus.Skipped);
                State.ReportSkipped();
                continue;
            }

            entry.SetWorkStatus(TranslationWorkStatus.InProgress);
            try
            {
                var translated = await _provider.TranslateAsync(entry.SourceText, token);
                if (string.Equals(translated, entry.SourceText, StringComparison.Ordinal))
                {
                    entry.SetWorkStatus(TranslationWorkStatus.Skipped);
                    State.ReportSkipped();
                }
                else
                {
                    entry.ApplyAiTranslation(translated);
                    entry.SetWorkStatus(TranslationWorkStatus.Completed);
                    State.ReportSuccess();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                entry.SetWorkStatus(TranslationWorkStatus.None);
                State.MarkCancelled();
                return;
            }
            catch (ProtectedTokenException ex)
            {
                entry.SetWorkStatus(TranslationWorkStatus.Error);
                State.ReportError(new TranslationError(
                    entry.Key,
                    ex.Message,
                    TranslationErrorKind.ProtectedTokenMismatch));
            }
            catch (Exception ex) when (ex is TimeoutException or HttpRequestException)
            {
                entry.SetWorkStatus(TranslationWorkStatus.Error);
                State.ReportError(new TranslationError(
                    entry.Key,
                    ex.Message,
                    TranslationErrorKind.Transient));
            }
            catch (Exception ex)
            {
                entry.SetWorkStatus(TranslationWorkStatus.Error);
                State.ReportError(new TranslationError(
                    entry.Key,
                    ex.Message,
                    TranslationErrorKind.Permanent));
            }
        }

        State.MarkCompleted();
    }

    public void Cancel()
    {
        if (!State.IsActive)
            return;

        State.MarkCancelling();
        _cancellation?.Cancel();
    }
}
