using System.Windows.Controls;
using InterviewCoach.App.ViewModels;

namespace InterviewCoach.App.Views;

public partial class LearnView : UserControl
{
    public LearnView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is LearnViewModel old) old.QuestionChanged -= ScrollToTop;
            if (e.NewValue is LearnViewModel vm) vm.QuestionChanged += ScrollToTop;
        };
    }

    // A new question should start at the top, not wherever the previous answer was scrolled to.
    private void ScrollToTop() => Scroller.ScrollToTop();
}
