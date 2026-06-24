namespace LosslessChecker.Models;

public class MetricItem
{
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Status { get; set; } = "";
    public string StatusColor { get; set; } = "#585b70";
    public string Description { get; set; } = "";
    public string Typical { get; set; } = "";
    public bool IsHeader { get; set; }
}
