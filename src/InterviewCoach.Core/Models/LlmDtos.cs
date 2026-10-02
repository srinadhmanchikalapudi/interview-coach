namespace InterviewCoach.Core.Models;

// JSON shapes the prompts ask for (spec section 8). Property names map to snake_case in JsonResponseParser.
// Lists default to empty and strings to "" so a model that omits a field does not crash the app.

/// <summary>Reply from question_generator.md.</summary>
public class QuestionDto
{
    public string Question { get; init; } = "";
    public string QuestionType { get; init; } = "";
    public string Source { get; init; } = "";
    public string Focus { get; init; } = "";
}

public class FeedbackPoint
{
    /// <summary>strength | fix | missing</summary>
    public string Kind { get; init; } = "";
    public string Point { get; init; } = "";
    public string? Quote { get; init; }
}

public class FollowUp
{
    public string Question { get; init; } = "";
    public string Hint { get; init; } = "";
}

/// <summary>Reply from coach.md.</summary>
public class CoachOutput
{
    public string WhatTheyreTesting { get; init; } = "";
    public List<FeedbackPoint> Feedback { get; init; } = [];
    public string ModelAnswer { get; init; } = "";
    public string Shape { get; init; } = "";
    public string? Delivery { get; init; }
    public List<FollowUp> FollowUps { get; init; } = [];

    /// <summary>
    /// Learn mode has no candidate answer, so there is nothing to give feedback on (coach.md says to return an empty
    /// feedback array and null delivery). Smaller models sometimes invent feedback anyway; this drops it.
    /// </summary>
    public CoachOutput WithoutFeedback() => new()
    {
        WhatTheyreTesting = WhatTheyreTesting,
        Feedback = [],
        ModelAnswer = ModelAnswer,
        Shape = Shape,
        Delivery = null,
        FollowUps = FollowUps,
    };

    /// <summary>
    /// What Learn mode shows: no invented feedback, and a model answer that starts with the point rather than a warm-up such as
    /// "Sure. Short version:". Applied to fresh replies and to answers saved before the prompt was changed.
    /// </summary>
    public CoachOutput ForLearning()
    {
        var clean = WithoutFeedback();
        return new CoachOutput
        {
            WhatTheyreTesting = clean.WhatTheyreTesting,
            Feedback = clean.Feedback,
            ModelAnswer = CoachText.WithoutOpeningFiller(clean.ModelAnswer),
            Shape = clean.Shape,
            Delivery = clean.Delivery,
            FollowUps = clean.FollowUps,
        };
    }
}
