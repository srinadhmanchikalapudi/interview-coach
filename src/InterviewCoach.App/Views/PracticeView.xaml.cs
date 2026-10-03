using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using InterviewCoach.App.ViewModels;

namespace InterviewCoach.App.Views;

public partial class PracticeView : UserControl
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _wasAnswering;

    public PracticeView()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => (DataContext as PracticeViewModel)?.Tick();
        Loaded += (_, _) => _clock.Start();
        Unloaded += (_, _) => _clock.Stop();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is PracticeViewModel old)
            {
                old.QuestionChanged -= ScrollToTop;
                old.PropertyChanged -= OnChanged;
                old.CaretRequested -= MoveCaret;
            }
            if (e.NewValue is PracticeViewModel vm)
            {
                vm.QuestionChanged += ScrollToTop;
                vm.PropertyChanged += OnChanged;
                vm.CaretRequested += MoveCaret;
                _wasAnswering = vm.IsAnswering;
            }
        };
    }

    // Dictated words are inserted where the cursor is, so the view tells the model where that is.
    private void AnswerBox_SelectionChanged(object sender, RoutedEventArgs e) => (DataContext as PracticeViewModel)?.SetCaret(AnswerBox.CaretIndex);

    // After dictated words went in, the cursor goes to the end of them so the next words follow on.
    private void MoveCaret(int index) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        AnswerBox.CaretIndex = Math.Min(index, AnswerBox.Text.Length);
        if (AnswerBox.CaretIndex >= AnswerBox.Text.Length) AnswerBox.ScrollToEnd();
    });

    // A new question should start at the top, not wherever the previous feedback was scrolled to.
    private void ScrollToTop() => Scroller.ScrollToTop();

    // When the answer box appears (a new question, or trying again), put the cursor in it so typing can start at once.
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PracticeViewModel vm) return;
        var answering = vm.IsAnswering;
        if (answering && !_wasAnswering)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => AnswerBox.Focus());
        _wasAnswering = answering;
    }
}
