using System;
using System.Windows.Input;

namespace Klankhuis_Hero_Sample.Models;

/// <summary>
/// Minimal <see cref="ICommand"/> wrapper around an <see cref="Action{T}"/>
/// — just enough to wire <c>HeroCarouselItem.PrimaryCtaCommand</c> /
/// <c>SecondaryCtaCommand</c> in the sample without taking a dependency
/// on <c>CommunityToolkit.Mvvm</c>'s <c>RelayCommand</c>. Production apps
/// should use the toolkit version.
/// </summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;

    public RelayCommand(Action<object?> execute)
    {
        _execute = execute;
    }

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>Never raised — this command is always executable.</summary>
    public event EventHandler? CanExecuteChanged
    {
        add { } remove { }
    }
}
