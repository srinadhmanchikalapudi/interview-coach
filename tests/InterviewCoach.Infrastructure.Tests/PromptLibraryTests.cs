using InterviewCoach.Core.Prompts;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.Infrastructure.Tests;

public class PromptLibraryTests
{
    private static readonly string[] AllVars =
    [
        "JOB_ROLE", "SENIORITY", "JOB_DESCRIPTION", "RESUME", "ROUND_TYPE", "DURATION", "PLAN_JSON", "QUESTION_TYPES",
        "ALREADY_ASKED", "FOCUS_TECHNOLOGY", "QUESTION_TYPE", "EMPLOYMENT_TYPE", "MODE", "QUESTION", "TRANSCRIPT", "CANDIDATE_ANSWER", "PREVIOUS_ATTEMPT", "INPUT_METHOD", "ANSWER_LENGTH",
        "DURATION_SECONDS", "WORD_COUNT",
    ];

    private static Dictionary<string, string?> FullVars() => AllVars.ToDictionary(v => v, v => (string?)$"<<{v}>>");

    // A directory with no files forces the embedded fallback, which is what ships inside the assembly.
    private static PromptLibrary EmbeddedOnly() => new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

    [Theory]
    [MemberData(nameof(AllPrompts))]
    public void Every_prompt_renders_with_a_full_variable_set_and_leaves_no_placeholder(PromptName name)
    {
        var rendered = EmbeddedOnly().Render(name, FullVars());

        Assert.DoesNotContain("{{", rendered);
        Assert.DoesNotContain("}}", rendered);
        Assert.Contains("<<JOB_ROLE>>", rendered);
    }

    [Theory]
    [MemberData(nameof(AllPrompts))]
    public void Every_prompt_throws_when_a_variable_is_missing(PromptName name)
    {
        var vars = FullVars();
        vars.Remove("JOB_ROLE");

        Assert.Throws<InvalidOperationException>(() => EmbeddedOnly().Render(name, vars));
    }

    [Fact]
    public void Question_generator_prompt_defines_the_scenario_type_and_allows_a_short_setup()
    {
        var rendered = EmbeddedOnly().Render(PromptName.QuestionGenerator, FullVars());

        Assert.Contains("- scenario:", rendered);
        Assert.Contains("one question about what they would do", rendered);
        Assert.Contains("up to three short sentences", rendered);
    }

    [Fact]
    public void Question_generator_asks_for_short_direct_questions_with_one_ask_each()
    {
        var rendered = EmbeddedOnly().Render(PromptName.QuestionGenerator, FullVars());

        Assert.Contains("technical_concept: one direct sentence of about 6 to 18 words", rendered);
        Assert.Contains("What's the difference between checked and unchecked exceptions?", rendered);
        Assert.Contains("Ask one thing only", rendered);
        Assert.Contains("no \"walk me through\"", rendered);
        Assert.Contains("Design a rate limiter for a public API.", rendered);
    }

    [Fact]
    public void Everything_that_changes_per_call_comes_after_the_cached_resume_block_in_the_generator_and_coach_prompts()
    {
        // Prompt caching marks the text up to </candidate_resume>. Per-call values before it would defeat the cache.
        foreach (var name in new[] { PromptName.QuestionGenerator, PromptName.Coach })
        {
            var rendered = EmbeddedOnly().Render(name, FullVars());
            var boundary = rendered.IndexOf("</candidate_resume>", StringComparison.Ordinal);
            Assert.True(boundary > 0, $"{name} has no resume block");
            var cached = rendered[..boundary];
            foreach (var perCall in new[] { "<<MODE>>", "<<QUESTION>>", "<<TRANSCRIPT>>", "<<ALREADY_ASKED>>", "<<QUESTION_TYPES>>", "<<ANSWER_LENGTH>>", "<<FOCUS_TECHNOLOGY>>", "<<CANDIDATE_ANSWER>>" })
                Assert.DoesNotContain(perCall, cached);
        }
    }

    [Fact]
    public void The_coach_guidance_before_the_candidate_header_is_identical_for_every_request_so_it_can_be_cached()
    {
        const string header = "=== THE CANDIDATE AND THE QUESTION ===";
        var first = EmbeddedOnly().Render(PromptName.Coach, FullVars());
        var different = AllVars.ToDictionary(v => v, v => (string?)$"completely different value for {v}");
        different["JOB_ROLE"] = "Data Engineer";
        different["SENIORITY"] = "Junior";
        different["ANSWER_LENGTH"] = null;
        var second = EmbeddedOnly().Render(PromptName.Coach, different);

        Assert.Equal(1, CountOccurrences(first, header));
        var firstFixed = first[..first.IndexOf(header, StringComparison.Ordinal)];
        var secondFixed = second[..second.IndexOf(header, StringComparison.Ordinal)];

        Assert.Equal(firstFixed, secondFixed);              // byte-for-byte the same, whoever the candidate is
        Assert.DoesNotContain("<<", firstFixed);            // no variable leaks into the fixed part
        Assert.DoesNotContain("{{", firstFixed);
        Assert.True(firstFixed.Length > 8_000, "the fixed guidance should be big enough to be worth caching");
    }

    [Fact]
    public void Question_generator_knows_how_full_time_and_contract_interviews_differ_and_defines_the_two_extra_types()
    {
        var rendered = EmbeddedOnly().Render(PromptName.QuestionGenerator, FullVars());

        Assert.Contains("<employment_type>\n<<EMPLOYMENT_TYPE>>\n</employment_type>", rendered.Replace("\r\n", "\n"));
        Assert.Contains("Full-time: fundamentals, ownership, long-term thinking", rendered);
        Assert.Contains("Contract: hands-on depth in the exact stack", rendered);
        Assert.Contains("availability, notice, rate and contract length", rendered);
        Assert.Contains("motivation_fit is for full-time interviews only and engagement is for contract interviews only", rendered);
        Assert.Contains("- motivation_fit: full-time only.", rendered);
        Assert.Contains("- engagement: contract only.", rendered);
        Assert.Contains("\"Any\" means any type that fits the employment type", rendered);
    }

    [Fact]
    public void Employment_type_comes_after_the_cached_resume_block_so_switching_it_does_not_waste_the_cache()
    {
        foreach (var name in new[] { PromptName.QuestionGenerator, PromptName.Coach })
        {
            var rendered = EmbeddedOnly().Render(name, FullVars());
            var boundary = rendered.IndexOf("</candidate_resume>", StringComparison.Ordinal);

            Assert.True(boundary > 0);
            Assert.DoesNotContain("<<EMPLOYMENT_TYPE>>", rendered[..boundary]);
            Assert.Contains("<<EMPLOYMENT_TYPE>>", rendered[boundary..]);
        }
    }

    [Fact]
    public void Coach_prompt_explains_both_kinds_of_interview_and_how_to_answer_the_two_extra_types()
    {
        var rendered = EmbeddedOnly().Render(PromptName.Coach, FullVars());

        Assert.Contains("=== FULL-TIME AND CONTRACT INTERVIEWS ===", rendered);
        Assert.Contains("Full-time: the interviewer is hiring for years", rendered);
        Assert.Contains("Contract: the interviewer is hiring for a deliverable", rendered);
        Assert.Contains("Employment type: <<EMPLOYMENT_TYPE>>", rendered);
        Assert.Contains("- Motivation and fit (full-time interviews)", rendered);
        Assert.Contains("- Availability and engagement (contract interviews)", rendered);
        Assert.Contains("Never invent availability, rates or terms", rendered);   // no made-up personal facts
        Assert.Contains("[your earliest start date]", rendered);
        Assert.Contains("- Motivation and fit: 90–160 words", rendered);
        Assert.Contains("- Availability and engagement: 40–100 words", rendered);
    }

    [Fact]
    public void Coach_prompt_still_ends_by_asking_for_the_json_output_after_the_candidate_details()
    {
        var rendered = EmbeddedOnly().Render(PromptName.Coach, FullVars());

        Assert.True(rendered.IndexOf("=== OUTPUT ===", StringComparison.Ordinal) < rendered.IndexOf("<job_description>", StringComparison.Ordinal));
        Assert.EndsWith("Reply with only the JSON described under OUTPUT.\n", rendered.Replace("\r\n", "\n"));
        Assert.Contains("Role: <<JOB_ROLE>>", rendered);
        Assert.Contains("Seniority: <<SENIORITY>>", rendered);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void Coach_prompt_knows_how_to_coach_scenarios_and_honours_a_requested_length()
    {
        var rendered = EmbeddedOnly().Render(PromptName.Coach, FullVars());

        Assert.Contains("- Scenario (\"what would you do if...\")", rendered);
        Assert.Contains("Requested model answer length: <<ANSWER_LENGTH>>", rendered);
        Assert.Contains("write about that many, never more than 10 percent over", rendered);
        Assert.Contains("If it gives a range (\"between 60 and 150 words\"), stay inside it and aim for the middle", rendered);
        Assert.Contains("Question type: <<QUESTION_TYPE>>", rendered);
        Assert.Contains("Scenario answers: 120–220 words", rendered);
    }

    [Fact]
    public void A_file_on_disk_wins_over_the_embedded_copy()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "planner.md"), "custom {{JOB_ROLE}}");

            var rendered = new PromptLibrary(dir.FullName).Render(PromptName.Planner, new Dictionary<string, string?> { ["JOB_ROLE"] = "SRE" });

            Assert.Equal("custom SRE", rendered);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Absent_values_render_as_none()
    {
        var vars = FullVars();
        vars["PREVIOUS_ATTEMPT"] = null;

        var rendered = EmbeddedOnly().Render(PromptName.Coach, vars);

        Assert.Contains("<previous_attempt>\n(none)\n</previous_attempt>", rendered.Replace("\r\n", "\n"));
    }

    public static IEnumerable<object[]> AllPrompts() => Enum.GetValues<PromptName>().Select(n => new object[] { n });
}
