using System;
using System.Windows.Input;

namespace Fanadapter.SimHub.UI
{
    /// <summary>Minimal ICommand so the XAML can bind to view-model methods.</summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute();

        public virtual void Execute(object parameter) => _execute();

        public event EventHandler CanExecuteChanged;

        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// RelayCommand that passes the binding's CommandParameter through — used
    /// by the per-slot buttons, where the parameter identifies which slot was
    /// clicked.
    /// </summary>
    public class ParameterCommand : RelayCommand
    {
        private readonly Action<object> _execute;

        public ParameterCommand(Action<object> execute, Func<bool> canExecute = null)
            : base(() => { }, canExecute)
        {
            _execute = execute;
        }

        public override void Execute(object parameter) => _execute(parameter);
    }
}
