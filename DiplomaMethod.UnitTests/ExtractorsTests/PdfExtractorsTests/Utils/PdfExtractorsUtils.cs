using DiplomaMethod.Core.Models.Documents;

namespace DiplomaMethod.UnitTests.ExtractorsTests.PdfExtractorsTests.Utils;

public static class PdfExtractorsUtils
{
    public static LayoutImage CreateTestLayoutImage(string fileName, int pageNumber)
    {
        var testDataPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, 
            "TestData", 
            fileName);
    
        if (!File.Exists(testDataPath))
            throw new FileNotFoundException($"Test file not found: {testDataPath}");
    
        return new LayoutImage
        {
           ImageStream = File.OpenRead(testDataPath),
           Page = new PageInfo { PageNumber = pageNumber }
        };
    }
}
