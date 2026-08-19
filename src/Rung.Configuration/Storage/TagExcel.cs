using System.Globalization;
using MiniExcelLibs;
using Rung.Abstractions;

namespace Rung.Configuration.Storage;

/// <summary>Excel 导入时逐行报出的问题。</summary>
/// <param name="Sheet">工作表名。</param>
/// <param name="Row">行号，与 Excel 里看到的一致（含表头行）。</param>
/// <param name="Reason">原因，应当足以让电气工程师自己改对。</param>
public readonly record struct ExcelIssue(string Sheet, int Row, string Reason)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Sheet} 第 {Row} 行：{Reason}";
}

/// <summary>
/// 点位表的 Excel 导入导出。
/// <para>
/// 这是整个配置环节最实用的一块：现场交接时电气工程师给的就是一张 Excel，
/// 能直接导入省掉的是几小时的手工誊抄——而手工誊抄正是地址配错的主要来源。
/// </para>
/// <para>
/// 表头用中文，因为读写它的人是中国工厂的电气工程师，不是程序员。
/// 解析逐行进行、错误带行号，因为这些表是人手工维护的，一定会有脏数据；
/// 报一句"格式错误"而不指出是哪一行，等于没报。
/// </para>
/// </summary>
public static class TagExcel
{
    /// <summary>设备工作表名。</summary>
    public const string DeviceSheet = "设备";

    /// <summary>点位工作表名。</summary>
    public const string TagSheet = "点位";

    /// <summary>把配置导出成 Excel。</summary>
    public static async Task ExportAsync(string path, RungConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        var devices = config.ResolveDevices();

        var deviceRows = devices.Select(static device => new Dictionary<string, object?>
        {
            ["设备ID"] = device.DeviceId,
            ["协议"] = device.Protocol,
            ["地址"] = device.Host,
            ["端口"] = device.Port,
            ["超时ms"] = device.TimeoutMs,
            ["重试"] = device.RetryCount,
            ["采集周期ms"] = device.PollIntervalMs,
            ["额外参数"] = device.Extra is { Count: > 0 }
                ? string.Join(";", device.Extra.Select(static kv => $"{kv.Key}={kv.Value}"))
                : null,
        }).ToList();

        var tagRows = devices.SelectMany(static device => (device.Tags ?? [])
            .Select(tag => new Dictionary<string, object?>
            {
                ["设备ID"] = device.DeviceId,
                ["点位名"] = tag.Name,
                ["地址"] = tag.Address,
                ["数据类型"] = tag.DataType.ToString(),
                ["长度"] = tag.Length,
                ["字节序"] = tag.ByteOrder.ToString(),
                ["倍率"] = tag.Scale,
                ["偏移"] = tag.Offset,
                ["死区"] = tag.Deadband,
                ["读写"] = tag.Access.ToString(),
                ["采集组"] = tag.PollGroup,
                ["描述"] = tag.Description,
                ["启用"] = tag.Enabled ? "是" : "否",
            })).ToList();

        var sheets = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [DeviceSheet] = deviceRows,
            [TagSheet] = tagRows,
        };

        await MiniExcel.SaveAsAsync(path, sheets, overwriteFile: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 从 Excel 读入配置。
    /// </summary>
    /// <param name="path">Excel 文件路径。</param>
    /// <param name="issues">逐行的问题清单。有问题的行会被跳过，其余照常导入。</param>
    public static RungConfig Import(string path, out IReadOnlyList<ExcelIssue> issues)
    {
        var found = new List<ExcelIssue>();

        var devices = ReadDevices(path, found);
        var tagsByDevice = ReadTags(path, found, devices.Keys);

        var result = devices.Values
            .Select(device => device with
            {
                Tags = tagsByDevice.TryGetValue(device.DeviceId, out var tags) ? tags : [],
            })
            .OrderBy(static device => device.DeviceId, StringComparer.Ordinal)
            .ToList();

        issues = found;
        return new RungConfig { Devices = result };
    }

    private static Dictionary<string, DeviceConfig> ReadDevices(string path, List<ExcelIssue> issues)
    {
        var devices = new Dictionary<string, DeviceConfig>(StringComparer.Ordinal);
        var row = 1;

        foreach (var record in MiniExcel.Query(path, useHeaderRow: true, sheetName: DeviceSheet))
        {
            row++;
            var cells = (IDictionary<string, object?>)record;

            var deviceId = Text(cells, "设备ID");
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                continue; // 空行，Excel 里很常见
            }

            var host = Text(cells, "地址");
            if (string.IsNullOrWhiteSpace(host))
            {
                issues.Add(new ExcelIssue(DeviceSheet, row, $"设备 {deviceId} 缺少地址"));
                continue;
            }

            if (!devices.TryAdd(deviceId, new DeviceConfig
            {
                DeviceId = deviceId,
                Protocol = Text(cells, "协议") is { Length: > 0 } protocol ? protocol : "s7",
                Host = host,
                Port = Int(cells, "端口") ?? 0,
                TimeoutMs = Int(cells, "超时ms") ?? 3000,
                RetryCount = Int(cells, "重试") ?? 1,
                PollIntervalMs = Int(cells, "采集周期ms"),
                Extra = ParseExtra(Text(cells, "额外参数")),
            }))
            {
                issues.Add(new ExcelIssue(DeviceSheet, row, $"设备标识 {deviceId} 重复"));
            }
        }

        return devices;
    }

    private static Dictionary<string, List<TagConfig>> ReadTags(
        string path, List<ExcelIssue> issues, Dictionary<string, DeviceConfig>.KeyCollection knownDevices)
    {
        var byDevice = new Dictionary<string, List<TagConfig>>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var row = 1;

        foreach (var record in MiniExcel.Query(path, useHeaderRow: true, sheetName: TagSheet))
        {
            row++;
            var cells = (IDictionary<string, object?>)record;

            var name = Text(cells, "点位名");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var deviceId = Text(cells, "设备ID");
            if (!knownDevices.Contains(deviceId))
            {
                issues.Add(new ExcelIssue(TagSheet, row,
                    $"点位 {name} 指向未定义的设备 \"{deviceId}\"，请先在「{DeviceSheet}」表里加上"));
                continue;
            }

            if (!seenNames.Add(name))
            {
                // 重名会让写命令路由到错误的设备上，这个必须拦
                issues.Add(new ExcelIssue(TagSheet, row, $"点位名 {name} 重复，业务名必须全局唯一"));
                continue;
            }

            if (!TryEnum<TagDataType>(Text(cells, "数据类型"), out var dataType))
            {
                issues.Add(new ExcelIssue(TagSheet, row,
                    $"点位 {name} 的数据类型 \"{Text(cells, "数据类型")}\" 无法识别，"
                    + $"可用：{string.Join(" / ", Enum.GetNames<TagDataType>())}"));
                continue;
            }

            var address = Text(cells, "地址");
            if (string.IsNullOrWhiteSpace(address))
            {
                issues.Add(new ExcelIssue(TagSheet, row, $"点位 {name} 缺少地址"));
                continue;
            }

            if (!TryEnum<ByteOrder>(Text(cells, "字节序"), out var byteOrder))
            {
                byteOrder = ByteOrder.ABCD;
            }

            if (!TryEnum<TagAccess>(Text(cells, "读写"), out var access))
            {
                access = TagAccess.Read;
            }

            if (!byDevice.TryGetValue(deviceId, out var list))
            {
                list = [];
                byDevice[deviceId] = list;
            }

            list.Add(new TagConfig
            {
                Name = name,
                Address = address,
                DataType = dataType,
                Length = Int(cells, "长度") ?? 0,
                ByteOrder = byteOrder,
                Scale = Double(cells, "倍率") ?? 1.0,
                Offset = Double(cells, "偏移") ?? 0.0,
                Deadband = Double(cells, "死区") ?? 0.0,
                Access = access,
                PollGroup = Text(cells, "采集组") is { Length: > 0 } group ? group : "default",
                Description = Text(cells, "描述") is { Length: > 0 } desc ? desc : null,
                Enabled = Text(cells, "启用") is not ("否" or "false" or "0" or "FALSE"),
            });
        }

        return byDevice;
    }

    private static string Text(IDictionary<string, object?> cells, string column)
        => cells.TryGetValue(column, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty
            : string.Empty;

    private static int? Int(IDictionary<string, object?> cells, string column)
        => Double(cells, column) is { } value ? (int)Math.Round(value) : null;

    private static double? Double(IDictionary<string, object?> cells, string column)
    {
        var text = Text(cells, column);

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static bool TryEnum<T>(string text, out T value) where T : struct, Enum
        => Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value);

    /// <summary>解析 <c>rack=0;slot=1</c> 形式的额外参数。</summary>
    private static Dictionary<string, string> ParseExtra(string text)
    {
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                extra[pair[..equals].Trim()] = pair[(equals + 1)..].Trim();
            }
        }

        return extra;
    }
}
