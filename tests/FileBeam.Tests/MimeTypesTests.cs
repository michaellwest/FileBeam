namespace FileBeam.Tests;

public class MimeTypesTests
{
    [Theory]
    [InlineData("file.txt",  "text/plain")]
    [InlineData("file.md",   "text/markdown")]
    [InlineData("file.html", "text/html")]
    [InlineData("file.css",  "text/css")]
    [InlineData("file.js",   "application/javascript")]
    [InlineData("file.json", "application/json")]
    [InlineData("file.xml",  "application/xml")]
    [InlineData("file.pdf",  "application/pdf")]
    [InlineData("file.zip",  "application/zip")]
    [InlineData("file.png",  "image/png")]
    [InlineData("file.jpg",  "image/jpeg")]
    [InlineData("file.jpeg", "image/jpeg")]
    [InlineData("file.gif",  "image/gif")]
    [InlineData("file.webp", "image/webp")]
    [InlineData("file.svg",  "image/svg+xml")]
    [InlineData("file.mp4",  "video/mp4")]
    [InlineData("file.mp3",  "audio/mpeg")]
    [InlineData("file.wav",  "audio/wav")]
    [InlineData("file.csv",  "text/csv")]
    [InlineData("file.log",  "text/plain")]
    public void GetMimeType_KnownExtension_ReturnsCorrectMime(string fileName, string expected)
    {
        Assert.Equal(expected, MimeTypes.GetMimeType(fileName));
    }

    [Theory]
    [InlineData("file.unknown")]
    [InlineData("file.xyz")]
    [InlineData("file")]
    [InlineData("")]
    public void GetMimeType_UnknownExtension_ReturnsOctetStream(string fileName)
    {
        Assert.Equal("application/octet-stream", MimeTypes.GetMimeType(fileName));
    }

    [Theory]
    [InlineData("FILE.TXT")]
    [InlineData("file.TXT")]
    [InlineData("file.Txt")]
    [InlineData("FILE.PDF")]
    [InlineData("FILE.MP4")]
    public void GetMimeType_IsCaseInsensitive(string fileName)
    {
        var mime = MimeTypes.GetMimeType(fileName);
        Assert.NotEqual("application/octet-stream", mime);
    }

    [Fact]
    public void GetMimeType_AcceptsFullPath()
    {
        Assert.Equal("text/plain", MimeTypes.GetMimeType("/some/path/to/file.txt"));
        Assert.Equal("image/png",  MimeTypes.GetMimeType(@"C:\Users\test\image.png"));
    }
}
