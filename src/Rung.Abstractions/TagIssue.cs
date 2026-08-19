namespace Rung.Abstractions;

/// <summary>
/// 编译读取计划时发现的单个点位的配置问题。
/// <para>
/// 刻意不做成异常：产线上上千个点位里配错一两个是常态，
/// 一个写错的 DB 号不该让整台设备的采集全部停摆。
/// 有问题的点位会被排除出采集计划、在每轮采集中置为
/// <see cref="TagQuality.ConfigError"/>，其余点位照常运行。
/// </para>
/// <para>
/// 代价是配置错误会变得安静，因此 Web UI 必须把 <see cref="IReadPlan.Issues"/>
/// 显著地暴露出来——这是这个折中能成立的前提。
/// </para>
/// </summary>
/// <param name="TagIndex">点位在 <see cref="IReadPlan.Tags"/> 中的下标。</param>
/// <param name="TagName">点位名，便于直接展示。</param>
/// <param name="Reason">人类可读的原因，应当足以让电气工程师自己改对。</param>
public readonly record struct TagIssue(int TagIndex, string TagName, string Reason)
{
    /// <inheritdoc/>
    public override string ToString() => $"{TagName}: {Reason}";
}
