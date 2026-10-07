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
            ToolTip = "Copy the sheet number, then add two sheet parameters to the front of it.",
            LongDescription = "Saves the current main parameter into a destination parameter, then joins two chosen parameters and places them at the start of the main parameter.",
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
