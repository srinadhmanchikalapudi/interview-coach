using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using InterviewCoach.App.ViewModels;

namespace InterviewCoach.App.Views;

public partial class MockView : UserControl
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _wasAnswering;

    public MockView()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => (DataContext as MockViewModel)?.Tick();
        Loaded += (_, _) => _clock.Start();
        Unloaded += (_, _) => _clock.Stop();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MockViewModel old)
            {
                old.PropertyChanged -= OnChanged;
                old.Composer.CaretRequested -= MoveCaret;
            }
            if (e.NewValue is MockViewModel vm)
            {
                vm.PropertyChanged += OnChanged;
                vm.Composer.CaretRequested += MoveCaret;
                _wasAnswering = vm.IsAnswering;
            }
        };
    }

    // Dictated words are inserted where the cursor is, so the view tells the composer where that is.
    private void AnswerBox_SelectionChanged(object sender, RoutedEventArgs e) => (DataContext as MockViewModel)?.Composer.SetCaret(AnswerBox.CaretIndex);

    // After dictated words went in, the cursor goes to the end of them so the next words follow on.
    private void MoveCaret(int index) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        AnswerBox.CaretIndex = Math.Min(index, AnswerBox.Text.Length);
        if (AnswerBox.CaretIndex >= AnswerBox.Text.Length) AnswerBox.ScrollToEnd();
    });

    // When it becomes the candidate's turn, put the cursor in the box so typing can start at once; a new line from the interviewer
    // starts at the top of the page.
    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not MockViewModel vm) return;
        var answering = vm.IsAnswering;
        if (answering && !_wasAnswering)
        {
            Scroller.ScrollToTop();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => AnswerBox.Focus());
        }
        _wasAnswering = answering;
    }
}
