using DoMa.App.UI;

namespace DoMa.App.Tests;

public class SettingsFormTests
{
    [Fact]
    public void parses_separators_dots_case_and_duplicates()
    {
        Assert.Equal(["pdf", "docx", "zip"], SettingsForm.ParseExtensions(" .PDF, docx;ZIP\tpdf\r\n.docx ,, ."));
    }

    [Fact]
    public void empty_text_gives_no_extensions()
    {
        Assert.Empty(SettingsForm.ParseExtensions("  , ;"));
    }
}
