using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Tickbird.Core;
using Tickbird.Desktop.Services;

namespace Tickbird.Desktop.Tests;

public class AboutDialogTests
{
    [AvaloniaFact]
    public void TheAboutDialog_ShowsTheWarrantyAndLiabilityNotice()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            _ = TickbirdDialogs.AboutAsync(owner, "9.9.9", null, null, _ => { });
            var dialog = Assert.Single(owner.OwnedWindows);
            var texts = dialog.GetLogicalDescendants().OfType<TextBlock>().ToDictionary(t => t.Name ?? t.Text ?? "", t => t.Text);

            Assert.Equal(Legal.Warranty, texts["AboutWarranty"]);
            Assert.Equal(Legal.Liability, texts["AboutLiability"]);
            Assert.Contains("Version 9.9.9", texts.Keys);
            Assert.Contains(texts.Values, t => t != null && t.Contains("MIT License"));
            dialog.Close();
        }
        finally { owner.Close(); }
    }
}
