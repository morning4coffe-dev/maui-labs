using System.Xml.Linq;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

public sealed class CliPackagingTests
{
    [Fact]
    public void PackageProject_DeclaresAndPacksCliReadme()
    {
        var project = LoadProject();

        Assert.Equal(
            "README.md",
            project.Descendants().Single(element => element.Name.LocalName == "PackageReadmeFile").Value);
        Assert.Equal(
            "false",
            project.Descendants().Single(element => element.Name.LocalName == "PackRepoRootReadme").Value);

        var readme = Assert.Single(
            project.Descendants(),
            element =>
                element.Name.LocalName == "None" &&
                string.Equals((string?)element.Attribute("Include"), @"..\README.md", StringComparison.Ordinal));
        Assert.Equal("README.md", (string?)readme.Attribute("Link"));
        Assert.Equal("true", (string?)readme.Attribute("Pack"));
        Assert.Equal("/", (string?)readme.Attribute("PackagePath"));
        Assert.DoesNotContain(
            "](../../",
            File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Cli", "README.md")));
    }

    [Fact]
    public void AppProjectReferenceBuildAssets_ArePublishOnlyNonContent()
    {
        var project = LoadProject();
        var assets = Assert.Single(
            project.Descendants(),
            element =>
                element.Name.LocalName == "None" &&
                ((string?)element.Attribute("Include"))?.Contains(
                    "AppProjectReference",
                    StringComparison.Ordinal) == true);

        Assert.Equal("false", (string?)assets.Attribute("Pack"));
        Assert.Equal("PreserveNewest", (string?)assets.Attribute("CopyToOutputDirectory"));
        Assert.Equal("PreserveNewest", (string?)assets.Attribute("CopyToPublishDirectory"));
        Assert.Equal(
            @"Build\AppProjectReference\%(RecursiveDir)%(Filename)%(Extension)",
            (string?)assets.Attribute("Link"));
        Assert.DoesNotContain(
            project.Descendants(),
            element =>
                element.Name.LocalName == "Content" &&
                ((string?)element.Attribute("Include"))?.Contains(
                    "AppProjectReference",
                    StringComparison.Ordinal) == true);
    }

    private static XDocument LoadProject()
    {
        var projectPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Cli",
            "Microsoft.Maui.Cli",
            "Microsoft.Maui.Cli.csproj");
        return XDocument.Load(projectPath);
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
