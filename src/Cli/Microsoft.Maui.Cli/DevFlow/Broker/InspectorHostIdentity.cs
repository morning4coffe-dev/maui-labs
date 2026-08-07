namespace Microsoft.Maui.Cli.DevFlow.Broker;

/// <summary>
/// The closed set of surfaces that can embed the DevFlow Inspector, and the source-apply trust
/// policy keyed on them.
/// </summary>
/// <remarks>
/// Host identity never selects a layout or a behaviour in the Inspector UI — capabilities do. It is
/// load-bearing in exactly one place: deciding which surface may carry out a reviewed, human
/// confirmed source apply on the local machine. That decision is a positive allowlist so an
/// unrecognised or spoofed host identity fails closed rather than inheriting trust.
/// </remarks>
internal static class InspectorHostIdentity
{
    public const string Browser = "browser";
    public const string VSCode = "vscode";
    public const string Canvas = "canvas";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        Browser,
        VSCode,
        Canvas,
    };

    /// <summary>
    /// Surfaces that can mediate an explicit local source apply through a native reviewed diff.
    /// </summary>
    private static readonly HashSet<string> NativeApplyHosts = new(StringComparer.Ordinal)
    {
        VSCode,
    };

    public static bool IsKnown(string? hostId) =>
        !string.IsNullOrWhiteSpace(hostId) && Known.Contains(hostId);

    public static bool CanApplySourceNatively(string? hostId) =>
        !string.IsNullOrWhiteSpace(hostId) && NativeApplyHosts.Contains(hostId);

    /// <summary>
    /// Explains why a surface cannot apply source, naming the surface when it is one we recognise.
    /// </summary>
    public static string DescribeApplyDenial(string? hostId, string artifact) => hostId switch
    {
        Canvas => $"Canvas cannot apply {artifact}; use VS Code or another native local host.",
        Browser => $"A browser tab cannot apply {artifact}; use VS Code or another native local host.",
        _ => $"This host is not a recognised local apply surface and cannot apply {artifact}. "
            + "Preview or download the patch instead.",
    };
}
