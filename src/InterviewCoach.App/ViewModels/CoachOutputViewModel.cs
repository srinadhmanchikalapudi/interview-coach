using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.ViewModels;

public record ShapeStep(string Text, bool ShowArrow);

public record FeedbackItem(string Kind, string Icon, string Label, string Point, string? Quote)
{
    public bool HasQuote => CoachText.CleanOptional(Quote) is not null;
}

public record FollowUpItem(string Question, string Hint, ICommand OpenCommand);

/// <summary>Display model for one Coach reply (spec 4.7). Immutable: a new instance is made for each reply.</summary>
public sealed class CoachOutputViewModel
{
    public CoachOutputViewModel(CoachOutput coach, Action<FollowUp> openFollowUp, string? questionTypeId = null, string? followUpPrompt = null)
    {
        FollowUpPrompt = followUpPrompt ?? "Click one to see how to answer it.";
        WhatTheyreTesting = coach.WhatTheyreTesting;
        ModelAnswer = coach.ModelAnswer;
        WordCount = AnswerLength.CountWords(coach.ModelAnswer);
        var expected = AnswerLength.Expectation(questionTypeId);
        ExpectationText = expected is { } range ? "Interviewers typically expect " + AnswerLength.DescribeExpectation(range) + " for this kind of question." : null;
        Delivery = CoachText.CleanOptional(coach.Delivery);

        var steps = CoachText.SplitShape(coach.Shape);
        ShapeSteps = steps.Select((s, i) => new ShapeStep(s, i < steps.Count - 1)).ToList();

        Feedback = coach.Feedback.Select(ToItem).ToList();
        FollowUps = coach.FollowUps
            .Where(f => !string.IsNullOrWhiteSpace(f.Question))
            .Select(f => new FollowUpItem(f.Question, f.Hint, new RelayCommand(() => openFollowUp(f))))
            .ToList();
    }

    /// <summary>The line under the follow-ups: in Learn they show how to answer, in Practice they become the next question.</summary>
    public string FollowUpPrompt { get; }
    public string WhatTheyreTesting { get; }
    public string ModelAnswer { get; }
    public int WordCount { get; }
    public string LengthSummary => $"{WordCount} words, about {AnswerLength.Spoken(WordCount)} spoken";
    public string? ExpectationText { get; }
    public bool HasExpectation => ExpectationText is not null;
    public string? Delivery { get; }
    public IReadOnlyList<ShapeStep> ShapeSteps { get; }
    public IReadOnlyList<FeedbackItem> Feedback { get; }
    public IReadOnlyList<FollowUpItem> FollowUps { get; }

    // "How your answer landed" has nothing to show in Learn mode, so the card hides itself when there is no feedback.
    public bool HasFeedback => Feedback.Count > 0;
    public bool HasDelivery => Delivery is not null;
    public bool HasShape => ShapeSteps.Count > 0;
    public bool HasFollowUps => FollowUps.Count > 0;

    private static FeedbackItem ToItem(FeedbackPoint p) => p.Kind.Trim().ToLowerInvariant() switch
    {
        "strength" => new FeedbackItem("strength", "✓", "Worked", p.Point, p.Quote),
        "fix" => new FeedbackItem("fix", "!", "Fix", p.Point, p.Quote),
        "missing" => new FeedbackItem("missing", "?", "Missing", p.Point, p.Quote),
        _ => new FeedbackItem("other", "•", "Note", p.Point, p.Quote),
    };
}
