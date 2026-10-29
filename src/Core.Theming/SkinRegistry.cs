using System.Text.Json;
using System.Text.Json.Serialization;

namespace GearTray.Theming;

/// <summary>
/// 皮肤登记表：内置皮肤 + <c>skins/</c> 目录下的离线皮肤。
///
/// 纯离线加载，不联网、不执行任何代码 —— 皮肤只是一个 JSON，
/// 这让「第三方的皮肤」不需要任何信任成本，也让皮肤出错最多是配色难看，不会让程序崩。
/// </summary>
public sealed class SkinRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<string> _errors = [];

    private SkinRegistry(IReadOnlyList<Skin> skins)
    {
        All = skins;
        Default = skins.FirstOrDefault(s => s.Id == "light") ?? skins[0];
    }

    public IReadOnlyList<Skin> All { get; }

    public Skin Default { get; }

    /// <summary>加载失败的皮肤。UI 不必展示，但要能查 —— 静默吞掉是排查噩梦。</summary>
    public IReadOnlyList<string> Errors => _errors;

    public Skin Get(string? id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>
    /// 内置两套（浅色 / 深色），再叠加磁盘皮肤。
    /// 磁盘皮肤与内置同名时**覆盖内置**，方便用户改配色而不必重新编译。
    /// </summary>
    public static SkinRegistry Load(string? skinsDirectory)
    {
        var skins = new List<Skin> { Light, Dark };
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(skinsDirectory) && Directory.Exists(skinsDirectory))
        {
            // 每个子目录一个皮肤（skins/<id>/skin.json），只看一层：
            // 递归会把别人克隆进来的整个仓库当成皮肤扫一遍，没必要。
            foreach (var dir in Directory.EnumerateDirectories(skinsDirectory))
            {
                var file = Path.Combine(dir, "skin.json");
                if (!File.Exists(file))
                {
                    continue;
                }

                try
                {
                    var skin = JsonSerializer.Deserialize<Skin>(File.ReadAllText(file), JsonOptions);
                    if (skin is null || string.IsNullOrWhiteSpace(skin.Id))
                    {
                        errors.Add($"{file}: 缺少 id");
                        continue;
                    }

                    var withSource = skin with { SourcePath = file };
                    var existing = skins.FindIndex(s => string.Equals(s.Id, skin.Id, StringComparison.OrdinalIgnoreCase));
                    if (existing >= 0)
                    {
                        skins[existing] = withSource;
                    }
                    else
                    {
                        skins.Add(withSource);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // 一条坏皮肤不该让整个程序起不来。
                    errors.Add($"{file}: {ex.Message}");
                }
            }
        }

        var registry = new SkinRegistry(skins);
        registry._errors.AddRange(errors);
        return registry;
    }

    public static Skin Light { get; } = new()
    {
        Id = "light",
        Name = "浅色",
        IsDark = false,
        Layout = LayoutKind.CardList,
        Tokens = new SkinTokens(),
    };

    public static Skin Dark { get; } = new()
    {
        Id = "dark",
        Name = "深色",
        IsDark = true,
        Layout = LayoutKind.CardList,
        Tokens = new SkinTokens
        {
            WindowBackground = "#1B1B1F",
            Surface = "#232329",
            SurfaceAlt = "#2B2B33",
            TextPrimary = "#F2F2F5",
            TextSecondary = "#9A9AA5",
            TextTertiary = "#6E6E78",
            Border = "#33FFFFFF",
            Accent = "#4C8DFF",
            AccentForeground = "#0B0B0E",
            ControlBackground = "#2B2B33",
            ControlBorder = "#4A4A55",
            Danger = "#FF6B6B",
            Warning = "#FFC55C",
            Success = "#5FD07A",
        },
    };
}
