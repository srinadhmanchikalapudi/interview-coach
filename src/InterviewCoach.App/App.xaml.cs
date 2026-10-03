using System.Windows;
using System.Windows.Threading;
using InterviewCoach.App.Services;
using InterviewCoach.App.ViewModels;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Infrastructure.Documents;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Llm;
using InterviewCoach.Infrastructure.Persistence;
using InterviewCoach.Infrastructure.Prompts;
using InterviewCoach.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InterviewCoach.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _host = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory })
            .ConfigureInterviewCoach()
            .Build();
        await _host.StartAsync();

        try
        {
            Database.Migrate(_host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>());
            await _host.Services.GetRequiredService<HomeViewModel>().InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The profile database could not be opened:\n\n{ex.Message}\n\nLocation: {Database.DefaultPath}",
                "Interview Coach", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }

    // Something nobody expected: shown above the page (so the work on screen is not lost behind a dialog) when the window is up, in a dialog before that.
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_host?.Services.GetService<MainViewModel>() is { } main && MainWindow is { IsLoaded: true })
            main.ReportError($"Something unexpected went wrong: {e.Exception.Message}");
        else
            MessageBox.Show(e.Exception.Message, "Interview Coach", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

internal static class ServiceRegistration
{
    public static HostApplicationBuilder ConfigureInterviewCoach(this HostApplicationBuilder builder)
    {
        var s = builder.Services;

        s.AddSingleton<ISettingsStore, SettingsStore>(_ => new SettingsStore());
        s.AddSingleton<IClock, SystemClock>();
        s.AddSingleton<IPromptLibrary>(_ => new PromptLibrary());

        s.AddSingleton<IChatClientFactory, ChatClientFactory>();
        s.AddSingleton<IOpenRouterCatalog>(_ => new OpenRouterCatalog(new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) }));
        s.AddSingleton<LlmService>(sp => new LlmService(sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<IChatClientFactory>()));
        s.AddSingleton<FakeLlmService>(_ => new FakeLlmService());
        s.AddSingleton<ILlmService, RoutingLlmService>();

        s.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(Database.ConnectionString(Database.DefaultPath)));
        s.AddSingleton<IProfileRepository, ProfileRepository>();
        s.AddSingleton<TechBankRepository>();
        s.AddSingleton<InterviewCoach.Infrastructure.Fakes.InMemoryTechBankRepository>();
        s.AddSingleton<ITechBankRepository, RoutingTechBankRepository>(); // Demo mode uses a throwaway in-memory bank
        s.AddSingleton<TechBank>(sp => new TechBank(
            sp.GetRequiredService<ITechBankRepository>(), sp.GetRequiredService<ILlmService>(), sp.GetRequiredService<IPromptLibrary>()));
        s.AddSingleton<LearnHistoryRepository>();
        s.AddSingleton<InterviewCoach.Infrastructure.Fakes.InMemoryLearnHistory>(_ => new InterviewCoach.Infrastructure.Fakes.InMemoryLearnHistory());
        s.AddSingleton<ILearnHistory, RoutingLearnHistory>(); // the library; Demo mode keeps it in memory only
        s.AddSingleton<PracticeHistoryRepository>();
        s.AddSingleton<InterviewCoach.Infrastructure.Fakes.InMemoryPracticeHistory>(_ => new InterviewCoach.Infrastructure.Fakes.InMemoryPracticeHistory());
        s.AddSingleton<IPracticeHistory, RoutingPracticeHistory>(); // practice answers and their feedback, shown in the library
        s.AddSingleton<MockHistoryRepository>();
        s.AddSingleton<InterviewCoach.Infrastructure.Fakes.InMemoryMockHistory>();
        s.AddSingleton<IMockHistory, RoutingMockHistory>(); // finished mock interviews; Demo mode keeps them in memory only
        s.AddSingleton<IDocumentTextExtractor, ResumeTextExtractor>();
        s.AddSingleton<IDialogService, WpfDialogService>();
        s.AddSingleton<ISpeechFactory, InterviewCoach.Infrastructure.Speech.SpeechFactory>(); // dictation and spoken questions (Azure, OpenAI or Windows voices)

        s.AddSingleton<HomeViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<LearnViewModel>();
        s.AddSingleton<LibraryViewModel>();
        s.AddSingleton<PracticeViewModel>();
        s.AddSingleton<ConceptsViewModel>();
        s.AddSingleton<MockViewModel>();
        s.AddSingleton<HistoryViewModel>();
        s.AddSingleton<MainViewModel>();
        s.AddSingleton<MainWindow>();
        return builder;
    }
}
