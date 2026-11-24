using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace DesktopDock;

public sealed record DockThemeState
{
    public const string PrimaryDockId = "primary";
    public const int MaxCustomThemes = 64;
    public const int MaxDockOverrides = 128;

    public string GlobalThemeId { get; init; } = DockThemeBuiltIns.MinimalId;
    public bool AnimationsEnabled { get; init; } = true;
    public bool PerformanceMode { get; init; }
    public ImmutableArray<DockTheme> CustomThemes { get; init; } = [];
    public ImmutableDictionary<string, DockThemeOverride> Overrides { get; init; } = ImmutableDictionary<string, DockThemeOverride>.Empty;

    [JsonIgnore]
    public IEnumerable<DockTheme> Themes => DockThemeBuiltIns.All.Concat(CustomThemes);

    public DockTheme GetTheme(string id) => Themes.FirstOrDefault(theme => theme.Id == id)
        ?? throw new ArgumentException("所选主题不存在。");

    public string SelectedThemeId(string? dockId = null) =>
        dockId != null && Overrides.TryGetValue(dockId, out DockThemeOverride? value)
            ? value.ThemeId ?? GlobalThemeId : GlobalThemeId;

    public DockThemeTokens Resolve(string dockId)
    {
        DockThemeTokens basis = GetTheme(SelectedThemeId(dockId)).Tokens;
        return Overrides.TryGetValue(dockId, out DockThemeOverride? entry) ? entry.Patch.Apply(basis) : basis;
    }

    public DockThemeState SelectTheme(string themeId, string? dockId = null)
    {
        _ = GetTheme(themeId);
        if (dockId == null)
            return themeId == GlobalThemeId ? this : this with { GlobalThemeId = themeId };

        var existing = Overrides.GetValueOrDefault(dockId) ?? new DockThemeOverride();
        return SetOverride(dockId, existing with { ThemeId = themeId });
    }

    public DockThemeState SetOverride(string dockId, DockThemeOverride? value)
    {
        DockTheme.ValidateId(dockId);
        if (value is { Patch: null })
            throw new ArgumentException("Dock 的主题覆盖参数无效。");
        if (value == null || value is { ThemeId: null } && value.Patch == new DockThemePatch())
            return Overrides.ContainsKey(dockId) ? this with { Overrides = Overrides.Remove(dockId) } : this;

        value.Patch.Apply(GetTheme(value.ThemeId ?? GlobalThemeId).Tokens).Validate();
        if (!Overrides.ContainsKey(dockId) && Overrides.Count >= MaxDockOverrides)
            throw new ArgumentException($"最多保存 {MaxDockOverrides} 个 Dock 主题覆盖。");
        return value == Overrides.GetValueOrDefault(dockId) ? this : this with { Overrides = Overrides.SetItem(dockId, value) };
    }

    public DockThemeState SaveTheme(DockTheme theme)
    {
        theme.Validate();
        if (DockThemeBuiltIns.All.Any(existing => existing.Id == theme.Id))
            throw new ArgumentException("内置主题可另存为自定义主题。");

        for (int index = 0; index < CustomThemes.Length; index++)
        {
            if (CustomThemes[index].Id == theme.Id)
                return CustomThemes[index] == theme ? this : this with { CustomThemes = CustomThemes.SetItem(index, theme) };
        }
        return AppendTheme(theme);
    }

    public DockThemeState AddTheme(DockTheme theme)
    {
        theme.Validate();
        if (Themes.Any(existing => existing.Id == theme.Id))
            throw new ArgumentException("该主题标识已存在。可用新的标识另存为自定义主题。");
        return AppendTheme(theme);
    }

    private DockThemeState AppendTheme(DockTheme theme)
    {
        if (CustomThemes.Length >= MaxCustomThemes)
            throw new ArgumentException($"最多保存 {MaxCustomThemes} 个自定义主题。");
        return this with { CustomThemes = CustomThemes.Add(theme) };
    }

    internal void Validate()
    {
        if (CustomThemes.IsDefault || CustomThemes.Length > MaxCustomThemes ||
            Overrides == null || Overrides.Count > MaxDockOverrides)
            throw new ArgumentException("主题列表或 Dock 覆盖超过范围。");

        var ids = DockThemeBuiltIns.All.Select(theme => theme.Id).ToHashSet(StringComparer.Ordinal);
        foreach (DockTheme theme in CustomThemes)
        {
            if (theme == null || !ids.Add(theme.Id))
                throw new ArgumentException("主题列表包含空主题或重复标识。");
            theme.Validate();
        }

        DockThemeTokens global = GetTheme(GlobalThemeId).Tokens;
        foreach ((string dockId, DockThemeOverride value) in Overrides)
        {
            DockTheme.ValidateId(dockId);
            if (value?.Patch == null)
                throw new ArgumentException("Dock 的主题覆盖参数无效。");
            value.Patch.Apply(value.ThemeId == null ? global : GetTheme(value.ThemeId).Tokens).Validate();
        }
    }
}
