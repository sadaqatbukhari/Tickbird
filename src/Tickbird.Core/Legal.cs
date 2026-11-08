namespace Tickbird.Core;

/// <summary>
/// The warranty and liability notice. It is shown in the About dialog and written in the README, the help guides
/// and the release notes; a test keeps them the same, so change the wording here and there together.
/// </summary>
public static class Legal
{
    /// <summary>No warranty of any kind.</summary>
    public const string Warranty =
        "The program is provided AS IS with NO WARRANTY OF ANY KIND, INCLUDING THE WARRANTY OF DESIGN, MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE.";

    /// <summary>The provider is not liable for loss of data.</summary>
    public const string Liability =
        "The provider of this tool is not responsible or liable for any loss of data, or any other loss or damage, arising from its use. Back up your data before running commands that change or delete it.";

    /// <summary>The short form, for release notes.</summary>
    public const string Short = "Provided AS IS with no warranty; the provider is not liable for any loss of data.";
}
