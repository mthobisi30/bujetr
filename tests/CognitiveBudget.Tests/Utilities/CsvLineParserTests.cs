using CognitiveBudget.Web.Utilities;
using FluentAssertions;
using Xunit;

namespace CognitiveBudget.Tests.Utilities;

public class CsvLineParserTests
{
    [Fact]
    public void Parse_SimpleLine_SplitsOnCommas()
    {
        var fields = CsvLineParser.Parse("2025-01-01,12.50,Coffee,Food");
        fields.Should().Equal("2025-01-01", "12.50", "Coffee", "Food");
    }

    [Fact]
    public void Parse_QuotedFieldWithEmbeddedComma_KeepsItIntact()
    {
        var fields = CsvLineParser.Parse("2025-01-01,12.50,\"Coffee, large\",Food");
        fields.Should().HaveCount(4);
        fields[2].Should().Be("Coffee, large");
    }

    [Fact]
    public void Parse_EscapedQuotes_AreUnescaped()
    {
        var fields = CsvLineParser.Parse("a,\"She said \"\"hi\"\"\",b");
        fields.Should().Equal("a", "She said \"hi\"", "b");
    }

    [Fact]
    public void Parse_TrimsUnquotedWhitespace()
    {
        var fields = CsvLineParser.Parse(" 2025-01-01 , 12.50 , Coffee , Food ");
        fields.Should().Equal("2025-01-01", "12.50", "Coffee", "Food");
    }

    [Fact]
    public void Parse_TrailingEmptyField_IsPreserved()
    {
        var fields = CsvLineParser.Parse("2025-01-01,12.50,Coffee,Food,");
        fields.Should().HaveCount(5);
        fields[4].Should().BeEmpty();
    }
}
