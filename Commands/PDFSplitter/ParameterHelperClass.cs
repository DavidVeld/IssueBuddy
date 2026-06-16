using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace IssueBuddy
{
    /// <summary>
    /// Resolves a file name template (a list of ParameterClass items) into an
    /// actual file name for a given sheet.
    /// </summary>
    public static class ParameterHelperClass
    {
        /// <summary>
        /// Builds the file name (without extension) for a sheet from a template.
        /// The result is sanitised so it is always a legal Windows file name.
        /// </summary>
        internal static string getFileName(List<ParameterClass> nameTemplate, ViewSheet sheet, UIApplication app)
        {
            if (nameTemplate == null || nameTemplate.Count == 0)
                return "";

            string name = "";
            foreach (ParameterClass parClss in nameTemplate)
            {
                name += getParameterValue(sheet, parClss, app);
            }

            return SanitizeFileName(name);
        }

        internal static string getParameterValue(ViewSheet sheet, ParameterClass parameterToFind, UIApplication app)
        {
            if (parameterToFind == null)
                return "";

            // Separators don't need a document at all.
            if (parameterToFind.TypeName == "Separator")
                return parameterToFind.Name ?? "";

            UIDocument uidoc = app != null ? app.ActiveUIDocument : null;
            Document doc = uidoc != null ? uidoc.Document : null;
            if (doc == null)
                return "";

            if (parameterToFind.TypeName == "ProjectInfo")
            {
                ProjectInfo info = doc.ProjectInformation;
                Parameter param = info != null ? info.LookupParameter(parameterToFind.Name) : null;
                return param != null ? getParameterValue(param) : "";
            }

            if (parameterToFind.TypeName == "ViewSheet")
            {
                Parameter param = sheet != null ? sheet.LookupParameter(parameterToFind.Name) : null;
                return param != null ? getParameterValue(param) : "";
            }

            return "";
        }

        private static string getParameterValue(Parameter param)
        {
            if (param == null || !param.HasValue)
                return "";

            StorageType type = param.StorageType;

            if (type == StorageType.String)
                return param.AsString() ?? "";

            if (type == StorageType.Double)
                return param.AsValueString() ?? "";

            if (type == StorageType.Integer)
                return param.AsInteger().ToString();

            if (type == StorageType.ElementId)
                return param.AsValueString() ?? "";

            return "";
        }

        /// <summary>
        /// Strips characters that Windows does not allow in file names.
        /// Sheet parameters regularly contain things like '/' or ':' — without
        /// this the save silently fails or writes to a wrong path.
        /// </summary>
        internal static string SanitizeFileName(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "";

            char[] invalid = Path.GetInvalidFileNameChars();
            string clean = new string(raw.Where(c => !invalid.Contains(c)).ToArray());

            return clean.Trim();
        }
    }
}
