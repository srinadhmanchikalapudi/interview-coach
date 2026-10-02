using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.Controls;

/// <summary>
/// A TextBlock that shows bracketed placeholders such as "[your actual p99]" highlighted, so the candidate can see
/// at a glance which details of a model answer they must replace with real values.
/// </summary>
public class HighlightedTextBlock : TextBlock
{
    private static readonly Brush HighlightBackground = Freeze(new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xB3, 0x00)));

    public static readonly DependencyProperty HighlightedTextProperty = DependencyProperty.Register(
        nameof(HighlightedText), typeof(string), typeof(HighlightedTextBlock),
        new PropertyMetadata(null, (d, e) => ((HighlightedTextBlock)d).Rebuild(e.NewValue as string)));

    public string? HighlightedText
    {
        get => (string?)GetValue(HighlightedTextProperty);
        set => SetValue(HighlightedTextProperty, value);
    }

    private void Rebuild(string? text)
    {
        Inlines.Clear();
        if (string.IsNullOrEmpty(text)) return;

        foreach (var piece in CoachText.SplitPlaceholders(text))
        {
            var run = new Run(piece.Text);
            if (piece.IsPlaceholder)
            {
                run.Background = HighlightBackground;
                run.FontWeight = FontWeights.SemiBold;
            }
            Inlines.Add(run);
        }
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
