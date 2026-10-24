using System.Text.Json;
using System.Text.Json.Serialization;

namespace JiyuBox.Core;

/// <summary>
/// 目标软件适配档。极域 2016 与 2021(v6) 的进程名/窗口特征不同，
/// 全部差异收敛在 JSON 里，新增版本只需要加一个文件，不改代码。
/// </summary>
public sealed class AdapterProfile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "自动识别";
    [JsonPropertyName("processNames")] public List<string> ProcessNames { get; set; } = new();
    [JsonPropertyName("broadcastHints")] public List<string> BroadcastHints { get; set; } = new();
    [JsonPropertyName("blackScreenHints")] public List<string> BlackScreenHints { get; set; } = new();
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static AdapterProfile Jiyu2016() => new()
    {
        Name = "极域电子教室 2016",
        ProcessNames = new() { "StudentMain.exe", "StudentDisplay.exe", "TopSrv.exe" },
        BroadcastHints = new() { "TD", "TopDomain", "Student" },
        BlackScreenHints = new() { "Black", "ScreenLock", "TDBlack" },
        Notes = "2016 版学生端主进程 StudentMain.exe；广播窗口为全屏置顶无边框窗口。"
    };

    public static AdapterProfile Jiyu2021() => new()
    {
        Name = "极域电子教室 2021 (v6)",
        ProcessNames = new() { "StudentMain.exe", "StudentDisplay.exe", "TopSrv.exe", "Student.exe" },
        BroadcastHints = new() { "TD", "Mythware", "Student" },
        BlackScreenHints = new() { "Black", "ScreenLock", "TDBlack", "Quiet" },
        Notes = "2021 v6 学生端可能同时存在 StudentMain.exe 与 StudentDisplay.exe 两个进程。"
    };

    public static AdapterProfile LoadOrCreate(string path, string preset)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AdapterProfile>(File.ReadAllText(path), JsonOpts);
                if (loaded is { ProcessNames.Count: > 0 })
                    return loaded;
            }
        }
        catch
        {
            // 配置坏了就退回内置预设，不让 UI 起不来
        }

        var fallback = preset.Equals("2016", StringComparison.OrdinalIgnoreCase)
            ? Jiyu2016()
            : Jiyu2021();
        TrySave(path, fallback);
        return fallback;
    }

    public static void TrySave(string path, AdapterProfile profile)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOpts));
        }
        catch
        {
            // 只读目录（Program Files 未提权时）忽略
        }
    }
}
