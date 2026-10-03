using InterviewCoach.Core.Models;
using InterviewCoach.Core.Speech;

namespace InterviewCoach.Core.Tests;

public class VoiceLogicTests
{
    // ---- putting dictated text into the box

    [Theory]
    [InlineData("", 0, "Hello there", "Hello there", 11)]
    [InlineData("Hello", 5, "there", "Hello there", 11)]                           // a space is added before
    [InlineData("Hello ", 6, "there", "Hello there", 11)]                           // but not twice
    [InlineData("Hello\n", 6, "there", "Hello\nthere", 11)]
    [InlineData("  Start of the answer. ", 23, "  and more  ", "  Start of the answer. and more", 31)]    // the segment is trimmed
    public void A_dictated_segment_goes_in_at_the_cursor_with_spaces_only_where_needed(string text, int caret, string segment, string expected, int expectedCaret)
    {
        var result = DictationText.Insert(text, caret, segment);

        Assert.Equal(expected, result.Text);
        Assert.Equal(expectedCaret, result.Caret);
    }

    [Fact]
    public void Dictating_in_the_middle_keeps_what_comes_after_and_moves_the_cursor_to_the_end_of_the_new_words()
    {
        var result = DictationText.Insert("First part. Last part.", 11, "the middle bit");

        Assert.Equal("First part. the middle bit Last part.", result.Text);
        Assert.Equal("First part. the middle bit".Length, result.Caret);   // right after the new words; the existing space follows
    }

    [Fact]
    public void A_space_is_not_put_before_punctuation_that_follows()
    {
        var result = DictationText.Insert("I said hello", 12, "world");     // cursor at the end
        Assert.Equal("I said hello world", result.Text);

        var before = DictationText.Insert("Wait. Really?", 4, "right");      // cursor before the full stop
        Assert.Equal("Wait right. Really?", before.Text);
    }

    [Fact]
    public void An_empty_segment_changes_nothing_and_a_cursor_out_of_range_is_pulled_back()
    {
        var same = DictationText.Insert("Keep this", 4, "   ");
        Assert.Equal("Keep this", same.Text);
        Assert.Equal(4, same.Caret);

        var clamped = DictationText.Insert("abc", 99, "def");
        Assert.Equal("abc def", clamped.Text);
        Assert.Equal(7, clamped.Caret);

        Assert.Equal("only this", DictationText.Insert("", -5, "only this").Text);
        Assert.Equal("x", DictationText.Insert(null!, 0, "x").Text);
    }

    // ---- how the answer was given

    [Fact]
    public void An_untouched_answer_counts_as_typed()
    {
        Assert.Equal("typed", new AnswerInputTracker().Method);
    }

    [Fact]
    public void Speech_alone_is_voice_and_typing_alone_is_typed_and_both_together_are_mixed()
    {
        var voice = new AnswerInputTracker();
        voice.NoteVoice();
        Assert.Equal("voice", voice.Method);
        Assert.True(voice.UsedVoice);

        var typed = new AnswerInputTracker();
        typed.NoteTyped();
        Assert.Equal("typed", typed.Method);

        var both = new AnswerInputTracker();
        both.NoteVoice();
        both.NoteTyped();
        Assert.Equal("mixed", both.Method);
    }

    [Fact]
    public void Starting_a_new_answer_forgets_how_the_last_one_was_given()
    {
        var tracker = new AnswerInputTracker();
        tracker.NoteVoice();
        tracker.NoteTyped();

        tracker.Reset();

        Assert.Equal("typed", tracker.Method);
        Assert.False(tracker.UsedVoice);
    }

    // ---- silence auto-submit

    private sealed class Clock
    {
        public DateTime Now { get; set; } = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }

    [Fact]
    public void It_submits_only_after_enough_words_and_enough_silence_and_only_once()
    {
        var clock = new Clock();
        var detector = new SilenceDetector(() => clock.Now);
        detector.Start();

        clock.Advance(10);
        Assert.False(detector.ShouldSubmit(true, 6, wordCount: 9));        // too few words, however long the silence
        Assert.True(detector.ShouldSubmit(true, 6, wordCount: 10));        // ten words and ten silent seconds
        Assert.False(detector.ShouldSubmit(true, 6, wordCount: 50));       // already asked once this session
    }

    [Fact]
    public void Any_speech_restarts_the_silence()
    {
        var clock = new Clock();
        var detector = new SilenceDetector(() => clock.Now);
        detector.Start();
        clock.Advance(5);
        detector.NoteSpeech();
        clock.Advance(5);

        Assert.False(detector.ShouldSubmit(true, 6, 40));                  // only 5 seconds since the last words
        clock.Advance(1);
        Assert.True(detector.ShouldSubmit(true, 6, 40));
    }

    [Fact]
    public void It_does_nothing_when_the_setting_is_off_or_when_not_listening()
    {
        var clock = new Clock();
        var detector = new SilenceDetector(() => clock.Now);

        clock.Advance(60);
        Assert.False(detector.ShouldSubmit(true, 6, 40));                  // never started

        detector.Start();
        clock.Advance(60);
        Assert.False(detector.ShouldSubmit(false, 6, 40));                 // switched off
        detector.Stop();
        Assert.False(detector.ShouldSubmit(true, 6, 40));                  // stopped
    }

    [Fact]
    public void A_new_listening_session_can_submit_again()
    {
        var clock = new Clock();
        var detector = new SilenceDetector(() => clock.Now);
        detector.Start();
        clock.Advance(10);
        Assert.True(detector.ShouldSubmit(true, 6, 20));

        detector.Start();
        clock.Advance(10);

        Assert.True(detector.ShouldSubmit(true, 6, 20));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_silly_number_of_seconds_is_treated_as_one(int seconds)
    {
        var clock = new Clock();
        var detector = new SilenceDetector(() => clock.Now);
        detector.Start();

        Assert.False(detector.ShouldSubmit(true, seconds, 20));            // no time has passed
        clock.Advance(1);
        Assert.True(detector.ShouldSubmit(true, seconds, 20));
    }

    // ---- text for a voice

    [Theory]
    [InlineData("A **strong** answer", "A strong answer")]
    [InlineData("Use `OVER(PARTITION BY x)` here", "Use OVER(PARTITION BY x) here")]
    [InlineData("We cut p99 from [300ms] to [your actual p99].", "We cut p99 from 300ms to your actual p99.")]
    [InlineData("Direct answer → the constraint → result", "Direct answer , then the constraint , then result")]
    [InlineData("Two  spaces\n\nand lines", "Two spaces and lines")]
    [InlineData("It works — mostly", "It works , mostly")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Text_is_tidied_before_it_is_read_aloud(string? text, string expected)
    {
        Assert.Equal(expected, SpeechText.ForSpeaking(text));
    }

    // ---- readiness and the new settings

    [Fact]
    public void Voice_settings_have_sensible_defaults()
    {
        var s = new AppSettings();

        Assert.True(s.SpeakQuestions);
        Assert.Equal(TtsProvider.Windows, s.TextToSpeech);   // needs no key, so questions can be spoken from the first run
        Assert.True(s.AutoListen);
        Assert.False(s.SilenceAutoSubmit);
        Assert.Equal(6, s.SilenceSeconds);
        Assert.Equal(1.0, s.SpeakingRate);
    }
}
