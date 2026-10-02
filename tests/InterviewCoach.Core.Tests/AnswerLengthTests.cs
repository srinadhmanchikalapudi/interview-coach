using InterviewCoach.Core.Models;

namespace InterviewCoach.Core.Tests;

public class AnswerLengthTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   \n ", 0)]
    [InlineData("one", 1)]
    [InlineData("Yeah, so I'd start before I open the tool.", 9)]
    [InlineData("line one\nline   two\ttabbed", 5)]
    public void Words_are_counted_by_whitespace(string? text, int expected)
    {
        Assert.Equal(expected, AnswerLength.CountWords(text));
    }

    [Fact]
    public void A_placeholder_counts_as_the_words_inside_it()
    {
        Assert.Equal(6, AnswerLength.CountWords("p99 was [your actual number] then"));
    }

    [Theory]
    [InlineData(130, 60)]
    [InlineData(65, 30)]
    [InlineData(260, 120)]
    [InlineData(0, 0)]
    public void Spoken_time_uses_a_130_words_per_minute_pace(int words, int seconds)
    {
        Assert.Equal(seconds, AnswerLength.SpokenSeconds(words));
    }

    [Theory]
    [InlineData(0, "5 sec")]
    [InlineData(28, "30 sec")]
    [InlineData(45, "45 sec")]
    [InlineData(60, "1 min")]
    [InlineData(69, "1 min 10 sec")]
    [InlineData(120, "2 min")]
    [InlineData(150, "2 min 30 sec")]
    public void Durations_are_rounded_to_five_seconds_and_read_naturally(int seconds, string expected)
    {
        Assert.Equal(expected, AnswerLength.FormatSeconds(seconds));
    }

    [Fact]
    public void No_requested_length_renders_as_absent_so_the_prompt_says_none()
    {
        Assert.Null(AnswerLength.ForPrompt(null));
    }

    [Fact]
    public void A_requested_length_is_described_in_words_and_speaking_time()
    {
        Assert.Equal("about 130 words (roughly 1 min spoken)", AnswerLength.ForPrompt(130));
    }

    [Theory]
    [InlineData(5, AnswerLength.MinCustomWords)]
    [InlineData(150, 150)]
    [InlineData(5000, AnswerLength.MaxCustomWords)]
    public void Custom_lengths_are_kept_within_sensible_bounds(int requested, int expected)
    {
        Assert.Equal(expected, AnswerLength.ClampCustom(requested));
    }

    [Fact]
    public void Every_question_type_has_an_interviewer_expectation()
    {
        foreach (var type in QuestionTypes.All)
        {
            var range = AnswerLength.Expectation(type.Id());
            Assert.True(range is { } r && r.Min > 0 && r.Max > r.Min, $"{type.Id()} has no sensible word range");
        }
    }

    [Fact]
    public void Expectations_match_the_targets_in_the_coach_prompt()
    {
        Assert.Equal((150, 280), AnswerLength.Expectation("behavioral"));
        Assert.Equal((60, 150), AnswerLength.Expectation("technical_concept"));
        Assert.Equal((130, 200), AnswerLength.Expectation("tell_me_about_yourself"));
        Assert.Equal((120, 220), AnswerLength.Expectation("scenario"));
    }

    [Fact]
    public void Unknown_or_missing_types_have_no_expectation()
    {
        Assert.Null(AnswerLength.Expectation("something_new"));
        Assert.Null(AnswerLength.Expectation(null));
        Assert.Null(AnswerLength.Expectation(""));
    }

    [Fact]
    public void Expectation_reads_as_a_word_range_with_speaking_time()
    {
        Assert.Equal("150–280 words, about 1 min 10 sec to 2 min 10 sec spoken", AnswerLength.DescribeExpectation((150, 280)));
    }
}
