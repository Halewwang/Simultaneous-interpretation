using System.ComponentModel;
using System.Runtime.CompilerServices;
using EMKE.Core;
using EMKE.Windows.App.Commands;
using EMKE.Windows.App.Presentation;

namespace EMKE.Windows.App.Floating;

internal sealed class FloatingStatusViewModel :
    INotifyPropertyChanged,
    IDisposable
{
    private const int CaptionTextElementLimit = 96;

    private readonly IRuntimeCommandSink _runtimeCommands;
    private readonly FloatingStatusVisibilityController
        _visibilityController;
    private readonly IDisposable _presentationSubscription;
    private AppPresentation? _presentation;
    private int _disposed;

    public FloatingStatusViewModel(
        PresentationCoordinator presentationCoordinator,
        IRuntimeCommandSink runtimeCommands,
        FloatingStatusVisibilityController visibilityController)
    {
        ArgumentNullException.ThrowIfNull(presentationCoordinator);
        _runtimeCommands = runtimeCommands
            ?? throw new ArgumentNullException(nameof(runtimeCommands));
        _visibilityController = visibilityController
            ?? throw new ArgumentNullException(nameof(visibilityController));
        StopCommand = new AsyncRuntimeCommand(
            SubmitStopAsync,
            () => _presentation?.StopAction.IsEnabled == true,
            isPriority: true);
        _presentationSubscription =
            presentationCoordinator.Subscribe(ApplyPresentation);
        _visibilityController.EnabledChanged += OnVisibilityEnabledChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncRuntimeCommand StopCommand { get; }

    public bool ShouldBeVisible =>
        _visibilityController.Enabled
        && _presentation is not null
        && (!(_presentation.StartAction.IsVisible
                && _presentation.StartAction.IsEnabled
                && !_presentation.StopAction.IsVisible)
            || _presentation.Severity
                is PresentationSeverity.Warning
                or PresentationSeverity.Critical);

    public string RuntimeStatus => _presentation?.RuntimeLabel ?? string.Empty;

    public string InboundStatus =>
        _presentation?.InboundChannel.StatusLabel ?? string.Empty;

    public string OutboundStatus =>
        _presentation?.OutboundChannel.StatusLabel ?? string.Empty;

    public double InboundLevel => _presentation?.InboundLevel ?? 0;

    public double OutboundLevel => _presentation?.OutboundLevel ?? 0;

    public string SourceCaption => BoundedPresentationText.Caption(
        _presentation?.SourceCaption ?? string.Empty,
        CaptionTextElementLimit);

    public string TranslatedCaption => BoundedPresentationText.Caption(
        _presentation?.TranslatedCaption ?? string.Empty,
        CaptionTextElementLimit);

    public string StopLabel =>
        _presentation?.StopAction.Label ?? string.Empty;

    public bool IsStopVisible =>
        _presentation?.StopAction.IsVisible == true;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _visibilityController.EnabledChanged -= OnVisibilityEnabledChanged;
        _presentationSubscription.Dispose();
        StopCommand.Dispose();
    }

    private async Task SubmitStopAsync(CancellationToken cancellationToken)
    {
        _ = await _runtimeCommands.SubmitAsync(
                new RuntimeCommand.Stop(),
                cancellationToken)
            .ConfigureAwait(true);
    }

    private void ApplyPresentation(AppPresentation presentation)
    {
        _presentation = presentation
            ?? throw new ArgumentNullException(nameof(presentation));
        OnPropertyChanged(string.Empty);
        StopCommand.NotifyCanExecuteChanged();
    }

    private void OnVisibilityEnabledChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        OnPropertyChanged(nameof(ShouldBeVisible));
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
