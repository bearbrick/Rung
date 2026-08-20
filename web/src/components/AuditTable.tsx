import { Empty, Space, Table, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import type { AuditRecord } from '../api/client';
import { formatTime } from './format';

/**
 * 写操作审计。
 *
 * 记下来了但界面上看不到，等于只做了一半——出事之后没人会先去 ssh 到网关机器上
 * tail 文件。被拒绝的尝试同样列出来：「谁试图往只读点位写东西」「谁没带密钥就来写」
 * 是安全审计里最该看到的信号。
 */
export function AuditTable() {
  const audit = useQuery({
    queryKey: ['audit'],
    queryFn: ({ signal }) => api.audit(200, signal),
    refetchInterval: 5000,
    refetchIntervalInBackground: true,
  });

  const columns: ColumnsType<AuditRecord> = [
    {
      title: '时刻',
      dataIndex: 'timestampUtc',
      width: 130,
      render: (value: string) => (
        <Typography.Text type="secondary" style={{ fontVariantNumeric: 'tabular-nums' }}>
          {formatTime(value)}
        </Typography.Text>
      ),
    },
    {
      title: '结果',
      key: 'success',
      width: 90,
      render: (_, record) =>
        record.success ? <Tag color="green">成功</Tag> : <Tag color="red">拒绝</Tag>,
    },
    {
      title: '调用方',
      dataIndex: 'caller',
      width: 140,
      render: (value: string) => <Typography.Text strong>{value}</Typography.Text>,
    },
    { title: '点位', dataIndex: 'tagName', width: '22%' },
    {
      title: '地址',
      key: 'address',
      width: 180,
      render: (_, record) =>
        record.address ? (
          <Typography.Text code>
            {record.deviceId}/{record.address}
          </Typography.Text>
        ) : (
          <Typography.Text type="secondary">—</Typography.Text>
        ),
    },
    {
      title: '写入 → 回读',
      key: 'value',
      render: (_, record) =>
        record.success ? (
          <Typography.Text style={{ fontVariantNumeric: 'tabular-nums' }}>
            {record.requested} → {record.actual}
          </Typography.Text>
        ) : (
          // 失败时回读值没有意义，直接给原因
          <Typography.Text type="danger">{record.error}</Typography.Text>
        ),
    },
  ];

  if (!audit.isLoading && (audit.data ?? []).length === 0) {
    return (
      <Empty
        description={
          <Space direction="vertical" size={4}>
            <span>还没有写操作记录</span>
            <Typography.Text type="secondary">
              未配置 audit 段时不落盘。配置后每次写入（含被拒绝的尝试）都会出现在这里。
            </Typography.Text>
          </Space>
        }
      />
    );
  }

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <Typography.Text type="secondary">
        最近 {audit.data?.length ?? 0} 条。被拒绝的尝试同样留痕——
        「谁试图往只读点位写东西」「谁没带密钥就来写」是最该看到的信号。
      </Typography.Text>

      <Table
        rowKey={(record) => `${record.timestampUtc}-${record.tagName}-${record.caller}`}
        size="small"
        loading={audit.isLoading}
        dataSource={audit.data ?? []}
        columns={columns}
        pagination={{ pageSize: 20, showSizeChanger: false }}
      />
    </Space>
  );
}
