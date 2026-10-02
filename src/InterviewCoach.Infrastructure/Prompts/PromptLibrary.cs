using System.Reflection;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Prompts;

namespace InterviewCoach.Infrastructure.Prompts;

/// <summary>Loads prompt files from disk (so they can be edited without recompiling) and falls back to the embedded copy.</summary>
public sealed class PromptLibrary(string? promptsDirectory = null) : IPromptLibrary
{
    private readonly string _directory = promptsDirectory ?? Path.Combine(AppContext.BaseDirectory, "Prompts");

    public string Render(PromptName name, IReadOnlyDictionary<string, string?> vars)
        => PromptRenderer.Render(LoadTemplate(name), vars);

    public string LoadTemplate(PromptName name)
    {
        var file = name.FileName();
        var path = Path.Combine(_directory, file);
        if (File.Exists(path))
            return File.ReadAllText(path);

        var asm = typeof(PromptLibrary).Assembly;
        using var stream = asm.GetManifestResourceStream($"Prompts.{file}")
            ?? throw new FileNotFoundException($"Prompt '{file}' not found on disk ({_directory}) or as an embedded resource.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
