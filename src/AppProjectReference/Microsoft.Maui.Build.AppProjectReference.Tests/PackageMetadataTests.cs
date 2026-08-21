using System.Xml.Linq;

namespace Microsoft.Maui.Build.AppProjectReference.Tests;

public sealed class PackageMetadataTests
{
    [Fact]
    public void PackageProject_DeclaresAndPacksNuGetReadme()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "AppProjectReference",
            "Microsoft.Maui.Build.AppProjectReference",
            "Microsoft.Maui.Build.AppProjectReference.csproj");
        var project = XDocument.Load(projectPath);

        Assert.Equal(
            "README.md",
            project.Descendants().Single(element => element.Name.LocalName == "PackageReadmeFile").Value);

        var readme = Assert.Single(
            project.Descendants(),
            element =>
                element.Name.LocalName == "None" &&
                string.Equals((string?)element.Attribute("Include"), @"..\README.md", StringComparison.Ordinal));
        Assert.Equal("README.md", (string?)readme.Attribute("Link"));
        Assert.Equal("true", (string?)readme.Attribute("Pack"));
        Assert.Equal("/", (string?)readme.Attribute("PackagePath"));
        Assert.True(File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, @"..\README.md"))));
    }

    [Fact]
    public void PackageProject_PacksBuildAssetsWithoutContentFiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "AppProjectReference",
            "Microsoft.Maui.Build.AppProjectReference",
            "Microsoft.Maui.Build.AppProjectReference.csproj");
        var project = XDocument.Load(projectPath);

        Assert.DoesNotContain(project.Descendants(), element => element.Name.LocalName == "Content");
        foreach (var include in new[] { @"build\**", @"buildTransitive\**" })
        {
            var asset = Assert.Single(
                project.Descendants(),
                element =>
                    element.Name.LocalName == "None" &&
                    string.Equals((string?)element.Attribute("Include"), include, StringComparison.Ordinal));
            Assert.Equal("true", (string?)asset.Attribute("Pack"));
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MauiLabs.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
