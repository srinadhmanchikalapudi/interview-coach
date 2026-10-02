using System.Windows;
using Microsoft.Win32;

namespace InterviewCoach.App.Services;

public interface IDialogService
{
    /// <summary>Shows a file picker and returns the chosen path, or null if cancelled.</summary>
    string? PickFile(string title, string filter);

    /// <summary>Yes/No question; true for Yes.</summary>
    bool Confirm(string title, string message);
}

public sealed class WpfDialogService : IDialogService
{
    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
