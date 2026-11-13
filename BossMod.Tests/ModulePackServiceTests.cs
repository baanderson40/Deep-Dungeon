using System.IO;
using Xunit;

namespace BossMod.Tests;

public class ModulePackServiceTests
{
    [Fact]
    public void DevelopmentPluginPrefersModulesBesideItsOwnCoreDll()
    {
        var portable = Path.Join("plugin", "modules-dev");
        Assert.Equal(portable, ModuleDeployment.SelectModuleDirectory(true, "plugin", "storage", path => Path.GetDirectoryName(path) == portable));
    }

    [Fact]
    public void OlderDevelopmentLayoutFallsBackToAppData()
        => Assert.Equal(Path.Join("storage", "modules-dev"), ModuleDeployment.SelectModuleDirectory(true, "plugin", "storage", _ => false));

    [Fact]
    public void ReleasePluginKeepsExistingModuleStorage()
        => Assert.Equal(Path.Join("storage", "modules"), ModuleDeployment.SelectModuleDirectory(false, "plugin", "storage", _ => true));

    [Fact]
    public void PartialPortableBuildDoesNotHideOtherCachedModules()
        => Assert.Equal(Path.Join("storage", "modules-dev"), ModuleDeployment.SelectModuleDirectory(true, "plugin", "storage", path => Path.GetFileName(path) == "BossMod.Modules.dll"));
}
