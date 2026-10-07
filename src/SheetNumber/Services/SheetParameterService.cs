using System.Globalization;
using Autodesk.Revit.DB;
using SheetNumber.Models;

namespace SheetNumber.Services;

public static class SheetParameterService
{
    public static SheetNumberCatalog Load(Document doc, ViewSheet? activeSheet)
    {
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        List<ViewSheet> sheets = CollectSheets(doc);

        if (sheets.Count > 0)
            CollectNames(sheets[0], names);

        foreach (Element instance in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_TitleBlocks)
                     .WhereElementIsNotElementType())
        {
            CollectNames(instance, names);
        }

        foreach (Element type in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_TitleBlocks)
                     .WhereElementIsElementType())
        {
            CollectNames(type, names);
        }

        if (doc.ProjectInformation != null)
            CollectNames(doc.ProjectInformation, names);

        ViewSheet? previewSheet = ResolvePreviewSheet(activeSheet, sheets);
        Dictionary<string, string> previewValues = previewSheet == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ReadAll(doc, previewSheet);

        foreach (string name in previewValues.Keys)
            names.Add(name);

        return new SheetNumberCatalog
        {
            ParameterNames = names.ToList(),
            SheetCount = sheets.Count,
            PreviewSheetLabel = previewSheet == null ? null : SheetLabel(previewSheet),
            PreviewValues = previewValues
        };
    }

    public static SheetNumberApplyResult Apply(Document doc, SheetNumberRequest request)
    {
        List<ViewSheet> sheets = CollectSheets(doc);
        int updated = 0;
        int skippedPrefixed = 0;
        int skippedEmpty = 0;
        var failures = new List<string>();

        using var transaction = new Transaction(doc, "Update sheet numbers");
        transaction.Start();
        FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new QuietWarnings());
        transaction.SetFailureHandlingOptions(options);

        foreach (ViewSheet sheet in sheets)
        {
            string label = SheetLabel(sheet);
            try
            {
                switch (UpdateSheet(doc, sheet, request, out string? failure))
                {
                    case UpdateStatus.Updated:
                        updated++;
                        break;
                    case UpdateStatus.SkippedPrefixed:
                        skippedPrefixed++;
                        break;
                    case UpdateStatus.SkippedEmpty:
                        skippedEmpty++;
                        break;
                    default:
                        failures.Add($"{label}: {failure}");
                        break;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{label}: {ex.Message}");
            }
        }

        bool committed = false;
        if (updated > 0)
        {
            transaction.Commit();
            committed = true;
        }
        else
        {
            transaction.RollBack();
        }

        return new SheetNumberApplyResult
        {
            Updated = updated,
            SkippedPrefixed = skippedPrefixed,
            SkippedEmpty = skippedEmpty,
            Failures = failures,
            ChangesCommitted = committed
        };
    }

    private static UpdateStatus UpdateSheet(Document doc, ViewSheet sheet, SheetNumberRequest request, out string? failure)
    {
        failure = null;

        List<ParamHit> mainHits = FindHits(doc, sheet, request.MainParameter);
        List<ParamHit> destinationHits = FindHits(doc, sheet, request.DestinationParameter);
        List<ParamHit> firstHits = FindHits(doc, sheet, request.FirstPrefixParameter);
        List<ParamHit> secondHits = FindHits(doc, sheet, request.SecondPrefixParameter);

        ParamHit? main = Prefer(mainHits, writable: true);
        if (main == null)
        {
            failure = DescribeMissing(request.MainParameter, mainHits);
            return UpdateStatus.Failed;
        }

        ParamHit? destination = Prefer(destinationHits, writable: true);
        if (destination == null)
        {
            failure = DescribeMissing(request.DestinationParameter, destinationHits);
            return UpdateStatus.Failed;
        }

        if (firstHits.Count == 0)
        {
            failure = $"Could not find {request.FirstPrefixParameter} on this sheet.";
            return UpdateStatus.Failed;
        }

        if (secondHits.Count == 0)
        {
            failure = $"Could not find {request.SecondPrefixParameter} on this sheet.";
            return UpdateStatus.Failed;
        }

        string current = Prefer(mainHits, writable: false)?.Value ?? "";
        if (string.IsNullOrEmpty(current))
            return UpdateStatus.SkippedEmpty;

        string first = Prefer(firstHits, writable: false)?.Value ?? "";
        string second = Prefer(secondHits, writable: false)?.Value ?? "";
        string prefix = first + second;

        if (request.SkipIfAlreadyPrefixed
            && prefix.Length > 0
            && current.StartsWith(prefix, StringComparison.Ordinal))
        {
            return UpdateStatus.SkippedPrefixed;
        }

        string updated = prefix + current;
        string previousDestination = destination.Value;

        // Copy the current value first so the original sheet number is kept,
        // then write the prefixed value back to the main parameter.
        if (!TrySet(destination.Parameter, current, out string? copyError))
        {
            failure = $"Could not write {request.DestinationParameter}. {copyError}";
            return UpdateStatus.Failed;
        }

        if (!TrySet(main.Parameter, updated, out string? prefixError))
        {
            TrySet(destination.Parameter, previousDestination, out _);
            failure = $"Could not write {request.MainParameter}. {prefixError}";
            return UpdateStatus.Failed;
        }

        return UpdateStatus.Updated;
    }

    private static bool TrySet(Parameter parameter, string value, out string? error)
    {
        error = null;
        try
        {
            if (parameter.IsReadOnly)
            {
                error = "The parameter is read-only.";
                return false;
            }

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    if (parameter.Set(value))
                        return true;
                    error = "Revit did not accept the new value.";
                    return false;

                case StorageType.Integer:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                    {
                        error = $"\"{value}\" is not a whole number.";
                        return false;
                    }

                    if (parameter.Set(number))
                        return true;
                    error = "Revit did not accept the new value.";
                    return false;

                default:
                    error = "This parameter type cannot store the value.";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static List<ViewSheet> CollectSheets(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(sheet => !sheet.IsTemplate && !sheet.IsPlaceholder)
            .OrderBy(sheet => sheet.SheetNumber, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static ViewSheet? ResolvePreviewSheet(ViewSheet? activeSheet, List<ViewSheet> sheets)
    {
        if (activeSheet != null && !activeSheet.IsTemplate && !activeSheet.IsPlaceholder)
            return activeSheet;

        return sheets.FirstOrDefault();
    }

    private static Dictionary<string, string> ReadAll(Document doc, ViewSheet sheet)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Element host in Hosts(doc, sheet))
        {
            foreach (Parameter parameter in host.Parameters)
                Accumulate(values, parameter);
        }

        return values;
    }

    private static void Accumulate(Dictionary<string, string> values, Parameter parameter)
    {
        if (!IsListed(parameter))
            return;

        string name = parameter.Definition.Name;
        string value = ReadParameter(parameter);
        if (IsBuiltInSheetNumber(parameter))
        {
            values[name] = value;
            return;
        }

        if (!values.TryGetValue(name, out string? existing)
            || (string.IsNullOrEmpty(existing) && !string.IsNullOrEmpty(value)))
        {
            values[name] = value;
        }
    }

    private static List<ParamHit> FindHits(Document doc, ViewSheet sheet, string parameterName)
    {
        var hits = new List<ParamHit>();
        foreach (Element host in Hosts(doc, sheet))
        {
            Parameter? parameter = host.LookupParameter(parameterName);
            if (parameter == null || !IsListed(parameter))
                continue;

            hits.Add(new ParamHit(parameter, ReadParameter(parameter)));
        }

        return hits;
    }

    private static string DescribeMissing(string parameterName, List<ParamHit> hits)
    {
        if (hits.Count == 0)
            return $"Could not find {parameterName} on this sheet.";

        if (hits.All(hit => IsTypeParameter(hit.Parameter)))
            return $"{parameterName} is a type parameter. Each sheet needs its own instance parameter.";

        return $"{parameterName} is read-only on this sheet.";
    }

    private static ParamHit? Prefer(List<ParamHit> hits, bool writable)
    {
        List<ParamHit> pool = writable
            ? hits.Where(hit => !hit.Parameter.IsReadOnly && !IsTypeParameter(hit.Parameter)).ToList()
            : hits;

        if (pool.Count == 0)
            return null;

        ParamHit? sheetNumber = pool.FirstOrDefault(hit => IsBuiltInSheetNumber(hit.Parameter));
        if (sheetNumber != null)
            return sheetNumber;

        ParamHit? withValue = pool.FirstOrDefault(hit => !string.IsNullOrEmpty(hit.Value));
        return withValue ?? pool[0];
    }

    private static IEnumerable<Element> Hosts(Document doc, ViewSheet sheet)
    {
        foreach (Element titleBlock in new FilteredElementCollector(doc, sheet.Id)
                     .OfCategory(BuiltInCategory.OST_TitleBlocks)
                     .WhereElementIsNotElementType())
        {
            yield return titleBlock;
        }

        yield return sheet;

        if (doc.ProjectInformation != null)
            yield return doc.ProjectInformation;
    }

    private static void CollectNames(Element element, ISet<string> names)
    {
        foreach (Parameter parameter in element.Parameters)
        {
            if (IsListed(parameter))
                names.Add(parameter.Definition.Name);
        }
    }

    private static bool IsListed(Parameter parameter)
    {
        try
        {
            if (parameter.Definition?.Name is not { Length: > 0 })
                return false;

            if (parameter.StorageType is not (StorageType.String or StorageType.Integer))
                return false;

            if (parameter.Definition is InternalDefinition definition
                && definition.GetDataType() == SpecTypeId.Boolean.YesNo)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBuiltInSheetNumber(Parameter parameter)
    {
        return parameter.Definition is InternalDefinition definition
               && definition.BuiltInParameter == BuiltInParameter.SHEET_NUMBER;
    }

    private static bool IsTypeParameter(Parameter parameter)
    {
        try
        {
            return parameter.Element is ElementType;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadParameter(Parameter parameter)
    {
        try
        {
            string raw = parameter.StorageType switch
            {
                StorageType.String => parameter.AsString() ?? "",
                StorageType.Integer => parameter.AsInteger().ToString(CultureInfo.InvariantCulture),
                _ => ""
            };

            return raw.Trim();
        }
        catch
        {
            return "";
        }
    }

    private static string SheetLabel(ViewSheet sheet)
    {
        string number = sheet.SheetNumber?.Trim() ?? "";
        string name = sheet.Name?.Trim() ?? "";
        if (number.Length == 0)
            return name.Length == 0 ? "Sheet" : name;
        if (name.Length == 0)
            return number;
        return $"{number} — {name}";
    }

    private enum UpdateStatus
    {
        Updated,
        SkippedPrefixed,
        SkippedEmpty,
        Failed
    }

    private sealed class ParamHit(Parameter parameter, string value)
    {
        public Parameter Parameter { get; } = parameter;
        public string Value { get; } = value;
    }

    private sealed class QuietWarnings : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                    failuresAccessor.DeleteWarning(failure);
            }

            return FailureProcessingResult.Continue;
        }
    }
}
