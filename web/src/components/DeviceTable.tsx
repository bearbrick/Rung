import { Alert, Space, Table, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import type { DeviceView } from '../api/types';
import { secondsAgo, stateColor } from './format';

interface Props {
  devices: DeviceView[];
  loading: boolean;
}

const columns: ColumnsType<DeviceView> = [
  {
    title: '设备',
    dataIndex: 'deviceId',
    render: (value: string) => <Typography.Text strong>{value}</Typography.Text>,
  },
  {
    title: '状态',
    dataIndex: 'state',
    render: (value: string) => <Tag color={stateColor(value)}>{value}</Tag>,
  },
  {
    title: '上次成功采集',
    dataIndex: 'lastSuccessUtc',
    render: (value: string | null) => secondsAgo(value),
  },
  {
    title: '上轮耗时',
    dataIndex: 'lastPollMs',
    align: 'right',
    render: (value: number) => `${value.toFixed(2)} ms`,
  },
  {
    title: '点位 / 请求',
    key: 'plan',
    align: 'right',
    // 这两个数放一起看，就知道批量合并的效果如何——现场调采集周期时最直观的指标
    render: (_, device) => `${device.activeTagCount} / ${device.requestCount}`,
  },
  { title: 'PDU', dataIndex: 'negotiatedPduLength', align: 'right' },
  {
    title: '重连',
    dataIndex: 'reconnectCount',
    align: 'right',
    render: (value: number) => (value > 0 ? <Tag color="orange">{value}</Tag> : value),
  },
  {
    title: '超时',
    dataIndex: 'overrunCount',
    align: 'right',
    // 这个数持续增长说明采集周期设得太快，或者点位太多需要拆组
    render: (value: number) => (value > 0 ? <Tag color="orange">{value}</Tag> : value),
  },
];

export function DeviceTable({ devices, loading }: Props) {
  return (
    <Table
      rowKey="deviceId"
      size="small"
      loading={loading}
      dataSource={devices}
      columns={columns}
      pagination={false}
      expandable={{
        // 只有真的有内容时才让行可展开，否则一排没用的箭头很碍眼
        rowExpandable: (device) => device.issues.length > 0 || Boolean(device.lastError),
        expandedRowRender: (device) => (
          <Space direction="vertical" style={{ width: '100%' }}>
            {device.lastError && (
              <Alert
                type={device.state === 'Connected' ? 'info' : 'error'}
                showIcon
                message={
                  device.state === 'Connected'
                    ? `上次断开的原因：${device.lastError}`
                    : `当前故障：${device.lastError}（连续失败 ${device.consecutiveFailures} 次）`
                }
              />
            )}
            {device.issues.map((issue) => (
              <Alert
                key={issue.tagName}
                type="warning"
                showIcon
                message={`${issue.tagName}：${issue.reason}`}
              />
            ))}
          </Space>
        ),
      }}
    />
  );
}
