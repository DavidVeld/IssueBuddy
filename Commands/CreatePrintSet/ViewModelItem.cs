using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace IssueBuddy
{
    /// <summary>
    /// ViewModel wrapper for a Revit Revision — drives the checkbox list in the UI.
    /// Implements INotifyPropertyChanged so the binding engine sees checkbox state changes.
    /// </summary>
    public class RevisionViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        /// <summary>The underlying Revit Revision element.</summary>
        public Revision Revision { get; set; }

        /// <summary>Formatted display string shown next to the checkbox.</summary>
        public string RevisionName { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Single sheet row class used both in the ListView and when building the print set.
    /// SheetElementId lets the command retrieve sheets by ElementId instead of a fragile
    /// string-number lookup.
    /// </summary>
    public class SheetRow
    {
        public string Number { get; set; }
        public string Name { get; set; }
        public string Rev { get; set; }

        /// <summary>ElementId of the ViewSheet — used in CreatePrintSet.cs to avoid a second collector call.</summary>
        public ElementId SheetElementId { get; set; }
    }
}