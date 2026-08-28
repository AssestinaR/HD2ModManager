using System;
using System.Collections.Generic;

namespace HD2ModManager.Models
{
    public class ModEntity
    {
        public string Guid { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Image { get; set; }
        public string? ThumbImage { get; set; }
        public string? IconPath { get; set; }
        public string? Url { get; set; }
        public List<FileGroup> FileGroups { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? SourcePath { get; set; }
        public bool IsDecoration { get; set; }
        public bool IsOption { get; set; }
        public string[] HostModGuids { get; set; } = Array.Empty<string>();
        // This is a lightweight structural fact projected from ModNode.PatchGroups.
        // It must not depend on FileGroups, which may intentionally be omitted
        // during the fast library load.
        public bool HasPatchContent { get; set; }
        // Persistent user-controlled derived overwrite state. The actual payload remains
        // under the Mod's Overwrite directory; this flag only records whether it should be
        // regenerated when decorations or source content change.
        public bool IsHelmetToArmorMerged { get; set; }
        public ModCapabilities Capabilities => ModCapabilities.For(IsDecoration, IsOption, HasPatchContent);
    }
}
