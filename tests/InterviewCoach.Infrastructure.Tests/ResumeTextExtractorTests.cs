using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Infrastructure.Documents;
using PdfPageSize = UglyToad.PdfPig.Content.PageSize;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace InterviewCoach.Infrastructure.Tests;

public class ResumeTextExtractorTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();
    private readonly ResumeTextExtractor _extractor = new();

    public void Dispose() => _dir.Delete(true);

    private string PathFor(string file) => Path.Combine(_dir.FullName, file);

    [Fact]
    public async Task Reads_plain_text_and_markdown()
    {
        File.WriteAllText(PathFor("cv.txt"), "Jane Doe\r\nEngineer   \r\n\r\n\r\n\r\nSkills: SQL");
        File.WriteAllText(PathFor("cv.md"), "# Jane Doe\n\n- SQL");

        Assert.Equal("Jane Doe\nEngineer\n\nSkills: SQL", await _extractor.ExtractTextAsync(PathFor("cv.txt")));
        Assert.Equal("# Jane Doe\n\n- SQL", await _extractor.ExtractTextAsync(PathFor("cv.md")));
    }

    [Fact]
    public async Task Extension_check_is_case_insensitive()
    {
        File.WriteAllText(PathFor("CV.TXT"), "hello");

        Assert.Equal("hello", await _extractor.ExtractTextAsync(PathFor("CV.TXT")));
    }

    [Fact]
    public async Task Reads_docx_paragraphs_bullets_and_table_cells()
    {
        var path = PathFor("cv.docx");
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var bullet = new Paragraph(
                new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 })),
                new Run(new Text("Built the ranking service")));
            var table = new Table(new TableRow(
                new TableCell(new Paragraph(new Run(new Text("Python")))),
                new TableCell(new Paragraph(new Run(new Text("Go"))))));
            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Jane Doe"))),
                bullet,
                table));
        }

        var text = await _extractor.ExtractTextAsync(path);

        Assert.Contains("Jane Doe", text);
        Assert.Contains("- Built the ranking service", text);
        Assert.Contains("Python", text);
        Assert.Contains("Go", text);
    }

    [Fact]
    public async Task Reads_pdf_text_and_keeps_lines_apart()
    {
        var path = PathFor("cv.pdf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PdfPageSize.A4);
        page.AddText("Jane Doe", 12, new PdfPoint(50, 780), font);
        page.AddText("Senior Backend Engineer", 12, new PdfPoint(50, 765), font);
        page.AddText("Built the ranking service", 12, new PdfPoint(50, 700), font);
        File.WriteAllBytes(path, builder.Build());

        var text = await _extractor.ExtractTextAsync(path);

        var lines = text.Split('\n');
        Assert.Equal("Jane Doe", lines[0]);
        Assert.Equal("Senior Backend Engineer", lines[1]);
        Assert.Contains("Built the ranking service", text);
    }

    [Fact]
    public async Task Pdf_without_text_gets_a_helpful_message()
    {
        var path = PathFor("scan.pdf");
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PdfPageSize.A4); // a blank page, like a scan with no text layer
        File.WriteAllBytes(path, builder.Build());

        var ex = await Assert.ThrowsAsync<DocumentExtractionException>(() => _extractor.ExtractTextAsync(path));

        Assert.Contains("No text found", ex.Message);
        Assert.Contains("OCR", ex.Message);
    }

    [Fact]
    public async Task Corrupt_files_report_a_friendly_error()
    {
        File.WriteAllText(PathFor("broken.pdf"), "this is not a pdf");
        File.WriteAllText(PathFor("broken.docx"), "this is not a zip");

        await Assert.ThrowsAsync<DocumentExtractionException>(() => _extractor.ExtractTextAsync(PathFor("broken.pdf")));
        await Assert.ThrowsAsync<DocumentExtractionException>(() => _extractor.ExtractTextAsync(PathFor("broken.docx")));
    }

    [Fact]
    public async Task Old_doc_and_unknown_types_are_refused_with_advice()
    {
        File.WriteAllText(PathFor("cv.doc"), "x");
        File.WriteAllText(PathFor("cv.rtf"), "x");

        var doc = await Assert.ThrowsAsync<DocumentExtractionException>(() => _extractor.ExtractTextAsync(PathFor("cv.doc")));
        var rtf = await Assert.ThrowsAsync<DocumentExtractionException>(() => _extractor.ExtractTextAsync(PathFor("cv.rtf")));

        Assert.Contains(".docx", doc.Message);
        Assert.Contains("PDF, DOCX, TXT or MD", rtf.Message);
    }

    [Fact]
    public async Task Empty_and_missing_files_are_reported()
    {
        File.WriteAllText(PathFor("empty.txt"), "  \n\n ");

        await Assert.ThrowsAsync<DocumentExtractionException>(() => _extractor.ExtractTextAsync(PathFor("empty.txt")));
        await Assert.ThrowsAsync<DocumentExtractionException>(() => _extractor.ExtractTextAsync(PathFor("nope.txt")));
    }

    [Fact]
    public async Task Open_file_is_still_readable_when_shared_for_reading()
    {
        // A resume open in Word is locked for writing but not for reading.
        File.WriteAllText(PathFor("open.txt"), "still here");
        using var held = new FileStream(PathFor("open.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.Equal("still here", await _extractor.ExtractTextAsync(PathFor("open.txt")));
    }
}
