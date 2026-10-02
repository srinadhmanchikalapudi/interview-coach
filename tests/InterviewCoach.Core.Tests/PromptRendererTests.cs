using InterviewCoach.Core.Prompts;

namespace InterviewCoach.Core.Tests;

public class PromptRendererTests
{
    [Fact]
    public void Fills_every_placeholder()
    {
        var result = PromptRenderer.Render("Role: {{JOB_ROLE}} / {{SENIORITY}} / {{JOB_ROLE}}",
            new Dictionary<string, string?> { ["JOB_ROLE"] = "Backend Engineer", ["SENIORITY"] = "Senior" });

        Assert.Equal("Role: Backend Engineer / Senior / Backend Engineer", result);
    }

    [Fact]
    public void Missing_variable_throws_and_names_it()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PromptRenderer.Render("{{A}} {{B}}", new Dictionary<string, string?> { ["A"] = "x" }));

        Assert.Contains("B", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Absent_value_renders_as_none(string? value)
    {
        var result = PromptRenderer.Render("Previous: {{PREVIOUS_ATTEMPT}}",
            new Dictionary<string, string?> { ["PREVIOUS_ATTEMPT"] = value });

        Assert.Equal("Previous: (none)", result);
    }

    [Fact]
    public void Values_containing_placeholder_syntax_are_inserted_raw()
    {
        // A resume could contain anything, including "{{X}}". It must not be re-expanded or flagged.
        var result = PromptRenderer.Render("<resume>{{RESUME}}</resume>",
            new Dictionary<string, string?> { ["RESUME"] = "uses {{TEMPLATES}} & <xml> tags" });

        Assert.Equal("<resume>uses {{TEMPLATES}} & <xml> tags</resume>", result);
    }

    [Fact]
    public void Json_braces_in_the_template_are_left_alone()
    {
        var result = PromptRenderer.Render("{ \"say\": \"{{TEXT}}\" }", new Dictionary<string, string?> { ["TEXT"] = "hi" });

        Assert.Equal("{ \"say\": \"hi\" }", result);
    }
}
