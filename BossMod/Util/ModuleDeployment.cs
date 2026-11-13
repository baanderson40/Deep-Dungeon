using System.IO;

namespace BossMod;

internal static class ModuleDeployment
{
    internal static string SelectModuleDirectory(bool development, string assemblyDirectory, string storageDirectory, Func<string, bool> fileExists)
    {
        var portable = Path.Join(assemblyDirectory, "modules-dev");
        var complete = development && fileExists(Path.Join(portable, "BossMod.Modules.dll"))
            && fileExists(Path.Join(portable, "BossMod.Autorotation.dll"))
            && fileExists(Path.Join(portable, "BossMod.Ultimate.dll"));
        return complete ? portable : Path.Join(storageDirectory, development ? "modules-dev" : "modules");
    }
}
