using DoMa.Core.Models;

namespace DoMa.Core.Files;

public class CategoryResolver(IReadOnlyDictionary<Category, string[]> extensions, string rootFolder)
{
    public static IReadOnlyDictionary<Category, string[]> Defaults { get; } = new Dictionary<Category, string[]>
    {
        [Category.Document] = ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "csv", "rtf", "odt", "ods", "epub"],
        [Category.Software] = ["exe", "msi", "msix", "iso", "img", "apk", "appx"],
        [Category.Archive] = ["zip", "rar", "7z", "tar", "gz", "bz2", "xz"],
    };

    public Category Resolve(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.');
        if (ext.Length == 0)
            return Category.Other;
        foreach (var (category, list) in extensions)
        {
            if (list.Contains(ext, StringComparer.OrdinalIgnoreCase))
                return category;
        }
        return Category.Other;
    }

    public string FolderFor(Category c) => Path.Combine(rootFolder, c switch
    {
        Category.Document => "TaiLieu",
        Category.Software => "PhanMem",
        Category.Archive => "Nen",
        _ => "Khac",
    });
}
