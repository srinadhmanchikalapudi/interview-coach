using InterviewCoach.Core.Models;

namespace InterviewCoach.Core.Abstractions;

public interface IProfileRepository
{
    /// <summary>Most recently updated first.</summary>
    Task<IReadOnlyList<CandidateProfile>> ListAsync(CancellationToken ct = default);
    Task<CandidateProfile?> GetAsync(int id, CancellationToken ct = default);
    /// <summary>Inserts when Id is 0, otherwise updates. Stamps CreatedAt/UpdatedAt and returns the saved copy.</summary>
    Task<CandidateProfile> SaveAsync(CandidateProfile profile, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>Pulls plain text out of a resume or job description file (PDF, DOCX, TXT, MD).</summary>
public interface IDocumentTextExtractor
{
    IReadOnlyList<string> SupportedExtensions { get; }
    /// <exception cref="DocumentExtractionException">The file is unsupported, unreadable, or contains no text.</exception>
    Task<string> ExtractTextAsync(string path, CancellationToken ct = default);
}

/// <summary>A document could not be turned into text; the message is written for the user.</summary>
public class DocumentExtractionException : Exception
{
    public DocumentExtractionException(string message) : base(message) { }
    public DocumentExtractionException(string message, Exception inner) : base(message, inner) { }
}
