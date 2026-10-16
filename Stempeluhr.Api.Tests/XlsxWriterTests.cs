using System.IO.Compression;
using System.Xml.Linq;
using Stempeluhr.Api.Services;
using Xunit;

namespace Stempeluhr.Api.Tests;

public sealed class XlsxWriterTests
{
    private static byte[] Sample(params object?[] firstRow)
    {
        var sheet = new XlsxSheet("Eins", ["Name", "Wert"], [20, 10]);
        sheet.AddRow(firstRow);
        return XlsxWriter.Write([sheet, new XlsxSheet("Zwei", ["X"], [5]).AddBoldRow("Summe")]);
    }

    [Fact]
    public void Write_ProducesTheOpcPartsAndWellFormedXml()
    {
        var reader = new XlsxTestReader(Sample("Max", 1.5m));

        Assert.Equal(
            ["[Content_Types].xml", "_rels/.rels", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/workbook.xml",
                "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml"],
            reader.PartNames.Order(StringComparer.Ordinal).ToArray());
        foreach (var part in reader.PartNames)
        {
            Assert.NotNull(reader.Part(part).Root);
        }

        var types = reader.Part("[Content_Types].xml").Root!.Elements().Select(element => (string?)element.Attribute("PartName")).ToArray();
        Assert.Contains("/xl/workbook.xml", types);
        Assert.Contains("/xl/worksheets/sheet2.xml", types);
        Assert.Contains("/xl/styles.xml", types);
        Assert.Equal(["Eins", "Zwei"], reader.SheetNames);
    }

    [Fact]
    public void Write_FirstEntryIsContentTypes()
    {
        using var zip = new ZipArchive(new MemoryStream(Sample("a", 1)), ZipArchiveMode.Read);
        Assert.Equal("[Content_Types].xml", zip.Entries[0].FullName);
    }

    [Fact]
    public void Write_FreezesAndBoldsTheHeaderAndSetsColumnWidths()
    {
        var reader = new XlsxTestReader(Sample("Max", 1));
        var sheet = reader.SheetDocument("Eins");
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        var pane = sheet.Descendants(main + "pane").Single();
        Assert.Equal("frozen", (string?)pane.Attribute("state"));
        Assert.Equal("1", (string?)pane.Attribute("ySplit"));
        Assert.Equal(["20", "10"], sheet.Descendants(main + "col").Select(col => (string)col.Attribute("width")!).ToArray());
        Assert.Equal("Name", reader.Cell("Eins", "A1"));
        Assert.True(reader.IsBold("Eins", "A1"));
        Assert.False(reader.IsBold("Eins", "A2"));
        Assert.True(reader.IsBold("Zwei", "A2"));
    }

    [Fact]
    public void Write_WritesTypedCellsWithFormats()
    {
        var sheet = new XlsxSheet("Typen", ["a", "b", "c", "d", "e", "f"], []);
        sheet.AddRow("Text", 7, 1.25m, new DateOnly(2026, 10, 5), new TimeOnly(18, 0), null);
        var reader = new XlsxTestReader(XlsxWriter.Write([sheet]));

        Assert.Equal("Text", reader.Cell("Typen", "A2"));
        Assert.Equal("7", reader.Cell("Typen", "B2"));
        Assert.Equal("1.25", reader.Cell("Typen", "C2"));
        Assert.Equal("0.00", reader.FormatCode("Typen", "C2"));
        // 2026-10-05 is serial 46300 (1900 date system, 1899-12-30 epoch).
        Assert.Equal("46300", reader.Cell("Typen", "D2"));
        Assert.Equal("dd.MM.yyyy", reader.FormatCode("Typen", "D2"));
        Assert.Equal("0.75", reader.Cell("Typen", "E2"));
        Assert.Equal("HH:mm", reader.FormatCode("Typen", "E2"));
        Assert.Null(reader.Cell("Typen", "F2"));
    }

    [Fact]
    public void Write_EscapesMarkupAndKeepsUmlauts()
    {
        const string text = "Müller & Söhne <Küche> \"Straße\" ä ö ü ß €";
        var reader = new XlsxTestReader(Sample(text, 1));

        Assert.Equal(text, reader.Cell("Eins", "A2"));
    }

    [Fact]
    public void Write_DropsCharactersXmlCannotRepresent()
    {
        var reader = new XlsxTestReader(Sample("a\u0000b\u0001c\u000Bd￾e\uD800f\tg\nh😀i", 1));

        Assert.Equal("abcdef\tg\nh😀i", reader.Cell("Eins", "A2"));
    }

    [Fact]
    public void Write_PreservesLeadingAndTrailingSpaces()
    {
        var reader = new XlsxTestReader(Sample("  x  ", 1));

        Assert.Equal("  x  ", reader.Cell("Eins", "A2"));
    }

    [Fact]
    public void ColumnName_CountsPastZ()
    {
        Assert.Equal("A", XlsxWriter.ColumnName(0));
        Assert.Equal("Z", XlsxWriter.ColumnName(25));
        Assert.Equal("AA", XlsxWriter.ColumnName(26));
        Assert.Equal("AZ", XlsxWriter.ColumnName(51));
        Assert.Equal("BA", XlsxWriter.ColumnName(52));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bad/Name")]
    [InlineData("Dies ist ein viel zu langer Blattname")]
    public void Write_RejectsInvalidSheetNames(string name)
    {
        Assert.Throws<ArgumentException>(() => XlsxWriter.Write([new XlsxSheet(name, ["a"], [])]));
    }

    [Fact]
    public void Write_RejectsDuplicateSheetNames()
    {
        Assert.Throws<ArgumentException>(() => XlsxWriter.Write([new XlsxSheet("A", ["a"], []), new XlsxSheet("a", ["a"], [])]));
    }
}
