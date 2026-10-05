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
        "Senior Backend Engineer, Orders Platform\n\nWe are looking for an engineer to own services that process orders for eight regional stores.\n\n" +
        "Requirements\n- 6+ years building services in C# and .NET\n- Strong SQL Server and query tuning skills\n- Experience with Redis, Kafka or Azure Service Bus\n" +
        "- Comfortable with Docker and CI/CD\n- You review code carefully and write tests that fail for the right reason\n\nNice to have\n- React front ends\n- Kubernetes";

    private const string Resume =
        "Jane Doe - Senior Software Engineer\n\nExperience\nAcme Retail (2021 - present)\n- Built the product search service in C# on .NET 8, cutting p99 from 300ms to 80ms\n" +
        "- Introduced Kafka for order events and Redis for hot lookups\n- Moved the checkout API from a monolith to services behind feature flags\n\n" +
        "Skills: C#, .NET, SQL Server, Redis, Kafka, Docker, Azure, React, TypeScript";

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
                Name = "Orders platform", JobRole = "Senior Backend Engineer", Seniority = Seniority.Senior, JobDescription = JobDescription, ResumeText = Resume,
            }).GetAwaiter().GetResult();
            repo.SaveAsync(new CandidateProfile
            {
                Name = "Fintech startup", JobRole = "Staff Engineer", Seniority = Seniority.Staff, JobDescription = "Payments platform.", ResumeText = Resume,
            }).GetAwaiter().GetResult();

            var snapHistory = new InMemoryMockHistory();
            var home = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs(), bank, null, snapHistory);
            home.InitializeAsync().GetAwaiter().GetResult();
            var learn = new LearnViewModel(llm, prompts, settings, bank, () => 0.0);
            var settingsVm = new SettingsViewModel(settings, llm, bank, new ScriptedDialogs());
            var clock = DateTime.UtcNow.AddHours(-3);
            var history = new InMemoryLearnHistory(() => clock);
            CoachOutput Sample(string text, params FollowUp[] followUps) => new()
            {
                WhatTheyreTesting = "They want to know you understand how state works in function components, and when a hook is the right tool.",
                ModelAnswer = text,
                Shape = "Direct answer → how it works → a rule to remember → when not to use it",
                FollowUps = [.. followUps],
            };
            void Add(string question, string type, string? technology, bool general, string answer, params FollowUp[] followUps)
            {
                history.RecordAsync(new LearnHistoryEntry
                {
                    Question = question, QuestionType = type, Technology = technology, Seniority = "Senior", Source = "fundamentals",
                    IsGeneral = general, ProfileName = general ? null : "Orders platform", Coach = Sample(answer, followUps),
                }).GetAwaiter().GetResult();
                clock = clock.AddMinutes(25);
            }
            Add("What is a struct?", "technical_concept", "C#", true, "A struct is a value type, so it is copied on assignment and usually lives on the stack or inline in its container.");
            Add("What's the difference between PUT and PATCH requests in REST?", "technical_concept", "REST", true, "PUT replaces the whole resource with what you send, while PATCH changes only the fields you name.");
            Add("What does a hook do in React?", "technical_concept", "React", true,
                "A hook lets a function component keep state or run effects without becoming a class. useState remembers a value between renders, and useEffect runs code after a render. The rule to remember is that hooks run in the same order on every render, so they cannot sit inside conditions or loops.",
                new FollowUp { Question = "Why can hooks not be called in a condition?", Hint = "React tracks hooks by their call order." },
                new FollowUp { Question = "When would you write a custom hook?", Hint = "Share stateful logic between components without sharing UI." });
            Add("Why can hooks not be called in a condition?", "technical_concept", "React", true, "React matches each hook to its stored state by the order of the calls, so changing the order between renders gives the wrong state to the wrong hook.");
            Add("Why did you choose Redis over Memcached for the catalog cache?", "resume_deep_dive", null, false, "We needed expiry per key and a shared cache across several instances, and Redis gave us both along with simple data structures for the hot lookups.");
            Add("Tell me about a time you pushed back on a deadline.", "behavioral", null, false, "On the catalog migration the date was set before the data was clean. I showed the team the failure rate from a trial run and we moved the cutover by two weeks.");
            Add("Design a notification service for a orders platform.", "system_design", null, false, "I would put a queue between the orders service and the senders, keep a preference store per user, and retry failed sends with backoff.");
            var practiceHistory = new InMemoryPracticeHistory(() => clock);
            practiceHistory.RecordAsync(new PracticeRecord
            {
                Question = "Why did you choose Redis over Memcached for the catalog cache?", QuestionType = "resume_deep_dive", ProfileName = "Orders platform",
                AnswerText = "We needed expiry per key and a cache shared by several instances, and Redis gave us both. I also wanted simple data structures for the hot lookups.",
                WordCount = 27, DurationSeconds = 74,
                Coach = new CoachOutput
                {
                    WhatTheyreTesting = "Whether you chose the tool for reasons or by habit, and can name the alternative you ruled out.",
                    Feedback =
                    [
                        new FeedbackPoint { Kind = "strength", Point = "You gave two concrete reasons.", Quote = "expiry per key and a cache shared by several instances" },
                        new FeedbackPoint { Kind = "missing", Point = "Say what it did for the system: a number, before and after." },
                    ],
                    ModelAnswer = "We needed per-key expiry and one cache shared by several instances, and Redis gave us both. After the move the hot lookups dropped from about 40 ms to under 5 ms.",
                    Shape = "Direct answer → the constraint → the result with a number",
                    FollowUps = [new FollowUp { Question = "What did you do about cache invalidation?", Hint = "Name the strategy and one failure it prevented." }],
                },
            }).GetAwaiter().GetResult();
            clock = clock.AddMinutes(25);
            var library = new LibraryViewModel(history, new ScriptedDialogs(), null, practiceHistory);
            var practiceClock = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
            var practiceLlm = new StubLlm { QuestionText = "Tell me about a time you pushed back on a deadline." };
            var practiceVm = new PracticeViewModel(practiceLlm, prompts, settings, null, () => 0.0, () => practiceClock);
            var demoSpeech = new InterviewCoach.Infrastructure.Speech.SpeechFactory(new MemorySettings(new AppSettings { DemoMode = true }));
            var voiceVm = new PracticeViewModel(practiceLlm, prompts, new MemorySettings(new AppSettings { SpeakQuestions = false, AutoListen = false }), null, () => 0.0, () => practiceClock, speech: demoSpeech);
            var conceptsVm = new ConceptsViewModel(bank, settings);
            conceptsVm.Role = "Backend developer";
            conceptsVm.ShowTechnologiesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            foreach (var option in conceptsVm.Technologies.Where(t => t.Name is "C#" or "SQL Server" or "Docker")) option.IsChecked = true;
            conceptsVm.OtherTechnologies = "Kafka";
            conceptsVm.DifficultyOptions.First(o => o.Difficulty == Difficulty.Advanced).IsSelected = true;
            var mockLlm = new FakeLlmService();
            var mockSpeech = new TestSpeech();
            var mockVm = new MockViewModel(mockLlm, prompts, new MemorySettings(new AppSettings { AutoListen = false }), mockSpeech, new ScriptedDialogs(), snapHistory, () => practiceClock);
            var historyVm = new HistoryViewModel(snapHistory, repo, new ScriptedDialogs(), settings);
            DebriefViewModel? debriefVm = null;
            mockVm.DebriefReady += d => debriefVm = d;
            var main = new MainViewModel(home, settingsVm, learn, settings, library, null, conceptsVm, mockVm, historyVm);
            main.DismissNoticeCommand.Execute(null); // the pictures are of the app in use, not of a first run without a key

            var window = new MainWindow(main);
            var homeWithHistory = home;

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
                Save(window, 1180, 2500, Path.Combine(dir, $"4-settings-{name}.png"));

                var openRouter = new SettingsViewModel(new MemorySettings(new AppSettings { Provider = LlmProvider.OpenRouter }), llm, bank, new ScriptedDialogs(),
                    new FakeCatalog(
                        new OpenRouterModel("anthropic/claude-sonnet-5.5", "Anthropic: Claude Sonnet 5.5", 1_000_000, 2m, 10m),
                        new OpenRouterModel("anthropic/claude-haiku-4.5", "Anthropic: Claude Haiku 4.5", 200_000, 1m, 5m),
                        new OpenRouterModel("google/gemini-3.5-flash-lite", "Google: Gemini 3.5 Flash Lite", 1_048_576, 0.3m, 2.5m),
                        new OpenRouterModel("deepseek/deepseek-v4-flash", "DeepSeek: V4 Flash", 1_048_576, 0.028m, 0.056m),
                        new OpenRouterModel("openai/gpt-5-mini", "OpenAI: GPT-5 Mini", 400_000, 0.25m, 2m)));
                openRouter.LoadModelsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                openRouter.CheapestFirst = true;
                openRouter.SelectedCatalogModel = openRouter.CatalogModels.First();
                main.CurrentPage = openRouter;
                Save(window, 1180, 1500, Path.Combine(dir, $"5-settings-openrouter-{name}.png"));

                library.LoadAsync().GetAwaiter().GetResult();
                library.Selected = library.Rows.First(r => r.IsPractice);
                main.CurrentPage = library;
                Save(window, 1180, 1100, Path.Combine(dir, $"6-library-{name}.png"));

                practiceClock = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
                practiceVm.Begin(new LearnSessionRequest(repo.ListAsync().GetAwaiter().GetResult()[0], [QuestionType.Behavioral], null, [], EmploymentType.FullTime));
                practiceVm.AnswerText = "On the catalog migration the date was set before the data was clean. I ran a trial cutover, showed the team a four percent failure rate, and proposed moving the date by two weeks while we fixed the mapping. We moved it, and the real cutover had no failed claims.";
                practiceClock = practiceClock.AddSeconds(102);
                practiceVm.Tick();
                main.CurrentPage = practiceVm;
                Save(window, 1180, 760, Path.Combine(dir, $"7-practice-answering-{name}.png"));

                practiceVm.SubmitCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                Save(window, 1180, 1850, Path.Combine(dir, $"8-practice-feedback-{name}.png"));

                practiceClock = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
                voiceVm.Begin(new LearnSessionRequest(repo.ListAsync().GetAwaiter().GetResult()[0], [QuestionType.Behavioral], null, [], EmploymentType.FullTime));
                voiceVm.ToggleMicCommand.ExecuteAsync(null).GetAwaiter().GetResult();   // the demo microphone starts with a partial result
                practiceClock = practiceClock.AddSeconds(38);
                voiceVm.Tick();
                main.CurrentPage = voiceVm;
                Save(window, 1180, 820, Path.Combine(dir, $"9-practice-listening-{name}.png"));
                voiceVm.ToggleMicCommand.ExecuteAsync(null).GetAwaiter().GetResult();

                main.CurrentPage = conceptsVm;
                Save(window, 1180, 1500, Path.Combine(dir, $"10-concepts-{name}.png"));

                // Mock Interview: the Home options, a running interview with a partial answer, and the debrief.
                main.CurrentPage = home;
                home.SelectMockCommand.Execute(null);
                Save(window, 1180, 1500, Path.Combine(dir, $"11-home-mock-{name}.png"));
                home.SelectLearnCommand.Execute(null);

                practiceClock = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
                debriefVm = null;
                mockVm.Begin(new MockSessionRequest(repo.ListAsync().GetAwaiter().GetResult()[0], RoundType.Technical, 30, true, EmploymentType.FullTime));
                mockVm.AnswerText = "Good, thanks. I have been looking forward to this one.";
                mockVm.SubmitCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                mockVm.Composer.ToggleMicCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                mockSpeech.Mic.Final("On the catalog migration the date was set before the data was clean.");
                mockSpeech.Mic.Partial("so I ran a trial cutover and showed the team a four percent");
                practiceClock = practiceClock.AddSeconds(262);
                mockVm.Tick();
                main.CurrentPage = mockVm;
                Save(window, 1180, 1100, Path.Combine(dir, $"12-mock-interview-{name}.png"));

                mockVm.Composer.StopListeningAsync().GetAwaiter().GetResult();
                for (var guard = 0; debriefVm is null && guard < 20; guard++)
                {
                    if (mockVm.IsAnswering)
                    {
                        mockVm.AnswerText = "I compared the failure rate before and after on the migration dashboard.";
                        mockVm.SubmitCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                    }
                }
                main.CurrentPage = debriefVm!;
                Save(window, 1180, 2000, Path.Combine(dir, $"13-debrief-{name}.png"));
                // History: the finished interview, one that was left midway (offered back on Home), and the notice bar of a first run.
                practiceClock = new DateTime(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc);
                mockVm.Begin(new MockSessionRequest(repo.ListAsync().GetAwaiter().GetResult()[0], RoundType.SystemDesign, 45, true, EmploymentType.FullTime));
                practiceClock = practiceClock.AddSeconds(605);
                mockVm.AnswerText = "Good, thanks.";
                mockVm.SubmitCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                mockVm.Abandon();
                historyVm.LoadAsync().GetAwaiter().GetResult();
                main.CurrentPage = historyVm;
                Save(window, 1180, 640, Path.Combine(dir, $"14-history-{name}.png"));

                homeWithHistory.RefreshMockStatusAsync().GetAwaiter().GetResult();
                main.CurrentPage = home;
                Save(window, 1180, 900, Path.Combine(dir, $"15-home-resume-{name}.png"));
                foreach (var left in snapHistory.ListAsync().GetAwaiter().GetResult().Where(r => r.IsUnfinished).ToList()) snapHistory.DeleteAsync(left.Id).GetAwaiter().GetResult();
                homeWithHistory.RefreshMockStatusAsync().GetAwaiter().GetResult();

                home.AnswerRules = string.Join(Environment.NewLine, home.AnswerRuleExamples.Where(e => e.Label is "STAR" or "Concise" or "Realistic").Select(e => e.Rule));
                Save(window, 1180, 1500, Path.Combine(dir, $"17-home-answer-rules-{name}.png"));
                home.AnswerRules = "";

                var firstRun = new MainViewModel(home, settingsVm, learn, new MemorySettings(new AppSettings()));
                main.CurrentPage = home;
                var firstRunWindow = new MainWindow(firstRun);
                Save(firstRunWindow, 1180, 520, Path.Combine(dir, $"16-no-key-{name}.png"));
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
            // Draw exactly the window's rectangle at its own scale. A plain VisualBrush stretches the bounds of every descendant,
            // including scrolled-away content, into the picture, which rescales it.
            var area = new Rect(0, 0, width, height);
            var brush = new VisualBrush(root)
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = area, ViewportUnits = BrushMappingMode.Absolute, Viewport = area,
            };
            dc.DrawRectangle(brush, null, area);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
