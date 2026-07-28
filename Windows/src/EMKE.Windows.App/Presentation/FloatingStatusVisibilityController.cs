namespace EMKE.Windows.App.Presentation;

internal sealed class FloatingStatusVisibilityController
{
    private int _enabled;

    public FloatingStatusVisibilityController(bool enabled)
    {
        _enabled = enabled ? 1 : 0;
    }

    public event EventHandler? EnabledChanged;

    public bool Enabled => Volatile.Read(ref _enabled) != 0;

    public void SetEnabled(bool enabled)
    {
        int next = enabled ? 1 : 0;
        if (Interlocked.Exchange(ref _enabled, next) != next)
        {
            EnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
