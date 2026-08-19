import { Button, Input, Select, Space, Table, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useMemo, useState } from 'react';
import type { TagView } from '../api/types';
import { formatTime, formatValue, qualityColor } from './format';

interface Props {
  tags: Map<string, TagView>;
  changedAt: Map<string, number>;
  devices: string[];
  onWrite: (tag: TagView) => void;
}

/** 变化后高亮多久。太短看不见，太长会让整张表一直在闪。 */
const HighlightMs = 900;

export function TagTable({ tags, changedAt, devices, onWrite }: Props) {
  const [search, setSearch] = useState('');
  const [device, setDevice] = useState<string | undefined>();
  const [onlyBad, setOnlyBad] = useState(false);

  const rows = useMemo(() => {
    const keyword = search.trim().toLowerCase();

    return [...tags.values()]
      .filter((tag) => !device || tag.deviceId === device)
      .filter((tag) => !onlyBad || tag.quality !== 'Good')
      .filter((tag) => !keyword || tag.name.toLowerCase().includes(keyword) ||
        tag.address.toLowerCase().includes(keyword))
      .sort((a, b) => a.name.localeCompare(b.name));
  }, [tags, search, device, onlyBad]);

  const columns: ColumnsType<TagView> = [
    {
      title: '点位名',
      dataIndex: 'name',
      width: '26%',
      render: (value: string, tag) => (
        <Space size={4} direction="vertical">
          <Typography.Text strong>{value}</Typography.Text>
          {tag.description && (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {tag.description}
            </Typography.Text>
          )}
        </Space>
      ),
    },
    {
      title: '值',
      key: 'value',
      align: 'right',
      width: 140,
      render: (_, tag) => {
        const recent = Date.now() - (changedAt.get(tag.name) ?? 0) < HighlightMs;
        return (
          <Typography.Text
            className={recent ? 'value-flash' : undefined}
            style={{ fontVariantNumeric: 'tabular-nums', fontSize: 15 }}
            title={String(tag.value ?? '')}
          >
            {formatValue(tag)}
          </Typography.Text>
        );
      },
    },
    {
      title: '质量',
      dataIndex: 'quality',
      width: 110,
      render: (value: string) => <Tag color={qualityColor(value)}>{value}</Tag>,
    },
    { title: '类型', dataIndex: 'dataType', width: 90 },
    { title: '设备', dataIndex: 'deviceId', width: 130 },
    {
      title: '地址',
      dataIndex: 'address',
      width: 130,
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
    },
    {
      title: '更新时刻',
      dataIndex: 'timestampUtc',
      width: 130,
      render: (value: string) => (
        <Typography.Text type="secondary" style={{ fontVariantNumeric: 'tabular-nums' }}>
          {formatTime(value)}
        </Typography.Text>
      ),
    },
    {
      title: '',
      key: 'actions',
      width: 70,
      render: (_, tag) => (
        <Button size="small" onClick={() => onWrite(tag)}>
          写入
        </Button>
      ),
    },
  ];

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <Space wrap>
        <Input.Search
          allowClear
          placeholder="按点位名或地址过滤"
          style={{ width: 280 }}
          onChange={(event) => setSearch(event.target.value)}
        />
        <Select
          allowClear
          placeholder="全部设备"
          style={{ width: 180 }}
          value={device}
          onChange={setDevice}
          options={devices.map((id) => ({ value: id, label: id }))}
        />
        <Button type={onlyBad ? 'primary' : 'default'} onClick={() => setOnlyBad((v) => !v)}>
          只看异常
        </Button>
        <Typography.Text type="secondary">共 {rows.length} 个点位</Typography.Text>
      </Space>

      <Table
        rowKey="name"
        size="small"
        dataSource={rows}
        columns={columns}
        pagination={false}
        // 上千个点位时必须虚拟滚动，否则浏览器会被 DOM 节点数拖垮
        virtual
        scroll={{ y: 460, x: 1100 }}
      />
    </Space>
  );
}
