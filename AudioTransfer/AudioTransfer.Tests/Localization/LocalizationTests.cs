using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace AudioTransfer.Tests.Localization;

public class LocalizationTests
{
    // Base path assuming test run from bin/Debug/netX.Y-windows/
    private const string EnPath = @"..\..\..\..\AudioTransfer.GUI\Themes\Strings.en.xaml";
    private const string ViPath = @"..\..\..\..\AudioTransfer.GUI\Themes\Strings.vi.xaml";

    private string[] GetKeys(string path)
    {
        var doc = XDocument.Load(path);
        return doc.Descendants()
                  .Select(e => e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value)
                  .Where(k => k != null)
                  .ToArray()!;
    }

    [Fact]
    public void EnglishResourceDictionary_ContainsAllKeys()
    {
        var keys = GetKeys(EnPath);
        Assert.True(keys.Length >= 22, $"Found {keys.Length} keys, expected at least 22.");
    }

    [Fact]
    public void VietnameseResourceDictionary_ContainsAllKeys()
    {
        var keys = GetKeys(ViPath);
        Assert.True(keys.Length >= 22, $"Found {keys.Length} keys, expected at least 22.");
    }

    [Fact]
    public void BothDictionaries_HaveSameKeySet()
    {
        var enKeys = GetKeys(EnPath).OrderBy(k => k).ToArray();
        var viKeys = GetKeys(ViPath).OrderBy(k => k).ToArray();
        
        Assert.Equal(enKeys, viKeys);
    }

    [Fact]
    public void NoKey_ReturnsNull_NotCrash()
    {
        // Actually load a dictionary into memory to test missing keys
        using var fs = new FileStream(EnPath, FileMode.Open, FileAccess.Read);
        var dict = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Load(fs);
        
        var value = dict["SomeNonExistentMissingKey123"];
        Assert.Null(value);
    }
}
