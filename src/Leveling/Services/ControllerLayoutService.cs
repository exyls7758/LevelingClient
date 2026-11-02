using Multiverse.Models;
using System;
using System.IO;
using System.Text.Json;

namespace Multiverse.Services
{
    public sealed class ControllerLayoutService
    {
        public static ControllerLayoutService Instance { get; } = new();

        private readonly string _folderPath;
        private readonly string _xboxFilePath;
        private readonly string _ps5FilePath;
        private readonly string _legacyLayoutPath;
        private readonly string _legacyAlignmentPath;
        private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        private ControllerLayoutService()
        {
            _folderPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Leveling");
            _xboxFilePath = Path.Combine(_folderPath, "controller-layout-v2.json");
            _ps5FilePath = Path.Combine(_folderPath, "controller-layout-ps5-v2.json");
            _legacyLayoutPath = Path.Combine(_folderPath, "controller-layout.json");
            _legacyAlignmentPath = Path.Combine(_folderPath, "controller-stick-alignment.json");
        }

        public ControllerVisualLayout? Load(ControllerVisualSkin skin)
        {
            try
            {
                string filePath = GetFilePath(skin);
                if (!File.Exists(filePath))
                    return null;

                string json = File.ReadAllText(filePath);
                ControllerVisualLayout? layout = JsonSerializer.Deserialize<ControllerVisualLayout>(json, _jsonOptions);
                if (layout is null || layout.Version != 2 || layout.Elements.Count == 0)
                    return null;

                return layout;
            }
            catch
            {
                return null;
            }
        }

        public bool Save(ControllerVisualSkin skin, ControllerVisualLayout layout)
        {
            try
            {
                Directory.CreateDirectory(_folderPath);
                layout.Version = 2;
                string json = JsonSerializer.Serialize(layout, _jsonOptions);
                string filePath = GetFilePath(skin);
                string tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, true);

                if (skin == ControllerVisualSkin.Xbox)
                {
                    // Old Xbox editor files are intentionally retired so layouts from
                    // CORE 11.44/11.45 cannot unexpectedly deform protected defaults.
                    DeleteIfPresent(_legacyLayoutPath);
                    DeleteIfPresent(_legacyAlignmentPath);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Reset(ControllerVisualSkin skin)
        {
            try
            {
                DeleteIfPresent(GetFilePath(skin));
                if (skin == ControllerVisualSkin.Xbox)
                {
                    DeleteIfPresent(_legacyLayoutPath);
                    DeleteIfPresent(_legacyAlignmentPath);
                }
            }
            catch
            {
                // Visual-layout persistence must never affect controller input.
            }
        }

        private string GetFilePath(ControllerVisualSkin skin) =>
            skin == ControllerVisualSkin.PlayStation5
                ? _ps5FilePath
                : _xboxFilePath;

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
