using System.Windows;
using System.Windows.Controls;

namespace InterviewCoach.App.Controls;

/// <summary>PasswordBox.Password is not bindable; this attached property makes it so.</summary>
public static class PasswordBoxBinder
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxBinder),
        // The default must differ from any bound value (including ""), otherwise the first binding raises no change
        // callback and the PasswordChanged handler below is never attached.
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    private static readonly DependencyProperty UpdatingProperty =
        DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(PasswordBoxBinder), new PropertyMetadata(false));

    public static string GetBoundPassword(DependencyObject d) => (string?)d.GetValue(BoundPasswordProperty) ?? string.Empty;
    public static void SetBoundPassword(DependencyObject d, string value) => d.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        box.PasswordChanged -= OnPasswordChanged;
        if (!(bool)box.GetValue(UpdatingProperty))
            box.Password = e.NewValue as string ?? string.Empty;
        box.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetValue(UpdatingProperty, true);
        SetBoundPassword(box, box.Password);
        box.SetValue(UpdatingProperty, false);
    }
}
