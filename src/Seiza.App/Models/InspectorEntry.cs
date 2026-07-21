namespace Seiza.App.Models;

public sealed class InspectorEntry
{
    public InspectorEntry()
    {
    }

    public InspectorEntry(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
