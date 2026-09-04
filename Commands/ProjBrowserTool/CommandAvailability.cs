using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace IssueBuddy
{
    /// <summary>
    /// Hides the context-menu item unless the user has at least one printable
    /// view or sheet in their current selection.
    /// </summary>
    public class CommandAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        {
            var uidoc = applicationData?.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return false;

            // A family document also has views that report CanBePrinted, but print
            // sets do not exist there — hide the item rather than let it fail.
            if (doc.IsFamilyDocument) return false;

            var ids = uidoc.Selection.GetElementIds();
            if (ids == null || ids.Count == 0) return false;

            foreach (var id in ids)
            {
                var elem = doc.GetElement(id);
                if (elem is ViewSheet) return true;
                if (elem is View v && !v.IsTemplate && v.CanBePrinted) return true;
            }
            return false;
        }
    }
}
