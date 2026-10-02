using InterviewCoach.App.ViewModels;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.Tests;

public class CoachOutputViewModelTests
{
    private static CoachOutput Coach(string answer) => new()
    {
        WhatTheyreTesting = "signal",
        ModelAnswer = answer,
        Shape = "A → B",
    };

    private static readonly string TwoHundredWords = string.Join(' ', Enumerable.Repeat("word", 200));

    [Fact]
    public void Shows_the_word_count_and_spoken_time_of_the_model_answer()
    {
        var vm = new CoachOutputViewModel(Coach(TwoHundredWords), _ => { }, "behavioral");

        Assert.Equal(200, vm.WordCount);
        Assert.Equal("200 words, about 1 min 30 sec spoken", vm.LengthSummary);
    }

    [Fact]
    public void Says_what_interviewers_expect_for_the_question_type()
    {
        var vm = new CoachOutputViewModel(Coach(TwoHundredWords), _ => { }, "behavioral");

        Assert.True(vm.HasExpectation);
        Assert.Contains("150–280 words", vm.ExpectationText);
        Assert.StartsWith("Interviewers typically expect", vm.ExpectationText);
    }

    [Fact]
    public void Scenario_questions_have_their_own_expectation()
    {
        var vm = new CoachOutputViewModel(Coach("short"), _ => { }, "scenario");

        Assert.Contains("120–220 words", vm.ExpectationText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a_type_the_model_invented")]
    public void No_expectation_line_when_the_type_is_unknown(string? type)
    {
        var vm = new CoachOutputViewModel(Coach("short"), _ => { }, type);

        Assert.False(vm.HasExpectation);
        Assert.Null(vm.ExpectationText);
    }

    [Fact]
    public void An_empty_answer_does_not_crash_the_summary()
    {
        var vm = new CoachOutputViewModel(Coach(""), _ => { }, "behavioral");

        Assert.Equal(0, vm.WordCount);
        Assert.Contains("0 words", vm.LengthSummary);
    }
}
