using InterviewCoach.Core.Models;
using InterviewCoach.Core.Prompts;

namespace InterviewCoach.Core.Tests;

public class CoachTextTests
{
    [Fact]
    public void Placeholders_are_split_out_of_the_answer()
    {
        var pieces = CoachText.SplitPlaceholders("p99 went from [300ms] to [your actual after], and we shipped.");

        Assert.Equal(
            [("p99 went from ", false), ("[300ms]", true), (" to ", false), ("[your actual after]", true), (", and we shipped.", false)],
            pieces.Select(p => (p.Text, p.IsPlaceholder)));
    }

    [Fact]
    public void Text_without_placeholders_is_one_plain_piece_and_pieces_rejoin_to_the_original()
    {
        var plain = CoachText.SplitPlaceholders("Nothing to fill in here.");
        Assert.Equal([("Nothing to fill in here.", false)], plain.Select(p => (p.Text, p.IsPlaceholder)));

        const string text = "[A] mid [B] end";
        Assert.Equal(text, string.Concat(CoachText.SplitPlaceholders(text).Select(p => p.Text)));
    }

    [Fact]
    public void Brackets_that_are_empty_or_span_lines_are_not_placeholders()
    {
        var pieces = CoachText.SplitPlaceholders("an empty [] pair and [a broken\nline] one");

        Assert.All(pieces, p => Assert.False(p.IsPlaceholder));
    }

    [Fact]
    public void Shape_is_split_on_arrows_and_tolerates_ascii_arrows()
    {
        Assert.Equal(["Direct answer", "the constraint", "result"], CoachText.SplitShape("Direct answer → the constraint → result"));
        Assert.Equal(["a", "b", "c"], CoachText.SplitShape("a -> b => c"));
        Assert.Equal(["just one step"], CoachText.SplitShape("just one step"));
        Assert.Empty(CoachText.SplitShape(""));
    }
}

public class QuestionTypesTests
{
    [Fact]
    public void No_filter_or_every_type_renders_as_Any()
    {
        Assert.Equal("Any", QuestionTypes.RenderFilter([]));
        Assert.Equal("Any", QuestionTypes.RenderFilter(QuestionTypes.All.ToList()));
    }

    [Fact]
    public void A_filter_renders_one_prompt_id_per_line()
    {
        var rendered = QuestionTypes.RenderFilter([QuestionType.Behavioral, QuestionType.ResumeDeepDive, QuestionType.Behavioral]);

        Assert.Equal("- resume_deep_dive\n- behavioral", rendered);
    }

    [Fact]
    public void Ids_match_the_names_used_in_the_question_generator_prompt()
    {
        Assert.Equal(
            ["tell_me_about_yourself", "resume_deep_dive", "technical_concept", "system_design", "coding_talkthrough", "behavioral", "scenario", "motivation_fit", "engagement"],
            QuestionTypes.All.Select(t => t.Id()));
    }

    [Theory]
    [InlineData("behavioral", "Behavioral")]
    [InlineData("RESUME_DEEP_DIVE", "Resume deep-dive")]
    [InlineData("something_new", "Something new")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Model_returned_ids_get_readable_labels(string? id, string expected)
    {
        Assert.Equal(expected, QuestionTypes.LabelFor(id));
    }

    [Fact]
    public void Already_asked_is_numbered_or_none()
    {
        Assert.Equal("(none)", PromptVars.AlreadyAsked([]));
        Assert.Equal("1. First?\n2. Second?", PromptVars.AlreadyAsked(["First?", "Second?"]));
    }
}
