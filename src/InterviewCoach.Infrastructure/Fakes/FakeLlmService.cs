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
        var json = responder is not null ? responder(role, systemPrompt) : DefaultResponder(role, systemPrompt, messages);
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

    private string DefaultResponder(LlmRole role, string systemPrompt, IReadOnlyList<ChatTurn> messages)
    {
        if (role == LlmRole.Planner) return DemoPlanJson;
        if (role == LlmRole.Interviewer) return NextInterviewerTurnJson(messages);
        if (role == LlmRole.Debrief) return DemoDebriefJson;
        if (systemPrompt.Contains("List the major technologies a"))
            return "{\"technologies\": [\"C#\", \".NET\", \"SQL Server\", \"Docker\", \"Git\", \"REST APIs\", \"Azure\"]}";
        if (systemPrompt.Contains("List the main technologies this job actually requires"))
            return "{\"technologies\": [\"C#\", \".NET\", \"SQL Server\"]}";
        return role switch
        {
            LlmRole.QuestionGenerator => FocusTechnology.Match(systemPrompt) is { Success: true } m && m.Groups[1].Value != "(none)"
                ? NextTechnologyQuestionJson(m.Groups[1].Value)
                : NextQuestionJson(),
            LlmRole.Coach => systemPrompt.Contains("Mode: mock") ? DemoMockCoachJson : DemoCoachJson,
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

    // A short scripted interview for Demo mode: small talk, two main questions with a follow-up, the candidate's turn to ask, the close.
    private static string NextInterviewerTurnJson(IReadOnlyList<ChatTurn> messages)
    {
        var answers = messages.Count(m => m.Role == ChatTurnRole.User);
        var timeUp = messages.LastOrDefault(m => m.Role == ChatTurnRole.User)?.Content.Contains("Time is up") == true;
        (string Say, string Type, string Phase, bool End) turn = timeUp || answers >= 6
            ? ("Thanks, this was great to chat. The team will be in touch with next steps.", "closing", "candidate_questions", true)
            : answers switch
            {
                1 => ("Hi, thanks for joining. How is your day going?", "smalltalk", "opener", false),
                2 => ("Okay. Walk me through a project you are proud of.", "main_question", "resume_deep_dive", false),
                3 => ("Got it. What was your part in that, specifically?", "follow_up", "resume_deep_dive", false),
                4 => ("Makes sense. How would you track down a slow endpoint in production?", "main_question", "technical", false),
                _ => ("Okay. Do you have any questions for me about the team or the role?", "candidate_questions", "candidate_questions", false),
            };
        return JsonSerializer.Serialize(new { say = turn.Say, turn_type = turn.Type, phase = turn.Phase, focus_area_id = "fa1", end_interview = turn.End });
    }

    private const string DemoPlanJson = """
        {
          "focus_areas": [
            { "id": "fa1", "name": "Ownership of past work", "why": "Demo mode: a sample focus area, not planned from your resume.", "source": "resume" },
            { "id": "fa2", "name": "Debugging in production", "why": "Demo mode: a sample focus area.", "source": "fundamentals" }
          ],
          "resume_claims_to_probe": [ { "claim": "A sample claim from the resume", "probe": "What was your part, specifically?" } ],
          "phases": [
            { "phase": "opener", "target_minutes": 2, "topics": ["introductions"] },
            { "phase": "resume_deep_dive", "target_minutes": 6, "topics": ["a proud project"] },
            { "phase": "technical", "target_minutes": 5, "topics": ["debugging"] },
            { "phase": "candidate_questions", "target_minutes": 2, "topics": ["the team"] }
          ],
          "opening_line": "Hi, thanks for joining. How is your day going?"
        }
        """;

    private const string DemoDebriefJson = """
        {
          "overall_summary": "Demo mode: this is a sample debrief, not a judgement of your answers. With a real key it rates you on the focus areas the interviewer planned and quotes what you said.",
          "hire_signal": "lean_yes",
          "focus_area_ratings": [
            { "focus_area_id": "fa1", "name": "Ownership of past work", "rating": 3, "evidence": "Sample evidence." },
            { "focus_area_id": "fa2", "name": "Debugging in production", "rating": null, "evidence": "This did not come up." }
          ],
          "strengths": ["A sample strength with evidence."],
          "top_fixes": [ { "fix": "Say what you personally did.", "example": "A sample moment.", "how_to_practice": "Answer in three sentences: the problem, your part, the result." } ],
          "practice_next": ["Tell me about a time you debugged a production issue."]
        }
        """;

    private const string DemoMockCoachJson = """
        {
          "what_theyre_testing": "Demo mode: sample coaching for one question of the mock interview.",
          "feedback": [
            { "kind": "strength", "point": "A sample strength.", "quote": null },
            { "kind": "fix", "point": "A sample fix: give the number.", "quote": null }
          ],
          "model_answer": "I started by measuring where the time went. At [Company] we had [a specific problem, with a number], and I [what you personally did]. The result was [your before and after metric].",
          "shape": "Direct answer → what you did → result with a number",
          "delivery": null,
          "follow_ups": [
            { "question": "What would you do differently now?", "hint": "Name one honest change and why." },
            { "question": "How did you know it worked?", "hint": "Give the metric you watched, before and after." }
          ]
        }
        """;

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
