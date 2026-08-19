using Rung.Abstractions;
using Xunit;

namespace Rung.Protocols.S7.Tests;

public class S7ReadPlannerTests
{
    /// <summary>S7-300 的典型协商值。单次可读 222 字节，单请求最多 19 项。</summary>
    private const int Pdu240 = 240;

    private static TagDef Tag(string name, string address, TagDataType type, int length = 0)
        => new() { Name = name, Address = address, DataType = type, Length = length };

    [Fact]
    public void 相邻点位合并成一次读取()
    {
        TagDef[] tags =
        [
            Tag("a", "DB1.DBW0", TagDataType.Int16),
            Tag("b", "DB1.DBW2", TagDataType.Int16),
            Tag("c", "DB1.DBW4", TagDataType.Int16),
        ];

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        Assert.Empty(plan.Issues);
        Assert.Equal(1, plan.RequestCount);

        var item = Assert.Single(plan.Requests[0].Items);
        Assert.Equal(0, item.Address.ByteOffset);
        Assert.Equal(6, item.Count);

        // 三个点位落在同一项内，靠字节偏移区分
        Assert.Equal(new S7TagLocation(0, 0, 0, 0, 2), plan.Locations[0]);
        Assert.Equal(new S7TagLocation(0, 0, 2, 0, 2), plan.Locations[1]);
        Assert.Equal(new S7TagLocation(0, 0, 4, 0, 2), plan.Locations[2]);
    }

    [Theory]
    [InlineData(8, 1)]   // 空洞 8 字节，正好等于阈值：合并
    [InlineData(7, 2)]   // 阈值调小到 7：不合并
    [InlineData(0, 2)]   // 只合并严格相邻的
    public void 空洞大小决定是否合并(int maxGapBytes, int expectedItemCount)
    {
        // DB1.DBW0 占 [0,2)，DB1.DBW10 占 [10,12)，中间空洞正好 8 字节
        TagDef[] tags =
        [
            Tag("a", "DB1.DBW0", TagDataType.Int16),
            Tag("b", "DB1.DBW10", TagDataType.Int16),
        ];

        var plan = S7ReadPlanner.Create(tags, Pdu240, new S7ReadPlannerOptions { MaxGapBytes = maxGapBytes });

        Assert.Equal(1, plan.RequestCount);
        Assert.Equal(expectedItemCount, plan.Requests[0].Items.Count);
    }

    [Fact]
    public void 合并跨越空洞时会多读回废字节()
    {
        TagDef[] tags =
        [
            Tag("a", "DB1.DBW0", TagDataType.Int16),
            Tag("b", "DB1.DBW10", TagDataType.Int16),
        ];

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        // 点位实际只要 4 字节，合并后取回 12 字节。这两个数并排显示在 Web UI 上，
        // 现场调 MaxGapBytes 时才知道自己在拿什么换什么
        Assert.Equal(12, plan.TotalFetchedBytes);
    }

    [Fact]
    public void 不同数据块不合并()
    {
        TagDef[] tags =
        [
            Tag("a", "DB1.DBW0", TagDataType.Int16),
            Tag("b", "DB2.DBW0", TagDataType.Int16),
        ];

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        Assert.Equal(1, plan.RequestCount);
        Assert.Equal(2, plan.Requests[0].Items.Count);
        Assert.Equal(1, plan.Requests[0].Items[0].Address.DbNumber);
        Assert.Equal(2, plan.Requests[0].Items[1].Address.DbNumber);
    }

    [Fact]
    public void 不同存储区不合并()
    {
        TagDef[] tags =
        [
            Tag("a", "DB1.DBW0", TagDataType.Int16),
            Tag("b", "MW0", TagDataType.Int16),
        ];

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        Assert.Equal(2, plan.Requests[0].Items.Count);
    }

    [Fact]
    public void 布尔点位搭字节读取的便车()
    {
        // 同一个字节里的两个位不该产生两次读取，
        // 而且既然附近的字都要读，这两个位等于白送
        TagDef[] tags =
        [
            Tag("bit0", "DB1.DBX0.0", TagDataType.Bool),
            Tag("bit5", "DB1.DBX0.5", TagDataType.Bool),
            Tag("word", "DB1.DBW2", TagDataType.Int16),
        ];

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        var item = Assert.Single(plan.Requests[0].Items);
        Assert.Equal(4, item.Count);
        Assert.False(item.IsBitAccess);

        Assert.Equal(new S7TagLocation(0, 0, 0, 0, 1), plan.Locations[0]);
        Assert.Equal(new S7TagLocation(0, 0, 0, 5, 1), plan.Locations[1]);
        Assert.Equal(new S7TagLocation(0, 0, 2, 0, 2), plan.Locations[2]);
    }

    [Fact]
    public void 重叠的点位合并后只读一次()
    {
        // DB1.DBD0 占 [0,4)，DB1.DBW2 占 [2,4)——同一块内存的两种解读方式，
        // 现场为了兼容老程序确实会这么配
        TagDef[] tags =
        [
            Tag("dword", "DB1.DBD0", TagDataType.Float32),
            Tag("high", "DB1.DBW2", TagDataType.Int16),
        ];

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        var item = Assert.Single(plan.Requests[0].Items);
        Assert.Equal(4, item.Count);
        Assert.Equal(2, plan.Locations[1].ByteOffset);
    }

    [Fact]
    public void 超过单次读取上限时切分成多个区块()
    {
        // 222 是 PDU 240 下的单次读取上限
        TagDef[] tags =
        [
            Tag("a", "DB1.DBB0", TagDataType.Bytes, length: 200),
            Tag("b", "DB1.DBB200", TagDataType.Bytes, length: 100),
        ];

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        Assert.Empty(plan.Issues);

        // 合并后跨度 300 > 222，必须拆开
        var allItems = plan.Requests.SelectMany(static r => r.Items).ToArray();
        Assert.Equal(2, allItems.Length);
        Assert.All(allItems, static item => Assert.True(item.Count <= 222));
    }

    [Fact]
    public void 数据项个数触顶时开新的一次请求()
    {
        // 25 个彼此隔开 18 字节（大于默认阈值 8）的点位 → 25 个独立区块
        var tags = Enumerable.Range(0, 25)
            .Select(i => Tag($"t{i}", $"DB1.DBW{i * 20}", TagDataType.Int16))
            .ToArray();

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        Assert.Empty(plan.Issues);
        Assert.Equal(2, plan.RequestCount);
        Assert.Equal(19, plan.Requests[0].Items.Count); // MaxReadItems(240)
        Assert.Equal(6, plan.Requests[1].Items.Count);
    }

    [Fact]
    public void 响应字节数触顶时开新的一次请求()
    {
        // 每项 60 字节：14(头) + n*(4+60) ≤ 240 → n 最多 3
        var tags = Enumerable.Range(0, 10)
            .Select(i => Tag($"t{i}", $"DB1.DBB{i * 100}", TagDataType.Bytes, length: 60))
            .ToArray();

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        Assert.Equal(4, plan.RequestCount);
        Assert.Equal(3, plan.Requests[0].Items.Count);
        Assert.Single(plan.Requests[3].Items);

        // 每个请求的响应都必须真的装得下，只看项数上限会在真机上被拒收
        Assert.All(plan.Requests, r => Assert.True(
            r.ResponseByteLength <= Pdu240 + S7Protocol.IsoHeaderLength,
            $"响应预估 {r.ResponseByteLength} 字节超出 PDU"));
    }

    [Fact]
    public void 输入顺序不影响编译结果()
    {
        TagDef[] ordered =
        [
            Tag("a", "DB1.DBW0", TagDataType.Int16),
            Tag("b", "DB1.DBW2", TagDataType.Int16),
            Tag("c", "DB2.DBW0", TagDataType.Int16),
        ];
        TagDef[] shuffled = [ordered[2], ordered[0], ordered[1]];

        var planA = S7ReadPlanner.Create(ordered, Pdu240);
        var planB = S7ReadPlanner.Create(shuffled, Pdu240);

        Assert.Equal(planA.RequestCount, planB.RequestCount);
        Assert.Equal(planA.TotalFetchedBytes, planB.TotalFetchedBytes);

        // 位置回填必须跟着各自的输入顺序走
        Assert.Equal(planA.Locations[0], planB.Locations[1]);
        Assert.Equal(planA.Locations[2], planB.Locations[0]);
    }

    [Fact]
    public void 同样的输入编译出逐项相同的计划()
    {
        // Web UI 上显示的"128 个点位 → 3 次请求"必须是可复现的，
        // 否则现场调优时没法判断改动有没有生效
        var tags = Enumerable.Range(0, 40)
            .Select(i => Tag($"t{i}", $"DB{1 + (i % 3)}.DBW{i * 6}", TagDataType.Int16))
            .ToArray();

        var first = S7ReadPlanner.Create(tags, Pdu240);
        var second = S7ReadPlanner.Create(tags, Pdu240);

        Assert.Equal(first.RequestCount, second.RequestCount);
        Assert.Equal(first.Locations, second.Locations);
        Assert.Equal(
            first.Requests.SelectMany(static r => r.Items),
            second.Requests.SelectMany(static r => r.Items));
    }

    [Fact]
    public void 地址写错的点位被隔离而不影响其余点位()
    {
        // 上千个点位里配错一两个是常态，一个坏点位不该让整台设备停摆
        TagDef[] tags =
        [
            Tag("good1", "DB1.DBW0", TagDataType.Int16),
            Tag("broken", "DB0.DBW0", TagDataType.Int16),
            Tag("good2", "DB1.DBW2", TagDataType.Int16),
        ];

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        var issue = Assert.Single(plan.Issues);
        Assert.Equal(1, issue.TagIndex);
        Assert.Equal("broken", issue.TagName);
        Assert.Contains("不能为 0", issue.Reason, StringComparison.Ordinal);

        Assert.False(plan.Locations[1].IsValid);
        Assert.True(plan.Locations[0].IsValid);
        Assert.True(plan.Locations[2].IsValid);
        Assert.Equal(2, plan.ActiveTagCount);
    }

    [Fact]
    public void 地址宽度与数据类型不符会被拦下()
    {
        // DBW 是 2 字节，Float32 要 4 字节。不拦的话现场会读回一个乱码，
        // 而且因为长度对得上，排查时很难想到是配置问题
        TagDef[] tags = [Tag("bad", "DB1.DBW10", TagDataType.Float32)];

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        var issue = Assert.Single(plan.Issues);
        Assert.Contains("2 字节", issue.Reason, StringComparison.Ordinal);
        Assert.Contains("4 字节", issue.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 位地址配成非布尔类型会被拦下()
    {
        TagDef[] tags = [Tag("bad", "DB1.DBX0.3", TagDataType.Int16)];

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        Assert.Contains("位地址", Assert.Single(plan.Issues).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 变长类型缺少长度会被拦下()
    {
        TagDef[] tags = [Tag("bad", "DB1.DBB0", TagDataType.Bytes)];

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        Assert.Contains("必须配置 Length", Assert.Single(plan.Issues).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 单个点位超过读取上限时给出可照做的提示()
    {
        // 跨请求分片会让拆包复杂一大截，而这种点位很少见。
        // 明确拒绝 + 说清怎么改，好过默默读回半截数据
        TagDef[] tags = [Tag("huge", "DB1.DBB0", TagDataType.Bytes, length: 500)];

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        var issue = Assert.Single(plan.Issues);
        Assert.Contains("222", issue.Reason, StringComparison.Ordinal);
        Assert.Contains("拆分成多个点位", issue.Reason, StringComparison.Ordinal);
        Assert.Empty(plan.Requests);
    }

    [Fact]
    public void PDU越大合并得越狠请求次数越少()
    {
        var tags = Enumerable.Range(0, 60)
            .Select(i => Tag($"t{i}", $"DB1.DBB{i * 40}", TagDataType.Bytes, length: 20))
            .ToArray();

        var s7300 = S7ReadPlanner.Create(tags, 240);
        var s71500 = S7ReadPlanner.Create(tags, 480);

        Assert.True(s71500.RequestCount < s7300.RequestCount,
            $"PDU 480 用了 {s71500.RequestCount} 次请求，PDU 240 用了 {s7300.RequestCount} 次");
    }

    [Fact]
    public void 空点位列表编译出空计划()
    {
        var plan = S7ReadPlanner.Create([], Pdu240);

        Assert.Equal(0, plan.RequestCount);
        Assert.Empty(plan.Issues);
        Assert.Equal(0, plan.ActiveTagCount);
    }

    [Fact]
    public void 全部点位无效时不产生任何请求()
    {
        TagDef[] tags =
        [
            Tag("x", "垃圾地址", TagDataType.Int16),
            Tag("y", "DB1.DBX0.9", TagDataType.Bool),
        ];

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        Assert.Equal(2, plan.Issues.Count);
        Assert.Empty(plan.Requests);
        Assert.All(plan.Locations, static loc => Assert.False(loc.IsValid));
    }

    [Fact]
    public void 协商PDU长度过小时直接拒绝()
        => Assert.Throws<ArgumentOutOfRangeException>(() => S7ReadPlanner.Create([], 128));

    [Fact]
    public void 一个典型状态块的128个点位只需两次往返()
    {
        // 这是合并算法存在的理由：产线上一个状态 DB 里连续排布上百个点位是常态。
        // 逐个读 = 128 次网络往返，500ms 周期根本跑不完；合并后只要 2 次
        var tags = Enumerable.Range(0, 128)
            .Select(i => Tag($"t{i}", $"DB1.DBW{i * 2}", TagDataType.Int16))
            .ToArray();

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        Assert.Empty(plan.Issues);
        Assert.Equal(2, plan.RequestCount);

        // 第一项正好顶到 222 字节的读取上限，响应恰好用满 240 字节的 PDU
        Assert.Equal(222, plan.Requests[0].Items[0].Count);
        Assert.Equal(Pdu240 + S7Protocol.IsoHeaderLength, plan.Requests[0].ResponseByteLength);

        // 连续排布时没有任何浪费：取回的字节数正好等于点位需要的字节数
        Assert.Equal(256, plan.TotalFetchedBytes);
        Assert.All(plan.Locations, static loc => Assert.True(loc.IsValid));
    }

    [Fact]
    public void 计划里的每一项都能组包成合法请求()
    {
        // 端到端自洽性检查：合并算法算出来的东西，组包器必须真的写得出来，
        // 而且长度不能超过协商的 PDU
        var tags = Enumerable.Range(0, 50)
            .Select(i => Tag($"t{i}", $"DB{1 + (i % 2)}.DBD{i * 12}", TagDataType.Float32))
            .ToArray();

        var plan = S7ReadPlanner.Create(tags, Pdu240);

        foreach (var request in plan.Requests)
        {
            var items = request.Items.ToArray();
            var buffer = new byte[S7RequestBuilder.GetReadRequestLength(items.Length)];

            var written = S7RequestBuilder.WriteReadRequest(buffer, 1, items);

            Assert.Equal(buffer.Length, written);
            Assert.True(
                written - S7Protocol.IsoHeaderLength <= Pdu240,
                $"请求 S7 部分 {written - S7Protocol.IsoHeaderLength} 字节超出 PDU {Pdu240}");
        }
    }
}
