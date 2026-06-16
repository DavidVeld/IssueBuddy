using System.Reflection;
using Autodesk.Revit.UI;

namespace IssueBuddy
{
    /// <summary>
    /// Builds the right-click context-menu entry "Create Print Set From Selection".
    /// Visibility is gated by <see cref="CommandAvailability"/> so the item only shows
    /// when the user has at least one printable view or sheet selected.
    /// </summary>
#if REVIT2025 || REVIT2026 || REVIT2027

    public class PrintSetContextMenuCreator : IContextMenuCreator
    {
        public void BuildContextMenu(ContextMenu menu)
        {
            var assemblyPath = Assembly.GetExecutingAssembly().Location;

            var item = new CommandMenuItem(
                name: "Create Print Set From Selection",
                className: typeof(CreatePrintSetCommand).FullName,
                assemblyName: assemblyPath);

            item.SetAvailabilityClassName(typeof(CommandAvailability).FullName);
            item.SetToolTip("Creates a print set from the views/sheets currently selected.");

            menu.AddItem(item);
        }
    }
#endif
}
