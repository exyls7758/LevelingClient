using System.Collections.Generic;

namespace Multiverse.Models
{
    public enum ControllerVisualSkin
    {
        Xbox = 0,
        PlayStation5 = 1
    }

    public sealed class ControllerVisualLayout
    {
        public int Version { get; set; } = 2;
        public bool UseCustomControllerRendering { get; set; }
        public Dictionary<string, ControllerVisualElementLayout> Elements { get; set; } = new();
    }

    public sealed class ControllerVisualElementLayout
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool UsesTranslation { get; set; }
    }
}
