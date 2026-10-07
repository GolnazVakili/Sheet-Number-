using System.Reflection;
using Autodesk.Revit.UI;

namespace SheetNumber;

public class App : IExternalApplication
{
    public static string AssemblyPath { get; private set; } = string.Empty;

    public Result OnStartup(UIControlledApplication application)
    {
        AssemblyPath = Assembly.GetExecutingAssembly().Location;
        CreateRibbon(application);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private static void CreateRibbon(UIControlledApplication app)
    {
        const string tabName = "Automation";
        try { app.CreateRibbonTab(tabName); }
        catch { /* tab already exists */ }

        RibbonPanel panel = GetOrCreatePanel(app, tabName, "Sheets");

        var buttonData = new PushButtonData(
            "SheetNumber.Update",
            "Sheet\nNumber",
            AssemblyPath,
            "SheetNumber.Commands.UpdateSheetNumberCommand")
        {
            ToolTip = "Read a sheet parameter, add two parameters to the front, and write the result to a destination parameter.",
            LongDescription = "Reads the main parameter, joins two chosen parameters in front of that value, and writes the result to the destination parameter.",
            AvailabilityClassName = "SheetNumber.Commands.ProjectDocumentAvailability"
        };

        panel.AddItem(buttonData);
    }

    private static RibbonPanel GetOrCreatePanel(UIControlledApplication app, string tabName, string panelName)
    {
        foreach (RibbonPanel existing in app.GetRibbonPanels(tabName))
        {
            if (existing.Name.Equals(panelName, StringComparison.OrdinalIgnoreCase))
                return existing;
        }

        return app.CreateRibbonPanel(tabName, panelName);
    }
}
