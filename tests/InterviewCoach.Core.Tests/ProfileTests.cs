using InterviewCoach.Core.Models;

namespace InterviewCoach.Core.Tests;

public class ProfileTests
{
    [Fact]
    public void Empty_profile_is_missing_role_jd_and_resume()
    {
        Assert.Equal(["job role", "job description", "resume"], new CandidateProfile().MissingForStart());
    }

    [Fact]
    public void Whitespace_only_counts_as_missing()
    {
        var p = new CandidateProfile { JobRole = "  ", JobDescription = "\n", ResumeText = "x" };

        Assert.Equal(["job role", "job description"], p.MissingForStart());
    }

    [Fact]
    public void Complete_profile_has_nothing_missing()
    {
        var p = new CandidateProfile { JobRole = "SRE", JobDescription = "jd", ResumeText = "cv" };

        Assert.Empty(p.MissingForStart());
    }
}

public class TextToolsTests
{
    [Fact]
    public void Short_text_is_returned_unchanged()
    {
        Assert.Equal("hello", TextTools.TrimTo("hello", 100));
    }

    [Fact]
    public void Prefers_a_paragraph_boundary()
    {
        var text = new string('a', 80) + "\n\n" + new string('b', 40);

        var trimmed = TextTools.TrimTo(text, 100);

        Assert.Equal(new string('a', 80), trimmed);
    }

    [Fact]
    public void Falls_back_to_a_line_boundary_then_a_hard_cut()
    {
        var withLines = new string('a', 80) + "\n" + new string('b', 40);
        Assert.Equal(new string('a', 80), TextTools.TrimTo(withLines, 100));

        var noBreaks = new string('c', 300);
        Assert.Equal(100, TextTools.TrimTo(noBreaks, 100).Length);
    }

    [Fact]
    public void Does_not_discard_more_than_a_quarter_to_find_a_boundary()
    {
        var text = new string('a', 10) + "\n\n" + new string('b', 200); // the only break is near the very start

        Assert.Equal(100, TextTools.TrimTo(text, 100).Length);
    }
}
