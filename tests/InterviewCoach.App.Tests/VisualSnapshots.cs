using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using InterviewCoach.App.ViewModels;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.App.Tests;

/// <summary>
/// A development aid, not a test of behavior: renders the real screens to PNG files in light and dark themes so the look can
/// be reviewed. It does nothing unless the ICOACH_SNAPSHOTS environment variable names an output folder.
/// </summary>
public class VisualSnapshots
{
    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current => s;
        public void Save(AppSettings settings) { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private const string JobDescription =
        "Senior Backend Engineer, Claims Platform\n\nWe are looking for an engineer to own services that process claims for eight regional trusts.\n\n" +
        "Requirements\n- 6+ years building services in C# and .NET\n- Strong SQL Server and query tuning skills\n- Experience with Redis, RabbitMQ or Azure Service Bus\n" +
        "- Comfortable with Docker and CI/CD\n- You review code carefully and write tests that fail for the right reason\n\nNice to have\n- React front ends\n- Kubernetes";

    private const string Resume =
        "Jane Doe - Senior Software Engineer\n\nExperience\nClaims Processing Facility (2021 - present)\n- Built the claims ranking service in C# on .NET 8, cutting p99 from 300ms to 80ms\n" +
        "- Introduced RabbitMQ for notification fan-out and Redis for hot lookups\n- Led the migration from WCF to ASP.NET Core using the strangler fig pattern\n\n" +
        "Skills: C#, .NET, SQL Server, Redis, RabbitMQ, Docker, Azure, React, TypeScript";

    [Fact]
    public void Capture_screens_when_asked()
    {
        var dir = Environment.GetEnvironmentVariable("ICOACH_SNAPSHOTS");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);

        WpfHost.Run(() =>
        {
            var prompts = new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));
            var llm = new FakeLlmService();
            var bank = new TechBank(new InMemoryTechBankRepository(), llm, prompts, () => 0.0);
            var settings = new MemorySettings(new AppSettings());

            var repo = new InMemoryProfileRepository();
            repo.SaveAsync(new CandidateProfile
            {
                Name = "Claims platform", JobRole = "Senior Backend Engineer", Seniority = Seniority.Senior, JobDescription = JobDescription, ResumeText = Resume,
            }).GetAwaiter().GetResult();
            repo.SaveAsync(new CandidateProfile
            {
                Name = "Fintech startup", JobRole = "Staff Engineer", Seniority = Seniority.Staff, JobDescription = "Payments platform.", ResumeText = Resume,
            }).GetAwaiter().GetResult();

            var home = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs(), bank);
            home.InitializeAsync().GetAwaiter().GetResult();
            var learn = new LearnViewModel(llm, prompts, settings, bank, () => 0.0);
            var settingsVm = new SettingsViewModel(settings, llm, bank, new ScriptedDialogs());
            var main = new MainViewModel(home, settingsVm, learn, settings);

            var window = new MainWindow(main);

            foreach (var (theme, name) in new[] { (ThemeMode.Light, "light"), (ThemeMode.Dark, "dark") })
            {
                Application.Current.ThemeMode = theme;

                main.CurrentPage = home;
                home.TechnologyMode = false;
                Save(window, 1180, 820, Path.Combine(dir, $"1-home-{name}.png"));

                home.TechnologyMode = true;
                home.Technologies.FirstOrDefault()?.GetType();
                foreach (var option in home.Technologies.Take(2)) option.IsChecked = true;
                home.OtherTechnologies = "Kafka, GraphQL";
                Save(window, 1180, 1700, Path.Combine(dir, $"2-home-technology-{name}.png"));

                learn.Begin(home.AllowedTypes.Count == 0 ? repo.ListAsync().GetAwaiter().GetResult()[0] : repo.ListAsync().GetAwaiter().GetResult()[0], [QuestionType.TechnicalConcept]);
                main.CurrentPage = learn;
                Save(window, 1180, 1500, Path.Combine(dir, $"3-learn-{name}.png"));

                settingsVm.Load();
                main.CurrentPage = settingsVm;
                Save(window, 1180, 2000, Path.Combine(dir, $"4-settings-{name}.png"));
            }
        });
    }

    private static void Save(Window window, int width, int height, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        for (var pass = 0; pass < 2; pass++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
        }

        // Paint the theme's window background first, so dark mode is not rendered on transparent white.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
