using System.Windows.Input;

namespace StardewModdingAPI.Installer.Gui.ViewModels;

/// <summary>An awaitable, single-flight command for frontend operations.</summary>
internal sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    Action<Exception>? onError = null
) : ICommand
{
    private readonly Func<Task> ExecuteAction = execute ?? throw new ArgumentNullException(nameof(execute));
    private readonly Func<bool>? CanExecuteAction = canExecute;
    private readonly Action<Exception>? ErrorAction = onError;
    private int IsExecutingValue;

    public event EventHandler? CanExecuteChanged;

    public bool IsExecuting => Volatile.Read(ref this.IsExecutingValue) != 0;

    public bool CanExecute(object? parameter)
    {
        return !this.IsExecuting && (this.CanExecuteAction?.Invoke() ?? true);
    }

    public async void Execute(object? parameter)
    {
        await this.ExecuteAsync().ConfigureAwait(true);
    }

    public async Task ExecuteAsync()
    {
        if (!this.CanExecute(null) || Interlocked.CompareExchange(ref this.IsExecutingValue, 1, 0) != 0)
            return;

        this.NotifyCanExecuteChanged();
        try
        {
            await this.ExecuteAction().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            this.ErrorAction?.Invoke(ex);
        }
        finally
        {
            Volatile.Write(ref this.IsExecutingValue, 0);
            this.NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged()
    {
        this.CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
