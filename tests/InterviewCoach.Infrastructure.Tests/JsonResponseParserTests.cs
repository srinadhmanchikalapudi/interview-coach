using System.Text.Json;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Llm;

namespace InterviewCoach.Infrastructure.Tests;

public class JsonResponseParserTests
{
    private sealed record Sample(string Say, string? TurnType, bool EndInterview);

    [Fact]
    public void Parses_plain_json_with_snake_case_names()
    {
        var s = JsonResponseParser.Parse<Sample>("""{"say": "Hi there", "turn_type": "smalltalk", "end_interview": false}""");

        Assert.Equal("Hi there", s.Say);
        Assert.Equal("smalltalk", s.TurnType);
        Assert.False(s.EndInterview);
    }

    [Fact]
    public void Strips_json_fences()
    {
        var raw = "```json\n{\"say\": \"Hi\", \"turn_type\": null, \"end_interview\": true}\n```";

        Assert.True(JsonResponseParser.Parse<Sample>(raw).EndInterview);
    }

    [Fact]
    public void Tolerates_leading_and_trailing_prose()
    {
        var raw = "Sure, here you go:\n{\"say\": \"Hi\", \"turn_type\": null, \"end_interview\": false}\nHope that helps!";

        Assert.Equal("Hi", JsonResponseParser.Parse<Sample>(raw).Say);
    }

    [Fact]
    public void Tolerates_trailing_commas_and_wrong_case()
    {
        var s = JsonResponseParser.Parse<Sample>("""{"Say": "Hi", "end_interview": false,}""");

        Assert.Equal("Hi", s.Say);
    }

    [Fact]
    public void Parses_arrays()
    {
        var items = JsonResponseParser.Parse<List<int>>("Here: [1, 2, 3]");

        Assert.Equal([1, 2, 3], items);
    }

    [Fact]
    public void Haiku_reply_with_a_stray_closing_brace_and_commentary_after_the_json_parses_without_a_retry()
    {
        // The shape of a real failure from the debug log: fenced JSON, one "}" too many, then prose.
        var raw = "```json\n{\n  \"say\": \"Hi\",\n  \"turn_type\": null,\n  \"end_interview\": false\n}\n}\n```\n\n---\n\n## What to know before you answer:\n\n**The move:** be honest {really}.";

        var s = JsonResponseParser.Parse<Sample>(raw);

        Assert.Equal("Hi", s.Say);
    }

    [Fact]
    public void Braces_and_escaped_quotes_inside_strings_do_not_confuse_the_extraction()
    {
        var raw = "Here: {\"say\": \"use {braces} and \\\"quotes\\\" and a stray } here\", \"turn_type\": null, \"end_interview\": true} trailing }";

        var s = JsonResponseParser.Parse<Sample>(raw);

        Assert.Equal("use {braces} and \"quotes\" and a stray } here", s.Say);
        Assert.True(s.EndInterview);
    }

    [Fact]
    public void Nested_objects_and_arrays_are_balanced_correctly()
    {
        var items = JsonResponseParser.Parse<Dictionary<string, List<Dictionary<string, int>>>>(
            """{"a": [{"x": 1}, {"y": 2}], "b": []} and then {"another": 1}""");

        Assert.Equal(2, items["a"].Count);
        Assert.Empty(items["b"]);
    }

    [Fact]
    public void Truncated_json_still_fails_so_the_repair_path_can_run()
    {
        Assert.ThrowsAny<JsonException>(() => JsonResponseParser.Parse<Sample>("{\"say\": \"cut off mid-sen"));
    }

    [Fact]
    public void Question_generator_sample_from_the_spec_deserializes()
    {
        var q = JsonResponseParser.Parse<QuestionDto>("""
            {
              "question": "Walk me through how the ranking service worked.",
              "question_type": "resume_deep_dive",
              "source": "resume",
              "focus": "Ranking service ownership"
            }
            """);

        Assert.Equal("Walk me through how the ranking service worked.", q.Question);
        Assert.Equal("resume_deep_dive", q.QuestionType);
        Assert.Equal("resume", q.Source);
        Assert.Equal("Ranking service ownership", q.Focus);
    }

    [Fact]
    public void Coach_sample_from_the_spec_deserializes_including_nulls()
    {
        var c = JsonResponseParser.Parse<CoachOutput>("""
            ```json
            {
              "what_theyre_testing": "Whether you really did it.",
              "feedback": [
                { "kind": "strength", "point": "Named the real system", "quote": "the ranking service" },
                { "kind": "missing", "point": "No numbers", "quote": null }
              ],
              "model_answer": "Yeah, so [Company] had...",
              "shape": "Direct answer → constraint → result",
              "delivery": null,
              "follow_ups": [ { "question": "Why Redis?", "hint": "Name the alternative." } ]
            }
            ```
            """);

        Assert.Equal("Whether you really did it.", c.WhatTheyreTesting);
        Assert.Equal(2, c.Feedback.Count);
        Assert.Equal("strength", c.Feedback[0].Kind);
        Assert.Null(c.Feedback[1].Quote);
        Assert.Null(c.Delivery);
        Assert.Equal("Why Redis?", c.FollowUps.Single().Question);
    }

    [Fact]
    public void Coach_reply_with_missing_fields_still_deserializes_with_safe_defaults()
    {
        var c = JsonResponseParser.Parse<CoachOutput>("""{ "model_answer": "Short." }""");

        Assert.Equal("Short.", c.ModelAnswer);
        Assert.Equal("", c.WhatTheyreTesting);
        Assert.Empty(c.Feedback);
        Assert.Empty(c.FollowUps);
    }

    [Fact]
    public void Garbage_throws_JsonException()
    {
        Assert.ThrowsAny<JsonException>(() => JsonResponseParser.Parse<Sample>("I cannot do that."));
    }
}
