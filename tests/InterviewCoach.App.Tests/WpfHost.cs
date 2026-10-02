using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using InterviewCoach.App;

namespace InterviewCoach.App.Tests;

/// <summary>
/// One shared STA thread running a WPF dispatcher and the real App resources (styles, theme, data templates).
/// WPF allows a single Application per process and every UI object belongs to the thread that made it,
/// so all view tests run their UI work through <see cref="Run{T}"/>.
/// </summary>
public static class WpfHost
{
    private static readonly Lazy<Dispatcher> Host = new(Start);
    private static readonly BindingErrorListener Errors = new();

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new InterviewCoach.App.App();
            app.InitializeComponent();
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "WPF test host" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(Errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        return dispatcher!;
    }

    public static T Run<T>(Func<T> action) => Host.Value.Invoke(action);

    public static void Run(Action action) => Host.Value.Invoke(action);

    /// <summary>Binding errors (wrong property path, two-way binding on a read-only property...) seen since the last call.</summary>
    public static string TakeBindingErrors() => Errors.Take();

    private sealed class BindingErrorListener : TraceListener
    {
        private readonly StringBuilder _buffer = new();
        private readonly object _gate = new();

        public override void Write(string? message) { lock (_gate) _buffer.Append(message); }
        public override void WriteLine(string? message) { lock (_gate) _buffer.AppendLine(message); }

        public string Take()
        {
            lock (_gate)
            {
                var text = _buffer.ToString();
                _buffer.Clear();
                return text.Trim();
            }
        }
    }
}
