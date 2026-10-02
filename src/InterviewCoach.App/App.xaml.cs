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

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
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
        s.AddSingleton<IDocumentTextExtractor, ResumeTextExtractor>();
        s.AddSingleton<IDialogService, WpfDialogService>();

        s.AddSingleton<HomeViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<LearnViewModel>();
        s.AddSingleton<MainViewModel>();
        s.AddSingleton<MainWindow>();
        return builder;
    }
}
