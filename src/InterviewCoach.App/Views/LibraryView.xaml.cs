using System.ComponentModel;
using System.Windows.Controls;
using InterviewCoach.App.ViewModels;

namespace InterviewCoach.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is LibraryViewModel old) old.PropertyChanged -= OnChanged;
            if (e.NewValue is LibraryViewModel vm) vm.PropertyChanged += OnChanged;
        };
    }

    // Opening another question should start at its top, not wherever the previous answer was scrolled to.
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.Selected)) DetailScroller.ScrollToTop();
    }
}
