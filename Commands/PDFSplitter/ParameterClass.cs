using System;

namespace IssueBuddy
{
    /// <summary>
    /// Describes one building block of a file name template.
    /// TypeName is one of: "ProjectInfo", "ViewSheet" or "Separator".
    /// Serialised to XML as part of FileNameTemplateCollection.
    /// </summary>
    [Serializable]
    public class ParameterClass
    {
        public int Id { get; set; }

        public string Name { get; set; }

        /// <summary>"ProjectInfo", "ViewSheet" or "Separator"</summary>
        public string TypeName { get; set; }

        public ParameterClass()
        {
            Id = -999;
            Name = "";
            TypeName = "";
        }

        public ParameterClass(string name, string typeName, int id = -999)
        {
            Id = id;
            Name = name;
            TypeName = typeName;
        }

        /// <summary>
        /// Templates must own their own copies — never share instances with
        /// the source parameter list, otherwise edits bleed between presets.
        /// </summary>
        public ParameterClass Clone()
        {
            return new ParameterClass(Name, TypeName, Id);
        }
    }
}
