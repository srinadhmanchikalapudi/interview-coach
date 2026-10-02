using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewCoach.App.Controls;
using InterviewCoach.App.ViewModels;
using InterviewCoach.App.Views;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.Tests;

public class WheelScrollingTests
{
    // ---- the rule

    [Theory]
    [InlineData(false, -120, 0.0, 500.0, false)]   // not being edited: the page scrolls, even though the box has text to scroll
    [InlineData(false, 120, 200.0, 500.0, false)]
    [InlineData(true, -120, 0.0, 500.0, true)]     // editing, scrolling down with room left: the box keeps the wheel
    [InlineData(true, -120, 250.0, 500.0, true)]
    [InlineData(true, 120, 250.0, 500.0, true)]    // editing, scrolling up with room above: the box keeps the wheel
    [InlineData(true, -120, 500.0, 500.0, false)]  // at the bottom of the text: hand over to the page
    [InlineData(true, 120, 0.0, 500.0, false)]     // at the top of the text: hand over to the page
    [InlineData(true, -120, 0.0, 0.0, false)]      // nothing to scroll in the box at all
    public void The_box_keeps_the_wheel_only_while_it_is_being_edited_and_has_room(bool focused, int delta, double offset, double scrollable, bool expected)
    {
        Assert.Equal(expected, WheelScrolling.ShouldScrollInside(focused, delta, offset, scrollable));
    }

    // ---- on the real Home screen

    private static string LongText => string.Join("\n", Enumerable.Range(1, 200).Select(i => $"Line {i} of a long job description or resume."));

    private static (HomeView View, ScrollViewer Page, TextBox JobDescription, TextBox Resume) ShowHome()
    {
        var repo = new InMemoryProfileRepository();
        var profile = Samples.CompleteProfile();
        profile.JobDescription = LongText;
        profile.ResumeText = LongText;
        repo.SaveAsync(profile).GetAwaiter().GetResult();

        var vm = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs());
        vm.InitializeAsync().GetAwaiter().GetResult();

        var view = new HomeView { DataContext = vm };
        Layout(view);
        var page = (ScrollViewer)view.FindName("EditorScroller")!;
        var boxes = Descendants<TextBox>(view).Where(t => t.AcceptsReturn).ToList();
        return (view, page, boxes[0], boxes[1]);
    }

    // A short window, so the page has to scroll to reach the Start button.
    private static void Layout(FrameworkElement view)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(new Size(1000, 400));
            view.Arrange(new Rect(0, 0, 1000, 400));
            view.UpdateLayout();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static MouseWheelEventArgs Wheel(UIElement source, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = source,
        };
        source.RaiseEvent(args);

        // WPF only reports the new VerticalOffset after the next layout pass, so run one like a real window would.
        DependencyObject root = source;
        while (VisualTreeHelper.GetParent(root) is { } parent) root = parent;
        Layout((FrameworkElement)root);
        return args;
    }

    [Fact]
    public void Wheeling_over_the_job_description_scrolls_the_page_not_the_box()
    {
        WpfHost.Run(() =>
        {
            var (_, page, jobDescription, _) = ShowHome();
            Assert.True(page.ScrollableHeight > 0, "the page must be taller than the window for this test to mean anything");
            Assert.True(jobDescription.Template.FindName("PART_ContentHost", jobDescription) is ScrollViewer { ScrollableHeight: > 0 },
                "the box itself must have text to scroll: this is the case that used to trap the wheel");

            var args = Wheel(jobDescription, -120);

            Assert.True(args.Handled);
            Assert.True(page.VerticalOffset > 0, "the page should have scrolled down");
            var inner = (ScrollViewer)jobDescription.Template.FindName("PART_ContentHost", jobDescription);
            Assert.Equal(0, inner.VerticalOffset);   // the box did not move
        });
    }

    [Fact]
    public void Wheeling_over_the_resume_also_scrolls_the_page_and_keeps_going_with_every_notch()
    {
        WpfHost.Run(() =>
        {
            var (_, page, _, resume) = ShowHome();

            var offsets = new List<double> { page.VerticalOffset };
            for (var i = 0; i < 5; i++)
            {
                Wheel(resume, -120);
                offsets.Add(page.VerticalOffset);
            }

            Assert.True(offsets.Zip(offsets.Skip(1), (a, b) => b >= a).All(x => x));
            Assert.True(offsets[^1] > offsets[0]);
        });
    }

    [Fact]
    public void The_page_can_be_scrolled_back_up_with_the_pointer_over_a_text_box()
    {
        WpfHost.Run(() =>
        {
            var (_, page, jobDescription, _) = ShowHome();
            page.ScrollToVerticalOffset(page.ScrollableHeight);
            Layout(page);
            var bottom = page.VerticalOffset;
            Assert.True(bottom > 0);

            Wheel(jobDescription, 120);

            Assert.True(page.VerticalOffset < bottom);
        });
    }

    [Fact]
    public void Both_long_text_boxes_on_the_home_screen_use_the_behavior()
    {
        WpfHost.Run(() =>
        {
            var (_, _, jobDescription, resume) = ShowHome();

            Assert.True(WheelScrolling.GetPassToPage(jobDescription));
            Assert.True(WheelScrolling.GetPassToPage(resume));
        });
    }

    [Fact]
    public void A_text_box_without_the_behavior_still_traps_the_wheel_so_the_fix_is_what_changed_things()
    {
        WpfHost.Run(() =>
        {
            var (_, page, jobDescription, _) = ShowHome();
            WheelScrolling.SetPassToPage(jobDescription, false);

            var args = Wheel(jobDescription, -120);

            Assert.False(args.Handled);              // nothing forwarded it
            Assert.Equal(0, page.VerticalOffset);
        });
    }
}
