using System;
using System.Reflection;
using Autodesk.Revit.UI;
using System.Windows.Media.Imaging;

namespace ALIS
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // Create the ribbon tab
                string tabName = "ALIS";
                application.CreateRibbonTab(tabName);

                // Create the ribbon panel
                RibbonPanel panel = application.CreateRibbonPanel(tabName, "Interior Revision");

                // Path of the current assembly
                string assemblyPath = Assembly.GetExecutingAssembly().Location;

                // Create the push button data
                PushButtonData buttonData = new PushButtonData(
                    "ALISButton",                    // Button name
                    "ALIS\nWindow",                  // Button text (line break)
                    assemblyPath,                   // DLL path
                    "ALIS.MyCommand"                 // Full class name
                );

                // Set the button tooltip
                buttonData.ToolTip = "Open Interior Revision System";
                buttonData.LongDescription = "ALIS (Architectural Language Interactive Synthesis): compare and manage interior design changes before and after each revision.";

                // Set the button images (optional)
                // 32x32 pixel image
                // buttonData.LargeImage = new BitmapImage(new Uri("pack://application:,,,/ALIS;component/Resources/icon32.png"));
                // 16x16 pixel image
                // buttonData.Image = new BitmapImage(new Uri("pack://application:,,,/ALIS;component/Resources/icon16.png"));

                // Add the button to the panel
                PushButton button = panel.AddItem(buttonData) as PushButton;

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

    }
}