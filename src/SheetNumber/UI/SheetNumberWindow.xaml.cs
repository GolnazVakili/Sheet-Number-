using System.Windows;
using System.Windows.Controls;
using SheetNumber.Models;

namespace SheetNumber.UI;

public partial class SheetNumberWindow : Window
{
    private readonly SheetNumberCatalog _catalog;

    public SheetNumberRequest? Request { get; private set; }

    public SheetNumberWindow(SheetNumberCatalog catalog)
    {
        _catalog = catalog;
        InitializeComponent();

        MainParameterCombo.ItemsSource = catalog.ParameterNames;
        DestinationParameterCombo.ItemsSource = catalog.ParameterNames;
        FirstPrefixCombo.ItemsSource = catalog.ParameterNames;
        SecondPrefixCombo.ItemsSource = catalog.ParameterNames;

        Select(MainParameterCombo, name => name.Equals("Sheet Number", StringComparison.OrdinalIgnoreCase));
        Select(DestinationParameterCombo, name => name.Equals("SheetNumber2", StringComparison.OrdinalIgnoreCase));
        Select(FirstPrefixCombo, name => IsDisciplineOriginator(name) && !IsSubDiscipline(name));
        Select(SecondPrefixCombo, IsSubDiscipline);

        SkipPrefixedCheckBox.IsChecked = true;
        SheetCountText.Text = catalog.SheetCount == 1
            ? "Applies to 1 sheet. One undo reverses it."
            : $"Applies to {catalog.SheetCount} sheets. One undo reverses them.";

        MainParameterCombo.SelectionChanged += (_, _) => UpdatePreview();
        DestinationParameterCombo.SelectionChanged += (_, _) => UpdatePreview();
        FirstPrefixCombo.SelectionChanged += (_, _) => UpdatePreview();
        SecondPrefixCombo.SelectionChanged += (_, _) => UpdatePreview();
        SkipPrefixedCheckBox.Checked += (_, _) => UpdatePreview();
        SkipPrefixedCheckBox.Unchecked += (_, _) => UpdatePreview();

        ApplyButton.IsEnabled = catalog.SheetCount > 0;
        UpdatePreview();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (MainParameterCombo.SelectedItem is not string main
            || DestinationParameterCombo.SelectedItem is not string destination
            || FirstPrefixCombo.SelectedItem is not string first
            || SecondPrefixCombo.SelectedItem is not string second)
        {
            MessageBox.Show(this, "Choose all four parameters before applying.", "Sheet Number", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.Equals(main, destination, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Main Parameter and Destination Parameter have to be different.", "Sheet Number", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Request = new SheetNumberRequest
        {
            MainParameter = main,
            DestinationParameter = destination,
            FirstPrefixParameter = first,
            SecondPrefixParameter = second,
            SkipIfAlreadyPrefixed = SkipPrefixedCheckBox.IsChecked == true
        };

        DialogResult = true;
    }

    private void UpdatePreview()
    {
        string? main = MainParameterCombo.SelectedItem as string;
        string? destination = DestinationParameterCombo.SelectedItem as string;
        string? first = FirstPrefixCombo.SelectedItem as string;
        string? second = SecondPrefixCombo.SelectedItem as string;

        if (string.IsNullOrEmpty(_catalog.PreviewSheetLabel))
        {
            PreviewText.Text = "This project has no sheets to update.";
            return;
        }

        string destLabel = destination ?? "Destination Parameter";
        string mainLabel = main ?? "Main Parameter";
        var lines = new List<string> { _catalog.PreviewSheetLabel };

        if (string.IsNullOrEmpty(main) || !_catalog.PreviewValues.TryGetValue(main, out string? current))
        {
            string missing = string.IsNullOrEmpty(main) ? "(choose a parameter)" : "(not found on this sheet)";
            lines.Add($"{destLabel}  =  {missing}");
            lines.Add($"{mainLabel}  =  {missing}");
            PreviewText.Text = string.Join(Environment.NewLine, lines);
            return;
        }

        if (current.Length == 0)
        {
            lines.Add($"{destLabel}  =  (empty)");
            lines.Add($"{mainLabel}  =  (empty)");
            lines.Add("This sheet will be skipped because the main parameter is empty.");
            PreviewText.Text = string.Join(Environment.NewLine, lines);
            return;
        }

        string prefix = RawValue(_catalog, first) + RawValue(_catalog, second);
        lines.Add($"{destLabel}  =  {current}");
        lines.Add($"{mainLabel}  =  {prefix}{current}");

        if (!string.IsNullOrEmpty(first) && !_catalog.PreviewValues.ContainsKey(first))
            lines.Add($"{first} was not found on this sheet.");
        if (!string.IsNullOrEmpty(second) && !_catalog.PreviewValues.ContainsKey(second))
            lines.Add($"{second} was not found on this sheet.");

        if (SkipPrefixedCheckBox.IsChecked == true
            && prefix.Length > 0
            && current.StartsWith(prefix, StringComparison.Ordinal))
        {
            lines.Add("This sheet already starts with the prefix, so it will be skipped.");
        }

        PreviewText.Text = string.Join(Environment.NewLine, lines);
    }

    private void Select(ComboBox combo, Func<string, bool> predicate)
    {
        string? match = _catalog.ParameterNames.FirstOrDefault(predicate);
        if (match != null)
            combo.SelectedItem = match;
    }

    private static string RawValue(SheetNumberCatalog catalog, string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "";
        return catalog.PreviewValues.TryGetValue(name, out string? value) ? value : "";
    }

    private static bool IsSubDiscipline(string name)
    {
        return name.Contains("Sub-Discipline", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Sub Discipline", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Subdiscipline", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDisciplineOriginator(string name)
    {
        return name.Contains("Discipline", StringComparison.OrdinalIgnoreCase)
               && (name.Contains("Originator", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("Orginator", StringComparison.OrdinalIgnoreCase));
    }
}
