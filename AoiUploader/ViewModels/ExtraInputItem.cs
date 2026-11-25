namespace AoiUploader.ViewModels;

/// <summary>One additional operator input box on the main window (e.g. Work Order).</summary>
public class ExtraInputItem : ObservableObject
{
    private string _value = "";

    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Required { get; init; }

    public string Value { get => _value; set => Set(ref _value, value); }

    public bool IsMissing => Required && string.IsNullOrWhiteSpace(Value);
}
