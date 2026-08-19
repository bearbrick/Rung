import { App as AntApp, ConfigProvider, Layout, Space, Tabs, Typography, theme } from 'antd';
import zhCN from 'antd/locale/zh_CN';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from './api/client';
import type { TagView } from './api/types';
import { DeviceTable } from './components/DeviceTable';
import { HealthBar } from './components/HealthBar';
import { TagTable } from './components/TagTable';
import { WriteModal } from './components/WriteModal';
import { useLiveTags } from './hooks/useLiveTags';

function Dashboard() {
  const { tags, changedAt, connection } = useLiveTags();
  const [writing, setWriting] = useState<TagView | null>(null);

  // 设备状态和健康度变化不频繁，轮询就够，不必再开一条推送通道。
  //
  // refetchIntervalInBackground 必须打开：React Query 默认在窗口失焦时停止轮询，
  // 而这个界面的典型用法是挂在车间墙上的一块屏，永远没人去点它——
  // 默认行为会让看板停在几十秒前的数据上，还看不出是停了。
  const live = { refetchInterval: 2000, refetchIntervalInBackground: true };

  const health = useQuery({
    queryKey: ['health'],
    queryFn: ({ signal }) => api.health(signal),
    ...live,
  });

  const devices = useQuery({
    queryKey: ['devices'],
    queryFn: ({ signal }) => api.devices(signal),
    ...live,
  });

  const deviceIds = (devices.data ?? []).map((device) => device.deviceId);

  return (
    <Layout style={{ minHeight: '100vh', background: 'transparent' }}>
      <Layout.Header style={{ background: 'transparent', padding: '16px 24px', height: 'auto' }}>
        <Space align="baseline" size={12}>
          <Typography.Title level={3} style={{ margin: 0 }}>
            Rung
          </Typography.Title>
          <Typography.Text type="secondary">PLC 数据采集网关</Typography.Text>
        </Space>
      </Layout.Header>

      <Layout.Content style={{ padding: '0 24px 24px' }}>
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          <HealthBar health={health.data} connection={connection} />

          <Tabs
            defaultActiveKey="tags"
            items={[
              {
                key: 'tags',
                label: `点位实时值${tags.size ? ` (${tags.size})` : ''}`,
                children: (
                  <TagTable
                    tags={tags}
                    changedAt={changedAt}
                    devices={deviceIds}
                    onWrite={setWriting}
                  />
                ),
              },
              {
                key: 'devices',
                label: `设备${deviceIds.length ? ` (${deviceIds.length})` : ''}`,
                children: (
                  <DeviceTable devices={devices.data ?? []} loading={devices.isLoading} />
                ),
              },
            ]}
          />
        </Space>
      </Layout.Content>

      <WriteModal tag={writing} onClose={() => setWriting(null)} />
    </Layout>
  );
}

export function App() {
  return (
    <ConfigProvider locale={zhCN} theme={{ algorithm: theme.defaultAlgorithm }}>
      <AntApp>
        <Dashboard />
      </AntApp>
    </ConfigProvider>
  );
}
