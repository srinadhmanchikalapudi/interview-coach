using System.Text.Json;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Llm;

namespace InterviewCoach.Infrastructure.Fakes;

/// <summary>
/// Demo-mode LLM. The responder returns raw JSON for a role and goes through the same parser as the real service.
/// The default answers the Question Generator with a rotating set of generic questions and the Coach with a sample
/// model answer, so Learn mode can be explored without a key. Planner, Interviewer and Debrief replies arrive with Mock mode.
/// </summary>
public sealed class FakeLlmService(Func<LlmRole, string, string>? responder = null) : ILlmService
{
    private static readonly (string Question, string Type, string Source, string Focus)[] DemoQuestions =
    [
        ("Can you tell me a bit about yourself and what brought you to this role?", "tell_me_about_yourself", "behavioral", "Career story"),
        ("Walk me through the project on your resume that you're proudest of.", "resume_deep_dive", "resume", "Project ownership"),
        ("What's the difference between a process and a thread, and when would you pick one over the other?", "technical_concept", "fundamentals", "Concurrency basics"),
        ("Tell me about a time you disagreed with a teammate on a technical decision.", "behavioral", "behavioral", "Conflict and influence"),
        ("How would you design a URL shortener that has to handle a lot of traffic?", "system_design", "jd", "Scalable service design"),
        ("Given a list of meeting times, how would you find out whether any of them overlap?", "coding_talkthrough", "fundamentals", "Problem decomposition"),
    ];

    private int _questionCounter;

    public List<(LlmRole Role, string SystemPrompt, IReadOnlyList<ChatTurn> Messages)> Calls { get; } = [];

    public Task<T> GetJsonAsync<T>(LlmRole role, string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct)
    {
        Calls.Add((role, systemPrompt, messages));
        var json = (responder ?? DefaultResponder)(role, systemPrompt);
        return Task.FromResult(JsonResponseParser.Parse<T>(json));
    }

    public Task<IReadOnlyList<ConnectionTestResult>> TestConnectionAsync(AppSettings settings, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ConnectionTestResult>>(
            [new ConnectionTestResult(LlmRole.Coach, "demo", true, "Demo mode is on: no network calls are made.")]);

    private static readonly string[] TechQuestionTemplates =
    [
        "What is the most common mistake people make with {0}, and how do you avoid it?",
        "How does {0} work under the hood, at a level you could explain to a new teammate?",
        "When would you choose not to use {0}, and what would you pick instead?",
        "What are the main trade-offs to weigh when you use {0} in a production system?",
    ];

    private static readonly System.Text.RegularExpressions.Regex FocusTechnology =
        new(@"<focus_technology>\s*(.+?)\s*</focus_technology>", System.Text.RegularExpressions.RegexOptions.Singleline);

    private string DefaultResponder(LlmRole role, string systemPrompt)
    {
        if (systemPrompt.Contains("List the major technologies a"))
            return "{\"technologies\": [\"C#\", \".NET\", \"SQL Server\", \"Docker\", \"Git\", \"REST APIs\", \"Azure\"]}";
        if (systemPrompt.Contains("List the main technologies this job actually requires"))
            return "{\"technologies\": [\"C#\", \".NET\", \"SQL Server\"]}";
        return role switch
        {
            LlmRole.QuestionGenerator => FocusTechnology.Match(systemPrompt) is { Success: true } m && m.Groups[1].Value != "(none)"
                ? NextTechnologyQuestionJson(m.Groups[1].Value)
                : NextQuestionJson(),
            LlmRole.Coach => DemoCoachJson,
            _ => "{\"ok\": true}",
        };
    }

    private string NextTechnologyQuestionJson(string technology)
    {
        var template = TechQuestionTemplates[(Interlocked.Increment(ref _questionCounter) - 1) % TechQuestionTemplates.Length];
        return JsonSerializer.Serialize(new
        {
            question = string.Format(template, technology),
            question_type = "technical_concept",
            source = "fundamentals",
            focus = technology + " fundamentals",
        });
    }

    private string NextQuestionJson()
    {
        var q = DemoQuestions[(Interlocked.Increment(ref _questionCounter) - 1) % DemoQuestions.Length];
        return JsonSerializer.Serialize(new { question = q.Question, question_type = q.Type, source = q.Source, focus = q.Focus });
    }

    private const string DemoCoachJson = """
        {
          "what_theyre_testing": "Demo mode: this is sample coaching text, not advice written for your resume. With a real key, this explains the signal the interviewer wants at your level.",
          "feedback": [],
          "model_answer": "I picked the simplest thing that could work first. At [Company] we had [a specific problem, with a number], and the reason we didn't just [obvious alternative] was [the real constraint]. What I ended up doing was [what you personally built or decided]. The result was [your actual before/after metric]. Honestly, what I'd change is [one honest thing you'd do differently].",
          "shape": "Direct answer → the constraint → what you chose and why → result with a number → what you'd change",
          "delivery": null,
          "follow_ups": [
            { "question": "Why that approach over the alternatives?", "hint": "Name one real alternative and the specific reason you ruled it out." },
            { "question": "How did you know it worked?", "hint": "Give the metric you watched and what it was before and after." }
          ]
        }
        """;
}
