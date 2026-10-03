using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;

namespace InterviewCoach.Core.Tests;

// What the debrief and the coach are told that is not the words themselves (found in the first real mock interview log).
public partial class MockEngineTests
{
    [Fact]
    public async Task A_round_the_user_cut_short_is_described_to_the_debrief_so_unasked_topics_are_not_held_against_them()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 15);
        await Speak(h.Engine);
        h.Clock.Advance(100);
        await h.Engine.SubmitAnswerAsync("Good, thanks.", AnswerInputMethod.Voice, 6);
        await Speak(h.Engine);                                               // the main question is on screen
        h.Clock.Advance(170);
        await h.Engine.SubmitAnswerAsync("I put Redis in front of the hot lookups.", AnswerInputMethod.Voice, 50);
        await Speak(h.Engine);                                               // the follow-up is on screen
        await h.Engine.EndNowAsync();

        var prompt = Assert.Single(h.Script.Debriefs).SystemPrompt;

        Assert.Contains("<round_facts>", prompt);
        Assert.Contains("Planned length: 15 minutes. The round lasted 4:30, which is 30% of the planned time.", prompt);
        Assert.Contains("The candidate ended the round before the interviewer closed it.", prompt);
        Assert.Contains("1 main question was asked and 1 has an answer.", prompt);
        Assert.Contains("Less than half of the planned time was used.", prompt);
        Assert.DoesNotContain("ran past its planned length", prompt);
    }

    [Fact]
    public async Task A_round_the_interviewer_closed_says_so_and_does_not_claim_it_was_short()
    {
        var h = Standard();
        h.Script.Turns.Clear();
        h.Script.Turns.Enqueue(Turn("Hello.", "smalltalk", "opener"));
        h.Script.Turns.Enqueue(Turn("Walk me through it.", "main_question"));
        h.Script.Turns.Enqueue(Turn("Thanks.", "closing", end: true));
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 15);
        await Speak(h.Engine);
        h.Clock.Advance(500);
        await h.Engine.SubmitAnswerAsync("Hi.");
        await Speak(h.Engine);
        h.Clock.Advance(450);
        await h.Engine.SubmitAnswerAsync("An answer.");
        await Speak(h.Engine);

        var prompt = Assert.Single(h.Script.Debriefs).SystemPrompt;

        Assert.Contains("The interviewer closed the round.", prompt);
        Assert.Contains("which is 106% of the planned time.", prompt);
        Assert.Contains("The round ran past its planned length.", prompt);
        Assert.DoesNotContain("Less than half", prompt);
        Assert.DoesNotContain("candidate ended the round", prompt);
    }

    [Fact]
    public async Task Each_spoken_answer_is_labelled_with_its_own_time_for_the_coach_but_not_in_the_debrief_transcript()
    {
        var h = Standard();
        await RunStandardInterview(h);                                       // answers of 40 s and 20 s, spoken

        var coach = Assert.Single(h.Script.Coaches).SystemPrompt;
        var debrief = Assert.Single(h.Script.Debriefs).SystemPrompt;

        Assert.Contains("You (40s): I put Redis in front of the hot lookups and cut p99 by sixty percent.", coach);
        Assert.Contains("You (20s): I compared p99 before and after on the dashboard.", coach);
        Assert.Contains("duration_seconds=\"60\"", coach);
        Assert.Contains("You: I put Redis in front of the hot lookups and cut p99 by sixty percent.", debrief);
        Assert.DoesNotContain("You (40s)", debrief);
    }

    [Fact]
    public async Task A_typed_answer_in_a_thread_is_not_labelled_with_a_time()
    {
        var h = new Harness();
        h.Script.Turns.Enqueue(Turn("Walk me through it.", "main_question"));
        h.Script.Turns.Enqueue(Turn("Thanks.", "closing", end: true));
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("I typed this.", AnswerInputMethod.Typed, 90);
        await Speak(h.Engine);
        await h.Engine.WhenCoachedAsync();

        var coach = Assert.Single(h.Script.Coaches).SystemPrompt;

        Assert.Contains("You: I typed this.", coach);
        Assert.DoesNotContain("You (90s)", coach);
    }

    [Fact]
    public async Task The_coach_is_given_the_usual_answer_length_for_the_kind_of_question_as_in_Practice()
    {
        var h = new Harness();
        h.Script.Turns.Enqueue(Turn("Tell me about yourself.", "main_question", "tell_me_about_yourself"));
        h.Script.Turns.Enqueue(Turn("Thanks.", "closing", end: true));
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("I am a backend engineer.");
        await Speak(h.Engine);
        await h.Engine.WhenCoachedAsync();

        var coach = Assert.Single(h.Script.Coaches).SystemPrompt;

        Assert.Contains("Question type: tell_me_about_yourself", coach);
        Assert.DoesNotContain("Requested model answer length: (none)", coach);
        Assert.Contains("130", coach);                                       // the range for this kind of question
    }
}
