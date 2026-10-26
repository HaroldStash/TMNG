using System.Text.Json;
using System.Text.Json.Serialization;
using Tmng.App.Theme;
using Tmng.Core;

namespace Tmng.App;

/// <summary>User choices that survive a restart, in %LOCALAPPDATA%\TMNG\settings.json.</summary>
public sealed class AppSettings
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TMNG");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public sealed class RuleSetting
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
        public double Threshold { get; set; }
        public int Seconds { get; set; }
    }

    public AppTheme Theme { get; set; } = AppTheme.Light;
    public ColorScheme Scheme { get; set; } = ColorScheme.Mono;
    public bool IncludeSelf { get; set; }
    public bool Notifications { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public int ProcessRefreshSeconds { get; set; } = 1;
    public int AlertCaptureMinutes { get; set; } = 1;
    public List<RuleSetting> Rules { get; set; } = [];

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception)
        {
            // A damaged file falls back to defaults rather than blocking startup.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception)
        {
            // Settings are a convenience; failing to save must not break the app.
        }
    }

    /// <summary>Saved alert rules; null until the user first changes one.</summary>
    public List<AlertRule>? AlertRules { get; set; }

    // Names the starter rules had when only one threshold per rule could be set.
    private static readonly Dictionary<string, string> LegacyRuleIds = new()
    {
        ["CPU >"] = "builtin.cpu", ["GPU >"] = "builtin.gpu", ["Memory >"] = "builtin.memory",
        ["Disk busy >"] = "builtin.disk", ["CPU temp >"] = "builtin.cputemp", ["GPU temp >"] = "builtin.gputemp",
        ["Network >"] = "builtin.network",
    };

    /// <summary>The saved rules, or the starter rules with any choices made under the old single-threshold rules.</summary>
    public List<AlertRule> LoadAlertRules()
    {
        if (AlertRules is { Count: > 0 } saved)
        {
            // Starter rules added in a later version still appear for people with saved rules.
            var missing = AlertRule.Defaults().Where(d => saved.All(r => r.Id != d.Id));
            return [.. saved, .. missing];
        }

        var rules = AlertRule.Defaults();
        foreach (var legacy in Rules)
        {
            if (!LegacyRuleIds.TryGetValue(legacy.Name, out var id) || rules.FirstOrDefault(r => r.Id == id) is not { } rule) continue;
            rule.Enabled = legacy.Enabled;
            if (legacy.Seconds > 0) rule.Seconds = legacy.Seconds;
            if (legacy.Threshold > 0) rule.Conditions[0].Threshold = legacy.Threshold;
        }
        return rules;
    }

    public void SaveAlertRules(IEnumerable<AlertRule> rules)
    {
        AlertRules = rules.ToList();
        Rules = [];
        Save();
    }
}
