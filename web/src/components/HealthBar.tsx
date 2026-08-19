import { Alert, Card, Space, Statistic, Tag } from 'antd';
import type { ConnectionState } from '../hooks/useLiveTags';
import type { HealthView } from '../api/types';

interface Props {
  health: HealthView | undefined;
  connection: ConnectionState;
}

const connectionLabel: Record<ConnectionState, { color: string; text: string }> = {
  live: { color: 'green', text: '实时推送已连接' },
  connecting: { color: 'blue', text: '正在连接推送…' },
  offline: { color: 'red', text: '推送已断开' },
};

export function HealthBar({ health, connection }: Props) {
  const badge = connectionLabel[connection];

  return (
    <Card size="small" styles={{ body: { padding: '12px 16px' } }}>
      <Space size={40} wrap>
        <Statistic
          title="网关状态"
          value={health?.status === 'healthy' ? '正常' : '有设备异常'}
          valueStyle={{
            fontSize: 20,
            color: health?.status === 'healthy' ? '#389e0d' : '#cf1322',
          }}
        />
        <Statistic
          title="设备"
          value={health ? `${health.connectedCount}/${health.deviceCount}` : '—'}
          valueStyle={{ fontSize: 20 }}
        />
        <Statistic title="点位" value={health?.tagCount ?? '—'} valueStyle={{ fontSize: 20 }} />
        <Statistic
          title="配置问题"
          value={health?.issueCount ?? 0}
          valueStyle={{ fontSize: 20, color: health?.issueCount ? '#cf1322' : undefined }}
        />
        <Statistic
          title="运行时长"
          value={health ? formatUptime(health.uptimeSeconds) : '—'}
          valueStyle={{ fontSize: 20 }}
        />
        <Tag color={badge.color} style={{ marginTop: 18 }}>
          {badge.text}
        </Tag>
      </Space>

      {health && health.issueCount > 0 && (
        <Alert
          type="warning"
          showIcon
          style={{ marginTop: 12 }}
          message={`有 ${health.issueCount} 个点位配置有误，这些点位不参与采集。展开设备行查看详情。`}
        />
      )}
    </Card>
  );
}

function formatUptime(seconds: number): string {
  if (seconds < 60) {
    return `${seconds.toFixed(0)} 秒`;
  }
  if (seconds < 3600) {
    return `${(seconds / 60).toFixed(0)} 分钟`;
  }
  return `${(seconds / 3600).toFixed(1)} 小时`;
}
