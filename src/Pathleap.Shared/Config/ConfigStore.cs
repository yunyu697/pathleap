using System.Text.Json;

namespace Pathleap.Shared.Config;

/// <summary>配置读取/原子写入。首次运行会生成带默认值的配置文件。</summary>
public static class ConfigStore
{
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var lpFresh = new AppConfig();
            lpFresh.Normalize();
            Save(path, lpFresh);
            return lpFresh;
        }

        try
        {
            var lpJson = File.ReadAllText(path);
            var lpConfig = JsonSerializer.Deserialize<AppConfig>(lpJson, JsonDefaults.Options) ?? new AppConfig();
            lpConfig.Normalize();
            return lpConfig;
        }
        catch (Exception)
        {
            // 配置损坏不应阻塞启动：回退默认值，并把原文件保留在原地供诊断。
            var lpFallback = new AppConfig();
            lpFallback.Normalize();
            return lpFallback;
        }
    }

    public static void Save(string path, AppConfig config)
    {
        config.Normalize();
        var lpParent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(lpParent))
        {
            AppPaths.EnsureDirectory(lpParent);
        }

        var lpTemp = path + ".tmp";
        File.WriteAllText(lpTemp, JsonSerializer.Serialize(config, JsonDefaults.Options));
        File.Move(lpTemp, path, overwrite: true);
    }
}
