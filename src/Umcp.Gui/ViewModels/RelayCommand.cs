using System.Windows.Input;

namespace Umcp.Gui.ViewModels;

/// <summary>
/// The smallest command that works, sync or async.
///
/// An async handler runs with the button disabled and never lets an exception escape onto the UI
/// thread: an unhandled task exception in a click handler takes the whole window down, and a
/// crashed window looks to a user exactly like a crashed server.
/// </summary>
public sealed class RelayCommand : ICommand
{
    readonly Func<object?, Task>? _executeAsync;
    readonly Action<object?>? _execute;
    readonly Func<object?, bool>? _canExecute;
    bool _running;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        try
        {
            if (_executeAsync is not null)
            {
                _running = true;
                RaiseCanExecuteChanged();
                await _executeAsync(parameter);
            }
            else _execute?.Invoke(parameter);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[gui] command failed: {e}");
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}
