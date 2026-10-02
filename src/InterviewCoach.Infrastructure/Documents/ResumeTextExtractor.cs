using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using InterviewCoach.Core.Abstractions;
using UglyToad.PdfPig;

namespace InterviewCoach.Infrastructure.Documents;

public sealed partial class ResumeTextExtractor : IDocumentTextExtractor
{
    private const long MaxFileBytes = 20 * 1024 * 1024;

    public IReadOnlyList<string> SupportedExtensions { get; } = [".pdf", ".docx", ".txt", ".md"];

    public Task<string> ExtractTextAsync(string path, CancellationToken ct = default)
        => Task.Run(() => Extract(path, ct), ct);

    private string Extract(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new DocumentExtractionException($"Could not find the file: {path}");

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".doc")
            throw new DocumentExtractionException("Old .doc files are not supported. Save it as .docx or PDF from Word and load that instead.");
        if (!SupportedExtensions.Contains(ext))
            throw new DocumentExtractionException($"'{ext}' files are not supported. Use PDF, DOCX, TXT or MD, or paste the text.");
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new DocumentExtractionException("That file is over 20 MB, which is far too big for a resume or job description.");

        string raw;
        try
        {
            raw = ext switch
            {
                ".pdf" => ReadPdf(path, ct),
                ".docx" => ReadDocx(path),
                _ => File.ReadAllText(path), // detects UTF-8/UTF-16 byte-order marks, defaults to UTF-8
            };
        }
        catch (DocumentExtractionException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (IOException ex)
        {
            throw new DocumentExtractionException($"Could not read the file. Is it open in another program? ({ex.Message})", ex);
        }
        catch (Exception ex)
        {
            throw new DocumentExtractionException($"Could not read this {ext.TrimStart('.').ToUpperInvariant()} file. It may be damaged or password-protected.", ex);
        }

        var text = Normalize(raw);
        if (text.Length == 0)
        {
            throw new DocumentExtractionException(ext == ".pdf"
                ? "No text found in this PDF. It may be a scan or an image. Paste the text instead, or run OCR on it first."
                : "The file has no text in it.");
        }
        return text;
    }

    private static string ReadPdf(string path, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            double? lastBottom = null;
            foreach (var word in page.GetWords())
            {
                // Judge vertical gaps against the font size: a word's bounding box shrinks on lines with no ascenders/descenders.
                var size = word.Letters.Count > 0 ? word.Letters.Max(l => l.PointSize) : word.BoundingBox.Height;
                size = Math.Max(size, 1);
                if (lastBottom is { } last)
                {
                    var drop = Math.Abs(word.BoundingBox.Bottom - last);
                    if (drop > size * 1.7) sb.Append("\n\n");      // paragraph / section gap
                    else if (drop > size * 0.5) sb.Append('\n');   // next line
                    else sb.Append(' ');
                }
                sb.Append(word.Text);
                lastBottom = word.BoundingBox.Bottom;
            }
            sb.Append("\n\n");
        }
        return sb.ToString();
    }

    private static string ReadDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, isEditable: false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return "";

        var sb = new StringBuilder();
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            if (paragraph.ParagraphProperties?.NumberingProperties is not null)
                sb.Append("- ");
            foreach (var element in paragraph.Descendants())
            {
                switch (element)
                {
                    case Text t: sb.Append(t.Text); break;
                    case TabChar: sb.Append('\t'); break;
                    case Break or CarriageReturn: sb.Append('\n'); break;
                }
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Unix line endings, no trailing spaces, at most one blank line in a row, trimmed.</summary>
    internal static string Normalize(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace(" ", " ").Replace("\0", "");
        text = TrailingSpaces().Replace(text, "");
        text = ManyBlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"[ \t]+(?=\n|$)")]
    private static partial Regex TrailingSpaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyBlankLines();
}
