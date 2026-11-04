using DoMa.App.UI;

namespace DoMa.App.Tests;

public class SizeFormatterTests
{
    [Theory]
    [InlineData(null, "Không rõ")]
    [InlineData(-1L, "Không rõ")]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1536L, "1,5 KB")]
    [InlineData(5L * 1024 * 1024, "5,0 MB")]
    [InlineData(6227020800L, "5,8 GB")]
    public void formats_with_vietnamese_decimal_comma(long? bytes, string expected)
    {
        Assert.Equal(expected, SizeFormatter.Format(bytes));
    }
}
