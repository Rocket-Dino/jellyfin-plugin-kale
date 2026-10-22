using System;
using System.IO;
using Jellyfin.Plugin.Kale.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Xunit;

namespace Jellyfin.Plugin.Kale.Tests;

/// <summary>
/// Uninstalling the plugin takes its configuration with it: Seerr's API key and the invitation
/// sign-ins' hashes must not outlive it. Jellyfin itself deletes only the plugin's folder.
/// </summary>
public class UninstallTests
{
    [Fact]
    public void UninstallingDeletesTheConfigurationAndItsSecrets()
    {
        var root = Directory.CreateTempSubdirectory("kale-uninstall").FullName;
        try
        {
            var plugin = new Plugin(new Paths(root), new NoXml());
            Directory.CreateDirectory(Path.GetDirectoryName(plugin.ConfigurationFilePath)!);
            File.WriteAllText(plugin.ConfigurationFilePath, "<PluginConfiguration />");

            plugin.OnUninstalling();

            Assert.False(File.Exists(plugin.ConfigurationFilePath));
            plugin.OnUninstalling(); // nothing left: still fine
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class Paths(string root) : IApplicationPaths
    {
        public string ProgramDataPath => root;
        public string WebPath => Path.Combine(root, "web");
        public string ProgramSystemPath => root;
        public string DataPath => Path.Combine(root, "data");
        public string ImageCachePath => Path.Combine(root, "cache", "images");
        public string PluginsPath => Path.Combine(root, "plugins");
        public string PluginConfigurationsPath => Path.Combine(root, "plugins", "configurations");
        public string LogDirectoryPath => Path.Combine(root, "log");
        public string ConfigurationDirectoryPath => Path.Combine(root, "config");
        public string SystemConfigurationFilePath => Path.Combine(root, "config", "system.xml");
        public string CachePath => Path.Combine(root, "cache");
        public string TempDirectory => Path.Combine(root, "tmp");
        public string VirtualDataPath => "%AppDataPath%";
        public string TrickplayPath => Path.Combine(root, "trickplay");
        public string BackupPath => Path.Combine(root, "backup");

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
        }
    }

    private sealed class NoXml : IXmlSerializer
    {
        public object? DeserializeFromStream(Type type, Stream stream) => new PluginConfiguration();

        public void SerializeToStream(object obj, Stream stream)
        {
        }

        public void SerializeToFile(object obj, string file)
        {
        }

        public object? DeserializeFromFile(Type type, string file) => new PluginConfiguration();

        public object? DeserializeFromBytes(Type type, byte[] buffer) => new PluginConfiguration();
    }
}
