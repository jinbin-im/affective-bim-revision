using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ALIS
{
    [Transaction(TransactionMode.Manual)]
    public class MyCommand : IExternalCommand
    {
        // Static field so only one window is open (optional)
        public static MyWindow _myWindow = null;

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            try
            {
                // Check whether the window is already open
                if (_myWindow == null || !_myWindow.IsLoaded)
                {
                    // 1. Create the handler instance
                    MyEventHandler handler = new MyEventHandler();

                    // 2. Create the external event (wraps this handler)
                    ExternalEvent exEvent = ExternalEvent.Create(handler);

                    // 3. Create the WPF window (pass the event and handler)
                    _myWindow = new MyWindow(exEvent, handler);

                    // 4. Show the window modeless so Revit stays usable
                    _myWindow.Show();
                }
                else
                {
                    _myWindow.Activate(); // Already open: bring it to the front
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}