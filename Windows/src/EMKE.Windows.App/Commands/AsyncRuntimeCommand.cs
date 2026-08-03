using System.Globalization;
using System.Windows.Input;
using EMKE.Core;

namespace EMKE.Windows.App.Commands;

internal interface IRuntimeCommandSink
{
    Task<RuntimeError?> SubmitAsync(
        RuntimeCommand command,
        CancellationToken cancellationToken);
}

internal interface IAppSurfaceActions
{
    ValueTask OpenSettingsAsync(CancellationToken cancellationToken);

    ValueTask OpenDiagnosticsAsync(CancellationToken cancellationToken);
}

internal sealed class AsyncRuntimeCommandGroup
{
    private readonly object _sync = new();
    private bool _normalExecuting;
    private bool _priorityExecuting;

    public event EventHandler? StateChanged;

    public bool CanEnter(bool priority)
    {
        lock (_sync)
        {
            return priority
                ? !_priorityExecuting
                : !_normalExecuting && !_priorityExecuting;
        }
    }

    public bool TryEnter(bool priority)
    {
        bool entered;
        lock (_sync)
        {
            entered = priority
                ? !_priorityExecuting
                : !_normalExecuting && !_priorityExecuting;
            if (entered)
            {
                if (priority)
                {
                    _priorityExecuting = true;
                }
                else
                {
                    _normalExecuting = true;
                }
            }
        }

        if (entered)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return entered;
    }

    public void Exit(bool priority)
    {
        lock (_sync)
        {
            if (priority)
            {
                _priorityExecuting = false;
            }
            else
            {
                _normalExecuting = false;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class CommandExecutionFailedEventArgs : EventArgs
{
    public CommandExecutionFailedEventArgs(Exception exception)
    {
        Exception =
            exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public Exception Exception { get; }
}

internal sealed class AsyncRuntimeCommand : ICommand, IDisposable
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool> _canExecute;
    private readonly AsyncRuntimeCommandGroup _group;
    private readonly bool _isPriority;
    private int _disposed;

    public AsyncRuntimeCommand(
        Func<CancellationToken, Task> execute,
        Func<bool>? canExecute = null,
        AsyncRuntimeCommandGroup? group = null,
        bool isPriority = false)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute ?? (() => true);
        _group = group ?? new AsyncRuntimeCommandGroup();
        _isPriority = isPriority;
        _group.StateChanged += OnGroupStateChanged;
    }

    public event EventHandler? CanExecuteChanged;

    public event EventHandler<CommandExecutionFailedEventArgs>? ExecutionFailed;

    public bool IsPriority => _isPriority;

    public bool CanExecute(object? parameter)
    {
        return Volatile.Read(ref _disposed) == 0
            && _canExecute()
            && _group.CanEnter(_isPriority);
    }

#pragma warning disable CA1031 // ICommand cannot surface Task failures to its void caller.
    public async void Execute(object? parameter)
    {
        try
        {
            _ = await ExecuteAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ExecutionFailed?.Invoke(
                this,
                new CommandExecutionFailedEventArgs(exception));
        }
    }
#pragma warning restore CA1031

    public async Task<bool> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (!_canExecute() || !_group.TryEnter(_isPriority))
        {
            return false;
        }

        try
        {
            await _execute(cancellationToken).ConfigureAwait(true);
            return true;
        }
        finally
        {
            _group.Exit(_isPriority);
        }
    }

    public void NotifyCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _group.StateChanged -= OnGroupStateChanged;
            NotifyCanExecuteChanged();
        }
    }

    private void OnGroupStateChanged(object? sender, EventArgs e)
    {
        NotifyCanExecuteChanged();
    }
}

internal static class BoundedPresentationText
{
    public static string Caption(string value, int maximumTextElements)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (maximumTextElements < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTextElements),
                maximumTextElements,
                "The caption bound must leave room for an ellipsis.");
        }

        StringInfo text = new(value);
        if (text.LengthInTextElements <= maximumTextElements)
        {
            return value;
        }

        return text.SubstringByTextElements(
            0,
            maximumTextElements - 1) + '…';
    }
}
